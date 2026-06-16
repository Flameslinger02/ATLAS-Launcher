using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Text;
using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IBattlEyeRconClient"/>
/// <remarks>Protocol reference: <c>docs/REFERENCE_arma3.md</c> (BERCon over UDP).</remarks>
public sealed class BattlEyeRconClient : IBattlEyeRconClient, IDisposable
{
    private const byte Login = 0x00;
    private const byte Command = 0x01;
    private const byte ServerMessage = 0x02;

    private readonly BehaviorSubject<RconState> _state = new(RconState.Disconnected);
    private readonly Subject<string> _messages = new();
    private readonly Subject<IReadOnlyList<RconPlayer>> _players = new();
    private readonly object _lock = new();
    private readonly Dictionary<byte, PendingCommand> _pending = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _loginTcs;
    private Task? _receiveTask;
    private Task? _keepAliveTask;
    private Task? _playerPollTask;
    private byte _nextSeq;
    private long _lastSentTicks;
    private byte? _lastMsgSeq;          // receive-thread only: de-dupes resent server messages

    public RconState State => _state.Value;
    public IObservable<RconState> StateChanged => _state;
    public IObservable<string> MessageReceived => _messages;
    public IObservable<IReadOnlyList<RconPlayer>> PlayersUpdated => _players;

    // ----- Connect / disconnect -----

    public async Task<bool> ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        Disconnect();
        SetState(RconState.Connecting);
        try
        {
            var udp = new UdpClient();
            udp.Connect(host, port);
            var cts = new CancellationTokenSource();
            var loginTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _udp = udp;
                _cts = cts;
                _loginTcs = loginTcs;
            }
            var token = cts.Token;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(udp, cts, token));

            await SendAsync(BuildPacket(Login, Encoding.UTF8.GetBytes(password))).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var loginReg = timeoutCts.Token.Register(() => loginTcs.TrySetResult(false));

            var ok = await loginTcs.Task.ConfigureAwait(false);
            if (ok)
            {
                SetState(RconState.Connected);
                _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(token));
                _playerPollTask = Task.Run(() => PlayerPollLoopAsync(token));
                Log.Information("RCON connected to {Host}:{Port}.", host, port);
                return true;
            }

            Log.Warning("RCON login failed for {Host}:{Port}.", host, port);
            SetState(RconState.Failed);
            Disconnect();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RCON connect failed.");
            SetState(RconState.Failed);
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        CancellationTokenSource? cts;
        UdpClient? udp;
        lock (_lock)
        {
            cts = _cts; _cts = null;
            udp = _udp; _udp = null;
            _loginTcs?.TrySetResult(false);    // unblock a pending login wait
            foreach (var p in _pending.Values) p.Tcs.TrySetCanceled();
            _pending.Clear();
        }
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { udp?.Close(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
        if (State != RconState.Failed) SetState(RconState.Disconnected);
    }

    // ----- Commands -----

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (State != RconState.Connected)
            throw new InvalidOperationException("RCON is not connected.");

        byte seq;
        var pending = new PendingCommand();
        lock (_lock)
        {
            var attempts = 0;
            while (_pending.ContainsKey(_nextSeq))
            {
                _nextSeq++;
                if (++attempts >= 256)
                    throw new InvalidOperationException("RCON sequence space exhausted (too many commands in flight).");
            }
            seq = _nextSeq++;
            _pending[seq] = pending;
        }

        var cmdBytes = Encoding.UTF8.GetBytes(command);
        var payload = new byte[1 + cmdBytes.Length];
        payload[0] = seq;
        Buffer.BlockCopy(cmdBytes, 0, payload, 1, cmdBytes.Length);

        try
        {
            await SendAsync(BuildPacket(Command, payload)).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var reg = timeoutCts.Token.Register(() => pending.Tcs.TrySetCanceled());
            return await pending.Tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _pending.Remove(seq);
        }
    }

    public async Task<List<RconPlayer>> GetPlayersAsync(CancellationToken ct = default)
    {
        var players = ParsePlayers(await SendCommandAsync("players", ct).ConfigureAwait(false));
        try { _players.OnNext(players); } catch (ObjectDisposedException) { /* disposed mid-flight */ }
        return players;
    }

    public async Task<List<RconBan>> GetBansAsync(CancellationToken ct = default)
        => ParseBans(await SendCommandAsync("bans", ct).ConfigureAwait(false));

    public async Task<List<string>> GetMissionsAsync(CancellationToken ct = default)
        => ParseMissions(await SendCommandAsync("missions", ct).ConfigureAwait(false));

    public Task SayGlobalAsync(string message) => SendCommandAsync($"say -1 {message}");
    public Task SayPlayerAsync(int id, string message) => SendCommandAsync($"say {id} {message}");
    public Task KickPlayerAsync(int id, string reason = "") => SendCommandAsync($"kick {id} {reason}".TrimEnd());
    public Task BanPlayerAsync(int id, int minutes = 0, string reason = "") => SendCommandAsync($"ban {id} {minutes} {reason}".TrimEnd());
    public Task BanGuidAsync(string guid, int minutes = 0, string reason = "") => SendCommandAsync($"addBan {guid} {minutes} {reason}".TrimEnd());
    public Task UnbanAsync(int banIndex) => SendCommandAsync($"removeBan {banIndex}");
    public Task LockServerAsync() => SendCommandAsync("#lock");
    public Task UnlockServerAsync() => SendCommandAsync("#unlock");
    public Task ShutdownServerAsync() => SendCommandAsync("#shutdown");
    public Task RestartMissionAsync() => SendCommandAsync("#restart");
    public Task ReassignMissionAsync() => SendCommandAsync("#reassign");

    // ----- Receive / dispatch -----

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationTokenSource ownCts, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception) { break; }   // socket closed / error
                HandleDatagram(result.Buffer);
            }
        }
        finally
        {
            // If the loop ended WITHOUT a deliberate cancel, the socket died on its own — tear the
            // connection down fully, but only if this loop still owns the current connection (a newer
            // ConnectAsync must not be clobbered).
            if (!ct.IsCancellationRequested)
            {
                bool current;
                lock (_lock) current = ReferenceEquals(_cts, ownCts);
                if (current)
                {
                    Log.Warning("RCON receive loop ended unexpectedly; disconnecting.");
                    Disconnect();
                }
            }
        }
    }

    private void HandleDatagram(byte[] buf)
    {
        if (buf.Length < 9 || buf[0] != (byte)'B' || buf[1] != (byte)'E' || buf[6] != 0xFF) return;

        // Verify the embedded CRC32 (LE) over [0xFF, type, payload]; drop corrupted datagrams.
        var crc = (uint)(buf[2] | (buf[3] << 8) | (buf[4] << 16) | (buf[5] << 24));
        if (Crc32(buf.AsSpan(6)) != crc) return;

        var type = buf[7];
        switch (type)
        {
            case Login:
                _loginTcs?.TrySetResult(buf[8] == 0x01);
                break;

            case Command:
            {
                var seq = buf[8];
                var multipart = buf.Length >= 12 && buf[9] == 0x00;
                if (multipart)
                {
                    var total = buf[10];
                    var index = buf[11];
                    var part = Encoding.UTF8.GetString(buf, 12, buf.Length - 12);
                    CompleteMultipart(seq, total, index, part);
                }
                else
                {
                    var data = buf.Length > 9 ? Encoding.UTF8.GetString(buf, 9, buf.Length - 9) : string.Empty;
                    CompleteSingle(seq, data);
                }
                break;
            }

            case ServerMessage:
            {
                var seq = buf[8];
                _ = SendAsync(BuildPacket(ServerMessage, new[] { seq }));   // always acknowledge (even duplicates)
                if (_lastMsgSeq == seq) break;                              // de-dupe a resent message
                _lastMsgSeq = seq;
                var msg = buf.Length > 9 ? Encoding.UTF8.GetString(buf, 9, buf.Length - 9) : string.Empty;
                try { _messages.OnNext(msg); } catch (ObjectDisposedException) { /* disposed mid-flight */ }
                break;
            }
        }
    }

    private void CompleteSingle(byte seq, string data)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(seq, out var p)) p.Tcs.TrySetResult(data);
        }
    }

    private void CompleteMultipart(byte seq, byte total, byte index, string part)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(seq, out var p)) return;
            p.Parts ??= new string?[total];
            if (index < p.Parts.Length && p.Parts[index] is null)
            {
                p.Parts[index] = part;
                p.Received++;
            }
            if (p.Parts.Length > 0 && p.Received >= p.Parts.Length && p.Parts.All(x => x is not null))
                p.Tcs.TrySetResult(string.Concat(p.Parts));
        }
    }

    // ----- Keepalive -----

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (State != RconState.Connected) continue;

                var idleMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSentTicks)) / TimeSpan.TicksPerMillisecond;
                if (idleMs < 25_000) continue;

                // An empty command IS the keepalive; routing it through SendCommandAsync reuses the
                // collision-safe seq allocation and pending lifecycle (so it can't clash with a real command).
                try { await SendCommandAsync(string.Empty, ct).ConfigureAwait(false); }
                catch { /* a dead socket surfaces via the receive loop */ }
            }
        }
        catch (OperationCanceledException) { /* expected on disconnect */ }
    }

    // ----- Player polling (30s GUID-diff feed; see PlayersUpdated) -----

    private async Task PlayerPollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (State != RconState.Connected) continue;
                // GetPlayersAsync publishes to _players; subscribers do the join/leave diff + history.
                try { await GetPlayersAsync(ct).ConfigureAwait(false); }
                catch { /* a dead socket surfaces via the receive loop; transient errors are skipped */ }
            }
        }
        catch (OperationCanceledException) { /* expected on disconnect */ }
    }

    // ----- Wire helpers -----

    private async Task SendAsync(byte[] packet)
    {
        var udp = _udp;
        if (udp is null) return;
        await udp.SendAsync(packet, packet.Length).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastSentTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Frames a payload as a BERCon packet: 'B''E' + CRC32(0xFF,type,payload) LE + 0xFF + type + payload.</summary>
    internal static byte[] BuildPacket(byte type, byte[] payload)
    {
        var inner = new byte[2 + payload.Length];
        inner[0] = 0xFF;
        inner[1] = type;
        Buffer.BlockCopy(payload, 0, inner, 2, payload.Length);

        var crc = Crc32(inner);
        var packet = new byte[6 + inner.Length];
        packet[0] = (byte)'B';
        packet[1] = (byte)'E';
        packet[2] = (byte)(crc & 0xFF);
        packet[3] = (byte)((crc >> 8) & 0xFF);
        packet[4] = (byte)((crc >> 16) & 0xFF);
        packet[5] = (byte)((crc >> 24) & 0xFF);
        Buffer.BlockCopy(inner, 0, packet, 6, inner.Length);
        return packet;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Standard CRC-32 (poly 0xEDB88320), as required by the BERCon header.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Parses the <c>players</c> command output into a player list.</summary>
    internal static List<RconPlayer> ParsePlayers(string response)
    {
        var list = new List<RconPlayer>();
        foreach (var raw in response.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Players on server", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("[#]")) continue;
            if (line.StartsWith("---")) continue;
            if (line.StartsWith("(")) continue;   // "(N players in total)"

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !int.TryParse(parts[0], out var id)) continue;

            var player = new RconPlayer { Id = id, IpPort = parts[1] };
            int.TryParse(parts[2], out var ping);
            player.Ping = ping;

            var guidField = parts[3];
            var paren = guidField.IndexOf('(');
            if (paren >= 0)
            {
                player.Guid = guidField[..paren];
                player.Verified = guidField.Contains("(OK)", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                player.Guid = guidField;
            }

            var name = string.Join(' ', parts.Skip(4));
            // A still-in-lobby player has a trailing "(Lobby)" marker after the name.
            if (name.EndsWith("(Lobby)", StringComparison.OrdinalIgnoreCase))
            {
                player.InLobby = true;
                name = name[..^"(Lobby)".Length].TrimEnd();
            }
            player.Name = name;
            list.Add(player);
        }
        return list;
    }

    /// <summary>Parses the <c>bans</c> command output (GUID Bans + IP Bans sections) into a ban list.</summary>
    internal static List<RconBan> ParseBans(string response)
    {
        var list = new List<RconBan>();
        foreach (var raw in response.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[#]")) continue;
            if (line.StartsWith("---")) continue;
            if (line.EndsWith("Bans:", StringComparison.OrdinalIgnoreCase)) continue;  // section headers

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var index)) continue;

            var ban = new RconBan { Index = index, GuidOrIp = parts[1] };
            // parts[2] = minutes left ("perm" / "-" => permanent). Reason is everything after it.
            if (parts.Length >= 3)
            {
                var minsField = parts[2];
                if (minsField.Equals("perm", StringComparison.OrdinalIgnoreCase) || minsField == "-" || minsField == "-1")
                    ban.MinutesRemaining = -1;
                else if (int.TryParse(minsField, out var mins))
                    ban.MinutesRemaining = mins;
                else
                    ban.MinutesRemaining = -1;

                ban.Reason = parts.Length >= 4 ? string.Join(' ', parts.Skip(3)) : string.Empty;
            }
            list.Add(ban);
        }
        return list;
    }

    /// <summary>Parses the <c>missions</c> command output into the list of mission template names.</summary>
    internal static List<string> ParseMissions(string response)
    {
        var list = new List<string>();
        foreach (var raw in response.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Missions on server", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("---")) continue;
            if (line.StartsWith("(")) continue;
            list.Add(line);
        }
        return list;
    }

    private void SetState(RconState s)
    {
        try { _state.OnNext(s); } catch (ObjectDisposedException) { /* disposed mid-flight */ }
    }

    public void Dispose()
    {
        Disconnect();
        try { _state.OnCompleted(); } catch { /* ignore */ }
        try { _messages.OnCompleted(); } catch { /* ignore */ }
        try { _players.OnCompleted(); } catch { /* ignore */ }
        _state.Dispose();
        _messages.Dispose();
        _players.Dispose();
    }

    private sealed class PendingCommand
    {
        public readonly TaskCompletionSource<string> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string?[]? Parts;
        public int Received;
    }
}
