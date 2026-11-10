using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Forms = System.Windows.Forms;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace GrokUsageMonitor;

internal static class UsageToast
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private static readonly SolidColorBrush CardBg = Freeze(Color.FromRgb(0x16, 0x18, 0x1D));
    private static readonly SolidColorBrush CardBorder = Freeze(Color.FromRgb(0x2A, 0x2E, 0x36));
    private static readonly SolidColorBrush TitleFg = Freeze(Color.FromRgb(0xF3, 0xEE, 0xE4));

    private static Window? _window;
    private static TextBlock? _title;
    private static TextBlock? _detail;
    private static Action? _open;
    private static bool _hovered;

    static UsageToast()
    {
        Timer.Tick += (_, _) => Hide();
    }

    public static void Show(string title, string detail, Action open, bool dockTop)
    {
        _open = open;
        if (_window is null)
            _window = Create();

        _title!.Text = title;
        _detail!.Text = detail;
        if (!_window.IsVisible)
            _window.Show();
        _window.UpdateLayout();
        Place(_window, dockTop);
        SystemSounds.Asterisk.Play();
        ArmTimer();
    }

    public static void Hide()
    {
        Timer.Stop();
        _hovered = false;
        var window = _window;
        _window = null;
        _title = null;
        _detail = null;
        _open = null;
        if (window is not null)
            window.Close();
    }

    private static void ArmTimer()
    {
        if (_hovered) return;
        Timer.Stop();
        Timer.Start();
    }

    private static Window Create()
    {
        var title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TitleFg,
            TextWrapping = TextWrapping.Wrap
        };
        var detail = new TextBlock
        {
            FontSize = 12,
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var hint = new TextBlock
        {
            Text = "点击打开",
            FontSize = 11,
            Foreground = Palette.Gold,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var close = new TextBlock
        {
            Text = "×",
            FontSize = 16,
            Foreground = Palette.Muted,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, -2, 0, 0)
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Hide();
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var text = new StackPanel();
        text.Children.Add(header);
        text.Children.Add(detail);
        text.Children.Add(hint);

        var card = new Border
        {
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 12, 12),
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black
            },
            Child = new DockPanel { LastChildFill = true }
        };
        var accent = new Border
        {
            Width = 3,
            Background = Palette.Gold,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 12, 0)
        };
        DockPanel.SetDock(accent, Dock.Left);
        ((DockPanel)card.Child).Children.Add(accent);
        ((DockPanel)card.Child).Children.Add(text);

        card.MouseEnter += (_, _) =>
        {
            _hovered = true;
            Timer.Stop();
        };
        card.MouseLeave += (_, _) =>
        {
            _hovered = false;
            if (_window?.IsVisible == true) ArmTimer();
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            var open = _open;
            Hide();
            open?.Invoke();
        };

        var window = new Window
        {
            Title = "Grok 用量通知",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Focusable = false,
            Content = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(16),
                Child = card
            }
        };
        window.SourceInitialized += (_, _) => ApplyNoActivate(window);
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            Timer.Stop();
            _window = null;
            _title = null;
            _detail = null;
            _open = null;
        };
        _title = title;
        _detail = detail;
        return window;
    }

    private static void Place(Window window, bool dockTop)
    {
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var wa = screen.WorkingArea;
        var topLeft = PixelToDip(window, wa.Left, wa.Top);
        var bottomRight = PixelToDip(window, wa.Right, wa.Bottom);
        var width = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 1 ? window.ActualHeight : 96;
        window.Left = Math.Max(topLeft.X, bottomRight.X - width);
        window.Top = dockTop
            ? topLeft.Y
            : Math.Max(topLeft.Y, bottomRight.Y - height);
    }

    private static Point PixelToDip(Window window, double x, double y)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is { } ct)
            return ct.TransformFromDevice.Transform(new Point(x, y));
        var dpi = VisualTreeHelper.GetDpi(window);
        var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Point(x / sx, y / sy);
    }

    private static void ApplyNoActivate(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, (ex | WsExNoActivate | WsExToolWindow) & ~WsExAppWindow);
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;
    private const int WsExAppWindow = 0x40000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
