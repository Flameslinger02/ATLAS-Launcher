using System.Collections.ObjectModel;
using System.Windows;
using Atlas.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;

namespace Atlas.Core.ViewModels;

/// <summary>
/// View model for the shell window: sidebar navigation, header status, and the status bar.
/// Hosts the currently-navigated page via <see cref="CurrentView"/>.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ISettingsService _settings;
    private readonly IProfileService _profiles;
    private readonly IUpdateService _updates;

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private bool _sidebarCollapsed;
    [ObservableProperty] private string _activeProfileName = "No active profile";
    [ObservableProperty] private string _serverStatusText = "Stopped";
    [ObservableProperty] private string _lastActionMessage = "Ready";
    [ObservableProperty] private string _uptimeText = "00:00:00";
    [ObservableProperty] private int _playerCount;

    // ----- Update banner (non-blocking; slides in on startup if a newer release exists) -----
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateBannerText = string.Empty;
    private string? _releaseUrl;

    public ObservableCollection<NavItem> NavItems { get; }

    public string VersionText { get; }

    /// <summary>Sidebar column width — wider when expanded, icon-only when collapsed.</summary>
    public GridLength SidebarWidth => new(SidebarCollapsed ? 56 : 240);

    public MainViewModel(INavigationService navigation, ISettingsService settings, IProfileService profiles,
        IUpdateService updates)
    {
        _navigation = navigation;
        _settings = settings;
        _profiles = profiles;
        _updates = updates;

        _navigation.Navigated += (_, view) => CurrentView = view;

        if (_profiles.ActiveProfile is not null) _activeProfileName = _profiles.ActiveProfile.Name;
        _profiles.ActiveProfileChanged += OnActiveProfileChanged;

        var version = typeof(MainViewModel).Assembly.GetName().Version;
        VersionText = version is null ? "v0.1.0" : $"v{version.Major}.{version.Minor}.{version.Build}";

        _sidebarCollapsed = settings.Settings.SidebarCollapsed;

        NavItems = new ObservableCollection<NavItem>
        {
            new() { Key = AppConstants.Pages.Dashboard,       Label = "Dashboard",        Icon = PackIconKind.ViewDashboard },
            new() { Key = AppConstants.Pages.ServerConfig,    Label = "Server Config",    Icon = PackIconKind.Tune },
            new() { Key = AppConstants.Pages.Mods,            Label = "Mods",             Icon = PackIconKind.Puzzle },
            new() { Key = AppConstants.Pages.ModPresets,      Label = "Mod Presets",      Icon = PackIconKind.PackageVariant },
            new() { Key = AppConstants.Pages.Missions,        Label = "Missions",         Icon = PackIconKind.Map },
            new() { Key = AppConstants.Pages.Profiles,        Label = "Profiles",         Icon = PackIconKind.AccountMultiple },
            new() { Key = AppConstants.Pages.HeadlessClients, Label = "Headless Clients", Icon = PackIconKind.Server },
            new() { Key = AppConstants.Pages.DiscordBot,      Label = "Discord Bot",      Icon = PackIconKind.Robot },
            new() { Key = AppConstants.Pages.Scheduler,       Label = "Scheduler",        Icon = PackIconKind.CalendarClock },
            new() { Key = AppConstants.Pages.Console,         Label = "Console / RCON",   Icon = PackIconKind.Console },
            new() { Key = AppConstants.Pages.Settings,        Label = "Settings",         Icon = PackIconKind.Cog },
        };

        Navigate(AppConstants.Pages.Dashboard);

        if (_settings.Settings.CheckUpdatesOnStartup)
            _ = CheckForUpdatesOnStartupAsync();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await _updates.CheckForUpdateAsync().ConfigureAwait(false);
            if (!result.UpdateAvailable) return;

            void Show()
            {
                _releaseUrl = result.ReleaseUrl;
                UpdateBannerText =
                    $"ATLAS v{result.LatestVersion} is available (you have v{result.CurrentVersion}).";
                UpdateBannerVisible = true;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Show();
            else await dispatcher.InvokeAsync(Show);
        }
        catch
        {
            // Startup update check is best-effort; UpdateService already logs failures.
        }
    }

    [RelayCommand]
    private void ViewRelease()
    {
        if (!string.IsNullOrWhiteSpace(_releaseUrl)) _updates.OpenReleasePage(_releaseUrl!);
        UpdateBannerVisible = false;
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateBannerVisible = false;

    private void OnActiveProfileChanged(object? sender, Models.ServerProfile profile)
    {
        // The event may originate off the UI thread; marshal the property update.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) ActiveProfileName = profile.Name;
        else dispatcher.InvokeAsync(() => ActiveProfileName = profile.Name);
    }

    partial void OnSidebarCollapsedChanged(bool value)
    {
        _settings.Settings.SidebarCollapsed = value;
        OnPropertyChanged(nameof(SidebarWidth));
    }

    [RelayCommand]
    private void Navigate(string? pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey)) return;
        _navigation.NavigateTo(pageKey);
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;
}
