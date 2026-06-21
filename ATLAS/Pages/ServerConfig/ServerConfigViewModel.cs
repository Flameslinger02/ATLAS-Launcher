using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.ServerConfig;

/// <summary>
/// Server Configuration editor. Edits the in-memory active profile, previews the generated
/// <c>server.cfg</c> and launch command via <see cref="IConfigGeneratorService"/>, and can write
/// the config files to disk or persist the profile.
/// </summary>
public partial class ServerConfigViewModel : BaseViewModel
{
    private readonly IProfileService _profiles;
    private readonly IConfigGeneratorService _configGen;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private ServerProfile? _profile;
    public bool HasActiveProfile => Profile is not null;

    [ObservableProperty] private string _motdText = string.Empty;
    [ObservableProperty] private int _selectedTabIndex;            // Preview = index 7
    [ObservableProperty] private string _serverCfgPreview = string.Empty;
    [ObservableProperty] private string _launchCommandPreview = string.Empty;
    [ObservableProperty] private int _rconPort;                    // live-warning mirror

    public bool ShowRconPortWarning =>
        RconPort >= AppConstants.ReservedPortRangeStart && RconPort <= AppConstants.ReservedPortRangeEnd;

    public int[] VerifySignaturesOptions { get; } = { 0, 1, 2 };
    public int[] ExThreadsOptions { get; } = { 1, 3, 5, 7 };
    public int[] BandwidthAlgOptions { get; } = { 1, 2 };
    public string[] DifficultyOptions { get; } = { "Recruit", "Regular", "Veteran", "Custom" };

    public ObservableCollection<string> LoadExtensions { get; } = new();
    public ObservableCollection<string> PreprocessExtensions { get; } = new();
    public ObservableCollection<string> HtmlExtensions { get; } = new();
    public ObservableCollection<string> HtmlUris { get; } = new();

    [ObservableProperty] private string _newLoadExtension = string.Empty;
    [ObservableProperty] private string _newPreprocessExtension = string.Empty;
    [ObservableProperty] private string _newHtmlExtension = string.Empty;
    [ObservableProperty] private string _newHtmlUri = string.Empty;

    public ServerConfigViewModel(IProfileService profiles, IConfigGeneratorService configGen, IDialogService dialogs)
    {
        _profiles = profiles;
        _configGen = configGen;
        _dialogs = dialogs;
        Title = "Server Config";
        // This view model is transient: a fresh instance is created on every navigation to the page,
        // so loading the active profile in the constructor is sufficient. We deliberately do NOT
        // subscribe to ActiveProfileChanged — the active profile can only change from the Profiles
        // page (i.e. after navigating away), and subscribing here would leak this instance into the
        // long-lived IProfileService singleton with no way to unsubscribe (navigation lifecycle hooks
        // are not invoked by NavigationService).
        LoadActiveProfile();
    }

    partial void OnProfileChanged(ServerProfile? value) => OnPropertyChanged(nameof(HasActiveProfile));

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 7) RefreshPreview();
    }

    partial void OnRconPortChanged(int value)
    {
        if (Profile is not null) Profile.RconPort = value;
        OnPropertyChanged(nameof(ShowRconPortWarning));
    }

    private void LoadActiveProfile()
    {
        Profile = _profiles.ActiveProfile;
        if (Profile is null)
        {
            MotdText = string.Empty;
            LoadExtensions.Clear();
            PreprocessExtensions.Clear();
            HtmlExtensions.Clear();
            HtmlUris.Clear();
            return;
        }

        MotdText = string.Join(Environment.NewLine, Profile.MotdLines);
        RconPort = Profile.RconPort;

        LoadExtensions.Clear();
        foreach (var e in Profile.AllowedLoadFileExtensions) LoadExtensions.Add(e);
        PreprocessExtensions.Clear();
        foreach (var e in Profile.AllowedPreprocessFileExtensions) PreprocessExtensions.Add(e);
        HtmlExtensions.Clear();
        foreach (var e in Profile.AllowedHTMLLoadExtensions) HtmlExtensions.Add(e);
        HtmlUris.Clear();
        foreach (var u in Profile.AllowedHTMLLoadURIs) HtmlUris.Add(u);

        RefreshPreview();
    }

    private void SyncProfile()
    {
        if (Profile is null) return;
        Profile.MotdLines = MotdText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();
        Profile.AllowedLoadFileExtensions = LoadExtensions.ToList();
        Profile.AllowedPreprocessFileExtensions = PreprocessExtensions.ToList();
        Profile.AllowedHTMLLoadExtensions = HtmlExtensions.ToList();
        Profile.AllowedHTMLLoadURIs = HtmlUris.ToList();
    }

    [RelayCommand]
    private void RefreshPreview()
    {
        if (Profile is null) return;
        SyncProfile();
        ServerCfgPreview = _configGen.GenerateServerCfg(Profile);
        LaunchCommandPreview = _configGen.BuildLaunchArguments(Profile);
    }

    [RelayCommand]
    private void CopyServerCfg()
    {
        RefreshPreview();
        try { System.Windows.Clipboard.SetText(ServerCfgPreview ?? ""); } catch { }
    }

    [RelayCommand]
    private void CopyLaunchCommand()
    {
        RefreshPreview();
        try { System.Windows.Clipboard.SetText(LaunchCommandPreview ?? ""); } catch { }
    }

    [RelayCommand]
    private async Task WriteConfigFiles()
    {
        if (Profile is null) return;
        if (string.IsNullOrWhiteSpace(Profile.ServerDirectory))
        {
            await _dialogs.ShowErrorAsync("Server directory required",
                "Set the server directory on the Performance tab first.");
            return;
        }
        SyncProfile();
        try
        {
            await _configGen.WriteAllConfigFilesAsync(Profile);
            await _dialogs.ShowInfoAsync("Config written",
                "Wrote server.cfg, basic.cfg and BattlEye\\beserver_x64.cfg to:\n" + Profile.ServerDirectory);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Write failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        if (Profile is null) return;
        var err = Validate();
        if (err is not null)
        {
            await _dialogs.ShowErrorAsync("Validation", err);
            return;
        }
        SyncProfile();
        await _profiles.UpdateProfileAsync(Profile);
        await _dialogs.ShowInfoAsync("Saved", "Profile '" + Profile.Name + "' saved.");
    }

    [RelayCommand]
    private async Task BrowseServerExe()
    {
        if (Profile is null) return;
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
        if (Profile is null) return;
        var path = await _dialogs.BrowseFolderAsync("Select the server directory",
            string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? null : Profile.ServerDirectory);
        if (path is not null)
        {
            Profile.ServerDirectory = path;
            OnPropertyChanged(nameof(Profile));
        }
    }

    [RelayCommand]
    private async Task BrowseMissionDir()
    {
        if (Profile is null) return;
        var start = !string.IsNullOrWhiteSpace(Profile.MissionDirectory)
            ? Profile.MissionDirectory
            : (string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? null : Profile.ServerDirectory);
        var path = await _dialogs.BrowseFolderAsync("Select the mission folder", start);
        if (path is not null) { Profile.MissionDirectory = path; OnPropertyChanged(nameof(Profile)); }
    }

    [RelayCommand]
    private void OpenServerFolder()
    {
        if (Profile is not null && Directory.Exists(Profile.ServerDirectory))
        {
            try { Process.Start(new ProcessStartInfo(Profile.ServerDirectory) { UseShellExecute = true }); }
            catch { }
        }
    }

    [RelayCommand]
    private void AddLoadExtension()
    {
        var e = NewLoadExtension?.Trim().TrimStart('.');
        if (!string.IsNullOrWhiteSpace(e) &&
            !LoadExtensions.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)))
            LoadExtensions.Add(e);
        NewLoadExtension = string.Empty;
    }

    [RelayCommand]
    private void AddPreprocessExtension()
    {
        var e = NewPreprocessExtension?.Trim().TrimStart('.');
        if (!string.IsNullOrWhiteSpace(e) &&
            !PreprocessExtensions.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)))
            PreprocessExtensions.Add(e);
        NewPreprocessExtension = string.Empty;
    }

    [RelayCommand]
    private void AddHtmlExtension()
    {
        var e = NewHtmlExtension?.Trim().TrimStart('.');
        if (!string.IsNullOrWhiteSpace(e) &&
            !HtmlExtensions.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)))
            HtmlExtensions.Add(e);
        NewHtmlExtension = string.Empty;
    }

    [RelayCommand]
    private void AddHtmlUri()
    {
        var e = NewHtmlUri?.Trim();
        if (!string.IsNullOrWhiteSpace(e) &&
            !HtmlUris.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)))
            HtmlUris.Add(e);
        NewHtmlUri = string.Empty;
    }

    [RelayCommand]
    private void RemoveLoadExtension(string ext) => LoadExtensions.Remove(ext);

    [RelayCommand]
    private void RemovePreprocessExtension(string ext) => PreprocessExtensions.Remove(ext);

    [RelayCommand]
    private void RemoveHtmlExtension(string ext) => HtmlExtensions.Remove(ext);

    [RelayCommand]
    private void RemoveHtmlUri(string ext) => HtmlUris.Remove(ext);

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
}
