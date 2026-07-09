using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Atlas.Controls;

/// <summary>
/// A minimal dependency-free line chart: renders a sequence of numeric samples as a filled polyline that
/// fills the control. Newest sample is on the right. Set <see cref="Minimum"/>/<see cref="Maximum"/> for a
/// fixed scale (e.g. CPU 0–100) or leave them NaN to auto-scale to the data.
/// </summary>
public sealed class SparklineChart : Control
{
    static SparklineChart()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SparklineChart), new FrameworkPropertyMetadata(typeof(SparklineChart)));
    }

    public static readonly DependencyProperty PointsSourceProperty = DependencyProperty.Register(
        nameof(PointsSource), typeof(IEnumerable), typeof(SparklineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(SparklineChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SparklineChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? PointsSource { get => (IEnumerable?)GetValue(PointsSourceProperty); set => SetValue(PointsSourceProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SparklineChart)d).InvalidateVisual();

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1 || PointsSource is null) return;

        var data = PointsSource.Cast<object>().Select(Convert.ToDouble).ToList();
        if (data.Count < 2) return;

        var min = Minimum;
        var max = Maximum;
        if (double.IsNaN(min)) min = data.Min();
        if (double.IsNaN(max)) max = data.Max();
        if (max - min < 1e-6) max = min + 1;   // flat series → avoid divide-by-zero

        const double pad = 3;
        double X(int i) => pad + (w - 2 * pad) * i / (data.Count - 1);
        double Y(double v) => h - pad - (h - 2 * pad) * (Math.Clamp(v, min, max) - min) / (max - min);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(0), h - pad), isFilled: true, isClosed: true);
            for (var i = 0; i < data.Count; i++) g.LineTo(new Point(X(i), Y(data[i])), true, false);
            g.LineTo(new Point(X(data.Count - 1), h - pad), true, false);
        }
        geo.Freeze();

        var stroke = LineBrush;
        Brush fill = stroke is SolidColorBrush sc
            ? new SolidColorBrush(Color.FromArgb(40, sc.Color.R, sc.Color.G, sc.Color.B))
            : Brushes.Transparent;
        if (fill.CanFreeze) fill.Freeze();

        dc.DrawGeometry(fill, new Pen(stroke, 1.5), geo);
    }
}
