using Avalonia;

namespace WorkFlowSync.App;

internal static class Program
{
    // Avalonia must be initialised before any UI / SynchronizationContext-dependent code.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Best-effort crash dump next to the executable; a broken logger must never mask the crash.</summary>
    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
                $"[{source}] {DateTime.Now:O}{Environment.NewLine}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
