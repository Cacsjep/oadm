using System.Diagnostics;

using Oadm.Plugins.DateAndTime.Client;
using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DateAndTime.Tests;

public sealed class ViewModelTests
{
    private static readonly DateTimeOffset ClientNow = new(2026, 10, 7, 18, 30, 0, TimeSpan.FromHours(2));

    internal static CurrentTimeSettings Current10048 =>
        TimeParsers.ParseNtpInfo(Fixture.Read(Fixture.NtpInfo), TimeParsers.ParseDateTimeInfo(Fixture.Read(Fixture.DateTimeInfo), new CurrentTimeSettings { Source = "time-service 1.1, ntp 1.5" }))
            with { SupportsNts = true, ServerUtc = Fixture.DeviceNow.AddSeconds(-0.4), ServerTimeZone = "Europe/Vienna" };

    private static DateTimeDialogViewModel Create(int count = 1, IReadOnlyList<Sdk.Vapix.DeviceApi>? apis = null) =>
        new([.. Enumerable.Range(0, count).Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.{i / 250}.{i % 250 + 1}") { Apis = apis ?? Fixture.Modern })], () => ClientNow);

    private static string[] Errors(DateTimeDialogViewModel vm, string property) => [.. vm.GetErrors(property).Cast<string>()];

    private static TimeZoneEntry Zone(string id) => TimeZoneCatalog.Find(id)!;

    [Fact]
    public void Starts_like_adm_without_keep_and_with_ok_disabled_until_a_zone_is_known()
    {
        var vm = Create();

        Assert.True(vm.IsNtp); // no "Keep unchanged": NTP until the device says otherwise
        Assert.Null(vm.SelectedZone);
        Assert.False(vm.CanApply);
        Assert.False(vm.HasErrors); // nothing edited: no error below any field yet
        Assert.Equal("Select a time zone.", vm.ApplyBlockedReason); // the OK tooltip says why
        Assert.Null(vm.BuildPayload());
        Assert.Equal("Set date and time", vm.Title);
        Assert.True(vm.IsLoading);
        Assert.Equal("2026-10-07", vm.ManualDate);
        Assert.Equal(313, vm.Zones.Count); // one drop-down for every zone
    }

    [Fact]
    public void Device_time_shows_the_first_device()
    {
        var vm = Create();

        vm.ApplyCurrent(Current10048);

        Assert.False(vm.IsLoading);
        Assert.True(vm.HasDeviceTime);
        Assert.False(vm.HasDeviceTimeStatus);
        Assert.Equal("2026-10-07 18:24:01 (UTC+02:00)", vm.DeviceTimeText);
        Assert.True(vm.HasServerTime);
        Assert.EndsWith(" · device and server agree", vm.ServerTimeText, StringComparison.Ordinal);
        Assert.Equal("10.0.0.17", vm.NtpServersText);
        Assert.Equal("10.0.0.1 · P3265-V", vm.DeviceLabel);
        Assert.Equal("2026-10-07", vm.ManualDate);
        Assert.Equal("18:24:01", vm.ManualTime);

        // ADM defaults: the device's mode (NTP enabled) and, as its POSIX zone has no list entry, the server's zone.
        Assert.True(vm.IsNtp);
        Assert.Equal("Europe/Vienna", vm.SelectedZone!.Id);
        Assert.False(vm.TimeZoneUnchanged);
        Assert.False(vm.HasErrors);
        Assert.True(vm.CanApply);
        var payload = vm.BuildPayload()!;
        Assert.Equal("Europe/Vienna", payload.TimeZone);
        Assert.Equal(TimeMode.Ntp, payload.Mode);
        Assert.Equal(["10.0.0.17"], payload.Ntp!.Servers);
    }

    [Fact]
    public void Device_and_server_time_tick_like_a_clock()
    {
        var now = ClientNow;
        var vm = new DateTimeDialogViewModel([new FakeDevice(Guid.NewGuid())], () => now);
        vm.Tick(); // nothing read yet
        Assert.Equal(string.Empty, vm.DeviceTimeText);
        Assert.False(vm.HasServerTime);

        vm.ApplyCurrent(Current10048);
        var server = vm.ServerTimeText;
        now = now.AddSeconds(5);
        vm.Tick();

        Assert.Equal("2026-10-07 18:24:06 (UTC+02:00)", vm.DeviceTimeText);
        Assert.NotEqual(server, vm.ServerTimeText);
        Assert.EndsWith(" · device and server agree", vm.ServerTimeText, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_with_ntp_off_defaults_to_set_manually()
    {
        var vm = Create(3);

        vm.ApplyCurrent(Current10048 with { NtpEnabled = false, TimeZone = "Asia/Tokyo" });

        Assert.True(vm.IsManual);
        Assert.Equal("Asia/Tokyo", vm.SelectedZone!.Id); // the first device's zone, also for several devices
        Assert.False(vm.TimeZoneUnchanged); // several devices: the name stays "Change date and time"
        Assert.Equal(TimeMode.Manual, vm.BuildPayload()!.Mode);
    }

    [Fact]
    public void Ok_shows_every_error_below_its_field()
    {
        var vm = Create();
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;

        vm.ApplyCommand.Execute(null);

        Assert.Null(closed);
        Assert.Equal(["Select a time zone."], Errors(vm, nameof(vm.SelectedZone))); // below the time zone drop-down
        Assert.Equal(["Enter at least one NTP server."], Errors(vm, nameof(vm.NtpServersText)));
        vm.SelectedZone = Zone("Europe/Vienna");
        Assert.Empty(Errors(vm, nameof(vm.SelectedZone)));
    }

    [Fact]
    public void Single_device_with_an_iana_zone_preselects_it_without_changing()
    {
        var vm = Create();

        vm.ApplyCurrent(Current10048 with { TimeZone = "Europe/Vienna" });

        Assert.Equal("Europe/Vienna", vm.SelectedZone!.Id);
        Assert.True(vm.TimeZoneUnchanged); // the task is named after the time mode
        Assert.True(vm.CanApply); // ADM: OK always writes zone and mode
        Assert.True(vm.BuildPayload()!.TimeZoneUnchanged);
        vm.AdjustForDst = false;
        Assert.False(vm.TimeZoneUnchanged);
        Assert.False(vm.BuildPayload()!.DaylightSaving);
    }

    [Fact]
    public void Choosing_a_time_zone()
    {
        var vm = Create(3);
        vm.UseDhcp = true;

        vm.SelectedZone = vm.Zones.Single(z => z.ShortLabel == "(UTC+01:00) Vienna");

        Assert.True(vm.CanApply);
        Assert.Equal("Europe/Vienna", vm.BuildPayload()!.TimeZone);
        Assert.Equal(TimeMode.Ntp, vm.BuildPayload()!.Mode);
        Assert.True(vm.CanAdjustDst);
        Assert.Equal("10.0.0.1 · P3265-V, first of 3 devices", vm.DeviceLabel);
    }

    [Fact]
    public void Ntp_servers_report_errors_on_the_field()
    {
        var vm = Create();
        vm.SelectedZone = Zone("Europe/Vienna");
        vm.IsNtp = true;
        vm.UseDhcp = false;
        vm.NtpServersText = "10.0.0.17, bad host!";

        Assert.Equal(["\"host!\" is not a valid host name or IP address."], Errors(vm, nameof(vm.NtpServersText)));
        Assert.True(vm.HasErrors);
        Assert.False(vm.CanApply);

        vm.NtpServersText = "10.0.0.17,pool.ntp.org\n"; // commas, also new lines (pasted)

        Assert.Empty(Errors(vm, nameof(vm.NtpServersText)));
        Assert.True(vm.CanApply);
        Assert.Equal(["10.0.0.17", "pool.ntp.org"], vm.BuildPayload()!.Ntp!.Servers);
    }

    [Fact]
    public void Dhcp_needs_no_server_list()
    {
        var vm = Create();
        vm.SelectedZone = Zone("Europe/Vienna");
        vm.IsNtp = true;
        vm.UseDhcp = true;
        vm.NtpServersText = "garbage !!";

        Assert.False(vm.ShowServerList);
        Assert.Empty(Errors(vm, nameof(vm.NtpServersText)));
        Assert.True(vm.CanApply);
        Assert.Equal(NtpSource.Dhcp, vm.BuildPayload()!.Ntp!.Source);
        Assert.Empty(vm.BuildPayload()!.Ntp!.Servers);
    }

    [Fact]
    public void Nts_uses_the_server_list()
    {
        var vm = Create();
        vm.SelectedZone = Zone("Europe/Vienna");
        vm.IsNtp = true;
        vm.UseDhcp = true;
        vm.UseNts = true;
        vm.NtpServersText = "nts.netnod.se";

        Assert.True(vm.ShowNts);
        Assert.True(vm.ShowServerList);
        Assert.StartsWith("Up to 5 NTS KE servers", vm.ServersPlaceholder, StringComparison.Ordinal);
        Assert.False(vm.HasNtsHint); // every selected device can use NTS
        var ntp = vm.BuildPayload()!.Ntp!;
        Assert.True(ntp.Nts);
        Assert.Equal(NtpSource.Static, ntp.Source);
    }

    [Fact]
    public void Manual_date_and_time_errors_on_the_fields()
    {
        var vm = Create();
        vm.ApplyCurrent(Current10048);
        vm.IsManual = true;
        vm.ManualDate = "07.10.2026";
        vm.ManualTime = "25:00";

        Assert.Equal(["Enter the date as yyyy-mm-dd."], Errors(vm, nameof(vm.ManualDate)));
        Assert.Equal(["Enter the time as hh:mm or hh:mm:ss."], Errors(vm, nameof(vm.ManualTime)));
        Assert.False(vm.CanApply);

        vm.ManualDate = "2070-01-01";
        vm.ManualTime = "18:00";
        Assert.Equal(["The year must be between 1970 and 2069."], Errors(vm, nameof(vm.ManualDate)));

        vm.ManualDate = "2026-10-07";
        Assert.False(vm.HasErrors);
        Assert.Equal("2026-10-07T18:00:00", vm.BuildPayload()!.ManualDateTime);

        vm.UseComputerTimeCommand.Execute(null);
        Assert.Equal("18:30:00", vm.ManualTime);
    }

    [Fact]
    public void Server_time_uses_the_server_zone()
    {
        var vm = Create();
        vm.ApplyCurrent(Current10048);
        vm.SelectedZone = Zone("Asia/Tokyo");

        vm.IsServerTime = true;

        Assert.False(vm.CanEditTimeZone);
        Assert.False(vm.TimeZoneUnchanged);
        Assert.Contains("the server's time zone (Europe/Vienna)", vm.ServerTimeNote, StringComparison.Ordinal);
        Assert.Null(vm.NtpNote);
        Assert.Null(vm.ManualNote);
        var payload = vm.BuildPayload()!;
        Assert.Equal(TimeMode.ServerTime, payload.Mode);
        Assert.Null(payload.TimeZone);
        Assert.Contains("once", vm.ModeNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_returns_the_payload_and_closes()
    {
        var vm = Create();
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        vm.IsServerTime = true;

        vm.ApplyCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(TimeMode.ServerTime, DateTimePayload.Parse(vm.ResultJson).Mode);
        vm.CancelCommand.Execute(null);
        Assert.False(closed);
    }

    [Fact]
    public void Load_error_is_shown_in_the_device_card()
    {
        var vm = Create();

        vm.ShowLoadError("Unauthorized - HTTP 401");

        Assert.True(vm.DeviceTimeFailed);
        Assert.False(vm.IsLoading);
        Assert.Equal("The device time could not be read: Unauthorized - HTTP 401", vm.DeviceTimeStatus);
    }

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public void Five_thousand_devices_are_summarized_in_one_pass()
    {
        var devices = Enumerable.Range(0, 5000)
            .Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.{i / 62500}.{i / 250 % 250}.{i % 250 + 1}")
            {
                Apis = (i % 10) switch { 0 => Fixture.LegacyOnly, 1 => Fixture.NoNts, _ => Fixture.Modern },
            })
            .ToList();
        var watch = Stopwatch.StartNew();

        var vm = new DateTimeDialogViewModel(devices, () => ClientNow);
        vm.IsNtp = true;
        vm.NtpServersText = "10.0.0.1, 10.0.0.2";
        vm.SelectedZone = Zone("Europe/Vienna");
        vm.IsManual = true;

        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(4000, vm.NtsDeviceCount);
        Assert.Equal(500, vm.NoTimeApiCount);
        Assert.Equal(500, vm.SingleNtpServerCount);
        Assert.Equal("Set date and time for 5,000 devices", vm.Title);
        Assert.Contains("500 of the selected devices have no Time API", vm.ModeNote, StringComparison.Ordinal);
        vm.IsNtp = true;
        Assert.StartsWith("500 of the selected devices take only one NTP server", vm.NtpNote, StringComparison.Ordinal);
        Assert.False(vm.HasNtsHint);
        vm.UseNts = true;
        Assert.True(vm.HasNtsHint);
        Assert.Equal("NTS works on 4,000 of 5,000 devices; the others are not changed.", vm.NtsHint);
    }
}
