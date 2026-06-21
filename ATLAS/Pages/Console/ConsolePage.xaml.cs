using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace Atlas.Pages.Console;

/// <summary>Console page (logs + updates); DI injects the view model and wires it as the DataContext.</summary>
public partial class ConsolePage : UserControl
{
    private readonly ConsoleViewModel _viewModel;

    public ConsolePage(ConsoleViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // The page is transient but the VMs/collections are singletons, so subscribe only while loaded and
        // detach on unload — otherwise each navigation would leak this page via the singleton's event.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ServerRpt.CollectionChanged -= OnServerRptChanged;
        _viewModel.ServerRpt.CollectionChanged += OnServerRptChanged;
        _viewModel.AppLog.Entries.CollectionChanged -= OnAppLogChanged;
        _viewModel.AppLog.Entries.CollectionChanged += OnAppLogChanged;
        _viewModel.Updater.ArmaOutput.CollectionChanged -= OnArmaOutputChanged;
        _viewModel.Updater.ArmaOutput.CollectionChanged += OnArmaOutputChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ServerRpt.CollectionChanged -= OnServerRptChanged;
        _viewModel.AppLog.Entries.CollectionChanged -= OnAppLogChanged;
        _viewModel.Updater.ArmaOutput.CollectionChanged -= OnArmaOutputChanged;
    }

    private void OnServerRptChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollLast(ServerRptList, e);
    private void OnArmaOutputChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollLast(ArmaOutputList, e);

    private void OnAppLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_viewModel.AppLog.AutoScroll) return;
        ScrollLast(AppLogList, e);
    }

    private static void ScrollLast(ListBox list, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || list.Items.Count == 0) return;
        list.ScrollIntoView(list.Items[^1]);
    }
}
