using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace GrokUsageMonitor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly MonitorSettings _settings;
    private readonly TrayService _tray;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _cts = new();
    private UsageSnapshot? _snap;
    private bool _exitRequested;
    private bool _fetching;
    private bool _snapping;
    private static readonly double[] NotifyMarks = [50, 80, 90, 100];

    public MainWindow()
    {
        _settings = AppSettings.Load();
        InitializeComponent();
        DataContext = this;

        _tray = new TrayService(ToggleWindow, RestoreWindow, ExitApp);

        Topmost = false;
        ShowInTaskbar = false;

        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        SourceInitialized += (_, _) => HideFromTaskbar();
        Loaded += async (_, _) =>
        {
            ApplyStartupCaption();
            SnapToIcon();
            await RefreshAsync(true);
        };
        ContentRendered += (_, _) => SnapToIcon();
    }

    private void HideFromTaskbar()
    {
        ShowInTaskbar = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, (ex | WsExToolWindow) & ~WsExAppWindow);
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExAppWindow = 0x40000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string LiveLabel => _fetching ? "刷新中" : "手动";
    public Brush StatusBrush => _snap?.StatusBrush ?? BrushOf("#8B909A");
    public Brush RingBrush => UsedPercent >= 90 ? BrushOf("#E85D5D")
        : UsedPercent >= 75 ? BrushOf("#E8B44C")
        : BrushOf("#E2B66A");

    public string AccountLine => string.IsNullOrWhiteSpace(_snap?.DisplayName) ? "Grok 账号" : _snap!.DisplayName;
    public string Email => _snap?.Email ?? "读取 ~/.grok/auth.json";
    public string Tier => string.IsNullOrWhiteSpace(_snap?.Tier) ? "—" : _snap!.Tier;
    public double UsedPercent => _snap?.UsedPercent ?? 0;
    public string RemainingText => _snap is null ? "—" : $"{_snap.RemainingPercent:0.#}%";
    public string UsedLine => _snap is null ? "等待首次刷新" : $"已用 {_snap.UsedPercent:0.#}%";
    public string ResetLine => _snap?.PeriodEnd is null
        ? "重置时间未知"
        : $"重置倒计时  {UsageSnapshot.FormatRemaining(_snap.RemainingTime)}";
    public string PaceLine => _snap is null ? "" : _snap.PaceLabel;
    public string RunoutLine => _snap?.RunoutLabel ?? "";
    public string PeriodType => _snap?.PeriodType ?? "周期";
    public string PeriodRange => _snap?.PeriodStart is { } s && _snap.PeriodEnd is { } e
        ? $"{s.ToLocalTime():MM-dd HH:mm}  →  {e.ToLocalTime():MM-dd HH:mm}"
        : "—";
    public string PrepaidText => _snap is null ? "$0.00" : $"${_snap.PrepaidUsd:0.00}";
    public string PoolHint => _snap?.UnifiedPool == true
        ? "Chat / Imagine / Build 共用同一额度池"
        : "订阅周额度";
    public ObservableCollection<ProductShare> Products { get; } = new();
    public string EmptyProducts => Products.Count == 0 ? "暂无产品拆分" : "";
    public IList<double> History { get; private set; } = Array.Empty<double>();
    public string FooterText => _snap?.Error is not null
        ? "上次刷新失败"
        : _snap is null
            ? "尚未刷新"
            : $"更新于 {_snap.FetchedAt:HH:mm:ss}";
    public string ErrorText => _snap?.Error ?? "";
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorText) ? Visibility.Collapsed : Visibility.Visible;

    public string? Error => _snap?.Error;

    private async Task RefreshAsync(bool force)
    {
        if (!force && _fetching) return;
        if (!await _gate.WaitAsync(0)) return;
        _fetching = true;
        Raise(nameof(LiveLabel));
        try
        {
            var snap = await Task.Run(() => BillingClient.FetchAsync(_cts.Token));
            ApplySnapshot(snap);
            HistoryStore.Append(snap);
            MaybeNotify(snap);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            ApplySnapshot(new UsageSnapshot
            {
                Email = AuthService.ReadFromDisk()?.Email ?? "",
                DisplayName = AuthService.ReadFromDisk()?.DisplayName ?? "",
                Error = ex.Message
            });
            _tray.SetPercent(0, idle: true, extra: "刷新失败");
        }
        finally
        {
            _fetching = false;
            _gate.Release();
            Raise(nameof(LiveLabel), nameof(FooterText));
        }
    }

    private void ApplySnapshot(UsageSnapshot snap)
    {
        _snap = snap;
        Products.Clear();
        foreach (var p in snap.Products) Products.Add(p);
        History = HistoryStore.LoadPercents(TimeSpan.FromDays(7)).ToList();

        if (snap.Error is null)
        {
            var remain = UsageSnapshot.FormatRemaining(snap.RemainingTime);
            _tray.SetPercent(snap.UsedPercent, extra: $"{snap.Tier} · {remain}");
        }

        Raise(
            nameof(StatusBrush), nameof(RingBrush), nameof(AccountLine), nameof(Email), nameof(Tier),
            nameof(UsedPercent), nameof(RemainingText), nameof(UsedLine), nameof(ResetLine),
            nameof(PaceLine), nameof(RunoutLine), nameof(PeriodType), nameof(PeriodRange),
            nameof(PrepaidText), nameof(PoolHint), nameof(EmptyProducts), nameof(History),
            nameof(FooterText), nameof(ErrorText), nameof(ErrorVisibility));
    }

    private void MaybeNotify(UsageSnapshot snap)
    {
        if (!_settings.NotifyOnThresholds || snap.Error is not null) return;

        var periodKey = snap.PeriodEnd?.ToString("o");
        if (!string.Equals(periodKey, _settings.LastPeriodEnd, StringComparison.Ordinal))
        {
            _settings.LastNotifiedPercent = -1;
            _settings.LastPeriodEnd = periodKey;
        }

        foreach (var mark in NotifyMarks)
        {
            if (snap.UsedPercent >= mark && _settings.LastNotifiedPercent < mark)
            {
                _settings.LastNotifiedPercent = mark;
                AppSettings.Save(_settings);
                var remain = snap.RemainingPercent;
                _tray.Notify("Grok 额度提醒",
                    mark >= 100
                        ? "本周额度已用尽。可购买额外额度，或等待周期重置。"
                        : $"已用 {snap.UsedPercent:0.#}%（阈值 {mark:0}%）。剩余 {remain:0.#}%。");
                break;
            }
        }
    }

    private void RefreshClock()
    {
        if (_snap is null) return;
        Raise(nameof(ResetLine), nameof(PaceLine), nameof(RunoutLine), nameof(FooterText));
        if (_snap.Error is null)
            _tray.SetPercent(_snap.UsedPercent, extra: $"{_snap.Tier} · {UsageSnapshot.FormatRemaining(_snap.RemainingTime)}");
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync(true);

    private void ToggleStartup(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        AppSettings.Save(_settings);
        ApplyStartupCaption();
    }

    private void ApplyStartupCaption()
    {
        StartupButton.Content = _settings.StartWithWindows ? "已开机启动" : "开机启动";
    }

    private void ToggleWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (IsVisible)
                HideToTray();
            else
                RestoreWindow();
        });
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = false;
        Show();
        WindowState = WindowState.Normal;
        SnapToIcon();
        Activate();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void SnapToIcon()
    {
        if (_snapping || !IsVisible) return;
        _snapping = true;
        try { WindowAnchor.SnapAboveTray(this, _tray.TryGetIconRect()); }
        finally { _snapping = false; }
    }

    private void OnActivated(object sender, EventArgs e)
    {
        if (IsVisible) SnapToIcon();
    }

    private void OnLocationChanged(object sender, EventArgs e)
    {
        if (!_snapping && IsVisible)
            SnapToIcon();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(SnapToIcon);

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        HideToTray();
    }

    private void ExitApp()
    {
        _exitRequested = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _cts.Cancel();
        _clockTimer.Stop();
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void Raise(params string[] names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private static SolidColorBrush BrushOf(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
}
