using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oadm.Contracts.Security;

/// <summary>Where a client keeps the pinned server certificate fingerprints (client settings file, tests: memory).</summary>
public interface IServerPinStore
{
    /// <summary>The pinned SHA-256 fingerprint of a server (key from <see cref="ServerCertificatePinning.KeyOf"/>), or null.</summary>
    string? GetPin(string serverKey);

    void SetPin(string serverKey, string fingerprint);

    void RemovePin(string serverKey);
}

/// <summary>Result of the last TLS handshake check of a server.</summary>
public enum PinCheck
{
    /// <summary>No TLS handshake yet (or plain http).</summary>
    None,

    /// <summary>The certificate matches the pinned fingerprint.</summary>
    Trusted,

    /// <summary>First connection: nothing pinned yet, the user has to confirm <see cref="ServerCertificatePinning.PresentedFingerprint"/>.</summary>
    Unknown,

    /// <summary>The server presents another certificate than the pinned one: refused.</summary>
    Changed,
}

/// <summary>
/// Trust on first use for the OADM server's self-signed TLS certificate: the client accepts exactly the certificate whose
/// SHA-256 fingerprint is pinned for the server. An unknown certificate is refused until the user confirmed its
/// fingerprint (<see cref="Trust"/>); a changed one is refused until the user forgets the server
/// (<see cref="Forget"/>). The CA chain and the host name are not evaluated: the pin is the trust.
/// </summary>
public sealed class ServerCertificatePinning(IServerPinStore store)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (PinCheck Check, string? Fingerprint)> _last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"host:port" in lower case, the key of a server in the pin store.</summary>
    public static string KeyOf(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return (address.IdnHost + ":" + address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToLowerInvariant();
    }

    /// <summary>SHA-256 of the DER certificate as upper-case hex pairs separated by colons ("3F:A2:...").</summary>
    public static string Fingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(":", Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).Chunk(2).Select(c => new string(c)));
    }

    /// <summary>The outcome of the last handshake with the server.</summary>
    public PinCheck LastCheck(Uri address)
    {
        lock (_gate)
        {
            return _last.TryGetValue(KeyOf(address), out var last) ? last.Check : PinCheck.None;
        }
    }

    /// <summary>The fingerprint the server presented in the last handshake (also when it was refused).</summary>
    public string? PresentedFingerprint(Uri address)
    {
        lock (_gate)
        {
            return _last.TryGetValue(KeyOf(address), out var last) ? last.Fingerprint : null;
        }
    }

    /// <summary>The pinned fingerprint of the server, or null.</summary>
    public string? PinnedFingerprint(Uri address) => store.GetPin(KeyOf(address));

    /// <summary>The user confirmed the fingerprint: pins it.</summary>
    public void Trust(Uri address, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        store.SetPin(KeyOf(address), fingerprint);
    }

    /// <summary>Clears the remembered outcome before a new attempt (so an old result is not taken for a new one).</summary>
    public void ResetCheck(Uri address)
    {
        lock (_gate)
        {
            _last.Remove(KeyOf(address));
        }
    }

    /// <summary>Forget server: removes the pin, the next connection asks again.</summary>
    public void Forget(Uri address)
    {
        store.RemovePin(KeyOf(address));
        lock (_gate)
        {
            _last.Remove(KeyOf(address));
        }
    }

    /// <summary>The TLS check for one server address (SslClientAuthenticationOptions.RemoteCertificateValidationCallback).</summary>
    public RemoteCertificateValidationCallback CallbackFor(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return (_, certificate, _, _) => Check(address, certificate);
    }

    /// <summary>True when <paramref name="certificate"/> is the pinned certificate of the server.</summary>
    public bool Check(Uri address, X509Certificate? certificate)
    {
        ArgumentNullException.ThrowIfNull(address);
        string key = KeyOf(address);
        if (certificate is null)
        {
            Remember(key, PinCheck.Changed, null);
            return false;
        }

        string presented = Fingerprint(certificate);
        string? pinned = store.GetPin(key);
        var check = pinned is null ? PinCheck.Unknown
            : string.Equals(pinned, presented, StringComparison.OrdinalIgnoreCase) ? PinCheck.Trusted
            : PinCheck.Changed;
        Remember(key, check, presented);
        return check == PinCheck.Trusted;
    }

    private void Remember(string key, PinCheck check, string? fingerprint)
    {
        lock (_gate)
        {
            _last[key] = (check, fingerprint);
        }
    }
}

/// <summary>Pins in memory (tests, fake mode).</summary>
public sealed class InMemoryPinStore : IServerPinStore
{
    private readonly Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

    public string? GetPin(string serverKey)
    {
        lock (_pins)
        {
            return _pins.TryGetValue(serverKey, out var pin) ? pin : null;
        }
    }

    public void SetPin(string serverKey, string fingerprint)
    {
        lock (_pins)
        {
            _pins[serverKey] = fingerprint;
        }
    }

    public void RemovePin(string serverKey)
    {
        lock (_pins)
        {
            _pins.Remove(serverKey);
        }
    }
}
