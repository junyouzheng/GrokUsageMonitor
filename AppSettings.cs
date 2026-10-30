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

    public static IReadOnlyList<double> LoadPercents(TimeSpan window)
    {
        EnsureLoaded();
        var cutoff = DateTimeOffset.Now - window;
        if (Points.Count > 0 && Points[0].At < cutoff)
        {
            var removed = Points.RemoveAll(p => p.At < cutoff);
            if (removed >= 60) Rewrite();
        }

        var values = new double[Points.Count];
        for (var i = 0; i < Points.Count; i++) values[i] = Points[i].UsedPercent;
        return values;
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
