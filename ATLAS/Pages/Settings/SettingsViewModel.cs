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
    private readonly AtlasDatabase _db;
    private readonly LoggingLevelSwitch _levelSwitch;

    private CancellationTokenSource? _busyCts;

    // ----- SteamCMD / Steam -----
    [ObservableProperty] private string _steamCmdPath = string.Empty;
    [ObservableProperty] private string _modStagingDirectory = string.Empty;
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
        IUpdateService updates, IThemeService theme, ISteamCmdService steam, AtlasDatabase db,
        LoggingLevelSwitch levelSwitch)
    {
        _settings = settings;
        _secrets = secrets;
        _dialogs = dialogs;
        _updates = updates;
        _theme = theme;
        _steam = steam;
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

    // ----------------------------------------------------------------- mod staging

    [RelayCommand]
    private async Task BrowseModStaging()
    {
        var dir = await _dialogs.BrowseFolderAsync("Select mod staging directory", ModStagingDirectory);
        if (!string.IsNullOrWhiteSpace(dir)) ModStagingDirectory = dir;
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
                if (await _dialogs.ConfirmAsync("Update Available",
                        $"A newer version is available: v{result.LatestVersion} (you have v{result.CurrentVersion}).{notes}",
                        "View Release", "Close") && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                    _updates.OpenReleasePage(result.ReleaseUrl!);
                StatusMessage = $"Update available: v{result.LatestVersion}.";
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
