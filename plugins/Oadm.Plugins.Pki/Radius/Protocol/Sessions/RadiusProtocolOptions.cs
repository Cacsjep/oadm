using Oadm.Plugins.Radius.Protocol.Tls;

namespace Oadm.Plugins.Radius.Protocol.Sessions;

/// <summary>A switch as the server package found it for a datagram (by source address, never by NAS-Identifier).</summary>
/// <param name="Key">Its stable key (e.g. its id, or site + address at a site).</param>
/// <param name="Secret">The shared secret (RFC 2865 3 "MUST NOT be empty"). Never logged.</param>
public sealed record RadiusClient(string Key, ReadOnlyMemory<byte> Secret)
{
    public string Key { get; } = !string.IsNullOrEmpty(Key) ? Key : throw new ArgumentException("A switch needs a key.", nameof(Key));

    public ReadOnlyMemory<byte> Secret { get; } = !Secret.IsEmpty ? Secret : throw new ArgumentException("The shared secret must not be empty (RFC 2865 3).", nameof(Secret));

    /// <summary>Never prints the secret.</summary>
    public override string ToString() => Key;
}

/// <summary>
/// The settings of the protocol layer, replaced as a whole while the server runs (a running session keeps the settings it
/// started with, except the server identity, which it reads when its handshake starts).
/// </summary>
public sealed record RadiusProtocolOptions
{
    /// <summary>The smallest and largest "Largest fragment" (docs/spec/radius-server.md "Advanced").</summary>
    public const int MinFragment = 500;

    public const int MaxFragment = 1400;

    public const int DefaultFragment = 1020; // RFC 3748 3.1: the EAP MTU every link guarantees

    /// <summary>Minimum version and cipher suites (Advanced).</summary>
    public TlsSettings Tls { get; init; } = TlsSettings.Default;

    /// <summary>The largest EAP packet (EAP header included), 500..1400 (Advanced); the switch's Framed-MTU makes it smaller.</summary>
    public int LargestFragment
    {
        get;
        init => field = value is >= MinFragment and <= MaxFragment ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "500..1400 bytes.");
    } = DefaultFragment;

    /// <summary>
    /// Reauthentication (the 802.1X server policy): every Access-Accept carries Session-Timeout with this many seconds and
    /// Termination-Action RADIUS-Request, so the switch authenticates the port again then (RFC 3580 3.17, 3.19); null: none
    /// are sent and the switch decides.
    /// </summary>
    public TimeSpan? Reauthentication { get; init; }

    /// <summary>The server certificate; null refuses every handshake ("The 802.1X server has no certificate").</summary>
    public TlsServerIdentity? ServerIdentity { get; init; }

    /// <summary>A session ends Timed out without a message within this time (30 s).</summary>
    public TimeSpan MessageTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A session ends Timed out after this time in all (60 s).</summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Access-Requests per session at most (60).</summary>
    public int MaxRoundTrips { get; init; } = 60;

    /// <summary>The largest TLS message the device may announce (64 KB, RFC 5216 2.1.5).</summary>
    public int MaxMessageLength { get; init; } = 64 * 1024;

    /// <summary>Open sessions at most (10,000).</summary>
    public int MaxSessions { get; init; } = 10_000;

    /// <summary>Open sessions per switch at most (1,000).</summary>
    public int MaxSessionsPerClient { get; init; } = 1_000;

    /// <summary>Open sessions per switch port at most (8): a new one on a full port ends the oldest there.</summary>
    public int MaxSessionsPerPort { get; init; } = 8;
}
