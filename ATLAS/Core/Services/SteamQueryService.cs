using System.Net.Sockets;
using System.Text;
using Serilog;

namespace Atlas.Core.Services;

/// <summary>Result of a Steam A2S_INFO query against a running server's query port.</summary>
public sealed record ServerQueryInfo(
    string Name, string Map, string Game, int Players, int MaxPlayers, bool PasswordProtected);

/// <summary>
/// Queries a game server over Valve's A2S protocol — the same channel the Steam server browser and
/// launcher use. A successful reply proves the server process has its Steam layer up and is answering
/// queries; independent of RCON/BattlEye.
/// </summary>
public interface ISteamQueryService
{
    /// <summary>A2S_INFO query. Returns null when the server does not answer within the timeout.</summary>
    Task<ServerQueryInfo?> QueryInfoAsync(string host, int queryPort, TimeSpan timeout, CancellationToken ct = default);
}

/// <inheritdoc cref="ISteamQueryService"/>
public sealed class SteamQueryService : ISteamQueryService
{
    // FF FF FF FF 'T' "Source Engine Query\0"
    private static readonly byte[] InfoRequest =
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x54 }
            .Concat(Encoding.ASCII.GetBytes("Source Engine Query\0")).ToArray();

    public async Task<ServerQueryInfo?> QueryInfoAsync(
        string host, int queryPort, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect(host, queryPort);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await udp.SendAsync(InfoRequest, cts.Token).ConfigureAwait(false);
            var response = (await udp.ReceiveAsync(cts.Token).ConfigureAwait(false)).Buffer;

            // Modern servers first answer with an A2S challenge (0x41): resend with the 4 challenge bytes.
            if (response.Length >= 9 && response[4] == 0x41)
            {
                var challenged = InfoRequest.Concat(response.Skip(5).Take(4)).ToArray();
                await udp.SendAsync(challenged, cts.Token).ConfigureAwait(false);
                response = (await udp.ReceiveAsync(cts.Token).ConfigureAwait(false)).Buffer;
            }

            return Parse(response);
        }
        catch (OperationCanceledException) { return null; }                    // timed out — not answering
        catch (SocketException) { return null; }                               // port closed / unreachable
        catch (Exception ex)
        {
            Log.Debug(ex, "A2S query to {Host}:{Port} failed.", host, queryPort);
            return null;
        }
    }

    /// <summary>Parses an S2A_INFO (0x49) payload. Returns null on any unexpected shape.</summary>
    private static ServerQueryInfo? Parse(byte[] d)
    {
        if (d.Length < 6 || d[4] != 0x49) return null;
        var pos = 5;
        pos++;                                        // protocol version byte
        var name = ReadString(d, ref pos);
        var map = ReadString(d, ref pos);
        ReadString(d, ref pos);                       // folder
        var game = ReadString(d, ref pos);
        pos += 2;                                     // steam app id (short)
        if (pos + 5 >= d.Length) return null;
        int players = d[pos++];
        int maxPlayers = d[pos++];
        pos++;                                        // bots
        pos++;                                        // server type
        pos++;                                        // environment
        var passworded = pos < d.Length && d[pos] == 1;       // visibility byte: 1 = passworded
        return new ServerQueryInfo(name, map, game, players, maxPlayers, passworded);
    }

    private static string ReadString(byte[] d, ref int pos)
    {
        var start = pos;
        while (pos < d.Length && d[pos] != 0) pos++;
        var s = Encoding.UTF8.GetString(d, start, pos - start);
        pos++;
        return s;
    }
}
