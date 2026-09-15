using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WorkFlowSync.App.Services;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

[Trait("Category", "E2E")]
public sealed class TrayModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfs-tray-" + Guid.NewGuid().ToString("N"));

    public TrayModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Background_loop_starts_pauses_and_resumes()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        var configPath = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = src, Target = Path.Combine(_root, "dst") } },
            Interval = TimeSpan.FromMinutes(1),
        }, configPath);

        using var loop = new BackgroundLoop(configPath, () => Path.Combine(_root, "logs"));
        loop.Start();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (loop.LastPassUtc is null && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.NotNull(loop.LastPassUtc);
        Assert.Equal("a", File.ReadAllText(Path.Combine(_root, "dst", "a.txt")));
        Assert.True(loop.IsActive);
        Assert.Contains("скопійовано/оновлено: 1", loop.LastResult);
        Assert.Contains("останній прохід", loop.StatusText);

        loop.Pause();
        Assert.Equal(LoopState.Paused, loop.State);
        Assert.Contains("На паузі", loop.StatusText);

        loop.Resume();
        Assert.True(loop.IsActive);
        loop.Stop();
        Assert.Equal(LoopState.Stopped, loop.State);
    }

    [Fact]
    public void Startup_shortcut_mode_is_written_and_read_back()
    {
        var startup = Path.Combine(_root, "Startup");
        var gui = Path.Combine(_root, "WorkFlowSync.exe");
        var console = Path.Combine(_root, "wfs.exe");
        File.WriteAllText(gui, "stub");
        File.WriteAllText(console, "stub");
        var configPath = Path.Combine(_root, "config.json");

        Autostart.EnableStartupShortcut(configPath, startup, gui, Autostart.Mode.Tray);
        Assert.Equal(Autostart.Mode.Tray, Autostart.ReadStartupShortcutMode(startup));

        Autostart.EnableStartupShortcut(configPath, startup, console, Autostart.Mode.ConsoleLoop);
        Assert.Equal(Autostart.Mode.ConsoleLoop, Autostart.ReadStartupShortcutMode(startup));

        Autostart.DisableStartupShortcut(startup);
        Assert.Null(Autostart.ReadStartupShortcutMode(startup));
    }

    [Fact]
    public void Application_icon_is_a_valid_multi_size_ico()
    {
        // AssetLoader needs a running Avalonia app, so verify the source asset itself.
        var ico = Path.Combine(RepoRoot(), "src", "WorkFlowSync.App", "Assets", "app.ico");
        Assert.True(File.Exists(ico), ico);
        var bytes = File.ReadAllBytes(ico);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);              // ICONDIR: reserved=0, type=1 (icon)
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(count >= 3, $"expected several sizes, got {count}");
        // Every entry must point at a PNG inside the file.
        for (var i = 0; i < count; i++)
        {
            var e = 6 + 16 * i;
            var size = BitConverter.ToInt32(bytes, e + 8);
            var offset = BitConverter.ToInt32(bytes, e + 12);
            Assert.InRange(offset + size, 0, bytes.Length);
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes[offset..(offset + 4)]);
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    /// <summary>
    /// Autostart must be silent: `WorkFlowSync.exe --tray` may not put any window on screen
    /// (the desktop lifetime shows whatever is assigned to MainWindow, so tray mode leaves it unset).
    /// Skipped when the GUI executable has not been built next to the repository.
    /// </summary>
    [Fact]
    public void Tray_mode_starts_without_showing_a_window()
    {
        var exe = FindGuiExe();
        if (exe is null) return;

        var configPath = Path.Combine(_root, "config.json");
        ConfigFile.Save(new SyncConfig
        {
            Pairs = { new FolderPair { Name = "t", Source = Path.Combine(_root, "src"), Target = Path.Combine(_root, "dst") } },
            Interval = TimeSpan.FromMinutes(30),
            LogPath = Path.Combine(_root, "logs"),
            StatePath = Path.Combine(_root, "state.db"),
        }, configPath);

        using var p = Process.Start(new ProcessStartInfo(exe, $"--tray --config \"{configPath}\"") { UseShellExecute = false })!;
        try
        {
            // Give the app long enough to create and (wrongly) show its window.
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && !p.HasExited)
            {
                Assert.Empty(VisibleWindowTitles(p.Id));
                Thread.Sleep(250);
            }
            Assert.False(p.HasExited, "tray mode exited instead of staying resident");
        }
        finally
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { /* already gone */ }
        }
    }

    private static string? FindGuiExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "WorkFlowSync.App", "bin");
            if (!Directory.Exists(candidate)) continue;
            return Directory.EnumerateFiles(candidate, "WorkFlowSync.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    private static List<string> VisibleWindowTitles(int pid)
    {
        var titles = new List<string>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid && IsWindowVisible(h))
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                titles.Add(sb.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
}
