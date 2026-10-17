using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>Fake mode: the waiting list in memory. Nothing ever answers, so Try now only records the try.</summary>
public sealed partial class FakeOadmApi
{
    private const int FakeMaxWaiting = 30_000;
    private readonly List<WaitingDevice> _waiting = [];
    private readonly Broadcast<WaitingSummary> _waitingEvents = new();

    /// <summary>The lines kept (tests), with the passwords they came with.</summary>
    public IReadOnlyList<WaitingLine> KeptWaitingLines => _keptWaitingLines;

    private readonly List<WaitingLine> _keptWaitingLines = [];

    public Task<AddWaitingReply> AddWaitingAsync(IReadOnlyCollection<WaitingLine> lines, string source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            ThrowIfOffline();
            var byAddress = _waiting.ToDictionary(w => w.Address.ToUpperInvariant(), StringComparer.Ordinal);
            int added = lines.Select(l => l.Address.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count(a => !byAddress.ContainsKey(a));
            if (_waiting.Count + added > FakeMaxWaiting)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "The waiting list holds at most 30,000 devices. Remove devices from it first."));
            }

            foreach (WaitingLine line in lines)
            {
                string key = line.Address.Trim().ToUpperInvariant();
                if (!byAddress.TryGetValue(key, out WaitingDevice? entry))
                {
                    entry = new WaitingDevice { Id = Guid.NewGuid().ToString(), Created = Timestamp.FromDateTime(DateTime.UtcNow) };
                    _waiting.Add(entry);
                    byAddress[key] = entry;
                }

                entry.Address = line.Address.Trim();
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

    public Task<WaitingList> ListWaitingAsync(int offset, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var list = new WaitingList { TotalCount = _waiting.Count };
            list.Devices.AddRange(_waiting.Skip(Math.Max(0, offset)).Take(limit <= 0 ? int.MaxValue : limit).Select(w => w.Clone()));
            return Task.FromResult(list);
        }
    }

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
            foreach (WaitingDevice entry in _waiting.Where(w => all || chosen.Contains(w.Id)))
            {
                entry.Tries++;
                entry.LastTry = Timestamp.FromDateTime(DateTime.UtcNow);
                entry.LastResult = $"No Axis device answered at {entry.Address}.";
                entry.LoginFailed = false;
            }

            PublishWaitingLocked();
            return Task.FromResult(WaitingSummaryLocked());
        }
    }

    private WaitingSummary WaitingSummaryLocked() => new()
    {
        Count = _waiting.Count,
        LoginFailed = _waiting.Count(w => w.LoginFailed),
        NextTry = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(15)),
    };

    private void PublishWaitingLocked() => _waitingEvents.Publish(WaitingSummaryLocked());
}
