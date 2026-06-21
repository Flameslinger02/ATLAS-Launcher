using System.Collections.ObjectModel;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.Profiles;

/// <summary>
/// Profile navigation + management — the single source for the sidebar profile list AND the
/// all-profiles overview page (both bind to this one singleton instance, so they always agree).
/// Owns the profile list, per-profile commands (open/clone/delete/set-default/export) plus new/import,
/// and reloads itself whenever the profile set changes (<see cref="IProfileService.ProfilesChanged"/>).
/// </summary>
public partial class ProfilesViewModel : BaseViewModel
{
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ISettingsService _settings;
    private readonly IArmaInstallLocator _arma;

    public ObservableCollection<ServerProfile> Profiles { get; } = new();

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>Id of the active profile (or -1) — used to highlight the active row.</summary>
    public int ActiveProfileId => _profiles.ActiveProfile?.Id ?? -1;

    public ProfilesViewModel(IProfileService profiles, IDialogService dialogs, INavigationService navigation,
        ISettingsService settings, IArmaInstallLocator arma)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        _navigation = navigation;
        _settings = settings;
        _arma = arma;
        Title = "Profiles";

        // Singleton (app-lifetime): safe to subscribe without unsubscribing. Keeps the sidebar + overview
        // in sync with any change, including a Save made from the profile editor.
        _profiles.ProfilesChanged += (_, _) => _ = LoadAsync();
        _profiles.ActiveProfileChanged += (_, _) => OnPropertyChanged(nameof(ActiveProfileId));

        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var all = await _profiles.GetAllProfilesAsync();
            Profiles.Clear();
            foreach (var p in all) Profiles.Add(p);
            OnPropertyChanged(nameof(ActiveProfileId));
            OnPropertyChanged(nameof(HasProfiles));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Clicking a profile sets it active and opens its editor (after an unsaved-edit check).</summary>
    [RelayCommand]
    private async Task OpenProfile(ServerProfile? profile)
    {
        if (profile is null) return;
        if (!await _navigation.ConfirmLeaveAsync()) return;
        _profiles.SetActiveProfile(profile);
        _navigation.NavigateTo(AppConstants.Pages.ProfileWorkspace);
    }

    [RelayCommand]
    private async Task NewProfile()
    {
        if (!await _navigation.ConfirmLeaveAsync()) return;
        var profile = new ServerProfile { Name = await UniqueDefaultNameAsync() };
        PrefillServerPaths(profile);
        var created = await _profiles.CreateProfileAsync(profile);
        _profiles.SetActiveProfile(created);
        _navigation.NavigateTo(AppConstants.Pages.ProfileWorkspace);
    }

    [RelayCommand]
    private async Task CloneProfile(ServerProfile? profile)
    {
        if (profile is null) return;
        var name = await _dialogs.PromptAsync("Clone Profile", "New profile name", profile.Name + " (copy)");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _profiles.CloneProfileAsync(profile.Id, name);
    }

    [RelayCommand]
    private async Task DeleteProfile(ServerProfile? profile)
    {
        if (profile is null) return;
        if (!await _dialogs.ConfirmAsync("Delete Profile",
                $"Delete '{profile.Name}'? This cannot be undone.", "Delete", "Cancel"))
            return;
        await _profiles.DeleteProfileAsync(profile.Id);
    }

    [RelayCommand]
    private async Task SetDefault(ServerProfile? profile)
    {
        if (profile is null) return;
        await _profiles.SetDefaultProfileAsync(profile.Id);
    }

    [RelayCommand]
    private async Task ExportProfile(ServerProfile? profile)
    {
        if (profile is null) return;
        var path = await _dialogs.SaveFileAsync("Export Profile",
            "ATLAS Profile (*.atlasprofile)|*.atlasprofile", profile.Name + ".atlasprofile");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _profiles.ExportProfileAsync(profile.Id, path);
            await _dialogs.ShowInfoAsync("Export", $"Profile exported to:\n{path}");
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportProfile()
    {
        var path = await _dialogs.BrowseFileAsync("Import Profile",
            "ATLAS Profile (*.atlasprofile)|*.atlasprofile|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _profiles.ImportProfileAsync(path);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Import failed", ex.Message);
        }
    }

    /// <summary>Pre-fills a new profile's server directory/exe from the global Arma install (Settings or auto-detect).</summary>
    private void PrefillServerPaths(ServerProfile profile)
    {
        var dir = !string.IsNullOrWhiteSpace(_settings.Settings.ArmaServerDirectory)
            ? _settings.Settings.ArmaServerDirectory
            : _arma.FindServerDirectory();
        if (string.IsNullOrWhiteSpace(dir)) return;

        profile.ServerDirectory = dir!;
        var exe = ArmaInstallLocator.FindServerExecutable(dir);
        if (exe is not null) profile.ServerExecutablePath = exe;
    }

    private async Task<string> UniqueDefaultNameAsync()
    {
        var existing = (await _profiles.GetAllProfilesAsync())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains("New Profile")) return "New Profile";
        for (var i = 2; ; i++)
            if (!existing.Contains($"New Profile ({i})")) return $"New Profile ({i})";
    }
}
