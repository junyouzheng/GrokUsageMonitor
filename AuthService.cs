using System.IO;
using System.Net.Http;
using System.Text.Json;
using Path = System.IO.Path;
using File = System.IO.File;

namespace GrokUsageMonitor;

internal sealed class GrokAuth
{
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Email { get; init; } = "";
    public string UserId { get; init; } = "";
    public string ClientId { get; init; } = "b1a00492-073a-47ea-816f-4c329264a828";
    public string Issuer { get; init; } = "https://auth.x.ai";
    public string DisplayName { get; init; } = "";
}

internal static class AuthService
{
    private static readonly HttpClient Http = CreateHttp();
    private static GrokAuth? _memory;

    public static string GrokHome =>
        Environment.GetEnvironmentVariable("GROK_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");

    public static string AuthPath => Path.Combine(GrokHome, "auth.json");

    public static async Task<GrokAuth> LoadFreshAsync(CancellationToken ct)
    {
        var auth = ReadFromDisk() ?? _memory
            ?? throw new InvalidOperationException("未找到 Grok 登录信息。请先运行 grok login。");

        if (auth.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5) &&
            !string.IsNullOrWhiteSpace(auth.AccessToken))
        {
            _memory = auth;
            return auth;
        }

        if (string.IsNullOrWhiteSpace(auth.RefreshToken))
            throw new InvalidOperationException("登录已过期，且没有刷新令牌。请运行 grok login。");

        auth = await RefreshAsync(auth, ct);
        _memory = auth;
        return auth;
    }

    public static async Task<GrokAuth> RefreshAsync(GrokAuth auth, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = auth.RefreshToken ?? "",
            ["client_id"] = auth.ClientId
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://auth.x.ai/oauth2/token")
        {
            Content = content
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("刷新登录失败，请运行 grok login。");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        auth.AccessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("刷新登录失败：响应缺少 access_token。");
        if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } next)
        {
            // Keep the new refresh token in memory only. Do not rewrite grok's auth.json.
            auth = new GrokAuth
            {
                AccessToken = auth.AccessToken,
                RefreshToken = next,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 21600),
                Email = auth.Email,
                UserId = auth.UserId,
                ClientId = auth.ClientId,
                Issuer = auth.Issuer,
                DisplayName = auth.DisplayName
            };
        }
        else
        {
            var seconds = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 21600;
            auth.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        _memory = auth;
        return auth;
    }

    public static GrokAuth? ReadFromDisk()
    {
        if (!File.Exists(AuthPath)) return null;

        using var fs = File.OpenRead(AuthPath);
        using var doc = JsonDocument.Parse(fs);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        JsonElement? chosen = null;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("key", out var key) || string.IsNullOrWhiteSpace(key.GetString()))
                continue;
            chosen = prop.Value;
            if (prop.Name.StartsWith("https://auth.x.ai", StringComparison.OrdinalIgnoreCase))
                break;
        }

        if (chosen is not { } entry) return null;

        var first = entry.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
        var last = entry.TryGetProperty("last_name", out var ln) ? ln.GetString() : null;
        var display = string.Join(" ", new[] { last, first }.Where(s => !string.IsNullOrWhiteSpace(s)));

        DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(1);
        if (entry.TryGetProperty("expires_at", out var expEl) && expEl.GetString() is { } expStr &&
            DateTimeOffset.TryParse(expStr, out var parsed))
        {
            expires = parsed;
        }

        return new GrokAuth
        {
            AccessToken = entry.GetProperty("key").GetString() ?? "",
            RefreshToken = entry.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            ExpiresAt = expires,
            Email = entry.TryGetProperty("email", out var em) ? em.GetString() ?? "" : "",
            UserId = entry.TryGetProperty("user_id", out var uid) ? uid.GetString() ?? "" : "",
            ClientId = entry.TryGetProperty("oidc_client_id", out var cid)
                ? cid.GetString() ?? "b1a00492-073a-47ea-816f-4c329264a828"
                : "b1a00492-073a-47ea-816f-4c329264a828",
            Issuer = entry.TryGetProperty("oidc_issuer", out var iss)
                ? iss.GetString() ?? "https://auth.x.ai"
                : "https://auth.x.ai",
            DisplayName = display
        };
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GrokUsageMonitor/1.0");
        return client;
    }
}
