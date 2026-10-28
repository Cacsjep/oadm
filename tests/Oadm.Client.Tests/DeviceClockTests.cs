using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>
/// The Clock column of the device grid (text, sort, search) and the Settings > Polling "Allowed clock difference (s)".
/// Set OADM_SCREENSHOT_DIR for client-clock-column-*.png and client-settings-polling-*.png (dark, or light with
/// OADM_TEST_APPEARANCE=Light).
/// </summary>
public sealed class DeviceClockTests
{
    [Theory]
    [InlineData(999, "OK")] // the same text as the Clock compliance check
    [InlineData(-999, "OK")]
    [InlineData(1000, "1 s ahead")]
    [InlineData(2100, "3 s ahead")] // seconds round up: off never reads as within the allowed difference
    [InlineData(-49_400, "50 s behind")]
    [InlineData(59_001, "1 min ahead")]
    [InlineData(-125_000, "2 min behind")]
    [InlineData(3_599_000, "59 min ahead")]
    [InlineData(3 * 3600 * 1000 + 10_000, "3 h ahead")]
    [InlineData(-86_400_000, "1 day behind")]
    [InlineData(int.MaxValue, "24 days ahead")]
    [InlineData(int.MinValue, "24 days behind")]
    public void Offset_text_uses_whole_units(int offsetMs, string expected) =>
        Assert.Equal(expected, Oadm.Sdk.Devices.ClockTexts.OffsetText(offsetMs));

    [Fact]
    public void Cell_shows_the_offset_beyond_the_tolerance_then_ntp_then_ok_and_a_chip_only_while_off()
    {
        Assert.Equal(ChipInfo.Empty, DeviceClockText.ToChip(null, NtpState.NotSynced, clockOff: false, beyondTolerance: false));

        ChipInfo ok = DeviceClockText.ToChip(1500, NtpState.Synced, clockOff: false, beyondTolerance: false);
        Assert.Equal("OK", ok.Text);
        Assert.True(ok.IsOk);
        Assert.False(ok.IsChip);

        ChipInfo ahead = DeviceClockText.ToChip(49_400, NtpState.Synced, clockOff: true, beyondTolerance: true);
        Assert.Equal("50 s ahead", ahead.Text);
        Assert.True(ahead.IsWarning);
        Assert.True(ahead.IsChip);

        // Beyond the tolerance wins over NTP; within it, NTP not synced says so.
        Assert.Equal("2 min behind", DeviceClockText.ToChip(-125_000, NtpState.NotSynced, clockOff: true, beyondTolerance: true).Text);
        ChipInfo ntp = DeviceClockText.ToChip(400, NtpState.NotSynced, clockOff: true, beyondTolerance: false);
        Assert.Equal("NTP not synced", ntp.Text);
        Assert.True(ntp.IsWarning);

        // A device that is not Ok is never off: its last measured offset as plain text.
        ChipInfo lastMeasured = DeviceClockText.ToChip(3 * 3600 * 1000 + 10_000, NtpState.Off, clockOff: false, beyondTolerance: true);
        Assert.Equal("3 h ahead", lastMeasured.Text);
        Assert.False(lastMeasured.IsChip);
        Assert.Equal("NTP not synced", DeviceClockText.ToChip(400, NtpState.NotSynced, clockOff: false, beyondTolerance: false).Text);
    }

    [Fact]
    public void Tooltip_gives_the_offset_and_the_ntp_state()
    {
        Assert.Null(DeviceClockText.Tooltip(null, NtpState.Synced));
        Assert.Equal("Within 1 s of the server, NTP synced", DeviceClockText.Tooltip(300, NtpState.Synced));
        Assert.Equal("50 s ahead, NTP not synced", DeviceClockText.Tooltip(49_400, NtpState.NotSynced));
        Assert.Equal("2 min behind, NTP off", DeviceClockText.Tooltip(-125_000, NtpState.Off));
        Assert.Equal("3 s ahead", DeviceClockText.Tooltip(2500, NtpState.Unknown));
    }

    [Fact]
    public void Rows_sort_by_the_absolute_offset_with_unmeasured_last()
    {
        DeviceRowViewModel[] rows =
        [
            Row("1", null), Row("2", -125_000), Row("3", 300), Row("4", 49_400), Row("5", -800),
        ];

        string[] order = [.. rows.OrderBy(r => r.ClockSortKey).Select(r => r.Id)];

        Assert.Equal(["3", "5", "4", "2", "1"], order);
    }

    [Fact]
    public void Row_follows_device_updates_and_the_search_finds_the_clock_text()
    {
        Device device = TestSupport.Device("1", "ACCC8E5F6071", "10.0.0.48", "AXIS P3265-V");
        var row = new DeviceRowViewModel(device);
        Assert.Equal("", row.Clock.Text);
        Assert.Equal(long.MaxValue, row.ClockSortKey);

        device.ClockOffsetMs = -125_000;
        device.NtpState = NtpState.Off;
        device.ClockBeyondTolerance = true;
        device.ClockOff = true;
        Assert.True(row.Apply(device));
        Assert.True(row.Clock.IsWarning);
        Assert.Equal("2 min behind", row.Clock.Text);
        Assert.Equal("2 min behind, NTP off", row.ClockTooltip);
        Assert.Equal(125_000, row.ClockSortKey);
        Assert.True(row.Matches("behind"));
        Assert.False(row.Matches("ahead"));
        Assert.False(row.Apply(device)); // nothing changed: no events

        device.ClockOff = false; // the device is no longer Ok: the last measured offset, no chip
        Assert.True(row.Apply(device));
        Assert.Equal("2 min behind", row.Clock.Text);
        Assert.False(row.Clock.IsChip);

        device.ClockBeyondTolerance = false;
        Assert.True(row.Apply(device));
        Assert.Equal("OK", row.Clock.Text);
    }

    [Fact]
    public void Fake_devices_show_every_kind_of_clock_value()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        List<string> texts = [];
        foreach (string serial in Enumerable.Range(0, 20).Select(i => "ACCC8E" + i.ToString("X6", System.Globalization.CultureInfo.InvariantCulture)))
        {
            Device device = FakeOadmApi.WithSampleClock(TestSupport.Device(serial, serial, "10.0.0.1", "AXIS P3265-V"), 2);
            texts.Add(new DeviceRowViewModel(device).Clock.Text);
        }

        Assert.Contains("", texts);
        Assert.Contains("OK", texts);
        Assert.Contains("NTP not synced", texts);
        Assert.Contains(texts, t => t.EndsWith(" ahead", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.EndsWith(" behind", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(3600, null)]
    [InlineData(0, "Enter a whole number from 1 to 3600.")]
    [InlineData(3601, "Enter a whole number from 1 to 3600.")]
    [InlineData(null, "Enter a whole number from 1 to 3600.")]
    public void Allowed_clock_difference_is_validated_like_the_other_numbers(int? value, string? expected) =>
        Assert.Equal(expected, SettingsViewModel.RangeError(value, SettingsViewModel.MinClockTolerance, SettingsViewModel.MaxClockTolerance));

    [Fact]
    public async Task Allowed_clock_difference_is_read_validated_and_saved()
    {
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var vm = new SettingsViewModel(f.Api, connection, NullLogger<SettingsViewModel>.Instance);
            await vm.LoadAsync();
            Assert.Equal(2, vm.ClockToleranceSeconds); // the server's default

            vm.ClockToleranceSeconds = 0;
            Assert.Equal("Enter a whole number from 1 to 3600.", vm.ErrorOf(nameof(vm.ClockToleranceSeconds)));
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.Equal("Enter a whole number from 1 to 3600.", vm.SaveBlockedReason);

            vm.ClockToleranceSeconds = 30;
            Assert.True(vm.SaveCommand.CanExecute(null));
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(30, (await api.GetSettingsAsync(CancellationToken.None)).ClockToleranceSeconds);
            Assert.Equal(30, vm.ClockToleranceSeconds);
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Clock_column_renders_in_the_device_grid_and_sorts_by_the_offset()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
            using var fixture = new DevicesFixture(api);
            fixture.SeedDevices([.. await api.ListDevicesAsync(CancellationToken.None)]);
            DevicesViewModel vm = fixture.Devices;
            var window = new Window { Width = 1700, Height = 800, Content = new DevicesView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "DeviceGrid");
            DataGridColumn clock = grid.Columns.Single(c => c.Header as string == "Clock");
            Assert.True(clock.IsVisible);
            Assert.False(grid.Columns.Single(c => c.Header as string == "DHCP").IsVisible); // off by default

            // Ok plain text, an off clock as a warning chip.
            Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "OK" && t.IsEffectivelyVisible);
            Assert.Contains(grid.GetVisualDescendants().OfType<StatusChip>(), c => c.IsWarning && c.IsEffectivelyVisible
                && (c.Text == "NTP not synced" || c.Text?.EndsWith(" ahead", StringComparison.Ordinal) == true || c.Text?.EndsWith(" behind", StringComparison.Ordinal) == true));
            Capture(window, outDir, "client-clock-column");

            clock.Sort(ListSortDirection.Ascending);
            Dispatcher.UIThread.RunJobs();
            List<long> keys = grid.GetVisualDescendants().OfType<DataGridRow>()
                .OrderBy(r => r.Bounds.Top).Select(r => ((IDeviceGridItem)r.DataContext!).Row.ClockSortKey).ToList();
            Assert.NotEmpty(keys);
            Assert.Equal(keys.Order().ToList(), keys);
            Capture(window, outDir, "client-clock-column-sorted");
            clock.ClearSort();
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Settings_polling_card_shows_the_allowed_clock_difference()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var vm = new SettingsViewModel(f.Api, connection, NullLogger<SettingsViewModel>.Instance);
            await vm.LoadAsync();
            var window = new Window { Width = 900, Height = 700, Content = new SettingsView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            CollapsibleCard polling = window.GetVisualDescendants().OfType<CollapsibleCard>().Single(c => c.Title == "Polling");
            polling.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            FormField field = window.GetVisualDescendants().OfType<FormField>().Single(x => x.Label == "Allowed clock difference (s)" && x.IsEffectivelyVisible);
            Assert.Equal(2m, field.GetVisualDescendants().OfType<NumericUpDown>().Single().Value);
            Capture(window, outDir, "client-settings-polling");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static DeviceRowViewModel Row(string id, int? offsetMs)
    {
        Device device = TestSupport.Device(id, "ACCC8E00000" + id, "10.0.0." + id, "AXIS P3265-V");
        if (offsetMs is { } offset)
        {
            device.ClockOffsetMs = offset;
        }

        return new DeviceRowViewModel(device);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            string appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase)
                ? "light" : "dark";
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, $"{name}-{appearance}.png"), PngBitmapEncoderOptions.Default);
        }
    }
}
