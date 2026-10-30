using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace GrokUsageMonitor;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;

    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase)))
        {
            if (!AttachConsole(-1)) AllocConsole();
            RunOnce();
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, @"Local\GrokUsageMonitor.SingleInstance", out var created);
        if (!created)
        {
            System.Windows.MessageBox.Show("Grok 用量监控已经在运行。", "Grok 用量监控",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new MainWindow();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void RunOnce()
    {
        try
        {
            var snapshot = Task.Run(() => BillingClient.FetchAsync(CancellationToken.None)).GetAwaiter().GetResult();
            var text = snapshot.ToDisplayText();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(text);
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(
                Path.Combine(AppSettings.DataDir, "last-once.txt"),
                text,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }
}
