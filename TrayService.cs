using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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

        var label = remaining.ToString();
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        using var format = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(label, family, (int)System.Drawing.FontStyle.Bold, 100f, PointF.Empty, format);
        var bounds = path.GetBounds();
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            var scale = Math.Min(size / bounds.Width, size / bounds.Height);
            using var matrix = new Matrix();
            matrix.Translate(-bounds.X, -bounds.Y, MatrixOrder.Append);
            matrix.Scale(scale, scale, MatrixOrder.Append);
            matrix.Translate(
                (size - bounds.Width * scale) / 2f,
                (size - bounds.Height * scale) / 2f,
                MatrixOrder.Append);
            path.Transform(matrix);
            g.FillPath(DrawingBrushes.White, path);
        }

        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        var clone = (Icon)icon.Clone();
        icon.Dispose();
        NativeDestroy(hIcon);
        return clone;
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroy(IntPtr handle);
}
