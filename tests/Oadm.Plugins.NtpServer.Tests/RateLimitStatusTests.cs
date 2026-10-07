using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Status;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.NtpServer.Tests;

public sealed class RateLimiterTests
{
    private static readonly UInt128 A = ClientAddress.FromIPAddress(IPAddress.Parse("10.0.0.48"));
    private static readonly UInt128 B = ClientAddress.FromIPAddress(IPAddress.Parse("10.0.0.49"));

    [Fact]
    public void Burst_of_8_then_drop_with_one_kiss_and_one_log_entry_per_minute()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new NtpRateLimiter(new RateLimitOptions(), time);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(RateDecision.Allow, limiter.Check(A).Decision);
        }

        var first = limiter.Check(A);
        Assert.Equal(new RateResult(RateDecision.DropWithKiss, Log: true), first);
        Assert.Equal(new RateResult(RateDecision.Drop, Log: false), limiter.Check(A));
        Assert.Equal(RateDecision.Allow, limiter.Check(B).Decision); // other clients are not affected

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(RateDecision.Allow, limiter.Check(A).Decision); // refilled meanwhile
    }

    [Fact]
    public void Refill_is_one_request_per_2_seconds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new NtpRateLimiter(new RateLimitOptions(), time);
        for (var i = 0; i < 8; i++)
        {
            limiter.Check(A);
        }

        Assert.NotEqual(RateDecision.Allow, limiter.Check(A).Decision);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(RateDecision.Allow, limiter.Check(A).Decision);
        time.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Equal(RateDecision.Allow, limiter.Check(A).Decision);
        Assert.NotEqual(RateDecision.Allow, limiter.Check(A).Decision);

        // A camera polling every 64 s is never limited.
        for (var i = 0; i < 100; i++)
        {
            time.Advance(TimeSpan.FromSeconds(64));
            Assert.Equal(RateDecision.Allow, limiter.Check(B).Decision);
        }
    }

    [Fact]
    public void Client_table_is_an_LRU_with_a_cap_and_idle_expiry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new NtpRateLimiter(new RateLimitOptions { MaxClients = 3, GlobalPerSecond = 1_000_000 }, time);
        for (var i = 0; i < 1000; i++)
        {
            limiter.Check(ClientAddress.FromIPAddress(new IPAddress(i + 1)));
        }

        Assert.Equal(3, limiter.ClientCount);

        // A exhausts its bucket, three others push it out, so it starts again with a full bucket.
        for (var i = 0; i < 9; i++)
        {
            limiter.Check(A);
        }

        Assert.NotEqual(RateDecision.Allow, limiter.Check(A).Decision);
        for (var i = 0; i < 3; i++)
        {
            limiter.Check(ClientAddress.FromIPAddress(new IPAddress(5000 + i)));
        }

        Assert.Equal(RateDecision.Allow, limiter.Check(A).Decision);

        time.Advance(TimeSpan.FromMinutes(11));
        limiter.Check(B);
        Assert.Equal(1, limiter.ClientCount); // the idle ones expired
    }

    [Fact]
    public void Global_cap_drops_everything_above_it_and_reports_it()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new NtpRateLimiter(new RateLimitOptions { GlobalPerSecond = 2_000 }, time);
        var allowed = 0;
        for (var i = 0; i < 2_500; i++)
        {
            if (limiter.Check(ClientAddress.FromIPAddress(new IPAddress(i + 1))).Decision == RateDecision.Allow)
            {
                allowed++;
            }
        }

        Assert.Equal(2_000, allowed);
        Assert.True(limiter.GloballyLimitedWithin(TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(limiter.GloballyLimitedWithin(TimeSpan.FromSeconds(10)));
        Assert.Equal(RateDecision.Allow, limiter.Check(A).Decision); // refilled
    }

    [Fact]
    public void Client_keys_round_trip_for_IPv4_and_IPv6()
    {
        Assert.Equal(IPAddress.Parse("10.0.0.48"), ClientAddress.ToIPAddress(A));
        var v6 = IPAddress.Parse("2001:db8::48");
        Assert.Equal(v6, ClientAddress.ToIPAddress(ClientAddress.FromIPAddress(v6)));

        var socketAddress = new IPEndPoint(IPAddress.Parse("10.0.0.48"), 50123).Serialize();
        Assert.True(ClientAddress.TryGetKey(socketAddress, out var key));
        Assert.Equal(A, key);
        var socketAddress6 = new IPEndPoint(v6, 123).Serialize();
        Assert.True(ClientAddress.TryGetKey(socketAddress6, out var key6));
        Assert.Equal(v6, ClientAddress.ToIPAddress(key6));
    }

    [Fact]
    public void Request_log_keeps_the_last_40_newest_first()
    {
        var log = new RequestLog();
        var start = new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 100; i++)
        {
            log.Add(start.AddSeconds(i), A, i, i % 10 == 0 ? RequestResult.RateLimited : RequestResult.Answered);
        }

        var entries = log.Snapshot();
        Assert.Equal(40, entries.Count);
        Assert.Equal(100, entries[0].Seq);
        Assert.Equal(61, entries[^1].Seq);
        Assert.Equal("10.0.0.48", entries[0].Client);
        Assert.Equal(RequestEntry.RateLimited, log.Snapshot().Single(e => e.Seq == 91).Result);
        Assert.Equal(3, log.Snapshot(afterSeq: 97).Count);
        Assert.Equal("+2 ms", RequestLog.FormatOffset(2.4));
        Assert.Equal("-3.2 s", RequestLog.FormatOffset(-3200));
        Assert.Equal("-", RequestLog.FormatOffset(null));
        Assert.Equal("0 ms", RequestLog.FormatOffset(-0.3));
    }
}

public sealed class StatusMappingTests
{
    [Fact]
    public void Address_in_use_on_Windows_names_the_Windows_Time_service_when_it_runs()
    {
        var withW32 = NtpStatusTexts.ForBindError(SocketError.AddressAlreadyInUse, 123, "Ethernet", HostOs.Windows, windowsTimeRunning: true);
        Assert.Equal(NtpStatusInfo.Error, withW32.Kind);
        Assert.Equal("Port 123 is in use by another program (Windows Time service)", withW32.Text);
        Assert.Contains("net stop w32time", withW32.Detail, StringComparison.Ordinal);

        var other = NtpStatusTexts.ForBindError(SocketError.AddressAlreadyInUse, 123, "Ethernet", HostOs.Windows);
        Assert.Equal("Port 123 is in use by another program", other.Text);
    }

    [Fact]
    public void Access_denied_on_Windows_means_in_use_because_Windows_has_no_privileged_ports()
    {
        var status = NtpStatusTexts.ForBindError(SocketError.AccessDenied, 123, "Ethernet", HostOs.Windows, windowsTimeRunning: true);
        Assert.Equal("Port 123 is in use by another program (Windows Time service)", status.Text);
    }

    [Fact]
    public void Access_denied_on_Linux_and_macOS_is_a_permission_problem_with_the_fix()
    {
        var linux = NtpStatusTexts.ForBindError(SocketError.AccessDenied, 123, "eth0", HostOs.Linux);
        Assert.Equal(NtpStatusInfo.Error, linux.Kind);
        Assert.Equal("Insufficient permission to use port 123", linux.Text);
        Assert.Contains("setcap 'cap_net_bind_service=+ep'", linux.Detail, StringComparison.Ordinal);

        var mac = NtpStatusTexts.ForBindError(SocketError.AccessDenied, 123, "en0", HostOs.MacOs);
        Assert.Equal("Insufficient permission to use port 123", mac.Text);
        Assert.Contains("sudo", mac.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HostOs.Linux, "ss -ulpn")]
    [InlineData(HostOs.MacOs, "lsof")]
    public void Address_in_use_on_Linux_and_macOS(HostOs os, string hint)
    {
        var status = NtpStatusTexts.ForBindError(SocketError.AddressAlreadyInUse, 123, "eth0", os);
        Assert.Equal("Port 123 is in use by another program", status.Text);
        Assert.Contains(hint, status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HostOs.Windows)]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOs)]
    public void Address_not_available_means_the_interface_is_gone(HostOs os)
    {
        var status = NtpStatusTexts.ForBindError(SocketError.AddressNotAvailable, 123, "Ethernet", os);
        Assert.Equal(NtpStatusInfo.Error, status.Kind);
        Assert.Equal("Interface Ethernet is not available", status.Text);
    }

    [Fact]
    public void Running_stopped_and_warning_texts()
    {
        Assert.Equal("Running on 10.0.0.17:123", NtpStatusTexts.Running([new IPEndPoint(IPAddress.Parse("10.0.0.17"), 123)], false).Text);
        Assert.Equal("Running on all interfaces, port 123", NtpStatusTexts.Running([new IPEndPoint(IPAddress.Any, 123), new IPEndPoint(IPAddress.IPv6Any, 123)], true).Text);
        Assert.Equal(new NtpStatusInfo(NtpStatusInfo.Neutral, "Stopped"), NtpStatusTexts.Stopped);
        Assert.Equal("Upstream pool.ntp.org not reachable, serving the server clock", NtpStatusTexts.UpstreamNotReachable("pool.ntp.org", "No answer within 2 s").Text);
        Assert.Equal("Server clock differs from upstream by 3.2 s", NtpStatusTexts.ClockDiffers(-3.2).Text);
        Assert.Equal("Too many requests, dropping", NtpStatusTexts.TooManyRequests(2000).Text);
        Assert.Equal(NtpStatusInfo.Warning, NtpStatusTexts.ClockDiffers(2).Kind);
    }
}

public sealed class InterfaceListingTests
{
    [Fact]
    public void System_listing_works_on_this_OS()
    {
        var list = SystemNetworkInterfaces.Instance.List();
        Assert.All(list, n => Assert.False(string.IsNullOrEmpty(n.Id)));
        Assert.Contains(list, n => n.IsLoopback || n.Addresses.Count > 0);
    }

    [Fact]
    public async Task Select_offers_all_interfaces_first_then_up_interfaces_with_addresses()
    {
        await using var service = new NtpServerService(Options.Test() with { IncludeLoopback = false });
        var options = service.ListInterfaces();

        Assert.Equal(["all", "eth-id"], options.Select(o => o.Id));
        Assert.Equal("All interfaces", options[0].Label);
        Assert.Equal("Ethernet - 192.0.2.17 (Intel(R) Ethernet Connection I219-LM)", options[1].Label);
        Assert.Equal(["192.0.2.17", "fe80::1"], options[1].Addresses);
    }

    [Fact]
    public void Primary_address_prefers_IPv4()
    {
        var nic = new ServerNetworkInterface("x", "x", "x", [IPAddress.Parse("fe80::1"), IPAddress.Parse("2001:db8::1"), IPAddress.Parse("10.0.0.1")], true, false);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), nic.PrimaryAddress);
        var v6 = nic with { Addresses = [IPAddress.Parse("fe80::1"), IPAddress.Parse("2001:db8::1")] };
        Assert.Equal(IPAddress.Parse("2001:db8::1"), v6.PrimaryAddress);
    }
}
