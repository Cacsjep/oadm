using Oadm.Sdk.Network;

namespace Oadm.Plugins.NtpServer.Serving;

/// <summary>Limits of <see cref="NtpRateLimiter"/>.</summary>
public sealed record RateLimitOptions
{
    /// <summary>Requests a client may send back to back.</summary>
    public int Burst { get; init; } = 8;

    /// <summary>One request token is added every this many seconds (1 request / 2 s).</summary>
    public double RefillSeconds { get; init; } = 2;

    /// <summary>Most clients tracked; the least recently seen one is forgotten first.</summary>
    public int MaxClients { get; init; } = 10_000;

    /// <summary>Clients not seen for this long are forgotten.</summary>
    public TimeSpan IdleExpiry { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Requests per second over all clients; beyond that everything is dropped.</summary>
    public int GlobalPerSecond { get; init; } = 2_000;

    /// <summary>At most one Kiss-o'-Death "RATE" per client in this time.</summary>
    public TimeSpan KissInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>At most one "Rate limited" log entry per client in this time.</summary>
    public TimeSpan LimitedLogInterval { get; init; } = TimeSpan.FromMinutes(1);
}

public enum RateDecision
{
    /// <summary>Answer.</summary>
    Allow = 0,

    /// <summary>Over the client's limit: drop silently.</summary>
    Drop = 1,

    /// <summary>Over the client's limit: drop, but send one Kiss-o'-Death "RATE".</summary>
    DropWithKiss = 2,

    /// <summary>Over the global limit: drop.</summary>
    GlobalDrop = 3,
}

/// <param name="Decision">What to do with the request.</param>
/// <param name="Log">Write a "Rate limited" entry (rate limited itself: once per client per minute).</param>
public readonly record struct RateResult(RateDecision Decision, bool Log);

/// <summary>
/// The NTP view of the shared <see cref="KeyedRateLimiter{TKey}"/> (key = client address): token bucket per client in a
/// bounded LRU table plus one global bucket; notice 0 = Kiss-o'-Death "RATE", notice 1 = "Rate limited" log entry.
/// </summary>
public sealed class NtpRateLimiter
{
    private readonly KeyedRateLimiter<UInt128> _limiter;

    public NtpRateLimiter(RateLimitOptions? options = null, TimeProvider? time = null)
    {
        Options = options ?? new RateLimitOptions();
        _limiter = new KeyedRateLimiter<UInt128>(
            new KeyedRateLimitOptions
            {
                Burst = Options.Burst,
                RefillSeconds = Options.RefillSeconds,
                MaxClients = Options.MaxClients,
                IdleExpiry = Options.IdleExpiry,
                GlobalPerSecond = Options.GlobalPerSecond,
                NoticeIntervals = [Options.KissInterval, Options.LimitedLogInterval],
            },
            time);
    }

    public RateLimitOptions Options { get; }

    /// <summary>Clients currently tracked (bounded by <see cref="RateLimitOptions.MaxClients"/>).</summary>
    public int ClientCount => _limiter.ClientCount;

    /// <summary>The global limit dropped a request within <paramref name="window"/>.</summary>
    public bool GloballyLimitedWithin(TimeSpan window) => _limiter.GloballyLimitedWithin(window);

    public RateResult Check(UInt128 client)
    {
        var result = _limiter.Check(client);
        return result.Decision switch
        {
            RateLimitDecision.Allow => new RateResult(RateDecision.Allow, false),
            RateLimitDecision.GlobalDrop => new RateResult(RateDecision.GlobalDrop, false),
            _ => new RateResult(result.IsDue(0) ? RateDecision.DropWithKiss : RateDecision.Drop, result.IsDue(1)),
        };
    }
}
