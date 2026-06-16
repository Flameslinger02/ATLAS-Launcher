using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using Atlas.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Console;

/// <summary>
/// Live BattlEye RCon console for the active profile. Three RCON sub-tabs — Players (grid + context menu +
/// 30s auto-refresh), Bans (list + unban/manual-ban/export) and the RCON command console (history + quick
/// buttons) — plus a log viewer (the ATLAS App Log and the live Server RPT tail). Bans are written to
/// <c>BanHistory</c>; player join/leave is derived from the 30s poll diff and written to <c>PlayerHistory</c>.
/// Singleton VM (holds long-lived RCON/server subscriptions; mirrors the Dashboard no-leak rule).
/// </summary>
public partial class ConsoleViewModel : BaseViewModel, IDisposable
{
    private readonly IBattlEyeRconClient _rcon;
    private readonly IServerProcessService _server;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly AtlasDatabase _db;
    private readonly List<IDisposable> _subs = new();
    private readonly DispatcherTimer _countdownTimer;
    private const int MaxLogLines = 500;
    private const int PollSeconds = 30;

    // Player join/leave diff state (poll-to-poll).
    private readonly Dictionary<string, string> _lastPlayers = new();   // guid -> name
    private bool _hasBaseline;

    // RCON command history (most-recent last); _historyIndex == Count means "new line".
    private readonly List<string> _history = new();
    private int _historyIndex;

    [ObservableProperty] private RconState _connectionState = RconState.Disconnected;
    [ObservableProperty] private string _connectionStateText = "Disconnected";
    [ObservableProperty] private string _commandInput = string.Empty;
    [ObservableProperty] private string _globalMessageInput = string.Empty;
    [ObservableProperty] private string _statusMessage = "Not connected.";
    [ObservableProperty] private PlayerRow? _selectedPlayer;
    [ObservableProperty] private RconBan? _selectedBan;
    [ObservableProperty] private int _refreshCountdown = PollSeconds;

    public ObservableCollection<string> ConsoleLog { get; } = new();
    public ObservableCollection<string> ServerRpt { get; } = new();
    public ObservableCollection<PlayerRow> Players { get; } = new();
    public ObservableCollection<RconBan> Bans { get; } = new();

    /// <summary>The "App Log" tab view model (ATLAS's own Serilog output).</summary>
    public AppLogViewModel AppLog { get; }

    public bool HasActiveProfile => _profiles.ActiveProfile is not null;
    public bool IsConnected => ConnectionState == RconState.Connected;
    public bool IsDisconnected => ConnectionState is RconState.Disconnected or RconState.Failed;

    public string PlayersHeader => $"Players ({Players.Count})";
    public string CountdownText => IsConnected ? $"auto-refresh in {RefreshCountdown}s" : "—";

    public ConsoleViewModel(IBattlEyeRconClient rcon, IServerProcessService server, IProfileService profiles,
        IDialogService dialogs, AtlasDatabase db, AppLogViewModel appLog)
    {
        _rcon = rcon;
        _server = server;
        _profiles = profiles;
        _dialogs = dialogs;
        _db = db;
        AppLog = appLog;
        Title = "Console / RCON";

        ApplyState(_rcon.State);
        _subs.Add(_rcon.StateChanged.Subscribe(s => OnUi(() => ApplyState(s))));
        _subs.Add(_rcon.MessageReceived.Subscribe(m => OnUi(() => OnServerMessage(m))));
        _subs.Add(_rcon.PlayersUpdated.Subscribe(p => OnUi(() => ApplyPlayers(p))));
        _subs.Add(_server.LogOutput.Subscribe(line => OnUi(() => AppendServerRpt(line))));
        _profiles.ActiveProfileChanged += OnActiveProfileChanged;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        _countdownTimer.Start();
    }

    private void OnActiveProfileChanged(object? sender, ServerProfile profile) => OnUi(() =>
    {
        OnPropertyChanged(nameof(HasActiveProfile));
        ConnectCommand.NotifyCanExecuteChanged();
    });

    private void ApplyState(RconState s)
    {
        ConnectionState = s;
        ConnectionStateText = s.ToString();
        StatusMessage = s switch
        {
            RconState.Connected => "Connected.",
            RconState.Connecting => "Connecting...",
            RconState.Failed => "Connection failed.",
            _ => "Not connected."
        };
        if (s != RconState.Connected) ResetPlayerState();
        AppendLog($"[rcon] {s}");
    }

    partial void OnConnectionStateChanged(RconState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(CanActOnPlayer));
        OnPropertyChanged(nameof(CountdownText));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        SendGlobalMessageCommand.NotifyCanExecuteChanged();
        RefreshPlayersCommand.NotifyCanExecuteChanged();
        RefreshBansCommand.NotifyCanExecuteChanged();
        AddManualBanCommand.NotifyCanExecuteChanged();
        SayCommand.NotifyCanExecuteChanged();
        LockServerCommand.NotifyCanExecuteChanged();
        UnlockServerCommand.NotifyCanExecuteChanged();
        RestartMissionCommand.NotifyCanExecuteChanged();
        ReassignMissionCommand.NotifyCanExecuteChanged();
        ShutdownServerCommand.NotifyCanExecuteChanged();
        ListMissionsCommand.NotifyCanExecuteChanged();
        ChangeMissionCommand.NotifyCanExecuteChanged();
        KickAllCommand.NotifyCanExecuteChanged();
        UnbanCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPlayerChanged(PlayerRow? value) => OnPropertyChanged(nameof(CanActOnPlayer));
    partial void OnSelectedBanChanged(RconBan? value) => UnbanCommand.NotifyCanExecuteChanged();
    partial void OnRefreshCountdownChanged(int value) => OnPropertyChanged(nameof(CountdownText));

    /// <summary>Single-target context actions require a live connection and a selected player.</summary>
    public bool CanActOnPlayer => IsConnected && SelectedPlayer is not null;

    // ----- Connection -----

    private bool CanConnect => HasActiveProfile && IsDisconnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return;
        if (string.IsNullOrWhiteSpace(p.RconPassword))
        {
            await _dialogs.ShowErrorAsync("RCON", "This profile has no RCON password. Set one on the Server Config / RCON tab.");
            return;
        }
        AppendLog($"[rcon] connecting to 127.0.0.1:{p.RconPort}...");
        var ok = await _rcon.ConnectAsync("127.0.0.1", p.RconPort, p.RconPassword);
        if (ok) { await RefreshPlayers(); await RefreshBans(); }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Disconnect() => _rcon.Disconnect();

    // ----- RCON command console -----

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task Send()
    {
        var cmd = CommandInput.Trim();
        if (cmd.Length == 0) return;
        PushHistory(cmd);
        CommandInput = string.Empty;
        AppendLog($"> {cmd}");
        try
        {
            var response = await _rcon.SendCommandAsync(cmd);
            if (!string.IsNullOrWhiteSpace(response)) AppendLog(response);
        }
        catch (Exception ex)
        {
            AppendLog($"[error] {ex.Message}");
        }
    }

    private void PushHistory(string cmd)
    {
        if (_history.Count == 0 || !string.Equals(_history[^1], cmd, StringComparison.Ordinal))
            _history.Add(cmd);
        _historyIndex = _history.Count;   // reset to "new line"
    }

    /// <summary>Recalls the previous (older) command into the input box; called from the code-behind on Up.</summary>
    public void HistoryPrevious()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        CommandInput = _history[_historyIndex];
    }

    /// <summary>Recalls the next (newer) command, or clears to a fresh line past the end; on Down.</summary>
    public void HistoryNext()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        CommandInput = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task SendGlobalMessage()
    {
        var msg = GlobalMessageInput.Trim();
        if (msg.Length == 0) return;
        GlobalMessageInput = string.Empty;
        await RunRcon(() => _rcon.SayGlobalAsync(msg));
        AppendLog($"[say] {msg}");
    }

    // ----- Quick buttons -----

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task LockServer() => RunRcon(_rcon.LockServerAsync);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task UnlockServer() => RunRcon(_rcon.UnlockServerAsync);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task RestartMission() => RunRcon(_rcon.RestartMissionAsync);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ReassignMission() => RunRcon(_rcon.ReassignMissionAsync);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ShutdownServer()
    {
        if (!await _dialogs.ConfirmAsync("Shutdown", "Send #shutdown to the server?")) return;
        await RunRcon(_rcon.ShutdownServerAsync);
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ListMissions()
    {
        try
        {
            var missions = await _rcon.GetMissionsAsync();
            AppendLog($"[missions] {(missions.Count == 0 ? "(none reported)" : string.Join(", ", missions))}");
        }
        catch (Exception ex) { AppendLog($"[error] missions: {ex.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task Say()
    {
        var msg = await _dialogs.PromptAsync("Broadcast", "Message to all players:");
        if (string.IsNullOrEmpty(msg)) return;
        await RunRcon(() => _rcon.SayGlobalAsync(msg));
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ChangeMission()
    {
        var name = await _dialogs.PromptAsync("Change mission", "Mission template name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunRcon(() => _rcon.SendCommandAsync($"#mission {name}"));
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task KickAll()
    {
        if (!await _dialogs.ConfirmAsync("Kick all", "Kick every connected player?")) return;
        var snapshot = Players.ToList();
        foreach (var row in snapshot)
            await RunRcon(() => _rcon.KickPlayerAsync(row.Id, "Server maintenance"));
        await RefreshPlayers();
    }

    // ----- Players -----

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task RefreshPlayers()
    {
        try { await _rcon.GetPlayersAsync(); }   // publishes to PlayersUpdated -> ApplyPlayers
        catch (Exception ex) { AppendLog($"[error] players: {ex.Message}"); }
    }

    private void ApplyPlayers(IReadOnlyList<RconPlayer> players)
    {
        // Preserve per-row unmask state and the current selection across the 30s rebuild (keyed by GUID).
        var unmasked = Players.Where(r => r.IsUnmasked).Select(r => r.Guid).ToHashSet(StringComparer.Ordinal);
        var selectedGuid = SelectedPlayer?.Guid;

        Players.Clear();
        foreach (var pl in players)
            Players.Add(new PlayerRow(pl) { IsUnmasked = unmasked.Contains(pl.Guid) });

        if (!string.IsNullOrEmpty(selectedGuid))
            SelectedPlayer = Players.FirstOrDefault(r => r.Guid == selectedGuid);

        DiffPlayerHistory(players);

        OnPropertyChanged(nameof(PlayersHeader));
        StatusMessage = $"{Players.Count} player(s) online.";
        RefreshCountdown = PollSeconds;   // a fresh list resets the visible auto-refresh countdown
    }

    private void DiffPlayerHistory(IReadOnlyList<RconPlayer> players)
    {
        var current = players.Where(p => !string.IsNullOrWhiteSpace(p.Guid))
                             .ToDictionary(p => p.Guid, p => p, StringComparer.Ordinal);

        // The first snapshot only establishes the baseline (don't log everyone as a join).
        if (_hasBaseline)
        {
            foreach (var (guid, p) in current)
                if (!_lastPlayers.ContainsKey(guid))
                    _ = WritePlayerHistoryAsync(p.Name, "Connected", p.Ip, guid);

            foreach (var (guid, name) in _lastPlayers)
                if (!current.ContainsKey(guid))
                    _ = WritePlayerHistoryAsync(name, "Disconnected", string.Empty, guid);
        }

        _lastPlayers.Clear();
        foreach (var (guid, p) in current) _lastPlayers[guid] = p.Name;
        _hasBaseline = true;
    }

    private void ResetPlayerState()
    {
        Players.Clear();
        _lastPlayers.Clear();
        _hasBaseline = false;
        RefreshCountdown = PollSeconds;
        OnPropertyChanged(nameof(PlayersHeader));
    }

    private void OnCountdownTick()
    {
        if (!IsConnected) return;
        if (RefreshCountdown > 0) RefreshCountdown--;
        // The actual refresh is driven by the client's 30s poll (PlayersUpdated), which also resets the
        // countdown; this timer only animates the visible figure.
    }

    // ----- Player context actions (invoked from the grid context menu in the code-behind) -----

    public async Task KickPlayersAsync(IList? rows)
    {
        foreach (var row in Snapshot(rows))
        {
            var reason = await _dialogs.PromptAsync("Kick player", $"Reason for kicking {row.Name}:", "Kicked by admin");
            if (reason is null) continue;
            await RunRcon(() => _rcon.KickPlayerAsync(row.Id, reason));
        }
        await RefreshPlayers();
    }

    public async Task BanPlayersByIdAsync(IList? rows)
    {
        foreach (var row in Snapshot(rows))
        {
            var (minutes, reason) = await PromptBanAsync(row.Name);
            if (reason is null) continue;
            await RunRcon(() => _rcon.BanPlayerAsync(row.Id, minutes, reason));
            await WriteBanHistoryAsync(row.Guid, row.Name, minutes, reason);
        }
        await RefreshPlayers();
    }

    public async Task BanPlayerByGuidAsync(PlayerRow? row)
    {
        if (row is null) return;
        if (string.IsNullOrWhiteSpace(row.Guid)) { await _dialogs.ShowErrorAsync("Ban by GUID", "This player has no resolved GUID yet."); return; }
        var (minutes, reason) = await PromptBanAsync(row.Name);
        if (reason is null) return;
        await RunRcon(() => _rcon.BanGuidAsync(row.Guid, minutes, reason));
        await WriteBanHistoryAsync(row.Guid, row.Name, minutes, reason);
        await RefreshBans();
    }

    public async Task SendPrivateMessageAsync(PlayerRow? row)
    {
        if (row is null) return;
        var msg = await _dialogs.PromptAsync("Private message", $"Message to {row.Name}:");
        if (string.IsNullOrEmpty(msg)) return;
        await RunRcon(() => _rcon.SayPlayerAsync(row.Id, msg));
        AppendLog($"[pm->{row.Id}] {msg}");
    }

    public void CopyGuid(PlayerRow? row) => CopyToClipboard(row?.Guid);
    public void CopyName(PlayerRow? row) => CopyToClipboard(row?.Name);
    public void ToggleUnmaskIp(PlayerRow? row) { if (row is not null) row.IsUnmasked = !row.IsUnmasked; }

    private async Task<(int minutes, string? reason)> PromptBanAsync(string name)
    {
        var minsText = await _dialogs.PromptAsync("Ban player", $"Ban duration in minutes (0 = permanent) for {name}:", "0");
        if (minsText is null) return (0, null);
        int.TryParse(minsText, out var minutes);
        var reason = await _dialogs.PromptAsync("Ban player", "Ban reason:", "Banned by admin");
        return (minutes, reason);
    }

    private static List<PlayerRow> Snapshot(IList? rows) =>
        rows?.OfType<PlayerRow>().ToList() ?? new List<PlayerRow>();

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch (Exception ex) { Log.Warning(ex, "Clipboard copy failed."); }
    }

    // ----- Bans -----

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task RefreshBans()
    {
        try
        {
            var bans = await _rcon.GetBansAsync();
            await ResolveKnownNamesAsync(bans);
            Bans.Clear();
            foreach (var b in bans) Bans.Add(b);
            StatusMessage = $"{Bans.Count} ban(s).";
        }
        catch (Exception ex) { AppendLog($"[error] bans: {ex.Message}"); }
    }

    private bool CanUnban => IsConnected && SelectedBan is not null;

    [RelayCommand(CanExecute = nameof(CanUnban))]
    private async Task Unban()
    {
        var ban = SelectedBan;
        if (ban is null) return;
        if (!await _dialogs.ConfirmAsync("Unban", $"Remove ban #{ban.Index} ({ban.GuidOrIp})?")) return;
        await RunRcon(() => _rcon.UnbanAsync(ban.Index));
        await RefreshBans();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task AddManualBan()
    {
        var target = await _dialogs.PromptAsync("Add ban", "GUID to ban:");
        if (string.IsNullOrWhiteSpace(target)) return;
        var minsText = await _dialogs.PromptAsync("Add ban", "Duration in minutes (0 = permanent):", "0");
        if (minsText is null) return;
        int.TryParse(minsText, out var minutes);
        var reason = await _dialogs.PromptAsync("Add ban", "Reason:", "Banned by admin");
        if (reason is null) return;
        await RunRcon(() => _rcon.BanGuidAsync(target.Trim(), minutes, reason));
        await WriteBanHistoryAsync(target.Trim(), string.Empty, minutes, reason);
        await RefreshBans();
    }

    [RelayCommand]
    private async Task ExportBans()
    {
        if (Bans.Count == 0) { await _dialogs.ShowInfoAsync("Export bans", "There are no bans to export."); return; }
        var path = await _dialogs.SaveFileAsync("Export bans", "Text file|*.txt", $"atlas-bans-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var lines = Bans.Select(b =>
                $"{b.Index}\t{b.GuidOrIp}\t{(b.IsPermanent ? "perm" : b.MinutesRemaining + "m")}\t{b.KnownName}\t{b.Reason}").ToList();
            await File.WriteAllLinesAsync(path, lines);
            StatusMessage = $"Exported {lines.Count} ban(s).";
        }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Export failed", ex.Message); }
    }

    // ----- Server RPT log viewer -----

    [RelayCommand]
    private void OpenRptInExplorer()
    {
        var dir = _profiles.ActiveProfile?.ServerDirectory;
        if (string.IsNullOrWhiteSpace(dir)) { StatusMessage = "No active profile / server directory."; return; }
        var profilesDir = Path.Combine(dir, "profiles");
        var target = Directory.Exists(profilesDir) ? profilesDir : dir;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Failed to open RPT folder."); StatusMessage = "Could not open the RPT folder."; }
    }

    [RelayCommand]
    private async Task SaveRpt()
    {
        if (ServerRpt.Count == 0) { await _dialogs.ShowInfoAsync("Save RPT", "The RPT tail is empty."); return; }
        var path = await _dialogs.SaveFileAsync("Save RPT tail", "Log file|*.log;*.txt", $"server-rpt-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { await File.WriteAllLinesAsync(path, ServerRpt.ToList()); StatusMessage = "RPT tail saved."; }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Save failed", ex.Message); }
    }

    [RelayCommand]
    private void ClearServerRpt() => ServerRpt.Clear();

    private void AppendServerRpt(string line)
    {
        ServerRpt.Add(line);
        while (ServerRpt.Count > MaxLogLines) ServerRpt.RemoveAt(0);
    }

    // ----- Helpers -----

    private async Task RunRcon(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { AppendLog($"[error] {ex.Message}"); }
    }

    private void OnServerMessage(string message) => AppendLog(message);

    private void AppendLog(string line)
    {
        ConsoleLog.Add(line);
        while (ConsoleLog.Count > MaxLogLines) ConsoleLog.RemoveAt(0);
    }

    private async Task ResolveKnownNamesAsync(IReadOnlyList<RconBan> bans)
    {
        var profileId = _profiles.ActiveProfile?.Id ?? 0;
        if (profileId <= 0 || bans.Count == 0) return;
        try
        {
            await using var conn = _db.CreateOpenConnection();
            foreach (var ban in bans)
            {
                if (string.IsNullOrWhiteSpace(ban.GuidOrIp)) continue;
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT PlayerName FROM PlayerHistory WHERE ProfileId = $pid AND PlayerGuid = $guid " +
                    "AND PlayerName <> '' ORDER BY EventAt DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$pid", profileId);
                cmd.Parameters.AddWithValue("$guid", ban.GuidOrIp);
                if (await cmd.ExecuteScalarAsync().ConfigureAwait(false) is string name) ban.KnownName = name;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to resolve ban known-names."); }
    }

    private async Task WriteBanHistoryAsync(string guid, string name, int minutes, string reason)
    {
        var profileId = _profiles.ActiveProfile?.Id ?? 0;
        if (profileId <= 0) return;   // BanHistory.ProfileId is NOT NULL
        try
        {
            await _db.WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var conn = _db.CreateOpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO BanHistory (ProfileId, PlayerGuid, PlayerName, Reason, Duration, BannedAt, BannedBy) " +
                    "VALUES ($pid, $guid, $name, $reason, $dur, $at, 'ATLAS');";
                cmd.Parameters.AddWithValue("$pid", profileId);
                cmd.Parameters.AddWithValue("$guid", guid);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$reason", reason);
                cmd.Parameters.AddWithValue("$dur", minutes);
                cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { _db.WriteLock.Release(); }
        }
        catch (Exception ex) { Log.Error(ex, "Failed to write BanHistory."); }
    }

    private async Task WritePlayerHistoryAsync(string name, string evt, string ip, string guid)
    {
        var profileId = _profiles.ActiveProfile?.Id ?? 0;
        if (profileId <= 0) return;   // PlayerHistory.ProfileId is NOT NULL
        try
        {
            await _db.WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var conn = _db.CreateOpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO PlayerHistory (ProfileId, PlayerGuid, PlayerName, EventType, EventAt, SessionIp) " +
                    "VALUES ($pid, $guid, $name, $evt, $at, $ip);";
                cmd.Parameters.AddWithValue("$pid", profileId);
                cmd.Parameters.AddWithValue("$guid", guid ?? string.Empty);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$evt", evt);
                cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$ip", ip ?? string.Empty);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { _db.WriteLock.Release(); }
        }
        catch (Exception ex) { Log.Error(ex, "Failed to write PlayerHistory."); }
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        _countdownTimer.Stop();
        _profiles.ActiveProfileChanged -= OnActiveProfileChanged;
        foreach (var s in _subs)
        {
            try { s.Dispose(); } catch { /* ignore */ }
        }
        _subs.Clear();
    }
}
