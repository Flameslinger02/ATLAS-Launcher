using System.Windows;

namespace Atlas.Pages.Profiles;

/// <summary>Read-only list of a mission's required addons (mission.sqm <c>addOns[]</c>). Base-game/DLC
/// (A3_*) entries are hidden by default — a "Show vanilla" toggle reveals them.</summary>
public partial class MissionDependenciesWindow : Window
{
    private readonly IReadOnlyList<string> _all;
    private readonly List<string> _modsOnly;
    private readonly string _missionName;

    public MissionDependenciesWindow(string missionName, IReadOnlyList<string> addOns)
    {
        InitializeComponent();
        _all = addOns;
        _missionName = missionName;
        _modsOnly = addOns.Where(a => !a.StartsWith("a3_", StringComparison.OrdinalIgnoreCase)).ToList();
        Title = $"{missionName} — Dependencies";
        Refresh();
    }

    private bool ShowingVanilla => ShowVanilla.IsChecked == true;

    private void OnToggleVanilla(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var shown = ShowingVanilla ? _all : _modsOnly;
        DepsList.ItemsSource = shown;

        var vanillaCount = _all.Count - _modsOnly.Count;
        if (_all.Count == 0)
            HeaderText.Text = $"{_missionName}: mission.sqm lists no required addons.";
        else if (ShowingVanilla)
            HeaderText.Text = $"{_missionName} — {_all.Count} required addon(s) ({_modsOnly.Count} mod, {vanillaCount} vanilla)";
        else
            HeaderText.Text = vanillaCount > 0
                ? $"{_missionName} — {_modsOnly.Count} mod addon(s)  ·  {vanillaCount} vanilla hidden"
                : $"{_missionName} — {_modsOnly.Count} mod addon(s)";
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var shown = ShowingVanilla ? _all : (IReadOnlyList<string>)_modsOnly;
        try { Clipboard.SetText(string.Join(Environment.NewLine, shown)); }
        catch { /* clipboard momentarily locked by another app */ }
    }
}
