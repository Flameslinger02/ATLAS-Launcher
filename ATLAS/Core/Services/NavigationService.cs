using Atlas.Pages.Console;
using Atlas.Pages.Dashboard;
using Atlas.Pages.DiscordBot;
using Atlas.Pages.HeadlessClients;
using Atlas.Pages.Missions;
using Atlas.Pages.ModPresets;
using Atlas.Pages.Mods;
using Atlas.Pages.Profiles;
using Atlas.Pages.Scheduler;
using Atlas.Pages.ServerConfig;
using Atlas.Pages.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="INavigationService"/>
public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<string, Type> _pageMap;

    public event EventHandler<object>? Navigated;
    public object? CurrentView { get; private set; }
    public string? CurrentPageKey { get; private set; }

    public NavigationService(IServiceProvider services)
    {
        _services = services;
        _pageMap = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            [AppConstants.Pages.Dashboard] = typeof(DashboardPage),
            [AppConstants.Pages.ServerConfig] = typeof(ServerConfigPage),
            [AppConstants.Pages.Mods] = typeof(ModsPage),
            [AppConstants.Pages.ModPresets] = typeof(ModPresetsPage),
            [AppConstants.Pages.Missions] = typeof(MissionsPage),
            [AppConstants.Pages.Profiles] = typeof(ProfilesPage),
            [AppConstants.Pages.HeadlessClients] = typeof(HeadlessClientsPage),
            [AppConstants.Pages.DiscordBot] = typeof(DiscordBotPage),
            [AppConstants.Pages.Scheduler] = typeof(SchedulerPage),
            [AppConstants.Pages.Console] = typeof(ConsolePage),
            [AppConstants.Pages.Settings] = typeof(SettingsPage),
        };
    }

    public void NavigateTo(string pageKey)
    {
        if (!_pageMap.TryGetValue(pageKey, out var viewType))
        {
            Log.Warning("Navigation requested for unknown page key '{Key}'.", pageKey);
            return;
        }

        try
        {
            var view = _services.GetRequiredService(viewType);
            CurrentView = view;
            CurrentPageKey = pageKey;
            Navigated?.Invoke(this, view);
            Log.Debug("Navigated to {Key}.", pageKey);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to navigate to page '{Key}'.", pageKey);
        }
    }
}
