using System.Windows;
using Atlas.Core.ViewModels;

namespace Atlas;

/// <summary>The shell window. Its <see cref="MainViewModel"/> is injected via DI.</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
