using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.DateAndTime.Tests;

/// <summary>Step sequences through the same end rules as the task engine, against a stateful fake device.</summary>
public sealed class TaskPluginTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 16, 24, 0, TimeSpan.Zero));
    private readonly FakeTimeVapix _vapix;
    private readonly RecordingContext _ctx;
    private readonly DateTimeTaskPlugin _plugin;

    public TaskPluginTests()
    {
        _vapix = new FakeTimeVapix(_time);
        _ctx = new RecordingContext(_vapix);
        _plugin = new DateTimeTaskPlugin(_time, () => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
    }

    private Task RunAsync(DateTimePayload payload, IDeviceInfo? device = null) =>
        StepRun.RunAsync(_ctx.Steps, () => _plugin.ExecuteAsync(_ctx, device ?? new FakeDevice(Guid.NewGuid()), payload.ToJson(), CancellationToken.None));

    private string[] Lines => StepRun.Lines(_ctx.Steps);

    private string? Detail(string step) => StepRun.Detail(_ctx.Steps, step);

    [Fact]
    public void Plugin_identity()
    {
        Assert.Equal("oadm.datetime", _plugin.Id);
        Assert.Equal("Date and time", _plugin.DisplayName);
        Assert.Equal(TaskGroups.Maintenance, _plugin.Group);
        Assert.Equal("clock", _plugin.IconKey);
        Assert.True(_plugin.RequiresDialog);
        Assert.False(_plugin.ShowInToolbar);
        Assert.True(_plugin.DisplayName.Length <= TaskPluginNames.MaxDisplayNameLength);
    }

    [Fact]
    public void CanRun_uses_the_cached_api_list_and_status()
    {
        Assert.True(_plugin.CanRun(new FakeDevice(Guid.NewGuid())));
        Assert.True(_plugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = Fixture.LegacyOnly }));
        Assert.False(_plugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = [] }));
        Assert.False(_plugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = Fixture.FutureMajor }));
        Assert.False(_plugin.CanRun(new FakeDevice(Guid.NewGuid(), Status: DeviceStatus.CredentialsRequired)));
        Assert.False(_plugin.CanRun(new FakeDevice(Guid.NewGuid(), Status: DeviceStatus.CertificateChanged)));
    }

    [Fact]
    public async Task Time_zone_only()
    {
        await RunAsync(new DateTimePayload("Europe/Vienna"));

        Assert.Equal(
        [
            "Check compatibility: Done", "Read current time settings: Done", "Read NTP settings: Done", "Validate settings: Done",
            "Set time zone: Done", "Verify time settings: Done", "Completed: Done",
        ], Lines);
        Assert.Equal("setTimeZone", Assert.Single(_vapix.Writes).Method);
        Assert.Equal("time-service 1.1, ntp 1.5", Detail("Check compatibility"));
        Assert.Equal("2026-10-07 18:24:01 (UTC+02:00), POSIX <UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00, daylight saving on", Detail("Read current time settings"));
        Assert.Equal("NTP servers 10.0.0.17, synchronized", Detail("Read NTP settings"));
        Assert.Equal("Time zone Europe/Vienna", Detail("Set time zone"));
        Assert.EndsWith("Europe/Vienna", Detail("Verify time settings"), StringComparison.Ordinal);
        Assert.Empty(_ctx.Warnings);
    }

    [Fact]
    public async Task Ntp_servers()
    {
        await RunAsync(new DateTimePayload(Mode: TimeMode.Ntp, Ntp: new NtpSettings(NtpSource.Static, ["10.0.0.1", "pool.ntp.org"])));

        Assert.Equal(
        [
            "Check compatibility: Done", "Read current time settings: Done", "Read NTP settings: Done", "Validate settings: Done",
            "Set NTP configuration: Done", "Verify time settings: Skipped", "Verify NTP settings: Done", "Completed: Done",
        ], Lines);
        Assert.Equal("NTP servers 10.0.0.1, pool.ntp.org, not synchronized (the device synchronizes within a few minutes)", Detail("Verify NTP settings"));
        Assert.Equal("Time zone and time were not changed.", Detail("Verify time settings"));
    }

    [Fact]
    public async Task Unchanged_ntp_is_skipped_and_nothing_is_written()
    {
        await RunAsync(new DateTimePayload(Mode: TimeMode.Ntp, Ntp: new NtpSettings(NtpSource.Static, ["10.0.0.17"])));

        Assert.Equal("Set NTP configuration: Skipped", Lines[4]);
        Assert.Equal("Already set", Detail("Set NTP configuration"));
        Assert.Equal("Nothing was changed.", Detail("Verify NTP settings"));
        Assert.Empty(_vapix.Writes);
    }

    [Fact]
    public async Task Sync_with_server_time()
    {
        await RunAsync(new DateTimePayload(Mode: TimeMode.ServerTime));

        Assert.Equal(
        [
            "Check compatibility: Done", "Read current time settings: Done", "Read NTP settings: Done", "Validate settings: Done",
            "Set time zone: Done", "Turn off NTP: Done", "Set date and time: Done", "Verify time settings: Done", "Verify NTP settings: Done", "Completed: Done",
        ], Lines);
        Assert.Equal(["setTimeZone", "setNTPClientConfiguration", "setDateTime"], _vapix.Writes.Select(w => w.Method));
        Assert.Equal("Europe/Berlin", _vapix.Writes[0].Params!["timeZone"]!.GetValue<string>());
        Assert.Equal("2026-10-07T16:24:00Z", _vapix.Writes[2].Params!["dateTime"]!.GetValue<string>()); // server UTC when sent
        Assert.Equal("2026-10-07T16:24:00Z (server time)", Detail("Set date and time"));
        Assert.Equal("NTP off", Detail("Verify NTP settings"));
        Assert.Empty(_ctx.Warnings);
    }

    [Fact]
    public async Task Manual_date_and_time()
    {
        await RunAsync(new DateTimePayload("Europe/Vienna", TimeMode.Manual, ManualDateTime: "2026-12-24T18:00:00"));

        Assert.Equal(["setTimeZone", "setNTPClientConfiguration", "setDateTime"], _vapix.Writes.Select(w => w.Method));
        Assert.Equal("2026-12-24T17:00:00Z", _vapix.Writes[2].Params!["dateTime"]!.GetValue<string>()); // CET = UTC+1 in December
        Assert.Equal("Verify time settings: Done", Lines[7]);
        Assert.Equal(new DateTimeOffset(2026, 12, 24, 17, 0, 0, TimeSpan.Zero), _vapix.DeviceUtc);
    }

    [Fact]
    public async Task Legacy_param_cgi_device()
    {
        _vapix.ApiList = Fixture.LegacyOnly;

        await RunAsync(new DateTimePayload("Europe/London", TimeMode.Ntp, new NtpSettings(NtpSource.Dhcp, [])), new FakeDevice(Guid.NewGuid()) { Apis = Fixture.LegacyOnly });

        Assert.Equal(
        [
            "Check compatibility: Done", "Read current time settings: Done", "Read NTP settings: Skipped", "Validate settings: Done",
            "Set time zone: Done", "Set NTP configuration: Done", "Verify time settings: Done", "Verify NTP settings: Done", "Completed: Done",
        ], Lines);
        Assert.Equal("param.cgi", Detail("Check compatibility"));
        Assert.Equal("Read with the current time settings (param.cgi)", Detail("Read NTP settings"));
        Assert.All(_vapix.Writes, w => Assert.StartsWith("action=update", w.Body, StringComparison.Ordinal));
        Assert.Equal("yes", _vapix.Parameters["Time.ObtainFromDHCP"]);
        Assert.Equal("<UTC0>0<UTC1>-1,M3.5.0/1:00:00,M10.5.0/2:00:00", _vapix.Parameters["Time.POSIXTimeZone"]);
    }

    [Fact]
    public async Task Invalid_payload_fails_before_any_request()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new DateTimePayload(Mode: TimeMode.Ntp, Ntp: new NtpSettings(NtpSource.Static, ["bad host!"]))));

        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_vapix.Sent);
    }

    [Fact]
    public async Task Incompatible_device_fails_in_validate_with_nothing_changed()
    {
        _vapix.ApiList = Fixture.LegacyOnly;

        var ex = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunAsync(new DateTimePayload("Europe/Vienna", TimeMode.Manual, ManualDateTime: "2026-10-07T18:00:00")));

        Assert.Equal("Device does not support time-service (needs 1.0 or later). Nothing was changed.", ex.Message);
        Assert.Equal("Validate settings: Failed", Lines[3]);
        Assert.All(Lines[4..], l => Assert.EndsWith(": Skipped", l, StringComparison.Ordinal));
        Assert.Empty(_vapix.Writes);
    }

    [Fact]
    public async Task Future_major_version_is_not_compatible()
    {
        _vapix.ApiList = Fixture.FutureMajor;

        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunAsync(new DateTimePayload("Europe/Vienna")));

        Assert.Equal("Check compatibility: Failed", Lines[0]);
        Assert.Empty(_vapix.Sent);
    }

    [Fact]
    public async Task Device_error_on_the_first_write()
    {
        _vapix.Respond = r => r.Method == "setTimeZone"
            ? (HttpStatusCode.OK, """{"apiVersion":"1.1","method":"setTimeZone","error":{"code":2002,"message":"Invalid time zone"}}""")
            : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new DateTimePayload("Europe/Vienna", TimeMode.Ntp, new NtpSettings(NtpSource.Dhcp, []))));

        Assert.Equal("setTimeZone failed (2002): Invalid time zone. Nothing was changed.", ex.Message);
        Assert.Equal("Set time zone: Failed", Lines[4]);
        Assert.Equal("Set NTP configuration: Skipped", Lines[5]);
    }

    [Fact]
    public async Task Http_error_after_a_write_says_what_was_applied()
    {
        _vapix.Respond = r => r.Method == "setNTPClientConfiguration" ? (HttpStatusCode.Forbidden, "Forbidden") : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new DateTimePayload("Europe/Vienna", TimeMode.Ntp, new NtpSettings(NtpSource.Dhcp, []))));

        Assert.Equal("setNTPClientConfiguration failed: Forbidden - HTTP 403 (administrator rights are required): Forbidden. Already applied: Time zone Europe/Vienna.", ex.Message);
    }

    [Fact]
    public async Task Transport_error_is_readable()
    {
        _vapix.Respond = r => r.Method == "getDateTimeInfo" ? throw new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)) : null;

        var ex = await Assert.ThrowsAsync<Vapix.TimeApiException>(() => RunAsync(new DateTimePayload("Europe/Vienna")));

        Assert.Equal("getDateTimeInfo failed: Connection refused", ex.Message);
        Assert.Equal("Read current time settings: Failed", Lines[1]);
    }

    [Fact]
    public async Task Verify_warns_when_the_device_did_not_apply_the_change()
    {
        _vapix.ApplyWrites = false;

        await RunAsync(new DateTimePayload("Europe/Vienna", TimeMode.Ntp, new NtpSettings(NtpSource.Static, ["10.0.0.1"])));

        Assert.Equal("Verify time settings: Warning", Lines[6]);
        Assert.Equal("Verify NTP settings: Warning", Lines[7]);
        Assert.StartsWith("Check failed: the device reports time zone <UTC1>", Detail("Verify time settings"), StringComparison.Ordinal);
        Assert.Equal("Check failed: the device lists the NTP servers 10.0.0.17.", Detail("Verify NTP settings"));
        Assert.Equal(2, _ctx.Warnings.Count);
    }

    [Fact]
    public async Task Verify_warns_when_the_clock_is_off()
    {
        // The device acknowledges setDateTime but keeps its clock 1 hour behind.
        _vapix.Respond = r => r.Method == "setDateTime" ? (HttpStatusCode.OK, """{"apiVersion":"1.1","method":"setDateTime","data":{}}""") : null;

        await RunAsync(new DateTimePayload(Mode: TimeMode.Manual, ManualDateTime: "2026-10-07T19:24:00"));

        Assert.Equal("Verify time settings: Warning", Lines.Single(l => l.StartsWith("Verify time", StringComparison.Ordinal)));
        Assert.Contains("differs by -3599.0 s", Detail("Verify time settings"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_returns_the_device_time_and_the_server_zone()
    {
        var json = await _plugin.QueryAsync(_ctx, new FakeDevice(Guid.NewGuid()), DateTimeTaskPlugin.QueryGetTimeSettings, null, CancellationToken.None);

        var s = CurrentTimeSettings.FromJson(json!);
        Assert.Equal("Europe/Berlin", s.ServerTimeZone);
        Assert.Equal("<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00", s.PosixTimeZone);
        Assert.Equal(["10.0.0.17"], s.NtpServers);
        Assert.True(s.SupportsNts);
        Assert.Equal(TimeSpan.FromSeconds(1), s.Difference);
        Assert.Empty(_vapix.Writes);
        await Assert.ThrowsAsync<NotSupportedException>(() => _plugin.QueryAsync(_ctx, new FakeDevice(Guid.NewGuid()), "setDateTime", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"timeZone":"Europe/Vienna"}""", "Set time zone Europe/Vienna")]
    [InlineData("""{"timeZone":"Europe/Vienna","daylightSaving":false}""", "Set time zone Europe/Vienna without DST")]
    [InlineData("""{"mode":"Ntp","ntp":{"source":"Static","servers":["10.0.0.17","pool.ntp.org"]}}""", "Set NTP servers 10.0.0.17, pool.ntp.org")]
    [InlineData("""{"mode":"Ntp","ntp":{"source":"Static","servers":["a1","a2","a3","a4","a5"]}}""", "Set NTP servers a1, a2, a3 +2")]
    [InlineData("""{"mode":"Ntp","ntp":{"source":"Dhcp","servers":[]}}""", "Set NTP servers from DHCP")]
    [InlineData("""{"mode":"Ntp","ntp":{"source":"Static","servers":["nts.netnod.se"],"nts":true}}""", "Set NTS KE servers nts.netnod.se")]
    [InlineData("""{"mode":"ServerTime"}""", "Sync with server time")]
    [InlineData("""{"mode":"Manual","manualDateTime":"2026-10-07T18:00:00"}""", "Set date and time 2026-10-07 18:00")]
    [InlineData("""{"mode":"Manual","manualDateTime":"2026-10-07T18:00:30"}""", "Set date and time 2026-10-07 18:00:30")]
    [InlineData("""{"timeZone":"Europe/Vienna","mode":"Manual","manualDateTime":"2026-10-07T18:00:00"}""", "Change date and time")]
    [InlineData("""{"timeZone":"Europe/Vienna","mode":"Ntp","ntp":{"source":"Dhcp","servers":[]}}""", "Change date and time")]
    [InlineData("not json", "Date and time")]
    [InlineData(null, "Date and time")]
    public void Task_names(string? payload, string name)
    {
        Assert.Equal(name, _plugin.GetTaskName(payload));
    }

    [Fact]
    public void Planned_steps_follow_the_payload()
    {
        Assert.Equal(
            ["Check compatibility", "Read current time settings", "Read NTP settings", "Validate settings", "Set time zone", "Verify time settings"],
            DateTimeTaskRunner.PlannedSteps(new DateTimePayload("Europe/Vienna")));
        Assert.Equal(
            ["Check compatibility", "Read current time settings", "Read NTP settings", "Validate settings", "Set time zone", "Turn off NTP", "Set date and time", "Verify time settings", "Verify NTP settings"],
            DateTimeTaskRunner.PlannedSteps(new DateTimePayload(Mode: TimeMode.ServerTime)));
    }

    [Fact]
    public void Payload_with_ntp_parameters_is_valid_json()
    {
        var json = new DateTimePayload(Mode: TimeMode.Ntp, Ntp: new NtpSettings(NtpSource.Static, ["10.0.0.1"])).ToJson();

        Assert.Equal("Static", JsonNode.Parse(json)!["ntp"]!["source"]!.GetValue<string>());
    }
}
