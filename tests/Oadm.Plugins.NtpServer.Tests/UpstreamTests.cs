using System.Diagnostics;
using System.Net;

using Oadm.Plugins.NtpServer.Upstream;

namespace Oadm.Plugins.NtpServer.Tests;

public sealed class UpstreamClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    // Tests that expect an answer wait longer: a busy CI runner (macOS) took more than 300 ms for a loopback answer.
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    [Trait("Category", "Timing")] // offset accuracy depends on the round trip
    public async Task Good_answer_gives_stratum_offset_and_round_trip()
    {
        await using var upstream = new FakeUpstream { ClockOffset = TimeSpan.FromSeconds(3) };
        var sample = await UpstreamClient.QueryAsync(upstream.EndPoint, AnswerTimeout, TimeProvider.System, CancellationToken.None);

        Assert.Equal(2, sample.Stratum);
        // A loaded CI runner answers loopback slowly and unevenly; the offset error is up to half the round trip.
        Assert.InRange(sample.OffsetSeconds, 2.5, 3.5);
        Assert.InRange(sample.DelaySeconds, 0, 1);
        Assert.Equal(0.002, sample.RootDelaySeconds, 3);
        Assert.Equal(3, sample.ToState("x").Stratum);
        // Same tolerance as the offset above.
        Assert.Matches(@"^(2\.[5-9]|3\.[0-5]) s off, \d+ ms round trip$", sample.Describe());
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task No_answer_times_out_with_the_hard_timeout()
    {
        await using var upstream = new FakeUpstream(UpstreamBehavior.NoAnswer);
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamClient.QueryAsync(upstream.EndPoint, Timeout, TimeProvider.System, CancellationToken.None));
        Assert.Equal(UpstreamFailure.Timeout, ex.Failure);
        Assert.Equal("No answer within 300 ms", ex.Message);
        Assert.InRange(watch.Elapsed.TotalMilliseconds, 250, 2000);
    }

    [Fact]
    [Trait("Category", "Timing")] // round trip under 0.3 s
    public async Task Slow_answer_beyond_the_timeout_fails_and_the_late_answer_is_ignored()
    {
        await using var upstream = new FakeUpstream { Delay = TimeSpan.FromMilliseconds(600) };
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamClient.QueryAsync(upstream.EndPoint, Timeout, TimeProvider.System, CancellationToken.None));
        Assert.Equal(UpstreamFailure.Timeout, ex.Failure);

        // The next query (new socket, new origin) is answered fast; the late answer of the first never counts.
        upstream.Delay = TimeSpan.Zero;
        var sample = await UpstreamClient.QueryAsync(upstream.EndPoint, AnswerTimeout, TimeProvider.System, CancellationToken.None);
        Assert.InRange(sample.DelaySeconds, 0, 0.3);
        await Task.Delay(500); // the late answer arrives at the closed socket
        Assert.Equal(2, upstream.Requests);
    }

    [Fact]
    public async Task Wrong_origin_is_ignored()
    {
        await using var upstream = new FakeUpstream(UpstreamBehavior.WrongOrigin);
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamClient.QueryAsync(upstream.EndPoint, Timeout, TimeProvider.System, CancellationToken.None));
        Assert.Equal(UpstreamFailure.Timeout, ex.Failure);

        upstream.Behavior = UpstreamBehavior.WrongOriginThenAnswer;
        var sample = await UpstreamClient.QueryAsync(upstream.EndPoint, AnswerTimeout, TimeProvider.System, CancellationToken.None);
        Assert.Equal(2, sample.Stratum);
    }

    [Fact]
    public async Task Answer_from_another_address_or_port_is_ignored()
    {
        await using var upstream = new FakeUpstream(UpstreamBehavior.FromOtherPort);
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamClient.QueryAsync(upstream.EndPoint, Timeout, TimeProvider.System, CancellationToken.None));
        Assert.Equal(UpstreamFailure.Timeout, ex.Failure);
    }

    [Theory]
    [InlineData(UpstreamBehavior.KissRate, "RATE")]
    [InlineData(UpstreamBehavior.KissDeny, "DENY")]
    public async Task Kiss_of_death_is_rejected_with_its_code(UpstreamBehavior behavior, string code)
    {
        await using var upstream = new FakeUpstream(behavior);
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamClient.QueryAsync(upstream.EndPoint, AnswerTimeout, TimeProvider.System, CancellationToken.None));
        Assert.Equal(UpstreamFailure.KissOfDeath, ex.Failure);
        Assert.Equal(code, ex.KissCode);
        Assert.Equal(code == "RATE" ? "The server refused the request (too many requests)" : "The server refused the request (access denied)", ex.Message);
    }

    [Fact]
    public async Task Unsynchronized_upstream_is_rejected()
    {
        await using var upstream = new FakeUpstream(UpstreamBehavior.Unsynchronized);
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamClient.QueryAsync(upstream.EndPoint, AnswerTimeout, TimeProvider.System, CancellationToken.None));
        Assert.Equal(UpstreamFailure.InvalidAnswer, ex.Failure);
    }

    [Theory]
    [InlineData("pool.ntp.org", "pool.ntp.org", 123)]
    [InlineData("10.0.0.1:1123", "10.0.0.1", 1123)]
    [InlineData("[fd00::1]:124", "fd00::1", 124)]
    [InlineData("fd00::1", "fd00::1", 123)]
    [InlineData(" time.example.com ", "time.example.com", 123)]
    public void Upstream_field_parses(string text, string host, int port)
    {
        var parsed = UpstreamHost.TryParse(text, out var error);
        Assert.Null(error);
        Assert.Equal(new UpstreamHost(host, port), parsed);
    }

    [Theory]
    [InlineData("two hosts")]
    [InlineData("host:0")]
    [InlineData("host:99999")]
    [InlineData("[fd00::1")]
    [InlineData("bad_host!")]
    [InlineData("")]
    public void Upstream_field_rejects(string text)
    {
        Assert.Null(UpstreamHost.TryParse(text, out var error));
        Assert.NotNull(error);
    }
}

public sealed class ResolverTests
{
    [Fact]
    public async Task Dns_failure_is_an_upstream_error()
    {
        var resolver = new FakeResolver { Answer = _ => null };
        var caching = new CachingUpstreamResolver(resolver, Options.FastUpstream, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => caching.ResolveAsync(new UpstreamHost("time.invalid", 123), CancellationToken.None));
        Assert.Equal(UpstreamFailure.DnsFailure, ex.Failure);
        Assert.Equal("time.invalid could not be resolved", ex.Message);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Dns_hang_ends_at_the_dns_timeout_even_when_the_resolver_ignores_the_token()
    {
        var resolver = new FakeResolver { Hang = true };
        var caching = new CachingUpstreamResolver(resolver, Options.FastUpstream, TimeProvider.System);
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => caching.ResolveAsync(new UpstreamHost("slow.example", 123), CancellationToken.None));
        Assert.Equal(UpstreamFailure.DnsFailure, ex.Failure);
        Assert.Equal("slow.example could not be resolved within 300 ms", ex.Message);
        Assert.InRange(watch.Elapsed.TotalMilliseconds, 250, 2000);
    }

    [Fact]
    public async Task Dns_answers_are_cached_with_a_ttl_and_kept_when_dns_fails_later()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var resolver = new FakeResolver { Answer = _ => [IPAddress.Parse("10.0.0.1")] };
        var caching = new CachingUpstreamResolver(resolver, Options.FastUpstream with { DnsTtl = TimeSpan.FromMinutes(5) }, time);
        var host = new UpstreamHost("time.example", 123);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 123), await caching.ResolveAsync(host, CancellationToken.None));
        await caching.ResolveAsync(host, CancellationToken.None);
        Assert.Equal(1, resolver.Calls);

        time.Advance(TimeSpan.FromMinutes(6));
        resolver.Answer = _ => null;
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 123), await caching.ResolveAsync(host, CancellationToken.None));
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public async Task Literal_addresses_need_no_dns()
    {
        var resolver = new FakeResolver();
        var caching = new CachingUpstreamResolver(resolver, Options.FastUpstream, TimeProvider.System);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1123), await caching.ResolveAsync(new UpstreamHost("10.0.0.1", 1123), CancellationToken.None));
        Assert.Equal(0, resolver.Calls);
    }
}

public sealed class UpstreamMonitorTests
{
    [Fact]
    [Trait("Category", "Timing")] // 300 ms query timeout
    public async Task Good_answers_serve_stratum_plus_one_failures_fall_back_to_local_and_recovery_switches_back()
    {
        await using var upstream = new FakeUpstream();
        var host = UpstreamHost.TryParse(upstream.HostText, out _)!;
        await using var monitor = new UpstreamMonitor(host, Options.FastUpstream, new FakeResolver(), TimeProvider.System);
        monitor.Start();

        await Wait.UntilAsync(() => monitor.Snapshot.Reachable == true);
        Assert.Equal(3, monitor.ServingState!.Stratum);
        Assert.Equal(0x7F000001u, monitor.ServingState.ReferenceId);

        upstream.Behavior = UpstreamBehavior.NoAnswer;
        await Wait.UntilAsync(() => monitor.Snapshot.Reachable == false);
        Assert.Null(monitor.ServingState); // local mode
        Assert.True(monitor.Snapshot.ConsecutiveFailures >= Options.FastUpstream.FailuresBeforeUnreachable);
        Assert.Equal("No answer within 300 ms", monitor.Snapshot.LastError);

        upstream.Behavior = UpstreamBehavior.Answer;
        await Wait.UntilAsync(() => monitor.Snapshot.Reachable == true);
        Assert.Equal(3, monitor.ServingState!.Stratum);
    }

    [Fact]
    [Trait("Category", "Timing")] // 300 ms query timeout
    public async Task Two_failures_keep_serving_the_last_good_upstream_state()
    {
        await using var upstream = new FakeUpstream();
        var host = UpstreamHost.TryParse(upstream.HostText, out _)!;
        var options = Options.FastUpstream with { FailuresBeforeUnreachable = 100, FirstRetry = TimeSpan.FromMilliseconds(10) };
        await using var monitor = new UpstreamMonitor(host, options, new FakeResolver(), TimeProvider.System);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot.Reachable == true);

        upstream.Behavior = UpstreamBehavior.NoAnswer;
        await Wait.UntilAsync(() => monitor.Snapshot.ConsecutiveFailures >= 2);
        Assert.True(monitor.Snapshot.Reachable);
        Assert.NotNull(monitor.ServingState);
    }

    [Fact]
    [Trait("Category", "Timing")] // counts requests after a real 1 s wait
    public async Task Kiss_of_death_rate_backs_off_to_the_poll_interval()
    {
        await using var upstream = new FakeUpstream(UpstreamBehavior.KissRate);
        var host = UpstreamHost.TryParse(upstream.HostText, out _)!;
        // The upstream always answers (RATE): a 3 s query timeout, so a slow runner never sees a timeout instead of RATE.
        var options = Options.FastUpstream with { MinPoll = TimeSpan.FromSeconds(1), MaxPoll = TimeSpan.FromSeconds(4), QueryTimeout = TimeSpan.FromSeconds(3) };
        await using var monitor = new UpstreamMonitor(host, options, new FakeResolver(), TimeProvider.System);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot.LastError is not null);
        await Task.Delay(1000);

        // A plain failure would retry after 20, 40, 80 ... ms; RATE waits the (doubled) poll interval.
        Assert.Equal(1, upstream.Requests);
        Assert.Contains("too many requests", monitor.Snapshot.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dns_failure_counts_as_a_failure_and_retries_with_backoff()
    {
        var resolver = new FakeResolver { Answer = _ => null };
        await using var monitor = new UpstreamMonitor(new UpstreamHost("time.invalid", 123), Options.FastUpstream, resolver, TimeProvider.System);
        monitor.Start();
        await Wait.UntilAsync(() => monitor.Snapshot.Reachable == false);
        Assert.Equal("time.invalid could not be resolved", monitor.Snapshot.LastError);
        Assert.True(resolver.Calls >= 3);
    }

    [Fact]
    public async Task Seeded_monitor_serves_the_validation_answer_at_once()
    {
        await using var upstream = new FakeUpstream();
        var host = UpstreamHost.TryParse(upstream.HostText, out _)!;
        // The seed query must get its answer: 3 s, not the 300 ms of FastUpstream (too short on a busy CI runner).
        var seed = await UpstreamMonitor.QueryOnceAsync(host, Options.FastUpstream with { QueryTimeout = TimeSpan.FromSeconds(3) }, new FakeResolver(), TimeProvider.System, CancellationToken.None);
        await using var monitor = new UpstreamMonitor(host, Options.FastUpstream with { MinPoll = TimeSpan.FromMinutes(5) }, new FakeResolver(), TimeProvider.System);
        monitor.Start(seed);

        Assert.True(monitor.Snapshot.Reachable);
        Assert.Equal(3, monitor.ServingState!.Stratum);
        Assert.Equal(1, upstream.Requests); // no second query before the poll interval
    }
}
