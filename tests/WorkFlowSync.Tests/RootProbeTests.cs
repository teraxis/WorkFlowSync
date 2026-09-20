using System.Net;
using WorkFlowSync.Core;

namespace WorkFlowSync.Tests;

/// <summary>
/// Root classification (docs/plan-etap5.md §4.1). Only the pure parts are covered here; the parts that
/// touch DriveInfo, DNS and the network are exercised by `wfs probe` against a real root, because a LAN
/// or internet share cannot be reproduced in CI (docs/testing.md).
/// </summary>
public class RootProbeTests
{
    [Theory]
    [InlineData(@"\\server\share\folder", true, "server", "share", null)]
    [InlineData(@"\\storage.example.com\share", true, "storage.example.com", "share", null)]
    [InlineData(@"\\?\UNC\server\share\deep", true, "server", "share", null)]
    [InlineData(@"X:\data\folder", false, null, null, 'X')]
    [InlineData(@"c:\", false, null, null, 'C')]
    [InlineData(@"\\?\C:\long\path", false, null, null, 'C')]
    [InlineData(@"relative\path", false, null, null, null)]
    [InlineData("", false, null, null, null)]
    public void Shape_splits_unc_and_drive_paths(string path, bool isUnc, string? host, string? share, char? letter)
    {
        var shape = RootProbe.Shape(path);

        Assert.Equal(isUnc, shape.IsUnc);
        Assert.Equal(host, shape.Host);
        Assert.Equal(share, shape.Share);
        Assert.Equal(letter, shape.DriveLetter);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]     // just outside the private range
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]     // link-local
    [InlineData("100.64.0.1", true)]      // CGNAT, RFC 6598
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]         // unique local
    [InlineData("2001:4860:4860::8888", false)]
    public void Private_addresses_are_recognised(string address, bool expected) =>
        Assert.Equal(expected, RootProbe.IsPrivateAddress(IPAddress.Parse(address)));

    [Fact]
    public void Fixed_drive_is_local()
    {
        Assert.Equal(RootKind.Local, RootProbe.Classify(DriveType.Fixed, isUnc: false, hostIsPrivate: null, firstEntryMs: 0.1));
    }

    [Fact]
    public void Removable_media_is_its_own_kind()
    {
        Assert.Equal(RootKind.Removable, RootProbe.Classify(DriveType.Removable, isUnc: false, hostIsPrivate: null, firstEntryMs: 0.2));
        Assert.Equal(RootKind.Removable, RootProbe.Classify(DriveType.CDRom, isUnc: false, hostIsPrivate: null, firstEntryMs: 0.2));
    }

    [Fact]
    public void Private_host_makes_a_share_lan_even_when_it_is_slow()
    {
        // A LAN share behind a slow VPN must not be mistaken for the internet: the address is the stronger signal.
        Assert.Equal(RootKind.LanShare, RootProbe.Classify(DriveType.Network, isUnc: true, hostIsPrivate: true, firstEntryMs: 80));
    }

    [Fact]
    public void Public_host_makes_a_share_remote_even_when_it_is_fast()
    {
        Assert.Equal(RootKind.RemoteShare, RootProbe.Classify(DriveType.Network, isUnc: true, hostIsPrivate: false, firstEntryMs: 1));
    }

    [Theory]
    [InlineData(1.0, RootKind.LanShare)]
    [InlineData(48.0, RootKind.RemoteShare)]   // the measured Hetzner Storage Box round trip
    [InlineData(null, RootKind.RemoteShare)]   // nothing known at all: assume the worse
    public void Without_an_address_latency_decides(double? firstEntryMs, RootKind expected) =>
        Assert.Equal(expected, RootProbe.Classify(DriveType.Network, isUnc: true, hostIsPrivate: null, firstEntryMs));

    [Fact]
    public void Unc_path_on_an_unknown_drive_type_is_still_a_share()
    {
        // A UNC path has no drive letter, so DriveType stays Unknown — the shape must decide.
        Assert.Equal(RootKind.RemoteShare, RootProbe.Classify(DriveType.Unknown, isUnc: true, hostIsPrivate: false, firstEntryMs: null));
    }

    [Fact]
    public void Probe_reports_a_missing_root_without_throwing()
    {
        var info = RootProbe.Probe(Path.Combine(Path.GetTempPath(), "wfs-no-such-root-" + Guid.NewGuid().ToString("N")));

        Assert.False(info.Available);
        Assert.NotNull(info.Problem);
        Assert.Equal(RootKind.Local, info.Kind);
    }

    [Fact]
    public void Probe_measures_a_real_local_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wfs-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "b");

            var info = RootProbe.Probe(dir);

            Assert.True(info.Available);
            Assert.Equal(RootKind.Local, info.Kind);
            Assert.Equal(2, info.RootEntries);
            Assert.NotNull(info.FirstEntry);
            Assert.NotNull(info.RootListing);
            Assert.Null(info.Problem);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Notify_probe_sees_a_change_in_a_local_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wfs-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The probe itself never writes into the root, so the change has to come from outside it — here, the test.
            using var writer = new Timer(_ => File.WriteAllText(Path.Combine(dir, "new.txt"), "hello"), null, 300, Timeout.Infinite);

            var result = RootProbe.WatchNotify(dir, TimeSpan.FromSeconds(3));

            Assert.True(result.Started);
            Assert.Null(result.StartProblem);
            Assert.True(result.Events > 0, "a local watcher must see a file created while it listens");
            Assert.False(result.Inconclusive);
            Assert.NotNull(result.FirstEvent);
            Assert.NotEmpty(result.Samples);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Notify_probe_reports_a_missing_root_instead_of_throwing()
    {
        var result = RootProbe.WatchNotify(Path.Combine(Path.GetTempPath(), "wfs-gone-" + Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(1));

        Assert.False(result.Started);
        Assert.NotNull(result.StartProblem);
    }
}
