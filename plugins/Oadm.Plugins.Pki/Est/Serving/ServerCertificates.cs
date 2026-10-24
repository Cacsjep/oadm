using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// The EST server's own TLS certificates, issued by the OADM CA: one for this server's network (its addresses and host
/// name) and one per site (the node's addresses towards the cameras). Cameras dial an address, so there is no SNI: the
/// listener picks the certificate by the site the connection came through. Valid days and key come from PKI's Server
/// certificate policy "EST server" (90 days, ECC by default; docs/spec/pki.md). A certificate is made again when its
/// addresses change, the CA or the policy's key changes, or 30 days (at most a third of its time) are left; a change of the
/// policy's days applies to the next certificate. Kept in memory only (the key never leaves the server).
/// </summary>
public sealed class ServerCertificates(Func<IDeviceCertificateAuthority?> authority, TimeProvider? time = null) : IDisposable
{
    /// <summary>Without PKI's policy: 90 days, ECC (as before the policies).</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(Oadm.Plugins.Pki.Policies.PolicyLimits.DefaultEstServerDays);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly SemaphoreSlim _making = new(1, 1);

    /// <summary>PKI's Server certificate policy of the EST server (valid days, key); null: 90 days, ECC.</summary>
    public Func<Oadm.Plugins.Pki.Policies.PkiPolicy?> Policy { get; init; } = () => null;

    /// <summary>
    /// The certificate (with its key) for <paramref name="siteId"/> (<see cref="Guid.Empty"/> = this server) naming
    /// <paramref name="addresses"/> and <paramref name="hostName"/>; null without a ready CA.
    /// </summary>
    public async Task<X509Certificate2?> GetAsync(Guid siteId, IReadOnlyCollection<string> addresses, string? hostName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (authority() is not { IsReady: true } ca)
        {
            return null;
        }

        var names = string.Join('|', addresses.Order(StringComparer.Ordinal)) + "|" + hostName;
        var caId = ca.Chain.Count > 0 ? Convert.ToHexString(SHA256.HashData(ca.Chain[0])) : string.Empty;
        var (validity, rsa) = Wanted(Policy());
        lock (_gate)
        {
            if (_entries.TryGetValue(siteId, out var entry) && Fits(entry, names, caId, rsa))
            {
                return entry.Certificate;
            }
        }

        // One at a time: connections that arrive while a certificate is made wait for it instead of each signing their own.
        await _making.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(siteId, out var entry) && Fits(entry, names, caId, rsa))
                {
                    return entry.Certificate;
                }
            }

            var made = await MakeAsync(ca, addresses, hostName, validity, rsa, ct).ConfigureAwait(false);
            lock (_gate)
            {
                // The replaced certificate is left to the GC: a handshake may still use it.
                _entries[siteId] = new Entry(names, caId, rsa, made);
            }

            return made;
        }
        finally
        {
            _making.Release();
        }
    }

    /// <summary>This server's addresses (unicast, network cards that are up, no loopback or IPv6 link-local).</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up || card.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in card.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (IPAddress.IsLoopback(ip) || (ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6LinkLocal || IsTemporary(unicast))))
                    {
                        continue;
                    }

                    if (ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !addresses.Contains(ip.ToString()))
                    {
                        addresses.Add(ip.ToString());
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // no card list: the host name still names the server
        }

        return addresses;
    }

    /// <summary>A temporary (privacy) IPv6 address changes every day: it would make a new certificate each time.</summary>
    private static bool IsTemporary(UnicastIPAddressInformation unicast)
    {
        try
        {
            return OperatingSystem.IsWindows() && unicast.SuffixOrigin == SuffixOrigin.Random;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _making.Dispose();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Certificate.Dispose();
            }

            _entries.Clear();
        }
    }

    /// <summary>The validity and the key (RSA or ECC) of the policy; 90 days and ECC without one.</summary>
    private static (TimeSpan Validity, bool Rsa) Wanted(Oadm.Plugins.Pki.Policies.PkiPolicy? policy) =>
        policy is { ValidDays: > 0 and var days }
            ? (TimeSpan.FromDays(days), policy.Key == Oadm.Plugins.Pki.Policies.PolicyKeys.Rsa)
            : (DefaultValidity, false);

    /// <summary>The certificate made for these names by this CA with this key still has more than its renewal time left.</summary>
    private bool Fits(Entry entry, string names, string caId, bool rsa)
    {
        var certificate = entry.Certificate;
        var lifetime = certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime();
        return entry.Names == names && entry.CaId == caId && entry.Rsa == rsa
            && certificate.NotAfter.ToUniversalTime() - _time.GetUtcNow().UtcDateTime > Oadm.Plugins.Pki.Policies.PolicyLimits.RenewBefore(lifetime);
    }

    private static async Task<X509Certificate2> MakeAsync(
        IDeviceCertificateAuthority ca, IReadOnlyCollection<string> addresses, string? hostName, TimeSpan validity, bool rsa, CancellationToken ct)
    {
        using var rsaKey = rsa ? RSA.Create(2048) : null;
        using var ecKey = rsa ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var commonName = !string.IsNullOrWhiteSpace(hostName) ? hostName : addresses.FirstOrDefault() ?? "OADM EST server";
        var request = rsaKey is not null
            ? new CertificateRequest("CN=" + commonName, rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest("CN=" + commonName, ecKey!, HashAlgorithmName.SHA256);
        var csr = request.CreateSigningRequest();
        var dns = string.IsNullOrWhiteSpace(hostName) ? Array.Empty<string>() : [hostName];
        var issued = await ca.SignAsync(new DeviceCertificateRequest(csr, commonName, [.. addresses], dns, DeviceCertificateUse.Server) { Validity = validity, Source = "EST" }, ct)
            .ConfigureAwait(false);
        using var publicOnly = X509CertificateLoader.LoadCertificate(issued.Certificate);
        using var withKey = rsaKey is not null ? publicOnly.CopyWithPrivateKey(rsaKey) : publicOnly.CopyWithPrivateKey(ecKey!);
        // Through PKCS#12, so Windows TLS can use the key (an in-memory key fails there).
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private sealed record Entry(string Names, string CaId, bool Rsa, X509Certificate2 Certificate);
}
