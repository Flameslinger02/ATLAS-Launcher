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
/// it draws round-value Y ticks and fixed-interval time ticks on the X axis, with light gridlines.
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

    /// <summary>When &gt; 0, pins the X axis to this many seconds (newest sample at the right edge, older
    /// samples placed by age) instead of stretching the data to fill the plot. The axis frame and time
    /// ticks are drawn at this fixed span even before any data arrives (a blank live grid).</summary>
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(SparklineChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? PointsSource { get => (IEnumerable?)GetValue(PointsSourceProperty); set => SetValue(PointsSourceProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public bool ShowAxes { get => (bool)GetValue(ShowAxesProperty); set => SetValue(ShowAxesProperty, value); }
    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public string ValueSuffix { get => (string)GetValue(ValueSuffixProperty); set => SetValue(ValueSuffixProperty, value); }
    public double SecondsPerSample { get => (double)GetValue(SecondsPerSampleProperty); set => SetValue(SecondsPerSampleProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

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
        var showAxes = ShowAxes;
        var sps = SecondsPerSample;
        var windowSec = WindowSeconds;

        // Without a pinned window there is nothing to frame until at least two points exist.
        if (data.Count < 2 && (!showAxes || windowSec <= 0)) return;

        // ----- Y scale -----
        var hasData = data.Count >= 1;
        var autoMin = double.IsNaN(Minimum);
        var autoMax = double.IsNaN(Maximum);
        var min = autoMin ? (hasData ? data.Min() : 0) : Minimum;
        var max = autoMax ? (hasData ? data.Max() : 1) : Maximum;
        if (max - min < 1e-6) max = min + 1;   // flat / empty series → avoid divide-by-zero
        // With an auto-scaled axis and no data yet we can't put meaningful numbers on Y — draw the
        // gridlines but omit the labels (fixed-scale series like CPU 0–100 keep their labels).
        var showYLabels = hasData || (!autoMin && !autoMax);

        var yTicks = new List<double>();
        double leftGutter = 3, bottomGutter = 3;
        const double topPad = 3, rightPad = 4;

        if (showAxes)
        {
            // "Nice number" Y ticks (~4 intervals); snap the scale to tick multiples so the
            // gridlines land on round values (0/25/50/75/100 for a 0-100 series).
            var yStep = NiceStep((max - min) / 4.0);
            min = Math.Floor(min / yStep) * yStep;
            max = Math.Ceiling(max / yStep) * yStep;
            if (max - min < yStep / 2) max = min + yStep;
            for (var v = min; v <= max + yStep / 2; v += yStep) yTicks.Add(v);

            // Size the label gutter to the widest tick label so long values (e.g. "3000 MB") fit.
            leftGutter = showYLabels ? yTicks.Max(v => MeasureText(Fmt(v)).Width) + 6 : 8;
            bottomGutter = 14;
        }

        var plotW = w - leftGutter - rightPad;
        var plotH = h - topPad - bottomGutter;
        if (plotW <= 2 || plotH <= 2) return;

        // Pinned window: place each sample by its age (newest at the right edge). Otherwise stretch the
        // data across the whole plot by index.
        var pinned = windowSec > 0;
        var spanSec = pinned ? windowSec : Math.Max(1, (data.Count - 1) * sps);
        double X(int i) => pinned
            ? leftGutter + plotW * (1 - Math.Clamp((data.Count - 1 - i) * sps / spanSec, 0, 1))
            : leftGutter + plotW * i / Math.Max(1, data.Count - 1);
        double Y(double v) => topPad + plotH * (1 - (Math.Clamp(v, min, max) - min) / (max - min));

        if (showAxes)
        {
            var gridPen = new Pen(AxisBrush, 0.5) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
            gridPen.Freeze();

            foreach (var v in yTicks)
            {
                var y = Y(v);
                dc.DrawLine(gridPen, new Point(leftGutter, y), new Point(leftGutter + plotW, y));
                if (!showYLabels) continue;
                // Anchor the extremes inside the plot so their labels aren't clipped.
                var vAlign = Math.Abs(v - max) < 1e-9 ? VAlign.Below
                           : Math.Abs(v - min) < 1e-9 ? VAlign.Above
                           : VAlign.Middle;
                DrawText(dc, Fmt(v), leftGutter - 4, y, HAlign.Right, vAlign);
            }

            // X ticks at fixed time intervals back from "now" (right edge), e.g. -1m, -2m, ... -5m.
            var xStep = NiceTimeStep(spanSec / 5.0);
            for (var t = xStep; t <= spanSec + xStep / 2; t += xStep)
            {
                var x = leftGutter + plotW * (1 - Math.Min(t, spanSec) / spanSec);
                dc.DrawLine(gridPen, new Point(x, topPad), new Point(x, topPad + plotH));
                DrawText(dc, "-" + FmtSpan(t), x, h - bottomGutter + 1,
                         t >= spanSec ? HAlign.Left : HAlign.Center, VAlign.Below);
            }
            DrawText(dc, "now", leftGutter + plotW, h - bottomGutter + 1, HAlign.Right, VAlign.Below);
        }

        if (data.Count < 2) return;   // frame only — no line yet

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

    /// <summary>Rounds an interval up to a "nice" 1/2/2.5/5×10ⁿ step for readable axis ticks
    /// (2.5 keeps a 0–100 scale at 0/25/50/75/100).</summary>
    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var m = raw / mag;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * mag;
    }

    /// <summary>Rounds a time interval (seconds) up to a clock-friendly step (…30s, 1m, 2m, 5m…)
    /// so a 10-minute window ticks at 2m/4m/6m/8m/10m rather than a decimal-nice step.</summary>
    private static double NiceTimeStep(double rawSeconds)
    {
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        foreach (var s in steps)
            if (s >= rawSeconds) return s;
        return NiceStep(rawSeconds);
    }

    private enum HAlign { Left, Center, Right }
    private enum VAlign { Above, Middle, Below }

    private FormattedText MeasureText(string text)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, AxisBrush, dpi);
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, HAlign h, VAlign v)
    {
        var ft = MeasureText(text);
        var ox = h switch { HAlign.Right => x - ft.Width, HAlign.Center => x - ft.Width / 2, _ => x };
        var oy = v switch { VAlign.Above => y - ft.Height, VAlign.Middle => y - ft.Height / 2, _ => y };
        dc.DrawText(ft, new Point(ox, oy));
    }
}
