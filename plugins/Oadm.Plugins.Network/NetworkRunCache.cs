using System.Collections.Concurrent;

using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.Network;

/// <summary>
/// What the device tasks of one run share: the payload parsed and validated once for the whole batch, a refusal too
/// (every task of the run fails with the same text). The devices of a payload are a dictionary by device id. A run is its
/// payload text; an entry lives <see cref="KeepFor"/> from its first use (a longer run parses again), at most
/// <see cref="MaxRuns"/> at a time. Thread safe: tasks that start together wait for one parse.
/// </summary>
/// <param name="time">Clock of the entries' age.</param>
/// <param name="validate">The plugin's validation of the whole batch; throws <see cref="NetworkValidationException"/>.</param>
internal sealed class NetworkRunCache(TimeProvider time, Action<NetworkPayload> validate)
{
    /// <summary>How long a run's parsed payload is kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    /// <summary>Runs kept at the same time; the oldest goes first.</summary>
    public const int MaxRuns = 16;

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);
    private int _parses;

    /// <summary>Payloads parsed and validated so far (tests).</summary>
    internal int Parses => Volatile.Read(ref _parses);

    /// <summary>Runs kept now (tests).</summary>
    internal int Count => _runs.Count;

    /// <summary>The validated payload of <paramref name="payloadJson"/>; throws <see cref="NetworkValidationException"/> like the first task of the run.</summary>
    public NetworkPayload For(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return NetworkPayload.Parse(payloadJson); // throws: there is nothing to keep
        }

        var now = time.GetUtcNow();
        var run = Find(payloadJson, now);
        if (run is null)
        {
            var fresh = new Run(payloadJson, now, new Lazy<Outcome>(() => Read(payloadJson)));
            run = _runs.AddOrUpdate(payloadJson, fresh, (_, kept) => now - kept.Created >= KeepFor ? fresh : kept);
            Trim(now);
        }

        var outcome = run.Outcome.Value;
        return outcome.Payload ?? throw new NetworkValidationException(outcome.Errors);
    }

    /// <summary>
    /// The kept run of <paramref name="payloadJson"/>. The engine hands every task of a run the same string, so the runs
    /// are compared by reference first: a lookup by text hashes the whole payload (megabytes at 100,000 devices).
    /// </summary>
    private Run? Find(string payloadJson, DateTimeOffset now)
    {
        foreach (var (_, kept) in _runs)
        {
            if (ReferenceEquals(kept.Json, payloadJson))
            {
                return now - kept.Created < KeepFor ? kept : null;
            }
        }

        return _runs.TryGetValue(payloadJson, out var run) && now - run.Created < KeepFor ? run : null;
    }

    private Outcome Read(string payloadJson)
    {
        Interlocked.Increment(ref _parses);
        try
        {
            var payload = NetworkPayload.Parse(payloadJson);
            validate(payload);
            return new Outcome(payload, []);
        }
        catch (NetworkValidationException ex)
        {
            return new Outcome(null, ex.Errors);
        }
    }

    /// <summary>Drops runs older than <see cref="KeepFor"/>, then the oldest above <see cref="MaxRuns"/>.</summary>
    private void Trim(DateTimeOffset now)
    {
        foreach (var (key, run) in _runs)
        {
            if (now - run.Created >= KeepFor)
            {
                _runs.TryRemove(new KeyValuePair<string, Run>(key, run));
            }
        }

        while (_runs.Count > MaxRuns)
        {
            var oldest = _runs.MinBy(r => r.Value.Created);
            _runs.TryRemove(oldest);
        }
    }

    private sealed record Run(string Json, DateTimeOffset Created, Lazy<Outcome> Outcome);

    /// <summary>The payload, or the errors of its validation.</summary>
    private sealed record Outcome(NetworkPayload? Payload, IReadOnlyList<string> Errors);
}
