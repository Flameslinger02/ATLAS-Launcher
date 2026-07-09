using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Atlas.Controls;

/// <summary>
/// A minimal dependency-free line chart: renders a sequence of numeric samples as a filled polyline that
/// fills the control. Newest sample is on the right. Set <see cref="Minimum"/>/<see cref="Maximum"/> for a
/// fixed scale (e.g. CPU 0–100) or leave them NaN to auto-scale to the data. With <see cref="ShowAxes"/>
/// it draws min/max Y labels and oldest→now X labels with light gridlines.
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

    public static readonly DependencyProperty ShowAxesProperty = DependencyProperty.Register(
        nameof(ShowAxes), typeof(bool), typeof(SparklineChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush), typeof(Brush), typeof(SparklineChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueSuffixProperty = DependencyProperty.Register(
        nameof(ValueSuffix), typeof(string), typeof(SparklineChart),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Real seconds represented by one sample (used to label the X axis time span).</summary>
    public static readonly DependencyProperty SecondsPerSampleProperty = DependencyProperty.Register(
        nameof(SecondsPerSample), typeof(double), typeof(SparklineChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? PointsSource { get => (IEnumerable?)GetValue(PointsSourceProperty); set => SetValue(PointsSourceProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public bool ShowAxes { get => (bool)GetValue(ShowAxesProperty); set => SetValue(ShowAxesProperty, value); }
    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public string ValueSuffix { get => (string)GetValue(ValueSuffixProperty); set => SetValue(ValueSuffixProperty, value); }
    public double SecondsPerSample { get => (double)GetValue(SecondsPerSampleProperty); set => SetValue(SecondsPerSampleProperty, value); }

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

        // Reserve gutters for axis labels when enabled.
        var showAxes = ShowAxes;
        double leftGutter = showAxes ? 34 : 3;
        double bottomGutter = showAxes ? 14 : 3;
        const double topPad = 3, rightPad = 4;

        var plotW = w - leftGutter - rightPad;
        var plotH = h - topPad - bottomGutter;
        if (plotW <= 2 || plotH <= 2) return;

        double X(int i) => leftGutter + plotW * i / (data.Count - 1);
        double Y(double v) => topPad + plotH * (1 - (Math.Clamp(v, min, max) - min) / (max - min));

        if (showAxes)
        {
            var gridPen = new Pen(AxisBrush, 0.5) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
            gridPen.Freeze();
            // Top, middle and bottom gridlines.
            foreach (var frac in new[] { 0.0, 0.5, 1.0 })
            {
                var y = topPad + plotH * frac;
                dc.DrawLine(gridPen, new Point(leftGutter, y), new Point(leftGutter + plotW, y));
            }
            DrawText(dc, Fmt(max), leftGutter - 4, topPad, right: true, top: true);
            DrawText(dc, Fmt(min), leftGutter - 4, topPad + plotH, right: true, top: false);

            // X axis: oldest (left) → now (right).
            var spanSec = (data.Count - 1) * SecondsPerSample;
            DrawText(dc, "-" + FmtSpan(spanSec), leftGutter, h - bottomGutter + 1, right: false, top: true);
            DrawText(dc, "now", leftGutter + plotW, h - bottomGutter + 1, right: true, top: true);
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(0), topPad + plotH), isFilled: true, isClosed: true);
            for (var i = 0; i < data.Count; i++) g.LineTo(new Point(X(i), Y(data[i])), true, false);
            g.LineTo(new Point(X(data.Count - 1), topPad + plotH), true, false);
        }
        geo.Freeze();

        var stroke = LineBrush;
        Brush fill = stroke is SolidColorBrush sc
            ? new SolidColorBrush(Color.FromArgb(40, sc.Color.R, sc.Color.G, sc.Color.B))
            : Brushes.Transparent;
        if (fill.CanFreeze) fill.Freeze();

        dc.DrawGeometry(fill, new Pen(stroke, 1.5), geo);
    }

    private string Fmt(double v)
    {
        var n = Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
                                   : v.ToString("0.#", CultureInfo.InvariantCulture);
        return n + ValueSuffix;
    }

    private static string FmtSpan(double seconds) =>
        seconds >= 60 ? $"{Math.Round(seconds / 60)}m" : $"{Math.Round(seconds)}s";

    private void DrawText(DrawingContext dc, string text, double x, double y, bool right, bool top)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, AxisBrush, dpi);
        var ox = right ? x - ft.Width : x;
        var oy = top ? y : y - ft.Height;
        dc.DrawText(ft, new Point(ox, oy));
    }
}
