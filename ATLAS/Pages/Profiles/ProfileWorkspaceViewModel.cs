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

    /// <summary>The working copy currently being edited (a clone of the active profile).</summary>
    [ObservableProperty] private ServerProfile? _profile;
    [ObservableProperty] private string _activeProfileName = "No active profile";

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
        IDialogService dialogs)
    {
        _profiles = profiles;
        _missions = missions;
        _presets = presets;
        _configGen = configGen;
        _process = process;
        _dialogs = dialogs;
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
        Profile.Mods = Mods.ToList();
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
        Mods.Add(new ArmaModEntry { WorkshopId = id, Name = $"Workshop {id}", FolderName = $"@{id}", LoadOrder = Mods.Count });
        RefreshModSummary();
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
        RefreshModSummary();
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
            foreach (var mod in imported) { mod.LoadOrder = Mods.Count; Mods.Add(mod); }
            RefreshModSummary();
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
        var count = Mods.Count;
        ModSummary = $"{count} mod{(count == 1 ? "" : "s")}";
    }

    // ===================================================================== Missions

    partial void OnMissionSearchTextChanged(string value) => ApplyMissionFilter();
    partial void OnSelectedTerrainChanged(string value) => ApplyMissionFilter();

    [RelayCommand]
    private async Task ReloadMissionsAsync()
    {
        _allMissions.Clear();
        var hasFolder = Profile is not null &&
            (!string.IsNullOrWhiteSpace(Profile.ServerDirectory) || !string.IsNullOrWhiteSpace(Profile.MissionDirectory));
        if (Profile is null || !hasFolder)
        {
            Missions.Clear();
            Terrains.Clear();
            MissionStatus = "No server / mission folder set.";
            return;
        }

        IsBusy = true;
        try
        {
            var list = await _missions.ScanMissionsAsync(Profile.ServerDirectory, Profile.MissionDirectory);
            _allMissions.AddRange(list);
            RebuildTerrains();
            MarkActiveMission();
            ApplyMissionFilter();
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

    [RelayCommand]
    private void SetActiveMission()
    {
        if (SelectedMission is null || Profile is null) return;
        Profile.MissionName = SelectedMission.FullPboName;
        MarkActiveMission();
        ApplyMissionFilter();
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

    private void MarkActiveMission()
    {
        foreach (var m in _allMissions)
            m.IsActive = string.Equals(m.FullPboName, Profile?.MissionName, StringComparison.OrdinalIgnoreCase);
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
