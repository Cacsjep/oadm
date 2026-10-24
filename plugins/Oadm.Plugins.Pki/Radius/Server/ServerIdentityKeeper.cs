using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The 802.1X server's own certificate (docs/spec/radius-server.md "Server certificate"): issued by the OADM CA (PKI's
/// <see cref="IDeviceCertificateAuthority"/>) for the listen addresses and the server's host name, ECDSA P-256, valid for
/// <see cref="Validity"/>, kept in the plugin settings with its key encrypted (<see cref="ISecretProtector"/>; without a
/// protector in memory only) and made again 30 days before it ends, when the CA or the names change. The cameras trust it
/// through the PKI task "IEEE 802.1X" (they get the OADM CA). A new one applies to the next handshake.
/// </summary>
public sealed partial class ServerIdentityKeeper(RadiusStore store, Func<IDeviceCertificateAuthority?> authority, ISecretProtector? secrets, TimeProvider time, ILogger logger) : IDisposable
{
    /// <summary>Without PKI's policy: 365 days (as before the policies).</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(Oadm.Plugins.Pki.Policies.PolicyLimits.DefaultValidDays);
    public static readonly TimeSpan RenewBefore = Oadm.Plugins.Pki.Policies.PolicyLimits.ServerRenewBefore;

    /// <summary>
    /// PKI's Server certificate policy of the 802.1X server: its valid days for the next certificate (a change of the days
    /// alone makes no new one; a new CA does). Null: 365 days. The key stays ECDSA P-256 (the policy allows ECC only).
    /// </summary>
    public Func<Oadm.Plugins.Pki.Policies.PkiPolicy?> Policy { get; init; } = () => null;

    /// <summary>The key of every server certificate made here (ECDSA P-256): the cipher suites that can be negotiated follow it.</summary>
    public const ServerKeyKind KeyKind = ServerKeyKind.Ecdsa;
    private const string KeyPurpose = "radius:server-key";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Current? _current;
    private bool _loaded;

    /// <summary>The identity for the TLS server; null without a CA.</summary>
    public TlsServerIdentity? Identity => _current?.Identity;

    /// <summary>The certificate for the page; null without one.</summary>
    public ServerCertificateInfo? Info => _current is { } current ? new ServerCertificateInfo(current.NotAfterUtc, current.Names) : null;

    /// <summary>
    /// Makes sure a certificate of the current CA for <paramref name="names"/> (IP addresses and host names) exists with more
    /// than 30 days left; returns true when the identity changed (a new one, or none: the CA is gone).
    /// </summary>
    public async Task<bool> EnsureAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                _current = Load(await store.ServerCertificateAsync(ct).ConfigureAwait(false));
                if (_current is { } loaded && authority() is Oadm.Plugins.Pki.Ca.PkiCertificateAuthority pki)
                {
                    pki.NoteServerCertificate(loaded.Leaf, loaded.CaCertificate); // PKI's Certificates list shows it as used by this server
                }
            }

            if (authority() is not { IsReady: true } ca || ca.Chain.Count == 0)
            {
                var had = _current is not null;
                _current = null; // no CA: no handshake (the status says so); the stored one waits for the CA to come back
                return had;
            }

            var wanted = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            // Names that only went away (a card or cable down, an address gone) keep the certificate: only a new name, a new
            // CA or the end near makes a new one, so the certificate does not change with every link.
            if (_current is { } current && current.CaCertificate.AsSpan().SequenceEqual(ca.Chain[0])
                && wanted.All(n => current.Names.Contains(n, StringComparer.OrdinalIgnoreCase))
                && current.NotAfterUtc - time.GetUtcNow().UtcDateTime > Oadm.Plugins.Pki.Policies.PolicyLimits.RenewBefore(current.Lifetime))
            {
                return false;
            }

            var made = await MakeAsync(ca, wanted, ct).ConfigureAwait(false);
            _current = made.Current;
            await store.SaveServerCertificateAsync(made.Stored, ct).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
            {
                var joined = string.Join(", ", wanted);
                LogIssued(made.Current.NotAfterUtc, joined);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(Current Current, StoredServerCertificate? Stored)> MakeAsync(IDeviceCertificateAuthority ca, IReadOnlyList<string> names, CancellationToken ct)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ips = names.Where(n => System.Net.IPAddress.TryParse(n, out _)).ToList();
        var dns = names.Where(n => !System.Net.IPAddress.TryParse(n, out _)).ToList();
        var commonName = dns.FirstOrDefault() ?? ips.FirstOrDefault() ?? "OADM 802.1X server";
        var csr = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256).CreateSigningRequest();
        var validity = Policy() is { ValidDays: > 0 and var days } ? TimeSpan.FromDays(days) : Validity;
        var issued = await ca.SignAsync(new DeviceCertificateRequest(csr, commonName, ips, dns, DeviceCertificateUse.Server)
            { Validity = validity, Source = Oadm.Plugins.Pki.Certificates.ServerCertificateSources.Dot1x }, ct)
            .ConfigureAwait(false);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            List<byte[]> chain = [issued.Certificate, .. issued.Chain];
            var identity = TlsServerIdentity.Create(chain, pkcs8);
            using var leaf = X509CertificateLoader.LoadCertificate(issued.Certificate);
            var current = new Current(identity, issued.NotAfterUtc, names, issued.Chain.Count > 0 ? issued.Chain[0] : [], leaf.NotAfter.ToUniversalTime() - leaf.NotBefore.ToUniversalTime(), issued.Certificate);
            var stored = secrets is null ? null : new StoredServerCertificate
            {
                Chain = [.. chain.Select(Convert.ToBase64String)],
                KeyProtected = secrets.Protect(Convert.ToBase64String(pkcs8), KeyPurpose),
                Names = names,
            };
            return (current, stored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>The stored certificate; null when there is none, no protector or it cannot be read (a new one is made).</summary>
    private Current? Load(StoredServerCertificate? stored)
    {
        if (stored is null || secrets is null || stored.Chain.Count < 2 || string.IsNullOrEmpty(stored.KeyProtected))
        {
            return null;
        }

        byte[]? pkcs8 = null;
        try
        {
            var chain = stored.Chain.Select(Convert.FromBase64String).ToList();
            pkcs8 = Convert.FromBase64String(secrets.Unprotect(stored.KeyProtected, KeyPurpose));
            using var leaf = X509CertificateLoader.LoadCertificate(chain[0]);
            return new Current(TlsServerIdentity.Create(chain, pkcs8), leaf.NotAfter.ToUniversalTime(), [.. stored.Names], chain[1], leaf.NotAfter.ToUniversalTime() - leaf.NotBefore.ToUniversalTime(), chain[0]);
        }
#pragma warning disable CA1031 // A damaged or foreign value (another master key after a restore) only means a new certificate.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
        finally
        {
            if (pkcs8 is not null)
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server: new server certificate from the OADM CA, valid until {NotAfter:yyyy-MM-dd} ({Names})")]
    private partial void LogIssued(DateTime notAfter, string names);

    private sealed record Current(TlsServerIdentity Identity, DateTime NotAfterUtc, IReadOnlyList<string> Names, byte[] CaCertificate, TimeSpan Lifetime, byte[] Leaf);
}
