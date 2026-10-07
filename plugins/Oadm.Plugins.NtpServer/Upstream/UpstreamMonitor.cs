using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.NtpServer.Serving;

namespace Oadm.Plugins.NtpServer.Upstream;

/// <summary>What the monitor knows about the upstream right now.</summary>
/// <param name="Host">As configured.</param>
/// <param name="Reachable">Null until the first answer or until <see cref="UpstreamOptions.FailuresBeforeUnreachable"/> failures.</param>
/// <param name="LastSample">Last good answer (also kept while unreachable, for the page).</param>
/// <param name="ConsecutiveFailures">Failures since the last good answer.</param>
/// <param name="LastError">Text of the last failure.</param>
public sealed record UpstreamSnapshot(string Host, bool? Reachable, UpstreamSample? LastSample, int ConsecutiveFailures, string? LastError)
{
    public UpstreamInfo ToInfo() => new(
        Host,
        Reachable,
        LastSample?.Stratum,
        LastSample is { } s ? Math.Round(s.OffsetSeconds * 1000, 1) : null,
        LastSample?.TimeUtc,
        LastError);
}

/// <summary>
/// Queries the upstream on its own background loop, decoupled from serving: <see cref="ServingState"/> is read by the
/// responder without waiting. At most one query in flight (one loop). Good answer: serve stratum + 1 at once and poll
/// again after 64 s (doubling to 1024 s while answers stay good). Failure: retry after 2, 4, 8 ... s (capped at the
/// poll interval); after 3 failures in a row the upstream is not reachable and the server serves its own clock until
/// the next good answer. Kiss-o'-Death RATE doubles the poll interval, DENY / RSTR back off to the longest interval.
/// </summary>
public sealed partial class UpstreamMonitor : IAsyncDisposable
{
    private readonly UpstreamHost _host;
    private readonly string _hostText;
    private readonly UpstreamOptions _options;
    private readonly CachingUpstreamResolver _resolver;
    private readonly TimeProvider _time;
    private readonly Action _changed;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile UpstreamSnapshot _snapshot;
    private volatile TimeSourceState? _servingState;
    private long _queries;

    public UpstreamMonitor(UpstreamHost host, UpstreamOptions options, IUpstreamResolver resolver, TimeProvider time, Action? changed = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        _host = host;
        _hostText = host.ToString();
        _options = options;
        _time = time ?? TimeProvider.System;
        _resolver = new CachingUpstreamResolver(resolver, options, _time);
        _changed = changed ?? (() => { });
        _logger = logger ?? NullLogger.Instance;
        _snapshot = new UpstreamSnapshot(host.ToString(), null, null, 0, null);
    }

    public UpstreamSnapshot Snapshot => _snapshot;

    /// <summary>The upstream state to serve, null while not (yet) reachable: then the local clock is served.</summary>
    public TimeSourceState? ServingState => _servingState;

    /// <summary>Queries sent so far (tests).</summary>
    public long QueryCount => Interlocked.Read(ref _queries);

    /// <summary>
    /// One query with the same timeouts as the loop (resolve, then ask). Used by Save to validate a new upstream.
    /// Throws <see cref="UpstreamException"/>.
    /// </summary>
    public static async Task<UpstreamSample> QueryOnceAsync(UpstreamHost host, UpstreamOptions options, IUpstreamResolver resolver, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var endpoint = await new CachingUpstreamResolver(resolver, options, time).ResolveAsync(host, ct).ConfigureAwait(false);
        return await UpstreamClient.QueryAsync(endpoint, options.QueryTimeout, time, ct).ConfigureAwait(false);
    }

    /// <summary>Starts the loop; <paramref name="seed"/> (the answer of the Save validation) counts as the first good answer.</summary>
    public void Start(UpstreamSample? seed = null)
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        if (seed is not null)
        {
            Accept(seed);
        }

        _loop = Task.Run(() => RunAsync(seed is null ? TimeSpan.Zero : _options.MinPoll, _cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        _cts.Dispose();
    }

    private async Task RunAsync(TimeSpan firstDelay, CancellationToken ct)
    {
        var poll = _options.MinPoll;
        var good = 0;
        var delay = firstDelay;
        while (!ct.IsCancellationRequested)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }

            try
            {
                Interlocked.Increment(ref _queries);
                var endpoint = await _resolver.ResolveAsync(_host, ct).ConfigureAwait(false);
                var sample = await UpstreamClient.QueryAsync(endpoint, _options.QueryTimeout, _time, ct).ConfigureAwait(false);
                Accept(sample);
                good++;
                if (good >= _options.GoodAnswersBeforeLongerPoll && poll < _options.MaxPoll)
                {
                    poll = Min(poll * 2, _options.MaxPoll);
                    good = 0;
                }

                delay = poll;
            }
            catch (UpstreamException ex)
            {
                good = 0;
                var failures = Fail(ex.Message);
                if (ex.Failure == UpstreamFailure.KissOfDeath)
                {
                    poll = ex.KissCode == "RATE" ? Min(poll * 2, _options.MaxPoll) : _options.MaxPoll;
                    delay = poll;
                }
                else
                {
                    poll = _options.MinPoll;
                    var backoff = _options.FirstRetry * Math.Pow(2, Math.Min(failures - 1, 20));
                    delay = Min(backoff, poll);
                }

                LogFailed(_hostText, ex.Message, delay.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The loop must survive anything; the failure is shown on the page.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                good = 0;
                var failures = Fail(ex.Message);
                delay = Min(_options.FirstRetry * Math.Pow(2, Math.Min(failures - 1, 20)), _options.MinPoll);
                LogFailed(_hostText, ex.Message, delay.TotalSeconds);
            }
        }
    }

    private void Accept(UpstreamSample sample)
    {
        _servingState = sample.ToState(_host.ToString());
        _snapshot = new UpstreamSnapshot(_host.ToString(), true, sample, 0, null);
        _changed();
    }

    private int Fail(string message)
    {
        var previous = _snapshot;
        var failures = previous.ConsecutiveFailures + 1;
        var unreachable = failures >= _options.FailuresBeforeUnreachable;
        if (unreachable)
        {
            _servingState = null;
        }

        _snapshot = previous with
        {
            ConsecutiveFailures = failures,
            LastError = message,
            Reachable = unreachable ? false : previous.Reachable,
        };
        _changed();
        return failures;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    [LoggerMessage(Level = LogLevel.Information, Message = "NTP upstream {Host}: {Error}; next query in {Seconds} s")]
    private partial void LogFailed(string host, string error, double seconds);
}
