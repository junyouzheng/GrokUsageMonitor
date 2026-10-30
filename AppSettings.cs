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

    public static void Append(UsageSnapshot snap)
    {
        if (snap.Error is not null) return;
        Directory.CreateDirectory(AppSettings.DataDir);
        var line = JsonSerializer.Serialize(new HistoryPoint { At = snap.FetchedAt, UsedPercent = snap.UsedPercent });
        File.AppendAllText(PathFile, line + Environment.NewLine);
    }

    public static IReadOnlyList<double> LoadPercents(TimeSpan window)
    {
        if (!File.Exists(PathFile)) return [];
        var cutoff = DateTimeOffset.Now - window;
        var points = new List<double>();
        foreach (var line in File.ReadLines(PathFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var p = JsonSerializer.Deserialize<HistoryPoint>(line);
                if (p is null || p.At < cutoff) continue;
                points.Add(p.UsedPercent);
            }
            catch
            {
                // skip bad line
            }
        }

        return points;
    }
}
