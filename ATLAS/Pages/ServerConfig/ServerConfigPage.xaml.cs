using System.Windows.Controls;

namespace Atlas.Pages.ServerConfig;

/// <summary>Phase 1 stub view for the Server Config module.</summary>
public partial class ServerConfigPage : UserControl
{
    public ServerConfigPage(ServerConfigViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
