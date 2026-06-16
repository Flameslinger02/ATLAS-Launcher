using System.Windows.Controls;

namespace Atlas.Pages.Dashboard;

/// <summary>Phase 1 stub view for the Dashboard module.</summary>
public partial class DashboardPage : UserControl
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
