using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using Atlas.Data;
using Atlas.Pages.Console;
using Atlas.Pages.Dashboard;
using Atlas.Pages.DiscordBot;
using Atlas.Pages.HeadlessClients;
using Atlas.Pages.ModPresets;
using Atlas.Pages.Mods;
using Atlas.Pages.Profiles;
using Atlas.Pages.Scheduler;
using Atlas.Pages.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;

namespace Atlas.Core;

/// <summary>
/// Builds the application <see cref="IHost"/> and registers all services, view models and pages.
/// Services are added incrementally per phase; this is the Phase 1 registration set.
/// </summary>
public static class AppHost
{
    public static IHost Build(AppLogService appLog, LoggingLevelSwitch levelSwitch)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // ---- Core singletons ----
                // The app-log sink is created before Serilog is configured (App.OnStartup) and shared here
                // so the in-app log viewer reads the same buffer Serilog writes to.
                services.AddSingleton<IAppLogService>(appLog);
                // Shared with App so Settings > Logging can change the live minimum level.
                services.AddSingleton(levelSwitch);
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<AtlasDatabase>();

                // ---- Phase 2: profile system ----
                services.AddSingleton<IProfileService, ProfileService>();

                // ---- Phase 3: mod presets ----
                services.AddSingleton<IModPresetService, ModPresetService>();

                // ---- Phase 4: config generation ----
                services.AddSingleton<IConfigGeneratorService, ConfigGeneratorService>();

                // ---- Phase 5: SteamCMD & mod deployment ----
                services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
                services.AddSingleton<ISteamCmdService, SteamCmdService>();
                services.AddSingleton<IModDeploymentService, ModDeploymentService>();
                services.AddSingleton<IModLibraryService, ModLibraryService>();

                // ---- Phase 6: missions ----
                services.AddSingleton<IMissionService, MissionService>();
                services.AddSingleton<IMissionDependencyChecker, MissionDependencyChecker>();
                services.AddSingleton<ISteamQueryService, SteamQueryService>();
                services.AddSingleton<IRptAnalyzerService, RptAnalyzerService>();
                services.AddSingleton<IBackupService, BackupService>();
                services.AddSingleton<IArmaInstallLocator, ArmaInstallLocator>();

                // ---- Phase 7: server launch & process management ----
                services.AddSingleton<IServerProcessService, ServerProcessService>();
                services.AddSingleton<IHeadlessClientService, HeadlessClientService>();

                // ---- Phase 8: RCON (BattlEye BERCon) ----
                services.AddSingleton<IBattlEyeRconClient, BattlEyeRconClient>();

                // ---- Phase 11: scheduler (hosted background service) ----
                // One instance serves both ISchedulerService (CRUD/UI) and IHostedService (poll loop).
                services.AddSingleton<SchedulerService>();
                services.AddSingleton<ISchedulerService>(sp => sp.GetRequiredService<SchedulerService>());
                services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
                services.AddSingleton<IScheduledTaskEditorLauncher, ScheduledTaskEditorLauncher>();

                // ---- Phase 12: Discord bot (hosted background service) ----
                services.AddSingleton<DiscordBotService>();
                services.AddSingleton<IDiscordBotService>(sp => sp.GetRequiredService<DiscordBotService>());
                services.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());

                // ---- Phase 13: settings, update checker, theme ----
                services.AddSingleton<IUpdateService, UpdateService>();
                services.AddSingleton<IThemeService, ThemeService>();

                // ---- Phase 15: system tray ----
                services.AddSingleton<TrayService>();
                // (IServerBrowserService omitted — Phase 0 Q6 = No.)

                // ---- Shell ----
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();

                // ---- Pages + view models (new instance per navigation) ----
                // Dashboard + Headless Clients VMs are singletons: they hold long-lived subscriptions
                // to the process services, so a per-navigation transient would leak subscriptions
                // (NavigationService does not call OnNavigatedFrom). Their pages stay transient.
                services.AddTransient<DashboardPage>();
                services.AddSingleton<DashboardViewModel>();
                // The standalone Mod Presets page is folded into the global Mods hub; its view model
                // stays registered (composed into ModsViewModel as the hub's Presets tab).
                services.AddTransient<ModPresetsViewModel>();
                services.AddTransient<ModsPage>();
                // Singleton: an in-flight SteamCMD download keeps streaming across navigation (like UpdaterViewModel).
                services.AddSingleton<ModsViewModel>();
                // Profiles: the overview page + the sidebar profile list share ONE ProfilesViewModel
                // (singleton) so they stay in sync. The per-profile editor (workspace) is transient.
                services.AddTransient<ProfilesPage>();
                services.AddSingleton<ProfilesViewModel>();
                services.AddTransient<ProfileWorkspacePage>();
                services.AddTransient<ProfileWorkspaceViewModel>();
                services.AddTransient<HeadlessClientsPage>();
                services.AddSingleton<HeadlessClientsViewModel>();
                services.AddTransient<DiscordBotPage>();
                // Singleton: subscribes to IDiscordBotService.StateChanged for the page's lifetime (no-leak rule).
                services.AddSingleton<DiscordBotViewModel>();
                services.AddTransient<SchedulerPage>();
                // Singleton: subscribes to ISchedulerService.TasksChanged for the page's lifetime (no-leak rule).
                services.AddSingleton<SchedulerViewModel>();
                services.AddTransient<ConsolePage>();
                // Singletons: hold long-lived subscriptions (server-log / app-log) — same no-leak rule as Dashboard.
                services.AddSingleton<ConsoleViewModel>();
                services.AddSingleton<AppLogViewModel>();
                // Updater (Arma + ATLAS) is composed into the Console page; singleton so an in-flight update
                // keeps streaming across navigation.
                services.AddSingleton<UpdaterViewModel>();
                // RCON page split out of Console; its VM is a singleton (long-lived RCON subscriptions + poll timer).
                services.AddTransient<RconPage>();
                services.AddSingleton<RconViewModel>();
                services.AddTransient<SettingsPage>();
                services.AddTransient<SettingsViewModel>();
            })
            .Build();
    }
}
