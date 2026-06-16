using System.Windows.Controls;

namespace Atlas.Pages.HeadlessClients;

/// <summary>Phase 1 stub view for the Headless Clients module.</summary>
public partial class HeadlessClientsPage : UserControl
{
    public HeadlessClientsPage(HeadlessClientsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
