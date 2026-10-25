using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Protocol.Text;
using Oadm.Plugins.Radius.Tests.Support;
using Org.BouncyCastle.Tls;
using Xunit.Abstractions;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// Scale (docs/spec/radius-server.md "Limits and load": 30,000 devices after a power cut, the session table at 10,000):
/// many full handshakes in parallel on one processor, the session table at its limit with the timeout sweep, the duplicate
/// cache at capacity, 30,000 MAC addresses. Budgets about 10x the measured time; the times go to the test output.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PerfTests(ITestOutputHelper output)
{
    [Fact]
    public void ManyParallelHandshakesAllAuthenticate()
    {
        const int Count = 2000;
        var processor = new RadiusProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity }, new TestValidator());
        var accepted = 0;
        var watch = Stopwatch.StartNew();

        Parallel.For(0, Count, new ParallelOptions { MaxDegreeOfParallelism = 64 }, i =>
        {
            var camera = new TestSupplicant(new SupplicantOptions { Versions = i % 2 == 0 ? [ProtocolVersion.TLSv13] : [ProtocolVersion.TLSv12] });
            var radiusSwitch = new TestSwitch(processor)
            {
                CallingStationId = $"AC-CC-8E-{i >> 16 & 0xFF:X2}-{i >> 8 & 0xFF:X2}-{i & 0xFF:X2}",
                Source = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1024 + i),
                NasPortId = "Gi1/0/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), // one camera per port
            };
            var conversation = radiusSwitch.Run(camera);
            if (conversation.Final == RadiusCode.AccessAccept && camera.Msk!.AsSpan().SequenceEqual(conversation.MskFromAccept))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        watch.Stop();
        output.WriteLine($"{Count} handshakes (64 in parallel): {watch.ElapsedMilliseconds} ms, {watch.Elapsed.TotalMilliseconds / Count:F1} ms each");
        Assert.Equal(Count, accepted);
        Assert.Equal(0, processor.SessionCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TheSessionTableAtItsLimitAndTheTimeoutSweepStayFast()
    {
        var time = new FakeTimeProvider();
        var processor = new RadiusProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, MaxSessionsPerClient = 10_000 }, new TestValidator(), time);
        var client = new RadiusClient("switch-1", TestSwitch.DefaultSecret);
        var identity = EapPacket.Response(1, EapType.Identity, "axis-accc8e123456"u8.ToArray()).ToBytes();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            var request = RadiusPacketWriter.Request(RadiusCode.AccessRequest, (byte)i, System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), client.Secret.Span,
                [.. RadiusAttribute.EapMessages(identity), RadiusAttribute.Text(RadiusAttributeType.CallingStationId, $"AC-CC-8E-00-{i >> 8 & 0xFF:X2}-{i & 0xFF:X2}"), RadiusAttribute.Text(RadiusAttributeType.NasPortId, "Gi1/0/" + i)])!;
            Assert.Equal(RadiusOutcome.Answered, processor.Process(request, client, new IPEndPoint(IPAddress.Loopback, 1024 + (i % 60_000))).Outcome);
        }

        var opened = watch.ElapsedMilliseconds;
        Assert.Equal(10_000, processor.SessionCount);
        var refused = RadiusPacketWriter.Request(RadiusCode.AccessRequest, 1, new byte[16], client.Secret.Span, [.. RadiusAttribute.EapMessages(identity)])!;
        Assert.Equal(RadiusDropReason.TooManySessions, processor.Process(refused, client, new IPEndPoint(IPAddress.Loopback, 9)).Drop);

        watch.Restart();
        Assert.Empty(processor.ExpireSessions());
        var emptySweep = watch.ElapsedMilliseconds;
        time.Advance(TimeSpan.FromSeconds(31));
        watch.Restart();
        Assert.Equal(10_000, processor.ExpireSessions().Count);
        var sweep = watch.ElapsedMilliseconds;

        output.WriteLine($"10,000 sessions opened: {opened} ms; sweep without timeouts: {emptySweep} ms; sweep of 10,000 timeouts: {sweep} ms");
        Assert.Equal(0, processor.SessionCount);
        Assert.True(opened < 5_000, $"{opened} ms");
        Assert.True(emptySweep < 500, $"{emptySweep} ms");
        Assert.True(sweep < 1_000, $"{sweep} ms");
    }

    [Fact]
    public void TheDuplicateCacheAtCapacityStaysFast()
    {
        var cache = new RadiusDuplicateCache(new FakeTimeProvider());
        var authenticator = new byte[16];
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 200_000; i++)
        {
            var source = new IPEndPoint(IPAddress.Loopback, 1024 + (i % 60_000));
            cache.Begin("switch-" + (i % 50), source, (byte)i, authenticator);
            cache.Complete("switch-" + (i % 50), source, (byte)i, authenticator, [1]);
        }

        watch.Stop();
        output.WriteLine($"200,000 requests through the cache at 20,000 entries: {watch.ElapsedMilliseconds} ms");
        Assert.Equal(RadiusDuplicateCache.DefaultCapacity, cache.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ThirtyThousandMacAddressesNormalizeQuickly()
    {
        var formats = new Func<int, string>[]
        {
            i => $"AC-CC-8E-{i >> 16 & 0xFF:X2}-{i >> 8 & 0xFF:X2}-{i & 0xFF:X2}",
            i => $"accc.8e{i >> 16 & 0xFF:x2}.{i >> 8 & 0xFF:x2}{i & 0xFF:x2}",
            i => $"ACCC8E{i:X6}",
        };
        var watch = Stopwatch.StartNew();
        var valid = 0;
        for (var i = 0; i < 30_000; i++)
        {
            if (MacAddress.Parse(formats[i % 3](i)).IsValid)
            {
                valid++;
            }
        }

        watch.Stop();
        output.WriteLine($"30,000 MAC addresses: {watch.ElapsedMilliseconds} ms");
        Assert.Equal(30_000, valid);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.ElapsedMilliseconds} ms");
    }
}
