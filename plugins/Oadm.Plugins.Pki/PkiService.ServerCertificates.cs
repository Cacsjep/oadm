using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki;

/// <summary>
/// The certificate an OADM server (802.1X, EST) shows, read from the Certificates list (decision 2026-10-25): the leaf, the
/// chain of its issuer when PKI knows it, the key (PKCS#8; the caller clears it), its end and names.
/// </summary>
public sealed record ServerCertificateMaterial(string Id, byte[] Leaf, IReadOnlyList<byte[]> IssuerChain, byte[] Pkcs8, DateTime NotAfterUtc, IReadOnlyList<string> Names);

/// <summary>The certificate of a server policy: the material, or why the server has none (the server's status).</summary>
public sealed record ServerCertificateLoad(ServerCertificateMaterial? Material, string? Problem);

/// <summary>A certificate a server made and kept before 2026-10-25 (the 802.1X server's): imported once with its key.</summary>
public sealed record ServerCertificateImport(byte[] Leaf, byte[] CaCertificate, byte[] Pkcs8);

/// <summary>
/// The server policies' certificates (docs/spec/pki.md "Server certificates", decision 2026-10-25): the 802.1X and EST
/// server policies pick a certificate of the Certificates list with a key OADM holds; Create of the policy (and Turn on, the
/// first start after the update, when none is chosen) makes one: ECDSA P-256, 10 years (never past the CA's end), kept with
/// its key. Never renewed by itself: the Expiry warning and the server's status remind.
/// </summary>
public sealed partial class PkiService
{
    private readonly SemaphoreSlim _serverCertificateGate = new(1, 1);
    private ServerChoiceCache? _serverChoices; // one reference: never a torn read of its fields across threads
    private int _revocations; // counts the changes of the revocation list (the cache of the choices follows it)

    /// <summary>"The 802.1X server" or "The EST server".</summary>
    private static string ServerText(string kind) => kind == PolicyKinds.Dot1xServer ? "The 802.1X server" : "The EST server";

    /// <summary>
    /// The certificates of the list with a key OADM holds, each with what it names; ended ones and ones that are no server
    /// certificate say why. Cached per list, day and revocation list (the store reads it for every summary).
    /// </summary>
    internal IReadOnlyList<PolicyCertificateChoice> ServerCertificateChoices()
    {
        var revocations = Volatile.Read(ref _revocations); // read first: a revocation while this runs makes the next call build again
        var list = _local.List;
        var now = Now.UtcDateTime;
        if (Volatile.Read(ref _serverChoices) is { } cached && ReferenceEquals(cached.List, list) && cached.Day == now.Date && cached.Revocations == revocations)
        {
            return cached.Choices;
        }

        var choices = new List<PolicyCertificateChoice>();
        foreach (var local in list.Where(l => l.HasKey))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificate(local.Der);
                var problem = local.NotAfterUtc <= now ? "This certificate has ended. Choose another one."
                    : _issued.IsRevoked(local.Serial) ? "This certificate is revoked. Choose another one."
                    : !IsServerCertificate(certificate) ? "This is no server certificate. Choose another one."
                    : null;
                choices.Add(new PolicyCertificateChoice(local.Stored.Id, local.Stored.Name, local.IssuerName, local.NotAfterUtc,
                    certificate.GetECDsaPublicKey() is { } ec && Dispose(ec), NamesOf(certificate), problem));
            }
            catch (CryptographicException)
            {
                // a damaged row is no choice
            }
        }

        Volatile.Write(ref _serverChoices, new ServerChoiceCache(list, now.Date, revocations, choices));
        return choices;

        static bool Dispose(IDisposable key)
        {
            key.Dispose();
            return true;
        }
    }

    /// <summary>A certificate for TLS servers: no key usage limits, or server authentication among them.</summary>
    private static bool IsServerCertificate(X509Certificate2 certificate)
    {
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true })
        {
            return false;
        }

        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usages is null || usages.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == OidServerAuth);
    }

    /// <summary>The IP addresses and host names of a certificate (its subject alternative names).</summary>
    internal static IReadOnlyList<string> NamesOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault() is not { } san)
        {
            return [];
        }

        return [.. san.EnumerateIPAddresses().Select(ip => ip.ToString()), .. san.EnumerateDnsNames()];
    }

    /// <summary>The name of the certificate OADM makes for both servers.</summary>
    public const string SharedServerCertificateName = "802.1X and EST server";

    private async Task<(string? Id, string? Error)> MakeServerCertificateAsync(string name, CaMaterial ca, IReadOnlyList<string> ips, IReadOnlyList<string> dns, string userName, CancellationToken ct)
    {
        if (!_local.CanKeepKeys)
        {
            return (null, CertificateRules.NoKeyStore);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256);
        X509Certificate2 signed;
        try
        {
            signed = DeviceCertificateIssuer.Sign(ca, csr.CreateSigningRequestPem(), new CertificateNames(name, ips, dns), DeviceCertificatePurposes.Https, Now,
                DeviceCertificateValidity(PolicyTexts.ServerCertificateDays, ca));
        }
        catch (CertificateRequestRejectedException ex)
        {
            return (null, ex.Message);
        }

        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            using (signed)
            {
                var (row, error) = await _local.AddAsync(new StoredLocalCertificate
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = ListedCertificateKind.Created,
                    Name = name,
                    Purpose = ListedCertificateUses.Server,
                    Der = Convert.ToBase64String(signed.RawData),
                    IssuerFingerprint = ca.Id,
                    AddedUtc = Now.UtcDateTime,
                    AddedBy = userName,
                }, pkcs8, ct).ConfigureAwait(false);
                if (row is null)
                {
                    return (null, error);
                }

                LogServerCertificateCreated(name, userName);
                return (row.Stored.Id, null);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// Turn on, and the first start after the update: the server policy of <paramref name="kind"/> without a certificate gets
    /// <paramref name="existing"/> (the certificate the server kept so far, imported with its key so cameras keep working),
    /// else the shared one (<see cref="EnsureServerCertificatesAsync"/>). Nothing when the policy has one.
    /// </summary>
    internal async Task EnsureServerCertificateAsync(string kind, IReadOnlyList<string> names, Func<CancellationToken, Task<ServerCertificateImport?>>? existing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (Policies.IsUnreadable || Policies.FirstOf(kind) is not { CertificateId: null })
        {
            return;
        }

        if (existing is not null)
        {
            await _serverCertificateGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Policies.FirstOf(kind) is not { CertificateId: null })
                {
                    return; // another start was quicker
                }

                if (await existing(ct).ConfigureAwait(false) is { } kept)
                {
                    string? id;
                    try
                    {
                        id = await ImportServerCertificateAsync(kind, kept, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(kept.Pkcs8);
                    }

                    if (id is not null)
                    {
                        await Policies.SetServerCertificateAsync(kind, id, ct).ConfigureAwait(false);
                        return;
                    }
                }
            }
            finally
            {
                _serverCertificateGate.Release();
            }
        }

        await EnsureServerCertificatesAsync(names, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// OADM PRO became active (and every start with it), Turn on of a server (decision 2026-10-25): when a server policy has no
    /// certificate, one certificate for both servers ("802.1X and EST server": ECDSA P-256, 10 years, from the OADM CA) naming
    /// <paramref name="names"/>, the names both servers give and this computer's addresses and name, set on every server
    /// policy without one. Nothing when both have one, the list cannot be read or the OADM CA cannot issue yet (the next
    /// start tries again).
    /// </summary>
    internal async Task EnsureServerCertificatesAsync(IReadOnlyList<string>? names, CancellationToken ct)
    {
        if (Policies.IsUnreadable || !Missing())
        {
            return;
        }

        await _serverCertificateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Missing() || ServerIssuingCa() is not { } oadm)
            {
                return; // another start was quicker, or no CA can issue yet
            }

            List<string> all = [.. names ?? [], .. Policies.Dot1xServerNames?.Invoke() ?? [], .. Policies.EstServerNames?.Invoke() ?? [],
                .. Oadm.Plugins.Est.Serving.ServerCertificates.LocalAddresses(), Environment.MachineName];
            var ips = all.Where(n => IPAddress.TryParse(n, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dns = all.Where(n => !IPAddress.TryParse(n, out _) && CertificateNames.IsPlainHostName(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var (id, error) = await MakeServerCertificateAsync(SharedServerCertificateName, oadm, ips, dns, "OADM", ct).ConfigureAwait(false);
            if (id is null)
            {
                LogServerCertificateFailed(SharedServerCertificateName, error ?? string.Empty);
                return;
            }

            foreach (var kind in new[] { PolicyKinds.Dot1xServer, PolicyKinds.EstServer })
            {
                await Policies.SetServerCertificateAsync(kind, id, ct).ConfigureAwait(false); // only where none is set
            }
        }
        finally
        {
            _serverCertificateGate.Release();
        }

        bool Missing() => Policies.FirstOf(PolicyKinds.Dot1xServer) is { CertificateId: null } || Policies.FirstOf(PolicyKinds.EstServer) is { CertificateId: null };
    }

    /// <summary>
    /// The CA of a new shared server certificate: the CA a server policy names when it can issue (the first-start root CA by
    /// default), else the first CA that can issue (decision 2026-10-26: that CA may have been deleted).
    /// </summary>
    private CaMaterial? ServerIssuingCa() =>
        IssuingCa(Policies.FirstOf(PolicyKinds.Dot1xServer)?.CaId ?? Policies.FirstOf(PolicyKinds.EstServer)?.CaId)
        ?? IssuingCa(Policies.FirstOf(PolicyKinds.EstServer)?.CaId)
        ?? (IssuingCas() is { Count: > 0 } all ? all[0] : null);

    private async Task<string?> ImportServerCertificateAsync(string kind, ServerCertificateImport existing, CancellationToken ct)
    {
        try
        {
            using var leaf = X509CertificateLoader.LoadCertificate(existing.Leaf);
            if (leaf.NotAfter.ToUniversalTime() <= Now.UtcDateTime)
            {
                return null;
            }

            var fingerprint = CaCertificates.Fingerprint(leaf);
            if (_local.List.FirstOrDefault(l => l.Fingerprint == fingerprint) is { HasKey: true } listed)
            {
                return listed.Stored.Id;
            }

            var issuer = Convert.ToHexString(SHA256.HashData(existing.CaCertificate));
            var (row, _) = await _local.AddAsync(new StoredLocalCertificate
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = ListedCertificateKind.Created,
                Name = PolicyKinds.Name(kind),
                Purpose = ListedCertificateUses.Server,
                Der = Convert.ToBase64String(existing.Leaf),
                IssuerFingerprint = issuer,
                AddedUtc = Now.UtcDateTime,
                AddedBy = "OADM",
            }, existing.Pkcs8, ct).ConfigureAwait(false);
            return row?.Stored.Id;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The certificate the server of <paramref name="kind"/> shows now, or why it has none (its status says so).</summary>
    internal ServerCertificateLoad ServerCertificateOf(string kind)
    {
        var server = ServerText(kind);
        if (Policies.FirstOf(kind)?.CertificateId is not { } id)
        {
            return new(null, PolicyTexts.ServerNoCertificate(server));
        }

        if (_local.Find(id) is not { } local)
        {
            return new(null, PolicyTexts.ServerCertificateUnusable(server, "it is no longer in the Certificates list."));
        }

        if (local.NotAfterUtc <= Now.UtcDateTime)
        {
            return new(null, PolicyTexts.ServerCertificateUnusable(server, "it has ended."));
        }

        if (_issued.IsRevoked(local.Serial))
        {
            return new(null, PolicyTexts.ServerCertificateUnusable(server, "it is revoked."));
        }

        if (_local.KeyOf(local) is not { } pkcs8)
        {
            return new(null, PolicyTexts.ServerCertificateUnusable(server, "its key cannot be read."));
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(local.Der);
            return new(new ServerCertificateMaterial(id, local.Der, IssuerChainOf(local.Stored.IssuerFingerprint), pkcs8, local.NotAfterUtc, NamesOf(certificate)), null);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            return new(null, PolicyTexts.ServerCertificateUnusable(server, "it cannot be read."));
        }
    }

    /// <summary>The issuer of a listed certificate and the certificates above it (DER, the issuer first); empty when PKI does not know it.</summary>
    internal IReadOnlyList<byte[]> IssuerChainOf(string? issuerFingerprint)
    {
        if (string.IsNullOrEmpty(issuerFingerprint))
        {
            return [];
        }

        foreach (var authority in Authorities())
        {
            for (var i = 0; i < authority.Certificates.Count; i++)
            {
                if (string.Equals(Convert.ToHexString(SHA256.HashData(authority.Certificates[i])), issuerFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return [.. authority.Certificates.Skip(i)];
                }
            }
        }

        return [];
    }

    /// <summary>The issuer chain of the certificate the server of <paramref name="kind"/> shows (what cameras must trust); empty without one.</summary>
    internal IReadOnlyList<byte[]> ServerIssuerChain(string kind) =>
        Policies.FirstOf(kind)?.CertificateId is { } id && _local.Find(id) is { } local ? IssuerChainOf(local.Stored.IssuerFingerprint) : [];

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: {Server} certificate created by {User}")]
    private partial void LogServerCertificateCreated(string server, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: no certificate for the {Server}: {Error}")]
    private partial void LogServerCertificateFailed(string server, string error);

    /// <summary>The certificate for both servers could not be made (OADM PRO became active): the servers try again on Turn on.</summary>
    internal void LogServerCertificatesFailed(Exception ex) => LogServerCertificateFailed(SharedServerCertificateName, ex.Message);

    private void OnRevocationChanged(object? sender, EventArgs e) => Interlocked.Increment(ref _revocations);

    /// <summary><see cref="ServerCertificateChoices"/> of one list, day and revocation list.</summary>
    private sealed record ServerChoiceCache(IReadOnlyList<LocalCertificate> List, DateTime Day, int Revocations, IReadOnlyList<PolicyCertificateChoice> Choices);
}
