using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Atlas.Core.Behaviors;

/// <summary>
/// Attached sticky-bottom auto-scroll for log-style views. While "stuck" (the default, including on first
/// load) the view follows new content to the bottom. Scrolling up detaches it so history can be read in
/// peace; scrolling back to the bottom re-attaches it.
/// Usage: <c>b:AutoScrollBehavior.StickToBottom="True"</c> on a ListBox (or anything hosting a ScrollViewer).
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty StickToBottomProperty =
        DependencyProperty.RegisterAttached(
            "StickToBottom", typeof(bool), typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnStickToBottomChanged));

    /// <summary>Per-ScrollViewer stick state: true = currently following the tail.</summary>
    private static readonly DependencyProperty IsStuckProperty =
        DependencyProperty.RegisterAttached(
            "IsStuck", typeof(bool), typeof(AutoScrollBehavior), new PropertyMetadata(true));

    public static bool GetStickToBottom(DependencyObject obj) => (bool)obj.GetValue(StickToBottomProperty);
    public static void SetStickToBottom(DependencyObject obj, bool value) => obj.SetValue(StickToBottomProperty, value);

    private static void OnStickToBottomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.Loaded += OnLoaded;                      // pages are transient: re-attach on every load
            if (fe.IsLoaded) TryAttach(fe);
        }
        else
        {
            fe.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        // The ListBox template (and thus its ScrollViewer) may not be realized yet on Loaded — retry once
        // after layout settles.
        if (!TryAttach(fe))
            fe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TryAttach(fe));
    }

    private static bool TryAttach(FrameworkElement fe)
    {
        var sv = fe as ScrollViewer ?? FindScrollViewer(fe);
        if (sv is null) return false;

        sv.ScrollChanged -= OnScrollChanged;            // idempotent across repeated Loaded events
        sv.ScrollChanged += OnScrollChanged;
        sv.SetValue(IsStuckProperty, true);
        sv.ScrollToEnd();                               // land at the tail even when lines pre-exist the view
        return true;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (e.ExtentHeightChange != 0)
        {
            // Content grew (or shrank, e.g. a Clear): follow the tail only while stuck.
            if ((bool)sv.GetValue(IsStuckProperty)) sv.ScrollToEnd();
        }
        else
        {
            // Pure user scroll (or resize): stick exactly when resting at — or within a line of — the bottom.
            sv.SetValue(IsStuckProperty, sv.VerticalOffset >= sv.ScrollableHeight - 1.0);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
