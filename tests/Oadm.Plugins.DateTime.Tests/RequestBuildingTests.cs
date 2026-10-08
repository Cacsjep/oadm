using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DateAndTime.Tests;

/// <summary>Exact request bodies per API version (decision table in the plugin README).</summary>
public sealed class RequestBuildingTests
{
    private static readonly CurrentTimeSettings Current10048 =
        TimeParsers.ParseNtpInfo(Fixture.Read(Fixture.NtpInfo), TimeParsers.ParseDateTimeInfo(Fixture.Read(Fixture.DateTimeInfo), new CurrentTimeSettings()))
            with { SupportsNts = true };

    private static TimePlan Plan(DateTimePayload payload, IReadOnlyList<DeviceApi> apis, CurrentTimeSettings? current = null, string? serverZone = "Europe/Berlin") =>
        TimePlanner.Build(payload, apis, current ?? Current10048, serverZone, 2026);

    private static string[] Bodies(TimePlan plan) => [.. plan.Writes.SelectMany(s => s.Requests).Select(r => r.Body)];

    /// <summary>What 10.0.0.48 already has: NTP on with 10.0.0.17 (so the time mode section is skipped).</summary>
    private static readonly NtpSettings SameNtp = new(NtpSource.Static, ["10.0.0.17"]);

    /// <summary>Like ADM every payload has a time zone and a time mode; this one changes only the zone on 10.0.0.48.</summary>
    private static DateTimePayload Zone(string zone, bool dst = true) => new(zone, TimeMode.Ntp, SameNtp, DaylightSaving: dst);

    /// <summary>Changes only NTP on 10.0.0.48 when <paramref name="current"/> has Europe/Vienna.</summary>
    private static DateTimePayload Ntp(NtpSettings ntp) => new("Europe/Vienna", TimeMode.Ntp, ntp);

    private static readonly CurrentTimeSettings Vienna = Current10048 with { TimeZone = "Europe/Vienna" };

    private static string[] NtpBodies(TimePlan plan) => [.. plan.Find(SectionKind.Ntp)!.Requests.Select(r => r.Body)];

    [Fact]
    public void Time_zone_and_time_mode_are_always_planned()
    {
        // ADM: OK writes both; values the device already has are skipped ("Already ...").
        var plan = Plan(new DateTimePayload("Europe/Vienna", TimeMode.Ntp, SameNtp), Fixture.Modern, Vienna);

        Assert.Equal([TimePlanner.StepSetTimeZone, TimePlanner.StepSetNtp], plan.Sections.Select(s => s.StepName));
        Assert.Equal(["Already Europe/Vienna", "Already set"], plan.Sections.Select(s => s.SkipReason));
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void Time_zone_uses_setTimeZone_with_the_listed_version()
    {
        var plan = Plan(Zone("Europe/Vienna"), Fixture.Modern);

        var request = Assert.Single(plan.Writes.Single().Requests);
        Assert.Equal("axis-cgi/time.cgi", request.Path);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{"apiVersion":"1.1","context":"oadm","method":"setTimeZone","params":{"timeZone":"Europe/Vienna"}}""", request.Body);
        Assert.Equal("Europe/Vienna", plan.Expected.TimeZone);
    }

    [Fact]
    public void Time_zone_unknown_to_the_server_os_is_still_set_by_iana_id()
    {
        // The device applies its own rules; the server's time zone data is only needed for a POSIX string.
        var plan = Plan(Zone("Antarctica/Troll"), Fixture.Modern);

        Assert.Equal(["""{"apiVersion":"1.1","context":"oadm","method":"setTimeZone","params":{"timeZone":"Antarctica/Troll"}}"""], Bodies(plan));
    }

    [Fact]
    public void Time_zone_without_daylight_saving_uses_setPosixTimeZone()
    {
        var plan = Plan(Zone("America/New_York", dst: false), Fixture.NoNts);

        Assert.Equal(
            ["""{"apiVersion":"1.0","context":"oadm","method":"setPosixTimeZone","params":{"posixTimeZone":"<UTC-5>5<UTC-4>4,M3.2.0/2:00:00,M11.1.0/2:00:00","enableDst":false}}"""],
            Bodies(plan));
    }

    [Fact]
    public void Time_zone_on_legacy_firmware_uses_param_cgi()
    {
        var plan = Plan(Zone("Europe/London"), Fixture.LegacyOnly);

        var request = Assert.Single(plan.Find(SectionKind.TimeZone)!.Requests);
        Assert.Equal("axis-cgi/param.cgi", request.Path);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("action=update&Time.POSIXTimeZone=%3CUTC0%3E0%3CUTC1%3E-1%2CM3.5.0%2F1%3A00%3A00%2CM10.5.0%2F2%3A00%3A00&Time.DST.Enabled=yes", request.Body);
    }

    [Fact]
    public void Same_posix_zone_is_skipped()
    {
        // param.cgi only: the POSIX rule of Europe/Vienna is what 10.0.0.48 already has, so nothing is written.
        var plan = Plan(Zone("Europe/Vienna"), Fixture.LegacyOnly, TimeParsers.ParseParameters(Fixture.Parameters(Fixture.ParamTime), new CurrentTimeSettings()));

        Assert.StartsWith("Already <UTC1>", plan.Find(SectionKind.TimeZone)!.SkipReason, StringComparison.Ordinal);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void Same_iana_zone_is_skipped()
    {
        var plan = Plan(Zone("Europe/Vienna"), Fixture.Modern, Vienna);

        Assert.Equal("Already Europe/Vienna", plan.Find(SectionKind.TimeZone)!.SkipReason);
    }

    [Fact]
    public void Static_ntp_servers_on_ntp_1_5_send_the_nts_fields_too()
    {
        var plan = Plan(Ntp(new NtpSettings(NtpSource.Static, ["10.0.0.1", "pool.ntp.org"])), Fixture.Modern, Vienna);

        Assert.Equal(
            ["""{"apiVersion":"1.5","context":"oadm","method":"setNTPClientConfiguration","params":{"enabled":true,"serversSource":"static","staticServers":["10.0.0.1","pool.ntp.org"],"NTSEnabled":false,"staticNTSKEServers":[]}}"""],
            Bodies(plan));
        Assert.Equal(TimePlanner.StepSetNtp, plan.Writes.Single().StepName);
    }

    [Fact]
    public void Ntp_1_0_gets_no_nts_fields()
    {
        var plan = Plan(Ntp(new NtpSettings(NtpSource.Dhcp, [])), Fixture.NoNts, Vienna with { SupportsNts = false });

        Assert.Equal(
            ["""{"apiVersion":"1.0","context":"oadm","method":"setNTPClientConfiguration","params":{"enabled":true,"serversSource":"DHCP","staticServers":["10.0.0.17"]}}"""],
            NtpBodies(plan));
    }

    [Fact]
    public void Nts_servers()
    {
        var plan = Plan(Ntp(new NtpSettings(NtpSource.Static, ["nts.netnod.se"], Nts: true)), Fixture.Modern, Vienna);

        Assert.Equal(
            ["""{"apiVersion":"1.5","context":"oadm","method":"setNTPClientConfiguration","params":{"enabled":true,"serversSource":"static","staticServers":["10.0.0.17"],"NTSEnabled":true,"staticNTSKEServers":["nts.netnod.se"]}}"""],
            Bodies(plan));
    }

    [Fact]
    public void Nts_needs_ntp_1_5()
    {
        var payload = Ntp(new NtpSettings(NtpSource.Static, ["nts.netnod.se"], Nts: true));

        var ex = Assert.Throws<DeviceNotCompatibleException>(() => Plan(payload, Fixture.NoNts));

        Assert.Equal("Device has ntp 1.0, needs 1.5 or later. Nothing was changed.", ex.Message);
    }

    [Fact]
    public void Same_ntp_configuration_is_skipped()
    {
        var plan = Plan(Ntp(SameNtp), Fixture.Modern);

        Assert.Equal("Already set", plan.Find(SectionKind.Ntp)!.SkipReason);
    }

    [Fact]
    public void Too_many_servers_for_the_device()
    {
        var payload = Ntp(new NtpSettings(NtpSource.Static, ["a1", "a2", "a3"]));

        var ex = Assert.Throws<ArgumentException>(() => Plan(payload, Fixture.Modern, Current10048 with { MaxStaticServers = 2 }));

        Assert.Equal("This device takes at most 2 NTP servers. Nothing was changed.", ex.Message);
    }

    [Fact]
    public void Ntp_on_legacy_firmware_uses_param_cgi_with_one_server()
    {
        var plan = Plan(Ntp(new NtpSettings(NtpSource.Static, ["10.0.0.1"])), Fixture.LegacyOnly);
        Assert.Equal(["action=update&Time.SyncSource=NTP&Time.ObtainFromDHCP=no&Time.NTP.Server=10.0.0.1"], NtpBodies(plan));

        var dhcp = Plan(Ntp(new NtpSettings(NtpSource.Dhcp, [])), Fixture.LegacyOnly);
        Assert.Equal(["action=update&Time.SyncSource=NTP&Time.ObtainFromDHCP=yes"], NtpBodies(dhcp));

        var two = Ntp(new NtpSettings(NtpSource.Static, ["10.0.0.1", "10.0.0.2"]));
        Assert.Equal("This device takes one NTP server (no NTP API, param.cgi only). Nothing was changed.", Assert.Throws<ArgumentException>(() => Plan(two, Fixture.LegacyOnly)).Message);
    }

    [Fact]
    public void Manual_time_turns_off_ntp_and_converts_the_wall_time_in_the_device_zone()
    {
        var plan = Plan(new DateTimePayload("Europe/Vienna", TimeMode.Manual, ManualDateTime: "2026-10-07T18:00:00"), Fixture.Modern);

        Assert.Equal([TimePlanner.StepSetTimeZone, TimePlanner.StepTurnOffNtp, TimePlanner.StepSetDateTime], plan.Sections.Select(s => s.StepName));
        Assert.Contains("\"method\":\"setNTPClientConfiguration\",\"params\":{\"enabled\":false,\"serversSource\":\"static\",\"staticServers\":[\"10.0.0.17\"]", Bodies(plan)[1], StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 16, 0, 0, TimeSpan.Zero), plan.ManualUtc); // CEST = UTC+2
        Assert.Equal("""{"apiVersion":"1.1","context":"oadm","method":"setDateTime","params":{"dateTime":"2026-10-07T16:00:00Z"}}""", plan.DateTimeRequest(plan.ManualUtc!.Value).Body);
    }

    [Fact]
    public void Manual_time_without_a_known_zone_uses_the_device_offset()
    {
        // A zone this server cannot convert (none given): 10.0.0.48's current offset +02:00 applies.
        var utc = TimePlanner.ToUtc(new System.DateTime(2026, 10, 7, 18, 0, 0), null, dst: true, Current10048);

        Assert.Equal(new DateTimeOffset(2026, 10, 7, 16, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void Manual_time_in_the_spring_gap_is_refused()
    {
        var payload = new DateTimePayload("Europe/Vienna", TimeMode.Manual, ManualDateTime: "2027-03-28T02:30:00");

        var ex = Assert.Throws<ArgumentException>(() => Plan(payload, Fixture.Modern));

        Assert.Contains("does not exist in Europe/Vienna", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_year_beyond_the_device_limit_is_refused()
    {
        var payload = new DateTimePayload("Europe/Vienna", TimeMode.Manual, ManualDateTime: "2070-01-01T00:00:00");

        Assert.Throws<ArgumentException>(() => Plan(payload, Fixture.Modern));
    }

    [Fact]
    public void Server_time_sets_the_server_zone_turns_off_ntp_and_sends_server_time()
    {
        var plan = Plan(new DateTimePayload(null, TimeMode.ServerTime), Fixture.Modern, serverZone: "Europe/Berlin");

        Assert.Equal([TimePlanner.StepSetTimeZone, TimePlanner.StepTurnOffNtp, TimePlanner.StepSetDateTime], plan.Sections.Select(s => s.StepName));
        Assert.Contains("\"timeZone\":\"Europe/Berlin\"", Bodies(plan)[0], StringComparison.Ordinal);
        Assert.True(plan.UseServerTime);
        Assert.Null(plan.ManualUtc);
    }

    [Fact]
    public void Server_time_with_an_unknown_server_zone_warns_on_the_zone()
    {
        var plan = Plan(new DateTimePayload(null, TimeMode.ServerTime), Fixture.Modern, serverZone: null);

        var zone = plan.Sections[0];
        Assert.True(zone.SkipIsWarning);
        Assert.Equal(TimePlanner.StepSetTimeZone, zone.StepName);
    }

    [Fact]
    public void Date_and_time_needs_the_time_api()
    {
        var ex = Assert.Throws<DeviceNotCompatibleException>(() => Plan(new DateTimePayload("Europe/Vienna", TimeMode.Manual, ManualDateTime: "2026-10-07T18:00:00"), Fixture.LegacyOnly));

        Assert.Equal("Device does not support time-service (needs 1.0 or later). Nothing was changed.", ex.Message);
    }

    [Fact]
    public void Compatibility_per_api()
    {
        Assert.True(TimeApis.CanConfigure(Fixture.Modern));
        Assert.True(TimeApis.CanConfigure(Fixture.LegacyOnly));
        Assert.False(TimeApis.CanConfigure(Fixture.FutureMajor));
        Assert.False(TimeApis.CanConfigure([]));
        Assert.Equal("time-service 1.1, ntp 1.5", TimeApis.Describe(Fixture.Modern.ToList()));
        Assert.Equal("param.cgi", TimeApis.Describe(Fixture.LegacyOnly.ToList()));
        Assert.Equal("time-service 1.0, param.cgi", TimeApis.Describe(Fixture.TimeServiceOnly.ToList()));
    }
}
