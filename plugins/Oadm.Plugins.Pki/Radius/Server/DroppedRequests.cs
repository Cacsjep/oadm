using System.Net;

using Oadm.Plugins.Radius.Api;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// Requests dropped before a session starts, as log rows (docs/spec/radius-server.md "Log"): a sender that is no switch and
/// an added switch with a wrong shared secret. At most one row per sender address and reason per <see cref="Window"/>: the
/// first request gives a row at the next tick, the ones after it within the window are counted and give one row when the
/// window ended ("12 requests in the last minute"). At most <see cref="MaxUnknownSenders"/> senders that are no switch are
/// followed at once (spoofed addresses cannot grow it); the others are only counted (<c>UnknownRequests</c>). Senders with
/// a wrong secret are added switches (at most 500). Thread-safe; <see cref="Count"/> is O(1), <see cref="Drain"/> one pass.
/// </summary>
public sealed class DroppedRequests
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public const int MaxUnknownSenders = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Sender> _senders = [];
    private int _unknown;

    public enum Kind
    {
        NotAdded,
        WrongSecret,
    }

    /// <summary>The senders followed now (tests).</summary>
    public int Followed
    {
        get
        {
            lock (_gate)
            {
                return _senders.Count;
            }
        }
    }

    /// <summary>
    /// Counts one dropped request. <paramref name="switchName"/>: the switch's name (wrong secret) or the sender's text
    /// ("10.0.0.9 (not added)").
    /// </summary>
    public void Count(Kind kind, Guid? siteId, IPAddress address, string switchName, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(switchName);
        var key = new Key(kind, siteId, address);
        lock (_gate)
        {
            if (!_senders.TryGetValue(key, out var sender))
            {
                if (kind == Kind.NotAdded && _unknown >= MaxUnknownSenders)
                {
                    return;
                }

                _senders[key] = sender = new Sender();
                if (kind == Kind.NotAdded)
                {
                    _unknown++;
                }
            }

            sender.Pending++;
            sender.LastUtc = nowUtc;
            sender.SwitchName = switchName;
        }
    }

    /// <summary>
    /// The rows due now: senders with counted requests whose last row is older than <see cref="Window"/> (or that have none).
    /// Senders without new requests for a window are forgotten.
    /// </summary>
    public IReadOnlyList<LogRow> Drain(DateTime nowUtc)
    {
        List<LogRow>? rows = null;
        List<Key>? idle = null;
        lock (_gate)
        {
            foreach (var (key, sender) in _senders)
            {
                var quiet = sender.WrittenUtc is not { } written || nowUtc - written >= Window;
                if (!quiet)
                {
                    continue;
                }

                if (sender.Pending == 0)
                {
                    (idle ??= []).Add(key);
                    continue;
                }

                var reason = key.Kind == Kind.NotAdded ? DroppedRequestTexts.NotAdded : DroppedRequestTexts.WrongSecret;
                (rows ??= []).Add(new LogRow
                {
                    TimeUtc = sender.LastUtc,
                    SwitchName = sender.SwitchName,
                    Result = LogResults.Failed,
                    Reason = DroppedRequestTexts.Reason(reason, sender.Pending),
                    Requests = sender.Pending,
                    Sender = key.Kind == Kind.NotAdded ? key.Address.ToString() : null,
                    SenderSiteId = key.Kind == Kind.NotAdded ? key.SiteId : null,
                });
                sender.Pending = 0;
                sender.WrittenUtc = nowUtc;
            }

            if (idle is not null)
            {
                foreach (var key in idle)
                {
                    _senders.Remove(key);
                    if (key.Kind == Kind.NotAdded)
                    {
                        _unknown--;
                    }
                }
            }
        }

        if (rows is null)
        {
            return [];
        }

        rows.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc)); // oldest first, like ended sessions
        return rows;
    }

    private readonly record struct Key(Kind Kind, Guid? SiteId, IPAddress Address);

    private sealed class Sender
    {
        public int Pending;
        public DateTime LastUtc;
        public DateTime? WrittenUtc;
        public string SwitchName = string.Empty;
    }
}
