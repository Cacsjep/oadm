using Oadm.Plugins.DateAndTime.Model;

namespace Oadm.Plugins.DateAndTime.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("10.0.0.17")]
    [InlineData("pool.ntp.org")]
    [InlineData("time-a.example.com.")]
    [InlineData("ntp1")]
    [InlineData("2001:db8::123")]
    [InlineData("[2001:db8::123]")]
    [InlineData("fe80::1")]
    public void Valid_ntp_servers(string server)
    {
        Assert.True(PayloadValidator.IsHostOrAddress(server));
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0")]
    [InlineData("10.0.0.256")]
    [InlineData("1")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("under_score.example.com")]
    [InlineData("two..dots")]
    [InlineData("http://pool.ntp.org")]
    [InlineData("pool.ntp.org:123")]
    public void Invalid_ntp_servers(string server)
    {
        Assert.False(PayloadValidator.IsHostOrAddress(server));
    }

    [Fact]
    public void Ntp_server_list_rules()
    {
        Assert.Null(PayloadValidator.ValidateNtpServers(["10.0.0.17", "pool.ntp.org"]));
        Assert.Equal("Enter at least one NTP server.", PayloadValidator.ValidateNtpServers([]));
        Assert.Equal("Enter at least one NTS KE server.", PayloadValidator.ValidateNtpServers([" "], nts: true));
        Assert.Equal("At most 5 NTP servers are supported.", PayloadValidator.ValidateNtpServers(["a", "b", "c", "d", "e", "f"]));
        Assert.Equal("\"A.example\" is listed twice.", PayloadValidator.ValidateNtpServers(["a.example", "A.example"]));
        Assert.Equal("\"10.0.0\" is not a valid host name or IP address.", PayloadValidator.ValidateNtpServers(["10.0.0"]));
    }

    [Theory]
    [InlineData("Europe/Vienna", null)]
    [InlineData("UTC", null)]
    [InlineData("europe/vienna", "\"europe/vienna\" is not a time zone AXIS devices know.")]
    [InlineData("CET", "\"CET\" is not a time zone AXIS devices know.")]
    [InlineData("", "Select a time zone.")]
    public void Time_zone_ids(string id, string? error)
    {
        Assert.Equal(error, PayloadValidator.ValidateTimeZone(id));
    }

    [Theory]
    [InlineData("2026-10-07T18:00:00", 2069, null)]
    [InlineData("2070-01-01T00:00:00", 2069, "The year must be between 1970 and 2069.")]
    [InlineData("1969-12-31T23:59:59", 2069, "The year must be between 1970 and 2069.")]
    [InlineData("2026-13-01T00:00:00", 2069, "Enter the date as yyyy-mm-dd and the time as hh:mm or hh:mm:ss.")]
    [InlineData(null, 2069, "Enter the date and time.")]
    public void Manual_date_and_time(string? value, int maxYear, string? error)
    {
        Assert.Equal(error, PayloadValidator.ValidateManualDateTime(value, maxYear));
    }

    [Fact]
    public void Payload_needs_a_change()
    {
        var errors = PayloadValidator.Validate(new DateTimePayload());

        Assert.Equal(PayloadValidator.FieldMode, Assert.Single(errors).Field);
    }

    [Fact]
    public void Payload_errors_name_their_field()
    {
        var payload = new DateTimePayload("Mars/Base", TimeMode.Ntp, new NtpSettings(NtpSource.Static, ["bad host"]));

        var errors = PayloadValidator.Validate(payload);

        Assert.Equal([PayloadValidator.FieldTimeZone, PayloadValidator.FieldNtpServers], errors.Select(e => e.Field));
        var ex = Assert.Throws<ArgumentException>(() => PayloadValidator.ThrowIfInvalid(payload));
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dhcp_needs_no_servers_but_nts_does()
    {
        Assert.Empty(PayloadValidator.Validate(new DateTimePayload(Mode: TimeMode.Ntp, Ntp: new NtpSettings(NtpSource.Dhcp, []))));
        Assert.Single(PayloadValidator.Validate(new DateTimePayload(Mode: TimeMode.Ntp, Ntp: new NtpSettings(NtpSource.Static, [], Nts: true))));
    }

    [Fact]
    public void Payload_json_round_trip()
    {
        var payload = new DateTimePayload("Europe/Vienna", TimeMode.Ntp, new NtpSettings(NtpSource.Static, ["10.0.0.17"], Nts: false), DaylightSaving: false);

        var json = payload.ToJson();
        var back = DateTimePayload.Parse(json);

        Assert.Contains("\"mode\":\"Ntp\"", json, StringComparison.Ordinal);
        Assert.Equal(payload.TimeZone, back.TimeZone);
        Assert.Equal(payload.Ntp!.Servers, back.Ntp!.Servers);
        Assert.False(back.DaylightSaving);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    public void Unreadable_payload(string? json)
    {
        var ex = Assert.Throws<ArgumentException>(() => DateTimePayload.Parse(json));

        Assert.Contains("Nothing was changed.", ex.Message, StringComparison.Ordinal);
    }
}
