using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using Atlas.Pages.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.Mods;

/// <summary>
/// Edits the active profile's own mod list (<see cref="IProfileService.ActiveProfile"/>.Mods): add Workshop/local
/// mods, import an A3 launcher preset, reorder/toggle per-mod flags, check Workshop for updates, download/update
/// mods via SteamCMD, and deploy mods (+ keys) into the server directory. A profile can be driven by a mod preset
/// (managed) or a manual list; the preset dropdown mirrors the profile editor. Transient view model — loads the
/// active profile in the constructor (same rationale as ServerConfig) and never subscribes to ActiveProfileChanged.
/// </summary>
public partial class ModsViewModel : BaseViewModel
{
    private readonly IProfileService _profiles;
    private readonly IModPresetService _presets;
    private readonly ISteamCmdService _steamCmd;
    private readonly IModDeploymentService _deployment;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;

    private List<ModPreset> _presetCache = new();

    [ObservableProperty] private ServerProfile? _profile;
    public bool HasActiveProfile => Profile is not null;

    public ObservableCollection<ArmaModEntry> Mods { get; } = new();

    [ObservableProperty] private ArmaModEntry? _selectedMod;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _modSummary = string.Empty;
    [ObservableProperty] private WorkshopModInfo? _selectedModInfo;

    /// <summary>Mod-preset choices ("(None — Manual Mod List)" + all presets).</summary>
    public List<PresetChoice> PresetChoices { get; private set; } = new();

    [ObservableProperty] private PresetChoice? _selectedPresetChoice;

    public string PresetInfo => SelectedPresetChoice?.Id is null
        ? "Manual mod list"
        : $"Managed by preset: {SelectedPresetChoice.Name}";

    private CancellationTokenSource? _cts;
    public bool CanCancel => _cts is not null;

    /// <summary>Suppresses the preset-selection side effect while we re-seed the dropdown during load.</summary>
    private bool _suppressPresetChange;

    public ModsViewModel(
        IProfileService profiles,
        IModPresetService presets,
        ISteamCmdService steamCmd,
        IModDeploymentService deployment,
        ISettingsService settings,
        IDialogService dialogs)
    {
        _profiles = profiles;
        _presets = presets;
        _steamCmd = steamCmd;
        _deployment = deployment;
        _settings = settings;
        _dialogs = dialogs;
        Title = "Mods";
        LoadActiveProfile();
    }

    // ----- Property hooks -----

    partial void OnProfileChanged(ServerProfile? value) => OnPropertyChanged(nameof(HasActiveProfile));

    partial void OnSelectedModChanged(ArmaModEntry? value)
    {
        SelectedModInfo = null;
        if (value is { WorkshopId: > 0 })
            _ = LoadSelectedModInfoAsync(value);
    }

    partial void OnSelectedPresetChoiceChanged(PresetChoice? value)
    {
        if (_suppressPresetChange) return;
        if (value is null || Profile is null) return;
        if (value.Id is null)
        {
            Profile.ActiveModPresetId = null;
        }
        else
        {
            Profile.ActiveModPresetId = value.Id;
            var preset = _presetCache.FirstOrDefault(p => p.Id == value.Id.Value);
            if (preset is not null)
            {
                Mods.Clear();
                foreach (var mod in preset.Mods) Mods.Add(CopyMod(mod));
            }
        }
        OnPropertyChanged(nameof(PresetInfo));
        RefreshSummary();
    }

    private async Task LoadSelectedModInfoAsync(ArmaModEntry mod)
    {
        try
        {
            var info = await _steamCmd.GetWorkshopModInfoAsync(mod.WorkshopId);
            // Only apply if the selection has not changed since the fetch started.
            void Apply()
            {
                if (ReferenceEquals(SelectedMod, mod)) SelectedModInfo = info;
            }
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(Apply);
            else
                Apply();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mods] Failed to fetch Workshop info for {mod.WorkshopId}: {ex.Message}");
        }
    }

    // ----- Loading / sync helpers -----

    private void LoadActiveProfile()
    {
        Profile = _profiles.ActiveProfile;
        Mods.Clear();
        if (Profile is not null)
            foreach (var m in Profile.Mods) Mods.Add(m);
        _ = BuildPresetChoicesAsync();
        RefreshSummary();
    }

    private async Task BuildPresetChoicesAsync()
    {
        try { _presetCache = await _presets.GetAllPresetsAsync(); }
        catch { _presetCache = new List<ModPreset>(); }

        var choices = new List<PresetChoice> { new() { Id = null, Name = "(None — Manual Mod List)" } };
        choices.AddRange(_presetCache.Select(p => new PresetChoice { Id = p.Id, Name = p.Name }));
        PresetChoices = choices;
        OnPropertyChanged(nameof(PresetChoices));

        var current = PresetChoices.FirstOrDefault(c => c.Id == Profile?.ActiveModPresetId) ?? PresetChoices[0];
        // Seed the selection without re-triggering the change hook (which would clobber the loaded
        // mod list with the preset's copy). The guard lets us assign via the generated property.
        _suppressPresetChange = true;
        try { SelectedPresetChoice = current; }
        finally { _suppressPresetChange = false; }
        OnPropertyChanged(nameof(PresetInfo));
    }

    private void RefreshSummary()
    {
        var count = Mods.Count;
        var updates = Mods.Count(m => m.UpdateAvailable);
        var lastChecked = Mods.Where(m => m.LastChecked != default)
            .Select(m => (DateTime?)m.LastChecked)
            .DefaultIfEmpty(null)
            .Max();
        var checkedText = lastChecked is null ? "never checked" : $"last checked {lastChecked:yyyy-MM-dd HH:mm}";
        ModSummary = $"{count} mod{(count == 1 ? "" : "s")}, {updates} update{(updates == 1 ? "" : "s")} available, {checkedText}";
    }

    private void SyncToProfile()
    {
        if (Profile is null) return;
        for (var i = 0; i < Mods.Count; i++) Mods[i].LoadOrder = i;
        Profile.Mods = Mods.ToList();
    }

    private static ArmaModEntry CopyMod(ArmaModEntry m) => new()
    {
        WorkshopId = m.WorkshopId, Name = m.Name, FolderName = m.FolderName, LocalPath = m.LocalPath,
        IsLocal = m.IsLocal, Version = m.Version, SteamFileSize = m.SteamFileSize, LastUpdated = m.LastUpdated,
        LastChecked = m.LastChecked, UpdateAvailable = m.UpdateAvailable, LoadOrder = m.LoadOrder,
        EnabledForServer = m.EnabledForServer, EnabledForClient = m.EnabledForClient,
        EnabledForHeadless = m.EnabledForHeadless, IsOptional = m.IsOptional, IsServerOnly = m.IsServerOnly,
        IsHeadlessOnly = m.IsHeadlessOnly,
    };

    private static ulong ParseWorkshopId(string input)
    {
        var match = Regex.Match(input, @"\d{6,}");
        return match.Success && ulong.TryParse(match.Value, out var id) ? id : 0;
    }

    // ----- Save / preset commands -----

    [RelayCommand]
    private async Task Save()
    {
        if (Profile is null) return;
        SyncToProfile();
        await _profiles.UpdateProfileAsync(Profile);
        StatusMessage = "Saved.";
        await _dialogs.ShowInfoAsync("Saved", $"Mods saved to profile '{Profile.Name}'.");
    }

    [RelayCommand]
    private async Task SaveAsPreset()
    {
        if (Profile is null) return;
        var name = await _dialogs.PromptAsync("Save as Preset", "Preset name", Profile.Name + " Mods");
        if (string.IsNullOrWhiteSpace(name)) return;
        SyncToProfile();
        await _presets.CreatePresetFromProfileAsync(name, string.Empty, Profile);
        await BuildPresetChoicesAsync();
        await _dialogs.ShowInfoAsync("Saved", $"Preset '{name}' created from the current mod list.");
    }

    [RelayCommand]
    private void DetachPreset()
    {
        if (PresetChoices.Count > 0) SelectedPresetChoice = PresetChoices[0];
    }

    // ----- Add / remove / move mods -----

    [RelayCommand]
    private async Task AddByWorkshop()
    {
        if (Profile is null) return;
        var input = await _dialogs.PromptAsync("Add Workshop Mod", "Workshop URL or numeric ID", string.Empty);
        if (string.IsNullOrWhiteSpace(input)) return;
        var id = ParseWorkshopId(input);
        if (id == 0) { await _dialogs.ShowErrorAsync("Invalid input", "Could not find a Workshop ID."); return; }
        Mods.Add(new ArmaModEntry
        {
            WorkshopId = id,
            Name = $"Workshop {id}",
            FolderName = $"@{id}",
            LoadOrder = Mods.Count,
        });
        RefreshSummary();
    }

    [RelayCommand]
    private async Task AddLocal()
    {
        if (Profile is null) return;
        var folder = await _dialogs.BrowseFolderAsync("Select local mod folder (@ModName)");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var folderName = new DirectoryInfo(folder).Name;
        Mods.Add(new ArmaModEntry
        {
            WorkshopId = 0,
            IsLocal = true,
            LocalPath = folder,
            Name = folderName.TrimStart('@'),
            FolderName = folderName.StartsWith('@') ? folderName : "@" + folderName,
            LoadOrder = Mods.Count,
        });
        RefreshSummary();
    }

    [RelayCommand]
    private async Task ImportA3()
    {
        if (Profile is null) return;
        var path = await _dialogs.BrowseFileAsync("Import Arma 3 Launcher preset",
            "Arma 3 preset (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var imported = await _presets.ParseA3LauncherPresetAsync(path);
            foreach (var mod in imported)
            {
                mod.LoadOrder = Mods.Count;
                Mods.Add(mod);
            }
            StatusMessage = $"Imported {imported.Count} mod{(imported.Count == 1 ? "" : "s")} from the launcher preset.";
            RefreshSummary();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Import failed", ex.Message);
        }
    }

    [RelayCommand]
    private void RemoveMod()
    {
        if (SelectedMod is not null) Mods.Remove(SelectedMod);
        RefreshSummary();
    }

    [RelayCommand]
    private void MoveUp()
    {
        var i = SelectedMod is null ? -1 : Mods.IndexOf(SelectedMod);
        if (i > 0) Mods.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        var i = SelectedMod is null ? -1 : Mods.IndexOf(SelectedMod);
        if (i >= 0 && i < Mods.Count - 1) Mods.Move(i, i + 1);
    }

    [RelayCommand]
    private void OpenInWorkshop()
    {
        if (SelectedMod is null || SelectedMod.WorkshopId == 0) return;
        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={SelectedMod.WorkshopId}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore launch failures */ }
    }

    // ----- Update / deploy commands (long-running) -----

    [RelayCommand]
    private Task CheckForUpdates() => RunWithProgressAsync(async (progress, ct) =>
    {
        var workshopMods = Mods.Where(m => m.WorkshopId > 0).ToList();
        if (workshopMods.Count == 0) { progress.Report("No Workshop mods to check."); return; }

        var updated = 0;
        for (var i = 0; i < workshopMods.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var mod = workshopMods[i];
            progress.Report($"Checking {i + 1}/{workshopMods.Count}: {mod.Name}…");
            var info = await _steamCmd.GetWorkshopModInfoAsync(mod.WorkshopId, ct);
            if (info is not null)
            {
                mod.UpdateAvailable = mod.LastUpdated < info.TimeUpdated;
                mod.LastChecked = DateTime.UtcNow;
                if (mod.SteamFileSize == 0) mod.SteamFileSize = info.FileSize;
                if (mod.UpdateAvailable) updated++;
            }
        }

        RefreshSummary();
        RefreshGrid();
        progress.Report($"Checked {workshopMods.Count} mod(s); {updated} update(s) available.");
    });

    [RelayCommand]
    private Task UpdateSelected() => RunWithProgressAsync(async (progress, ct) =>
    {
        if (SelectedMod is not { WorkshopId: > 0 } mod) { progress.Report("Select a Workshop mod first."); return; }
        var login = await EnsureSteamReadyAsync(progress, ct);
        if (login is null) { progress.Report("Cancelled — Steam not configured."); return; }
        await _steamCmd.UpdateModAsync(mod.WorkshopId, StagingPath(), login, progress, ct, SteamGuardProvider);
        progress.Report($"Updated {mod.Name}.");
    });

    [RelayCommand]
    private Task UpdateAll() => RunWithProgressAsync(async (progress, ct) =>
    {
        var ids = Mods.Where(m => m.WorkshopId > 0).Select(m => m.WorkshopId).Distinct().ToList();
        if (ids.Count == 0) { progress.Report("No Workshop mods to update."); return; }
        var login = await EnsureSteamReadyAsync(progress, ct);
        if (login is null) { progress.Report("Cancelled — Steam not configured."); return; }
        await _steamCmd.UpdateModsAsync(ids, StagingPath(), login, progress, ct, SteamGuardProvider);
        progress.Report($"Updated {ids.Count} mod(s).");
    });

    [RelayCommand]
    private Task ForceRedownload() => RunWithProgressAsync(async (progress, ct) =>
    {
        if (SelectedMod is not { WorkshopId: > 0 } mod) { progress.Report("Select a Workshop mod first."); return; }
        var login = await EnsureSteamReadyAsync(progress, ct);
        if (login is null) { progress.Report("Cancelled — Steam not configured."); return; }
        await _steamCmd.UpdateModAsync(mod.WorkshopId, StagingPath(), login, progress, ct, SteamGuardProvider);
        progress.Report($"Re-validated {mod.Name}.");
    });

    [RelayCommand]
    private Task DeployMods() => RunWithProgressAsync(async (progress, ct) =>
    {
        if (Profile is null) return;
        if (string.IsNullOrWhiteSpace(Profile.ServerDirectory))
        {
            await _dialogs.ShowErrorAsync("Server directory required",
                "Set the server directory on the Server Config page before deploying mods.");
            return;
        }
        await _deployment.DeployModsAsync(Profile, progress, ct);
        progress.Report("Mods deployed.");
    });

    [RelayCommand]
    private Task CopyKeys() => RunWithProgressAsync(async (progress, ct) =>
    {
        if (Profile is null) return;
        if (string.IsNullOrWhiteSpace(Profile.ServerDirectory))
        {
            await _dialogs.ShowErrorAsync("Server directory required",
                "Set the server directory on the Server Config page before copying keys.");
            return;
        }
        await _deployment.CopyModKeysAsync(Profile, progress, ct);
        progress.Report("Keys copied.");
    });

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ----- Long-running infrastructure -----

    /// <summary>Forces the DataGrid to re-evaluate row visuals after mutating mods in place.</summary>
    private void RefreshGrid()
    {
        // ArmaModEntry is a POCO (no INotifyPropertyChanged), so a collection reset is how the grid picks
        // up in-place mutations (UpdateAvailable/LastChecked). Preserve the selection across it so
        // "Check for Updates" doesn't silently clear the selected row and detail panel.
        var selected = SelectedMod;
        var snapshot = Mods.ToList();
        Mods.Clear();
        foreach (var m in snapshot) Mods.Add(m);
        if (selected is not null && Mods.Contains(selected)) SelectedMod = selected;
    }

    private Task<string?> SteamGuardProvider(CancellationToken ct)
        => _dialogs.PromptAsync("Steam Guard", "Enter the Steam Guard / 2FA code", string.Empty);

    private async Task<string?> EnsureSteamReadyAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!await _steamCmd.IsSteamCmdAvailableAsync())
        {
            if (!await _dialogs.ConfirmAsync("SteamCMD required",
                    "SteamCMD is not installed. Download it now?", "Download", "Cancel"))
                return null;
            var downloadProgress = new Progress<(string message, int percent)>(t => progress.Report(t.message));
            await _steamCmd.DownloadSteamCmdAsync(downloadProgress, ct);
        }

        var user = _steamCmd.GetSavedUsername();
        if (string.IsNullOrWhiteSpace(user))
        {
            user = await _dialogs.PromptAsync("Steam login", "Steam username (no password is stored)", string.Empty);
            if (string.IsNullOrWhiteSpace(user)) return null;
            _steamCmd.SaveUsername(user);
        }
        return user;
    }

    private string StagingPath()
    {
        var configured = _settings.Settings.ModStagingDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppConstants.AppDataRoot, "Mods")
            : configured;
    }

    private async Task RunWithProgressAsync(Func<IProgress<string>, CancellationToken, Task> work)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        IsBusy = true;
        var progress = new Progress<string>(m => StatusMessage = m);
        try
        {
            await work(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed.";
            await _dialogs.ShowErrorAsync("Operation failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(CanCancel));
        }
    }
}
