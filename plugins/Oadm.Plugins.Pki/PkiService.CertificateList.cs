using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki;

/// <summary>
/// The Certificates card (docs/spec/pki.md "Certificates"): every certificate OADM knows in one paged list. First the CAs
/// (in the order of the Certificate authorities card), then the certificates of the 802.1X and EST servers, then the ones
/// created, signed or imported here (newest first), then the device certificates the OADM CA issued (newest first). The
/// first three are a few hundred rows at most and built per request; the device certificates are one pass over the issued
/// registry (60,000 rows at 30,000 devices). Users limited to a device group see only their devices' certificates.
/// </summary>
public sealed partial class PkiService
{
    /// <summary>The "Issued by" choice of certificates no CA of PKI issued.</summary>
    public const string OtherIssuerId = "other";

    public const string OtherIssuerName = "Other CA";

    private const string AuthorityRowPrefix = "ca:";

    /// <summary>The issuer name of a device certificate whose OADM CA has left the list of CAs.</summary>
    private const string FormerOadmCa = "OADM CA";

    private IssuerIndex? _issuerIndex;

    /// <summary>The certificates created, signed or imported here and the servers' certificates.</summary>
    public LocalCertificates Local => _local;

    /// <summary>
    /// A server made or loaded its certificate (<see cref="ServerCertificateSources"/>), issued by the CA with the SHA-256
    /// <paramref name="issuerFingerprint"/>: it is listed as used by that server.
    /// </summary>
    public void NoteServerCertificate(string? source, byte[] der, string? issuerFingerprint) =>
        _local.NoteServer(source ?? ServerCertificateSources.Dot1x, der, issuerFingerprint);

    /// <summary>
    /// One page for the page: <paramref name="devices"/> are the caller's devices; <paramref name="onlyListed"/> for a user
    /// limited to a group (only certificates of the group's devices: no CAs, server or local certificates).
    /// </summary>
    internal CertificatesReply GetCertificates(CertificatesRequest request, IReadOnlyList<IDeviceInfo> devices, bool onlyListed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(devices);
        var byId = new Dictionary<Guid, IDeviceInfo>(devices.Count);
        foreach (var device in devices)
        {
            byId[device.Id] = device;
        }

        var limit = Math.Clamp(request.Limit, 1, PkiPluginInfo.CertificatesPageSize);
        var issuers = Issuers();
        var query = new IssuedCertificateQuery
        {
            State = request.State is { } state && Enum.IsDefined(state) ? state : null,
            Purpose = ListedCertificateUses.All.Contains(request.Purpose ?? string.Empty, StringComparer.Ordinal) ? request.Purpose : null,
            Source = IssuedCertificateSources.All.Contains(request.Source ?? string.Empty, StringComparer.Ordinal) ? request.Source : null,
            IssuedBy = request.IssuedBy is { Length: > 0 } issuedBy && (issuedBy == OtherIssuerId || issuers.Choices.Any(c => c.Id == issuedBy)) ? issuedBy : null,
            Search = request.Search is { Length: > 100 } text ? text[..100] : request.Search,
            Offset = Math.Max(0, request.Offset),
            Limit = limit,
        };
        var warningDays = WarningDaysFor([.. byId.Values]);
        var extras = onlyListed ? [] : ExtraRows(query, issuers);
        var page = DevicePage(query, byId, onlyListed, warningDays, issuers, extras.Count);
        if (page.Rows.Count == 0 && page.Total > 0 && query.Offset > 0 && query.Offset >= page.Total)
        {
            // Fewer rows than before (a filter, revoked or deleted rows): the last page.
            query = query with { Offset = (page.Total - 1) / limit * limit };
            page = DevicePage(query, byId, onlyListed, warningDays, issuers, extras.Count);
        }

        // The days follow each device's Expiry warning policy: no one number for the page (the window follows the row's state).
        return new CertificatesReply
        {
            Rows = page.Rows,
            Total = page.Total,
            Offset = query.Offset,
            Issuers = onlyListed ? [.. issuers.Choices.Where(c => c.Id == CertificateAuthorityInfo.OadmId || c.Id.StartsWith(PreviousPrefix, StringComparison.Ordinal))] : issuers.Choices,
        };

        IssuedCertificatePage DevicePage(IssuedCertificateQuery q, Dictionary<Guid, IDeviceInfo> known, bool only, Func<Guid, int> days, IssuerIndex index, int extraCount)
        {
            var rows = new List<IssuedCertificateInfo>(q.Limit);
            var offset = q.Offset;
            if (offset < extraCount)
            {
                rows.AddRange(extras.Skip(offset).Take(q.Limit).Select(e => e.Row));
            }

            var deviceQuery = q with { Offset = Math.Max(0, offset - extraCount), Limit = Math.Max(1, q.Limit - rows.Count) };
            var devicePage = q.Purpose is ListedCertificateUses.Server or ListedCertificateUses.Ca or ListedCertificateUses.Other
                ? IssuedCertificatePage.Empty
                : _issued.Query(deviceQuery, known, only, days, DeviceFilter(q.IssuedBy, index), DeviceSearch(q.Search, index));
            if (rows.Count < q.Limit)
            {
                rows.AddRange(devicePage.Rows.Take(q.Limit - rows.Count).Select(r => r with { IssuedBy = index.Of(CaIdOf(r.SerialNumber)).Name }));
            }

            return new IssuedCertificatePage(rows, extraCount + devicePage.Total);
        }
    }

    /// <summary>The CA (SHA-256) of a listed device certificate.</summary>
    private string? CaIdOf(string serial) => _issued.Find(serial)?.CaId;

    /// <summary>The device certificates of the "Issued by" choice; null for every issuer.</summary>
    private static Func<IssuedCertificate, bool>? DeviceFilter(string? issuedBy, IssuerIndex index) =>
        issuedBy is null ? null : entry => index.Of(entry.CaId).Id == issuedBy;

    /// <summary>The search in the columns the registry does not know: use, issued by, valid until, state.</summary>
    private static Func<IssuedCertificate, IssuedCertificateState, bool>? DeviceSearch(string? search, IssuerIndex index)
    {
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return (entry, state) =>
            ListedCertificateUses.Text(entry.Purpose).Contains(text, StringComparison.OrdinalIgnoreCase)
            || index.Of(entry.CaId).Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || IssuedCertificateTexts(state).Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal);
    }

    /// <summary>The state names of the page (the same as the shared list's).</summary>
    private static string IssuedCertificateTexts(IssuedCertificateState state) => state switch
    {
        IssuedCertificateState.ExpiresSoon => "Expires soon",
        IssuedCertificateState.Expired => "Expired",
        IssuedCertificateState.Replaced => "Replaced",
        IssuedCertificateState.Revoked => "Revoked",
        _ => "Valid",
    };

    /// <summary>The rows before the device certificates that match the query (filters and search), in the list's order.</summary>
    private List<ExtraRow> ExtraRows(IssuedCertificateQuery query, IssuerIndex issuers)
    {
        if (query.Source is not null)
        {
            return []; // Task and EST name device certificates only
        }

        var now = Now.UtcDateTime;
        var days = Policies.DefaultExpiryDays;
        var all = new List<ExtraRow>();
        foreach (var ca in issuers.AuthorityRows)
        {
            all.Add(Extra(ca.Row with { State = StateOf(ca.Row.NotAfterUtc, revoked: null, now, days) }, ca.IssuerId));
        }

        foreach (var server in _local.Servers)
        {
            var issuer = issuers.Of(server.IssuerFingerprint);
            all.Add(Extra(new IssuedCertificateInfo
            {
                Id = server.RowId,
                SerialNumber = server.Serial,
                Kind = ListedCertificateKind.Server,
                Name = server.Name,
                Purpose = ListedCertificateUses.Server,
                IssuedBy = issuer.Id == OtherIssuerId ? server.IssuerName : issuer.Name,
                UsedBy = server.Server,
                NotBeforeUtc = server.NotBeforeUtc,
                NotAfterUtc = server.NotAfterUtc,
                IssuedUtc = server.NotBeforeUtc,
                State = StateOf(server.NotAfterUtc, _issued.FindRevocation(server.Serial), now, days),
                Revocation = _issued.FindRevocation(server.Serial),
            }, issuer.Id));
        }

        foreach (var local in _local.List)
        {
            var issuer = issuers.Of(local.Stored.IssuerFingerprint);
            var revocation = local.Stored.Kind == ListedCertificateKind.Imported ? null : _issued.FindRevocation(local.Serial);
            all.Add(Extra(new IssuedCertificateInfo
            {
                Id = local.RowId,
                SerialNumber = local.Serial,
                Kind = local.Stored.Kind,
                Name = local.Stored.Name,
                Purpose = local.Stored.Purpose,
                IssuedBy = issuer.Id == OtherIssuerId ? local.IssuerName : issuer.Name,
                NotBeforeUtc = local.NotBeforeUtc,
                NotAfterUtc = local.NotAfterUtc,
                IssuedUtc = local.Stored.AddedUtc,
                State = StateOf(local.NotAfterUtc, revocation, now, days),
                Revocation = revocation,
                HasKey = local.HasKey,
            }, issuer.Id));
        }

        var search = query.Search?.Trim() ?? string.Empty;
        var searchHex = search.Length == 0 ? string.Empty : CertificateSerials.Normalize(search);
        return
        [
            .. all.Where(e => (query.State is not { } wanted || e.Row.State == wanted)
                && (query.Purpose is not { } use || e.Row.Purpose == use)
                && (query.IssuedBy is not { } by || e.IssuerId == by)
                && (search.Length == 0 || e.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Row.SerialNumber.Contains(searchHex, StringComparison.Ordinal))),
        ];

        static ExtraRow Extra(IssuedCertificateInfo row, string issuerId) => new(row, issuerId, string.Join('\n',
            row.Name, ListedCertificateUses.Text(row.Purpose), row.IssuedBy, row.UsedBy, IssuedCertificateTexts(row.State),
            row.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    private static IssuedCertificateState StateOf(DateTime notAfterUtc, CertificateRevocation? revoked, DateTime nowUtc, int warningDays) =>
        revoked is not null ? IssuedCertificateState.Revoked
        : notAfterUtc <= nowUtc ? IssuedCertificateState.Expired
        : notAfterUtc <= nowUtc.AddDays(warningDays) ? IssuedCertificateState.ExpiresSoon
        : IssuedCertificateState.Valid;

    /// <summary>The CAs by fingerprint, the "Issued by" choices and the CA rows; built again when the list of CAs changes.</summary>
    private IssuerIndex Issuers()
    {
        var authorities = Authorities();
        if (Volatile.Read(ref _issuerIndex) is { } cached && ReferenceEquals(cached.Source, authorities))
        {
            return cached;
        }

        var index = IssuerIndex.Build(authorities);
        Volatile.Write(ref _issuerIndex, index);
        return index;
    }

    /// <summary>The DER of a CA row (<c>ca:&lt;SHA-256&gt;</c>); null when it is no longer listed.</summary>
    private byte[]? AuthorityDer(string rowId) =>
        Issuers().AuthorityRows.FirstOrDefault(r => r.Row.Id == rowId)?.Der;

    /// <summary>One row before the device certificates: the row, its issuer's id and the texts the search looks in.</summary>
    private sealed record ExtraRow(IssuedCertificateInfo Row, string IssuerId, string Text);

    /// <summary>A CA row of the list with its issuer and its certificate.</summary>
    private sealed record AuthorityListRow(IssuedCertificateInfo Row, string IssuerId, byte[] Der);

    /// <summary>The CAs of PKI by the SHA-256 of each of their certificates.</summary>
    private sealed class IssuerIndex
    {
        private readonly Dictionary<string, (string Id, string Name)> _byFingerprint = new(StringComparer.OrdinalIgnoreCase);

        private IssuerIndex(IReadOnlyList<CertificateAuthorityInfo> source) => Source = source;

        public IReadOnlyList<CertificateAuthorityInfo> Source { get; }

        /// <summary>"Issued by" choices: every CA of the list once, then "Other CA".</summary>
        public List<CertificateIssuerChoice> Choices { get; } = [];

        public List<AuthorityListRow> AuthorityRows { get; } = [];

        /// <summary>The CA of a certificate's issuer fingerprint; "other" when PKI does not list it.</summary>
        public (string Id, string Name) Of(string? fingerprint) =>
            fingerprint is not null && _byFingerprint.TryGetValue(fingerprint, out var known) ? known : (OtherIssuerId, FormerOadmCa);

        /// <summary>The CA that issued <paramref name="certificate"/> (subject and signature), or null.</summary>
        public static string? FingerprintOfIssuer(X509Certificate2 certificate, IEnumerable<byte[]> candidates)
        {
            foreach (var der in candidates)
            {
                using var issuer = X509CertificateLoader.LoadCertificate(der);
                if (certificate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData) && SignatureOk(certificate, issuer))
                {
                    return CaCertificates.Fingerprint(issuer);
                }
            }

            return null;
        }

        public static IssuerIndex Build(IReadOnlyList<CertificateAuthorityInfo> authorities)
        {
            var index = new IssuerIndex(authorities);
            foreach (var authority in authorities)
            {
                foreach (var der in authority.Certificates)
                {
                    index._byFingerprint.TryAdd(Convert.ToHexString(SHA256.HashData(der)), (authority.Id, authority.Name));
                }

                index.Choices.Add(new CertificateIssuerChoice(authority.Id, authority.Name));
            }

            index.Choices.Add(new CertificateIssuerChoice(OtherIssuerId, OtherIssuerName));
            var all = authorities.SelectMany(a => a.Certificates).ToList();
            foreach (var authority in authorities)
            {
                // The CA itself (the certificates above an OADM CA are its issuers, not rows); every Axis device ID CA.
                var rows = authority.Kind == CertificateAuthorityKind.AxisDeviceId ? authority.Certificates : authority.Certificates.Take(1);
                foreach (var der in rows)
                {
                    using var certificate = X509CertificateLoader.LoadCertificate(der);
                    var fingerprint = CaCertificates.Fingerprint(certificate);
                    var self = CaCertificates.IsSelfIssued(certificate);
                    var issuerFingerprint = self ? fingerprint : FingerprintOfIssuer(certificate, all);
                    var issuer = index.Of(issuerFingerprint);
                    index.AuthorityRows.Add(new AuthorityListRow(new IssuedCertificateInfo
                    {
                        Id = AuthorityRowPrefix + fingerprint,
                        SerialNumber = CertificateSerials.Normalize(certificate.SerialNumber),
                        Kind = ListedCertificateKind.Authority,
                        Name = CaCertificates.CommonName(certificate),
                        Purpose = ListedCertificateUses.Ca,
                        IssuedBy = issuer.Id == OtherIssuerId ? LocalCertificate.IssuerCommonName(certificate) : issuer.Name,
                        NotBeforeUtc = certificate.NotBefore.ToUniversalTime(),
                        NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
                        IssuedUtc = certificate.NotBefore.ToUniversalTime(),
                    }, issuer.Id, der));
                }
            }

            return index;
        }

        private static bool SignatureOk(X509Certificate2 certificate, X509Certificate2 issuer)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(issuer);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.AllowUnknownCertificateAuthority;
            try
            {
                return chain.Build(certificate) && chain.ChainElements.Count >= 2 && chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(issuer.RawData);
            }
            catch (CryptographicException)
            {
                return false;
            }
            finally
            {
                foreach (var element in chain.ChainElements)
                {
                    if (!ReferenceEquals(element.Certificate, certificate))
                    {
                        element.Certificate.Dispose();
                    }
                }
            }
        }
    }
}
