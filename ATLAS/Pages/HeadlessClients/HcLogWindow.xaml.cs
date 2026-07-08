using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using Serilog;

namespace Atlas.Pages.HeadlessClients;

/// <summary>
/// A live tail of a single headless client's <c>.rpt</c> log. Polls the instance's <c>-profiles</c>
/// directory for the newest <c>.rpt</c> and streams complete lines into the list. The tail task is
/// cancelled when the window closes.
/// </summary>
public partial class HcLogWindow : Window
{
    private const int MaxLines = 1000;
    private readonly string _directory;
    private readonly ObservableCollection<string> _lines = new();
    private readonly CancellationTokenSource _cts = new();

    public HcLogWindow(string name, string directory)
    {
        InitializeComponent();
        _directory = directory;
        Title = $"{name} — Log";
        HeaderText.Text = $"{name} RPT tail — {directory}";
        LogList.ItemsSource = _lines;   // tail-follow is handled by AutoScrollBehavior in the XAML

        Loaded += (_, _) => _ = Task.Run(() => TailAsync(_cts.Token));
        Closed += (_, _) => { try { _cts.Cancel(); _cts.Dispose(); } catch { /* ignore */ } };
    }

    private void Append(string line) => Dispatcher.InvokeAsync(() =>
    {
        _lines.Add(line);
        while (_lines.Count > MaxLines) _lines.RemoveAt(0);
    });

    private async Task TailAsync(CancellationToken ct)
    {
        string? rpt = null;
        for (var i = 0; i < 60 && !ct.IsCancellationRequested && rpt is null; i++)
        {
            try
            {
                if (Directory.Exists(_directory))
                    rpt = new DirectoryInfo(_directory).GetFiles("*.rpt")
                        .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            }
            catch { /* directory momentarily inaccessible */ }

            if (rpt is null)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }
            }
        }

        if (rpt is null) { Append("(No .rpt log file found yet for this instance.)"); return; }

        Append($"Tailing {Path.GetFileName(rpt)}");
        try
        {
            await using var fs = new FileStream(rpt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var pending = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                var chunk = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (chunk.Length > 0)
                {
                    pending.Append(chunk);
                    var text = pending.ToString();
                    var lastNl = text.LastIndexOf('\n');
                    if (lastNl >= 0)
                    {
                        foreach (var line in text[..lastNl].Split('\n')) Append(line.TrimEnd('\r'));
                        pending.Clear();
                        pending.Append(text[(lastNl + 1)..]);
                    }
                }
                try { await Task.Delay(750, ct).ConfigureAwait(false); } catch { break; }
            }
        }
        catch (OperationCanceledException) { /* expected on close */ }
        catch (Exception ex) { Log.Warning(ex, "HC log tail error."); Append($"[tail error] {ex.Message}"); }
    }

    private void OnClear(object sender, RoutedEventArgs e) => _lines.Clear();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = Directory.Exists(_directory) ? _directory
                : Path.GetDirectoryName(_directory) ?? _directory;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to open HC log folder."); }
    }
}
