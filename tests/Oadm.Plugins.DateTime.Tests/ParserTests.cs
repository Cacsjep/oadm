using System.Text.Json;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;

namespace Oadm.Plugins.DateAndTime.Tests;

/// <summary>Recorded read-only answers of 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77).</summary>
public sealed class ParserTests
{
    [Fact]
    public void GetDateTimeInfo_with_a_posix_time_zone()
    {
        var s = TimeParsers.ParseDateTimeInfo(Fixture.Read(Fixture.DateTimeInfo), new CurrentTimeSettings());

        Assert.Equal(new DateTimeOffset(2026, 10, 7, 16, 24, 1, TimeSpan.Zero), s.DeviceUtc);
        Assert.Equal(TimeSpan.FromHours(2), s.DeviceLocal!.Value.Offset);
        Assert.Equal(18, s.DeviceLocal.Value.Hour);
        Assert.Null(s.TimeZone); // a POSIX zone is set, no IANA id
        Assert.Equal("<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00", s.PosixTimeZone);
        Assert.True(s.DstEnabled);
        Assert.Equal(2069, s.MaxYear);
    }

    [Fact]
    public void GetDateTimeInfo_with_an_iana_time_zone_as_documented()
    {
        const string json = """{"apiVersion":"1.0","method":"getDateTimeInfo","data":{"dateTime":"2018-11-19T13:26:53Z","localDateTime":"2018-11-19T14:26:53+01:00","maxYearSupported":2069,"timeZone":"Europe/Stockholm"}}""";

        var s = TimeParsers.ParseDateTimeInfo(json, new CurrentTimeSettings());

        Assert.Equal("Europe/Stockholm", s.TimeZone);
        Assert.Null(s.PosixTimeZone);
    }

    [Fact]
    public void GetNtpInfo_static_server_synchronized()
    {
        var s = TimeParsers.ParseNtpInfo(Fixture.Read(Fixture.NtpInfo), new CurrentTimeSettings());

        Assert.True(s.NtpEnabled);
        Assert.Equal(NtpSource.Static, s.NtpSource);
        Assert.Equal(["10.0.0.17"], s.NtpServers);
        Assert.Empty(s.AdvertisedServers);
        Assert.False(s.NtsEnabled);
        Assert.Empty(s.NtsServers);
        Assert.True(s.Synced);
        Assert.Equal(5, s.MaxStaticServers);
        Assert.Equal(0.016422, s.NtpOffsetMilliseconds);
    }

    [Fact]
    public void GetNtpInfo_dhcp_as_documented()
    {
        const string json = """{"apiVersion":"1.0","context":"abc","method":"getNTPInfo","data":{"client":{"enabled":true,"NTSEnabled":false,"serversSource":"DHCP","maxSupportedStaticServers":5,"staticServers":["192.168.0.80"],"advertisedServers":["ntp.someserver.com","12.7.232.11"],"staticNTSKEServers":["ntske.someserver.com"],"synced":true,"timeToNextSync":1234,"timeOffset":-12.34,"minpoll":2,"maxpoll":2}}}""";

        var s = TimeParsers.ParseNtpInfo(json, new CurrentTimeSettings());

        Assert.Equal(NtpSource.Dhcp, s.NtpSource);
        Assert.Equal(["ntp.someserver.com", "12.7.232.11"], s.AdvertisedServers);
        Assert.Equal(["ntske.someserver.com"], s.NtsServers);
    }

    [Fact]
    public void Param_cgi_time_group()
    {
        var s = TimeParsers.ParseParameters(Fixture.Parameters(Fixture.ParamTime), new CurrentTimeSettings());

        Assert.True(s.NtpEnabled); // Time.SyncSource=NTP
        Assert.Equal(NtpSource.Static, s.NtpSource); // Time.ObtainFromDHCP=no
        Assert.Equal(["10.0.0.17"], s.NtpServers);
        Assert.Empty(s.AdvertisedServers); // VolatileServer 0.0.0.0
        Assert.Equal("<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00", s.PosixTimeZone);
        Assert.True(s.DstEnabled);
    }

    [Fact]
    public void Param_cgi_keeps_what_the_time_api_reported()
    {
        var api = new CurrentTimeSettings { TimeZone = "Europe/Vienna", PosixTimeZone = "CET-1CEST", DstEnabled = true };
        var parameters = new Dictionary<string, string> { ["Time.SyncSource"] = "None", ["Time.POSIXTimeZone"] = "GMT0" };

        var s = TimeParsers.ParseParameters(parameters, api);

        Assert.Equal("Europe/Vienna", s.TimeZone);
        Assert.Equal("CET-1CEST", s.PosixTimeZone);
        Assert.False(s.NtpEnabled);
    }

    [Theory]
    [InlineData(Fixture.Error4000, "getDateTimeInfo failed (4000): Method does not exist")]
    [InlineData(Fixture.Error4001, "getDateTimeInfo failed (4001): The specified version is not supported")]
    public void Device_errors_become_readable_messages(string fixture, string message)
    {
        var ex = Assert.Throws<TimeApiException>(() => TimeParsers.ParseDateTimeInfo(Fixture.Read(fixture), new CurrentTimeSettings()));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Current_settings_round_trip_as_json()
    {
        var s = TimeParsers.ParseNtpInfo(Fixture.Read(Fixture.NtpInfo), TimeParsers.ParseDateTimeInfo(Fixture.Read(Fixture.DateTimeInfo), new CurrentTimeSettings()))
            with { ServerTimeZone = "Europe/Vienna", ServerUtc = Fixture.DeviceNow.AddSeconds(-1) };

        var back = CurrentTimeSettings.FromJson(s.ToJson());

        Assert.Equal(s.PosixTimeZone, back.PosixTimeZone);
        Assert.Equal(s.NtpServers, back.NtpServers);
        Assert.Equal(TimeSpan.FromSeconds(1), back.Difference);
        Assert.Contains("\"serverTimeZone\":\"Europe/Vienna\"", s.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rest_time_v2_fixture_agrees_with_the_time_api()
    {
        // The device-config REST API (not used yet, see README) reports the same zone as time.cgi.
        using var rest = JsonDocument.Parse(Fixture.Read("rest-time-v2-P3265-V-12.11.json"));
        var posix = rest.RootElement.GetProperty("data").GetProperty("timeZone").GetProperty("posix").GetProperty("timeZone").GetString();
        var s = TimeParsers.ParseDateTimeInfo(Fixture.Read(Fixture.DateTimeInfo), new CurrentTimeSettings());

        Assert.Equal(s.PosixTimeZone, posix);
    }
}
