using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DrawingFont = System.Drawing.Font;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace GrokUsageMonitor;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon?[] _icons = new Icon?[101];
    private int _shown = -1;
    private readonly Action _toggle;
    private readonly Action _exit;
    private readonly Action _toggleStartup;
    private readonly Func<bool> _startupEnabled;

    public TrayService(Action toggle, Action exit, Action toggleStartup, Func<bool> startupEnabled)
    {
        _toggle = toggle;
        _exit = exit;
        _toggleStartup = toggleStartup;
        _startupEnabled = startupEnabled;

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
        var remaining = Math.Clamp(100 - used, 0, 100);
        var bucket = (int)Math.Round(remaining);
        if (_shown != bucket)
        {
            _shown = bucket;
            _icon.Icon = _icons[bucket] ??= MakeIcon(bucket);
        }

        var tip = idle
            ? "Grok 用量监控"
            : $"Grok 剩余 {remaining:0.#}%  ·  已用 {used:0.#}%";
        if (!string.IsNullOrWhiteSpace(extra))
            tip += "\n" + extra;
        if (tip.Length > 63) tip = tip[..63];
        if (_icon.Text != tip) _icon.Text = tip;
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
        foreach (var icon in _icons) icon?.Dispose();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        var startup = new Forms.ToolStripMenuItem("开机自启动") { Checked = _startupEnabled() };
        startup.Click += (_, _) =>
        {
            _toggleStartup();
            startup.Checked = _startupEnabled();
        };
        menu.Opening += (_, _) => startup.Checked = _startupEnabled();
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _exit());
        return menu;
    }

    private static Icon MakeIcon(int remaining)
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(DrawingColor.Transparent);

        var box = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
        using var shape = RoundedRect(box, 12f);
        using (var bg = new SolidBrush(DrawingColor.Black))
            g.FillPath(bg, shape);

        var label = remaining.ToString();
        var fontSize = label.Length >= 3 ? 22f : label.Length == 2 ? 32f : 40f;
        using var font = new DrawingFont("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(label, font, DrawingBrushes.White, new RectangleF(0f, 2f, size, size - 2f), format);

        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        var clone = (Icon)icon.Clone();
        icon.Dispose();
        NativeDestroy(hIcon);
        return clone;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroy(IntPtr handle);
}
