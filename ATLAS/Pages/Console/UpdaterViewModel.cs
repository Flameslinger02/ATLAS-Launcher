using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Console;

/// <summary>
/// The Console page's "Updates" tab: install/update the Arma 3 dedicated server via SteamCMD (with live
/// console output) and update ATLAS itself (download the latest portable exe, then swap + relaunch). Both
/// reuse existing plumbing — <see cref="ISteamCmdService.UpdateServerAsync"/> and <see cref="IUpdateService"/>.
/// </summary>
public partial class UpdaterViewModel : BaseViewModel
{
    private readonly ISteamCmdService _steam;
    private readonly IArmaInstallLocator _arma;
    private readonly ISettingsService _settings;
    private readonly IUpdateService _updates;
    private readonly IDialogService _dialogs;

    private const int MaxOutputLines = 1000;
    private CancellationTokenSource? _armaCts;

    // ----- Arma 3 server -----
    [ObservableProperty] private string _armaServerDirectory = string.Empty;
    [ObservableProperty] private bool _useProfilingBranch;
    [ObservableProperty] private bool _isUpdatingArma;

    public ObservableCollection<string> ArmaOutput { get; } = new();

    /// <summary>The auto-detected server directory, shown as the textbox watermark when nothing is saved.</summary>
    public string DetectedArmaDirectory { get; }

    // ----- ATLAS launcher -----
    [ObservableProperty] private string _latestVersionText = "—";
    [ObservableProperty] private string _atlasStatus = string.Empty;
    [ObservableProperty] private bool _isUpdatingAtlas;
    [ObservableProperty] private double _atlasProgress;   // 0..1

    public string CurrentVersionText { get; }

    public UpdaterViewModel(ISteamCmdService steam, IArmaInstallLocator arma, ISettingsService settings,
        IUpdateService updates, IDialogService dialogs)
    {
        _steam = steam;
        _arma = arma;
        _settings = settings;
        _updates = updates;
        _dialogs = dialogs;
        Title = "Updates";

        DetectedArmaDirectory = _arma.FindServerDirectory() ?? string.Empty;
        ArmaServerDirectory = string.IsNullOrWhiteSpace(_settings.Settings.ArmaServerDirectory)
            ? DetectedArmaDirectory
            : _settings.Settings.ArmaServerDirectory;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        CurrentVersionText = v is null ? "v0.0.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
        if (!string.IsNullOrWhiteSpace(_settings.Settings.LastKnownLatestVersion))
            LatestVersionText = $"v{_settings.Settings.LastKnownLatestVersion}";
    }

    // ----------------------------------------------------------------- Arma 3 server

    [RelayCommand]
    private async Task BrowseArmaDir()
    {
        var dir = await _dialogs.BrowseFolderAsync("Select the Arma 3 server install directory", ArmaServerDirectory);
        if (!string.IsNullOrWhiteSpace(dir)) ArmaServerDirectory = dir;
    }

    private bool CanUpdateArma => !IsUpdatingArma;

    [RelayCommand(CanExecute = nameof(CanUpdateArma))]
    private async Task UpdateArma()
    {
        if (!await _steam.IsSteamCmdAvailableAsync())
        {
            await _dialogs.ShowErrorAsync("SteamCMD not set up",
                "SteamCMD isn't installed yet. Set it up under Settings → SteamCMD (Download SteamCMD), then try again.");
            return;
        }

        var dir = ArmaServerDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(dir)) dir = _arma.FindServerDirectory();
        if (string.IsNullOrWhiteSpace(dir))
        {
            await _dialogs.ShowErrorAsync("Install directory",
                "Choose where to install/update the Arma 3 server (use Browse), or set it in Settings.");
            return;
        }

        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Install directory", $"Couldn't use that directory:\n\n{ex.Message}");
            return;
        }

        // Remember the chosen directory so the next run (and the Settings page) agree.
        ArmaServerDirectory = dir;
        _settings.Settings.ArmaServerDirectory = dir;
        await _settings.SaveAsync();

        _armaCts = new CancellationTokenSource();
        IsUpdatingArma = true;
        ArmaOutput.Clear();
        AppendArma($"Installing / updating Arma 3 server into:\r\n  {dir}");
        AppendArma(UseProfilingBranch ? "Branch: profiling" : "Branch: release");

        var progress = new Progress<string>(line => OnUi(() => AppendArma(line)));
        try
        {
            await _steam.UpdateServerAsync(dir, UseProfilingBranch, progress, _armaCts.Token);
            AppendArma("✔ Server install/update finished.");
        }
        catch (OperationCanceledException)
        {
            AppendArma("✖ Cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Arma server update failed.");
            AppendArma($"✖ Failed: {ex.Message}");
            await _dialogs.ShowErrorAsync("Server update failed", ex.Message);
        }
        finally
        {
            IsUpdatingArma = false;
            _armaCts.Dispose();
            _armaCts = null;
        }
    }

    private bool CanCancelArma => IsUpdatingArma;

    [RelayCommand(CanExecute = nameof(CanCancelArma))]
    private void CancelArma()
    {
        _armaCts?.Cancel();
        AppendArma("Cancelling…");
    }

    private void AppendArma(string line)
    {
        foreach (var part in line.Replace("\r\n", "\n").Split('\n'))
            ArmaOutput.Add(part);
        while (ArmaOutput.Count > MaxOutputLines) ArmaOutput.RemoveAt(0);
    }

    // ----------------------------------------------------------------- ATLAS launcher

    private bool CanUpdateAtlas => !IsUpdatingAtlas;

    [RelayCommand(CanExecute = nameof(CanUpdateAtlas))]
    private async Task UpdateAtlas()
    {
        IsUpdatingAtlas = true;
        AtlasProgress = 0;
        AtlasStatus = "Checking for updates…";
        try
        {
            var result = await _updates.CheckForUpdateAsync();
            if (!string.IsNullOrWhiteSpace(result.LatestVersion)) LatestVersionText = $"v{result.LatestVersion}";

            if (result.Error is not null)
            {
                AtlasStatus = "Update check failed.";
                await _dialogs.ShowErrorAsync("Update check", $"Could not check for updates:\n\n{result.Error}");
                return;
            }
            if (!result.UpdateAvailable)
            {
                AtlasStatus = $"Up to date ({CurrentVersionText}).";
                await _dialogs.ShowInfoAsync("Up to date", $"You're running the latest version ({CurrentVersionText}).");
                return;
            }
            if (string.IsNullOrWhiteSpace(result.AssetDownloadUrl))
            {
                // A newer release exists but has no portable exe asset to swap in — fall back to the browser.
                AtlasStatus = $"v{result.LatestVersion} available (no portable asset).";
                if (await _dialogs.ConfirmAsync("Update available",
                        $"v{result.LatestVersion} is available but has no downloadable ATLAS.exe. Open the release page?",
                        "Open", "Close") && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                    _updates.OpenReleasePage(result.ReleaseUrl!);
                return;
            }

            var notes = string.IsNullOrWhiteSpace(result.ReleaseNotesSummary)
                ? string.Empty : $"\n\n{result.ReleaseNotesSummary}";
            if (!await _dialogs.ConfirmAsync("Update ATLAS",
                    $"Update to v{result.LatestVersion}? (You have {CurrentVersionText}.)\n\n" +
                    $"ATLAS will download the new version, then close and relaunch automatically.{notes}",
                    "Update & Restart", "Cancel"))
            {
                AtlasStatus = $"v{result.LatestVersion} available.";
                return;
            }

            AtlasStatus = "Downloading…";
            var progress = new Progress<double>(p => OnUi(() => AtlasProgress = p));
            var path = await _updates.DownloadUpdateAsync(result, progress);

            AtlasStatus = "Restarting to apply…";
            _updates.ApplyUpdateAndRestart(path);   // launches the swap helper, then shuts the app down
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ATLAS self-update failed.");
            AtlasStatus = "Update failed.";
            await _dialogs.ShowErrorAsync("Update ATLAS", ex.Message);
        }
        finally
        {
            IsUpdatingAtlas = false;
        }
    }

    // ----------------------------------------------------------------- helpers

    partial void OnIsUpdatingArmaChanged(bool value)
    {
        UpdateArmaCommand.NotifyCanExecuteChanged();
        CancelArmaCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsUpdatingAtlasChanged(bool value) => UpdateAtlasCommand.NotifyCanExecuteChanged();

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
