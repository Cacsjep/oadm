using System.Globalization;

using Oadm.Core.Devices;
using Oadm.Core.Tests.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Devices;

/// <summary>The clock offset from the Date header, the NTP state from getNTPInfo, and when a device counts as off.</summary>
public sealed class ClockHealthTests
{
    // The Date header recorded from a P3265-V (Fixtures/Vapix/401-http-headers.txt).
    private static readonly DateTimeOffset Recorded = DateTimeOffset.Parse("Wed, 07 Oct 2026 09:18:14 GMT", CultureInfo.InvariantCulture);

    [Fact]
    public void ADeviceInTimeIsWithinHalfASecond()
    {
        // The device sent "09:18:14" somewhere in 09:18:14.000..14.999; the round trip's middle is 14.400.
        var sent = Recorded.AddMilliseconds(380);
        var offset = ClockHealth.Offset(Recorded, sent, sent.AddMilliseconds(40));
        Assert.Equal(100, offset); // 14.000 + 0.5 - 14.400
    }

    [Fact]
    public void ADeviceAheadHasAPositiveOffset()
    {
        var sent = Recorded.AddSeconds(-50).AddMilliseconds(490);
        Assert.Equal(50_000, ClockHealth.Offset(Recorded, sent, sent.AddMilliseconds(20)));
    }

    [Fact]
    public void ADeviceBehindHasANegativeOffset()
    {
        var sent = Recorded.AddMinutes(2).AddMilliseconds(500);
        Assert.Equal(-120_000, ClockHealth.Offset(Recorded, sent, sent));
    }

    [Fact]
    public void TheHalfSecondCentersTheCutFraction()
    {
        // Without the half second, a device exactly in time would read 0.5 s behind on average.
        var sent = Recorded.AddMilliseconds(500);
        Assert.Equal(0, ClockHealth.Offset(Recorded, sent, sent));
    }

    [Fact]
    public void NoHeaderMeansNoReading()
    {
        Assert.Null(ClockHealth.Offset(null, Recorded, Recorded));
    }

    [Fact]
    public void AClockYearsOffIsClampedToTheIntRange()
    {
        var factoryNew = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(-int.MaxValue, ClockHealth.Offset(factoryNew, Recorded, Recorded));
    }

    [Theory]
    [InlineData(null, 300, 300)]
    [InlineData(300, null, 300)] // no header keeps the last value
    [InlineData(300, 900, 300)] // below a second: jitter of the whole-second header, kept
    [InlineData(300, 1300, 1300)]
    [InlineData(300, -700, -700)]
    public void ASmallChangeKeepsTheStoredOffset(int? stored, int? measured, int? expected)
    {
        Assert.Equal(expected, ClockHealth.Next(stored, measured));
    }

    [Theory]
    [InlineData(DeviceStatus.Ok, 2000, NtpState.Synced, false)]
    [InlineData(DeviceStatus.Ok, 2001, NtpState.Synced, true)]
    [InlineData(DeviceStatus.Ok, -2001, NtpState.Synced, true)]
    [InlineData(DeviceStatus.Ok, 0, NtpState.NotSynced, true)]
    [InlineData(DeviceStatus.Ok, 0, NtpState.Off, false)] // the column shows the offset; compliance can ask for NTP
    [InlineData(DeviceStatus.Ok, null, NtpState.Unknown, false)]
    [InlineData(DeviceStatus.Unreachable, 99_000, NtpState.NotSynced, false)] // not Ok: never off
    [InlineData(DeviceStatus.SiteOffline, 99_000, NtpState.Synced, false)]
    public void OffNeedsStatusOkAndAnOffsetBeyondTheToleranceOrNtpNotSynced(DeviceStatus status, int? offset, NtpState ntp, bool off)
    {
        Assert.Equal(off, ClockHealth.IsOff(status, offset, ntp, toleranceSeconds: 2));
    }

    [Fact]
    public void BeyondToleranceIgnoresTheStatus()
    {
        Assert.True(ClockHealth.IsBeyondTolerance(new Device { Status = DeviceStatus.Unreachable, ClockOffsetMs = 5000 }, 2));
        Assert.False(ClockHealth.IsBeyondTolerance(new Device { Status = DeviceStatus.Ok, ClockOffsetMs = null }, 2));
    }

    [Theory]
    [InlineData(400, "OK")]
    [InlineData(-999, "OK")]
    [InlineData(50_400, "51 s ahead")]
    [InlineData(2_100, "3 s ahead")]
    [InlineData(-120_000, "2 min behind")]
    [InlineData(10_800_000, "3 h ahead")]
    [InlineData(-90_000_000, "1 day behind")]
    [InlineData(-int.MaxValue, "24 days behind")]
    public void TheOffsetReadsInTheLargestWholeUnit(int offset, string text)
    {
        Assert.Equal(text, Oadm.Sdk.Devices.ClockTexts.OffsetText(offset));
    }

    [Fact]
    public void ARecordedSyncedAnswerIsSynced()
    {
        Assert.Equal(NtpState.Synced, ClockHealth.ParseNtpInfo(Fixtures.Read("ntp-getNTPInfo-P3265-V-12.11.json")));
    }

    [Fact]
    public void NotSyncedAndDisabledAreTold()
    {
        var recorded = Fixtures.Read("ntp-getNTPInfo-P3265-V-12.11.json");
        Assert.Equal(NtpState.NotSynced, ClockHealth.ParseNtpInfo(recorded.Replace("\"synced\": true", "\"synced\": false", StringComparison.Ordinal)));
        Assert.Equal(NtpState.Off, ClockHealth.ParseNtpInfo(recorded.Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnErrorOrMissingFieldsAreUnknown()
    {
        Assert.Equal(NtpState.Unknown, ClockHealth.ParseNtpInfo("""{"apiVersion":"1.5","method":"getNTPInfo","error":{"code":4000,"message":"Method does not exist"}}"""));
        Assert.Equal(NtpState.Unknown, ClockHealth.ParseNtpInfo("""{"apiVersion":"1.5","method":"getNTPInfo","data":{"client":{"enabled":true}}}"""));
    }

    [Fact]
    public void TheRequestAsksTheDevicesVersion()
    {
        Assert.Equal("""{"apiVersion":"1.5","context":"oadm","method":"getNTPInfo"}""", ClockHealth.NtpInfoRequest("1.5"));
    }
}
