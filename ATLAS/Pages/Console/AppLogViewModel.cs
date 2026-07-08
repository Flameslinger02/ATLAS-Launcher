using System.Collections.ObjectModel;
using System.Windows;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Console;

/// <summary>
/// The "App Log" tab on the Console page: a live, filterable view of ATLAS's own Serilog output (via
/// <see cref="IAppLogService"/>). Supports text search, minimum-level filter, pause, clear and export.
/// Singleton (long-lived subscription to the log service; mirrors the Dashboard no-leak rule).
/// </summary>
public partial class AppLogViewModel : BaseViewModel, IDisposable
{
    private readonly IAppLogService _log;
    private readonly IDialogService _dialogs;
    private readonly IDisposable _sub;
    private readonly List<LogEntry> _all = new();
    private const int MaxAll = 2000;
    private const int MaxShown = 1000;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _minLevel = "All";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _statusText = string.Empty;

    public string[] LevelOptions { get; } = { "All", "Debug", "Information", "Warning", "Error" };

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public AppLogViewModel(IAppLogService log, IDialogService dialogs)
    {
        _log = log;
        _dialogs = dialogs;
        Title = "App Log";

        _all.AddRange(_log.Snapshot());
        ApplyFilter();
        _sub = _log.Logged.Subscribe(e => OnUi(() => OnLogged(e)));
    }

    private void OnLogged(LogEntry entry)
    {
        _all.Add(entry);
        while (_all.Count > MaxAll) _all.RemoveAt(0);

        // While paused we keep buffering (the view catches up on un-pause) but still update the status
        // counter so the "buffered" figure stays live. Note: retention is bounded by the MaxAll ring.
        if (!IsPaused && Passes(entry))
        {
            Entries.Add(entry);
            while (Entries.Count > MaxShown) Entries.RemoveAt(0);
        }
        UpdateStatus();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnMinLevelChanged(string value) => ApplyFilter();

    partial void OnIsPausedChanged(bool value)
    {
        if (!value) ApplyFilter();      // catch up on the entries buffered while paused
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var e in _all.Where(Passes).TakeLast(MaxShown))
            Entries.Add(e);
        UpdateStatus();
    }

    private bool Passes(LogEntry e)
    {
        if (e.Severity < MinSeverity()) return false;
        return string.IsNullOrWhiteSpace(SearchText)
               || e.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private int MinSeverity() => MinLevel switch
    {
        "Debug" => 1,
        "Information" => 2,
        "Warning" => 3,
        "Error" => 4,
        _ => -1,            // "All"
    };

    private void UpdateStatus() =>
        StatusText = $"{Entries.Count} shown / {_all.Count} buffered{(IsPaused ? " (paused)" : "")}";

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Entries.Clear();
        UpdateStatus();
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = await _dialogs.SaveFileAsync("Export log", "Text file|*.txt", $"atlas-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // Materialize on the UI thread before the async write — File.WriteAllLinesAsync enumerates lazily
            // on a thread-pool continuation, which would race OnLogged mutating _all on the UI thread.
            var lines = _all.Select(e => e.Display).ToList();
            await File.WriteAllLinesAsync(path, lines);
            StatusText = $"Exported {lines.Count} line(s).";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export app log.");
            await _dialogs.ShowErrorAsync("Export failed", ex.Message);
        }
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        try { _sub.Dispose(); } catch { /* ignore */ }
    }
}
