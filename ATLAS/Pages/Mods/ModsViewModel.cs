using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using Atlas.Pages.ModPresets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Mods;

/// <summary>
/// The global "Mods" hub — two faces over the shared mod library:
/// <list type="bullet">
///   <item><b>Library</b>: every mod ATLAS knows about (across all profiles/presets). Add + download Workshop
///   items via SteamCMD, check Workshop for updates and batch-update, remove from the registry, and
///   deploy / verify a chosen profile's mods (junction + <c>.bikey</c> sync + duplicate-key check).</item>
///   <item><b>Presets</b>: the composed <see cref="ModPresetsViewModel"/> (named reusable collections).</item>
/// </list>
/// Registered as a singleton so an in-flight SteamCMD download keeps streaming across navigation
/// (mirrors <c>UpdaterViewModel</c>). SteamCMD output is marshalled to the UI via <see cref="Progress{T}"/>.
/// </summary>
public partial class ModsViewModel : BaseViewModel
{
    private readonly IModLibraryService _library;
    private readonly ISteamCmdService _steam;
    private readonly IModDeploymentService _deploy;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;

    private const int MaxOutputLines = 1000;
    private CancellationTokenSource? _cts;

    /// <summary>The composed Mod Presets view (the hub's second tab).</summary>
    public ModPresetsViewModel Presets { get; }

    public ObservableCollection<ModLibraryRow> Library { get; } = new();
    public ObservableCollection<ServerProfile> Profiles { get; } = new();
    public ObservableCollection<string> Output { get; } = new();

    [ObservableProperty] private string _addModInput = string.Empty;
    [ObservableProperty] private string _modDirectory = string.Empty;
    [ObservableProperty] private ServerProfile? _targetProfile;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _libraryStatus = string.Empty;

    // ----- Selected-mod detail card (Library tab) -----
    private int _detailRequest;   // guards against out-of-order async fetches on rapid selection
    [ObservableProperty] private ModLibraryRow? _selectedLibraryRow;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _hasWorkshopId;
    [ObservableProperty] private bool _hasDetailImage;
    [ObservableProperty] private string _detailName = string.Empty;
    [ObservableProperty] private string _detailInfo = string.Empty;      // loading / local / error line
    [ObservableProperty] private string _detailMeta = string.Empty;      // size · updated
    [ObservableProperty] private string _detailDescription = string.Empty;
    [ObservableProperty] private string _detailImageUrl = string.Empty;

    public ModsViewModel(IModLibraryService library, ISteamCmdService steam, IModDeploymentService deploy,
        IProfileService profiles, ISettingsService settings, IDialogService dialogs, ModPresetsViewModel presets)
    {
        _library = library;
        _steam = steam;
        _deploy = deploy;
        _profiles = profiles;
        _settings = settings;
        _dialogs = dialogs;
        Presets = presets;
        Title = "Mods";
        _modDirectory = _settings.Settings.ModStagingDirectory ?? string.Empty;
        _ = LoadLibraryAsync();
    }

    // --------------------------------------------------------------- load / refresh

    [RelayCommand]
    private async Task LoadLibraryAsync()
    {
        IsBusy = true;
        try
        {
            var mods = await _library.GetAllModsAsync();
            var usage = await _library.GetModUsageAsync();

            Library.Clear();
            foreach (var m in mods)
            {
                var usedBy = usage.TryGetValue(m.ModId, out var names) && names.Count > 0
                    ? string.Join(", ", names)
                    : "—";
                Library.Add(new ModLibraryRow(m, usedBy, StatusFor(m)));
            }

            // Refresh the deploy-target list, preserving the current selection (falling back to active/first).
            var selectedId = TargetProfile?.Id;
            var all = await _profiles.GetAllProfilesAsync();
            Profiles.Clear();
            foreach (var p in all) Profiles.Add(p);
            TargetProfile = Profiles.FirstOrDefault(p => p.Id == selectedId)
                            ?? Profiles.FirstOrDefault(p => p.Id == _profiles.ActiveProfile?.Id)
                            ?? Profiles.FirstOrDefault();

            var updatable = Library.Count(r => r.Mod.UpdateAvailable);
            LibraryStatus = updatable > 0
                ? $"{Library.Count} mod(s) — {updatable} with updates available."
                : $"{Library.Count} mod(s) in the library.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load the mod library.");
            LibraryStatus = "Failed to load the mod library.";
        }
        finally { IsBusy = false; }
    }

    private static string StatusFor(ArmaModEntry m) =>
        m.IsLocal ? "Local"
        : m.UpdateAvailable ? "Update available"
        : m.LastChecked == DateTime.MinValue ? "Not checked"
        : "Up to date";

    // --------------------------------------------------------------- selected-mod detail card

    partial void OnSelectedLibraryRowChanged(ModLibraryRow? value) => _ = LoadModDetailAsync(value);

    /// <summary>Populates the detail card for the selected library row. Workshop mods fetch their preview
    /// image + metadata from Steam; local/unlisted mods (no Workshop id) show a graceful placeholder.</summary>
    private async Task LoadModDetailAsync(ModLibraryRow? row)
    {
        var req = ++_detailRequest;
        HasDetailImage = false;
        DetailImageUrl = string.Empty;
        DetailMeta = string.Empty;
        DetailDescription = string.Empty;
        HasSelection = row is not null;
        HasWorkshopId = row is { WorkshopId: > 0 };

        if (row is null) { DetailName = string.Empty; DetailInfo = string.Empty; return; }

        DetailName = row.Name;
        if (row.WorkshopId == 0)
        {
            DetailInfo = row.IsLocal ? "Local mod — no Workshop data." : "No Workshop ID — no Workshop data.";
            return;
        }

        DetailInfo = "Loading Workshop info…";
        try
        {
            var info = await _steam.GetWorkshopModInfoAsync(row.WorkshopId);
            if (req != _detailRequest) return;   // a newer selection superseded this fetch
            if (info is null)
            {
                DetailInfo = "Couldn't load Workshop info (offline, or the item is hidden/removed).";
                return;
            }
            DetailName = string.IsNullOrWhiteSpace(info.Title) ? row.Name : info.Title;
            DetailMeta = $"{FormatBytes(info.FileSize)}  ·  updated {info.TimeUpdated.ToLocalTime():yyyy-MM-dd}";
            DetailDescription = Truncate(info.Description, 800);
            if (!string.IsNullOrWhiteSpace(info.PreviewUrl))
            {
                DetailImageUrl = info.PreviewUrl;
                HasDetailImage = true;
            }
            DetailInfo = string.Empty;
        }
        catch (Exception ex)
        {
            if (req == _detailRequest) DetailInfo = "Couldn't load Workshop info.";
            Log.Debug(ex, "Workshop detail fetch failed for {Id}.", row.WorkshopId);
        }
    }

    [RelayCommand]
    private void OpenSelectedInWorkshop()
    {
        var id = SelectedLibraryRow?.WorkshopId ?? 0;
        if (id == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo(
                $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Debug(ex, "Could not open the Workshop page for {Id}.", id); }
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes == 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.#} {units[u]}";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    // --------------------------------------------------------------- add + download

    private bool NotWorking => !IsWorking;

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private async Task SteamLogin()
    {
        if (!await EnsureSteamCmdAsync()) return;
        _cts = new CancellationTokenSource();
        IsWorking = true;
        Output.Clear();
        try
        {
            if (await DoSteamLoginAsync(_cts.Token))
                await _dialogs.ShowInfoAsync("Steam login",
                    "Logged in to Steam. Your session is cached, so mod downloads and updates won't ask again.");
        }
        catch (OperationCanceledException) { Append("✖ Cancelled."); }
        catch (Exception ex)
        {
            Log.Error(ex, "Steam login failed.");
            Append($"✖ Login failed: {ex.Message}");
            await _dialogs.ShowErrorAsync("Steam login", ex.Message);
        }
        finally { IsWorking = false; _cts.Dispose(); _cts = null; }
    }

    /// <summary>Prompts for a username + masked password and logs in to Steam (password used once, never
    /// stored). A Steam Guard code prompt, if required, is surfaced via <see cref="SteamGuardAsync"/>.
    /// Returns true on a confirmed login.</summary>
    private async Task<bool> DoSteamLoginAsync(CancellationToken ct)
    {
        var user = await _dialogs.PromptAsync("Log in to Steam", "Steam username",
            _steam.GetSavedUsername() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(user)) return false;
        var pass = await _dialogs.PromptAsync("Log in to Steam",
            "Steam password (used once to log in — never stored by ATLAS).", string.Empty, isPassword: true);
        if (string.IsNullOrWhiteSpace(pass)) return false;

        Append($"Logging in to Steam as '{user.Trim()}'…");
        var progress = new Progress<string>(line => OnUi(() => Append(line)));
        var ok = await _steam.LoginAsync(user.Trim(), pass, progress, ct, SteamGuardAsync);
        Append(ok ? "✔ Logged in." : "✖ Login did not complete.");
        if (!ok)
            await _dialogs.ShowErrorAsync("Steam login",
                "Login didn't complete. Check your username, password, and Steam Guard code, then try again.");
        return ok;
    }

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private async Task AddAndDownload()
    {
        var id = ParseWorkshopId(AddModInput);
        if (id == 0)
        {
            await _dialogs.ShowErrorAsync("Add mod", "Enter a Steam Workshop URL or numeric item ID.");
            return;
        }
        if (!await EnsureSteamCmdAsync()) return;
        var dir = await RequireModDirectoryAsync();
        if (dir is null) return;
        var login = await RequireLoginAsync();
        if (login is null) return;

        // Register a stub row up front so the download has a registry entry to refresh afterward.
        await _library.UpsertModAsync(new ArmaModEntry
        {
            WorkshopId = id,
            Name = $"Workshop {id}",
            FolderName = $"@{id}",
            LocalPath = string.Empty,
            IsLocal = false,
        });

        await RunStreamingAsync($"Downloading Workshop item {id} into:\r\n  {dir}", async (progress, ct) =>
        {
            await RunModDownloadAsync(new[] { id }, dir, login, progress, ct);
            await RefreshMetadataAsync(new[] { id });
            OnUi(() => AddModInput = string.Empty);
            progress.Report($"✔ Downloaded Workshop item {id}.");
        });
    }

    // --------------------------------------------------------------- check + update

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private async Task CheckForUpdates()
    {
        var rows = Library.Where(r => !r.IsLocal && r.WorkshopId > 0).ToList();
        if (rows.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Check for updates", "No Workshop mods in the library to check.");
            return;
        }
        await RunStreamingAsync($"Checking {rows.Count} mod(s) against the Workshop…", async (progress, ct) =>
        {
            var outdated = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var info = await _steam.GetWorkshopModInfoAsync(row.Mod.WorkshopId, ct);
                row.Mod.LastChecked = DateTime.UtcNow;
                if (info is not null)
                {
                    if (!string.IsNullOrWhiteSpace(info.Title)) row.Mod.Name = info.Title;
                    row.Mod.SteamFileSize = info.FileSize;
                    row.Mod.UpdateAvailable = row.Mod.LastUpdated < info.TimeUpdated;
                    if (row.Mod.UpdateAvailable) outdated++;
                }
                await _library.UpsertModAsync(row.Mod);
                OnUi(() => row.Status = StatusFor(row.Mod));
                progress.Report($"  {row.Mod.Name}: {(row.Mod.UpdateAvailable ? "update available" : "up to date")}");
            }
            progress.Report($"✔ Checked {rows.Count} mod(s); {outdated} need updating.");
        });
    }

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private Task UpdateOutdated() => UpdateModsAsync(Library.Where(r => r.Mod.UpdateAvailable && !r.IsLocal));

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private Task UpdateSelected() => UpdateModsAsync(Library.Where(r => r.Selected && !r.IsLocal));

    private async Task UpdateModsAsync(IEnumerable<ModLibraryRow> rows)
    {
        var ids = rows.Where(r => r.WorkshopId > 0).Select(r => r.Mod.WorkshopId).Distinct().ToList();
        if (ids.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Update mods",
                "Nothing to update. Check rows to update, or run \"Check for Updates\" first.");
            return;
        }
        if (!await EnsureSteamCmdAsync()) return;
        var dir = await RequireModDirectoryAsync();
        if (dir is null) return;
        var login = await RequireLoginAsync();
        if (login is null) return;

        await RunStreamingAsync($"Updating {ids.Count} mod(s) into:\r\n  {dir}", async (progress, ct) =>
        {
            await RunModDownloadAsync(ids, dir, login, progress, ct);
            await RefreshMetadataAsync(ids);
            progress.Report($"✔ Updated {ids.Count} mod(s).");
        });
    }

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private async Task RemoveSelected()
    {
        var selected = Library.Where(r => r.Selected).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Remove mods", "Check one or more mods to remove from the library.");
            return;
        }
        if (!await _dialogs.ConfirmAsync("Remove from library",
                $"Remove {selected.Count} mod(s) from the ATLAS library?\n\nThis also removes them from any " +
                "profile or preset that uses them. Files already downloaded to disk are left untouched.",
                "Remove", "Cancel"))
            return;

        foreach (var row in selected) await _library.DeleteModAsync(row.Mod.ModId);
        await LoadLibraryAsync();
    }

    // --------------------------------------------------------------- deploy + key tools

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private async Task Deploy()
    {
        if (TargetProfile is not { } profile) { await NoTargetAsync(); return; }
        await RunStreamingAsync($"Deploying mods for '{profile.Name}'…", async (progress, ct) =>
        {
            await _deploy.DeployModsAsync(profile, progress, ct);
            progress.Report("✔ Deploy finished.");
        });
    }

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private async Task CleanupStale()
    {
        if (TargetProfile is not { } profile) { await NoTargetAsync(); return; }
        await RunStreamingAsync($"Cleaning up stale deployed mods for '{profile.Name}'…", async (progress, ct) =>
        {
            await _deploy.CleanupStaleDeployedModsAsync(profile, progress, ct);
            progress.Report("✔ Cleanup finished.");
        });
    }

    [RelayCommand(CanExecute = nameof(NotWorking))]
    private async Task CheckKeyConflicts()
    {
        if (TargetProfile is not { } profile) { await NoTargetAsync(); return; }
        await RunStreamingAsync($"Checking .bikey conflicts for '{profile.Name}'…", async (progress, ct) =>
        {
            var conflicts = await _deploy.CheckKeyConflictsAsync(profile, ct);
            if (conflicts.Count == 0) { progress.Report("✔ No duplicate .bikey conflicts."); return; }
            progress.Report($"⚠ {conflicts.Count} key conflict(s) — the last-copied key wins:");
            foreach (var c in conflicts)
                progress.Report($"    {c.KeyFileName}: {c.FolderA} vs {c.FolderB}");
        });
    }

    private Task NoTargetAsync() =>
        _dialogs.ShowErrorAsync("No target profile", "Pick a profile in the deploy dropdown first.");

    // --------------------------------------------------------------- cancel

    private bool CanCancel => IsWorking;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        Append("Cancelling…");
    }

    // --------------------------------------------------------------- helpers

    /// <summary>Downloads/updates the given items; if SteamCMD reports the session is missing or expired,
    /// runs a login and retries once.</summary>
    private async Task RunModDownloadAsync(IReadOnlyList<ulong> ids, string dir, string login,
        IProgress<string> progress, CancellationToken ct)
    {
        // Normalize to the Steam root so SteamCMD reuses existing Workshop content instead of nesting a
        // duplicate steamapps\workshop tree under whatever sub-folder the user picked.
        var root = WorkshopPaths.ResolveSteamCmdRoot(dir);
        if (!string.Equals(root, dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            progress.Report($"(Resolved to Steam root: {root})");
        try
        {
            await _steam.UpdateModsAsync(ids, root, login, progress, ct, SteamGuardAsync);
        }
        catch (SteamLoginRequiredException)
        {
            progress.Report("Steam session missing or expired — logging in…");
            if (!await DoSteamLoginAsync(ct))
                throw new InvalidOperationException("A Steam login is required to download mods.");
            await _steam.UpdateModsAsync(ids, root, _steam.GetSavedUsername() ?? login, progress, ct, SteamGuardAsync);
        }
    }

    /// <summary>Pulls fresh Workshop metadata for the given items and writes it back to the registry.</summary>
    private async Task RefreshMetadataAsync(IReadOnlyCollection<ulong> workshopIds)
    {
        if (workshopIds.Count == 0) return;
        var byId = (await _library.GetAllModsAsync())
            .Where(m => m.WorkshopId > 0)
            .GroupBy(m => m.WorkshopId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var id in workshopIds)
        {
            if (!byId.TryGetValue(id, out var mod)) continue;
            var info = await _steam.GetWorkshopModInfoAsync(id);
            if (info is not null)
            {
                if (!string.IsNullOrWhiteSpace(info.Title)) mod.Name = info.Title;
                mod.SteamFileSize = info.FileSize;
                mod.LastUpdated = info.TimeUpdated;
            }
            mod.LastChecked = DateTime.UtcNow;
            mod.UpdateAvailable = false;   // just downloaded — current by definition
            await _library.UpsertModAsync(mod);
        }
    }

    /// <summary>Runs a streaming SteamCMD/deploy operation: busy state, cleared output, cancel + error handling,
    /// and a library refresh afterward to reflect any registry changes.</summary>
    private async Task RunStreamingAsync(string header, Func<IProgress<string>, CancellationToken, Task> body)
    {
        _cts = new CancellationTokenSource();
        IsWorking = true;
        Output.Clear();
        Append(header);
        var progress = new Progress<string>(line => OnUi(() => Append(line)));
        try
        {
            await body(progress, _cts.Token);
        }
        catch (OperationCanceledException) { Append("✖ Cancelled."); }
        catch (Exception ex)
        {
            Log.Error(ex, "Mods operation failed.");
            Append($"✖ Failed: {ex.Message}");
            await _dialogs.ShowErrorAsync("Mods", ex.Message);
        }
        finally
        {
            IsWorking = false;
            _cts.Dispose();
            _cts = null;
            await LoadLibraryAsync();
        }
    }

    /// <summary>Returns a saved Steam username, prompting for (and saving) one if none is stored.
    /// The password is never requested or stored — SteamCMD caches its own login token (FASTER-style).</summary>
    private async Task<string?> RequireLoginAsync()
    {
        var login = _steam.GetSavedUsername();
        if (!string.IsNullOrWhiteSpace(login)) return login;

        // No saved Steam login yet — prompt for one now (covers first-time use), then proceed.
        if (!await DoSteamLoginAsync(CancellationToken.None)) return null;
        return _steam.GetSavedUsername();
    }

    /// <summary>Steam Guard callback for SteamCMD. Invoked from the background stdout pump; the dialog service
    /// marshals the prompt to the UI thread internally.</summary>
    private async Task<string?> SteamGuardAsync(CancellationToken ct)
    {
        var code = await _dialogs.PromptAsync("Steam Guard",
            "Enter the Steam Guard code (from your email or the Steam mobile app).", string.Empty);
        return string.IsNullOrWhiteSpace(code) ? null : code.Trim();
    }

    private async Task<bool> EnsureSteamCmdAsync()
    {
        if (await _steam.IsSteamCmdAvailableAsync()) return true;
        await _dialogs.ShowErrorAsync("SteamCMD not set up",
            "SteamCMD isn't installed yet. Set it up under Settings → SteamCMD (Download SteamCMD), then try again.");
        return false;
    }

    /// <summary>Returns the configured mod download directory, prompting the user to pick one if unset. Never
    /// silently falls back to AppData — mods should land where the user already keeps Workshop content so
    /// nothing is duplicated. (Deployment into each profile's server folder is a separate, per-profile path.)</summary>
    private async Task<string?> RequireModDirectoryAsync()
    {
        if (!string.IsNullOrWhiteSpace(ModDirectory)) return ModDirectory.Trim();
        var dir = await _dialogs.BrowseFolderAsync(
            "Select the mod download directory (where SteamCMD downloads Workshop mods)");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        ModDirectory = dir;   // persists via OnModDirectoryChanged
        return dir;
    }

    [RelayCommand]
    private async Task BrowseModDirectory()
    {
        var dir = await _dialogs.BrowseFolderAsync(
            "Select the mod download directory (where SteamCMD downloads Workshop mods)", ModDirectory);
        if (!string.IsNullOrWhiteSpace(dir)) ModDirectory = dir;
    }

    partial void OnModDirectoryChanged(string value)
    {
        _settings.Settings.ModStagingDirectory = value?.Trim() ?? string.Empty;
        _ = _settings.SaveAsync();
    }

    private static ulong ParseWorkshopId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return 0;
        var match = Regex.Match(input, @"\d{6,}");
        return match.Success && ulong.TryParse(match.Value, out var id) ? id : 0;
    }

    private void Append(string line)
    {
        foreach (var part in line.Replace("\r\n", "\n").Split('\n'))
            Output.Add(part);
        while (Output.Count > MaxOutputLines) Output.RemoveAt(0);
    }

    partial void OnIsWorkingChanged(bool value)
    {
        SteamLoginCommand.NotifyCanExecuteChanged();
        AddAndDownloadCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        UpdateOutdatedCommand.NotifyCanExecuteChanged();
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        DeployCommand.NotifyCanExecuteChanged();
        CleanupStaleCommand.NotifyCanExecuteChanged();
        CheckKeyConflictsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
