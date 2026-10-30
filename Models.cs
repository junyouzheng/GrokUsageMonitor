using System.Text.Json.Serialization;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace GrokUsageMonitor;

public sealed class UsageSnapshot
{
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
    public string Email { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Tier { get; init; } = "";
    public double UsedPercent { get; init; }
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);
    public DateTimeOffset? PeriodStart { get; init; }
    public DateTimeOffset? PeriodEnd { get; init; }
    public string PeriodType { get; init; } = "每周";
    public bool UnifiedPool { get; init; }
    public decimal PrepaidUsd { get; init; }
    public IReadOnlyList<ProductShare> Products { get; init; } = [];
    public string? Error { get; init; }

    public TimeSpan? RemainingTime => PeriodEnd is { } end ? end - DateTimeOffset.Now : null;

    public double ElapsedPercent
    {
        get
        {
            if (PeriodStart is not { } start || PeriodEnd is not { } end) return 0;
            var total = (end - start).TotalSeconds;
            if (total <= 0) return 0;
            return Math.Clamp((DateTimeOffset.Now - start).TotalSeconds / total * 100, 0, 100);
        }
    }

    public string PaceLabel
    {
        get
        {
            if (PeriodStart is null || PeriodEnd is null) return "节奏未知";
            var delta = UsedPercent - ElapsedPercent;
            if (UsedPercent >= 99.5) return "额度已用尽";
            if (delta > 12) return "消耗偏快";
            if (delta < -12) return "消耗偏慢";
            return "节奏正常";
        }
    }

    public string? RunoutLabel
    {
        get
        {
            if (PeriodStart is null || UsedPercent < 0.4) return null;
            var elapsed = DateTimeOffset.Now - PeriodStart.Value;
            if (elapsed.TotalHours < 1) return null;
            var rate = UsedPercent / elapsed.TotalHours;
            if (rate <= 0.01) return null;
            var hoursLeft = RemainingPercent / rate;
            var eta = DateTimeOffset.Now.AddHours(hoursLeft);
            if (PeriodEnd is { } end && eta > end) return "按当前速度，本周期内不会用完";
            return $"按当前速度，约 {FormatRemaining(TimeSpan.FromHours(hoursLeft))} 后用尽";
        }
    }

    public Brush StatusBrush =>
        Error is not null || UsedPercent >= 90 ? Palette.Red
        : UsedPercent >= 75 ? Palette.Amber
        : Palette.Green;

    public string ToDisplayText()
    {
        var products = Products.Count == 0
            ? "  （无拆分）"
            : string.Join(Environment.NewLine, Products.Select(p => $"  {p.Label,-16} {p.Percent,5:0.#}%"));

        return $"""
            账号     {Email}
            套餐     {Tier}
            已用     {UsedPercent:0.#}%
            剩余     {RemainingPercent:0.#}%
            周期     {PeriodType}  {PeriodStart?.ToLocalTime():yyyy-MM-dd HH:mm} ~ {PeriodEnd?.ToLocalTime():yyyy-MM-dd HH:mm}
            重置     {FormatRemaining(RemainingTime)}
            节奏     {PaceLabel}
            预付     ${PrepaidUsd:0.00}
            产品
            {products}
            更新     {FetchedAt:yyyy-MM-dd HH:mm:ss}
            """;
    }

    public static string FormatRemaining(TimeSpan? span)
    {
        if (span is not { } t) return "—";
        if (t.TotalSeconds <= 0) return "已结束";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} 天 {t.Hours} 小时";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        return $"{t.Minutes} 分 {t.Seconds} 秒";
    }
}

internal static class Palette
{
    public static readonly SolidColorBrush Muted = Solid("#8B909A");
    public static readonly SolidColorBrush Red = Solid("#E85D5D");
    public static readonly SolidColorBrush Amber = Solid("#E8B44C");
    public static readonly SolidColorBrush Green = Solid("#7DDAA0");
    public static readonly SolidColorBrush Gold = Solid("#E2B66A");
    public static readonly SolidColorBrush Track = Solid("#2A2E36");

    private static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

public sealed class ProductShare
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public double Percent { get; init; }
}

public sealed class HistoryPoint
{
    public DateTimeOffset At { get; set; }
    public double UsedPercent { get; set; }
}

internal sealed class BillingResponse
{
    [JsonPropertyName("config")] public BillingConfig? Config { get; set; }
    [JsonPropertyName("subscriptionTier")] public string? SubscriptionTier { get; set; }
    [JsonPropertyName("onDemandEnabled")] public bool? OnDemandEnabled { get; set; }
}

internal sealed class BillingConfig
{
    [JsonPropertyName("creditUsagePercent")] public double? CreditUsagePercent { get; set; }
    [JsonPropertyName("currentPeriod")] public UsagePeriodDto? CurrentPeriod { get; set; }
    [JsonPropertyName("productUsage")] public List<ProductUsageDto>? ProductUsage { get; set; }
    [JsonPropertyName("prepaidBalance")] public CentValue? PrepaidBalance { get; set; }
    [JsonPropertyName("isUnifiedBillingUser")] public bool? IsUnifiedBillingUser { get; set; }
    [JsonPropertyName("billingPeriodStart")] public string? BillingPeriodStart { get; set; }
    [JsonPropertyName("billingPeriodEnd")] public string? BillingPeriodEnd { get; set; }
    [JsonPropertyName("onDemandCap")] public CentValue? OnDemandCap { get; set; }
    [JsonPropertyName("onDemandUsed")] public CentValue? OnDemandUsed { get; set; }
}

internal sealed class UsagePeriodDto
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("start")] public string? Start { get; set; }
    [JsonPropertyName("end")] public string? End { get; set; }
}

internal sealed class ProductUsageDto
{
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("usagePercent")] public double? UsagePercent { get; set; }
}

internal sealed class CentValue
{
    [JsonPropertyName("val")] public long Val { get; set; }
}

public sealed class MonitorSettings
{
    public bool StartWithWindows { get; set; }
    public bool NotifyOnThresholds { get; set; } = true;
    public double LastNotifiedPercent { get; set; } = -1;
    public string? LastPeriodEnd { get; set; }
}
