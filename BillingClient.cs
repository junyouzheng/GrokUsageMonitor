using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Path = System.IO.Path;
using File = System.IO.File;

namespace GrokUsageMonitor;

internal static class BillingClient
{
    private const string ProxyBase = "https://cli-chat-proxy.grok.com/v1";
    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Dictionary<string, string> ProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GrokBot"] = "Grok 对话",
        ["GrokBuild"] = "Grok Build",
        ["Imagine"] = "Imagine",
        ["Voice"] = "Voice",
        ["Api"] = "API",
        ["Cursor"] = "Cursor"
    };

    public static async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        var auth = await AuthService.LoadFreshAsync(ct);
        try
        {
            return await FetchWithAuthAsync(auth, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            auth = await AuthService.RefreshAsync(auth, ct);
            return await FetchWithAuthAsync(auth, ct);
        }
    }

    private static async Task<UsageSnapshot> FetchWithAuthAsync(GrokAuth auth, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"{ProxyBase}/billing?format=credits", auth);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 401)
                throw new HttpRequestException("未授权", null, resp.StatusCode);
            throw new InvalidOperationException($"读取额度失败（HTTP {(int)resp.StatusCode}）");
        }

        var billing = JsonSerializer.Deserialize<BillingResponse>(body, JsonOpts)
                      ?? throw new InvalidOperationException("额度响应无法解析。");
        var cfg = billing.Config ?? new BillingConfig();

        var used = cfg.CreditUsagePercent
                   ?? DerivePercent(cfg.OnDemandUsed, cfg.OnDemandCap)
                   ?? 0;

        var start = ParseTime(cfg.CurrentPeriod?.Start) ?? ParseTime(cfg.BillingPeriodStart);
        var end = ParseTime(cfg.CurrentPeriod?.End) ?? ParseTime(cfg.BillingPeriodEnd);
        var periodType = PeriodLabel(cfg.CurrentPeriod?.Type, start, end);
        var prepaidCents = cfg.PrepaidBalance?.Val ?? 0;
        var products = (cfg.ProductUsage ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Product))
            .Select(p => new ProductShare
            {
                Id = p.Product!,
                Label = ProductNames.TryGetValue(p.Product!, out var label) ? label : p.Product!,
                Percent = p.UsagePercent ?? 0
            })
            .OrderByDescending(p => p.Percent)
            .ToList();

        var tier = billing.SubscriptionTier;
        if (string.IsNullOrWhiteSpace(tier))
            tier = await TryReadTierAsync(auth, ct) ?? "SuperGrok";
        var email = await TryReadEmailAsync(auth, ct) ?? auth.Email;

        return new UsageSnapshot
        {
            FetchedAt = DateTimeOffset.Now,
            Email = email,
            DisplayName = auth.DisplayName,
            Tier = tier,
            UsedPercent = used,
            PeriodStart = start,
            PeriodEnd = end,
            PeriodType = periodType,
            UnifiedPool = cfg.IsUnifiedBillingUser == true,
            PrepaidUsd = prepaidCents / 100m,
            Products = products
        };
    }

    private static async Task<string?> TryReadEmailAsync(GrokAuth auth, CancellationToken ct)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Get, $"{ProxyBase}/user", auth);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("email", out var em) &&
                em.GetString() is { Length: > 0 } email)
                return email;
        }
        catch
        {
            // fall back to auth.json
        }

        return null;
    }

    private static async Task<string?> TryReadTierAsync(GrokAuth auth, CancellationToken ct)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Get, $"{ProxyBase}/settings", auth);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return ReadCachedTier();
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            return ReadTierFromJson(body) ?? ReadCachedTier();
        }
        catch
        {
            return ReadCachedTier();
        }
    }

    private static string? ReadTierFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(payload.GetString()!);
                root = inner.RootElement.TryGetProperty("settings", out var settings) ? settings : inner.RootElement;
            }
            else if (root.TryGetProperty("settings", out var settings))
            {
                root = settings;
            }

            if (root.TryGetProperty("subscription_tier_display", out var display) &&
                display.GetString() is { Length: > 0 } name)
                return name;
            if (root.TryGetProperty("subscription_tier", out var tier) &&
                tier.GetString() is { Length: > 0 } raw)
                return raw;
        }
        catch
        {
            // ignore malformed settings
        }

        return null;
    }

    private static string? ReadCachedTier()
    {
        try
        {
            var path = Path.Combine(AuthService.GrokHome, "settings_cache.json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return ReadTierFromJson(json);
        }
        catch
        {
            return null;
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, GrokAuth auth)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
        req.Headers.TryAddWithoutValidation("x-grok-client-mode", "cli");
        if (!string.IsNullOrWhiteSpace(auth.UserId))
            req.Headers.TryAddWithoutValidation("x-userid", auth.UserId);
        return req;
    }

    private static double? DerivePercent(CentValue? used, CentValue? cap)
    {
        if (used is null || cap is null || cap.Val <= 0) return null;
        return used.Val * 100.0 / cap.Val;
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var t) ? t : null;

    private static string PeriodLabel(string? type, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (type?.Contains("WEEKLY", StringComparison.OrdinalIgnoreCase) == true) return "每周";
        if (type?.Contains("MONTHLY", StringComparison.OrdinalIgnoreCase) == true) return "每月";
        if (start is { } s && end is { } e)
        {
            var days = (e - s).TotalDays;
            if (days is >= 4 and <= 12) return "每周";
            if (days is >= 20 and <= 45) return "每月";
        }

        return "周期";
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GrokUsageMonitor/1.0");
        return client;
    }
}
