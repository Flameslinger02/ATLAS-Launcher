using System.Windows;

namespace Atlas.Pages.Profiles;

/// <summary>Read-only list of a mission's required addons (mission.sqm <c>addOns[]</c>).</summary>
public partial class MissionDependenciesWindow : Window
{
    private readonly IReadOnlyList<string> _addOns;

    public MissionDependenciesWindow(string missionName, IReadOnlyList<string> addOns)
    {
        InitializeComponent();
        _addOns = addOns;
        Title = $"{missionName} — Dependencies";
        HeaderText.Text = addOns.Count == 0
            ? $"{missionName}: mission.sqm lists no required addons."
            : $"{missionName} — {addOns.Count} required addon(s)";
        DepsList.ItemsSource = addOns;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, _addOns)); }
        catch { /* clipboard momentarily locked by another app */ }
    }
}
