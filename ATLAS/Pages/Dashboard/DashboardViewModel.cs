using System.Collections.ObjectModel;
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
    private readonly DispatcherTimer _timer;
    private readonly List<IDisposable> _subs = new();
    private const int MaxLogLines = 300;

    [ObservableProperty] private ServerState _serverState = ServerState.Stopped;
    [ObservableProperty] private string _serverStateText = "Stopped";
    [ObservableProperty] private string _activeProfileName = "No active profile";
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private string _uptimeText = "00:00:00";
    [ObservableProperty] private int _playerCount;            // live from the shared RCON player poll
    [ObservableProperty] private string _cpuText = "0 %";
    [ObservableProperty] private string _memoryText = "0 MB";
    [ObservableProperty] private int _crashCount;
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
        IProfileService profiles, INavigationService nav, IDialogService dialogs)
    {
        _server = server;
        _hc = hc;
        _rcon = rcon;
        _profiles = profiles;
        _nav = nav;
        _dialogs = dialogs;
        Title = "Dashboard";

        ApplyActiveProfile(_profiles.ActiveProfile);
        ApplyServerState(_server.CurrentState);
        RconState = _rcon.State;

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
        OnUi(() => ApplyActiveProfile(profile));

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
        if (IsRunning)
        {
            var up = _server.Uptime;
            UptimeText = $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
            var (cpu, mem) = _server.GetResourceUsage();
            CpuText = $"{cpu:0} %";
            MemoryText = $"{mem / (1024.0 * 1024.0):0} MB";
        }
        else
        {
            UptimeText = "00:00:00";
            CpuText = "0 %";
            MemoryText = "0 MB";
        }
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

    [RelayCommand(CanExecute = nameof(IsRunning))]
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

    /// <summary>Force-kill is available whenever a process exists to kill — both Running (hung server
    /// escape hatch) and Stopping (graceful stop taking too long). Drives the button's CanExecute AND its
    /// Visibility so the two never disagree.</summary>
    public bool CanForceKill => IsRunning || IsStopping;

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
