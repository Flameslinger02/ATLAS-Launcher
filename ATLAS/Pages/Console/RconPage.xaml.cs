using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Atlas.Pages.Console;

/// <summary>RCON management view; DI injects the view model and wires it as the DataContext.
/// Console scrollback tailing is handled by <see cref="Core.Behaviors.AutoScrollBehavior"/>.</summary>
public partial class RconPage : UserControl
{
    private readonly RconViewModel _viewModel;

    public RconPage(RconViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
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
