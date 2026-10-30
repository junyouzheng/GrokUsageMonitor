using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace GrokUsageMonitor;

internal static class WindowAnchor
{
    public const double Width = 420;
    public const double Height = 720;

    public static void SnapAboveTray(Window window, Rect? iconPixels)
    {
        var work = WorkAreaDip(window);
        var size = FitSize(work);
        window.MinWidth = window.MaxWidth = window.Width = size.Width;
        window.MinHeight = window.MaxHeight = window.Height = size.Height;

        var icon = iconPixels is { } px && px.Width > 0
            ? PixelRectToDip(window, px)
            : new Rect(work.Right - 24, work.Bottom, 24, 1);

        var left = icon.X + icon.Width / 2 - size.Width / 2;
        var top = icon.Y - size.Height - 6;

        if (icon.Y + icon.Height / 2 < work.Top + work.Height * 0.25)
            top = icon.Y + icon.Height + 6;
        else if (icon.X + icon.Width / 2 < work.Left + 40)
        {
            left = icon.X + icon.Width + 6;
            top = icon.Y + icon.Height / 2 - size.Height / 2;
        }
        else if (icon.X > work.Right - 40)
        {
            left = icon.X - size.Width - 6;
            top = icon.Y + icon.Height / 2 - size.Height / 2;
        }

        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - size.Width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        if (Math.Abs(window.Left - left) > 1 || Math.Abs(window.Top - top) > 1)
        {
            window.Left = left;
            window.Top = top;
        }
    }

    private static Size FitSize(Rect work) =>
        new(Math.Min(Width, Math.Max(320, work.Width)),
            Math.Min(Height, Math.Max(360, work.Height)));

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

    private static Rect PixelRectToDip(Window window, Rect px)
    {
        var a = PixelToDip(window, px.X, px.Y);
        var b = PixelToDip(window, px.X + px.Width, px.Y + px.Height);
        return new Rect(a, b);
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
