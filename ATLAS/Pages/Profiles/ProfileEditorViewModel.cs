using Atlas.Core.Models;
using Atlas.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.Profiles;

/// <summary>
/// Edits a working copy of a <see cref="ServerProfile"/>. The dialog binds directly to
/// <see cref="Profile"/> (a POCO); on Save the input is validated and <see cref="CloseRequested"/>
/// fires with the result. The owner (ProfilesViewModel) then persists the copy.
/// </summary>
public partial class ProfileEditorViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IConfigGeneratorService _configGen;

    public ServerProfile Profile { get; }
    public bool IsNew { get; }
    public string DialogTitle { get; }

    /// <summary>Raised when the dialog should close. true = saved, false = cancelled.</summary>
    public event Action<bool>? CloseRequested;

    public int[] ExThreadsOptions { get; } = { 1, 3, 5, 7 };
    public int[] VerifySignaturesOptions { get; } = { 0, 1, 2 };
    public int[] BandwidthAlgOptions { get; } = { 1, 2 };
    public string[] DifficultyOptions { get; } = { "Recruit", "Regular", "Veteran", "Custom" };

    [ObservableProperty] private string _motdText;

    private readonly List<ModPreset> _presets;

    /// <summary>Mod-preset choices for the General tab dropdown ("(None)" + all presets).</summary>
    public List<PresetChoice> PresetChoices { get; }

    [ObservableProperty] private PresetChoice _selectedPresetChoice;

    public string PresetInfo => SelectedPresetChoice?.Id is null
        ? "Manual mod list — edit mods on the Mods page."
        : $"Managed by preset: {SelectedPresetChoice.Name}. Edit it on the Mod Presets page.";

    public ProfileEditorViewModel(ServerProfile profile, bool isNew, IDialogService dialogs, List<ModPreset> presets, IConfigGeneratorService configGen)
    {
        Profile = profile;
        IsNew = isNew;
        _dialogs = dialogs;
        _presets = presets;
        _configGen = configGen;
        DialogTitle = isNew ? "New Profile" : $"Edit Profile — {profile.Name}";
        _motdText = string.Join(Environment.NewLine, profile.MotdLines);

        PresetChoices = new List<PresetChoice> { new() { Id = null, Name = "(None — Manual Mod List)" } };
        PresetChoices.AddRange(presets.Select(p => new PresetChoice { Id = p.Id, Name = p.Name }));
        _selectedPresetChoice = PresetChoices.FirstOrDefault(c => c.Id == profile.ActiveModPresetId)
                                ?? PresetChoices[0];
    }

    partial void OnSelectedPresetChoiceChanged(PresetChoice value)
    {
        if (value is null) return;
        if (value.Id is null)
        {
            Profile.ActiveModPresetId = null;
        }
        else
        {
            Profile.ActiveModPresetId = value.Id;
            var preset = _presets.FirstOrDefault(p => p.Id == value.Id.Value);
            if (preset is not null) Profile.Mods = preset.Mods.Select(CopyMod).ToList();
        }
        OnPropertyChanged(nameof(PresetInfo));
    }

    [RelayCommand]
    private void DetachPreset() => SelectedPresetChoice = PresetChoices[0];

    private static ArmaModEntry CopyMod(ArmaModEntry m) => new()
    {
        WorkshopId = m.WorkshopId, Name = m.Name, FolderName = m.FolderName, LocalPath = m.LocalPath,
        IsLocal = m.IsLocal, Version = m.Version, SteamFileSize = m.SteamFileSize, LastUpdated = m.LastUpdated,
        LastChecked = m.LastChecked, UpdateAvailable = m.UpdateAvailable, LoadOrder = m.LoadOrder,
        EnabledForServer = m.EnabledForServer, EnabledForClient = m.EnabledForClient,
        EnabledForHeadless = m.EnabledForHeadless, IsOptional = m.IsOptional, IsServerOnly = m.IsServerOnly,
        IsHeadlessOnly = m.IsHeadlessOnly,
    };

    [RelayCommand]
    private async Task BrowseServerExe()
    {
        var path = await _dialogs.BrowseFileAsync("Select the Arma 3 server executable",
            "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? null : Profile.ServerDirectory);
        if (path is not null)
        {
            Profile.ServerExecutablePath = path;
            if (string.IsNullOrWhiteSpace(Profile.ServerDirectory))
                Profile.ServerDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            OnPropertyChanged(nameof(Profile));
        }
    }

    [RelayCommand]
    private async Task BrowseServerDir()
    {
        var path = await _dialogs.BrowseFolderAsync("Select the server directory",
            string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? null : Profile.ServerDirectory);
        if (path is not null) { Profile.ServerDirectory = path; OnPropertyChanged(nameof(Profile)); }
    }

    [RelayCommand]
    private async Task BrowseHeadlessExe()
    {
        var path = await _dialogs.BrowseFileAsync("Select the headless client executable",
            "Executables (*.exe)|*.exe|All files (*.*)|*.*", null);
        if (path is not null) { Profile.HeadlessClientExecutablePath = path; OnPropertyChanged(nameof(Profile)); }
    }

    [RelayCommand]
    private async Task TestLaunchArgs()
        => await _dialogs.ShowInfoAsync("Launch Command Preview", BuildPreview());

    [RelayCommand]
    private async Task Save()
    {
        var error = Validate();
        if (error is not null)
        {
            await _dialogs.ShowErrorAsync("Validation", error);
            return;
        }
        Profile.MotdLines = MotdText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private string? Validate()
    {
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

    private string BuildPreview()
    {
        return _configGen.BuildLaunchArguments(Profile);
    }
}
