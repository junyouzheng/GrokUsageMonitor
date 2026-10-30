using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using DrawingFont = System.Drawing.Font;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;
using Rect = System.Windows.Rect;

namespace GrokUsageMonitor;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private Icon? _current;
    private readonly Action _toggle;
    private readonly Action _show;
    private readonly Action _exit;

    public TrayService(Action toggle, Action show, Action exit)
    {
        _toggle = toggle;
        _show = show;
        _exit = exit;

        _icon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "Grok 用量监控",
            ContextMenuStrip = BuildMenu()
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _toggle();
        };
        SetPercent(0, idle: true);
    }

    public void SetPercent(double used, bool idle = false, string? extra = null)
    {
        var remaining = Math.Max(0, 100 - used);
        var color = idle
            ? DrawingColor.FromArgb(0x8B, 0x90, 0x9A)
            : used >= 90 ? DrawingColor.FromArgb(0xE8, 0x5D, 0x5D)
            : used >= 75 ? DrawingColor.FromArgb(0xE8, 0xB4, 0x4C)
            : DrawingColor.FromArgb(0x7D, 0xDA, 0xA0);

        var next = MakeIcon(used, color);
        _icon.Icon = next;
        _current?.Dispose();
        _current = next;

        var tip = idle
            ? "Grok 用量监控"
            : $"Grok 剩余 {remaining:0.#}%  ·  已用 {used:0.#}%";
        if (!string.IsNullOrWhiteSpace(extra))
            tip += "\n" + extra;
        if (tip.Length > 63) tip = tip[..63];
        _icon.Text = tip;
    }

    public Rect? TryGetIconRect()
    {
        try
        {
            var id = ReadField<int>(_icon, "id");
            var native = ReadField<object>(_icon, "window") ?? ReadField<object>(_icon, "_window");
            if (native is null) return null;
            var hwnd = native is Forms.NativeWindow nw
                ? nw.Handle
                : (IntPtr?)native.GetType().GetProperty("Handle")?.GetValue(native) ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return null;

            var ident = new NotifyIconIdentifier
            {
                CbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                Hwnd = hwnd,
                Uid = (uint)id
            };
            if (Shell_NotifyIconGetRect(ref ident, out var r) != 0) return null;
            if (r.Right <= r.Left || r.Bottom <= r.Top) return null;
            return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        catch
        {
            return null;
        }
    }

    private static T? ReadField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field is null) return default;
        var value = field.GetValue(target);
        return value is T t ? t : default;
    }

    public void Notify(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => _show());
        menu.Items.Add("打开 grok.com 用量", null, (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://grok.com/",
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _exit());
        return menu;
    }

    private static Icon MakeIcon(double used, DrawingColor accent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(DrawingColor.Transparent);

        using var track = new Pen(DrawingColor.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 3f);
        using var fill = new Pen(accent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var rect = new RectangleF(3, 3, size - 7, size - 7);
        g.DrawEllipse(track, rect);

        var sweep = (float)(Math.Clamp(used, 0, 100) / 100.0 * 360);
        if (sweep > 0.5f)
            g.DrawArc(fill, rect, -90, sweep);

        var label = used >= 99.5 ? "!" : ((int)Math.Round(100 - used)).ToString();
        if (label.Length > 2) label = "99";
        using var font = new DrawingFont("Segoe UI", label.Length == 1 ? 9.5f : 7.5f, System.Drawing.FontStyle.Bold);
        var sizeF = g.MeasureString(label, font);
        g.DrawString(label, font, DrawingBrushes.White,
            (size - sizeF.Width) / 2,
            (size - sizeF.Height) / 2 + 0.5f);

        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        var clone = (Icon)icon.Clone();
        icon.Dispose();
        NativeDestroy(hIcon);
        return clone;
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroy(IntPtr handle);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint CbSize;
        public IntPtr Hwnd;
        public uint Uid;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
