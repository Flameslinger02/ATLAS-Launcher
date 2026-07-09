using System.IO;
using System.Windows;
using System.Windows.Media;
using Atlas.Core.Services;

namespace Atlas.Pages.Console;

/// <summary>Read-only grouped view of a <see cref="RptAnalysis"/> — findings ordered by severity.</summary>
public partial class RptAnalysisWindow : Window
{
    private sealed record Row(string SeverityText, Brush SeverityBrush, string Title, string CountText,
        string Guidance, IReadOnlyList<string> Samples);

    public RptAnalysisWindow(RptAnalysis analysis)
    {
        InitializeComponent();
        HeaderText.Text = $"{Path.GetFileName(analysis.FilePath)} — {analysis.TotalLines:N0} lines scanned";

        if (analysis.Findings.Count == 0)
        {
            SummaryText.Text = "No known issues detected. 🎉";
            return;
        }

        var crit = analysis.Findings.Count(f => f.Rule.Severity == RptSeverity.Critical);
        var warn = analysis.Findings.Count(f => f.Rule.Severity == RptSeverity.Warning);
        var info = analysis.Findings.Count(f => f.Rule.Severity == RptSeverity.Info);
        SummaryText.Text = $"{crit} critical · {warn} warning · {info} informational issue type(s). "
                         + "Expand a row for guidance and sample lines. Informational items are usually harmless noise.";

        FindingsList.ItemsSource = analysis.Findings.Select(f => new Row(
            f.Rule.Severity.ToString().ToUpperInvariant(),
            SeverityBrush(f.Rule.Severity),
            f.Rule.Title,
            f.Count == 1 ? "1 match" : $"{f.Count} matches",
            f.Rule.Guidance,
            f.Samples)).ToList();
    }

    private static Brush SeverityBrush(RptSeverity s) => s switch
    {
        RptSeverity.Critical => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
        RptSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x00)),
        _ => new SolidColorBrush(Color.FromRgb(0x55, 0x6C, 0x7A)),
    };
}
