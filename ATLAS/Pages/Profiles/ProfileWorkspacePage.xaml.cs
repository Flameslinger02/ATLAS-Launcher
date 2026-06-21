using System.Windows.Controls;

namespace Atlas.Pages.Profiles;

/// <summary>The profile workspace: profile rail + per-profile Profile/Missions/Config/Network tabs.</summary>
public partial class ProfileWorkspacePage : UserControl
{
    public ProfileWorkspacePage(ProfileWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
