using System.Windows;
using System.Windows.Input;

namespace Atlas.Pages.Profiles;

/// <summary>Modal profile editor window. View-plumbing only (drag + close wiring).</summary>
public partial class ProfileEditorDialog : Window
{
    public ProfileEditorDialog(ProfileEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += result =>
        {
            // Setting DialogResult on a window shown via ShowDialog() closes it.
            DialogResult = result;
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
