using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace GrokUsageMonitor;

internal static class WindowAnchor
{
    public const double Width = 400;
    public const double Height = 640;
    private const double EdgeMarginPx = 4;

    public static void SnapToWorkAreaEdge(Window window)
    {
        var work = WorkAreaDip(window);
        var margin = MarginDip(window);
        var size = FitSize(work, margin);
        if (Math.Abs(window.Width - size.Width) > 0.5)
            window.MinWidth = window.MaxWidth = window.Width = size.Width;
        if (Math.Abs(window.Height - size.Height) > 0.5)
            window.MinHeight = window.MaxHeight = window.Height = size.Height;

        var left = Math.Max(work.Left, work.Right - size.Width - margin.Width);
        var top = Math.Max(work.Top, work.Bottom - size.Height - margin.Height);

        if (Math.Abs(window.Left - left) > 1 || Math.Abs(window.Top - top) > 1)
        {
            window.Left = left;
            window.Top = top;
        }
    }

    private static Size FitSize(Rect work, Size margin)
    {
        var availW = Math.Max(1, work.Width - margin.Width * 2);
        var availH = Math.Max(1, work.Height - margin.Height * 2);
        return new Size(Math.Min(Width, availW), Math.Min(Height, availH));
    }

    private static Size MarginDip(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Size(EdgeMarginPx / sx, EdgeMarginPx / sy);
    }

    private static Rect WorkAreaDip(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var screen = hwnd == IntPtr.Zero
            ? System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0]
            : System.Windows.Forms.Screen.FromHandle(hwnd);
        var wa = screen.WorkingArea;
        var topLeft = PixelToDip(window, wa.Left, wa.Top);
        var bottomRight = PixelToDip(window, wa.Right, wa.Bottom);
        return new Rect(topLeft, bottomRight);
    }

    private static Point PixelToDip(Window window, double x, double y)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is { } ct)
            return ct.TransformFromDevice.Transform(new Point(x, y));
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }
}
