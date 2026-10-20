using System.Net;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Links;

/// <summary>
/// One TLS connection between the node and a server, whichever side opened it. Kestrel serves gRPC on it; every call
/// finds its link through <see cref="LinkEndPoint"/> (what the server's certificate was, where it came from).
/// </summary>
public sealed class SiteLink
{
    private static long s_nextId;
    private readonly Lock _lock = new();
    private NodePairing? _pairing;
    private string? _pairingCode;
    private long _lastActivity;
    private bool _served;

    public SiteLink(bool outbound, bool worker, string remote, X509Certificate2 serverCertificate, byte[] nodeFingerprint)
    {
        ArgumentNullException.ThrowIfNull(serverCertificate);
        Outbound = outbound;
        Worker = worker;
        Remote = remote;
        RemoteHost = HostOf(remote);
        RemoteKey = IPAddress.TryParse(RemoteHost, out var address) ? AddressRateLimiter.KeyOf(address) : RemoteHost;
        ServerCertificate = serverCertificate;
        ServerFingerprint = Oadm.Sites.Transport.SiteTls.Fingerprint(serverCertificate);
        ServerFingerprintHex = Convert.ToHexString(ServerFingerprint);
        NodeFingerprint = nodeFingerprint;
        Id = Interlocked.Increment(ref s_nextId);
    }

    public long Id { get; }

    /// <summary>The node opened it (mode Connect).</summary>
    public bool Outbound { get; }

    /// <summary>A worker connection the server asked for with OpenConnections.</summary>
    public bool Worker { get; }

    /// <summary>"10.0.0.5:51234".</summary>
    public string Remote { get; }

    /// <summary>"10.0.0.5".</summary>
    public string RemoteHost { get; }

    public X509Certificate2 ServerCertificate { get; }

    public byte[] ServerFingerprint { get; }

    public string ServerFingerprintHex { get; }

    /// <summary>The node's own fingerprint on this link (the pairing proofs cover both).</summary>
    public byte[] NodeFingerprint { get; }

    /// <summary>The source for the attempt limits: the address, IPv6 by its /64 (<see cref="AddressRateLimiter.KeyOf"/>).</summary>
    public string RemoteKey { get; }

    /// <summary>The TimeProvider timestamp of the last call on this link (unbound links idle too long are closed).</summary>
    public long LastActivity => Interlocked.Read(ref _lastActivity);

    /// <summary>True once a call of the bound server was served on it (the outbound connector's backoff starts over then).</summary>
    public bool Served => Volatile.Read(ref _served);

    public void Touch(long timestamp) => Interlocked.Exchange(ref _lastActivity, timestamp);

    public void MarkServed() => Volatile.Write(ref _served, true);

    /// <summary>Closes the connection (Reset, a lost session).</summary>
    public CancellationTokenSource Closing { get; } = new();

    /// <summary>PairStart: one attempt per link, for the code at that time.</summary>
    public void SetPairing(NodePairing pairing, string code)
    {
        lock (_lock)
        {
            _pairing = pairing;
            _pairingCode = code;
        }
    }

    /// <summary>The attempt of the last PairStart, taken once.</summary>
    public (NodePairing? Pairing, string? Code) TakePairing()
    {
        lock (_lock)
        {
            var result = (_pairing, _pairingCode);
            _pairing = null;
            _pairingCode = null;
            return result;
        }
    }

    public void Close()
    {
        try
        {
            Closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string HostOf(string remote)
    {
        if (IPEndPoint.TryParse(remote, out var endpoint))
            return endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4().ToString() : endpoint.Address.ToString();
        var colon = remote.LastIndexOf(':');
        return colon > 0 ? remote[..colon] : remote;
    }
}

/// <summary>The remote end point Kestrel gets for a link, so a call can find it.</summary>
public sealed class LinkEndPoint(SiteLink link) : EndPoint
{
    public SiteLink Link { get; } = link;

    public override string ToString() => Link.Remote;
}
