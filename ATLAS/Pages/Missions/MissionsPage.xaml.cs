using System.Windows.Controls;

namespace Atlas.Pages.Missions;

/// <summary>Mission selector view; DI injects the view model and wires it as the DataContext.</summary>
public partial class MissionsPage : UserControl
{
    public MissionsPage(MissionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
