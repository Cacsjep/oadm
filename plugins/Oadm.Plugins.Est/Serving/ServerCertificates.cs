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
/// listener picks the certificate by the site the connection came through. A certificate is made again when its addresses
/// change, the CA changes or a third of its time is left. Kept in memory only (the key never leaves the server).
/// </summary>
public sealed class ServerCertificates(Func<IDeviceCertificateAuthority?> authority, TimeProvider? time = null) : IDisposable
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(90);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

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
        lock (_gate)
        {
            if (_entries.TryGetValue(siteId, out var entry) && entry.Names == names && entry.CaId == caId
                && entry.Certificate.NotAfter.ToUniversalTime() - _time.GetUtcNow().UtcDateTime > Validity / 3)
            {
                return entry.Certificate;
            }
        }

        var made = await MakeAsync(ca, addresses, hostName, ct).ConfigureAwait(false);
        lock (_gate)
        {
            // The replaced certificate is left to the GC: a handshake may still use it.
            _entries[siteId] = new Entry(names, caId, made);
        }

        return made;
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
                    if (IPAddress.IsLoopback(ip) || (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal))
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

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Certificate.Dispose();
            }

            _entries.Clear();
        }
    }

    private static async Task<X509Certificate2> MakeAsync(IDeviceCertificateAuthority ca, IReadOnlyCollection<string> addresses, string? hostName, CancellationToken ct)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var commonName = !string.IsNullOrWhiteSpace(hostName) ? hostName : addresses.FirstOrDefault() ?? "OADM EST server";
        var csr = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256).CreateSigningRequest();
        var dns = string.IsNullOrWhiteSpace(hostName) ? Array.Empty<string>() : [hostName];
        var issued = await ca.SignAsync(new DeviceCertificateRequest(csr, commonName, [.. addresses], dns, DeviceCertificateUse.Server) { Validity = Validity, Source = "EST" }, ct)
            .ConfigureAwait(false);
        using var publicOnly = X509CertificateLoader.LoadCertificate(issued.Certificate);
        using var withKey = publicOnly.CopyWithPrivateKey(key);
        // Through PKCS#12, so Windows TLS can use the key (an in-memory key fails there).
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private sealed record Entry(string Names, string CaId, X509Certificate2 Certificate);
}
