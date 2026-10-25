using Microsoft.Extensions.Logging;

using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Server;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.Pki;

/// <summary>
/// The policies of the page (docs/spec/pki.md "Policies"): the store, PKI's own conversion (warn before expiry -> Expiry
/// warning "All devices"), the page methods and Revoke (the IEEE 802.1X certificates of a revoke policy's devices are
/// revoked on save and every minute: new ones too). Revoke is PKI's own and never needs OADM PRO.
/// </summary>
public sealed partial class PkiService
{
    private static readonly TimeSpan RevokeRound = TimeSpan.FromMinutes(1);
    private readonly ISites? _sites;
    private ITimer? _revokeTimer;
    private int _revokeRunning;
    private string? _expiryKey; // the Expiry warning policies the host was last told

    /// <summary>The policies (one store; the 802.1X server inside PKI reads it in process).</summary>
    public PolicyStore Policies { get; }

    /// <summary>The cipher suites of the 802.1X server policy.</summary>
    public static IReadOnlyList<PolicyCipherSuite> CipherSuites => RadiusPolicies.CipherSuites;

    /// <summary>
    /// PKI's settings from before the policies (docs/spec/pki.md "Converting existing settings"): the Device certificates
    /// card's "Warn before expiry" becomes the Expiry warning policy "All devices" (30 days on a new server).
    /// </summary>
    public static IReadOnlyList<PkiPolicy> ConvertOwnSettings(PkiConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var days = config.ExpiryWarningDays is >= PolicyLimits.MinExpiryDays and <= PolicyLimits.MaxExpiryDays ? config.ExpiryWarningDays : PolicyLimits.DefaultExpiryDays;
        return [new PkiPolicy { Name = PolicyLimits.AllDevicesName, Kind = PolicyKinds.ExpiryWarning, Days = days, Devices = PolicyDevices.Everything }];
    }

    /// <summary>The owner of the conversion of stage 4 (the issuing policies), apart from stage 3's <see cref="PkiPluginInfo.PluginId"/>.</summary>
    public const string IssuingOwner = PkiPluginInfo.PluginId + ":issuing";

    /// <summary>
    /// PKI's settings from before the issuing policies (docs/spec/pki.md "Converting existing settings"): the Device
    /// certificates card's validity becomes the policy "HTTPS", it and the IEEE 802.1X card (EAPOL version, EAP identity,
    /// RADIUS server CA) the policy "IEEE 802.1X"; the servers' certificates get "802.1X server" (365 days) and "EST server"
    /// (90 days), as they were made before. The key: ECC on a new server; RSA where an OADM before ran
    /// (<paramref name="usedBefore"/>), because the tasks made RSA 2048 keys on the devices then (decision of stage 4: what
    /// the devices get does not change with the update).
    /// </summary>
    public static IReadOnlyList<PkiPolicy> ConvertIssuing(PkiConfig config, bool usedBefore)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dot1x = config.Dot1x ?? new Dot1xConfig();
        var days = config.DeviceCertValidityDays is >= PolicyLimits.MinValidDays and <= PolicyLimits.MaxValidDays ? config.DeviceCertValidityDays : PolicyLimits.DefaultValidDays;
        var key = usedBefore ? PolicyKeys.Rsa : PolicyKeys.Ecc;
        var radiusPem = dot1x.RadiusCa == RadiusCaSource.Imported && PolicyRules.IsCertificatePem(dot1x.RadiusCaPem) ? dot1x.RadiusCaPem : null;
        return
        [
            new PkiPolicy { Name = PolicyLimits.HttpsName, Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = days, Key = key },
            new PkiPolicy
            {
                Name = PolicyLimits.Dot1xName,
                Kind = PolicyKinds.Dot1xCertificate,
                CaId = CertificateAuthorityInfo.OadmId,
                ValidDays = days,
                Key = key,
                // A new installation: EAPOL version 3 and the IP address as identity (decision 2026-10-25); one used before
                // keeps what its cameras were set up with.
                EapolVersion = !usedBefore ? 3 : dot1x.EapolVersion is 1 or 2 or 3 ? dot1x.EapolVersion : 1,
                Identity = !usedBefore ? Dot1xIdentity.IpAddress : PkiValidation.Identity(dot1x.Identity) is null ? dot1x.Identity : Dot1xIdentity.Mac,
                CustomIdentity = dot1x.CustomIdentity,

                // An imported RADIUS CA goes into the CA list with ConvertRadiusCasAsync (RadiusCaId set there); one that cannot
                // be read keeps no CA: the task refuses until one is chosen (never a silent switch to the OADM CA, which the
                // RADIUS server may not use).
                RadiusCaId = dot1x.RadiusCa == RadiusCaSource.Imported ? null : CertificateAuthorityInfo.OadmId,
                RadiusCa = dot1x.RadiusCa == RadiusCaSource.Imported ? RadiusCaSource.Imported : RadiusCaSource.Oadm,
                RadiusCaPem = radiusPem,
            },
            // The 802.1X server policy comes from the 802.1X server's conversion (RadiusPolicies.DefaultServerPolicy); the EST
            // server's has a policy of its own. Both get their certificate when the server starts (decision 2026-10-25).
            new PkiPolicy { Name = PolicyLimits.EstServerCertificateName, Kind = PolicyKinds.EstServer },
        ];
    }

    /// <summary>
    /// The owner of the conversion of 2026-10-25 (<see cref="PolicyStore.ConvertKinds"/>): Server certificate into the server
    /// policies, Revoke into Block with Also revoke.
    /// </summary>
    public const string KindsOwner = PkiPluginInfo.PluginId + ":kinds-2026-10-25";

    /// <summary>The owner of the conversion of the RADIUS server CA into a CA of the list (<see cref="ConvertRadiusCasAsync"/>).</summary>
    public const string RadiusCaOwner = PkiPluginInfo.PluginId + ":radius-ca-2026-10-25";

    /// <summary>
    /// Once (decision 2026-10-25): every IEEE 802.1X policy without a RADIUS server CA of the list gets one. "OADM 802.1X
    /// server" becomes the CA that issued the 802.1X server policy's certificate (else the OADM CA); a CA imported into the
    /// policy goes into the CA list (or the listed CA with its fingerprint is used) and the policy names it. A PEM that cannot
    /// be read stays as it is and is logged: the form says below the field that a CA must be chosen.
    /// </summary>
    private async Task ConvertRadiusCasAsync(CancellationToken ct)
    {
        if (Policies.IsUnreadable || Policies.Converted.Contains(RadiusCaOwner, StringComparer.Ordinal))
        {
            return;
        }

        var chosen = new Dictionary<Guid, string>();
        foreach (var policy in Policies.All.Where(p => p.Kind == PolicyKinds.Dot1xCertificate && string.IsNullOrWhiteSpace(p.RadiusCaId)))
        {
            if (policy.RadiusCa != RadiusCaSource.Imported)
            {
                chosen[policy.Id] = AuthorityIdOf(ServerIssuerChain(PolicyKinds.Dot1xServer) is { Count: > 0 } chain ? chain[0] : null) ?? CertificateAuthorityInfo.OadmId;
                continue;
            }

            if (await ListedRadiusCaAsync(policy.RadiusCaPem, ct).ConfigureAwait(false) is { } id)
            {
                chosen[policy.Id] = id;
            }
            else
            {
                LogRadiusCaNotConverted(policy.Name);
            }
        }

        await Policies.RewriteOnceAsync(RadiusCaOwner, list => [.. list.Select(p => chosen.TryGetValue(p.Id, out var id)
            ? p with { RadiusCaId = id, RadiusCa = null, RadiusCaPem = null }
            : p)], ct).ConfigureAwait(false);
    }

    /// <summary>The id of the listed CA whose certificate is <paramref name="der"/> (its own, not one above it); null when none is.</summary>
    private string? AuthorityIdOf(byte[]? der)
    {
        if (der is null)
        {
            return null;
        }

        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(der));
        return Authorities().FirstOrDefault(a => a.Kind != CertificateAuthorityKind.PreviousOadm && a.Certificates.Count > 0
            && string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a.Certificates[0])), fingerprint, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>The listed CA of a RADIUS server CA imported into a policy before: the one already listed, else imported now; null when the PEM cannot be read.</summary>
    private async Task<string?> ListedRadiusCaAsync(string? pem, CancellationToken ct)
    {
        if (!PolicyRules.IsCertificatePem(pem))
        {
            return null;
        }

        byte[] der;
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
            der = certificate.RawData;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

        if (AuthorityIdOf(der) is { } listed)
        {
            return listed;
        }

        var reply = await ImportAuthorityAsync(new ImportAuthorityRequest(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pem!)), null), ct).ConfigureAwait(false);
        return reply.Ok ? AuthorityIdOf(der) : null;
    }

    /// <summary>The common name of a certificate (DER); empty when it cannot be read.</summary>
    private static string CommonNameOf(byte[] der)
    {
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(der);
            return Oadm.Plugins.Pki.Ca.CaCertificates.CommonName(certificate);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: the RADIUS server CA of the policy {Policy} cannot be read; choose a CA in the policy")]
    private partial void LogRadiusCaNotConverted(string policy);

    /// <summary>Loads the policies, converts PKI's own settings once and starts the minute round of Revoke.</summary>
    private async Task StartPoliciesAsync(CancellationToken ct)
    {
        await Policies.LoadAsync(ct).ConfigureAwait(false);
        var config = _config;
        await Policies.ConvertOnceAsync(PkiPluginInfo.PluginId, _ => Task.FromResult(ConvertOwnSettings(config)), ct).ConfigureAwait(false);
        await Policies.ConvertOnceAsync(IssuingOwner, async token => ConvertIssuing(config, await _store.UsedBeforeAsync(token).ConfigureAwait(false)), ct).ConfigureAwait(false);
        await Policies.RewriteOnceAsync(KindsOwner, list => PolicyStore.ConvertKinds(list, RadiusPolicies.DefaultServerPolicy), ct).ConfigureAwait(false);
        await ConvertRadiusCasAsync(ct).ConfigureAwait(false);
        Policies.Changed += OnPoliciesChanged;
        HandExpiryWarning();
        _revokeTimer = _options.Time.CreateTimer(_ => _ = RevokeByPoliciesAsync(null, CancellationToken.None), null, RevokeRound, RevokeRound);
    }

    private async ValueTask StopPoliciesAsync()
    {
        Policies.Changed -= OnPoliciesChanged;
        _anchors?.SetExpiryWarning(null, PolicyLimits.DefaultExpiryDays);
        if (_revokeTimer is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        Policies.Dispose();
    }

    private void OnPoliciesChanged(object? sender, EventArgs e)
    {
        _events?.Publish(PolicyMethods.Topic, "{}", PluginEventAudience.AllDevices);
        HandExpiryWarning();
        _ = PublishAsync(); // Used by of the CAs
    }

    /// <summary>
    /// Hands the host the days of the Expiry warning policies (the device grid's Certificate expires chip, a new Compliance
    /// check) when they changed: the host then sends every device row again, so only a change of those policies counts.
    /// </summary>
    private void HandExpiryWarning()
    {
        var expiry = Policies.PoliciesOf(PolicyKinds.ExpiryWarning);
        var key = PkiJson.Serialize(expiry.Select(p => new { p.Id, p.Days, p.Devices })) + "|" + Policies.TagsProtected;
        if (_anchors is null || Interlocked.Exchange(ref _expiryKey, key) == key)
        {
            return;
        }

        var store = Policies;
        _anchors.SetExpiryWarning(store.ExpiryDaysFor, store.DefaultExpiryDays);
    }

    /// <summary>The Policies card; a user limited to a device group gets nothing (the policies are server-wide).</summary>
    internal async Task<PolicyList> PolicyListAsync(bool hasAllDevices, bool mayManage, CancellationToken ct)
    {
        if (!hasAllDevices)
        {
            return new PolicyList();
        }

        var devices = await SystemDevicesAsync(ct).ConfigureAwait(false);
        var unreadable = Policies.IsUnreadable;
        return new PolicyList
        {
            Error = unreadable ? PolicyTexts.Unreadable : null,
            Rows = Policies.Rows(devices),
            Cas = Policies.CaChoices(),
            IssuingCas = Policies.IssuingCaChoices(),
            SignInCas = Policies.SignInCaChoices(),
            EstProfiles = await Policies.EstProfilesAsync(ct).ConfigureAwait(false),
            ServerCertificates = ServerCertificateChoices(),
            ServerCertificateCas = Policies.IssuingCaChoices(),
            Dot1xServerNames = Policies.Dot1xServerNames?.Invoke() ?? [],
            EstServerNames = Policies.EstServerNames?.Invoke() ?? [],
            Sites = [.. (_sites?.List ?? []).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new PolicySite(s.Id, s.Name))],
            Tags = Policies.TagChoices(devices),
            MayManage = mayManage && !unreadable, // nothing can be saved over a list that cannot be read
            Pro = IsPro(),
            Dot1xServerIssuer = ServerIssuerChain(PolicyKinds.Dot1xServer) is { Count: > 0 } issuer
                ? Authorities().FirstOrDefault(a => a.Certificates.Count > 0 && a.Certificates[0].AsSpan().SequenceEqual(issuer[0]))?.Name ?? CommonNameOf(issuer[0])
                : null,
        };
    }

    /// <summary>Saves one policy as <paramref name="userName"/>; a revoke policy revokes the certificates of its devices with the save.</summary>
    internal async Task<PolicySaveReply> SavePolicyAsync(PkiPolicy policy, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Kind == PolicyKinds.Dot1xServer && RadiusPolicies.ValidateTls(PolicyRules.Normalize(policy)) is { } tls)
        {
            return new PolicySaveReply { Error = tls };
        }

        var result = await Policies.SaveAsync(policy, userName, CipherSuites, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            return new PolicySaveReply { Error = error };
        }

        if (result.Saved is { } saved && PolicyRules.Revokes(saved))
        {
            await RevokeByPoliciesAsync(saved.Id, ct).ConfigureAwait(false);
        }

        return new PolicySaveReply { Saved = result.Saved, List = await PolicyListAsync(hasAllDevices: true, mayManage: true, ct).ConfigureAwait(false) };
    }

    internal async Task<PolicySaveReply> DeletePolicyAsync(Guid id, string userName, CancellationToken ct)
    {
        var result = await Policies.DeleteAsync(id, userName, ct).ConfigureAwait(false);
        return result.Error is { } error
            ? new PolicySaveReply { Error = error }
            : new PolicySaveReply { List = await PolicyListAsync(hasAllDevices: true, mayManage: true, ct).ConfigureAwait(false) };
    }

    /// <summary>How many IEEE 802.1X certificates a revoke policy with <paramref name="devices"/> would revoke now.</summary>
    internal async Task<PolicyRevocationCount> CountRevocationsAsync(PolicyDevices devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var policy = PolicyRules.Normalize(new PkiPolicy { Name = "count", Kind = PolicyKinds.Block, AlsoRevoke = true, Devices = devices });
        return new PolicyRevocationCount((await SerialsToRevokeAsync(policy, ct).ConfigureAwait(false)).Count);
    }

    /// <summary>The IEEE 802.1X certificates of <paramref name="policy"/>'s devices (never by tag) that are neither ended nor revoked.</summary>
    private async Task<List<string>> SerialsToRevokeAsync(PkiPolicy policy, CancellationToken ct)
    {
        var matcher = PolicyMatcher.Of(policy);
        var devices = await SystemDevicesAsync(ct).ConfigureAwait(false);
        var serials = new List<string>();
        foreach (var device in devices)
        {
            if (!matcher.Holds(device))
            {
                continue;
            }

            foreach (var serial in _issued.RevocableOf(device)) // by device id and serial: also a camera added again
            {
                if (string.Equals(_issued.Find(serial)?.Purpose, DeviceCertificatePurposes.Dot1x, StringComparison.Ordinal))
                {
                    serials.Add(serial);
                }
            }
        }

        return serials;
    }

    /// <summary>
    /// Revokes the current IEEE 802.1X certificates of the revoke policies' devices (one policy after its save, else all of
    /// them: the minute round catches new certificates), by the user who saved each policy, at most 5,000 per call. One round
    /// at a time; a round asked for while one runs is skipped (the next minute catches up), a save waits for its own.
    /// </summary>
    internal async Task RevokeByPoliciesAsync(Guid? only, CancellationToken ct)
    {
        var policies = Policies.PoliciesOf(PolicyKinds.Block).Where(p => PolicyRules.Revokes(p) && (only is null || p.Id == only)).ToList();
        if (policies.Count == 0)
        {
            return;
        }

        if (only is null && Interlocked.CompareExchange(ref _revokeRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            foreach (var policy in policies)
            {
                var serials = await SerialsToRevokeAsync(policy, ct).ConfigureAwait(false);
                foreach (var batch in serials.Chunk(PkiPluginInfo.MaxRevokeAtOnce))
                {
                    var result = await _issued.RevokeAsync(batch, RevocationReason.Other, policy.SavedBy ?? PkiPlugin.InProcessUser, ct).ConfigureAwait(false);
                    LogPolicyRevoked(result.Revoked, policy.Name);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
            // stopping
        }
#pragma warning disable CA1031 // The minute round tries again; the reason goes to the server log.
        catch (Exception ex) when (only is null)
#pragma warning restore CA1031
        {
            LogPolicyRevokeFailed(ex);
        }
        finally
        {
            if (only is null)
            {
                Volatile.Write(ref _revokeRunning, 0);
            }
        }
    }

    /// <summary>The days before the end that the certificates of <paramref name="deviceId"/> count as "expires soon".</summary>
    private Func<Guid, int> WarningDaysFor(IReadOnlyList<IDeviceInfo> devices)
    {
        var days = new Dictionary<Guid, int>(devices.Count);
        foreach (var device in devices)
        {
            days[device.Id] = Policies.ExpiryDaysFor(device);
        }

        var fallback = PolicyLimits.DefaultExpiryDays;
        return id => days.TryGetValue(id, out var d) ? d : fallback;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: {Count} certificates revoked by policy {Policy}")]
    private partial void LogPolicyRevoked(int count, string policy);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: revoking by policy failed")]
    private partial void LogPolicyRevokeFailed(Exception ex);
}
