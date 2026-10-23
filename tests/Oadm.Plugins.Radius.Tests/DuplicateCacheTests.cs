using System.Net;
using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.Radius.Protocol.Radius;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>The duplicate cache (RFC 5080 2.2.2; docs/spec/radius-server.md "Duplicates": 4,096 answers, 30 s each).</summary>
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
    public void AtMost4096EntriesTheOldestGoFirst()
    {
        var cache = new RadiusDuplicateCache(new FakeTimeProvider());
        Assert.Equal(4096, cache.Capacity);
        for (var i = 0; i < 4096 + 10; i++)
        {
            var source = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000 + i);
            cache.Begin("sw", source, 1, AuthenticatorA);
            cache.Complete("sw", source, 1, AuthenticatorA, [1]);
        }

        Assert.Equal(4096, cache.Count);
        Assert.Equal(DuplicateState.New, cache.Begin("sw", new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000), 1, AuthenticatorA).State);
        Assert.Equal(DuplicateState.Answered, cache.Begin("sw", new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1000 + 4105), 1, AuthenticatorA).State);
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
