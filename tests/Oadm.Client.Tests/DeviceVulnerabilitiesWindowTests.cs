using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Devices;
using Oadm.Client.Vulnerabilities;
using Oadm.Contracts.Vulnerabilities;

namespace Oadm.Client.Tests;

/// <summary>
/// The context menu entry "Vulnerabilities" and its window (docs/spec/vulnerabilities.md): only when a selected device has
/// known CVEs; each CVE once with level, score, fixed versions, the full description and the advisory link; with several
/// devices how many it affects and which. OADM_SCREENSHOT_DIR writes client-vulnerabilities-window.png and
/// client-vulnerabilities-window-several.png.
/// </summary>
public sealed class DeviceVulnerabilitiesWindowTests
{
    private static Vulnerability Cve(string id, string severity, double? score, string description, string? advisory, params VersionRange[] ranges) =>
        new(id, null, score, severity, description, advisory, [new AffectedFirmware(null, ranges)]);

    /// <summary>Devices "1" to "n" (P3265-V, 10.0.0.n) with the given firmware and two CVEs of two tracks.</summary>
    private static DevicesFixture Fixture(params string[] firmware)
    {
        var f = new DevicesFixture();
        f.Store.Vulnerabilities.Replace(new VulnerabilityList(DateTimeOffset.UnixEpoch, "release",
        [
            Cve("CVE-2024-0001", "Medium", 5.3, "A medium flaw.", "https://www.axis.com/dam/public/cve-2024-0001.pdf", new VersionRange("11.0", "11.11.93")),
            Cve("CVE-2024-0002", "Critical", 9.8, "A critical flaw.", "http://example.com/plain-http",
                new VersionRange("11.0", "11.11.50"), new VersionRange("10.0", "10.12.200")),
        ]));
        f.SeedDevices([.. firmware.Select((fw, i) =>
        {
            var device = TestSupport.Device($"{i + 1}", $"ACCC8E0000{i + 1:D2}", $"10.0.0.{i + 1}", "P3265-V");
            device.FirmwareVersion = fw;
            return device;
        })]);
        return f;
    }

    [Fact]
    public void The_entry_is_there_when_a_selected_device_has_known_cves()
    {
        using var f = Fixture("11.8.61", "12.11.147", "12.11.147");
        string[] Headers() => [.. f.Devices.ContextMenuEntries.Select(e => e.Header)];

        f.Select("1");
        var entry = f.Devices.ContextMenuEntries.Single(e => e.Header == DevicesViewModel.VulnerabilitiesMenuHeader);
        Assert.Equal("shieldAlert", entry.IconKey);
        Assert.Same(f.Devices.ShowVulnerabilitiesCommand, entry.Command);

        f.Select("2"); // current firmware: no CVEs
        Assert.DoesNotContain(DevicesViewModel.VulnerabilitiesMenuHeader, Headers());

        f.Select("2", "3"); // several, none with CVEs
        Assert.DoesNotContain(DevicesViewModel.VulnerabilitiesMenuHeader, Headers());

        f.Select("1", "2", "3"); // several, one with CVEs
        Assert.Contains(DevicesViewModel.VulnerabilitiesMenuHeader, Headers());

        f.Store.Vulnerabilities.Replace(new VulnerabilityList(DateTimeOffset.UnixEpoch, "release", [])); // a new list without them
        Assert.DoesNotContain(DevicesViewModel.VulnerabilitiesMenuHeader, Headers());
    }

    [Fact]
    public async Task One_device_lists_its_cves_worst_first_with_their_details()
    {
        using var f = Fixture("11.8.61");
        DeviceVulnerabilitiesViewModel? shown = null;
        f.Dialogs.ShowDeviceVulnerabilitiesAsync(Arg.Do<DeviceVulnerabilitiesViewModel>(vm => shown = vm)).Returns(Task.CompletedTask);
        f.Select("1");

        await f.Devices.ShowVulnerabilitiesCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.False(shown.ShowDevices);
        Assert.Equal("P3265-V · 10.0.0.1 · 11.8.61", shown.DeviceText);
        Assert.Equal("2 CVEs", shown.CountText);
        Assert.Equal(["CVE-2024-0002", "CVE-2024-0001"], shown.Rows.Select(r => r.Id));
        var critical = shown.Rows[0];
        Assert.Equal("Critical", critical.Level);
        Assert.True(critical.IsSevere);
        Assert.Equal("9.8", critical.ScoreText);
        Assert.Equal("11.11.50", critical.FixedIn);
        Assert.Equal("A critical flaw.", critical.Description);
        Assert.Null(critical.DevicesTip);
        Assert.False(critical.HasAdvisory); // only https links open
        var medium = shown.Rows[1];
        Assert.True(medium.IsModerate);
        Assert.True(medium.HasAdvisory);

        await medium.OpenAdvisory!.ExecuteAsync(medium);
        await f.Launcher.Received(1).OpenAsync(new Uri("https://www.axis.com/dam/public/cve-2024-0001.pdf"));
    }

    [Fact]
    public async Task Several_devices_list_each_cve_once_with_the_devices_it_affects()
    {
        using var f = Fixture("11.8.61", "11.9.60", "10.12.100", "12.11.147");
        DeviceVulnerabilitiesViewModel? shown = null;
        f.Dialogs.ShowDeviceVulnerabilitiesAsync(Arg.Do<DeviceVulnerabilitiesViewModel>(vm => shown = vm)).Returns(Task.CompletedTask);
        f.Select("1", "2", "3", "4");

        await f.Devices.ShowVulnerabilitiesCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.True(shown.ShowDevices);
        Assert.Equal("3 of 4 selected devices", shown.DeviceText);
        Assert.Equal(["CVE-2024-0002", "CVE-2024-0001"], shown.Rows.Select(r => r.Id));
        var critical = shown.Rows[0];
        Assert.Equal(3, critical.DeviceCount);
        Assert.Equal("10.12.200, 11.11.50", critical.FixedIn); // each track's fix, lowest first
        Assert.Equal(
            ["P3265-V · 10.0.0.1 · 11.8.61", "P3265-V · 10.0.0.2 · 11.9.60", "P3265-V · 10.0.0.3 · 10.12.100"],
            critical.DevicesTip!.Split(Environment.NewLine).Order(StringComparer.Ordinal));
        Assert.Equal(2, shown.Rows[1].DeviceCount);
    }

    [Fact]
    public void The_devices_tooltip_names_the_first_devices_and_counts_the_rest()
    {
        using var f = Fixture([.. Enumerable.Repeat("11.8.61", DeviceVulnerabilitiesViewModel.MaxDeviceNames + 5)]);
        var vm = new DeviceVulnerabilitiesViewModel([.. f.Store.Devices]);

        var lines = vm.Rows[0].DevicesTip!.Split(Environment.NewLine);
        Assert.Equal(DeviceVulnerabilitiesViewModel.MaxDeviceNames + 1, lines.Length);
        Assert.Equal("and 5 more", lines[^1]);
    }

    [Fact]
    public async Task The_window_renders_one_device_and_several()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var f = new DevicesFixture();
            f.Store.Vulnerabilities.Replace(VulnerabilityHeadlessTests.ReleaseList());
            f.SeedDevices([.. VulnerabilityHeadlessTests.Samples()]);
            var rows = f.Store.Devices.ToList();

            var one = new DeviceVulnerabilitiesViewModel([rows.Single(d => d.FirmwareVersion == "6.50.5.18")]);
            var window = Show(one, outDir, "client-vulnerabilities-window.png");
            var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "VulnerabilityGrid");
            Assert.Equal(["CVE", "Level", "Score", "Fixed in", "Description", ""],
                grid.Columns.Where(c => c.IsVisible).Select(c => c.Header is TextBlock t ? t.Text : c.Header as string));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == one.Rows[0].Description);
            one.CloseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);

            var several = new DeviceVulnerabilitiesViewModel(rows);
            window = Show(several, outDir, "client-vulnerabilities-window-several.png");
            grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "VulnerabilityGrid");
            Assert.Contains("Devices", grid.Columns.Where(c => c.IsVisible).Select(c => c.Header is TextBlock t ? t.Text : c.Header as string));
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private static DeviceVulnerabilitiesWindow Show(DeviceVulnerabilitiesViewModel vm, string? outDir, string file)
    {
        var window = new DeviceVulnerabilitiesWindow();
        window.Attach(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        return window;
    }
}
