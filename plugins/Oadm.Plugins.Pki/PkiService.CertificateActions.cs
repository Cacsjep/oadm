using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki;

/// <summary>
/// The actions of the Certificates card (docs/spec/pki.md "Certificates"): Create (OADM makes the key and the certificate
/// from an issuing policy), Sign request (a PKCS#10 request of another system; its key never leaves it), Import (a
/// certificate, or a PKCS#12 file with its key), Delete (only certificates nothing uses) and Export (PEM, DER; PKCS#12 only
/// where OADM holds the key, never a CA key). Keys are encrypted with the master key; passwords are never stored or logged.
/// The plugin checks the caller (Admin with "Manage the certificate authority" and all devices); the host audits.
/// </summary>
public sealed partial class PkiService
{
    private const string OidServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string OidClientAuth = "1.3.6.1.5.5.7.3.2";

    /// <summary>OADM makes a key and a certificate with an issuing policy (HTTPS or IEEE 802.1X); the key is kept encrypted.</summary>
    internal async Task<CertificateActionReply> CreateCertificateAsync(CreateCertificateRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var use = CertificateRules.Uses.Contains(request.Use ?? string.Empty, StringComparer.Ordinal) ? request.Use : null;
        if (use is null)
        {
            errors[CertificateFields.Use] = CertificateRules.ChooseUse;
        }

        var policy = PolicyFor(request.PolicyId, use is null ? null : CertificateRules.PolicyKindOf(use), errors);
        Add(errors, CertificateFields.Name, CertificateRules.Name(request.Name));
        var (ips, dns, addressError) = CertificateRules.ParseAddresses(request.Addresses, use);
        Add(errors, CertificateFields.Addresses, addressError);
        if (errors.Count > 0)
        {
            return new CertificateActionReply { Errors = errors };
        }

        if (_ca is not { HasKey: true } ca)
        {
            return new CertificateActionReply { Error = CertificateRules.NoCa };
        }

        if (!_local.CanKeepKeys)
        {
            return new CertificateActionReply { Error = CertificateRules.NoKeyStore };
        }

        var name = request.Name!.Trim();
        using var rsa = policy!.Key == PolicyKeys.Rsa ? RSA.Create(2048) : null;
        using var ec = rsa is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var csr = rsa is not null
            ? new CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest("CN=x", ec!, HashAlgorithmName.SHA256);
        X509Certificate2 signed;
        try
        {
            signed = DeviceCertificateIssuer.Sign(ca, csr.CreateSigningRequestPem(), new CertificateNames(name, ips, dns), use!, Now, DeviceCertificateValidity(policy.ValidDays));
        }
        catch (CertificateRequestRejectedException ex)
        {
            return new CertificateActionReply { Error = ex.Message };
        }

        var pkcs8 = rsa?.ExportPkcs8PrivateKey() ?? ec!.ExportPkcs8PrivateKey();
        try
        {
            using (signed)
            {
                var (row, error) = await _local.AddAsync(new StoredLocalCertificate
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = ListedCertificateKind.Created,
                    Name = name,
                    Purpose = use!,
                    Der = Convert.ToBase64String(signed.RawData),
                    IssuerFingerprint = ca.Id,
                    PolicyId = policy.Id,
                    AddedUtc = Now.UtcDateTime,
                    AddedBy = userName,
                }, pkcs8, ct).ConfigureAwait(false);
                if (row is null)
                {
                    return new CertificateActionReply { Error = error };
                }

                LogCertificateCreated(name, policy.Name, userName);
                return new CertificateActionReply { Id = row.RowId, Name = name };
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>The OADM CA signs a request (PKCS#10, PEM or DER) with an issuing policy; names from the request, checked first.</summary>
    internal async Task<CertificateActionReply> SignCertificateAsync(SignCertificateRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var policy = PolicyFor(request.PolicyId, null, errors);
        if (!TryDecode(request.FileBase64, out var bytes) || bytes.Length == 0)
        {
            errors[CertificateFields.File] = "Choose a file.";
        }
        else if (bytes.Length > PkiPluginInfo.MaxImportBytes)
        {
            errors[CertificateFields.File] = "The file is larger than 1 MB.";
        }

        if (errors.Count > 0)
        {
            return new CertificateActionReply { Errors = errors };
        }

        var read = ReadSigningRequest(bytes, policy!);
        if (read.Error is { } problem)
        {
            return new CertificateActionReply { Errors = new Dictionary<string, string>(StringComparer.Ordinal) { [CertificateFields.File] = problem } };
        }

        if (_ca is not { HasKey: true } ca)
        {
            return new CertificateActionReply { Error = CertificateRules.NoCa };
        }

        var use = CertificateRules.UseOf(policy!.Kind)!;
        X509Certificate2 signed;
        try
        {
            var pem = new string(PemEncoding.Write("CERTIFICATE REQUEST", read.Der));
            signed = DeviceCertificateIssuer.Sign(ca, pem, read.Names!, use, Now, DeviceCertificateValidity(policy.ValidDays));
        }
        catch (CertificateRequestRejectedException ex)
        {
            return new CertificateActionReply { Errors = new Dictionary<string, string>(StringComparer.Ordinal) { [CertificateFields.File] = ex.Message } };
        }

        using (signed)
        {
            var name = read.Names!.CommonName;
            var (row, error) = await _local.AddAsync(new StoredLocalCertificate
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = ListedCertificateKind.SignedRequest,
                Name = name,
                Purpose = use,
                Der = Convert.ToBase64String(signed.RawData),
                IssuerFingerprint = ca.Id,
                PolicyId = policy.Id,
                AddedUtc = Now.UtcDateTime,
                AddedBy = userName,
            }, null, ct).ConfigureAwait(false);
            if (row is null)
            {
                return new CertificateActionReply { Error = error };
            }

            LogCertificateSigned(name, policy.Name, userName);
            return new CertificateActionReply { Id = row.RowId, Name = name };
        }
    }

    /// <summary>
    /// Reads and checks a certificate request: PEM ("CERTIFICATE REQUEST") or DER, its signature, a digest of SHA-256 or
    /// stronger, an RSA (2,048 bits or more) or EC (P-256, P-384, P-521) key of the policy's kind, and its names (the common
    /// name and the host names and IP addresses it asks for; at least one, at most 20).
    /// </summary>
    internal static (byte[] Der, CertificateNames? Names, string? Error) ReadSigningRequest(byte[] bytes, PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(policy);
        byte[] der = bytes;
        if (CaCertificates.LooksLikePem(bytes))
        {
            var text = Encoding.ASCII.GetString(bytes);
            der = [];
            var rest = text.AsSpan();
            while (PemEncoding.TryFind(rest, out var fields))
            {
                var label = rest[fields.Label];
                if (label.SequenceEqual("CERTIFICATE REQUEST") || label.SequenceEqual("NEW CERTIFICATE REQUEST"))
                {
                    der = Convert.FromBase64String(rest[fields.Base64Data].ToString());
                    break;
                }

                rest = rest[fields.Location.End..];
            }

            if (der.Length == 0)
            {
                return ([], null, "The file is no certificate request (PKCS#10).");
            }
        }

        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException)
        {
            return (der, null, "The request cannot be read or its signature is wrong.");
        }

        try
        {
            PkiCertificateAuthority.CheckSignatureAlgorithm(der);
        }
        catch (CertificateRequestRefusedException ex)
        {
            return (der, null, ex.Message);
        }

        if (KeyProblem(csr.PublicKey, policy) is { } keyProblem)
        {
            return (der, null, keyProblem);
        }

        var ips = new List<string>();
        var dns = new List<string>();
        foreach (var san in csr.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            foreach (var ip in san.EnumerateIPAddresses())
            {
                if (!ips.Contains(ip.ToString()))
                {
                    ips.Add(ip.ToString());
                }
            }

            foreach (var name in san.EnumerateDnsNames())
            {
                if (!CertificateNames.IsPlainHostName(name))
                {
                    return (der, null, $"The request asks for \"{name}\", which is no host name.");
                }

                if (CertificateRules.IsCameraName(name))
                {
                    return (der, null, $"The request asks for \"{name}\", which belongs to an Axis camera.");
                }

                if (!dns.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    dns.Add(name);
                }
            }
        }

        if (ips.Count + dns.Count > CertificateRules.MaxAddresses)
        {
            return (der, null, string.Create(CultureInfo.InvariantCulture, $"The request asks for more than {CertificateRules.MaxAddresses} addresses."));
        }

        var commonName = CommonNameOf(csr.SubjectName) ?? dns.FirstOrDefault() ?? ips.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(commonName))
        {
            return (der, null, "The request names no host name or IP address.");
        }

        if (CertificateRules.Name(commonName) is { } nameProblem)
        {
            return (der, null, "The name of the request: " + nameProblem);
        }

        if (ips.Count + dns.Count == 0 && policy.Kind == PolicyKinds.HttpsCertificate)
        {
            return (der, null, "The request names no host name or IP address. An HTTPS certificate needs one.");
        }

        return (der, new CertificateNames(commonName.Trim(), ips, dns), null);
    }

    /// <summary>
    /// Imports a certificate (PEM or DER, one certificate) or a PKCS#12 file with its password (one certificate with its key;
    /// the key is kept encrypted). CA certificates belong in the Certificate authorities card.
    /// </summary>
    internal async Task<CertificateActionReply> ImportCertificateAsync(ImportCertificateRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryDecode(request.FileBase64, out var bytes) || bytes.Length == 0)
        {
            return FileError("Choose a file.");
        }

        if (bytes.Length > PkiPluginInfo.MaxImportBytes)
        {
            return FileError("The file is larger than 1 MB.");
        }

        X509Certificate2? certificate = null;
        byte[]? pkcs8 = null;
        try
        {
            if (CaCertificates.LooksLikePem(bytes))
            {
                if (Encoding.ASCII.GetString(bytes).Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                {
                    return FileError("The file contains a private key. Import a .pfx or .p12 file to keep a key.");
                }

                List<X509Certificate2> all;
                try
                {
                    all = CaCertificates.ReadPemCertificates(Encoding.ASCII.GetString(bytes));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    all = [];
                }

                if (all.Count != 1)
                {
                    foreach (var extra in all)
                    {
                        extra.Dispose();
                    }

                    return FileError(all.Count == 0 ? "The file is not a certificate (.crt, .cer, .pem, .pfx or .p12)."
                        : string.Create(CultureInfo.InvariantCulture, $"The file holds {all.Count} certificates. Import one at a time."));
                }

                certificate = all[0];
            }
            else if (TryLoadDer(bytes) is { } der)
            {
                certificate = der;
            }
            else
            {
                var (leaf, key, problem) = ReadPkcs12(bytes, request.Password);
                if (problem is not null)
                {
                    return problem == CertificateFiles.WrongPassword
                        ? new CertificateActionReply { Errors = new Dictionary<string, string>(StringComparer.Ordinal) { [CertificateFields.Password] = problem } }
                        : FileError(problem);
                }

                (certificate, pkcs8) = (leaf, key);
            }

            using (certificate)
            {
                if (certificate!.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true })
                {
                    return FileError("This is a CA certificate. Use Import CA in Certificate authorities.");
                }

                if (certificate.NotAfter.ToUniversalTime() <= Now.UtcDateTime)
                {
                    return FileError("This certificate has expired.");
                }

                if (pkcs8 is not null && !_local.CanKeepKeys)
                {
                    return new CertificateActionReply { Error = CertificateRules.NoKeyStore };
                }

                var fingerprint = CaCertificates.Fingerprint(certificate);
                var issuers = Issuers();
                if (issuers.Source.Any(a => a.Certificates.Any(c => Convert.ToHexString(SHA256.HashData(c)) == fingerprint))
                    || _issued.Find(certificate.SerialNumber) is not null || _local.Servers.Any(s => s.Fingerprint == fingerprint))
                {
                    return FileError(CertificateRules.AlreadyListed);
                }

                var name = CaCertificates.CommonName(certificate);
                var (row, error) = await _local.AddAsync(new StoredLocalCertificate
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = ListedCertificateKind.Imported,
                    Name = name,
                    Purpose = UseOfImported(certificate),
                    Der = Convert.ToBase64String(certificate.RawData),
                    IssuerFingerprint = IssuerIndex.FingerprintOfIssuer(certificate, issuers.Source.SelectMany(a => a.Certificates)),
                    AddedUtc = Now.UtcDateTime,
                    AddedBy = userName,
                }, pkcs8, ct).ConfigureAwait(false);
                if (row is null)
                {
                    return error == CertificateRules.AlreadyListed ? FileError(error) : new CertificateActionReply { Error = error };
                }

                LogCertificateImported(name, pkcs8 is not null, userName);
                return new CertificateActionReply { Id = row.RowId, Name = name };
            }
        }
        finally
        {
            if (pkcs8 is not null)
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }

        static CertificateActionReply FileError(string message) =>
            new() { Errors = new Dictionary<string, string>(StringComparer.Ordinal) { [CertificateFields.File] = message } };
    }

    /// <summary>
    /// Deletes rows nothing uses: device certificates and certificates created or signed here that are revoked or expired,
    /// imported certificates, ended server certificates. All or nothing: the first row that cannot go says why. Revocations stay.
    /// </summary>
    internal async Task<DeleteCertificatesReply> DeleteCertificatesAsync(DeleteCertificatesRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.Ids?.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (ids.Count == 0)
        {
            return new DeleteCertificatesReply { Error = PkiCertificateTexts.ChooseOne };
        }

        if (ids.Count > PkiPluginInfo.MaxRevokeAtOnce)
        {
            return new DeleteCertificatesReply { Error = string.Create(CultureInfo.InvariantCulture, $"Choose at most {PkiPluginInfo.MaxRevokeAtOnce:N0} certificates.") };
        }

        var now = Now.UtcDateTime;
        var serials = new List<string>();
        var locals = new List<string>();
        var servers = new List<string>();
        Dictionary<Guid, string>? addresses = null;
        foreach (var id in ids)
        {
            if (id.StartsWith(AuthorityRowPrefix, StringComparison.Ordinal))
            {
                return Refused(CertificateRules.RemoveCaThere);
            }

            if (id.StartsWith(LocalCertificates.ServerPrefix, StringComparison.Ordinal))
            {
                if (_local.FindServer(id[LocalCertificates.ServerPrefix.Length..]) is { } server)
                {
                    if (server.NotAfterUtc > now && !_issued.IsRevoked(server.Serial))
                    {
                        return Refused(CertificateRules.UsedBy(server.Server));
                    }

                    servers.Add(server.Slot);
                }

                continue;
            }

            if (id.StartsWith(LocalCertificates.RowPrefix, StringComparison.Ordinal))
            {
                // The OADM CA issued created and signed ones: still valid, they would be accepted without a trace. Revoke first.
                var localId = id[LocalCertificates.RowPrefix.Length..];
                if (_local.Find(localId) is { Stored.Kind: ListedCertificateKind.Created or ListedCertificateKind.SignedRequest } local
                    && local.NotAfterUtc > now && !_issued.IsRevoked(local.Serial))
                {
                    return Refused(CertificateRules.StillValid(local.Stored.Name));
                }

                locals.Add(localId);
                continue;
            }

            if (_issued.Find(id) is { } entry)
            {
                if (!_issued.IsRevoked(entry.SerialNumber) && entry.NotAfterUtc > now)
                {
                    addresses ??= (await SystemDevicesAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id, d => d.Address);
                    return Refused(CertificateRules.StillValid(addresses.GetValueOrDefault(entry.DeviceId) ?? entry.DeviceSerial ?? IssuedCertificateTextsUnknown));
                }

                serials.Add(entry.SerialNumber);
            }
        }

        var deleted = await _issued.DeleteEndedAsync(serials, ct).ConfigureAwait(false)
            + await _local.DeleteAsync(locals, ct).ConfigureAwait(false)
            + _local.DeleteServers(servers);
        if (deleted > 0)
        {
            LogCertificatesDeleted(deleted, userName);
        }

        return new DeleteCertificatesReply { Deleted = deleted };

        static DeleteCertificatesReply Refused(string why) => new() { Error = why + CertificateRules.NothingDeleted };
    }

    private const string IssuedCertificateTextsUnknown = "this device";

    /// <summary>
    /// The file of Export: PEM or DER of any listed certificate; PKCS#12 (with the issuing CA, AES-256, the password of the
    /// request) only of certificates OADM holds the key of. Refusals are <see cref="InvalidOperationException"/> with the plain
    /// reason. The audit entry names the certificate and the format, never the password.
    /// </summary>
    internal async Task<PluginDownload> OpenCertificateDownloadAsync(DownloadCertificateRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = request.Format?.ToLowerInvariant();
        if (format is not (CertificateFormats.Pem or CertificateFormats.Der or CertificateFormats.Pkcs12))
        {
            throw new InvalidOperationException(CertificateRules.ChooseFormat);
        }

        var id = request.Id ?? string.Empty;
        string name;
        byte[] der;
        LocalCertificate? local = null;
        var kind = ListedCertificateKind.Device;
        if (ResolveRow(id) is { } row)
        {
            (name, der, local, kind) = (row.Name, row.Der ?? throw new InvalidOperationException(CertificateRules.NotListed), row.Local, row.Kind);
        }
        else if (_issued.Find(id) is { } entry && await _issued.GetDerAsync(entry.SerialNumber, ct).ConfigureAwait(false) is { } deviceDer)
        {
            name = (entry.DeviceSerial ?? CertificateSerials.Normalize(entry.SerialNumber)) + "-" + entry.Purpose;
            der = deviceDer;
        }
        else
        {
            throw new InvalidOperationException(_issued.Find(id) is null ? CertificateRules.NotListed : PkiCertificateTexts.NoCopy);
        }

        byte[] bytes;
        string extension;
        string formatText;
        switch (format)
        {
            case CertificateFormats.Pem:
                bytes = Encoding.ASCII.GetBytes(new string(PemEncoding.Write("CERTIFICATE", der)) + "\n");
                (extension, formatText) = (".crt", "PEM");
                break;
            case CertificateFormats.Der:
                bytes = der;
                (extension, formatText) = (".cer", "DER");
                break;
            default:
                bytes = Pkcs12Of(local, kind, der, request.Password);
                (extension, formatText) = (".p12", "PKCS#12 with its key");
                LogCertificateKeyExported(name, userName);
                break;
        }

        return new PluginDownload(new MemoryStream(bytes, writable: false), CaCertificates.FileNameFor(name, extension), bytes.Length)
        {
            AuditDetail = $"Export certificate {name} ({formatText})",
        };
    }

    /// <summary>The PKCS#12 of a certificate created or imported here with its key and its issuing CA.</summary>
    private byte[] Pkcs12Of(LocalCertificate? local, ListedCertificateKind kind, byte[] der, string? password)
    {
        if (local is not { HasKey: true })
        {
            throw new InvalidOperationException(kind == ListedCertificateKind.Authority ? CertificateRules.NoCaKey : CertificateRules.NoKeyHere);
        }

        if (PkiValidation.BackupPassword(password) is { } passwordProblem)
        {
            throw new InvalidOperationException(passwordProblem);
        }

        var key = _local.KeyOf(local) ?? throw new InvalidOperationException("The key of this certificate cannot be read (another master key).");
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            using var withKey = WithKey(certificate, key);
            var collection = new X509Certificate2Collection { withKey };
            var issuers = new List<X509Certificate2>();
            if (local.Stored.IssuerFingerprint is { } issuerId && Issuers().Source.SelectMany(a => a.Certificates)
                    .FirstOrDefault(c => Convert.ToHexString(SHA256.HashData(c)) == issuerId) is { } issuerDer)
            {
                issuers.Add(X509CertificateLoader.LoadCertificate(issuerDer));
            }

            try
            {
                foreach (var issuer in issuers)
                {
                    collection.Add(issuer);
                }

                return collection.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000), password);
            }
            finally
            {
                foreach (var issuer in issuers)
                {
                    issuer.Dispose();
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The DER and serial of a row that is no device certificate (null: no such prefix; Der null: not listed).</summary>
    private (byte[]? Der, string? Serial)? CertificateRowDer(string? id) => ResolveRow(id ?? string.Empty) is { } row ? (row.Der, row.Serial) : null;

    /// <summary>A row by its id (<c>ca:</c>, <c>local:</c>, <c>server:</c>); null for a serial number.</summary>
    private RowRef? ResolveRow(string id)
    {
        if (id.StartsWith(AuthorityRowPrefix, StringComparison.Ordinal))
        {
            var der = AuthorityDer(id);
            return new RowRef(ListedCertificateKind.Authority, NameOf(der) ?? "CA", der, null, null);
        }

        if (id.StartsWith(LocalCertificates.RowPrefix, StringComparison.Ordinal))
        {
            var local = _local.Find(id[LocalCertificates.RowPrefix.Length..]);
            return new RowRef(local?.Stored.Kind ?? ListedCertificateKind.Imported, local?.Stored.Name ?? string.Empty, local?.Der, local?.Serial, local);
        }

        if (id.StartsWith(LocalCertificates.ServerPrefix, StringComparison.Ordinal))
        {
            var server = _local.FindServer(id[LocalCertificates.ServerPrefix.Length..]);
            return new RowRef(ListedCertificateKind.Server, server?.Name ?? string.Empty, server?.Der, server?.Serial, null);
        }

        return null;

        static string? NameOf(byte[]? der)
        {
            if (der is null)
            {
                return null;
            }

            using var certificate = X509CertificateLoader.LoadCertificate(der);
            return CaCertificates.CommonName(certificate);
        }
    }

    /// <summary>An issuing policy (HTTPS or IEEE 802.1X certificate; of <paramref name="kind"/> when given); else the error under Policy.</summary>
    private PkiPolicy? PolicyFor(Guid id, string? kind, Dictionary<string, string> errors)
    {
        var policy = Policies.Find(id);
        if (policy is null || CertificateRules.UseOf(policy.Kind) is null || (kind is not null && policy.Kind != kind))
        {
            errors[CertificateFields.Policy] = id != Guid.Empty && policy is null ? CertificateRules.PolicyGone : CertificateRules.ChoosePolicy;
            return null;
        }

        return policy;
    }

    /// <summary>A weak or unsupported key, or one of another kind than the policy's; null when it fits.</summary>
    private static string? KeyProblem(PublicKey key, PkiPolicy policy)
    {
        using (var rsa = key.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                if (rsa.KeySize < 2048)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"The request has a weak key (RSA {rsa.KeySize}). At least RSA 2048 is needed.");
                }

                return policy.Key == PolicyKeys.Ecc ? $"The request has an RSA key. The policy {policy.Name} uses ECC." : null;
            }
        }

        using var ec = key.GetECDsaPublicKey();
        if (ec is null)
        {
            return "The request has a key type OADM does not support. Use RSA or ECC.";
        }

        string? curve;
        try
        {
            curve = ec.ExportParameters(false).Curve.Oid?.Value;
        }
        catch (CryptographicException)
        {
            curve = null;
        }

        if (curve is not ("1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35"))
        {
            return "The request has an ECC key on a curve OADM does not support. Use P-256 or larger.";
        }

        return policy.Key == PolicyKeys.Rsa ? $"The request has an ECC key. The policy {policy.Name} uses RSA." : null;
    }

    private static string? CommonNameOf(X500DistinguishedName name)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().Value == "2.5.4.3")
            {
                return rdn.GetSingleElementValue();
            }
        }

        return null;
    }

    /// <summary>HTTPS for a server certificate, IEEE 802.1X for a client certificate, else Other.</summary>
    private static string UseOfImported(X509Certificate2 certificate)
    {
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>()).Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
        return usages.Contains(OidServerAuth) ? DeviceCertificatePurposes.Https
            : usages.Contains(OidClientAuth) ? DeviceCertificatePurposes.Dot1x
            : ListedCertificateUses.Other;
    }

    private static X509Certificate2? TryLoadDer(byte[] bytes)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The one certificate with a key of a PKCS#12 file and its key (PKCS#8; the caller clears it), or the problem.</summary>
    private static (X509Certificate2? Leaf, byte[]? Key, string? Error) ReadPkcs12(byte[] bytes, string? password)
    {
        X509Certificate2Collection collection;
        try
        {
            // Keys never touch the OS key store; macOS does not support ephemeral keys.
            var flags = X509KeyStorageFlags.Exportable | (OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            collection = X509CertificateLoader.LoadPkcs12Collection(bytes, password ?? string.Empty, flags);
        }
        catch (CryptographicException)
        {
            return (null, null, CertificateFiles.WrongPassword);
        }

        try
        {
            var leaves = collection.Where(c => c.HasPrivateKey).ToList();
            if (leaves.Count != 1)
            {
                return (null, null, leaves.Count == 0 ? "The file holds no certificate with its private key." : "The file contains more than one certificate with a private key.");
            }

            var leaf = leaves[0];
            byte[]? key;
            using (var rsa = leaf.GetRSAPrivateKey())
            {
                using var ec = rsa is null ? leaf.GetECDsaPrivateKey() : null;
                key = rsa?.ExportPkcs8PrivateKey() ?? ec?.ExportPkcs8PrivateKey();
            }

            if (key is null)
            {
                return (null, null, "The key type of this file is not supported. Use RSA or ECC.");
            }

            return (X509CertificateLoader.LoadCertificate(leaf.RawData), key, null);
        }
        catch (CryptographicException)
        {
            return (null, null, "The key of this file cannot be read.");
        }
        finally
        {
            foreach (var certificate in collection)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>The certificate with its PKCS#8 key (RSA or ECC).</summary>
    private static X509Certificate2 WithKey(X509Certificate2 certificate, byte[] pkcs8)
    {
        using (var rsaPublic = certificate.GetRSAPublicKey())
        {
            if (rsaPublic is not null)
            {
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(pkcs8, out _);
                return certificate.CopyWithPrivateKey(rsa);
            }
        }

        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(pkcs8, out _);
        return certificate.CopyWithPrivateKey(ec);
    }

    /// <summary>A row that is no device certificate.</summary>
    private sealed record RowRef(ListedCertificateKind Kind, string Name, byte[]? Der, string? Serial, LocalCertificate? Local);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: certificate {Name} created with the policy {Policy} by {User}")]
    private partial void LogCertificateCreated(string name, string policy, string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: certificate request {Name} signed with the policy {Policy} by {User}")]
    private partial void LogCertificateSigned(string name, string policy, string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: certificate {Name} imported (with key: {WithKey}) by {User}")]
    private partial void LogCertificateImported(string name, bool withKey, string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: {Count} certificates deleted from the list by {User}")]
    private partial void LogCertificatesDeleted(int count, string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: certificate {Name} exported with its key by {User}")]
    private partial void LogCertificateKeyExported(string name, string user);
}
