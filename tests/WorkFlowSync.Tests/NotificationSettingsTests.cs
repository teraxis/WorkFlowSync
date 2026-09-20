using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Tests;

/// <summary>
/// The notification settings as the window sees them: they must survive a round trip through config.json,
/// and the hint under the list must describe what was actually chosen (docs/product/features/tray.md).
/// </summary>
public class NotificationSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-notif-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public NotificationSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.json");
        // A paused pair: constructing the view-model with an active one starts the background checker.
        ConfigFile.Save(new SyncConfig
        {
            Pairs = { new FolderPair { Name = "p", Enabled = false, Source = Path.Combine(_dir, "a"), Target = Path.Combine(_dir, "b") } },
            Notifications = NotifyMode.ErrorsOnly,
            QuietHoursFrom = 22,
            QuietHoursTo = 8,
        }, _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_settings_are_read_from_the_file_and_written_back_unchanged()
    {
        var vm = new MainViewModel(_configPath);

        Assert.Equal(NotifyMode.ErrorsOnly, vm.Notifications);
        Assert.Equal(22, vm.QuietHoursFrom);
        Assert.Equal(8, vm.QuietHoursTo);

        var back = vm.ToConfig();
        Assert.Equal(NotifyMode.ErrorsOnly, back.Notifications);
        Assert.Equal(22, back.QuietHoursFrom);
        Assert.Equal(8, back.QuietHoursTo);
    }

    [Fact]
    public void The_hint_says_what_was_chosen_including_the_quiet_hours()
    {
        var vm = new MainViewModel(_configPath) { Notifications = NotifyMode.All };

        Assert.Contains("Одне повідомлення на перевірку", vm.NotificationsHint, StringComparison.Ordinal);
        Assert.Contains("22:00", vm.NotificationsHint, StringComparison.Ordinal);
        Assert.Contains("08:00", vm.NotificationsHint, StringComparison.Ordinal);

        vm.QuietHoursTo = 22;                                  // equal bounds = no quiet period
        Assert.DoesNotContain("притримуються", vm.NotificationsHint, StringComparison.Ordinal);

        vm.Notifications = NotifyMode.Off;
        Assert.Contains("нічого не показуватиме", vm.NotificationsHint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_without_the_settings_reads_as_talking_and_no_quiet_hours()
    {
        var plain = Path.Combine(_dir, "plain.json");
        File.WriteAllText(plain, """{ "pairs": [ { "name": "p", "enabled": false, "source": "S:\\a", "target": "D:\\b" } ] }""");

        var vm = new MainViewModel(plain);

        Assert.Equal(NotifyMode.All, vm.Notifications);
        Assert.Equal(vm.QuietHoursFrom, vm.QuietHoursTo);
    }
}
