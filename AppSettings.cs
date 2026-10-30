using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace GrokUsageMonitor;

internal static class AppSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GrokUsageMonitor");
    private static readonly string PathFile = Path.Combine(Dir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "GrokUsageMonitor";

    public static string DataDir => Dir;

    public static MonitorSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(PathFile)) ?? new();
        }
        catch
        {
            // keep defaults
        }

        return new MonitorSettings();
    }

    public static void Save(MonitorSettings settings)
    {
        Directory.CreateDirectory(Dir);
        var tmp = PathFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        File.Copy(tmp, PathFile, overwrite: true);
        File.Delete(tmp);
        ApplyStartup(settings.StartWithWindows);
    }

    public static void ApplyStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            key.SetValue(RunName, $"\"{exe}\"");
        }
        else if (key.GetValue(RunName) is not null)
        {
            key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }
}

internal static class HistoryStore
{
    private static readonly string PathFile = Path.Combine(AppSettings.DataDir, "history.jsonl");
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private static readonly List<HistoryPoint> Points = [];
    private static bool _loaded;

    public static void Append(UsageSnapshot snap)
    {
        if (snap.Error is not null) return;
        EnsureLoaded();
        Points.Add(new HistoryPoint { At = snap.FetchedAt, UsedPercent = snap.UsedPercent });
        Directory.CreateDirectory(AppSettings.DataDir);
        File.AppendAllText(PathFile, JsonSerializer.Serialize(Points[^1]) + Environment.NewLine);
    }

    public static IReadOnlyList<HistoryPoint> LoadPoints(TimeSpan window)
    {
        EnsureLoaded();
        var cutoff = DateTimeOffset.Now - window;
        if (Points.Count > 0 && Points[0].At < cutoff)
        {
            var removed = Points.RemoveAll(p => p.At < cutoff);
            if (removed >= 60) Rewrite();
        }

        var list = new List<HistoryPoint>(Points.Count);
        foreach (var point in Points)
        {
            if (point.At >= cutoff) list.Add(point);
        }

        return list;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(PathFile)) return;

        var cutoff = DateTimeOffset.Now - Keep;
        var dropped = false;
        foreach (var line in File.ReadLines(PathFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var point = JsonSerializer.Deserialize<HistoryPoint>(line);
                if (point is null) continue;
                if (point.At < cutoff)
                {
                    dropped = true;
                    continue;
                }

                Points.Add(point);
            }
            catch
            {
                dropped = true;
            }
        }

        if (dropped) Rewrite();
    }

    private static void Rewrite()
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        var tmp = PathFile + ".tmp";
        File.WriteAllLines(tmp, Points.Select(p => JsonSerializer.Serialize(p)));
        File.Copy(tmp, PathFile, overwrite: true);
        File.Delete(tmp);
    }
}

internal static class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly string PathFile = Path.Combine(AppSettings.DataDir, "last-snapshot.json");

    public static void Save(UsageSnapshot snap)
    {
        if (snap.Error is not null) return;
        Directory.CreateDirectory(AppSettings.DataDir);
        var dto = new StoredSnapshot
        {
            FetchedAt = snap.FetchedAt,
            Email = snap.Email,
            DisplayName = snap.DisplayName,
            Tier = snap.Tier,
            UsedPercent = snap.UsedPercent,
            PeriodStart = snap.PeriodStart,
            PeriodEnd = snap.PeriodEnd,
            PeriodType = snap.PeriodType,
            UnifiedPool = snap.UnifiedPool,
            PrepaidUsd = snap.PrepaidUsd,
            Products = snap.Products.Select(p => new ProductShare
            {
                Id = p.Id,
                Label = p.Label,
                Percent = p.Percent
            }).ToList()
        };
        var tmp = PathFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOpts));
        File.Copy(tmp, PathFile, overwrite: true);
        File.Delete(tmp);
    }

    public static UsageSnapshot? Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return null;
            var dto = JsonSerializer.Deserialize<StoredSnapshot>(File.ReadAllText(PathFile), JsonOpts);
            if (dto is null) return null;
            return new UsageSnapshot
            {
                FetchedAt = dto.FetchedAt,
                Email = dto.Email ?? "",
                DisplayName = dto.DisplayName ?? "",
                Tier = dto.Tier ?? "",
                UsedPercent = dto.UsedPercent,
                PeriodStart = dto.PeriodStart,
                PeriodEnd = dto.PeriodEnd,
                PeriodType = string.IsNullOrWhiteSpace(dto.PeriodType) ? "每周" : dto.PeriodType,
                UnifiedPool = dto.UnifiedPool,
                PrepaidUsd = dto.PrepaidUsd,
                Products = dto.Products ?? []
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed class StoredSnapshot
    {
        public DateTimeOffset FetchedAt { get; set; }
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
        public string? Tier { get; set; }
        public double UsedPercent { get; set; }
        public DateTimeOffset? PeriodStart { get; set; }
        public DateTimeOffset? PeriodEnd { get; set; }
        public string? PeriodType { get; set; }
        public bool UnifiedPool { get; set; }
        public decimal PrepaidUsd { get; set; }
        public List<ProductShare>? Products { get; set; }
    }
}
