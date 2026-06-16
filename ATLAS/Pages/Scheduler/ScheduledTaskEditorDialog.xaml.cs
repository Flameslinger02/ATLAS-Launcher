using System.Windows;
using System.Windows.Input;

namespace Atlas.Pages.Scheduler;

/// <summary>Modal scheduled-task editor window. View-plumbing only (drag + close wiring).</summary>
public partial class ScheduledTaskEditorDialog : Window
{
    public ScheduledTaskEditorDialog(ScheduledTaskEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += result => DialogResult = result;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
