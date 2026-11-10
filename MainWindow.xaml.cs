using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;

namespace GrokUsageMonitor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly MonitorSettings _settings;
    private readonly TrayService _tray;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _cts = new();
    private UsageSnapshot? _snap;
    private bool _exitRequested;
    private bool _fetching;
    private bool _snapping;
    private bool _ignoreTrayToggle;
    private string? _refreshError;
    private const double StatusStep = 5;
    private double _statusBucket = -1;
    private string? _statusPeriod;
    private bool _statusReady;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        InitializeComponent();
        DataContext = this;

        _tray = new TrayService(ToggleWindow, ExitApp, ToggleStartup, () => _settings.StartWithWindows);
        if (SnapshotStore.Load() is { } cached)
            ApplySnapshot(cached);
        else
        {
            History = HistoryStore.LoadPoints(TimeSpan.FromDays(7)).ToList();
            Raise(nameof(History));
        }

        Topmost = false;
        ShowInTaskbar = false;

        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();
        _usageTimer.Tick += async (_, _) => await RefreshAsync(manual: false);
        _usageTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        SourceInitialized += (_, _) => HideFromTaskbar();
        Loaded += (_, _) => SnapToEdge();
        ContentRendered += (_, _) => SnapToEdge();
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

    public Brush StatusBrush => _snap?.StatusBrush ?? Palette.Muted;
    public Brush RingBrush => UsedPercent >= 90 ? Palette.Red
        : UsedPercent >= 75 ? Palette.Amber
        : Palette.Gold;

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
    public IReadOnlyList<HistoryPoint> History { get; private set; } = Array.Empty<HistoryPoint>();
    public string FooterText => _fetching
        ? "刷新中"
        : _snap?.Error is not null
            ? "刷新失败"
            : _snap is null
                ? "尚未刷新"
                : $"更新于 {FormatFetched(_snap.FetchedAt)}";
    public string ErrorText => _refreshError ?? _snap?.Error ?? "";
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorText) ? Visibility.Collapsed : Visibility.Visible;

    private async Task RefreshAsync(bool manual)
    {
        if (!await _gate.WaitAsync(0)) return;
        if (manual)
        {
            _fetching = true;
            Raise(nameof(FooterText));
        }
        try
        {
            var snap = await Task.Run(() => BillingClient.FetchAsync(_cts.Token));
            var change = ClassifyStatus(snap);
            if (manual || change != UsageChange.None)
            {
                _refreshError = null;
                ApplySnapshot(snap);
                HistoryStore.Append(snap);
                SnapshotStore.Save(snap);
                if (!manual && change != UsageChange.None)
                    ShowUsageNotice(snap, change);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            if (!manual) return;
            _refreshError = ex.Message;
            if (_snap is null || _snap.Error is not null)
            {
                var auth = AuthService.ReadFromDisk();
                ApplySnapshot(new UsageSnapshot
                {
                    Email = auth?.Email ?? "",
                    DisplayName = auth?.DisplayName ?? "",
                    Error = ex.Message
                });
                _tray.SetPercent(0, idle: true, extra: "刷新失败");
            }
            else
            {
                Raise(nameof(FooterText), nameof(ErrorText), nameof(ErrorVisibility));
            }
        }
        finally
        {
            if (manual) _fetching = false;
            _gate.Release();
            if (manual) Raise(nameof(FooterText));
        }
    }

    private enum UsageChange { None, First, Step, NewPeriod }

    // Status moves once per 5% of used quota: 5, 10, 15 … 100.
    // A check that skips several marks updates and notifies only the highest one.
    private UsageChange ClassifyStatus(UsageSnapshot snap)
    {
        if (snap.Error is not null) return UsageChange.None;
        var period = snap.PeriodEnd?.ToString("o");
        var bucket = StatusBucket(snap.UsedPercent);
        if (!_statusReady)
            return bucket >= StatusStep ? UsageChange.First : UsageChange.None;
        if (!string.Equals(period, _statusPeriod, StringComparison.Ordinal))
            return UsageChange.NewPeriod;
        return bucket >= StatusStep && bucket > _statusBucket
            ? UsageChange.Step
            : UsageChange.None;
    }

    private void ShowUsageNotice(UsageSnapshot snap, UsageChange change)
    {
        string title;
        var detail = $"已用 {snap.UsedPercent:0.#}%，剩余 {snap.RemainingPercent:0.#}%";
        if (change == UsageChange.NewPeriod)
            title = "新周期已开始";
        else if (snap.UsedPercent >= 99.5)
        {
            title = "额度已用尽";
            detail = "本周期额度已经用完";
        }
        else
            title = $"用量已跨过 {StatusBucket(snap.UsedPercent):0}%";

        UsageToast.Show(title, detail, RestoreWindow, dockTop: IsVisible);
    }

    private void RememberStatus(UsageSnapshot snap)
    {
        _statusReady = true;
        _statusPeriod = snap.PeriodEnd?.ToString("o");
        _statusBucket = StatusBucket(snap.UsedPercent);
    }

    private static double StatusBucket(double usedPercent)
    {
        if (usedPercent >= 100) return 100;
        if (usedPercent < StatusStep) return 0;
        return Math.Floor(usedPercent / StatusStep) * StatusStep;
    }

    private void ApplySnapshot(UsageSnapshot snap)
    {
        _snap = snap;
        Products.Clear();
        foreach (var p in snap.Products) Products.Add(p);
        History = HistoryStore.LoadPoints(TimeSpan.FromDays(7)).ToList();

        if (snap.Error is null)
        {
            var remain = UsageSnapshot.FormatRemaining(snap.RemainingTime);
            _tray.SetPercent(snap.UsedPercent, extra: $"{snap.Tier} · {remain}");
            RememberStatus(snap);
        }

        Raise(
            nameof(StatusBrush), nameof(RingBrush), nameof(AccountLine), nameof(Email), nameof(Tier),
            nameof(UsedPercent), nameof(RemainingText), nameof(UsedLine), nameof(ResetLine),
            nameof(PaceLine), nameof(RunoutLine), nameof(PeriodType), nameof(PeriodRange),
            nameof(PrepaidText), nameof(PoolHint), nameof(EmptyProducts), nameof(History),
            nameof(FooterText), nameof(ErrorText), nameof(ErrorVisibility));
    }

    private static string FormatFetched(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm:ss")
            : local.ToString("MM-dd HH:mm");
    }

    private string _resetShown = "";
    private string _paceShown = "";
    private string _runoutShown = "";

    private void RefreshClock()
    {
        if (_snap is null) return;
        var reset = ResetLine;
        var pace = PaceLine;
        var runout = RunoutLine ?? "";
        if (reset != _resetShown) { _resetShown = reset; Raise(nameof(ResetLine)); }
        if (pace != _paceShown) { _paceShown = pace; Raise(nameof(PaceLine)); }
        if (runout != _runoutShown) { _runoutShown = runout; Raise(nameof(RunoutLine)); }
        if (_snap.Error is null)
            _tray.SetPercent(_snap.UsedPercent, extra: $"{_snap.Tier} · {UsageSnapshot.FormatRemaining(_snap.RemainingTime)}");
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync(manual: true);

    private void ToggleStartup()
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        AppSettings.Save(_settings);
    }

    private void ToggleWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (IsVisible)
                HideToTray();
            else if (!_ignoreTrayToggle)
                RestoreWindow();
        });
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = false;
        Show();
        WindowState = WindowState.Normal;
        SnapToEdge();
        Activate();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void SnapToEdge()
    {
        if (_snapping || !IsVisible) return;
        _snapping = true;
        try { WindowAnchor.SnapToWorkAreaEdge(this); }
        finally { _snapping = false; }
    }

    private void OnActivated(object sender, EventArgs e)
    {
        if (IsVisible) SnapToEdge();
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (!IsVisible || _exitRequested) return;
        HideToTray();
        // Tray left-click deactivates the window first, then fires MouseClick.
        // Ignore that same click so the window stays hidden instead of toggling back open.
        _ignoreTrayToggle = true;
        Dispatcher.BeginInvoke(() => _ignoreTrayToggle = false, DispatcherPriority.ApplicationIdle);
    }

    private void OnLocationChanged(object sender, EventArgs e)
    {
        if (!_snapping && IsVisible)
            SnapToEdge();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(SnapToEdge);

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
        _usageTimer.Stop();
        UsageToast.Hide();
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void Raise(params string[] names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
