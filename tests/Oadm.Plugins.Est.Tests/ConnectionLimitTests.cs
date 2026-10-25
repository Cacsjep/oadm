using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Est.Serving;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>The connection slots and the waiting times of connections that show no client certificate (docs/spec/est-server.md, "Limits").</summary>
public sealed class ConnectionLimitTests
{
    [Fact]
    public void Ipv6_addresses_of_one_64_network_share_the_slots_of_one_address()
    {
        var slots = new EstConnectionSlots();
        for (var i = 1; i <= EstConnectionSlots.MaxPerAddress; i++)
        {
            Assert.NotNull(slots.TryEnter(null, IPAddress.Parse($"2001:db8:1:2::{i:x}")));
        }

        Assert.Null(slots.TryEnter(null, IPAddress.Parse("2001:db8:1:2:ffff::99")));
        Assert.NotNull(slots.TryEnter(null, IPAddress.Parse("2001:db8:1:3::1"))); // the next /64 is another host
        Assert.NotNull(slots.TryEnter(Guid.NewGuid(), IPAddress.Parse("2001:db8:1:2::1"))); // the same address at a site is another camera
    }

    [Fact]
    public void An_ipv4_address_mapped_into_ipv6_counts_as_the_ipv4_address()
    {
        var slots = new EstConnectionSlots();
        for (var i = 0; i < EstConnectionSlots.MaxPerAddress; i++)
        {
            Assert.NotNull(slots.TryEnter(null, IPAddress.Parse("10.0.0.48")));
        }

        Assert.Null(slots.TryEnter(null, IPAddress.Parse("::ffff:10.0.0.48")));
        Assert.NotNull(slots.TryEnter(null, IPAddress.Parse("10.0.0.49")));
    }

    [Fact]
    public void Connections_without_a_certificate_get_fewer_slots_and_a_camera_that_shows_one_frees_its_slot_for_others()
    {
        var slots = new EstConnectionSlots();
        var held = new List<EstConnectionSlots.Slot>();
        for (var i = 0; i < EstConnectionSlots.MaxWithoutCertificate; i++)
        {
            held.Add(slots.TryEnter(null, IPAddress.Parse($"10.0.1.{i + 1}"))!);
        }

        Assert.Null(slots.TryEnter(null, IPAddress.Parse("10.0.2.1"))); // full without a certificate, far below the total

        held[0].Identified();
        Assert.Equal((EstConnectionSlots.MaxWithoutCertificate, EstConnectionSlots.MaxWithoutCertificate - 1), (slots.Open, slots.WithoutCertificate));
        var next = slots.TryEnter(null, IPAddress.Parse("10.0.2.1"));
        Assert.NotNull(next);

        held[0].Dispose();
        held[0].Dispose(); // given back once
        next!.Dispose();
        Assert.Equal((EstConnectionSlots.MaxWithoutCertificate - 1, EstConnectionSlots.MaxWithoutCertificate - 1), (slots.Open, slots.WithoutCertificate));
    }

    [Fact]
    public void The_total_counts_cameras_with_a_certificate_too()
    {
        var slots = new EstConnectionSlots();
        for (var i = 0; i < EstConnectionSlots.MaxConnections; i++)
        {
            slots.TryEnter(null, IPAddress.Parse($"10.0.{i / 200}.{(i % 200) + 1}"))!.Identified();
        }

        Assert.Null(slots.TryEnter(null, IPAddress.Parse("10.0.9.1")));
        Assert.Equal((EstConnectionSlots.MaxConnections, 0), (slots.Open, slots.WithoutCertificate));
    }

    [Fact]
    public async Task A_connection_that_never_starts_its_handshake_is_closed_after_the_handshake_time()
    {
        var clock = new TimerClock(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var ca = new FakeAuthority();
        var policies = await TestPolicies.EstStoreAsync();
        await using var est = new EstService(new EstHost(new FakeSettings(), new ServerTests.Devices(), new FakePluginNetwork(), () => ca, () => true)
        {
            Time = clock,
            LocalAddresses = () => ["10.0.0.17"],
            Policies = () => policies,
        });
        await est.StartAsync(CancellationToken.None);
        var relay = (InMemoryTcpRelay)est.Listener!;

        var device = relay.Connect(new IPEndPoint(IPAddress.Parse("10.0.0.50"), 40000));
        var read = device.ReadAsync(new byte[1]).AsTask();

        // The server waits for the handshake with its own, shorter time.
        await Eventually.TrueAsync(() => clock.Due.Contains(EstService.HandshakeTimeout), "the handshake waits");
        Assert.False(read.IsCompleted);
        clock.Fake.Advance(EstService.HandshakeTimeout);

        Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(30)));
        ca.Dispose();
    }

    /// <summary>A fake clock that records the due time of every timer (a timeout started).</summary>
    private sealed class TimerClock(FakeTimeProvider fake) : TimeProvider
    {
        private readonly ConcurrentBag<TimeSpan> _due = [];

        public FakeTimeProvider Fake { get; } = fake;

        public IReadOnlyCollection<TimeSpan> Due => _due;

        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();

        public override long GetTimestamp() => Fake.GetTimestamp();

        public override long TimestampFrequency => Fake.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _due.Add(dueTime);
            return Fake.CreateTimer(callback, state, dueTime, period);
        }
    }
}
