using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Profiles;

/// <summary>
/// The profile workspace — profiles are the driving entity. The left rail lists profiles (selecting
/// one makes it active); the right side edits a single working copy of the active profile across the
/// Profile / Missions / Config / Network tabs. One Save (footer) persists the whole working model from
/// any tab; Launch starts the server with it.
/// </summary>
/// <remarks>
/// This view model folds together what used to be the modal profile editor plus the Server Config,
/// Missions and Mods pages. Heavy mod operations (SteamCMD update / deploy / copy keys) are intentionally
/// NOT here — they move to the future global Mods tab; only mod-list building (add / import / reorder /
/// flags) lives in the Profile tab so a profile's mod list can still be assembled.
/// Transient VM (no <see cref="IProfileService.ActiveProfileChanged"/> subscription): the rail's
/// selection is the only activation path on this page.
/// </remarks>
public partial class ProfileWorkspaceViewModel : BaseViewModel, INavigationGuard
{
    private readonly IProfileService _profiles;
    private readonly IMissionService _missions;
    private readonly IModPresetService _presets;
    private readonly IConfigGeneratorService _configGen;
    private readonly IServerProcessService _process;
    private readonly IDialogService _dialogs;
    private readonly IModLibraryService _modLibrary;
    private readonly IMissionDependencyChecker _depChecker;
    private readonly IBackupService _backups;

    /// <summary>The working copy currently being edited (a clone of the active profile).</summary>
    [ObservableProperty] private ServerProfile? _profile;
    [ObservableProperty] private string _activeProfileName = "No active profile";
    /// <summary>Transient backup/restore progress line shown in the header while a snapshot runs.</summary>
    [ObservableProperty] private string _backupStatus = string.Empty;

    public bool HasActiveProfile => Profile is not null;

    // ----- Option lists (mirrors the old editor) -----
    public int[] ExThreadsOptions { get; } = { 1, 3, 5, 7 };
    public int[] VerifySignaturesOptions { get; } = { 0, 1, 2 };
    public string[] FilePatchingOptions { get; } = { "No clients", "Headless clients only", "All clients" };
    public int[] BandwidthAlgOptions { get; } = { 1, 2 };
    public string[] DifficultyOptions { get; } = { "Recruit", "Regular", "Veteran", "Custom" };
    public string[] OnLimitedOptions { get; } = { "Never", "Limited", "Always" };
    public string[] ThirdPersonOptions { get; } = { "Disabled", "Enabled", "Vehicles only" };

    // ----- server.cfg text-ish fields -----
    [ObservableProperty] private string _motdText = string.Empty;

    public ObservableCollection<string> LoadExtensions { get; } = new();
    public ObservableCollection<string> PreprocessExtensions { get; } = new();
    public ObservableCollection<string> HtmlExtensions { get; } = new();
    public ObservableCollection<string> HtmlUris { get; } = new();
    [ObservableProperty] private string _newLoadExtension = string.Empty;
    [ObservableProperty] private string _newPreprocessExtension = string.Empty;
    [ObservableProperty] private string _newHtmlExtension = string.Empty;
    [ObservableProperty] private string _newHtmlUri = string.Empty;

    // ----- Mods (selection only; heavy ops live in the future global Mods tab) -----
    public ObservableCollection<ArmaModEntry> Mods { get; } = new();
    [ObservableProperty] private ArmaModEntry? _selectedMod;
    [ObservableProperty] private string _modSummary = string.Empty;

    private List<ModPreset> _presetCache = new();
    public List<PresetChoice> PresetChoices { get; private set; } = new();
    [ObservableProperty] private PresetChoice? _selectedPresetChoice;
    private bool _suppressPresetChange;
    public string PresetInfo => SelectedPresetChoice?.Id is null
        ? "Manual mod list"
        : $"Managed by preset: {SelectedPresetChoice.Name} (edit it on the Mod Presets page).";

    // ----- Missions -----
    private readonly List<MissionInfo> _allMissions = new();
    public ObservableCollection<MissionInfo> Missions { get; } = new();
    public ObservableCollection<string> Terrains { get; } = new();
    [ObservableProperty] private MissionInfo? _selectedMission;

    /// <summary>Summary of the checked mission rotation, shown under the grid.</summary>
    [ObservableProperty] private string _queueSummary = "No missions in rotation.";

    /// <summary>True once a scan has populated <see cref="_allMissions"/>, so <see cref="SyncProfile"/> may flush
    /// the queue from the grid. Before the first scan the DB-loaded MissionQueue must be preserved (not overwritten
    /// with an empty grid).</summary>
    private bool _missionsLoaded;
    /// <summary>Suppresses per-item queue/preview refresh while IsActive is set programmatically (load / clear).</summary>
    private bool _loadingMissions;

    [ObservableProperty] private string _missionSearchText = string.Empty;
    [ObservableProperty] private string _selectedTerrain = "All";
    [ObservableProperty] private string _missionStatus = string.Empty;

    // ----- Config previews -----
    [ObservableProperty] private string _serverCfgPreview = string.Empty;
    [ObservableProperty] private string _basicCfgPreview = string.Empty;
    [ObservableProperty] private string _launchCommandPreview = string.Empty;
    [ObservableProperty] private string _arma3ProfilePreview = string.Empty;

    // ----- Dirty tracking -----
    private string _baselineJson = string.Empty;

    public ProfileWorkspaceViewModel(IProfileService profiles, IMissionService missions,
        IModPresetService presets, IConfigGeneratorService configGen, IServerProcessService process,
        IDialogService dialogs, IModLibraryService modLibrary, IMissionDependencyChecker depChecker,
        IBackupService backups)
    {
        _profiles = profiles;
        _missions = missions;
        _presets = presets;
        _configGen = configGen;
        _process = process;
        _dialogs = dialogs;
        _modLibrary = modLibrary;
        _depChecker = depChecker;
        _backups = backups;
        Title = "Profiles";

        // The active profile is set by the sidebar (OpenProfile) before navigating here, so edit it.
        if (_profiles.ActiveProfile is not null) LoadWorkingCopy(_profiles.ActiveProfile);
    }

    // ===================================================================== Navigation guard (unsaved edits)

    /// <summary>Called by the navigation service before leaving this page; prompt to save if dirty.</summary>
    public async Task<bool> CanLeaveAsync()
    {
        if (Profile is null || !IsDirty()) return true;
        var save = await _dialogs.ConfirmAsync("Unsaved changes",
            $"Save changes to '{Profile.Name}' before leaving?", "Save", "Discard");
        if (save) await SaveInternalAsync(showInfo: false);
        return true;
    }

    // ===================================================================== Working-copy load / dirty

    private void LoadWorkingCopy(ServerProfile source)
    {
        Profile = Clone(source);
        ActiveProfileName = Profile.Name;

        MotdText = string.Join(Environment.NewLine, Profile.MotdLines);
        Replace(LoadExtensions, Profile.AllowedLoadFileExtensions);
        Replace(PreprocessExtensions, Profile.AllowedPreprocessFileExtensions);
        Replace(HtmlExtensions, Profile.AllowedHTMLLoadExtensions);
        Replace(HtmlUris, Profile.AllowedHTMLLoadURIs);

        Mods.Clear();
        foreach (var m in Profile.Mods) Mods.Add(m);
        // Manual profiles show the WHOLE mod library with per-profile activation checkboxes; a preset-managed
        // profile shows only the preset's mods (its list is driven by the preset).
        if (Profile.ActiveModPresetId is null) _ = MergeLibraryModsAsync();
        RefreshModSummary();
        _ = BuildPresetChoicesAsync();

        _ = ReloadMissionsAsync();
        RefreshPreview();

        SyncProfile();
        _baselineJson = Serialize(Profile);
        OnPropertyChanged(nameof(HasActiveProfile));
    }

    /// <summary>True if the working copy differs from what was last loaded/saved.</summary>
    private bool IsDirty()
    {
        if (Profile is null) return false;
        SyncProfile();
        return Serialize(Profile) != _baselineJson;
    }

    /// <summary>Pushes the editable collections (MOTD / extensions / mods) back onto the working copy.</summary>
    private void SyncProfile()
    {
        if (Profile is null) return;
        Profile.MotdLines = MotdText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).ToList();
        Profile.AllowedLoadFileExtensions = LoadExtensions.ToList();
        Profile.AllowedPreprocessFileExtensions = PreprocessExtensions.ToList();
        Profile.AllowedHTMLLoadExtensions = HtmlExtensions.ToList();
        Profile.AllowedHTMLLoadURIs = HtmlUris.ToList();
        for (var i = 0; i < Mods.Count; i++) Mods[i].LoadOrder = i;
        // Only mods activated for THIS profile (any of server/client/headless ticked) are persisted to
        // ProfileMods; the rest are library rows shown for selection but not part of this profile.
        Profile.Mods = Mods.Where(IsModActive).ToList();

        // Flush the checked missions into the rotation — but only once a scan has populated the grid, so we never
        // overwrite a saved queue with an empty grid before the (async) scan completes.
        if (_missionsLoaded)
            Profile.MissionQueue = string.Join(";", _allMissions.Where(m => m.IsActive).Select(m => m.FullPboName));
    }

    partial void OnProfileChanged(ServerProfile? value) => OnPropertyChanged(nameof(HasActiveProfile));

    // ===================================================================== Save / Launch / Revert

    [RelayCommand]
    private async Task Save() => await SaveInternalAsync(showInfo: true);

    private async Task<bool> SaveInternalAsync(bool showInfo)
    {
        if (Profile is null) return false;
        var error = Validate();
        if (error is not null)
        {
            await _dialogs.ShowErrorAsync("Validation", error);
            return false;
        }
        SyncProfile();
        await _profiles.UpdateProfileAsync(Profile);
        _baselineJson = Serialize(Profile);

        // Make the saved copy the active instance (a clone, so later edits to the working copy don't
        // leak into it). The sidebar + overview refresh automatically via IProfileService.ProfilesChanged.
        _profiles.SetActiveProfile(Clone(Profile));
        ActiveProfileName = Profile.Name;

        if (showInfo) await _dialogs.ShowInfoAsync("Saved", $"Profile '{Profile.Name}' saved.");
        return true;
    }

    [RelayCommand]
    private async Task Launch()
    {
        if (Profile is null) return;
        if (IsDirty() && !await SaveInternalAsync(showInfo: false)) return;
        if (!await MissionDependencyGate.ConfirmAsync(_depChecker, _dialogs, Profile)) return;
        try
        {
            await _process.LaunchAsync(Profile);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Launch failed", ex.Message);
        }
    }

    [RelayCommand]
    private void Revert()
    {
        if (_profiles.ActiveProfile is not null) LoadWorkingCopy(_profiles.ActiveProfile);
    }

    /// <summary>Snapshots the profile's configs, keys, mission folder(s) and settings into a timestamped zip.</summary>
    [RelayCommand]
    private async Task Backup()
    {
        if (Profile is null) return;
        if (IsDirty() && !await SaveInternalAsync(showInfo: false)) return;   // capture the current state
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => BackupStatus = s);
            var zip = await _backups.CreateBackupAsync(Profile, progress);
            await _dialogs.ShowInfoAsync("Backup complete", $"Saved to:\n{zip}");
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Backup failed", ex.Message);
        }
        finally { IsBusy = false; BackupStatus = string.Empty; }
    }

    /// <summary>Restores configs/keys/missions from a chosen backup zip back into the server directory.</summary>
    [RelayCommand]
    private async Task Restore()
    {
        if (Profile is null) return;
        var root = _backups.GetBackupsRoot(Profile);
        var zip = await _dialogs.BrowseFileAsync("Choose a backup to restore",
            "ATLAS backup (*.zip)|*.zip", Directory.Exists(root) ? root : null);
        if (string.IsNullOrWhiteSpace(zip)) return;

        if (!await _dialogs.ConfirmAsync("Restore backup",
                "This overwrites the server's config files, Keys and mission folders with the contents of the "
                + "backup. The current files will be replaced. Continue?", "Restore", "Cancel"))
            return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => BackupStatus = s);
            await _backups.RestoreBackupAsync(zip, Profile, progress);
            await _dialogs.ShowInfoAsync("Restore complete", "The backup was restored to the server directory.");
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Restore failed", ex.Message);
        }
        finally { IsBusy = false; BackupStatus = string.Empty; }
    }

    private string? Validate()
    {
        if (Profile is null) return null;
        if (string.IsNullOrWhiteSpace(Profile.Name))
            return "Profile name is required.";
        if (!string.IsNullOrWhiteSpace(Profile.ServerExecutablePath) && !File.Exists(Profile.ServerExecutablePath))
            return "The server executable path does not exist on disk.";
        if (Profile.Port < AppConstants.MinUserPort || Profile.Port > AppConstants.MaxPort)
            return $"Game port must be between {AppConstants.MinUserPort} and {AppConstants.MaxPort}.";
        if (Profile.RconPort < AppConstants.MinUserPort || Profile.RconPort > AppConstants.MaxPort)
            return $"RCON port must be between {AppConstants.MinUserPort} and {AppConstants.MaxPort}.";
        if (Profile.RconPort >= AppConstants.ReservedPortRangeStart && Profile.RconPort <= AppConstants.ReservedPortRangeEnd)
            return $"RCON port must not be in the reserved range " +
                   $"{AppConstants.ReservedPortRangeStart}-{AppConstants.ReservedPortRangeEnd} (used by the game).";
        return null;
    }

    // ===================================================================== Previews

    [RelayCommand]
    private void RefreshPreview()
    {
        if (Profile is null) return;
        SyncProfile();
        ServerCfgPreview = _configGen.GenerateServerCfg(Profile);
        BasicCfgPreview = _configGen.GenerateBasicCfg(Profile);
        LaunchCommandPreview = _configGen.BuildLaunchArguments(Profile);
        Arma3ProfilePreview = _configGen.GenerateArma3Profile(Profile);
    }

    /// <summary>Fills the granular difficulty grid + AI from the selected <see cref="ServerProfile.MissionDifficulty"/>
    /// preset (Custom leaves it untouched). A plain working copy, so refresh the bindings afterwards.</summary>
    [RelayCommand]
    private void ApplyDifficultyPreset()
    {
        if (Profile is null) return;
        var p = Profile;
        switch ((p.MissionDifficulty ?? "Regular").Trim().ToLowerInvariant())
        {
            case "recruit":
                SetDiff(p, 2, 2, 2, 2, 2, 2, 2, 2, 1,
                    true, true, true, true, true, true, true, true, true, true, true, true, true, true, 0.4, 0.3);
                break;
            case "veteran":
                SetDiff(p, 0, 0, 0, 0, 1, 0, 1, 1, 0,
                    false, false, false, false, true, false, false, false, false, false, false, false, false, false, 0.8, 0.7);
                break;
            case "custom":
                MissionStatus = "Custom difficulty — grid left as-is.";
                return;
            default: // regular
                SetDiff(p, 1, 1, 0, 1, 1, 1, 2, 2, 1,
                    false, false, true, false, true, true, true, true, true, false, false, true, false, true, 0.6, 0.5);
                break;
        }
        OnPropertyChanged(nameof(Profile));
        MissionStatus = $"Loaded the {p.MissionDifficulty} preset into the difficulty grid.";
    }

    private static void SetDiff(ServerProfile p,
        int groupInd, int friendlyTags, int enemyTags, int detMines, int commands, int waypoints, int weaponInfo,
        int stance, int thirdPerson,
        bool reducedDmg, bool stamina, bool crosshair, bool visionAid, bool cameraShake, bool scoreTable, bool deathMsg,
        bool vonId, bool mapFriendly, bool mapEnemy, bool mapMines, bool autoReport, bool multiSaves, bool tacPing,
        double skill, double precision)
    {
        p.DiffGroupIndicators = groupInd; p.DiffFriendlyTags = friendlyTags; p.DiffEnemyTags = enemyTags;
        p.DiffDetectedMines = detMines; p.DiffCommands = commands; p.DiffWaypoints = waypoints;
        p.DiffWeaponInfo = weaponInfo; p.DiffStanceIndicator = stance; p.DiffThirdPersonView = thirdPerson;
        p.DiffReducedDamage = reducedDmg; p.DiffStaminaBar = stamina; p.DiffWeaponCrosshair = crosshair;
        p.DiffVisionAid = visionAid; p.DiffCameraShake = cameraShake; p.DiffScoreTable = scoreTable;
        p.DiffDeathMessages = deathMsg; p.DiffVonID = vonId; p.DiffMapContentFriendly = mapFriendly;
        p.DiffMapContentEnemy = mapEnemy; p.DiffMapContentMines = mapMines; p.DiffAutoReport = autoReport;
        p.DiffMultipleSaves = multiSaves; p.DiffTacticalPing = tacPing;
        p.SkillAI = skill; p.PrecisionAI = precision;
    }

    [RelayCommand]
    private void CopyServerCfg() => CopyToClipboard(ServerCfgPreview);

    [RelayCommand]
    private void CopyBasicCfg() => CopyToClipboard(BasicCfgPreview);

    [RelayCommand]
    private void CopyLaunchCommand() => CopyToClipboard(LaunchCommandPreview);

    private void CopyToClipboard(string text)
    {
        RefreshPreview();
        try { System.Windows.Clipboard.SetText(text ?? string.Empty); } catch { /* clipboard busy */ }
    }

    // ===================================================================== Browse helpers

    [RelayCommand]
    private async Task BrowseServerExe()
    {
        if (Profile is null) return;
        var path = await _dialogs.BrowseFileAsync("Select the Arma 3 server executable",
            "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? null : Profile.ServerDirectory);
        if (path is null) return;
        Profile.ServerExecutablePath = path;
        if (string.IsNullOrWhiteSpace(Profile.ServerDirectory))
            Profile.ServerDirectory = Path.GetDirectoryName(path) ?? string.Empty;
        OnPropertyChanged(nameof(Profile));
    }

    [RelayCommand]
    private async Task BrowseServerDir()
    {
        if (Profile is null) return;
        var path = await _dialogs.BrowseFolderAsync("Select the server directory",
            string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? null : Profile.ServerDirectory);
        if (path is not null) { Profile.ServerDirectory = path; OnPropertyChanged(nameof(Profile)); }
    }

    [RelayCommand]
    private async Task BrowseHeadlessExe()
    {
        if (Profile is null) return;
        var path = await _dialogs.BrowseFileAsync("Select the headless client executable",
            "Executables (*.exe)|*.exe|All files (*.*)|*.*", null);
        if (path is not null) { Profile.HeadlessClientExecutablePath = path; OnPropertyChanged(nameof(Profile)); }
    }

    [RelayCommand]
    private async Task BrowseMissionDir()
    {
        if (Profile is null) return;
        var start = !string.IsNullOrWhiteSpace(Profile.MissionDirectory)
            ? Profile.MissionDirectory
            : (string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? null : Profile.ServerDirectory);
        var path = await _dialogs.BrowseFolderAsync("Select the mission folder", start);
        if (path is not null)
        {
            Profile.MissionDirectory = path;
            OnPropertyChanged(nameof(Profile));
            await ReloadMissionsAsync();
        }
    }

    // ===================================================================== Extensions

    [RelayCommand]
    private void AddLoadExtension()
    {
        AddExt(LoadExtensions, NewLoadExtension);
        NewLoadExtension = string.Empty;
    }

    [RelayCommand]
    private void AddPreprocessExtension()
    {
        AddExt(PreprocessExtensions, NewPreprocessExtension);
        NewPreprocessExtension = string.Empty;
    }

    [RelayCommand]
    private void AddHtmlExtension()
    {
        AddExt(HtmlExtensions, NewHtmlExtension);
        NewHtmlExtension = string.Empty;
    }

    [RelayCommand]
    private void AddHtmlUri()
    {
        var e = NewHtmlUri?.Trim();
        if (!string.IsNullOrWhiteSpace(e) && !HtmlUris.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)))
            HtmlUris.Add(e);
        NewHtmlUri = string.Empty;
    }

    [RelayCommand] private void RemoveLoadExtension(string ext) => LoadExtensions.Remove(ext);
    [RelayCommand] private void RemovePreprocessExtension(string ext) => PreprocessExtensions.Remove(ext);
    [RelayCommand] private void RemoveHtmlExtension(string ext) => HtmlExtensions.Remove(ext);
    [RelayCommand] private void RemoveHtmlUri(string ext) => HtmlUris.Remove(ext);

    private static void AddExt(ObservableCollection<string> target, string? value)
    {
        var e = value?.Trim().TrimStart('.');
        if (!string.IsNullOrWhiteSpace(e) && !target.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)))
            target.Add(e);
    }

    // ===================================================================== Mods (selection / list building)

    partial void OnSelectedPresetChoiceChanged(PresetChoice? value)
    {
        if (_suppressPresetChange || value is null || Profile is null) return;
        if (value.Id is null)
        {
            Profile.ActiveModPresetId = null;
            _ = MergeLibraryModsAsync();   // back to manual: show the whole library again
        }
        else
        {
            Profile.ActiveModPresetId = value.Id;
            var preset = _presetCache.FirstOrDefault(p => p.Id == value.Id.Value);
            if (preset is not null)
            {
                Mods.Clear();
                foreach (var m in preset.Mods) Mods.Add(CopyMod(m));
            }
        }
        OnPropertyChanged(nameof(PresetInfo));
        RefreshModSummary();
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
        _suppressPresetChange = true;
        try { SelectedPresetChoice = current; }
        finally { _suppressPresetChange = false; }
        OnPropertyChanged(nameof(PresetInfo));
    }

    [RelayCommand]
    private void DetachPreset()
    {
        if (PresetChoices.Count > 0) SelectedPresetChoice = PresetChoices[0];
    }

    [RelayCommand]
    private async Task AddByWorkshop()
    {
        if (Profile is null) return;
        var input = await _dialogs.PromptAsync("Add Workshop Mod", "Workshop URL or numeric ID", string.Empty);
        if (string.IsNullOrWhiteSpace(input)) return;
        var id = ParseWorkshopId(input);
        if (id == 0) { await _dialogs.ShowErrorAsync("Invalid input", "Could not find a Workshop ID."); return; }
        AddOrActivate(new ArmaModEntry { WorkshopId = id, Name = $"Workshop {id}", FolderName = $"@{id}" });
    }

    [RelayCommand]
    private async Task AddLocal()
    {
        if (Profile is null) return;
        var folder = await _dialogs.BrowseFolderAsync("Select local mod folder (@ModName)");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var folderName = new DirectoryInfo(folder).Name;
        AddOrActivate(new ArmaModEntry
        {
            WorkshopId = 0,
            IsLocal = true,
            LocalPath = folder,
            Name = folderName.TrimStart('@'),
            FolderName = folderName.StartsWith('@') ? folderName : "@" + folderName,
        });
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
            foreach (var mod in imported) AddOrActivate(mod);
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
        RefreshModSummary();
    }

    [RelayCommand]
    private void MoveModUp()
    {
        var i = SelectedMod is null ? -1 : Mods.IndexOf(SelectedMod);
        if (i > 0) Mods.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveModDown()
    {
        var i = SelectedMod is null ? -1 : Mods.IndexOf(SelectedMod);
        if (i >= 0 && i < Mods.Count - 1) Mods.Move(i, i + 1);
    }

    [RelayCommand]
    private void OpenInWorkshop()
    {
        if (SelectedMod is null || SelectedMod.WorkshopId == 0) return;
        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={SelectedMod.WorkshopId}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void RefreshModSummary()
    {
        var active = Mods.Count(IsModActive);
        var total = Mods.Count;
        ModSummary = $"{active} active / {total} in library";
    }

    /// <summary>Adds every library mod not already on this profile to the grid as an inactive row, so the profile
    /// Mods tab shows the whole library and you activate per profile via the checkboxes. Manual mode only.</summary>
    private async Task MergeLibraryModsAsync()
    {
        if (Profile is null || Profile.ActiveModPresetId is not null) return;
        List<ArmaModEntry> library;
        try { library = await _modLibrary.GetAllModsAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Could not load the global mod library for the profile Mods tab."); return; }

        var present = new HashSet<string>(Mods.Select(ModKey), StringComparer.OrdinalIgnoreCase);
        foreach (var lib in library)
        {
            if (!present.Add(ModKey(lib))) continue;   // already on the profile, or a duplicate library row
            // Shown for selection but not active for this profile until the user ticks a box.
            lib.EnabledForServer = false;
            lib.EnabledForClient = false;
            lib.EnabledForHeadless = false;
            lib.IsServerOnly = false;
            lib.IsHeadlessOnly = false;
            lib.IsOptional = false;
            Mods.Add(lib);
        }
        RefreshModSummary();
    }

    /// <summary>Stable identity for a mod: Workshop id when present, else folder name, else display name.</summary>
    private static string ModKey(ArmaModEntry m) =>
        m.WorkshopId != 0 ? "w:" + m.WorkshopId
        : !string.IsNullOrWhiteSpace(m.FolderName) ? "f:" + m.FolderName.ToLowerInvariant()
        : "n:" + (m.Name ?? string.Empty).ToLowerInvariant();

    /// <summary>A mod is active for the profile when any runtime role (server/client/headless) is ticked.</summary>
    private static bool IsModActive(ArmaModEntry m) => m.EnabledForServer || m.EnabledForClient || m.EnabledForHeadless;

    /// <summary>Activates a mod for this profile: ticks the existing library row if present (re-inserted so the
    /// grid checkbox refreshes — ArmaModEntry isn't observable), otherwise adds it as a new active row.</summary>
    private void AddOrActivate(ArmaModEntry mod)
    {
        var key = ModKey(mod);
        var existing = Mods.FirstOrDefault(m => string.Equals(ModKey(m), key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var i = Mods.IndexOf(existing);
            existing.EnabledForServer = true;
            Mods.RemoveAt(i);
            Mods.Insert(i, existing);
            SelectedMod = existing;
        }
        else
        {
            mod.LoadOrder = Mods.Count;
            Mods.Add(mod);
            SelectedMod = mod;
        }
        RefreshModSummary();
    }

    // ===================================================================== Missions

    partial void OnMissionSearchTextChanged(string value) => ApplyMissionFilter();
    partial void OnSelectedTerrainChanged(string value) => ApplyMissionFilter();

    [RelayCommand]
    private async Task ReloadMissionsAsync()
    {
        foreach (var m in _allMissions) m.PropertyChanged -= OnMissionItemChanged;
        _allMissions.Clear();
        _missionsLoaded = false;
        _loadingMissions = true;
        try
        {
            var hasFolder = Profile is not null &&
                (!string.IsNullOrWhiteSpace(Profile.ServerDirectory) || !string.IsNullOrWhiteSpace(Profile.MissionDirectory));
            if (Profile is null || !hasFolder)
            {
                Missions.Clear();
                Terrains.Clear();
                MissionStatus = "No server / mission folder set.";
                UpdateQueueSummary();
                return;
            }

            IsBusy = true;
            try
            {
                var list = await _missions.ScanMissionsAsync(Profile.ServerDirectory, Profile.MissionDirectory);
                foreach (var m in list) m.PropertyChanged += OnMissionItemChanged;
                _allMissions.AddRange(list);
                RebuildTerrains();
                MarkQueuedMissions();
                ApplyMissionFilter();
                _missionsLoaded = true;
                MissionStatus = $"{_allMissions.Count} mission(s).";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to scan missions in {Dir}.", Profile.ServerDirectory);
                MissionStatus = "Failed to scan missions.";
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _loadingMissions = false;
        }
    }

    [RelayCommand]
    private void ClearMissionQueue()
    {
        _loadingMissions = true;
        foreach (var m in _allMissions) m.IsActive = false;
        _loadingMissions = false;
        UpdateQueueSummary();
        RefreshPreview();
    }

    /// <summary>A mission's checkbox toggled: refresh the rotation summary + the config preview
    /// (RefreshPreview → SyncProfile flushes the queue into the working copy).</summary>
    private void OnMissionItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loadingMissions) return;
        if (e.PropertyName == nameof(MissionInfo.IsActive))
        {
            UpdateQueueSummary();
            RefreshPreview();
        }
    }

    private void UpdateQueueSummary()
    {
        var names = _allMissions.Where(m => m.IsActive).Select(m => m.MissionName).ToList();
        QueueSummary = names.Count == 0
            ? "No missions checked — the single mission below is used (if set)."
            : $"Rotation ({names.Count}): " + string.Join(", ", names);
    }

    partial void OnSelectedMissionChanged(MissionInfo? value) =>
        ShowMissionDependenciesCommand.NotifyCanExecuteChanged();

    private bool CanShowMissionDependencies => SelectedMission is not null;

    /// <summary>Parses the selected mission's mission.sqm (packed or unpacked) and shows its required
    /// addOns[] list in a small read-only window.</summary>
    [RelayCommand(CanExecute = nameof(CanShowMissionDependencies))]
    private async Task ShowMissionDependenciesAsync()
    {
        if (Profile is null || SelectedMission is null) return;

        // Resolve the mission's on-disk path the same way OpenMissionFolder does: the override folder when
        // set, else <ServerDirectory>\<SourceFolder> (MPMissions/Missions).
        var dir = !string.IsNullOrWhiteSpace(Profile.MissionDirectory) && Directory.Exists(Profile.MissionDirectory)
            ? Profile.MissionDirectory
            : Path.Combine(Profile.ServerDirectory ?? string.Empty, SelectedMission.SourceFolder);
        var path = Path.Combine(dir, SelectedMission.PboFileName);

        try
        {
            var addOns = await _missions.GetMissionDependenciesAsync(path);
            var window = new MissionDependenciesWindow(SelectedMission.FullPboName, addOns)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read mission dependencies from {Path}.", path);
            await _dialogs.ShowErrorAsync("Dependencies", $"Could not read the mission's addOns list: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenMissionFolder()
    {
        if (Profile is null) return;
        string? target = null;
        if (!string.IsNullOrWhiteSpace(Profile.MissionDirectory) && Directory.Exists(Profile.MissionDirectory))
            target = Profile.MissionDirectory;
        else if (!string.IsNullOrWhiteSpace(Profile.ServerDirectory))
        {
            target = Profile.ServerDirectory;
            var sub = SelectedMission?.SourceFolder;
            if (!string.IsNullOrWhiteSpace(sub))
            {
                var candidate = Path.Combine(Profile.ServerDirectory, sub);
                if (Directory.Exists(candidate)) target = candidate;
            }
        }
        if (string.IsNullOrWhiteSpace(target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    private void RebuildTerrains()
    {
        Terrains.Clear();
        Terrains.Add("All");
        foreach (var t in _allMissions.Select(m => m.Terrain)
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            Terrains.Add(t);
        if (!Terrains.Contains(SelectedTerrain)) SelectedTerrain = "All";
    }

    private void ApplyMissionFilter()
    {
        Missions.Clear();
        foreach (var m in _allMissions)
        {
            var matchesSearch = string.IsNullOrWhiteSpace(MissionSearchText)
                || m.MissionName.Contains(MissionSearchText, StringComparison.OrdinalIgnoreCase)
                || m.Terrain.Contains(MissionSearchText, StringComparison.OrdinalIgnoreCase);
            var matchesTerrain = SelectedTerrain == "All"
                || string.Equals(m.Terrain, SelectedTerrain, StringComparison.OrdinalIgnoreCase);
            if (matchesSearch && matchesTerrain) Missions.Add(m);
        }
    }

    private void MarkQueuedMissions()
    {
        var queue = (Profile?.MissionQueue ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Legacy fallback: a profile saved before the queue existed only has the single MissionName — treat it as
        // the one checked mission so nothing is lost.
        if (queue.Count == 0 && !string.IsNullOrWhiteSpace(Profile?.MissionName))
            queue.Add(Profile!.MissionName.Trim());
        foreach (var m in _allMissions)
            m.IsActive = queue.Contains(m.FullPboName);
        UpdateQueueSummary();
    }

    // ===================================================================== Small helpers

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (var s in source) target.Add(s);
    }

    private static ulong ParseWorkshopId(string input)
    {
        var match = Regex.Match(input, @"\d{6,}");
        return match.Success && ulong.TryParse(match.Value, out var id) ? id : 0;
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

    private static ServerProfile Clone(ServerProfile p) =>
        JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(p))!;

    private static string Serialize(ServerProfile p) => JsonSerializer.Serialize(p);
}
