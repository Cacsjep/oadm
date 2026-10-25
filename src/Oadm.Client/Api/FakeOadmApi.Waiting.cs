using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode: the waiting list in memory. Nothing ever answers: Try now shows the entries as checking for
/// <see cref="FakeWaitingTryTime"/>, then records the try.
/// </summary>
public sealed partial class FakeOadmApi
{
    private const int FakeMaxWaiting = 30_000;
    private readonly List<WaitingDevice> _waiting = [];
    private readonly Broadcast<WaitingSummary> _waitingEvents = new();
    private bool _waitingTrying;

    /// <summary>How long a fake Try now "checks" (tests set it to zero).</summary>
    public TimeSpan FakeWaitingTryTime { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The clock of <see cref="FakeWaitingTryTime"/> (tests pass a fake clock and advance it).</summary>
    public TimeProvider FakeWaitingClock { get; set; } = TimeProvider.System;

    /// <summary>The lines kept (tests), with the passwords they came with.</summary>
    public IReadOnlyList<WaitingLine> KeptWaitingLines => _keptWaitingLines;

    private readonly List<WaitingLine> _keptWaitingLines = [];

    public Task<AddWaitingReply> AddWaitingAsync(IReadOnlyCollection<WaitingLine> lines, string source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            ThrowIfOffline();
            // Like the server: one entry per (site, address).
            var byAddress = _waiting.ToDictionary(w => WaitingKey(w.Address, w.SiteId), StringComparer.Ordinal);
            int added = lines.Select(l => WaitingKey(l.Address, l.SiteId)).Distinct(StringComparer.Ordinal).Count(a => !byAddress.ContainsKey(a));
            if (_waiting.Count + added > FakeMaxWaiting)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "The waiting list holds at most 30,000 devices. Remove devices from it first."));
            }

            foreach (WaitingLine line in lines)
            {
                string key = WaitingKey(line.Address, line.SiteId);
                if (!byAddress.TryGetValue(key, out WaitingDevice? entry))
                {
                    entry = new WaitingDevice { Id = Guid.NewGuid().ToString(), Created = Timestamp.FromDateTime(DateTime.UtcNow) };
                    _waiting.Add(entry);
                    byAddress[key] = entry;
                }

                entry.Address = line.Address.Trim();
                entry.SiteId = line.SiteId;
                entry.SiteName = Guid.TryParse(line.SiteId, out Guid site) && FakeSites.TryGetValue(site, out string? name) ? name : "";
                entry.ExpectedSerial = line.ExpectedSerial;
                entry.Tags.Clear();
                entry.Tags.AddRange(line.Tags);
                entry.Source = source;
                entry.AddedBy = "admin";
                entry.HasLogin = line.Password.Length > 0;
                entry.LoginFailed = false;
                entry.Tries = 0;
                entry.LastResult = "";
                entry.LastTry = null;
                _keptWaitingLines.Add(line);
            }

            PublishWaitingLocked();
            return Task.FromResult(new AddWaitingReply { Added = added, Updated = lines.Count - added, Total = _waiting.Count });
        }
    }

    private static string WaitingKey(string address, string siteId) => address.Trim().ToUpperInvariant() + "@" + siteId;

    public Task<WaitingList> ListWaitingAsync(string? after, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            // Like the server: address order (upper case), a cursor instead of an offset.
            int take = limit <= 0 ? int.MaxValue : limit;
            List<WaitingDevice> page = _waiting
                .Where(w => string.IsNullOrEmpty(after) || string.CompareOrdinal(WaitingKey(w), after) > 0)
                .OrderBy(WaitingKey, StringComparer.Ordinal)
                .Take(take)
                .ToList();
            var list = new WaitingList { TotalCount = _waiting.Count, NextAfter = page.Count == take ? WaitingKey(page[^1]) : "" };
            list.Devices.AddRange(page.Select(w => w.Clone()));
            return Task.FromResult(list);
        }
    }

    private static string WaitingKey(WaitingDevice device) => device.Address.Trim().ToUpperInvariant();

    public async IAsyncEnumerable<WaitingSummary> WatchWaitingAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<WaitingSummary> channel;
        WaitingSummary first;
        lock (_gate)
        {
            ThrowIfOffline();
            channel = _waitingEvents.Subscribe();
            first = WaitingSummaryLocked();
        }

        try
        {
            yield return first;
            await foreach (WaitingSummary summary in channel.Reader.ReadAllAsync(ct))
            {
                yield return summary;
            }
        }
        finally
        {
            _waitingEvents.Unsubscribe(channel);
        }
    }

    public Task<WaitingSummary> RemoveWaitingAsync(IReadOnlyCollection<string> ids, bool all, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            ThrowIfOffline();
            if (all)
            {
                _waiting.Clear();
            }
            else
            {
                var gone = ids.ToHashSet(StringComparer.Ordinal);
                _waiting.RemoveAll(w => gone.Contains(w.Id));
            }

            PublishWaitingLocked();
            return Task.FromResult(WaitingSummaryLocked());
        }
    }

    public Task<WaitingSummary> TryWaitingNowAsync(IReadOnlyCollection<string> ids, bool all, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            ThrowIfOffline();
            var chosen = ids.ToHashSet(StringComparer.Ordinal);
            List<WaitingDevice> tried = _waiting.Where(w => all || chosen.Contains(w.Id)).ToList();
            foreach (WaitingDevice entry in tried)
            {
                entry.Checking = true;
            }

            _waitingTrying = FakeWaitingTryTime > TimeSpan.Zero;
            if (!_waitingTrying)
            {
                FinishWaitingTryLocked(tried);
            }
            else
            {
                _ = Task.Delay(FakeWaitingTryTime, FakeWaitingClock, CancellationToken.None).ContinueWith(_ =>
                {
                    lock (_gate)
                    {
                        _waitingTrying = false;
                        FinishWaitingTryLocked(tried);
                    }
                }, TaskScheduler.Default);
            }

            PublishWaitingLocked();
            return Task.FromResult(WaitingSummaryLocked());
        }
    }

    private void FinishWaitingTryLocked(List<WaitingDevice> tried)
    {
        foreach (WaitingDevice entry in tried)
        {
            entry.Checking = false;
            entry.Tries++;
            entry.LastTry = Timestamp.FromDateTime(DateTime.UtcNow);
            entry.LastResult = $"No Axis device answered at {entry.Address}.";
            entry.LoginFailed = false;
        }

        PublishWaitingLocked();
    }

    private WaitingSummary WaitingSummaryLocked() => new()
    {
        Count = _waiting.Count,
        LoginFailed = _waiting.Count(w => w.LoginFailed),
        Trying = _waitingTrying,
        Remaining = _waiting.Count(w => w.Checking),
        NextTry = _waitingTrying ? null : Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(15)),
    };

    private void PublishWaitingLocked() => _waitingEvents.Publish(WaitingSummaryLocked());
}
