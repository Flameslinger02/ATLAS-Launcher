using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.HeadlessClients;

/// <summary>
/// Manages the headless-client pool for the active profile: a config panel (exe path, count, auto-restart,
/// HC IPs) that persists to the profile, and per-instance status cards with start/stop/restart/view-log.
/// Singleton (mirrors <see cref="Dashboard.DashboardViewModel"/>) so its service subscription does not leak.
/// </summary>
public partial class HeadlessClientsViewModel : BaseViewModel, IDisposable
{
    private readonly IHeadlessClientService _hc;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly IDisposable _sub;

    [ObservableProperty] private HcInstanceRow? _selectedInstance;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Config panel (seeded from the active profile; persisted by SaveConfig).
    [ObservableProperty] private string _hcExePath = string.Empty;
    [ObservableProperty] private int _hcCount = 1;
    [ObservableProperty] private bool _hcAutoRestart = true;
    [ObservableProperty] private string _hcIpsText = string.Empty;

    public ObservableCollection<HcInstanceRow> Instances { get; } = new();

    public bool HasActiveProfile => _profiles.ActiveProfile is not null;

    public HeadlessClientsViewModel(IHeadlessClientService hc, IProfileService profiles, IDialogService dialogs)
    {
        _hc = hc;
        _profiles = profiles;
        _dialogs = dialogs;
        Title = "Headless Clients";

        _sub = _hc.InstanceChanged.Subscribe(_ => OnUi(Rebuild));
        _profiles.ActiveProfileChanged += OnActiveProfileChanged;

        SeedConfig(_profiles.ActiveProfile);
        _hc.ConfigureInstances(_profiles.ActiveProfile?.HeadlessClientCount ?? 0);
        Rebuild();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => { foreach (var row in Instances) row.Tick(); };
        _uptimeTimer.Start();
    }

    private void OnActiveProfileChanged(object? sender, ServerProfile profile) => OnUi(() =>
    {
        OnPropertyChanged(nameof(HasActiveProfile));
        SeedConfig(profile);
        _hc.ConfigureInstances(profile?.HeadlessClientCount ?? 0);
        Rebuild();
        StartAllCommand.NotifyCanExecuteChanged();
        SaveConfigCommand.NotifyCanExecuteChanged();
    });

    private void SeedConfig(ServerProfile? p)
    {
        HcExePath = p?.HeadlessClientExecutablePath ?? string.Empty;
        HcCount = Math.Clamp(p?.HeadlessClientCount ?? 1, 1, 10);
        HcAutoRestart = p?.HeadlessAutoRestart ?? true;
        HcIpsText = p is null ? string.Empty : string.Join(Environment.NewLine, p.HeadlessClientIPs);
    }

    private void Rebuild()
    {
        var selectedIndex = SelectedInstance?.Index;
        Instances.Clear();
        foreach (var inst in _hc.Instances)
        {
            var row = new HcInstanceRow(inst);
            row.Tick();
            Instances.Add(row);
        }
        if (selectedIndex is int idx)
            SelectedInstance = Instances.FirstOrDefault(i => i.Index == idx);
    }

    // ----- Config panel -----

    [RelayCommand]
    private void IncrementCount() => HcCount = Math.Min(10, HcCount + 1);

    [RelayCommand]
    private void DecrementCount() => HcCount = Math.Max(1, HcCount - 1);

    [RelayCommand]
    private async Task BrowseExe()
    {
        var dir = _profiles.ActiveProfile?.ServerDirectory;
        var path = await _dialogs.BrowseFileAsync("Headless client executable", "Executable|*.exe|All files|*.*", dir);
        if (path is not null) HcExePath = path;
    }

    [RelayCommand(CanExecute = nameof(HasActiveProfile))]
    private async Task SaveConfig()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return;
        p.HeadlessClientExecutablePath = HcExePath.Trim();
        p.HeadlessClientCount = Math.Clamp(HcCount, 1, 10);
        p.HeadlessAutoRestart = HcAutoRestart;
        p.HeadlessClientIPs = HcIpsText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        try
        {
            await _profiles.UpdateProfileAsync(p);
            _hc.ConfigureInstances(p.HeadlessClientCount);
            Rebuild();
            StatusMessage = "Headless client configuration saved.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save headless client config.");
            await _dialogs.ShowErrorAsync("Save failed", ex.Message);
        }
    }

    // ----- Pool / instance control -----

    [RelayCommand(CanExecute = nameof(HasActiveProfile))]
    private async Task StartAll()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return;
        try { StatusMessage = "Starting all headless clients..."; await _hc.LaunchAllAsync(p); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Headless clients", ex.Message); }
    }

    [RelayCommand]
    private async Task StopAll()
    {
        try { StatusMessage = "Stopping all headless clients..."; await _hc.StopAllAsync(); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Headless clients", ex.Message); }
    }

    [RelayCommand]
    private async Task StartOne(HcInstanceRow? row)
    {
        var p = _profiles.ActiveProfile;
        if (row is null || p is null) return;
        try { await _hc.LaunchSingleAsync(p, row.Index); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Headless clients", ex.Message); }
    }

    [RelayCommand]
    private async Task StopOne(HcInstanceRow? row)
    {
        if (row is null) return;
        try { await _hc.StopSingleAsync(row.Index); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Headless clients", ex.Message); }
    }

    [RelayCommand]
    private async Task RestartOne(HcInstanceRow? row)
    {
        var p = _profiles.ActiveProfile;
        if (row is null || p is null) return;
        try { await _hc.RestartSingleAsync(p, row.Index); }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Headless clients", ex.Message); }
    }

    [RelayCommand]
    private void ViewLog(HcInstanceRow? row)
    {
        if (row is null) return;
        try
        {
            var dir = _hc.GetInstanceLogDirectory(row.Index);
            var window = new HcLogWindow(row.Name, dir)
            {
                Owner = Application.Current?.MainWindow
            };
            window.Show();
        }
        catch (Exception ex) { Log.Error(ex, "Failed to open HC log window."); }
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        _uptimeTimer.Stop();
        _profiles.ActiveProfileChanged -= OnActiveProfileChanged;
        try { _sub.Dispose(); } catch { /* ignore */ }
    }
}
