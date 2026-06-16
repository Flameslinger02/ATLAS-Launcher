using System.Windows;
using System.Windows.Threading;
using Atlas.Core;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Atlas;

/// <summary>
/// Application entry point. Configures logging, builds the DI host, initializes the database and
/// settings, installs global exception handlers, and shows the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private AppLogService? _appLog;
    private LoggingLevelSwitch? _levelSwitch;

    public App()
    {
        // Earliest possible safety net: catches exceptions thrown during App.xaml InitializeComponent
        // (resource-dictionary loading), which happens before OnStartup and before Serilog is configured.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(AppConstants.LogsDirectory);
                var path = Path.Combine(AppConstants.LogsDirectory, "startup-crash.txt");
                File.WriteAllText(path,
                    $"{DateTime.Now:O}{Environment.NewLine}{(args.ExceptionObject as Exception)?.ToString() ?? "unknown"}");
            }
            catch
            {
                // nothing else we can do this early
            }
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(AppConstants.LogsDirectory);

        // Created before the logger so the in-app log viewer (Console > App Log) shares Serilog's stream.
        _appLog = new AppLogService();
        // Runtime-adjustable minimum level (Settings > Logging). Starts at Debug; overridden from settings
        // once they load. Registered in DI so SettingsViewModel can change it live.
        _levelSwitch = new LoggingLevelSwitch(LogEventLevel.Debug);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .WriteTo.File(
                Path.Combine(AppConstants.LogsDirectory, "atlas-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(_appLog)
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _host = AppHost.Build(_appLog, _levelSwitch);

            // Initialize the DB and load settings + the active profile BEFORE starting hosted services,
            // so the scheduler's StartAsync sees a ready schema and an active profile.
            await _host.Services.GetRequiredService<AtlasDatabase>().InitializeAsync();
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync();

            // Apply the persisted log level and theme now, before any window is shown.
            _levelSwitch.MinimumLevel = ParseLevel(settings.Settings.LogLevel);
            _host.Services.GetRequiredService<IThemeService>().Apply(settings.Settings.Theme);

            // Phase 0 Q3 = No auto-start (server). The default profile is still made the active
            // profile so the shell header and pages have context.
            var profiles = _host.Services.GetRequiredService<IProfileService>();
            var defaultProfile = await profiles.GetDefaultProfileAsync();
            if (defaultProfile is not null) profiles.SetActiveProfile(defaultProfile);

            await _host.StartAsync();

            Log.Information("{App} {Version} started.", AppConstants.AppName,
                typeof(App).Assembly.GetName().Version);

            var window = _host.Services.GetRequiredService<MainWindow>();
            ApplyWindowBounds(window, settings.Settings);
            window.Show();

            // System tray (Phase 0 Q2 = yes). Created after the window exists so it can manage hide/restore.
            _host.Services.GetRequiredService<Core.Services.TrayService>().Initialize(window);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error during startup.");
            // Last-resort fallback ONLY: the themed dialog system depends on a successful startup,
            // which has just failed. A native message box is the safe way to surface this.
            MessageBox.Show($"ATLAS failed to start:\n\n{ex.Message}", "ATLAS — Fatal Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception.");
        try
        {
            _host?.Services.GetService<IDialogService>()
                ?.ShowErrorAsync("Unexpected Error", e.Exception.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to display error dialog for an unhandled exception.");
        }
        e.Handled = true; // never crash silently
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "Unhandled AppDomain exception (terminating={Terminating}).", e.IsTerminating);
    }

    /// <summary>Restores the saved window size/position before the window is shown (position only if on-screen).</summary>
    private static void ApplyWindowBounds(Window window, AppSettings s)
    {
        try
        {
            if (s.LastWindowWidth >= window.MinWidth) window.Width = s.LastWindowWidth;
            if (s.LastWindowHeight >= window.MinHeight) window.Height = s.LastWindowHeight;

            if (s.LastWindowLeft is { } left && s.LastWindowTop is { } top)
            {
                // Clamp the saved position onto the current virtual screen so the title bar is always
                // reachable (handles monitor layout changes / a since-disconnected display).
                var vx = SystemParameters.VirtualScreenLeft;
                var vy = SystemParameters.VirtualScreenTop;
                var vw = SystemParameters.VirtualScreenWidth;
                var vh = SystemParameters.VirtualScreenHeight;
                var w = Math.Min(window.Width, vw);
                var h = Math.Min(window.Height, vh);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = Math.Max(vx, Math.Min(left, vx + vw - w));
                window.Top = Math.Max(vy, Math.Min(top, vy + vh - h));
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Could not restore window bounds."); }
    }

    /// <summary>Captures the current normal-state window bounds into settings for next launch.</summary>
    private void CaptureWindowBounds(AppSettings s)
    {
        try
        {
            var w = MainWindow;
            if (w is null) return;
            var b = w.RestoreBounds; // normal bounds even if minimized/maximized/hidden
            if (b.Width >= 1 && b.Height >= 1)
            {
                s.LastWindowWidth = b.Width;
                s.LastWindowHeight = b.Height;
                s.LastWindowLeft = b.Left;
                s.LastWindowTop = b.Top;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Could not capture window bounds."); }
    }

    private static LogEventLevel ParseLevel(string level) => level switch
    {
        "Verbose" => LogEventLevel.Verbose,
        "Debug" => LogEventLevel.Debug,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        "Fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                var settings = _host.Services.GetService<ISettingsService>();
                if (settings is not null)
                {
                    CaptureWindowBounds(settings.Settings);
                    await settings.SaveAsync();
                }
                _host.Services.GetService<Core.Services.TrayService>()?.Dispose();
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during shutdown.");
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
