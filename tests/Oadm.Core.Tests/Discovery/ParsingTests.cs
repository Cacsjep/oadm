using System.Net;

using Oadm.Core.Discovery;

namespace Oadm.Core.Tests.Discovery;

public class AxisRealmTests
{
    [Theory]
    // Observed on AXIS OS 10.12 (HTTP and HTTPS) and 12.11 (HTTP).
    [InlineData("Digest realm=\"AXIS_B8A44F3B34BB\", nonce=\"LHeDrjxdBgA=d1c8\", algorithm=MD5, qop=\"auth\"", "B8A44F3B34BB")]
    // Observed on AXIS OS 12.11 over HTTPS.
    [InlineData("Basic realm=\"AXIS_B8A44F631339\"", "B8A44F631339")]
    [InlineData("Digest realm=\"axis_accc8e0a1b2c\"", "ACCC8E0A1B2C")]
    [InlineData("Digest realm=AXIS_ACCC8E0A1B2C, qop=auth", "ACCC8E0A1B2C")]
    [InlineData("Digest nonce=\"x\", REALM = \"AXIS_ACCC8E0A1B2C\"", "ACCC8E0A1B2C")]
    public void ExtractsSerial(string challenge, string expected)
        => Assert.Equal(expected, AxisRealm.TryGetSerial(challenge));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Digest realm=\"Hikvision\"")]
    [InlineData("Basic realm=\"AXIS\"")]
    [InlineData("Digest realm=\"AXIS_ACCC8E0A1B\"")]
    [InlineData("Digest realm=\"AXIS_ACCC8E0A1B2C3D\"")]
    [InlineData("Digest realm=\"AXIS_ACCC8E0A1BZZ\"")]
    [InlineData("Digest realm=\"NOTAXIS\", opaque=\"AXIS_ACCC8E0A1B2C\"")]
    public void RejectsNonAxisChallenges(string? challenge)
        => Assert.Null(AxisRealm.TryGetSerial(challenge));

    [Fact]
    public void UsesFirstAxisChallengeOfSeveral()
        => Assert.Equal(
            "ACCC8E0A1B2C",
            AxisRealm.TryGetSerial(["Negotiate", "Basic realm=\"AXIS_ACCC8E0A1B2C\"", "Digest realm=\"AXIS_000000000000\""]));
}

public class SerialNumberTests
{
    [Theory]
    [InlineData("b8:a4:4f:63:13:39", "B8A44F631339")]
    [InlineData("B8-A4-4F-63-13-39", "B8A44F631339")]
    [InlineData("b8a4.4f63.1339", "B8A44F631339")]
    [InlineData(" B8A44F631339 ", "B8A44F631339")]
    [InlineData("B8A44F63133", null)]
    [InlineData("B8A44F6313399", null)]
    [InlineData("G8A44F631339", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize(string? input, string? expected) => Assert.Equal(expected, SerialNumber.Normalize(input));

    [Theory]
    [InlineData("AXIS P3265-V - B8A44F631339", "B8A44F631339")]
    [InlineData("axis-b8a44f631339", "B8A44F631339")]
    [InlineData("My camera", null)]
    [InlineData("AB8A44F631339", null)]
    [InlineData("AXIS - B8A44F631339 (2)", null)]
    public void FromName(string input, string? expected) => Assert.Equal(expected, SerialNumber.FromName(input));
}

public class Ipv4RangeTests
{
    [Fact]
    public void EnumeratesInclusiveRangeAcrossOctetBoundary()
    {
        var range = Ipv4Range.Create(IPAddress.Parse("10.0.0.254"), IPAddress.Parse("10.0.1.2"));

        Assert.Equal(5, range.Count);
        Assert.Equal(["10.0.0.254", "10.0.0.255", "10.0.1.0", "10.0.1.1", "10.0.1.2"], range.Select(a => a.ToString()));
    }

    [Fact]
    public void TypicalClassCRangeHas254Addresses()
    {
        var range = Ipv4Range.Create(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.254"));

        Assert.Equal(254, range.Count);
        Assert.Equal(254, range.ToList().Count);
        Assert.Equal("10.0.0.1 - 10.0.0.254", range.ToString());
    }

    [Fact]
    public void SingleAddressAndTopOfAddressSpace()
    {
        var single = Ipv4Range.Create(IPAddress.Parse("10.0.0.48"), IPAddress.Parse("10.0.0.48"));
        Assert.Equal(["10.0.0.48"], single.Select(a => a.ToString()));

        var top = Ipv4Range.Create(IPAddress.Parse("255.255.255.254"), IPAddress.Broadcast);
        Assert.Equal(["255.255.255.254", "255.255.255.255"], top.Select(a => a.ToString()));
    }

    [Fact]
    public void AcceptsIpv4MappedIpv6()
    {
        var range = Ipv4Range.Create(IPAddress.Parse("::ffff:10.0.0.1"), IPAddress.Parse("10.0.0.2"));

        Assert.Equal(2, range.Count);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), range.From);
    }

    [Fact]
    public void RejectsReversedOversizedAndIpv6Ranges()
    {
        Assert.Throws<ArgumentException>(() => Ipv4Range.Create(IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.0.0.1")));
        Assert.Throws<ArgumentException>(() => Ipv4Range.Create(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.1.0.0")));
        Assert.Throws<ArgumentException>(() => Ipv4Range.Create(IPAddress.Parse("fe80::1"), IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void MaxSizeIsASlash16()
        => Assert.Equal(Ipv4Range.MaxSize, Ipv4Range.Create(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.255.255")).Count);

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.254", true)]
    [InlineData(" 10.0.0.1 ", "10.0.0.1", true)]
    [InlineData("10.1", "10.0.0.254", false)]
    [InlineData("10.0.0.1", "10.0.0.256", false)]
    [InlineData("10.0.0.1", "", false)]
    [InlineData("fe80::1", "fe80::2", false)]
    [InlineData("10.0.0.9", "10.0.0.1", false)]
    public void TryParse(string from, string to, bool ok)
    {
        Assert.Equal(ok, Ipv4Range.TryParse(from, to, out var range, out var error));
        Assert.Equal(ok, range is not null);
        Assert.Equal(ok, error is null);
    }
}
