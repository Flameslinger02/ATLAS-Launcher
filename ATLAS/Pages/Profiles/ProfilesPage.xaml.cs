using System.Windows.Controls;

namespace Atlas.Pages.Profiles;

/// <summary>Phase 1 stub view for the Profiles module.</summary>
public partial class ProfilesPage : UserControl
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
