using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Dashboard;

/// <summary>
/// The operations dashboard: server status + Start/Stop/Restart/Force-kill, live stats (uptime, CPU,
/// RAM, crashes), the headless-client panel, an RPT log tail and quick navigation to the console.
/// Registered as a singleton because it holds long-lived subscriptions to the process services
/// (NavigationService does not raise OnNavigatedFrom, so a transient VM would leak them).
/// </summary>
public partial class DashboardViewModel : BaseViewModel, IDisposable
{
    private readonly IServerProcessService _server;
    private readonly IHeadlessClientService _hc;
    private readonly IBattlEyeRconClient _rcon;
    private readonly IProfileService _profiles;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IMissionDependencyChecker _depChecker;
    private readonly ISteamQueryService _steamQuery;
    private readonly DispatcherTimer _timer;
    private int _steamTick;                 // seconds since the last A2S visibility poll
    private bool _steamQueryBusy;
    private readonly List<IDisposable> _subs = new();
    private const int MaxLogLines = 300;

    [ObservableProperty] private ServerState _serverState = ServerState.Stopped;
    [ObservableProperty] private string _serverStateText = "Stopped";
    [ObservableProperty] private string _activeProfileName = "No active profile";
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private string _uptimeText = "00:00:00";
    [ObservableProperty] private int _playerCount;            // live from the shared RCON player poll
    [ObservableProperty] private string _cpuText = "0 %";
    [ObservableProperty] private string _memoryText = "0 %";
    [ObservableProperty] private int _crashCount;
    [ObservableProperty] private string _steamStatusText = "Steam layer: —";

    /// <summary>Which scope the CPU/memory tiles and graphs report. True (default) = whole-computer load,
    /// all processes + OS, like Task Manager. False = just the Arma server process's share of the machine.
    /// Both scopes are sampled every tick, so flipping this swaps the graphs instantly with full history.</summary>
    [ObservableProperty] private bool _showSystemWide = true;

    /// <summary>Inverse of <see cref="ShowSystemWide"/>, for the "Server" segment of the scope toggle.</summary>
    public bool ShowInstance
    {
        get => !ShowSystemWide;
        set { if (value) ShowSystemWide = false; }
    }

    // Rolling performance history (1 sample/sec, 5 min window). Both scopes are recorded in parallel; the
    // *Series properties the sparklines bind to are snapshots of whichever scope is currently selected.
    private const int HistoryCapacity = 300;
    private readonly Queue<double> _cpuProcHistory = new();
    private readonly Queue<double> _cpuSysHistory = new();
    private readonly Queue<double> _memProcHistory = new();
    private readonly Queue<double> _memSysHistory = new();
    private readonly Queue<double> _playerHistory = new();
    [ObservableProperty] private IReadOnlyList<double> _cpuSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _memSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _playerSeries = Array.Empty<double>();
    [ObservableProperty] private string _memPeakText = "0 %";
    [ObservableProperty] private string _statusMessage = "Ready";

    // Quick actions (RCON, shared singleton client with the Console page).
    [ObservableProperty] private RconState _rconState = RconState.Disconnected;
    [ObservableProperty] private string _rconMessageInput = string.Empty;
    [ObservableProperty] private string _selectedMission = string.Empty;

    public ObservableCollection<string> LogTail { get; } = new();
    public ObservableCollection<HeadlessClientInstance> HeadlessClients { get; } = new();
    public ObservableCollection<string> Missions { get; } = new();

    public bool HasActiveProfile => _profiles.ActiveProfile is not null;
    public bool IsRunning => ServerState is ServerState.Running;
    public bool IsStopping => ServerState is ServerState.Stopping;
    public bool IsStopped => ServerState is ServerState.Stopped or ServerState.Crashed;
    public bool IsRconConnected => RconState == RconState.Connected;

    public DashboardViewModel(
        IServerProcessService server, IHeadlessClientService hc, IBattlEyeRconClient rcon,
        IProfileService profiles, INavigationService nav, IDialogService dialogs,
        IMissionDependencyChecker depChecker, ISteamQueryService steamQuery)
    {
        _server = server;
        _hc = hc;
        _rcon = rcon;
        _profiles = profiles;
        _nav = nav;
        _dialogs = dialogs;
        _depChecker = depChecker;
        _steamQuery = steamQuery;
        Title = "Dashboard";

        ApplyActiveProfile(_profiles.ActiveProfile);
        ApplyServerState(_server.CurrentState);
        RconState = _rcon.State;
        TryReattach();   // adopt a server left running from a previous session (if the profile is already active)

        _subs.Add(_server.StateChanged.Subscribe(s => OnUi(() => ApplyServerState(s))));
        _subs.Add(_server.LogOutput.Subscribe(line => OnUi(() => AppendLog(line))));
        _subs.Add(_hc.InstanceChanged.Subscribe(_ => OnUi(RebuildHeadlessClients)));
        _subs.Add(_rcon.StateChanged.Subscribe(s => OnUi(() => ApplyRconState(s))));
        _subs.Add(_rcon.PlayersUpdated.Subscribe(p => OnUi(() => PlayerCount = p.Count)));
        _profiles.ActiveProfileChanged += OnActiveProfileChanged;

        _hc.ConfigureInstances(_profiles.ActiveProfile?.HeadlessClientCount ?? 0);
        RebuildHeadlessClients();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ----- State / profile plumbing -----

    private void OnActiveProfileChanged(object? sender, ServerProfile profile) =>
        OnUi(() => { ApplyActiveProfile(profile); TryReattach(); });

    /// <summary>If a dedicated server from a previous session is still running, adopt it for the active
    /// profile so Stop/Force-kill, uptime, the RPT tail and crash detection are restored. No-ops if a
    /// server is already tracked/running or nothing matching is found. Runs when the profile is set or
    /// changes, because at app startup the active profile may not be known yet (no DB default).</summary>
    private void TryReattach()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return;
        if (ServerState is ServerState.Running or ServerState.Starting or ServerState.Stopping) return;
        try
        {
            if (_server.TryAdoptRunningServer(p))
                StatusMessage = "Re-attached to a server still running from a previous session.";
        }
        catch (Exception ex) { Log.Warning(ex, "Re-attach attempt failed."); }
    }

    private void ApplyActiveProfile(ServerProfile? p)
    {
        ActiveProfileName = p?.Name ?? "No active profile";
        ServerName = p?.ServerName ?? string.Empty;
        OnPropertyChanged(nameof(HasActiveProfile));
        StartCommand.NotifyCanExecuteChanged();
        StartHeadlessClientsCommand.NotifyCanExecuteChanged();
        ConnectRconCommand.NotifyCanExecuteChanged();
    }

    private void ApplyServerState(ServerState s)
    {
        ServerState = s;
        ServerStateText = s.ToString();
        CrashCount = _server.CrashCount;
    }

    private void ApplyRconState(RconState s)
    {
        RconState = s;
        if (s != RconState.Connected) { Missions.Clear(); PlayerCount = 0; }
        else _ = LoadMissionsAsync();
    }

    partial void OnRconStateChanged(RconState value)
    {
        OnPropertyChanged(nameof(IsRconConnected));
        ConnectRconCommand.NotifyCanExecuteChanged();
        SendRconMessageCommand.NotifyCanExecuteChanged();
        ChangeMissionQuickCommand.NotifyCanExecuteChanged();
        LockServerQuickCommand.NotifyCanExecuteChanged();
        UnlockServerQuickCommand.NotifyCanExecuteChanged();
        KickAllQuickCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadMissionsAsync()
    {
        try
        {
            var missions = await _rcon.GetMissionsAsync();
            OnUi(() =>
            {
                Missions.Clear();
                foreach (var m in missions) Missions.Add(m);
            });
        }
        catch (Exception ex) { Log.Debug(ex, "Could not load missions for the Dashboard dropdown."); }
    }

    partial void OnServerStateChanged(ServerState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopping));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(CanForceKill));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        ForceKillCommand.NotifyCanExecuteChanged();
    }

    private void Tick()
    {
        var running = IsRunning;

        // Sample BOTH scopes every tick, regardless of server state, so the graphs keep a continuous
        // timeline and the scope toggle swaps between them instantly with full history. Server-process
        // CPU is already a share of all cores; its memory becomes a share of total installed RAM. The
        // process figures read 0 while the server is stopped, so you can watch them jump on launch.
        var (procCpu, procMemBytes) = _server.GetResourceUsage();   // (0, 0) when there is no server process
        var (sysMemPct, totalRamMb) = QuerySystemMemory();
        var sysCpu = QuerySystemCpuLoadPercent();
        var procMemPct = totalRamMb > 0 ? procMemBytes / (1024.0 * 1024.0) / totalRamMb * 100.0 : 0;

        AppendHistory(procCpu, Math.Clamp(procMemPct, 0, 100),
                      Math.Max(sysCpu, 0), Math.Max(sysMemPct, 0), PlayerCount);
        RefreshSeries();

        if (running)
        {
            var up = _server.Uptime;
            UptimeText = $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
            // Poll Steam visibility every 30s (first check ~10s in, giving the Steam layer time to bind).
            if (++_steamTick >= 30) { _steamTick = 0; _ = RefreshSteamStatusAsync(); }
        }
        else
        {
            UptimeText = "00:00:00";
            _steamTick = 20;
            SteamStatusText = "Steam layer: —";
        }
    }

    private void AppendHistory(double procCpu, double procMemPct, double sysCpu, double sysMemPct, int players)
    {
        Push(_cpuProcHistory, procCpu);
        Push(_memProcHistory, procMemPct);
        Push(_cpuSysHistory, sysCpu);
        Push(_memSysHistory, sysMemPct);
        Push(_playerHistory, players);

        static void Push(Queue<double> q, double v) { q.Enqueue(v); while (q.Count > HistoryCapacity) q.Dequeue(); }
    }

    /// <summary>Republishes the tiles and sparkline series from whichever scope is selected. Called every
    /// tick and whenever the scope toggle flips — since both scopes are always recorded, switching redraws
    /// the full 5-minute history immediately rather than starting over.</summary>
    private void RefreshSeries()
    {
        var cpuQ = ShowSystemWide ? _cpuSysHistory : _cpuProcHistory;
        var memQ = ShowSystemWide ? _memSysHistory : _memProcHistory;

        CpuSeries = cpuQ.ToArray();
        MemSeries = memQ.ToArray();
        PlayerSeries = _playerHistory.ToArray();

        CpuText = $"{(cpuQ.Count > 0 ? cpuQ.Last() : 0):0} %";
        MemoryText = $"{(memQ.Count > 0 ? memQ.Last() : 0):0} %";
        MemPeakText = memQ.Count > 0 ? $"{memQ.Max():0} % peak" : "0 %";
    }

    partial void OnShowSystemWideChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInstance));
        RefreshSeries();
    }

    // Previous GetSystemTimes tick counts, for the delta-based system CPU calculation.
    private ulong _lastSysIdle, _lastSysKernel, _lastSysUser;
    private bool _sysCpuPrimed;

    /// <summary>Current system-wide CPU load as a 0–100 percentage (the same figure Task Manager
    /// reports), from the busy fraction of GetSystemTimes deltas between ticks. The first call primes
    /// the baseline and returns 0. Returns -1 if the query fails, which makes CPU fall back to the
    /// server process's own usage.</summary>
    private double QuerySystemCpuLoadPercent()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return -1;
            ulong i = ToU(idle), k = ToU(kernel), u = ToU(user);   // kernel time includes idle
            if (!_sysCpuPrimed)
            {
                _lastSysIdle = i; _lastSysKernel = k; _lastSysUser = u;
                _sysCpuPrimed = true;
                return 0;
            }
            var idleDelta = (double)(i - _lastSysIdle);
            var totalDelta = (double)((k - _lastSysKernel) + (u - _lastSysUser));
            _lastSysIdle = i; _lastSysKernel = k; _lastSysUser = u;
            if (totalDelta <= 0) return 0;
            return Math.Clamp((1.0 - idleDelta / totalDelta) * 100.0, 0, 100);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not read system CPU load; CPU will show the server process only."); return -1; }

        static ulong ToU(FILETIME f) => ((ulong)f.High << 32) | f.Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    /// <summary>One GlobalMemoryStatusEx read, giving both the system-wide physical memory load as a 0–100
    /// percentage (the figure Task Manager reports) and the total installed RAM in MB (used to express the
    /// server process's working set as a share of the machine). Returns (-1, 0) if the query fails.</summary>
    private static (double LoadPercent, double TotalMb) QuerySystemMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status)
                ? (status.dwMemoryLoad, status.ullTotalPhys / (1024.0 * 1024.0))
                : (-1, 0);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not read system memory."); return (-1, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>A2S_INFO against the local query port (game port + 1) — proves the server's Steam layer
    /// is up and answering browser queries, independent of RCON.</summary>
    [RelayCommand]
    private async Task RefreshSteamStatusAsync()
    {
        if (_steamQueryBusy) return;
        var p = _profiles.ActiveProfile;
        if (p is null || !IsRunning) { SteamStatusText = "Steam layer: —"; return; }

        _steamQueryBusy = true;
        try
        {
            var info = await _steamQuery.QueryInfoAsync("127.0.0.1", p.Port + 1, TimeSpan.FromSeconds(2));
            SteamStatusText = info is null
                ? "Steam layer: not answering (server may still be booting)"
                : $"Steam layer: up (local) — {info.Players}/{info.MaxPlayers} on {info.Map}{(info.PasswordProtected ? " (passworded)" : "")}";
        }
        finally { _steamQueryBusy = false; }
    }

    private void AppendLog(string line)
    {
        LogTail.Add(line);
        while (LogTail.Count > MaxLogLines) LogTail.RemoveAt(0);
    }

    private void RebuildHeadlessClients()
    {
        HeadlessClients.Clear();
        foreach (var inst in _hc.Instances) HeadlessClients.Add(inst);
    }

    // ----- Commands -----

    private bool CanStart => HasActiveProfile && IsStopped;
    private bool CanStartHeadless => HasActiveProfile;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) { await _dialogs.ShowErrorAsync("No active profile", "Activate a profile first."); return; }
        if (!await MissionDependencyGate.ConfirmAsync(_depChecker, _dialogs, p)) return;
        try
        {
            StatusMessage = "Launching server...";
            await _server.LaunchAsync(p);
        }
        catch (Exception ex)
        {
            StatusMessage = "Launch failed.";
            Log.Error(ex, "Dashboard server launch failed.");
            await _dialogs.ShowErrorAsync("Launch failed", ex.Message);
        }
    }

    // Stop must also be available while the launch is still in flight (config write / mod deploy /
    // Arma boot) — StopAsync aborts an in-progress launch rather than queueing behind it.
    private bool CanStop => ServerState is ServerState.Running or ServerState.Starting;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        try { StatusMessage = "Stopping server..."; await _server.StopAsync(false); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Stop failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task Restart()
    {
        try { StatusMessage = "Restarting server..."; await _server.RestartAsync(); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Restart failed", ex.Message); }
    }

    /// <summary>Force-kill is available whenever a launch or process is in flight — Starting (abort a
    /// launch stuck in config/deploy), Running (hung server escape hatch) and Stopping (graceful stop
    /// taking too long). Drives the button's CanExecute AND its Visibility so the two never disagree.</summary>
    public bool CanForceKill => IsRunning || IsStopping || ServerState is ServerState.Starting;

    [RelayCommand(CanExecute = nameof(CanForceKill))]
    private async Task ForceKill()
    {
        if (!await _dialogs.ConfirmAsync("Force kill", "Forcibly terminate the server process now?")) return;
        try { await _server.StopAsync(true); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Force kill failed", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanStartHeadless))]
    private async Task StartHeadlessClients()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return;
        try { StatusMessage = "Starting headless clients..."; await _hc.LaunchAllAsync(p); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Headless clients", ex.Message); }
    }

    [RelayCommand]
    private async Task StopHeadlessClients()
    {
        try { await _hc.StopAllAsync(); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Headless clients", ex.Message); }
    }

    [RelayCommand]
    private void OpenConsole() => _nav.NavigateTo(AppConstants.Pages.Console);

    // ----- Quick actions (RCON) -----

    private bool CanConnectRcon => HasActiveProfile && !IsRconConnected;

    [RelayCommand(CanExecute = nameof(CanConnectRcon))]
    private async Task ConnectRcon()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return;
        if (string.IsNullOrWhiteSpace(p.RconPassword))
        {
            await _dialogs.ShowErrorAsync("RCON", "This profile has no RCON password. Set one on the Server Config / RCON tab.");
            return;
        }
        try { await _rcon.ConnectAsync("127.0.0.1", p.RconPort, p.RconPassword); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("RCON", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(IsRconConnected))]
    private async Task SendRconMessage()
    {
        var msg = RconMessageInput.Trim();
        if (msg.Length == 0) return;
        RconMessageInput = string.Empty;
        await RunRcon(() => _rcon.SayGlobalAsync(msg));
    }

    [RelayCommand(CanExecute = nameof(IsRconConnected))]
    private async Task ChangeMissionQuick()
    {
        var name = SelectedMission?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunRcon(() => _rcon.SendCommandAsync($"#mission {name}"));
    }

    [RelayCommand(CanExecute = nameof(IsRconConnected))]
    private Task LockServerQuick() => RunRcon(_rcon.LockServerAsync);

    [RelayCommand(CanExecute = nameof(IsRconConnected))]
    private Task UnlockServerQuick() => RunRcon(_rcon.UnlockServerAsync);

    [RelayCommand(CanExecute = nameof(IsRconConnected))]
    private async Task KickAllQuick()
    {
        if (!await _dialogs.ConfirmAsync("Kick all", "Kick every connected player?")) return;
        try
        {
            var players = await _rcon.GetPlayersAsync();
            foreach (var pl in players) await RunRcon(() => _rcon.KickPlayerAsync(pl.Id, "Server maintenance"));
        }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Kick all", ex.Message); }
    }

    private async Task RunRcon(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { StatusMessage = $"RCON error: {ex.Message}"; }
    }

    // ----- helpers -----

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        _timer.Stop();
        _profiles.ActiveProfileChanged -= OnActiveProfileChanged;
        foreach (var s in _subs)
        {
            try { s.Dispose(); } catch { /* ignore */ }
        }
        _subs.Clear();
    }
}
