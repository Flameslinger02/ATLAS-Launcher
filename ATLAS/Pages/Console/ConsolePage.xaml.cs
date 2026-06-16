using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Atlas.Pages.Console;

/// <summary>Console / RCON view; DI injects the view model and wires it as the DataContext.</summary>
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
        _viewModel.ConsoleLog.CollectionChanged -= OnConsoleLogChanged;
        _viewModel.ConsoleLog.CollectionChanged += OnConsoleLogChanged;
        _viewModel.ServerRpt.CollectionChanged -= OnServerRptChanged;
        _viewModel.ServerRpt.CollectionChanged += OnServerRptChanged;
        _viewModel.AppLog.Entries.CollectionChanged -= OnAppLogChanged;
        _viewModel.AppLog.Entries.CollectionChanged += OnAppLogChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ConsoleLog.CollectionChanged -= OnConsoleLogChanged;
        _viewModel.ServerRpt.CollectionChanged -= OnServerRptChanged;
        _viewModel.AppLog.Entries.CollectionChanged -= OnAppLogChanged;
    }

    private void OnConsoleLogChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollLast(ConsoleList, e);
    private void OnServerRptChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollLast(ServerRptList, e);

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

    // ----- RCON command history (up/down) -----

    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up) { _viewModel.HistoryPrevious(); MoveCaretToEnd(); e.Handled = true; }
        else if (e.Key == Key.Down) { _viewModel.HistoryNext(); MoveCaretToEnd(); e.Handled = true; }
    }

    private void MoveCaretToEnd() => CommandBox.CaretIndex = CommandBox.Text.Length;

    // ----- Players grid: right-click selection + context / bulk actions -----

    private void OnPlayersRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        // Keep an existing multi-selection if the clicked row is part of it; otherwise select just this row.
        if (!row.IsSelected)
        {
            PlayersGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    private async void OnBulkKick(object sender, RoutedEventArgs e) => await _viewModel.KickPlayersAsync(PlayersGrid.SelectedItems);
    private async void OnBulkBan(object sender, RoutedEventArgs e) => await _viewModel.BanPlayersByIdAsync(PlayersGrid.SelectedItems);

    private async void OnCtxKick(object sender, RoutedEventArgs e) => await _viewModel.KickPlayersAsync(PlayersGrid.SelectedItems);
    private async void OnCtxBanId(object sender, RoutedEventArgs e) => await _viewModel.BanPlayersByIdAsync(PlayersGrid.SelectedItems);
    private async void OnCtxBanGuid(object sender, RoutedEventArgs e) => await _viewModel.BanPlayerByGuidAsync(RowOf(sender));
    private async void OnCtxPm(object sender, RoutedEventArgs e) => await _viewModel.SendPrivateMessageAsync(RowOf(sender));
    private void OnCtxCopyGuid(object sender, RoutedEventArgs e) => _viewModel.CopyGuid(RowOf(sender));
    private void OnCtxCopyName(object sender, RoutedEventArgs e) => _viewModel.CopyName(RowOf(sender));
    private void OnCtxUnmask(object sender, RoutedEventArgs e) => _viewModel.ToggleUnmaskIp(RowOf(sender));

    /// <summary>The player row the context menu was opened on (the menu item inherits the row's DataContext).</summary>
    private static PlayerRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as PlayerRow;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T) current = VisualTreeHelper.GetParent(current);
        return current as T;
    }
}
