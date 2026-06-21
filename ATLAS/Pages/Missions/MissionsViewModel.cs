using System.Collections.ObjectModel;
using System.Diagnostics;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Missions;

/// <summary>
/// Browses the active profile's mission folder (filesystem scan of <c>MPMissions</c>/<c>Missions</c>), lets the
/// user filter/search the mission list, set the active mission, and edit the mission parameters that live on the
/// profile (Difficulty/AutoInit/Persistent). Transient view model — snapshots the active profile in the
/// constructor and never subscribes to ActiveProfileChanged (same rationale as ModsViewModel).
/// </summary>
public partial class MissionsViewModel : BaseViewModel
{
    private readonly IMissionService _missions;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;

    /// <summary>Unfiltered scan results; <see cref="Missions"/> is the filtered view shown in the grid.</summary>
    private readonly List<MissionInfo> _all = new();

    [ObservableProperty] private ServerProfile? _profile;
    public bool HasActiveProfile => Profile is not null;

    public ObservableCollection<MissionInfo> Missions { get; } = new();

    [ObservableProperty] private MissionInfo? _selectedMission;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedTerrain = "All";
    public ObservableCollection<string> Terrains { get; } = new();
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Arma 3 mission difficulty presets bound by the params panel combo.</summary>
    public string[] DifficultyOptions { get; } = { "Recruit", "Regular", "Veteran", "Custom" };

    public MissionsViewModel(IMissionService missions, IProfileService profiles, IDialogService dialogs)
    {
        _missions = missions;
        _profiles = profiles;
        _dialogs = dialogs;
        Title = "Missions";
        _ = LoadAsync();
    }

    // ----- Property hooks -----

    partial void OnProfileChanged(ServerProfile? value) => OnPropertyChanged(nameof(HasActiveProfile));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTerrainChanged(string value) => ApplyFilter();

    // ----- Commands -----

    [RelayCommand]
    private async Task LoadAsync()
    {
        Profile = _profiles.ActiveProfile;
        _all.Clear();

        var hasFolder = Profile is not null &&
            (!string.IsNullOrWhiteSpace(Profile.ServerDirectory) || !string.IsNullOrWhiteSpace(Profile.MissionDirectory));
        if (Profile is null || !hasFolder)
        {
            Missions.Clear();
            Terrains.Clear();
            StatusMessage = "No active profile / mission folder.";
            return;
        }

        IsBusy = true;
        try
        {
            var list = await _missions.ScanMissionsAsync(Profile.ServerDirectory, Profile.MissionDirectory);
            _all.AddRange(list);

            RebuildTerrains();
            MarkActive();
            ApplyFilter();
            StatusMessage = $"{_all.Count} mission(s).";
        }
        catch (Exception ex)
        {
            // LoadAsync is fire-and-forget from the constructor — never let an exception escape unobserved.
            Log.Error(ex, "Failed to scan missions in {Dir}.", Profile.ServerDirectory);
            StatusMessage = "Failed to load missions.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetActiveMission()
    {
        if (SelectedMission is null || Profile is null) return;

        // Mutate in memory, then persist — roll back the in-memory change if the save fails so the UI
        // never claims a mission is active that was never written to the database.
        var previous = Profile.MissionName;
        Profile.MissionName = SelectedMission.FullPboName;
        try
        {
            await _profiles.UpdateProfileAsync(Profile);
        }
        catch (Exception ex)
        {
            Profile.MissionName = previous;
            Log.Error(ex, "Failed to set active mission.");
            await _dialogs.ShowErrorAsync("Save failed", "Could not save the active mission.");
            return;
        }
        MarkActive();
        ApplyFilter();
        await _dialogs.ShowInfoAsync("Active mission", $"Set to {SelectedMission.FullPboName}.");
    }

    [RelayCommand]
    private void OpenMissionFolder()
    {
        if (Profile is null) return;

        string? target = null;
        if (!string.IsNullOrWhiteSpace(Profile.MissionDirectory) && Directory.Exists(Profile.MissionDirectory))
        {
            target = Profile.MissionDirectory;
        }
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
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* ignore launch failures */ }
    }

    // ----- Private helpers -----

    private void RebuildTerrains()
    {
        Terrains.Clear();
        Terrains.Add("All");
        foreach (var t in _all
                     .Select(m => m.Terrain)
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            Terrains.Add(t);

        if (!Terrains.Contains(SelectedTerrain)) SelectedTerrain = "All";
    }

    private void ApplyFilter()
    {
        Missions.Clear();
        foreach (var m in _all)
        {
            var matchesSearch = string.IsNullOrWhiteSpace(SearchText)
                || m.MissionName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || m.Terrain.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            var matchesTerrain = SelectedTerrain == "All"
                || string.Equals(m.Terrain, SelectedTerrain, StringComparison.OrdinalIgnoreCase);

            if (matchesSearch && matchesTerrain) Missions.Add(m);
        }
    }

    private void MarkActive()
    {
        foreach (var m in _all)
            m.IsActive = string.Equals(m.FullPboName, Profile?.MissionName, StringComparison.OrdinalIgnoreCase);
    }
}
