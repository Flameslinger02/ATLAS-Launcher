using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Console;

/// <summary>
/// The Console page: read-only log viewing (ATLAS's own Serilog "App Log" and the live Server RPT tail) plus
/// the Updates tab (Arma 3 server + ATLAS self-update, hosted by <see cref="Updater"/>). Live BattlEye RCON
/// management lives on the separate RCON page (<see cref="RconViewModel"/>). Singleton VM (holds the
/// long-lived server-log subscription; mirrors the Dashboard no-leak rule).
/// </summary>
public partial class ConsoleViewModel : BaseViewModel, IDisposable
{
    private readonly IServerProcessService _server;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly IRptAnalyzerService _rptAnalyzer;
    private readonly List<IDisposable> _subs = new();
    private const int MaxLogLines = 500;

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Selected tab on the Console page: 0 = ATLAS Log, 1 = Server RPT, 2 = Updates. Two-way bound to the
    /// page's TabControl so the startup update banner can land the user straight on Updates (see <see cref="ShowUpdatesTab"/>).</summary>
    [ObservableProperty] private int _selectedTabIndex;

    public ObservableCollection<string> ServerRpt { get; } = new();

    /// <summary>The "App Log" tab view model (ATLAS's own Serilog output).</summary>
    public AppLogViewModel AppLog { get; }

    /// <summary>The "Updates" tab view model (Arma 3 server + ATLAS self-update).</summary>
    public UpdaterViewModel Updater { get; }

    public ConsoleViewModel(IServerProcessService server, IProfileService profiles, IDialogService dialogs,
        AppLogViewModel appLog, UpdaterViewModel updater, IRptAnalyzerService rptAnalyzer)
    {
        _server = server;
        _profiles = profiles;
        _dialogs = dialogs;
        _rptAnalyzer = rptAnalyzer;
        AppLog = appLog;
        Updater = updater;
        Title = "Console";

        _subs.Add(_server.LogOutput.Subscribe(line => OnUi(() => AppendServerRpt(line))));
    }

    /// <summary>Lands the Console page on the Updates tab (index 2). Called by the startup update banner's
    /// "Update" button so the user arrives on the in-app updater rather than the default ATLAS Log tab.</summary>
    public void ShowUpdatesTab() => SelectedTabIndex = 2;

    // ----- Server RPT log viewer -----

    [RelayCommand]
    private void OpenRptInExplorer()
    {
        var dir = _profiles.ActiveProfile?.ServerDirectory;
        if (string.IsNullOrWhiteSpace(dir)) { StatusMessage = "No active profile / server directory."; return; }
        var profilesDir = Path.Combine(dir, "profiles");
        var target = Directory.Exists(profilesDir) ? profilesDir : dir;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Failed to open RPT folder."); StatusMessage = "Could not open the RPT folder."; }
    }

    [RelayCommand]
    private async Task SaveRpt()
    {
        if (ServerRpt.Count == 0) { await _dialogs.ShowInfoAsync("Save RPT", "The RPT tail is empty."); return; }
        var path = await _dialogs.SaveFileAsync("Save RPT tail", "Log file|*.log;*.txt", $"server-rpt-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { await File.WriteAllLinesAsync(path, ServerRpt.ToList()); StatusMessage = "RPT tail saved."; }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Save failed", ex.Message); }
    }

    [RelayCommand]
    private void ClearServerRpt() => ServerRpt.Clear();

    /// <summary>Scans the newest RPT on disk for the active profile and opens a grouped known-issue report.</summary>
    [RelayCommand]
    private async Task AnalyzeRpt()
    {
        var dir = _profiles.ActiveProfile?.ServerDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            await _dialogs.ShowInfoAsync("Analyze RPT", "No active profile / server directory.");
            return;
        }
        var rpt = _rptAnalyzer.FindNewestRpt(dir);
        if (rpt is null)
        {
            await _dialogs.ShowInfoAsync("Analyze RPT",
                "No .rpt log found yet under the server's profiles folder — run the server at least once first.");
            return;
        }
        try
        {
            var analysis = await _rptAnalyzer.AnalyzeAsync(rpt);
            var window = new RptAnalysisWindow(analysis) { Owner = Application.Current?.MainWindow };
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RPT analysis failed for {Rpt}.", rpt);
            await _dialogs.ShowErrorAsync("Analyze RPT", $"Could not analyze the RPT: {ex.Message}");
        }
    }

    private void AppendServerRpt(string line)
    {
        ServerRpt.Add(line);
        while (ServerRpt.Count > MaxLogLines) ServerRpt.RemoveAt(0);
    }

    // ----- Helpers -----

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        foreach (var s in _subs)
        {
            try { s.Dispose(); } catch { /* ignore */ }
        }
        _subs.Clear();
    }
}
