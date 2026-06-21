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
    private readonly List<IDisposable> _subs = new();
    private const int MaxLogLines = 500;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<string> ServerRpt { get; } = new();

    /// <summary>The "App Log" tab view model (ATLAS's own Serilog output).</summary>
    public AppLogViewModel AppLog { get; }

    /// <summary>The "Updates" tab view model (Arma 3 server + ATLAS self-update).</summary>
    public UpdaterViewModel Updater { get; }

    public ConsoleViewModel(IServerProcessService server, IProfileService profiles, IDialogService dialogs,
        AppLogViewModel appLog, UpdaterViewModel updater)
    {
        _server = server;
        _profiles = profiles;
        _dialogs = dialogs;
        AppLog = appLog;
        Updater = updater;
        Title = "Console";

        _subs.Add(_server.LogOutput.Subscribe(line => OnUi(() => AppendServerRpt(line))));
    }

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
