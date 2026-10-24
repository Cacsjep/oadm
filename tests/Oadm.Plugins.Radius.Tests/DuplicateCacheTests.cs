using System.Net;
using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.Radius.Protocol.Radius;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>The duplicate cache (RFC 5080 2.2.2; docs/spec/radius-server.md "Duplicates": 20,000 answers or 32 MB, 30 s each, the final answers apart).</summary>
public sealed class DuplicateCacheTests
{
    private static readonly IPEndPoint Source = new(IPAddress.Parse("10.0.0.2"), 1645);
    private static readonly byte[] AuthenticatorA = [.. Enumerable.Repeat((byte)1, 16)];
    private static readonly byte[] AuthenticatorB = [.. Enumerable.Repeat((byte)2, 16)];

    [Fact]
    public void ARetransmissionGetsTheCachedAnswerAndOneInWorkIsDropped()
    {
        var cache = new RadiusDuplicateCache(new FakeTimeProvider());

        Assert.Equal(DuplicateState.New, cache.Begin("sw", Source, 5, AuthenticatorA).State);
        Assert.Equal(DuplicateState.InProgress, cache.Begin("sw", Source, 5, AuthenticatorA).State);

        cache.Complete("sw", Source, 5, AuthenticatorA, [1, 2, 3]);
        var again = cache.Begin("sw", Source, 5, AuthenticatorA);
        Assert.Equal(DuplicateState.Answered, again.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, again.Response);
    }

    [Fact]
    public void TheKeyIsSwitchAddressPortAndIdentifier()
    {
        var cache = new RadiusDuplicateCache(new FakeTimeProvider());
        cache.Begin("sw", Source, 5, AuthenticatorA);
        cache.Complete("sw", Source, 5, AuthenticatorA, [1]);

        Assert.Equal(DuplicateState.New, cache.Begin("other", Source, 5, AuthenticatorA).State);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", new IPEndPoint(Source.Address, 1646), 5, AuthenticatorA).State);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", new IPEndPoint(IPAddress.Parse("10.0.0.3"), 1645), 5, AuthenticatorA).State);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", Source, 6, AuthenticatorA).State);
        Assert.Equal(DuplicateState.Answered, cache.Begin("sw", Source, 5, AuthenticatorA).State);
    }

    [Fact]
    public void ANewAuthenticatorWithTheSameIdentifierReplacesTheEntry()
    {
        // RFC 5080 2.2.2: "Cache entries MUST also be purged if ... the Request Authenticator field is different".
        var cache = new RadiusDuplicateCache(new FakeTimeProvider());
        cache.Begin("sw", Source, 5, AuthenticatorA);
        cache.Complete("sw", Source, 5, AuthenticatorA, [1]);

        Assert.Equal(DuplicateState.New, cache.Begin("sw", Source, 5, AuthenticatorB).State);
        Assert.Equal(1, cache.Count);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", Source, 5, AuthenticatorA).State);
    }

    [Fact]
    public void EntriesExpireAfter30Seconds()
    {
        var time = new FakeTimeProvider();
        var cache = new RadiusDuplicateCache(time);
        cache.Begin("sw", Source, 5, AuthenticatorA);
        cache.Complete("sw", Source, 5, AuthenticatorA, [1]);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(DuplicateState.Answered, cache.Begin("sw", Source, 5, AuthenticatorA).State);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(DuplicateState.New, cache.Begin("sw", Source, 5, AuthenticatorA).State);
    }

    [Fact]
    public void AtMostTheCapacityTheOldestAnsweredGoFirst()
    {
        // docs/spec "Duplicates": 20,000 answers (twice the open sessions allowed), so the cache keeps up with 30,000 devices.
        Assert.Equal(20_000, new RadiusDuplicateCache(new FakeTimeProvider()).Capacity);
        var cache = new RadiusDuplicateCache(new FakeTimeProvider(), capacity: 100);
        for (var i = 0; i < 110; i++)
        {
            var source = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000 + i);
            cache.Begin("sw", source, 1, AuthenticatorA);
            cache.Complete("sw", source, 1, AuthenticatorA, [1]);
        }

        Assert.Equal(100, cache.Count);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000), 1, AuthenticatorA).State);
        Assert.Equal(DuplicateState.Answered, cache.Begin("sw", new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000 + 109), 1, AuthenticatorA).State);
    }

    [Fact]
    public void ARequestInWorkIsNeverEvicted()
    {
        // RFC 5080 2.2.2: a retransmission of a request still in work is discarded, never processed a second time.
        var cache = new RadiusDuplicateCache(new FakeTimeProvider(), capacity: 10);
        var inWork = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 1);
        cache.Begin("sw", inWork, 1, AuthenticatorA);
        for (var i = 0; i < 50; i++)
        {
            var source = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000 + i);
            cache.Begin("sw", source, 1, AuthenticatorA);
            cache.Complete("sw", source, 1, AuthenticatorA, [1]);
        }

        Assert.Equal(DuplicateState.InProgress, cache.Begin("sw", inWork, 1, AuthenticatorA).State);
    }

    [Fact]
    public void TheAnswersStayWithinTheirBytes()
    {
        var cache = new RadiusDuplicateCache(new FakeTimeProvider(), capacity: 1000, maxBytes: 4096);
        for (var i = 0; i < 10; i++)
        {
            var source = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000 + i);
            cache.Begin("sw", source, 1, AuthenticatorA);
            cache.Complete("sw", source, 1, AuthenticatorA, new byte[1000]);
        }

        Assert.Equal(4, cache.Count); // 4 x 1,000 bytes fit, the oldest went
        Assert.Equal(DuplicateState.Answered, cache.Begin("sw", new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1009), 1, AuthenticatorA).State);
    }

    [Fact]
    public void TheFinalAnswerOfASessionIsKeptByStateFor30Seconds()
    {
        // RFC 5080 2.2.2: the retransmission of the request that ended a session gets the same answer.
        var time = new FakeTimeProvider();
        var cache = new RadiusDuplicateCache(time);
        byte[] state = [7, 7, 7];
        cache.RememberFinal("sw", state, AuthenticatorA, [9, 9]);

        Assert.Equal(new byte[] { 9, 9 }, cache.FindFinal("sw", state, AuthenticatorA));
        Assert.Null(cache.FindFinal("sw", state, AuthenticatorB)); // another request with that State: not a retransmission
        Assert.Null(cache.FindFinal("other", state, AuthenticatorA));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(cache.FindFinal("sw", state, AuthenticatorA));

        cache.RememberFinal("sw", state, AuthenticatorA, [9]);
        cache.Forget("sw");
        Assert.Equal(0, cache.FinalCount);
    }

    [Fact]
    public void ADroppedRequestIsForgottenSoItsRetransmissionIsProcessed()
    {
        var cache = new RadiusDuplicateCache(new FakeTimeProvider());
        cache.Begin("sw", Source, 5, AuthenticatorA);
        cache.Complete("sw", Source, 5, AuthenticatorA, null);
        Assert.Equal(0, cache.Count);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", Source, 5, AuthenticatorA).State);
    }

    [Fact]
    public void ARemovedSwitchIsForgotten()
    {
        var cache = new RadiusDuplicateCache(new FakeTimeProvider());
        cache.Begin("sw", Source, 5, AuthenticatorA);
        cache.Begin("other", Source, 5, AuthenticatorA);

        cache.Forget("sw");

        Assert.Equal(1, cache.Count);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", Source, 5, AuthenticatorA).State);
    }
}
