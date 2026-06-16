using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Atlas.Core.Models;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace Atlas.Core.Services;

/// <summary>
/// Owns the system-tray icon (Phase 15): tooltip, context menu, balloon notifications, hide-to-tray on
/// window close, and double-click-to-restore. Created once after the main window is shown.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly IServerProcessService _server;
    private readonly IBattlEyeRconClient _rcon;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;

    private TaskbarIcon? _tray;
    private Window? _window;
    private MenuItem? _startItem, _stopItem, _restartItem;
    private IDisposable? _stateSub, _playersSub;

    private ServerState _state = ServerState.Stopped;
    private int _playerCount;
    private bool _exiting;
    private bool _disposed;

    public TrayService(IServerProcessService server, IBattlEyeRconClient rcon, IProfileService profiles,
        ISettingsService settings)
    {
        _server = server;
        _rcon = rcon;
        _profiles = profiles;
        _settings = settings;
    }

    /// <summary>Builds the tray icon and wires it to <paramref name="window"/>. Call once, on the UI thread.</summary>
    public void Initialize(Window window)
    {
        if (_tray is not null || _disposed) return; // initialize exactly once
        _window = window;

        _tray = new TaskbarIcon { ToolTipText = "ATLAS" };
        TryLoadIcon();
        _tray.TrayMouseDoubleClick += (_, _) => ShowWindow();
        _tray.ContextMenu = BuildMenu();

        _state = _server.CurrentState;
        _stateSub = _server.StateChanged.Subscribe(OnStateChanged);
        _playersSub = _rcon.PlayersUpdated.Subscribe(p => OnUi(() =>
        {
            _playerCount = p?.Count ?? 0;
            UpdateTooltip();
        }));

        _window.Closing += OnWindowClosing;
        UpdateTooltip();
        UpdateMenuState();
    }

    private void TryLoadIcon()
    {
        try
        {
            _tray!.IconSource = new BitmapImage(
                new Uri("pack://application:,,,/Resources/atlas_icon.ico", UriKind.Absolute));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the tray icon image.");
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show ATLAS" };
        show.Click += (_, _) => ShowWindow();
        menu.Items.Add(show);
        menu.Items.Add(new Separator());

        _startItem = new MenuItem { Header = "Start Server" };
        _startItem.Click += (_, _) => SafeFire(StartAsync);
        menu.Items.Add(_startItem);

        _stopItem = new MenuItem { Header = "Stop Server" };
        _stopItem.Click += (_, _) => SafeFire(() => _server.StopAsync());
        menu.Items.Add(_stopItem);

        _restartItem = new MenuItem { Header = "Restart Server" };
        _restartItem.Click += (_, _) => SafeFire(() => _server.RestartAsync());
        menu.Items.Add(_restartItem);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        return menu;
    }

    private async Task StartAsync()
    {
        var profile = _profiles.ActiveProfile;
        if (profile is null)
        {
            ShowBalloon("ATLAS", "No active profile to start.", BalloonIcon.Warning);
            return;
        }
        await _server.LaunchAsync(profile);
    }

    private void OnStateChanged(ServerState state) => OnUi(() =>
    {
        var previous = _state;
        _state = state;
        UpdateTooltip();
        UpdateMenuState();

        if (state == ServerState.Crashed)
            ShowBalloon("Server Crashed", "The Arma 3 server stopped unexpectedly.", BalloonIcon.Error);
        else if (state == ServerState.Running && previous == ServerState.Starting)
            ShowBalloon("Server Running", $"{ProfileName()} is now running.", BalloonIcon.Info);
    });

    private void UpdateTooltip()
    {
        if (_tray is null) return;
        // "[ServerName] — [State] — [N] players"
        _tray.ToolTipText = $"{ProfileName()} — {_state} — {_playerCount} player{(_playerCount == 1 ? "" : "s")}";
    }

    private void UpdateMenuState()
    {
        if (_startItem is null) return;
        _startItem.IsEnabled = (_state is ServerState.Stopped or ServerState.Crashed) && _profiles.ActiveProfile is not null;
        _stopItem!.IsEnabled = _state is ServerState.Running or ServerState.Starting;
        _restartItem!.IsEnabled = _state is ServerState.Running or ServerState.Starting or ServerState.Crashed;
    }

    private string ProfileName() => _server.ActiveProfileName
        ?? _profiles.ActiveProfile?.Name ?? AppConstants.AppName;

    // ----------------------------------------------------------------- window behavior

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Hide to tray instead of exiting, unless the user chose Exit or disabled the setting.
        if (_exiting || !_settings.Settings.MinimizeToTray) return;
        e.Cancel = true;
        _window?.Hide();
        ShowBalloon("ATLAS", "Still running in the tray. Right-click the icon for options.", BalloonIcon.Info);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Application.Current?.Shutdown();
    }

    // ----------------------------------------------------------------- helpers

    private void ShowBalloon(string title, string message, BalloonIcon icon)
    {
        try { _tray?.ShowBalloonTip(title, message, icon); }
        catch (Exception ex) { Log.Debug(ex, "Tray balloon failed."); }
    }

    private void SafeFire(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { Log.Error(ex, "Tray action failed."); }
        });
    }

    private void OnUi(Action action)
    {
        // Guard so a callback already queued when Dispose() runs becomes a no-op (no touching a disposed tray).
        void Guarded() { if (!_disposed) action(); }
        var d = _tray?.Dispatcher ?? Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Guarded();
        else d.InvokeAsync(Guarded);
    }

    public void Dispose()
    {
        if (_disposed) return;   // idempotent: App.OnExit disposes explicitly AND the host disposes the singleton
        _disposed = true;
        try { _stateSub?.Dispose(); } catch { /* ignore */ }
        try { _playersSub?.Dispose(); } catch { /* ignore */ }
        if (_window is not null) { try { _window.Closing -= OnWindowClosing; } catch { /* ignore */ } }
        try { _tray?.Dispose(); } catch { /* ignore */ }
    }
}
