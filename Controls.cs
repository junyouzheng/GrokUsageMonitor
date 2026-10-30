using System.Globalization;
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
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<HistoryPoint>), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE2, 0xB6, 0x6A)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    private HistoryPoint[] _source = [];
    private int _hover = -1;

    public Sparkline()
    {
        Cursor = System.Windows.Input.Cursors.Cross;
    }

    public IReadOnlyList<HistoryPoint>? Points
    {
        get => (IReadOnlyList<HistoryPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        var next = IndexAt(e.GetPosition(this).X);
        if (next == _hover) return;
        _hover = next;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4) return;

        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawLine(TrackPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        var source = Points;
        _source = source is null || source.Count == 0 ? [] : source as HistoryPoint[] ?? source.ToArray();
        if (_hover >= _source.Length) _hover = -1;
        if (_source.Length == 0) return;

        var max = 1d;
        var min = 0d;
        foreach (var point in _source)
        {
            if (point.UsedPercent > max) max = point.UsedPercent;
            if (point.UsedPercent < min) min = point.UsedPercent;
        }

        var range = Math.Max(1, max - min);
        var drawn = Sample(_source, 360);
        if (drawn.Length >= 2)
        {
            var step = w / (drawn.Length - 1);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (var i = 0; i < drawn.Length; i++)
                {
                    var p = new Point(i * step, YAt(drawn[i].UsedPercent, min, range, h));
                    if (i == 0) ctx.BeginFigure(p, false, false);
                    else ctx.LineTo(p, true, false);
                }
            }
            geo.Freeze();

            var fillGeo = new StreamGeometry();
            using (var ctx = fillGeo.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                for (var i = 0; i < drawn.Length; i++)
                    ctx.LineTo(new Point(i * step, YAt(drawn[i].UsedPercent, min, range, h)), true, false);
                ctx.LineTo(new Point(w, h), true, false);
            }
            fillGeo.Freeze();

            var fill = Stroke.Clone();
            fill.Opacity = 0.18;
            fill.Freeze();
            dc.DrawGeometry(fill, null, fillGeo);
            dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, geo);
        }

        if (_hover < 0) return;
        var x = _source.Length == 1 ? w / 2 : _hover * (w / (_source.Length - 1));
        var y = YAt(_source[_hover].UsedPercent, min, range, h);
        dc.DrawLine(GuidePen, new Point(x, 0), new Point(x, h));
        dc.DrawEllipse(Stroke, DotPen, new Point(x, y), 3.2, 3.2);
        DrawLabel(dc, _source[_hover], x, y, w, h, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static void DrawLabel(DrawingContext dc, HistoryPoint point, double x, double y, double w, double h, double dpi)
    {
        var local = point.At.ToLocalTime();
        var text = new FormattedText(
            $"已用 {point.UsedPercent:0.#}%   {local:MM-dd HH:mm}",
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            LabelText,
            dpi);
        const double padX = 7;
        const double padY = 3;
        var boxW = text.Width + padX * 2;
        var boxH = text.Height + padY * 2;
        var left = Math.Clamp(x - boxW / 2, 2, Math.Max(2, w - boxW - 2));
        var top = y - boxH - 8;
        if (top < 2) top = Math.Min(h - boxH - 2, y + 8);
        dc.DrawRoundedRectangle(LabelFill, null, new Rect(left, top, boxW, boxH), 4, 4);
        dc.DrawText(text, new Point(left + padX, top + padY));
    }

    private int IndexAt(double x)
    {
        var count = _source.Length;
        if (count == 0 || ActualWidth <= 0) return -1;
        if (count == 1) return 0;
        var t = Math.Clamp(x / ActualWidth, 0, 1);
        return (int)Math.Round(t * (count - 1));
    }

    private static double YAt(double value, double min, double range, double h) =>
        h - (value - min) / range * (h - 4) - 2;

    private static readonly Brush DotFill = Freeze(new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x10)));
    private static readonly Pen TrackPen = CreatePen(Palette.Track, 1);
    private static readonly Pen GuidePen = CreatePen(Palette.Muted, 1);
    private static readonly Pen DotPen = CreatePen(DotFill, 1.2);
    private static readonly Brush LabelText = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xEE, 0xE4)));
    private static readonly Brush LabelFill = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x18, 0x1E)));

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    // Each vertex is a real reading, so the hover time is that reading's time.
    private static HistoryPoint[] Sample(IReadOnlyList<HistoryPoint> points, int max)
    {
        if (points.Count <= max) return points.ToArray();
        var sampled = new HistoryPoint[max];
        var last = points.Count - 1;
        for (var i = 0; i < max; i++)
        {
            var index = i == max - 1 ? last : (int)((long)i * last / (max - 1));
            sampled[i] = points[index];
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
