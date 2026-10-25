using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// The EST server's own TLS certificate (decision 2026-10-25): the certificate PKI's EST server policy picks from the
/// Certificates list, with its key, shown on this server's network and at every site (cameras dial an address, there is no
/// SNI; the certificate names this server and the site nodes). Never made or renewed here: without one (Turn on, the first
/// start after the update) PKI gives the policy one from the OADM CA for <see cref="Names"/>, valid 10 years. Loaded again
/// when the policy picks another one.
/// </summary>
public sealed class ServerCertificates(Func<IDeviceCertificateAuthority?> authority) : IDisposable
{
    private const string Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.EstServer;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _ensuring = new(1, 1);
    private Entry? _entry;

    /// <summary>The names a new certificate gets: this server's addresses and host name, the site nodes' addresses.</summary>
    public Func<IReadOnlyList<string>> Names { get; init; } = () => [];

    /// <summary>The id of the certificate the EST server policy picks now (a cheap check per connection); null without one.</summary>
    public Func<string?> ChosenId { get; init; } = () => null;

    /// <summary>Why there is no certificate (the server's status); null with one, or without a CA (the status says that).</summary>
    public string? Problem { get; private set; }

    /// <summary>"EST server (OADM CA), until 2035-10-25" and the names of the certificate shown now; null without one.</summary>
    public (string Label, IReadOnlyList<string> Names)? Shown { get; private set; }

    /// <summary>The certificate (with its key) of the EST server policy; null without a usable one (<see cref="Problem"/> says why).</summary>
    public async Task<X509Certificate2?> GetAsync(CancellationToken ct)
    {
        if (authority() is not Oadm.Plugins.Pki.Ca.IServerCertificateSource pki)
        {
            Problem = null;
            return null;
        }

        // The certificate read before, while the policy still picks it and it has not ended: no key read per connection.
        if (ChosenId() is { } chosen)
        {
            lock (_gate)
            {
                if (_entry is { } known && known.Id == chosen && known.Certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
                {
                    return known.Certificate;
                }
            }
        }

        // Nothing when the policy has a certificate; else a new one (the OADM CA may come later: the next connection tries).
        await _ensuring.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await pki.EnsureServerCertificateAsync(Kind, Names(), null, ct).ConfigureAwait(false);
        }
        finally
        {
            _ensuring.Release();
        }

        var load = pki.ServerCertificate(Kind);
        Problem = load.Problem;
        if (load.Material is not { } material)
        {
            Shown = null;
            return null;
        }

        try
        {
            lock (_gate)
            {
                if (_entry is { } entry && entry.Id == material.Id && entry.Certificate.RawData.AsSpan().SequenceEqual(material.Leaf))
                {
                    return entry.Certificate;
                }
            }

            var made = WithKey(material);
            lock (_gate)
            {
                // The replaced certificate is left to the GC: a handshake may still use it.
                _entry = new Entry(material.Id, made);
            }

            Shown = (Label(made), material.Names);
            return made;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.Pkcs8);
        }
    }

    /// <summary>"EST server (OADM CA), until 2035-10-25".</summary>
    private static string Label(X509Certificate2 certificate) =>
        $"{certificate.GetNameInfo(X509NameType.SimpleName, false)} ({certificate.GetNameInfo(X509NameType.SimpleName, true)}), until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}";

    private static X509Certificate2 WithKey(Oadm.Plugins.Pki.ServerCertificateMaterial material)
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(material.Leaf);
        using var key = publicOnly.GetECDsaPublicKey() is { } ecPublic ? Ecdsa(ecPublic, material.Pkcs8) : null;
        using var rsa = key is null ? RSA.Create() : null;
        rsa?.ImportPkcs8PrivateKey(material.Pkcs8, out _);
        using var withKey = key is not null ? publicOnly.CopyWithPrivateKey(key) : publicOnly.CopyWithPrivateKey(rsa!);
        // Through PKCS#12, so Windows TLS can use the key (an in-memory key fails there).
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);

        static ECDsa Ecdsa(ECDsa ecPublic, byte[] pkcs8)
        {
            ecPublic.Dispose();
            var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(pkcs8, out _);
            return ec;
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

    /// <summary>A temporary (privacy) IPv6 address changes every day: it does not belong in a certificate.</summary>
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

    /// <summary>Reads the certificate again at the next connection (the policies or the Certificates list changed).</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _entry = null; // left to the GC: a handshake may still use it
        }
    }

    public void Dispose()
    {
        _ensuring.Dispose();
        lock (_gate)
        {
            _entry?.Certificate.Dispose();
            _entry = null;
        }
    }

    private sealed record Entry(string Id, X509Certificate2 Certificate);
}
