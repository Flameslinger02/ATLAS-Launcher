using System.Windows.Controls;

namespace Atlas.Pages.Console;

/// <summary>Console page (logs + updates); DI injects the view model and wires it as the DataContext.
/// Log tailing is handled by <see cref="Core.Behaviors.AutoScrollBehavior"/> on the list boxes.</summary>
public partial class ConsolePage : UserControl
{
    public ConsolePage(ConsoleViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
