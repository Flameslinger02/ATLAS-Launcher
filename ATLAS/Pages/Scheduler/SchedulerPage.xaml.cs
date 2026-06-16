using System.Windows.Controls;

namespace Atlas.Pages.Scheduler;

/// <summary>Phase 1 stub view for the Scheduler module.</summary>
public partial class SchedulerPage : UserControl
{
    public SchedulerPage(SchedulerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
