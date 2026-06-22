using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using Atlas.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Atlas.Pages.Settings;

/// <summary>
/// Full Settings module (Phase 13): SteamCMD, Steam credentials, Steam API key (DPAPI), mod staging,
/// startup, update checker, GitHub, logging, theme, about, and a danger zone.
/// Secrets are entered via a PasswordBox in the code-behind and never bound to a property.
/// </summary>
public partial class SettingsViewModel : BaseViewModel
{
    private readonly ISettingsService _settings;
    private readonly ISecretProtector _secrets;
    private readonly IDialogService _dialogs;
    private readonly IUpdateService _updates;
    private readonly IThemeService _theme;
    private readonly ISteamCmdService _steam;
    private readonly IArmaInstallLocator _arma;
    private readonly AtlasDatabase _db;
    private readonly LoggingLevelSwitch _levelSwitch;

    private CancellationTokenSource? _busyCts;

    // ----- SteamCMD / Steam -----
    [ObservableProperty] private string _steamCmdPath = string.Empty;
    [ObservableProperty] private string _modStagingDirectory = string.Empty;
    [ObservableProperty] private string _armaServerDirectory = string.Empty;
    [ObservableProperty] private string _steamUsername = string.Empty;
    [ObservableProperty] private bool _rememberSteamCredentials;
    [ObservableProperty] private bool _hasSavedApiKey;

    // ----- Startup -----
    [ObservableProperty] private bool _autoStartServerOnLaunch;
    [ObservableProperty] private bool _minimizeToTray;

    // ----- Updates -----
    [ObservableProperty] private bool _checkUpdatesOnStartup;
    [ObservableProperty] private string _gitHubOwner = string.Empty;
    [ObservableProperty] private string _gitHubRepo = string.Empty;
    [ObservableProperty] private string _lastUpdateText = "Never checked.";

    // ----- Logging -----
    [ObservableProperty] private string _logLevel = "Information";

    // ----- Theme -----
    [ObservableProperty] private bool _isLightTheme;

    // ----- Danger zone -----
    [ObservableProperty] private bool _wipeAllData;

    // ----- Shell -----
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyStatus = string.Empty;
    [ObservableProperty] private int _busyPercent;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<string> LogLevels { get; } = new()
    {
        "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"
    };

    public string VersionText { get; }
    public string BuildDateText { get; }
    public string GitHubUrl => $"https://github.com/{GitHubOwnerOrDefault}/{GitHubRepoOrDefault}";
    public string LicenseText => "ATLAS — MIT License. © 2026.";

    private string GitHubOwnerOrDefault =>
        string.IsNullOrWhiteSpace(GitHubOwner) ? AppConstants.GitHubOwner : GitHubOwner.Trim();
    private string GitHubRepoOrDefault =>
        string.IsNullOrWhiteSpace(GitHubRepo) ? AppConstants.GitHubRepo : GitHubRepo.Trim();

    public SettingsViewModel(ISettingsService settings, ISecretProtector secrets, IDialogService dialogs,
        IUpdateService updates, IThemeService theme, ISteamCmdService steam, IArmaInstallLocator arma,
        AtlasDatabase db, LoggingLevelSwitch levelSwitch)
    {
        _settings = settings;
        _secrets = secrets;
        _dialogs = dialogs;
        _updates = updates;
        _theme = theme;
        _steam = steam;
        _arma = arma;
        _db = db;
        _levelSwitch = levelSwitch;
        Title = "Settings";

        var version = typeof(SettingsViewModel).Assembly.GetName().Version;
        VersionText = version is null ? "v1.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        BuildDateText = ResolveBuildDate();

        SeedFromSettings();
    }

    private void SeedFromSettings()
    {
        var s = _settings.Settings;
        SteamCmdPath = s.SteamCmdPath;
        ModStagingDirectory = s.ModStagingDirectory;
        // Show the saved Arma path; if none saved yet, auto-detect from Steam (blank if not found).
        ArmaServerDirectory = string.IsNullOrWhiteSpace(s.ArmaServerDirectory)
            ? (_arma.FindServerDirectory() ?? string.Empty)
            : s.ArmaServerDirectory;
        SteamUsername = s.SteamUsername;
        RememberSteamCredentials = s.RememberSteamCredentials;
        HasSavedApiKey = !string.IsNullOrWhiteSpace(s.SteamApiKeyEncrypted);
        AutoStartServerOnLaunch = s.AutoStartServerOnLaunch;
        MinimizeToTray = s.MinimizeToTray;
        CheckUpdatesOnStartup = s.CheckUpdatesOnStartup;
        GitHubOwner = s.GitHubOwner;
        GitHubRepo = s.GitHubRepo;
        LogLevel = s.LogLevel;
        IsLightTheme = string.Equals(s.Theme, ThemeService.Light, StringComparison.OrdinalIgnoreCase);
        UpdateLastCheckedText();
    }

    private void UpdateLastCheckedText()
    {
        var s = _settings.Settings;
        if (s.LastUpdateCheck is { } when)
        {
            var ver = string.IsNullOrWhiteSpace(s.LastKnownLatestVersion) ? "unknown" : s.LastKnownLatestVersion;
            LastUpdateText = $"Last checked {when.ToLocalTime():g} — latest seen: v{ver}.";
        }
        else
        {
            LastUpdateText = "Never checked.";
        }
    }

    // ----- Theme: apply + persist immediately for instant feedback -----
    partial void OnIsLightThemeChanged(bool value)
    {
        var theme = value ? ThemeService.Light : ThemeService.Dark;
        _theme.Apply(theme);
        _settings.Settings.Theme = theme;
        _ = _settings.SaveAsync();
    }

    // ----------------------------------------------------------------- SteamCMD

    [RelayCommand]
    private async Task BrowseSteamCmd()
    {
        var path = await _dialogs.BrowseFileAsync("Locate steamcmd.exe", "SteamCMD|steamcmd.exe|Executable|*.exe");
        if (!string.IsNullOrWhiteSpace(path)) SteamCmdPath = path;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DownloadSteamCmd()
    {
        await RunBusyAsync("Downloading SteamCMD…", async ct =>
        {
            var progress = new Progress<(string message, int percent)>(p =>
                OnUi(() => { BusyStatus = p.message; BusyPercent = p.percent; }));
            await _steam.DownloadSteamCmdAsync(progress, ct);
            OnUi(() => SteamCmdPath = _settings.Settings.SteamCmdPath);
            StatusMessage = "SteamCMD installed.";
        });
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task TestSteamCmd()
    {
        await RunBusyAsync("Testing SteamCMD…", async ct =>
        {
            // Persist the path first so the test uses the field value.
            _settings.Settings.SteamCmdPath = SteamCmdPath?.Trim() ?? string.Empty;
            await _settings.SaveAsync();
            var (ok, message) = await _steam.TestSteamCmdAsync(ct);
            await _dialogs.ShowInfoAsync(ok ? "SteamCMD OK" : "SteamCMD", message);
            StatusMessage = message;
        });
    }

    [RelayCommand]
    private async Task ClearSavedCredentials()
    {
        if (!await _dialogs.ConfirmAsync("Clear Saved Credentials",
                "Remove the saved Steam username and SteamCMD's cached login token?")) return;
        _steam.ClearSavedCredentials();
        SteamUsername = string.Empty;
        StatusMessage = "Saved Steam credentials cleared.";
    }

    [RelayCommand]
    private async Task LogInToSteam()
    {
        if (!await _steam.IsSteamCmdAvailableAsync())
        {
            await _dialogs.ShowErrorAsync("SteamCMD not set up",
                "Install SteamCMD first (Download SteamCMD above), then log in.");
            return;
        }
        var prefill = string.IsNullOrWhiteSpace(SteamUsername) ? (_steam.GetSavedUsername() ?? string.Empty) : SteamUsername;
        var user = await _dialogs.PromptAsync("Log in to Steam", "Steam username", prefill);
        if (string.IsNullOrWhiteSpace(user)) return;
        var pass = await _dialogs.PromptAsync("Log in to Steam",
            "Steam password (used once to log in — never stored by ATLAS).", string.Empty, isPassword: true);
        if (string.IsNullOrWhiteSpace(pass)) return;

        await RunBusyAsync("Logging in to Steam…", async ct =>
        {
            var progress = new Progress<string>(line => OnUi(() => BusyStatus = line));
            Func<CancellationToken, Task<string?>> guard = _ =>
                _dialogs.PromptAsync("Steam Guard",
                    "Enter the Steam Guard code (from your email or the Steam mobile app).", string.Empty);
            var ok = await _steam.LoginAsync(user.Trim(), pass, progress, ct, guard);
            OnUi(() => SteamUsername = _steam.GetSavedUsername() ?? user.Trim());
            await _dialogs.ShowInfoAsync("Steam login",
                ok ? "Logged in to Steam. Mod downloads and updates won't prompt again."
                   : "Login didn't complete. Check your username, password, and Steam Guard code, then try again.");
            StatusMessage = ok ? "Logged in to Steam." : "Steam login didn't complete.";
        });
    }

    // ----------------------------------------------------------------- mod staging

    [RelayCommand]
    private async Task BrowseModStaging()
    {
        var dir = await _dialogs.BrowseFolderAsync("Select mod staging directory", ModStagingDirectory);
        if (!string.IsNullOrWhiteSpace(dir)) ModStagingDirectory = dir;
    }

    // ----------------------------------------------------------------- Arma install

    [RelayCommand]
    private void DetectArmaServer()
    {
        var dir = _arma.FindServerDirectory();
        if (string.IsNullOrWhiteSpace(dir))
        {
            StatusMessage = "No Arma 3 server install was found in your Steam libraries.";
            return;
        }
        ArmaServerDirectory = dir;
        StatusMessage = "Detected Arma 3 server install.";
    }

    [RelayCommand]
    private async Task BrowseArmaServer()
    {
        var dir = await _dialogs.BrowseFolderAsync("Select the Arma 3 server install directory", ArmaServerDirectory);
        if (!string.IsNullOrWhiteSpace(dir)) ArmaServerDirectory = dir;
    }

    // ----------------------------------------------------------------- Steam API key (code-behind supplies the value)

    /// <summary>Tests the entered API key (or, if blank, the saved one). Called from the code-behind.</summary>
    public async Task TestApiKeyAsync(string? enteredKey)
    {
        var key = !string.IsNullOrWhiteSpace(enteredKey)
            ? enteredKey.Trim()
            : _secrets.Decrypt(_settings.Settings.SteamApiKeyEncrypted);
        if (string.IsNullOrWhiteSpace(key))
        {
            await _dialogs.ShowErrorAsync("Steam API Key", "Enter an API key to test (or save one first).");
            return;
        }

        await RunBusyAsync("Testing Steam API key…", async ct =>
        {
            var ok = await _steam.TestApiKeyAsync(key, ct);
            await _dialogs.ShowInfoAsync(ok ? "API Key OK" : "API Key",
                ok ? "Steam accepted the API key." : "Steam rejected the API key (or the request failed).");
            StatusMessage = ok ? "Steam API key is valid." : "Steam API key is invalid.";
        });
    }

    /// <summary>Clears the stored Steam API key.</summary>
    [RelayCommand]
    private async Task ClearApiKey()
    {
        if (!await _dialogs.ConfirmAsync("Clear API Key", "Remove the stored Steam Web API key?")) return;
        _settings.Settings.SteamApiKeyEncrypted = string.Empty;
        await _settings.SaveAsync();
        HasSavedApiKey = false;
        StatusMessage = "Steam API key cleared.";
    }

    // ----------------------------------------------------------------- updates

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckForUpdatesNow()
    {
        await RunBusyAsync("Checking for updates…", async ct =>
        {
            var result = await _updates.CheckForUpdateAsync(ct);
            OnUi(UpdateLastCheckedText);
            if (result.Error is not null)
            {
                await _dialogs.ShowErrorAsync("Update Check", $"Could not check for updates:\n\n{result.Error}");
                StatusMessage = "Update check failed.";
            }
            else if (result.UpdateAvailable)
            {
                var notes = string.IsNullOrWhiteSpace(result.ReleaseNotesSummary)
                    ? string.Empty : $"\n\n{result.ReleaseNotesSummary}";

                if (string.IsNullOrWhiteSpace(result.AssetDownloadUrl))
                {
                    // Newer release exists but has no portable ATLAS.exe asset to swap — offer the browser.
                    if (await _dialogs.ConfirmAsync("Update Available",
                            $"A newer version is available: v{result.LatestVersion} (you have v{result.CurrentVersion}).{notes}",
                            "View Release", "Close") && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                        _updates.OpenReleasePage(result.ReleaseUrl!);
                    StatusMessage = $"Update available: v{result.LatestVersion}.";
                }
                else if (await _dialogs.ConfirmAsync("Update Available",
                        $"A newer version is available: v{result.LatestVersion} (you have v{result.CurrentVersion}).\n\n" +
                        $"ATLAS will download the update, then close and relaunch automatically.{notes}",
                        "Update & Restart", "Later"))
                {
                    BusyStatus = "Downloading update…";
                    var dl = new Progress<double>(p => OnUi(() => BusyPercent = (int)Math.Round(p * 100)));
                    var path = await _updates.DownloadUpdateAsync(result, dl, ct);
                    BusyStatus = "Restarting to apply…";
                    _updates.ApplyUpdateAndRestart(path);   // launches the swap helper, then shuts the app down
                }
                else
                {
                    StatusMessage = $"Update available: v{result.LatestVersion}.";
                }
            }
            else
            {
                await _dialogs.ShowInfoAsync("Up to Date",
                    $"You're running the latest version (v{result.CurrentVersion}).");
                StatusMessage = "ATLAS is up to date.";
            }
        });
    }

    // ----------------------------------------------------------------- logging / folders

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppConstants.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppConstants.AppDataRoot);

    [RelayCommand]
    private async Task ClearOldLogs()
    {
        if (!await _dialogs.ConfirmAsync("Clear Old Logs", "Delete ATLAS log files older than 30 days?")) return;
        try
        {
            var cutoff = DateTime.Now.AddDays(-30);
            var deleted = 0;
            if (Directory.Exists(AppConstants.LogsDirectory))
            {
                // Serilog holds today's rolling file open — never target it, even if its timestamp looks old.
                var activeLog = $"atlas-{DateTime.Now:yyyyMMdd}.log";
                foreach (var file in Directory.EnumerateFiles(AppConstants.LogsDirectory, "atlas-*.log"))
                {
                    if (string.Equals(Path.GetFileName(file), activeLog, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff) { File.Delete(file); deleted++; }
                    }
                    catch (Exception ex) { Log.Warning(ex, "Could not delete log file {File}.", file); }
                }
            }
            StatusMessage = $"Deleted {deleted} old log file(s).";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear old logs.");
            await _dialogs.ShowErrorAsync("Clear Old Logs", ex.Message);
        }
    }

    // ----------------------------------------------------------------- about

    [RelayCommand]
    private void OpenGitHub() => _updates.OpenReleasePage(GitHubUrl);

    // ----------------------------------------------------------------- save / persist

    /// <summary>Persists all editable fields. <paramref name="enteredApiKey"/> (from the PasswordBox) is
    /// DPAPI-encrypted and stored only when non-empty. Called from the code-behind Save button.</summary>
    public async Task ApplyAndSaveAsync(string? enteredApiKey)
    {
        var s = _settings.Settings;
        s.SteamCmdPath = SteamCmdPath?.Trim() ?? string.Empty;
        s.ModStagingDirectory = ModStagingDirectory?.Trim() ?? string.Empty;
        s.ArmaServerDirectory = ArmaServerDirectory?.Trim() ?? string.Empty;
        s.SteamUsername = SteamUsername?.Trim() ?? string.Empty;
        s.RememberSteamCredentials = RememberSteamCredentials;
        s.AutoStartServerOnLaunch = AutoStartServerOnLaunch;
        s.MinimizeToTray = MinimizeToTray;
        s.CheckUpdatesOnStartup = CheckUpdatesOnStartup;
        s.GitHubOwner = GitHubOwner?.Trim() ?? string.Empty;
        s.GitHubRepo = GitHubRepo?.Trim() ?? string.Empty;
        s.LogLevel = LogLevel;
        s.Theme = IsLightTheme ? ThemeService.Light : ThemeService.Dark;

        if (!string.IsNullOrWhiteSpace(enteredApiKey))
        {
            s.SteamApiKeyEncrypted = _secrets.Encrypt(enteredApiKey.Trim());   // DPAPI; never plaintext
            HasSavedApiKey = true;
        }

        // Apply the log level to the live logger immediately.
        _levelSwitch.MinimumLevel = ParseLevel(LogLevel);

        await _settings.SaveAsync();
        OnPropertyChanged(nameof(GitHubUrl));
        StatusMessage = "Settings saved.";
    }

    // ----------------------------------------------------------------- danger zone

    [RelayCommand]
    private async Task ResetAllSettings()
    {
        if (!await _dialogs.ConfirmAsync("Reset All Settings",
                "Reset ALL settings to defaults? This also clears the stored Steam API key and Discord token. " +
                "This cannot be undone.", "Reset", "Cancel")) return;

        await _settings.ResetAsync();
        SeedFromSettings();
        // Re-apply the (now default) theme + log level so the live app matches the reset state.
        _theme.Apply(_settings.Settings.Theme);
        _levelSwitch.MinimumLevel = ParseLevel(_settings.Settings.LogLevel);
        StatusMessage = "All settings reset to defaults.";
    }

    [RelayCommand]
    private async Task ClearDatabase()
    {
        var typed = await _dialogs.PromptWithValidationAsync("Clear Entire Database",
            "This permanently deletes ALL profiles, mods, presets, schedules, bans, and history. " +
            "Type DELETE to confirm.",
            v => string.Equals(v?.Trim(), "DELETE", StringComparison.Ordinal) ? null : "Type DELETE to proceed.");
        if (typed is null) return; // cancelled

        try
        {
            await _db.ClearAllDataAsync();
            await _dialogs.ShowInfoAsync("Database Cleared",
                "All data was deleted. Restart ATLAS to recreate default state.");
            StatusMessage = "Database cleared.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear database.");
            await _dialogs.ShowErrorAsync("Clear Database", ex.Message);
        }
    }

    /// <summary>
    /// Removes ATLAS's files. Always clears the regenerable app files (SteamCMD download, headless profiles,
    /// logs); when <see cref="WipeAllData"/> is set, also deletes settings.json + the profiles database (a full
    /// wipe). The running ATLAS.exe can't delete itself, so a detached helper removes it after the app exits.
    /// Any missing files or delete errors are collected and shown before closing.
    /// </summary>
    [RelayCommand]
    private async Task Uninstall()
    {
        var wipe = WipeAllData;
        var scope = wipe
            ? "This deletes ALL ATLAS files — SteamCMD download, headless profiles, logs, your settings " +
              "(including saved Steam/Discord keys) and the profiles database — and removes ATLAS.exe, then closes."
            : "This removes ATLAS.exe plus regenerable files (SteamCMD download, headless profiles, logs). " +
              "Your settings and profiles database are kept. ATLAS then closes.";
        var typed = await _dialogs.PromptWithValidationAsync("Uninstall ATLAS",
            scope + "\n\nType UNINSTALL to confirm.",
            v => string.Equals(v?.Trim(), "UNINSTALL", StringComparison.Ordinal) ? null : "Type UNINSTALL to proceed.");
        if (typed is null) return;

        var report = new List<string>();
        void Del(string path, bool isDir)
        {
            try
            {
                if (isDir)
                {
                    if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); report.Add($"Deleted    {path}"); }
                    else report.Add($"Not found  {path}");
                }
                else
                {
                    if (File.Exists(path)) { File.Delete(path); report.Add($"Deleted    {path}"); }
                    else report.Add($"Not found  {path}");
                }
            }
            catch (Exception ex) { report.Add($"ERROR      {path} — {ex.Message}"); }
        }

        // Regenerable app files — always.
        Del(AppConstants.SteamCmdDirectory, true);
        Del(AppConstants.HeadlessClientProfilesDirectory, true);

        if (wipe)
        {
            // Release pooled SQLite handles so the database file can be removed.
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best effort */ }
            Del(AppConstants.DatabasePath, false);
            Del(AppConstants.DatabasePath + "-wal", false);
            Del(AppConstants.DatabasePath + "-shm", false);
            Del(AppConstants.SettingsPath, false);
        }

        // Logs last: Serilog holds today's file open, so flush + close the logger before removing the folder.
        Log.Information("Uninstall requested (wipeData={Wipe}); closing logger to remove the Logs folder.", wipe);
        Serilog.Log.CloseAndFlush();
        Del(AppConstants.LogsDirectory, true);
        if (wipe) Del(AppConstants.AppDataRoot, true);   // remove any remaining/empty app-data root

        // The running exe can't delete itself — a detached helper does it once we've exited.
        var exe = Environment.ProcessPath;
        report.Add(string.IsNullOrWhiteSpace(exe)
            ? "Skipped    ATLAS.exe (path could not be resolved — delete it manually)"
            : $"On close   {exe} (removed by helper after exit)");

        var summary = "Uninstall results:\n\n" + string.Join("\n", report) +
                      "\n\nATLAS will now close" + (string.IsNullOrWhiteSpace(exe) ? "." : " and remove its program file.");
        await _dialogs.ShowInfoAsync("Uninstall ATLAS", summary);

        LaunchUninstallHelper(exe, wipe);
        OnUi(() => Application.Current?.Shutdown());
    }

    private static void LaunchUninstallHelper(string? exePath, bool wipeData)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ATLAS_uninstall");
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "uninstall.ps1");
            File.WriteAllText(script, UninstallHelperScript);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\" " +
                            $"-TargetPid {Environment.ProcessId} -Exe \"{exePath}\" " +
                            $"-AppData \"{AppConstants.AppDataRoot}\" -WipeData {(wipeData ? 1 : 0)}",
            };
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to launch the uninstall helper."); }
    }

    /// <summary>Detached helper: waits for ATLAS to exit, then (optionally) force-removes the app-data folder as
    /// a backstop and deletes ATLAS.exe, retrying past the file lock, then self-deletes.</summary>
    private const string UninstallHelperScript = """
        param([int]$TargetPid, [string]$Exe, [string]$AppData, [int]$WipeData)
        try { Wait-Process -Id $TargetPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Milliseconds 500
        if ($WipeData -eq 1 -and $AppData -and (Test-Path -LiteralPath $AppData)) {
            try { Remove-Item -LiteralPath $AppData -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        }
        if ($Exe) {
            for ($i = 0; $i -lt 20; $i++) {
                if (-not (Test-Path -LiteralPath $Exe)) { break }
                try { Remove-Item -LiteralPath $Exe -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 }
            }
        }
        try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch {}
        """;

    // ----------------------------------------------------------------- helpers

    private bool NotBusy() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        DownloadSteamCmdCommand.NotifyCanExecuteChanged();
        TestSteamCmdCommand.NotifyCanExecuteChanged();
        CheckForUpdatesNowCommand.NotifyCanExecuteChanged();
    }

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        _busyCts = new CancellationTokenSource();
        IsBusy = true;
        BusyStatus = status;
        BusyPercent = 0;
        try
        {
            await work(_busyCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Settings operation '{Status}' failed.", status);
            await _dialogs.ShowErrorAsync("Settings", ex.Message);
            StatusMessage = "Operation failed.";
        }
        finally
        {
            IsBusy = false;
            BusyStatus = string.Empty;
            BusyPercent = 0;
            _busyCts.Dispose();
            _busyCts = null;
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open folder {Path}.", path);
        }
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

    private static string ResolveBuildDate()
    {
        try
        {
            // Prefer the build date baked into the assembly (works under single-file publish).
            var attr = typeof(SettingsViewModel).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildDate");
            if (!string.IsNullOrWhiteSpace(attr?.Value)) return attr!.Value!;

            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd");
        }
        catch { /* best effort */ }
        return "unknown";
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
