using System.Collections.Concurrent;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The Sessions tab (docs/spec/radius-server.md "Session states"): the protocol layer's session snapshots arrive through
/// <see cref="Enqueue"/> (cheap, from inside the session lock) and <see cref="Drain"/> (every 500 ms) turns them into page
/// rows: the changed rows, the sessions that ended (once each, for the log and the event rules) and the rows of sessions
/// that ended 10 s ago, which leave the tab. At most 10,000 open sessions (the protocol's limit), O(1) per change.
/// </summary>
public sealed class SessionBoard(TimeProvider time)
{
    /// <summary>How long an ended session stays on the tab.</summary>
    public static readonly TimeSpan KeepEnded = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<EapSessionSnapshot> _incoming = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _sessions = [];
    private readonly Queue<(Guid Id, DateTimeOffset At)> _endedOrder = new(); // ended sessions, oldest first

    /// <summary>A session changed (called by the protocol layer under the session's lock: only queues).</summary>
    public void Enqueue(EapSessionSnapshot snapshot) => _incoming.Enqueue(snapshot);

    /// <summary>Open sessions and those ended within the last 10 s.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Applies the queued snapshots: returns the changed rows (removed ones with <see cref="SessionRow.Removed"/>) and the
    /// sessions that ended since the last drain.
    /// </summary>
    public (IReadOnlyList<SessionRow> Changed, IReadOnlyList<EndedSession> Ended) Drain(Func<string, SwitchEntry?> switchOf, DeviceIndex devices)
    {
        ArgumentNullException.ThrowIfNull(switchOf);
        ArgumentNullException.ThrowIfNull(devices);
        var now = time.GetUtcNow();
        var changed = new Dictionary<Guid, SessionRow>();
        var ended = new List<EndedSession>();
        lock (_gate)
        {
            while (_incoming.TryDequeue(out var snapshot))
            {
                if (!_sessions.TryGetValue(snapshot.Id, out var entry))
                {
                    var entrySwitch = switchOf(snapshot.Client);
                    entry = new Entry(entrySwitch?.Name ?? string.Empty);
                    _sessions[snapshot.Id] = entry;
                }

                if (entry.Snapshot is { IsEnded: true })
                {
                    continue; // ended: later snapshots change nothing
                }

                entry.Snapshot = snapshot;
                entry.Row = RowOf(snapshot, entry.SwitchName, devices, now);
                changed[snapshot.Id] = entry.Row;
                if (snapshot.IsEnded)
                {
                    entry.EndedAt = now;
                    _endedOrder.Enqueue((snapshot.Id, now));
                    ended.Add(new EndedSession(snapshot, entry.SwitchName, entry.Row));
                }
            }

            while (_endedOrder.TryPeek(out var oldest) && now - oldest.At >= KeepEnded)
            {
                _endedOrder.Dequeue();
                if (_sessions.Remove(oldest.Id, out var entry))
                {
                    changed[oldest.Id] = entry.Row! with { Removed = true };
                }
            }

            // The duration of open sessions goes on: their rows are sent again only when something else changed.
        }

        return ([.. changed.Values], ended);
    }

    /// <summary>The rows now (open and recently ended), oldest first.</summary>
    public IReadOnlyList<SessionRow> Rows()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            return [.. _sessions.Values.Where(e => e.Row is not null)
                .Select(e => e.Snapshot is { IsEnded: false } open ? e.Row! with { DurationMs = (long)(now - open.Started).TotalMilliseconds } : e.Row!)
                .OrderBy(r => r.StartedUtc)];
        }
    }

    /// <summary>The page row of a snapshot: the device the certificate named, else a guess from the MAC address.</summary>
    internal static SessionRow RowOf(EapSessionSnapshot snapshot, string switchName, DeviceIndex devices, DateTimeOffset now)
    {
        var (device, guess) = DeviceOf(snapshot, devices);
        var end = snapshot.Ended ?? now;
        return new SessionRow
        {
            Id = snapshot.Id,
            StartedUtc = snapshot.Started.UtcDateTime,
            SwitchName = switchName,
            Port = snapshot.Port,
            PortType = snapshot.PortType,
            Mac = snapshot.Mac.Display,
            Identity = snapshot.EapIdentity,
            DeviceId = device?.Id,
            DeviceName = DeviceIndex.NameOf(device),
            DeviceGuess = guess,
            IpAddress = DeviceIndex.IpAddressOf(device),
            State = StateName(snapshot.State),
            StateText = snapshot.Text,
            Fragment = snapshot.FragmentCount > 1 ? new FragmentInfo(snapshot.Fragment, snapshot.FragmentCount) : null,
            DurationMs = Math.Max(0, (long)(end - snapshot.Started).TotalMilliseconds),
            Vlan = snapshot.VlanId,
            TlsVersion = snapshot.TlsVersion is { } version ? VersionText(version) : null,
        };
    }

    /// <summary>The device of a session and whether it is only a guess (from the MAC address, before or without a certificate check).</summary>
    internal static (IDeviceInfo? Device, bool Guess) DeviceOf(EapSessionSnapshot snapshot, DeviceIndex devices)
    {
        if (snapshot.Tag is CheckedCertificate { DeviceId: { } id })
        {
            return (devices.ById(id), false);
        }

        if (snapshot.Tag is CheckedCertificate)
        {
            return (null, false); // the certificate named no managed device: never a guess from the MAC
        }

        var guess = devices.GuessByMac(snapshot.Mac.Serial);
        return (guess, guess is not null);
    }

    /// <summary>"waitingForIdentity", ..., "timedOut", "abandoned".</summary>
    public static string StateName(EapSessionState state)
    {
        var name = state.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static string VersionText(TlsVersion version) => version switch
    {
        TlsVersion.Tls10 => "TLS 1.0",
        TlsVersion.Tls11 => "TLS 1.1",
        TlsVersion.Tls12 => "TLS 1.2",
        _ => "TLS 1.3",
    };

    private sealed class Entry(string switchName)
    {
        public string SwitchName { get; } = switchName;

        public EapSessionSnapshot? Snapshot { get; set; }

        public SessionRow? Row { get; set; }

        public DateTimeOffset? EndedAt { get; set; }
    }
}

/// <summary>A session that ended: its last snapshot, its switch's name and its last row.</summary>
public sealed record EndedSession(EapSessionSnapshot Snapshot, string SwitchName, SessionRow Row);
