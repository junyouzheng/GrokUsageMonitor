using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace GrokUsageMonitor;

public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE2, 0xB6, 0x6A)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var t = Thickness;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= t) return;

        var radius = (size - t) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var trackPen = new Pen(TrackBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var fillPen = new Pen(FillBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var pct = Math.Clamp(Value, 0, 100) / 100.0;
        if (pct <= 0.0001) return;
        if (pct >= 0.999)
        {
            dc.DrawEllipse(null, fillPen, center, radius, radius);
            return;
        }

        var start = -90 * Math.PI / 180;
        var sweep = pct * 2 * Math.PI;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var p0 = PointOn(center, radius, start);
            var p1 = PointOn(center, radius, start + sweep);
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, fillPen, geo);
    }

    private static Point PointOn(Point c, double r, double radians) =>
        new(c.X + r * Math.Cos(radians), c.Y + r * Math.Sin(radians));
}

public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IList<double>), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE2, 0xB6, 0x6A)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<double>? Values
    {
        get => (IList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4) return;

        dc.DrawLine(TrackPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        var values = Values;
        if (values is null || values.Count < 2) return;
        values = Sample(values, 360);

        var max = Math.Max(1, values.Max());
        var min = Math.Min(0, values.Min());
        var range = Math.Max(1, max - min);
        var step = w / (values.Count - 1);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = i * step;
                var y = h - (values[i] - min) / range * (h - 4) - 2;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geo.Freeze();

        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (var i = 0; i < values.Count; i++)
            {
                var x = i * step;
                var y = h - (values[i] - min) / range * (h - 4) - 2;
                ctx.LineTo(new Point(x, y), true, false);
            }
            ctx.LineTo(new Point(w, h), true, false);
        }
        fillGeo.Freeze();

        var fill = Stroke.Clone();
        fill.Opacity = 0.18;
        fill.Freeze();
        dc.DrawGeometry(fill, null, fillGeo);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, geo);
    }

    private static readonly Pen TrackPen = CreateTrackPen();

    private static Pen CreateTrackPen()
    {
        var pen = new Pen(Palette.Track, 1);
        pen.Freeze();
        return pen;
    }

    private static IList<double> Sample(IList<double> values, int max)
    {
        if (values.Count <= max) return values;
        var sampled = new double[max];
        var count = values.Count;
        for (var i = 0; i < max; i++)
        {
            var start = i * count / max;
            var end = Math.Min(count, Math.Max(start + 1, (i + 1) * count / max));
            var acc = 0d;
            for (var j = start; j < end; j++) acc += values[j];
            sampled[i] = acc / (end - start);
        }

        return sampled;
    }
}

public sealed class PercentBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(PercentBar),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(PercentBar),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE2, 0xB6, 0x6A)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(Palette.Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        var pct = Math.Clamp(Value, 0, 100) / 100.0;
        var fw = Math.Max(h, w * pct);
        if (pct > 0)
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fw, h), h / 2, h / 2);
    }
}
