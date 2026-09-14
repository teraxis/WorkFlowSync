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
}
