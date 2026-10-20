using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.SiteNode;
using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

namespace Oadm.Sites.Tests;

/// <summary>The attempt limits per address and what a Hello may carry (docs/sites.md, Security review).</summary>
public sealed class LimitsTests
{
    [Fact]
    public void AttemptsAreLimitedPerAddressAndInAllWithinTheWindow()
    {
        var time = new FakeTimeProvider();
        var limiter = new AddressRateLimiter(perAddress: 3, total: 5, TimeSpan.FromMinutes(1), time);
        var a = IPAddress.Parse("10.0.0.5");
        var b = IPAddress.Parse("10.0.0.6");

        Assert.True(limiter.TryAcquire(a));
        Assert.True(limiter.TryAcquire(a));
        Assert.True(limiter.TryAcquire(a));
        Assert.False(limiter.TryAcquire(a));
        Assert.True(limiter.TryAcquire(b));
        Assert.True(limiter.TryAcquire(b));
        Assert.False(limiter.TryAcquire(IPAddress.Parse("10.0.0.7"))); // the total

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(a));
    }

    [Fact]
    public void IPv6CountsPerSlash64AndMappedIPv4AsIPv4()
    {
        Assert.Equal(AddressRateLimiter.KeyOf(IPAddress.Parse("2001:db8:1:2::5")), AddressRateLimiter.KeyOf(IPAddress.Parse("2001:db8:1:2:ffff::1")));
        Assert.NotEqual(AddressRateLimiter.KeyOf(IPAddress.Parse("2001:db8:1:2::5")), AddressRateLimiter.KeyOf(IPAddress.Parse("2001:db8:1:3::5")));
        Assert.Equal("10.0.0.5", AddressRateLimiter.KeyOf(IPAddress.Parse("::ffff:10.0.0.5")));
    }

    [Fact]
    public void OldAddressesAreDroppedAndTheTableIsBounded()
    {
        var time = new FakeTimeProvider();
        var limiter = new AddressRateLimiter(perAddress: 1, total: 0, TimeSpan.FromMinutes(1), time, maxAddresses: 100);
        for (var i = 0; i < 100; i++)
            Assert.True(limiter.TryAcquire(IPAddress.Parse($"10.0.{i}.1")));

        // Full: a new address waits for the window; the table never grows past its size.
        Assert.False(limiter.TryAcquire(IPAddress.Parse("10.1.0.1")));
        Assert.Equal(100, limiter.Addresses);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("10.1.0.1")));
        Assert.Equal(1, limiter.Addresses);
    }

    [Fact]
    public void AHelloWithinTheRulesPasses()
    {
        var hello = new HelloReply { NodeId = "0123456789abcdef", HostName = "site-pc.example.com", Version = "3.7.0", SiteName = "Gebäude B", BoundServerName = "OADM-SRV" };
        hello.Features.Add(["dial", "relay.udp"]);

        Assert.Null(HelloRules.Problem(hello));
        Assert.Null(HelloRules.Problem(new HelloReply { State = NodeState.Bound, Version = "3.7.0" })); // a bound node to a stranger
    }

    [Theory]
    [InlineData("not-an-id!", "h", "1")]
    [InlineData("0123", "h", "1")]
    [InlineData("0123456789abcdef", "line\nbreak", "1")]
    [InlineData("0123456789abcdef", "bidi‮txt", "1")]
    [InlineData("0123456789abcdef", "h", "3.7.0-a-version-that-is-far-too-long")]
    [InlineData("0123456789abcdef", "h", "3.7.0é")]
    public void AHelloOutsideTheRulesIsRefused(string id, string host, string version) =>
        Assert.NotNull(HelloRules.Problem(new HelloReply { NodeId = id, HostName = host, Version = version }));

    [Fact]
    public void LongTextsAndManyFeaturesAreRefused()
    {
        Assert.NotNull(HelloRules.Problem(new HelloReply { NodeId = "0123456789abcdef", HostName = new string('a', 254) }));
        Assert.NotNull(HelloRules.Problem(new HelloReply { NodeId = "0123456789abcdef", SiteName = new string('a', 65) }));
        var many = new HelloReply { NodeId = "0123456789abcdef" };
        many.Features.Add(Enumerable.Range(0, 33).Select(i => "f" + i));
        Assert.NotNull(HelloRules.Problem(many));
        Assert.True(HelloRules.IsNodeId(Guid.NewGuid().ToString()));
    }
}
