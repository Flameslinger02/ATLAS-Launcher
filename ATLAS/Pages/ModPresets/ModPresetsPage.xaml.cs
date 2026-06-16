using System.Windows.Controls;

namespace Atlas.Pages.ModPresets;

/// <summary>Phase 1 stub view for the Mod Presets module.</summary>
public partial class ModPresetsPage : UserControl
{
    public ModPresetsPage(ModPresetsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
