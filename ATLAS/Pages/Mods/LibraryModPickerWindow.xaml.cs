using System.Windows;
using Atlas.Core.Models;

namespace Atlas.Pages.Mods;

/// <summary>
/// Multi-select picker over the global mod library. Shows the candidate mods with a text filter;
/// the caller reads <see cref="SelectedMods"/> when <c>ShowDialog()</c> returns true.
/// </summary>
public partial class LibraryModPickerWindow : Window
{
    private sealed class Row
    {
        public bool IsChecked { get; set; }
        public required ArmaModEntry Mod { get; init; }
        public string Name => string.IsNullOrWhiteSpace(Mod.Name) ? Mod.FolderName : Mod.Name;
        public string Detail => Mod.WorkshopId != 0
            ? $"{Mod.FolderName}  ·  Workshop {Mod.WorkshopId}"
            : $"{Mod.FolderName}  ·  local";
    }

    private readonly List<Row> _rows;

    /// <summary>The mods ticked when the dialog was confirmed.</summary>
    public List<ArmaModEntry> SelectedMods { get; } = new();

    public LibraryModPickerWindow(IEnumerable<ArmaModEntry> candidates)
    {
        InitializeComponent();
        _rows = candidates
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new Row { Mod = m })
            .ToList();
        ModList.ItemsSource = _rows;
        UpdateCount();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        var f = FilterBox.Text.Trim();
        ModList.ItemsSource = f.Length == 0
            ? _rows
            : _rows.Where(r =>
                    r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    r.Detail.Contains(f, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private void OnRowToggled(object sender, RoutedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        var picked = _rows.Count(r => r.IsChecked);
        CountText.Text = $"{picked} of {_rows.Count} selected";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        SelectedMods.AddRange(_rows.Where(r => r.IsChecked).Select(r => r.Mod));
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
