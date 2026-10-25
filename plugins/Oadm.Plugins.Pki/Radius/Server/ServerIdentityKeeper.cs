using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The 802.1X server's own certificate (docs/spec/radius-server.md "Server certificate", decision 2026-10-25): the
/// certificate PKI's 802.1X server policy picks from the Certificates list, with its key (ECDSA: the cipher suites follow
/// it). Never made or renewed here. Without one (Turn on, the first start after the update) PKI gives the policy one: the
/// certificate this server kept before (imported with its key, so cameras keep working), else a new one from the OADM CA for
/// the listen addresses and the host name, valid 10 years. A new pick applies to the next handshake.
/// </summary>
public sealed partial class ServerIdentityKeeper(RadiusStore store, Func<IDeviceCertificateAuthority?> authority, ISecretProtector? secrets, ILogger logger) : IDisposable
{
    /// <summary>The key of the server certificate PKI makes (ECDSA P-256): the cipher suites that can be negotiated follow it.</summary>
    public const ServerKeyKind KeyKind = ServerKeyKind.Ecdsa;
    private const string KeyPurpose = "radius:server-key";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Current? _current;

    /// <summary>The identity for the TLS server; null without a usable certificate (<see cref="Problem"/> says why).</summary>
    public TlsServerIdentity? Identity => _current?.Identity;

    /// <summary>Why there is no identity ("The 802.1X server has no certificate. Choose one in its policy."); null with one.</summary>
    public string? Problem { get; private set; }

    /// <summary>The certificate for the page; null without one.</summary>
    public ServerCertificateInfo? Info => _current is { } current ? new ServerCertificateInfo(current.NotAfterUtc, current.Names) : null;

    /// <summary>
    /// Loads the certificate the 802.1X server policy picks (the first time: makes sure the policy has one, for
    /// <paramref name="names"/>); returns true when the identity changed.
    /// </summary>
    public async Task<bool> EnsureAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (authority() is not Oadm.Plugins.Pki.Ca.IServerCertificateSource pki)
            {
                Problem = null;
                return Replace(null);
            }

            // Nothing when the policy has a certificate; else the one kept so far, or a new one (the OADM CA may come later).
            await pki.EnsureServerCertificateAsync(Oadm.Plugins.Pki.Policies.PolicyKinds.Dot1xServer, names,
                async token => Kept(await store.ServerCertificateAsync(token).ConfigureAwait(false)), ct).ConfigureAwait(false);

            var load = pki.ServerCertificate(Oadm.Plugins.Pki.Policies.PolicyKinds.Dot1xServer);
            Problem = load.Problem;
            if (load.Material is not { } material)
            {
                return Replace(null);
            }

            try
            {
                if (_current is { } current && current.Id == material.Id && current.Leaf.AsSpan().SequenceEqual(material.Leaf))
                {
                    return false;
                }

                var identity = TlsServerIdentity.Create([material.Leaf, .. material.IssuerChain], material.Pkcs8);
                var changed = Replace(new Current(material.Id, identity, material.NotAfterUtc, material.Names, material.Leaf));
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var joined = string.Join(", ", material.Names);
                    LogLoaded(material.NotAfterUtc, joined);
                }
                return changed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.Pkcs8);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Replace(Current? next)
    {
        var changed = !ReferenceEquals(_current, next) && (_current is not null || next is not null);
        _current = next;
        return changed;
    }

    /// <summary>The certificate this server kept before 2026-10-25 with its key; null when there is none or it cannot be read.</summary>
    private Oadm.Plugins.Pki.ServerCertificateImport? Kept(StoredServerCertificate? stored)
    {
        if (stored is null || secrets is null || stored.Chain.Count < 2 || string.IsNullOrEmpty(stored.KeyProtected))
        {
            return null;
        }

        try
        {
            var chain = stored.Chain.Select(Convert.FromBase64String).ToList();
            var pkcs8 = Convert.FromBase64String(secrets.Unprotect(stored.KeyProtected, KeyPurpose));
            return new Oadm.Plugins.Pki.ServerCertificateImport(chain[0], chain[1], pkcs8);
        }
#pragma warning disable CA1031 // A damaged or foreign value (another master key after a restore) only means a new certificate.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "802.1X server: shows the certificate of its policy, valid until {NotAfter:yyyy-MM-dd} ({Names})")]
    private partial void LogLoaded(DateTime notAfter, string names);

    private sealed record Current(string Id, TlsServerIdentity Identity, DateTime NotAfterUtc, IReadOnlyList<string> Names, byte[] Leaf);
}
