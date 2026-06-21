using System.Collections.ObjectModel;
using System.Text.Json;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.Profiles;

/// <summary>Profile library: list, create, edit, clone, delete, set default, activate, import/export.</summary>
public partial class ProfilesViewModel : BaseViewModel
{
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly IProfileEditorLauncher _editor;
    private readonly ISettingsService _settings;
    private readonly IArmaInstallLocator _arma;

    public ObservableCollection<ServerProfile> Profiles { get; } = new();

    [ObservableProperty] private ServerProfile? _selectedProfile;

    /// <summary>Id of the active profile (or -1). Used by the list to highlight the active row.</summary>
    public int ActiveProfileId => _profiles.ActiveProfile?.Id ?? -1;

    public ProfilesViewModel(IProfileService profiles, IDialogService dialogs, IProfileEditorLauncher editor,
        ISettingsService settings, IArmaInstallLocator arma)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        _editor = editor;
        _settings = settings;
        _arma = arma;
        Title = "Profiles";

        // This view model is transient: a fresh instance is created on every navigation, and
        // LoadAsync below reads the current active profile, so the active-row highlight is correct
        // on entry. We deliberately do NOT subscribe to ActiveProfileChanged — that would leak this
        // instance into the long-lived IProfileService singleton with no way to unsubscribe
        // (NavigationService does not invoke the navigation lifecycle hooks). The only thing that can
        // change the active profile while this page is visible is the Activate command below, which
        // refreshes the highlight locally.
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var all = await _profiles.GetAllProfilesAsync();
            var selectedId = SelectedProfile?.Id;
            Profiles.Clear();
            foreach (var p in all) Profiles.Add(p);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NewProfile()
    {
        var profile = new ServerProfile { Name = await UniqueDefaultNameAsync() };
        PrefillServerPaths(profile);
        if (await _editor.EditAsync(profile, isNew: true))
        {
            var created = await _profiles.CreateProfileAsync(profile);
            await LoadAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == created.Id);
        }
    }

    [RelayCommand]
    private async Task EditProfile()
    {
        if (SelectedProfile is null) return;
        var working = Clone(SelectedProfile);
        if (await _editor.EditAsync(working, isNew: false))
        {
            await _profiles.UpdateProfileAsync(working);
            await LoadAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == working.Id);
        }
    }

    [RelayCommand]
    private async Task CloneProfile()
    {
        if (SelectedProfile is null) return;
        var name = await _dialogs.PromptAsync("Clone Profile", "New profile name",
            SelectedProfile.Name + " (copy)");
        if (string.IsNullOrWhiteSpace(name)) return;
        var clone = await _profiles.CloneProfileAsync(SelectedProfile.Id, name);
        await LoadAsync();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == clone.Id);
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (SelectedProfile is null) return;
        if (!await _dialogs.ConfirmAsync("Delete Profile",
                $"Delete '{SelectedProfile.Name}'? This cannot be undone.", "Delete", "Cancel"))
            return;
        await _profiles.DeleteProfileAsync(SelectedProfile.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SetDefault()
    {
        if (SelectedProfile is null) return;
        await _profiles.SetDefaultProfileAsync(SelectedProfile.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private void Activate()
    {
        if (SelectedProfile is null) return;
        _profiles.SetActiveProfile(SelectedProfile);
        OnPropertyChanged(nameof(ActiveProfileId)); // move the list highlight to the newly active profile
    }

    [RelayCommand]
    private async Task ExportProfile()
    {
        if (SelectedProfile is null) return;
        var path = await _dialogs.SaveFileAsync("Export Profile",
            "ATLAS Profile (*.atlasprofile)|*.atlasprofile", SelectedProfile.Name + ".atlasprofile");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _profiles.ExportProfileAsync(SelectedProfile.Id, path);
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
            var imported = await _profiles.ImportProfileAsync(path);
            await LoadAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == imported.Id);
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

    private static ServerProfile Clone(ServerProfile profile)
    {
        var json = JsonSerializer.Serialize(profile);
        return JsonSerializer.Deserialize<ServerProfile>(json)!;
    }
}
