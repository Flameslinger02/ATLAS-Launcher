using System.Windows.Controls;

namespace Atlas.Pages.Mods;

/// <summary>Phase 1 stub view for the Mods module.</summary>
public partial class ModsPage : UserControl
{
    public ModsPage(ModsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
