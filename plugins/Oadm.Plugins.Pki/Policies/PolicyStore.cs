using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.Pki.Policies;

/// <summary>The stored form (PKI's plugin setting <c>policies</c>, part of the backup).</summary>
public sealed record StoredPolicies
{
    /// <summary>Every policy, oldest first.</summary>
    public IReadOnlyList<PkiPolicy> Policies { get; init; } = [];

    /// <summary>The owners whose settings from before the policies were converted (once each): "oadm.pki", "oadm.radius".</summary>
    public IReadOnlyList<string> Converted { get; init; } = [];
}

/// <summary>The outcome of a save or delete: the stored policy, or the field error that refused it (nothing changed).</summary>
public sealed record PolicySaveResult(PkiPolicy? Saved, PolicyFieldError? Error);

/// <summary>
/// PKI's policies (docs/spec/pki.md "Policies"): one list in PKI's settings (part of the backup), changed only through the
/// page (users with all devices and "Manage the certificate authority", audited by the host). The 802.1X server inside PKI
/// reads it in process (<see cref="PoliciesOf"/>, <see cref="Version"/>, <see cref="Changed"/>) and keeps no rules of its
/// own. Saves are refused for a device in two policies of the same kind (against the devices of now), the same CA in two
/// Allow CA policies, a second 802.1X server policy. An overlap made later by a tag or site change shows a warning in both
/// rows; the safer one applies (the older VLAN policy, the longer expiry warning; Block wins over VLAN). Readers get an
/// immutable snapshot: a change is in place only after it was stored, then <see cref="Changed"/> follows.
/// </summary>
public sealed partial class PolicyStore : IDisposable
{
    public const string SettingsKey = "policies";

    private readonly IPluginSettings _settings;
    private readonly Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>> _devices;
    private readonly Func<IReadOnlyList<ISiteInfo>> _sites;
    private readonly Func<IReadOnlyList<CertificateAuthorityInfo>> _authorities;
    private readonly IProtectedTags? _protectedTags;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _snapshot;

    /// <param name="settings">PKI's plugin settings.</param>
    /// <param name="devices">Every managed device now (overlaps, warnings, tags).</param>
    /// <param name="sites">The bound sites (sites of a policy must exist).</param>
    /// <param name="authorities">PKI's CAs (Allow CA names one of them).</param>
    /// <param name="protectedTags">The host's protection of tags; null: the tag parts of VLAN policies match no device (fail closed).</param>
    /// <param name="logger">PKI's log.</param>
    public PolicyStore(IPluginSettings settings, Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>> devices, Func<IReadOnlyList<ISiteInfo>> sites,
        Func<IReadOnlyList<CertificateAuthorityInfo>> authorities, IProtectedTags? protectedTags, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _sites = sites ?? throw new ArgumentNullException(nameof(sites));
        _authorities = authorities ?? throw new ArgumentNullException(nameof(authorities));
        _protectedTags = protectedTags;
        _logger = logger ?? NullLogger.Instance;
        _snapshot = new Snapshot(new StoredPolicies(), 0, TagsProtected);
    }

    /// <summary>Grows with every change of the stored policies (a reader compares it with what it built its rules from).</summary>
    public long Version => _snapshot.Version;

    /// <summary>
    /// A stored list exists but cannot be read (damaged): the store holds no policies, converts and writes nothing (the stored
    /// value is never overwritten), refuses every change, and the 802.1X server refuses every device. A backup restores it.
    /// </summary>
    public bool IsUnreadable => _snapshot.Unreadable;

    /// <summary>The host protects the tags the policies use (only users with all devices add or remove them on devices).</summary>
    public bool TagsProtected => _protectedTags is not null;

    /// <summary>Raised after a change is stored and the new list is in place (from any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Every stored policy, oldest first.</summary>
    public IReadOnlyList<PkiPolicy> All => _snapshot.Stored.Policies;

    /// <summary>The owners converted so far.</summary>
    public IReadOnlyList<string> Converted => _snapshot.Stored.Converted;

    /// <summary>The policies of <paramref name="kind"/>, oldest first.</summary>
    public IReadOnlyList<PkiPolicy> PoliciesOf(string kind) => _snapshot.ByKind.GetValueOrDefault(kind) ?? [];

    /// <summary>The 802.1X server policy; null when it was deleted (the server then uses the defaults of the kind).</summary>
    public PkiPolicy? ServerPolicy => PoliciesOf(PolicyKinds.Dot1xServer) is { Count: > 0 } server ? server[0] : null;

    /// <summary>
    /// The days before the end that a certificate of <paramref name="device"/> counts as "expires soon": the longest of the
    /// Expiry warning policies that hold it, else <see cref="PolicyLimits.DefaultExpiryDays"/>.
    /// </summary>
    public int ExpiryDaysFor(IDeviceInfo device) => _snapshot.ExpiryDaysFor(device);

    /// <summary>The days of a check that covers no single device (a new Compliance check): the longest Expiry warning, else 30.</summary>
    public int DefaultExpiryDays => _snapshot.DefaultExpiryDays;

    /// <summary>Reads the stored policies and protects their tags.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var json = await _settings.GetAsync(SettingsKey, ct).ConfigureAwait(false);
        var read = PkiJson.TryDeserialize<StoredPolicies>(json);
        var unreadable = read is null && !string.IsNullOrWhiteSpace(json);
        if (unreadable)
        {
            LogUnreadable();
        }

        var stored = read ?? new StoredPolicies();
        stored = stored with { Policies = WithUniqueIds(stored.Policies ?? []), Converted = stored.Converted ?? [] };
        _snapshot = new Snapshot(stored, _snapshot.Version + 1, TagsProtected, unreadable);
        Protect(stored.Policies);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Once per <paramref name="owner"/>: the policies <paramref name="convert"/> makes of the owner's settings from before the
    /// policies (or the defaults of its kinds when it stored none) go to the end of the list, names made unique per kind.
    /// Later calls do nothing, so the conversion runs once even when the owner's old settings stay. Nothing is validated or
    /// dropped: overlaps found while converting are kept and show their warning.
    /// </summary>
    /// <returns>True when it converted now.</returns>
    public async Task<bool> ConvertOnceAsync(string owner, Func<CancellationToken, Task<IReadOnlyList<PkiPolicy>>> convert, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(convert);
        if (IsUnreadable || Converted.Contains(owner, StringComparer.Ordinal))
        {
            return false; // unreadable: converting would overwrite the stored list
        }

        var converted = await convert(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = _snapshot.Stored;
            if (_snapshot.Unreadable || stored.Converted.Contains(owner, StringComparer.Ordinal))
            {
                return false;
            }

            var list = stored.Policies.ToList();
            foreach (var policy in converted)
            {
                var normal = PolicyRules.Normalize(policy);
                if (MergedInto(list, normal))
                {
                    continue;
                }

                list.Add(normal with { Id = normal.Id == Guid.Empty || list.Any(p => p.Id == normal.Id) ? Guid.NewGuid() : normal.Id, Name = UniqueName(list, normal) });
            }

            var next = stored with { Policies = list, Converted = [.. stored.Converted, owner] };
            Protect(list);
            await StoreAsync(next, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        LogConverted(converted.Count, owner);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Once per <paramref name="owner"/>: the stored list becomes what <paramref name="rewrite"/> makes of it (a change of the
    /// kinds, e.g. <see cref="ConvertKinds"/>). Never on a list that cannot be read; later calls do nothing.
    /// </summary>
    /// <returns>True when it rewrote now.</returns>
    public async Task<bool> RewriteOnceAsync(string owner, Func<IReadOnlyList<PkiPolicy>, IReadOnlyList<PkiPolicy>> rewrite, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(rewrite);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        int changed;
        try
        {
            var stored = _snapshot.Stored;
            if (_snapshot.Unreadable || stored.Converted.Contains(owner, StringComparer.Ordinal))
            {
                return false; // unreadable: converting would overwrite the stored list
            }

            var list = WithUniqueIds(rewrite(stored.Policies));
            changed = list.Count(p => !stored.Policies.Contains(p));
            Protect(list);
            await StoreAsync(stored with { Policies = list, Converted = [.. stored.Converted, owner] }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        LogConverted(changed, owner);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The owner of the conversion of 2026-10-26: the previous OADM CAs became ordinary CAs (<see cref="ConvertPreviousCas"/>).</summary>
    public const string PreviousCasOwner = "oadm.pki:previous-cas-2026-10-26";

    /// <summary>The owner of the 802.1X server's conversion, which makes its trusted CAs 802.1X trusted CA policies.</summary>
    public const string RadiusOwner = "oadm.radius";

    /// <summary>
    /// Once (decision 2026-10-26), after the 802.1X server's trusted CAs became policies (<see cref="RadiusOwner"/>; before
    /// that nothing is done and nothing is marked): <see cref="ConvertPreviousCas"/> with the CAs PKI lists now. Never on a
    /// list that cannot be read. True when it converted now.
    /// </summary>
    public Task<bool> ConvertPreviousCasOnceAsync(CancellationToken ct)
    {
        if (IsUnreadable || !Converted.Contains(RadiusOwner, StringComparer.Ordinal) || Converted.Contains(PreviousCasOwner, StringComparer.Ordinal))
        {
            return Task.FromResult(false);
        }

        var authorities = _authorities();
        return RewriteOnceAsync(PreviousCasOwner, list => ConvertPreviousCas(list, authorities), ct);
    }

    /// <summary>
    /// The previous OADM CAs become ordinary CAs (decision 2026-10-26): until then an 802.1X trusted CA policy of the first-start
    /// CA also trusted every previous OADM CA. Each previous CA that is still valid (<paramref name="authorities"/> lists only
    /// those) gets its own 802.1X trusted CA policy with the settings of that policy, named after the CA and placed right after
    /// it, so every camera passes as before and nothing follows implicitly any more. A previous CA a policy names already, or a
    /// list without a policy of the first-start CA, stays as it is. Idempotent.
    /// </summary>
    public static IReadOnlyList<PkiPolicy> ConvertPreviousCas(IReadOnlyList<PkiPolicy> stored, IReadOnlyList<CertificateAuthorityInfo> authorities)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(authorities);
        var previous = authorities.Where(a => a.Kind == CertificateAuthorityKind.PreviousOadm).ToList();
        var list = stored.ToList();
        if (previous.Count == 0)
        {
            return list;
        }

        foreach (var policy in stored.Where(p => p.Kind == PolicyKinds.AllowCa && string.Equals(p.CaId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase)))
        {
            var at = list.IndexOf(policy) + 1;
            foreach (var ca in previous)
            {
                if (list.Any(p => p.Kind == PolicyKinds.AllowCa && string.Equals(p.CaId, ca.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var added = policy with { Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(ca.Name) ? "Previous OADM CA" : ca.Name.Trim(), CaId = ca.Id };
                list.Insert(at++, added with { Name = UniqueName(list, added) });
            }
        }

        return list;
    }

    /// <summary>
    /// The kinds of 2026-10-25 (docs/spec/pki.md "Converting existing settings"), nothing the user set lost: the Server
    /// certificate policy of the 802.1X server goes into the 802.1X server policy (its CA, days and key; without an 802.1X
    /// server policy one with the kind's TLS defaults takes them), the one of the EST server becomes the EST server policy
    /// in its place; a Revoke policy becomes a Block policy with Also revoke, a Block policy keeps it off; names stay,
    /// made unique per kind ("Stolen (2)"). Without an EST server policy one with the defaults (90 days, ECC, the OADM CA)
    /// is added. Idempotent: a converted list stays as it is.
    /// </summary>
    public static IReadOnlyList<PkiPolicy> ConvertKinds(IReadOnlyList<PkiPolicy> stored, Func<PkiPolicy> defaultDot1xServer)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(defaultDot1xServer);
        var servers = stored.Where(p => p.Kind == PolicyKinds.LegacyServerCertificate).ToList();
        var dot1xCertificate = servers.FirstOrDefault(p => p.Server == PolicyServers.Dot1x);
        var estCertificate = servers.FirstOrDefault(p => p.Server == PolicyServers.Est);
        var hasDot1xServer = stored.Any(p => p.Kind == PolicyKinds.Dot1xServer);
        var hasEstServer = stored.Any(p => p.Kind == PolicyKinds.EstServer);
        var list = new List<PkiPolicy>(stored.Count);
        foreach (var policy in stored)
        {
            switch (policy.Kind)
            {
                case PolicyKinds.LegacyServerCertificate when ReferenceEquals(policy, estCertificate) && !hasEstServer:
                    list.Add(policy with { Kind = PolicyKinds.EstServer, Server = null, CaId = policy.CaId ?? CertificateAuthorityInfo.OadmId });
                    break;
                case PolicyKinds.LegacyServerCertificate when ReferenceEquals(policy, dot1xCertificate) && !hasDot1xServer:
                    // No 802.1X server policy (deleted on a build before the system policies): one with the TLS defaults.
                    list.Add(WithCertificate(defaultDot1xServer() with { Id = policy.Id, SavedBy = policy.SavedBy }, policy));
                    break;
                case PolicyKinds.LegacyServerCertificate:
                    break; // its values went into the server policy
                case PolicyKinds.Dot1xServer:
                    list.Add(WithCertificate(policy, dot1xCertificate));
                    break;
                case PolicyKinds.LegacyRevoke:
                    list.Add(policy with { Kind = PolicyKinds.Block, AlsoRevoke = true });
                    break;
                case PolicyKinds.Block:
                    list.Add(policy with { AlsoRevoke = policy.AlsoRevoke ?? false });
                    break;
                default:
                    list.Add(policy);
                    break;
            }
        }

        if (!list.Any(p => p.Kind == PolicyKinds.EstServer))
        {
            list.Add(new PkiPolicy { Name = PolicyLimits.EstServerCertificateName, Kind = PolicyKinds.EstServer });
        }

        // Names are unique per kind: a Revoke and a Block policy of the same name are both Block policies now.
        for (var i = 0; i < list.Count; i++)
        {
            var policy = list[i];
            if (list.Take(i).Any(p => p.Kind == policy.Kind && string.Equals(p.Name.Trim(), policy.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                list[i] = policy with { Name = UniqueName(list.Take(i).ToList(), policy) };
            }
        }

        return list;

        // The 802.1X server policy with the certificate values (those of the Server certificate policy win: the user set
        // them there; without one the policy's own or the kind's defaults).
        static PkiPolicy WithCertificate(PkiPolicy server, PkiPolicy? certificate) => server with
        {
            CaId = certificate?.CaId ?? server.CaId ?? CertificateAuthorityInfo.OadmId,
            ValidDays = certificate?.ValidDays ?? server.ValidDays ?? PolicyLimits.DefaultValidDays,
            Key = certificate?.Key ?? server.Key ?? PolicyKeys.Ecc,
        };
    }

    /// <summary>
    /// Saves <paramref name="request"/> (a new one goes last: it is the newest) after the checks, as <paramref name="userName"/>;
    /// applies at once for the 802.1X server (<see cref="Changed"/>).
    /// </summary>
    public async Task<PolicySaveResult> SaveAsync(PkiPolicy request, string userName, IReadOnlyList<PolicyCipherSuite> suites, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(suites);
        var policy = PolicyRules.Normalize(request);
        if (PolicyRules.Validate(policy, suites) is { } own)
        {
            return new PolicySaveResult(null, own);
        }

        if (IsUnreadable)
        {
            return Refused(PolicyFields.Name, PolicyTexts.Unreadable);
        }

        var devices = PolicyKinds.HasDevices(policy.Kind) ? await _devices(ct).ConfigureAwait(false) : [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshot.Unreadable)
            {
                return Refused(PolicyFields.Name, PolicyTexts.Unreadable);
            }

            var stored = _snapshot.Stored;
            var list = stored.Policies.ToList();
            var index = policy.Id == Guid.Empty ? -1 : list.FindIndex(p => p.Id == policy.Id);
            if (policy.Id != Guid.Empty && index < 0)
            {
                return Refused(PolicyFields.Name, PolicyTexts.PolicyGone);
            }

            var before = index >= 0 ? list[index] : null;
            if (before is not null && before.Revision != policy.Revision)
            {
                return Refused(PolicyFields.Policy, PolicyTexts.ChangedElsewhere); // optimistic: the form read an older one
            }

            if (before is not null && before.Kind != policy.Kind)
            {
                return Refused(PolicyFields.Kind, PolicyTexts.KindKept);
            }

            if (index < 0 && list.Count >= PolicyLimits.MaxPolicies)
            {
                return Refused(PolicyFields.Name, PolicyTexts.TooMany);
            }

            var sameKind = list.Where(p => p.Id != policy.Id && p.Kind == policy.Kind).ToList();
            if (sameKind.Any(p => string.Equals(p.Name.Trim(), policy.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return Refused(PolicyFields.Name, PolicyTexts.NameTaken(policy.Kind));
            }

            if (PolicyKinds.OneOnly(policy.Kind) && sameKind.Count > 0)
            {
                return Refused(PolicyFields.Kind, PolicyTexts.OneOnly(policy.Kind));
            }

            // "Issued by" is a CA that can issue now: the OADM CA (also while it is being made) or an imported CA with its key.
            if (PolicyKinds.IsIssuing(policy.Kind) && !IssuingCaChoices().Any(c => string.Equals(c.Id, policy.CaId, StringComparison.OrdinalIgnoreCase)))
            {
                return Refused(PolicyFields.Ca, PolicyTexts.CannotIssue);
            }

            // The RADIUS server CA of an IEEE 802.1X policy is a CA of the list (decision 2026-10-25).
            if (policy.Kind == PolicyKinds.Dot1xCertificate
                && !_authorities().Any(a => string.Equals(a.Id, policy.RadiusCaId, StringComparison.OrdinalIgnoreCase)))
            {
                return Refused(PolicyFields.RadiusCa, PolicyTexts.ChooseRadiusCa);
            }

            // A server policy shows a certificate of the Certificates list with its key (decision 2026-10-25).
            if (PolicyKinds.IsServer(policy.Kind) && CertificateRefusal(policy) is { } certificate)
            {
                return Refused(PolicyFields.Certificate, certificate);
            }

            if (policy.Kind == PolicyKinds.AllowCa)
            {
                if (!_authorities().Any(a => string.Equals(a.Id, policy.CaId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Refused(PolicyFields.Ca, PolicyTexts.UnknownCa);
                }

                if (sameKind.Any(p => string.Equals(p.CaId, policy.CaId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Refused(PolicyFields.Ca, PolicyTexts.CaTaken);
                }
            }

            if (policy.Kind == PolicyKinds.SignInCa)
            {
                // The Axis device ID, the OADM CA or a previous OADM CA: EST reads a camera's serial only from those.
                if (!SignInCaChoices().Any(a => string.Equals(a.Id, policy.CaId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Refused(PolicyFields.Ca, PolicyTexts.NoSignInCa);
                }

                if (sameKind.Any(p => string.Equals(p.CaId, policy.CaId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Refused(PolicyFields.Ca, PolicyTexts.SignInCaTaken);
                }
            }

            if (policy is { Kind: PolicyKinds.Enrollment, ProfileScope: PolicyProfiles.Chosen } && ProfileNames is { } profileNames)
            {
                var known = new HashSet<string>(await profileNames(ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
                if ((policy.Profiles ?? []).FirstOrDefault(p => !known.Contains(p)) is { } unknown)
                {
                    return Refused(PolicyFields.Profiles, PolicyTexts.UnknownProfile(unknown));
                }
            }

            if (policy.Devices is { } chosen)
            {
                var sites = _sites().Select(s => s.Id).ToHashSet();
                var kept = before?.Devices?.SiteIds ?? [];
                if (chosen.SiteIds.Any(id => !sites.Contains(id) && !kept.Contains(id)))
                {
                    return Refused(PolicyFields.Devices, PolicyTexts.SiteGone);
                }

                if (NewOverlap(policy, before, sameKind, devices) is { } overlap)
                {
                    return Refused(PolicyFields.Devices, overlap);
                }
            }

            policy = policy with { Id = before?.Id ?? Guid.NewGuid(), SavedBy = userName, Revision = (before?.Revision ?? 0) + 1 };
            if (index >= 0)
            {
                list[index] = policy;
            }
            else
            {
                list.Add(policy);
            }

            // The tags of the old and the new list stay protected while the save runs (a failed save keeps the old list).
            Protect([.. stored.Policies, policy]);
            try
            {
                await StoreAsync(stored with { Policies = list }, ct).ConfigureAwait(false);
            }
            finally
            {
                Protect(_snapshot.Stored.Policies);
            }
        }
        finally
        {
            _gate.Release();
        }

        var kindName = PolicyKinds.Name(policy.Kind);
        LogSaved(policy.Name, kindName, userName);
        Changed?.Invoke(this, EventArgs.Empty);
        return new PolicySaveResult(policy, null);
    }

    /// <summary>Deletes the policy <paramref name="id"/>; the error when it no longer exists.</summary>
    public async Task<PolicySaveResult> DeleteAsync(Guid id, string userName, CancellationToken ct)
    {
        PkiPolicy? removed;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshot.Unreadable)
            {
                return Refused(PolicyFields.Name, PolicyTexts.Unreadable);
            }

            var stored = _snapshot.Stored;
            removed = stored.Policies.FirstOrDefault(p => p.Id == id);
            if (removed is null)
            {
                return Refused(PolicyFields.Name, PolicyTexts.PolicyGone);
            }

            if (await DeleteRefusalAsync(removed, stored.Policies, ct).ConfigureAwait(false) is { } refusal)
            {
                return Refused(PolicyFields.Name, refusal);
            }

            await StoreAsync(stored with { Policies = [.. stored.Policies.Where(p => p.Id != id)] }, ct).ConfigureAwait(false);
            Protect(_snapshot.Stored.Policies);
        }
        finally
        {
            _gate.Release();
        }

        var removedKind = PolicyKinds.Name(removed.Kind);
        LogDeleted(removed.Name, removedKind, userName);
        Changed?.Invoke(this, EventArgs.Empty);
        return new PolicySaveResult(removed, null);
    }

    /// <summary>
    /// The EST profiles that name issuing policies (policy id, profile name), set by the EST server's core plugin: an issuing
    /// policy a profile names is never deleted. Null: no profile names one.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<(Guid PolicyId, string Profile)>>>? ProfileUses { get; set; }

    /// <summary>
    /// The names of the EST server's profiles, set by the EST server's core plugin (also while the server is off): an
    /// Enrollment policy chooses among them. Null: not known (nothing is checked, the page offers none).
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? ProfileNames { get; set; }

    /// <summary>The names of the EST server's profiles; none while the EST server's core plugin did not tell them.</summary>
    public async Task<IReadOnlyList<string>> EstProfilesAsync(CancellationToken ct) =>
        ProfileNames is { } names ? await names(ct).ConfigureAwait(false) : [];

    /// <summary>The policy <paramref name="id"/>; null when there is none.</summary>
    public PkiPolicy? Find(Guid id) => id == Guid.Empty ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>The first (oldest) policy of <paramref name="kind"/>: the default of the task dialogs, renewals and new profiles.</summary>
    public PkiPolicy? FirstOf(string kind) => PoliciesOf(kind) is { Count: > 0 } list ? list[0] : null;

    /// <summary>The EST server policy (the certificate it shows, decision 2026-10-25); null without one.</summary>
    public PkiPolicy? EstServerPolicy => FirstOf(PolicyKinds.EstServer);

    /// <summary>
    /// The certificates of PKI's Certificates list with a key OADM holds, set by PKI (a server policy picks one; decision
    /// 2026-10-25). Null: none known (a server policy cannot be saved).
    /// </summary>
    public Func<IReadOnlyList<PolicyCertificateChoice>>? ServerCertificates { get; set; }

    /// <summary>
    /// The first-start root CA ("oadm") is being made, set by PKI: only then do the CA choices offer it without its certificate
    /// (decision 2026-10-26: once deleted it is gone). Null: never.
    /// </summary>
    public Func<bool>? DefaultCaPending { get; set; }

    /// <summary>The ids of the CAs made on this server (the first-start root CA, Create CA), set by PKI: they may be EST sign-in CAs.</summary>
    public Func<IReadOnlyCollection<string>>? CreatedCas { get; set; }

    private bool Pending => DefaultCaPending?.Invoke() == true;

    /// <summary>The names a new 802.1X server certificate gets (this server's addresses and host name), set by the 802.1X server.</summary>
    public Func<IReadOnlyList<string>>? Dot1xServerNames { get; set; }

    /// <summary>
    /// The names a new EST server certificate gets (this server's addresses and host name, the site nodes' addresses), set by
    /// the EST server; while it is off by its plugin (the site nodes' addresses the cameras were given).
    /// </summary>
    public Func<IReadOnlyList<string>>? EstServerNames { get; set; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<bool>> _serverSwitches = new(StringComparer.Ordinal);

    /// <summary>Raised when a server plugin of the package started (<see cref="ServerPluginStarted"/>).</summary>
    public event EventHandler? ServerPluginsChanged;

    /// <summary>
    /// The 802.1X or EST server plugin started (also while its server is off) and tells whether its server is turned on: PKI
    /// makes the shared server certificate only once both told (their names are known then) and leaves it to a server that
    /// is on (its start makes it with its names, the 802.1X server first takes over the certificate it kept).
    /// </summary>
    public void ServerPluginStarted(string kind, Func<bool> turnedOn)
    {
        ArgumentNullException.ThrowIfNull(turnedOn);
        _serverSwitches[kind] = turnedOn;
        ServerPluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Both server plugins started.</summary>
    public bool ServerPluginsKnown => _serverSwitches.ContainsKey(PolicyKinds.Dot1xServer) && _serverSwitches.ContainsKey(PolicyKinds.EstServer);

    /// <summary>A server is turned on (its own start makes the server certificate).</summary>
    public bool AnyServerTurnedOn => _serverSwitches.Values.Any(on => on());

    /// <summary>Why a server policy cannot show the certificate it picks; null when it can.</summary>
    private string? CertificateRefusal(PkiPolicy policy)
    {
        var choice = (ServerCertificates?.Invoke() ?? []).FirstOrDefault(c => string.Equals(c.Id, policy.CertificateId, StringComparison.Ordinal));
        return choice is null ? PolicyTexts.CertificateGone
            : choice.Problem is { } problem ? problem
            : !choice.UsableFor(policy.Kind) ? PolicyTexts.Dot1xServerNeedsEccCertificate
            : null;
    }

    /// <summary>The server policy that shows the certificate <paramref name="certificateId"/>; null when none does (it can then be deleted).</summary>
    public PkiPolicy? ShowingCertificate(string certificateId) =>
        All.FirstOrDefault(p => PolicyKinds.IsServer(p.Kind) && string.Equals(p.CertificateId, certificateId, StringComparison.Ordinal));

    /// <summary>
    /// Sets the certificate of the server policy of <paramref name="kind"/> without the checks of a save (Turn on and the
    /// first start after 2026-10-25 give a server without one a certificate; decision 2026-10-25). Never on a list that
    /// cannot be read; false when there is no such policy or it already has a certificate.
    /// </summary>
    public async Task<bool> SetServerCertificateAsync(string kind, string certificateId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateId);
        PkiPolicy changed;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = _snapshot.Stored;
            var index = stored.Policies.ToList().FindIndex(p => p.Kind == kind);
            if (_snapshot.Unreadable || index < 0 || !string.IsNullOrEmpty(stored.Policies[index].CertificateId))
            {
                return false;
            }

            changed = stored.Policies[index] with { CertificateId = certificateId, Revision = stored.Policies[index].Revision + 1 }; // an open form reads it again
            var list = stored.Policies.ToList();
            list[index] = changed;
            await StoreAsync(stored with { Policies = list }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var kindName = PolicyKinds.Name(changed.Kind);
        LogSaved(changed.Name, kindName, "OADM");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>"EST server (OADM CA), until 2035-10-25" per certificate id (the summary of a server policy).</summary>
    private Dictionary<string, string> CertificateLabels() =>
        (ServerCertificates?.Invoke() ?? []).GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);

    /// <summary>
    /// Why <paramref name="policy"/> cannot be deleted: a system policy (<see cref="IsSystem"/>; the server policies among
    /// them), an issuing policy an EST profile names. Null when it can.
    /// </summary>
    private async Task<string?> DeleteRefusalAsync(PkiPolicy policy, IReadOnlyList<PkiPolicy> list, CancellationToken ct)
    {
        if (IsSystem(policy, list))
        {
            return PolicyTexts.SystemPolicy;
        }

        if (!PolicyKinds.IsDeviceIssuing(policy.Kind))
        {
            return null;
        }

        if (ProfileUses is { } uses)
        {
            var profiles = (await uses(ct).ConfigureAwait(false)).Where(u => u.PolicyId == policy.Id).Select(u => u.Profile).ToList();
            if (profiles.Count > 0)
            {
                return PolicyTexts.UsedByProfiles(profiles);
            }
        }

        return null; // the last of a device issuing kind is its system policy (refused above)
    }

    /// <summary>
    /// A system policy (decision 2026-10-25): the first (oldest) policy of a kind with system policies
    /// (<see cref="PolicyKinds.HasSystemPolicy"/>). It cannot be deleted.
    /// </summary>
    public static bool IsSystem(PkiPolicy policy, IReadOnlyList<PkiPolicy> list)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(list);
        return PolicyKinds.HasSystemPolicy(policy.Kind) && list.FirstOrDefault(p => p.Kind == policy.Kind)?.Id == policy.Id;
    }

    /// <summary>
    /// The rows of the card: every policy with its summary, whether it is a system policy, and the warning of an overlap a
    /// tag or site change made later (checked against <paramref name="devices"/>: both rows warn and name the one that applies).
    /// </summary>
    public IReadOnlyList<PolicyRow> Rows(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var snapshot = _snapshot;
        var names = CaNames();
        var certificates = CertificateLabels();
        var warnings = Warnings(snapshot, devices);
        var list = snapshot.Stored.Policies;
        return
        [
            .. list.Select(p => new PolicyRow(p, PolicyKinds.Name(p.Kind), PolicyKinds.GroupOf(p.Kind), Summary(p, names, certificates), warnings.GetValueOrDefault(p.Id), IsSystem(p, list))),
        ];
    }

    /// <summary>The name and summary sentence of every policy of the kinds <paramref name="kinds"/> takes, in age order (the tooltip of a server tab's "N policies applied").</summary>
    public IReadOnlyList<PolicyLine> LinesOf(Func<string, bool> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var names = CaNames();
        var certificates = CertificateLabels();
        return [.. _snapshot.Stored.Policies.Where(p => kinds(p.Kind)).Select(p => new PolicyLine(p.Name, Summary(p, names, certificates)))];
    }

    /// <summary>The CAs an Allow CA policy (and the RADIUS server CA of an IEEE 802.1X policy) can name: every CA of the list.</summary>
    public IReadOnlyList<PolicyCaChoice> CaChoices() => [.. _authorities().Select(a => new PolicyCaChoice(a.Id, a.Name, a.Kind))];

    /// <summary>
    /// The CAs a Sign-in CA policy can name: the Axis device ID, the OADM CA and each previous OADM CA (EST reads a camera's
    /// serial only from the Axis device ID or the axis name the OADM CA put in its certificates; imported CAs never sign a
    /// camera in). The OADM CA is listed while it has no certificate yet.
    /// </summary>
    public IReadOnlyList<PolicyCaChoice> SignInCaChoices()
    {
        var all = _authorities();
        // Decision 2026-10-26: the CAs made on this server (Create CA) too; their key never left OADM.
        var created = new HashSet<string>(CreatedCas?.Invoke() ?? [], StringComparer.OrdinalIgnoreCase);
        var choices = all.Where(a => a.Kind is CertificateAuthorityKind.AxisDeviceId or CertificateAuthorityKind.Oadm or CertificateAuthorityKind.PreviousOadm
                || (a.Kind == CertificateAuthorityKind.Imported && a.Issues && created.Contains(a.Id)))
            .OrderBy(a => a.Kind switch { CertificateAuthorityKind.AxisDeviceId => 0, CertificateAuthorityKind.Oadm => 1, _ => 2 })
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(a => new PolicyCaChoice(a.Id, string.IsNullOrWhiteSpace(a.Name) ? DefaultCaName(a.Id) : a.Name, a.Kind)).ToList();
        if (!choices.Any(c => c.Kind == CertificateAuthorityKind.AxisDeviceId)) // a deleted Axis device ID stays a choice: EST then lets nothing in through it (fail closed)
        {
            choices.Insert(0, new PolicyCaChoice(CertificateAuthorityInfo.AxisDeviceIdId, AxisDeviceIdName, CertificateAuthorityKind.AxisDeviceId));
        }

        if (!choices.Any(c => c.Kind == CertificateAuthorityKind.Oadm) && Pending)
        {
            choices.Insert(Math.Min(1, choices.Count), new PolicyCaChoice(CertificateAuthorityInfo.OadmId, IssuingCaName, CertificateAuthorityKind.Oadm));
        }

        return choices;
    }

    /// <summary>
    /// The CAs an issuing policy can pick as "Issued by" (decision 2026-10-25): every CA that can issue (the OADM CA, the
    /// imported CAs with their key). The first-start root CA is listed while it is being made (<see cref="DefaultCaPending"/>).
    /// </summary>
    public IReadOnlyList<PolicyCaChoice> IssuingCaChoices()
    {
        var choices = _authorities().Where(a => a.Issues && a.Kind is CertificateAuthorityKind.Oadm or CertificateAuthorityKind.Imported)
            .Select(a => new PolicyCaChoice(a.Id, a.Name, a.Kind)).ToList();
        if (!choices.Any(c => c.Kind == CertificateAuthorityKind.Oadm) && Pending)
        {
            choices.Insert(0, new PolicyCaChoice(CertificateAuthorityInfo.OadmId, IssuingCaName, CertificateAuthorityKind.Oadm));
        }

        return choices;
    }

    /// <summary>
    /// Every policy that names <paramref name="authorityId"/> itself: issuing ones ("Issued by"; no CA: the OADM CA), 802.1X
    /// trusted CA, EST sign-in CA and the RADIUS server CA of IEEE 802.1X policies (decision 2026-10-25: such a CA cannot be
    /// removed).
    /// </summary>
    public IReadOnlyList<PkiPolicy> NamingCa(string authorityId) =>
        [.. All.Where(p => ((p.Kind is PolicyKinds.AllowCa or PolicyKinds.SignInCa || PolicyKinds.IsIssuing(p.Kind))
                && string.Equals(p.CaId ?? (PolicyKinds.IsIssuing(p.Kind) ? CertificateAuthorityInfo.OadmId : null), authorityId, StringComparison.OrdinalIgnoreCase))
            || (p.Kind == PolicyKinds.Dot1xCertificate && string.Equals(p.RadiusCaId, authorityId, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>The name of the Axis device ID CAs.</summary>
    public const string AxisDeviceIdName = "Axis device ID";

    private static string DefaultCaName(string id) =>
        string.Equals(id, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.OrdinalIgnoreCase) ? AxisDeviceIdName : IssuingCaName;

    /// <summary>The Allow CA policies that name <paramref name="authorityId"/> (decision 2026-10-26: no CA follows another).</summary>
    public IReadOnlyList<PkiPolicy> UsingCa(string authorityId) =>
        [.. PoliciesOf(PolicyKinds.AllowCa).Where(p => string.Equals(p.CaId, authorityId, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The tags a policy can name: those of <paramref name="devices"/> and of the policies, in name order.</summary>
    public IReadOnlyList<string> TagChoices(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var tags = new HashSet<string>(PolicyMatcher.TagsOf(All), StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            foreach (var tag in device.Tags)
            {
                tags.Add(tag);
            }
        }

        return [.. tags.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The row's summary (and the editor's preview line): one plain sentence that says what the policy does, with its values;
    /// the Devices column says which devices ("these devices"). One builder per kind (docs/spec/pki.md "Policies").
    /// </summary>
    public static string Summary(PkiPolicy policy, IReadOnlyDictionary<string, string> caNames, IReadOnlyDictionary<string, string>? certificates = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caNames);
        var ca = CaName(policy.CaId, caNames);
        var shows = policy.CertificateId is not { } certificateId ? "has no certificate yet"
            : certificates?.GetValueOrDefault(certificateId) is { } label ? $"shows the certificate {label}"
            : "shows a certificate that is no longer in the list";
        return policy.Kind switch
        {
            PolicyKinds.ExpiryWarning => string.Create(CultureInfo.CurrentCulture,
                $"Certificates with {PolicyTexts.DaysText(policy.Days ?? 0)} or less left show a warning and raise Certificate expires soon."),
            PolicyKinds.HttpsCertificate => $"Enable HTTPS issues certificates from {ca}, {Issued(policy)}.",
            PolicyKinds.Dot1xCertificate => $"Enable IEEE 802.1X issues certificates from {ca}, {Issued(policy)}. {SignsInAs(policy)}"
                + (policy.RadiusCaId is { } radius ? $" Cameras check the RADIUS server with {CaName(radius, caNames)}." : string.Empty),
            PolicyKinds.EstServer => $"The EST server {shows}.",
            PolicyKinds.Dot1xServer => string.Create(CultureInfo.CurrentCulture,
                $"The 802.1X server {shows}. It accepts TLS {policy.MinimumTls} or later with secure cipher suites, sends EAP packets of up to {policy.LargestEapPacket ?? 0:N0} bytes and ")
                + (policy.ReauthMinutes is { } minutes
                    ? string.Create(CultureInfo.CurrentCulture, $"asks cameras to sign in again every {minutes:N0} minutes.")
                    : "lets the switch decide when cameras sign in again."),
            PolicyKinds.AllowCa => policy.OnlyManaged ?? true
                ? $"The 802.1X server lets in devices OADM manages that have a certificate from {ca}."
                : $"The 802.1X server lets in any device with a valid certificate from {ca}.",
            PolicyKinds.Vlan => string.Create(CultureInfo.InvariantCulture, $"The 802.1X server puts these devices in VLAN {policy.Vlan}."),
            PolicyKinds.Block => "The 802.1X server refuses these devices.",
            PolicyKinds.Enrollment => PolicyTexts.EnrollmentSentence(policy),
            PolicyKinds.SignInCa => SignInSentence(policy, caNames),
            _ => PolicyKinds.Name(policy.Kind),
        };

        static string Issued(PkiPolicy policy) => $"valid {PolicyTexts.DaysText(policy.ValidDays ?? 0)}, with an {PolicyKeys.Name(policy.Key)} key";
    }

    /// <summary>"Cameras sign in with their MAC address." (the EAP identity of an IEEE 802.1X certificate policy).</summary>
    private static string SignsInAs(PkiPolicy policy) => policy.Identity switch
    {
        Dot1xIdentity.HostName => "Cameras sign in with their host name.",
        Dot1xIdentity.IpAddress => "Cameras sign in with their IP address.",
        Dot1xIdentity.IpLastOctet => "Cameras sign in with the last octet of their IP address.",
        Dot1xIdentity.Custom => $"Cameras sign in as {policy.CustomIdentity}.",
        _ => "Cameras sign in with their MAC address.",
    };

    /// <summary>The sentence of a Sign-in CA policy: the Axis device ID, or a CA whose certificates cameras sign in with.</summary>
    private static string SignInSentence(PkiPolicy policy, IReadOnlyDictionary<string, string> caNames)
    {
        var renewals = policy.SignInFor == PolicySignIn.Renewal;
        if (string.Equals(policy.CaId, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.OrdinalIgnoreCase))
        {
            return renewals
                ? "Cameras sign in to the EST server with their Axis device ID to renew their certificates."
                : "Cameras sign in to the EST server with their Axis device ID for new certificates and renewals.";
        }

        var ca = CaName(policy.CaId, caNames);
        return renewals
            ? $"Cameras with a certificate from {ca} may sign in to renew it."
            : $"Cameras with a certificate from {ca} may sign in to the EST server for new certificates and renewals.";
    }

    /// <summary>The name of a CA in a sentence: its name in PKI, "Axis device ID", "OADM CA" while it is being made, or that it was removed.</summary>
    private static string CaName(string? caId, IReadOnlyDictionary<string, string> caNames) =>
        caId is { } id && caNames.TryGetValue(id, out var name) ? name
        : string.Equals(caId, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.OrdinalIgnoreCase) ? AxisDeviceIdName
        : string.Equals(caId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase) ? IssuingCaName
        : PolicyTexts.RemovedCa;

    /// <summary>"OADM Root CA SERVER01, 365 days, ECC".</summary>
    public static string IssuingSummary(PkiPolicy policy, IReadOnlyDictionary<string, string> caNames)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caNames);
        var ca = policy.CaId is { } id && caNames.TryGetValue(id, out var name) ? name
            : string.Equals(policy.CaId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase) ? IssuingCaName : PolicyTexts.RemovedCa;
        return $"{ca}, {PolicyTexts.DaysText(policy.ValidDays)}, {PolicyKeys.Name(policy.Key)}";
    }

    /// <summary>The first-start root CA in a summary while it has no certificate (being made).</summary>
    public const string IssuingCaName = "OADM Root CA";

    /// <summary>The names of PKI's CAs by id (previous OADM CAs too: a Sign-in CA policy names them).</summary>
    private Dictionary<string, string> CaNames() =>
        _authorities().GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The overlaps of the same kind among <paramref name="devices"/>: per policy one line per other policy it shares devices with.</summary>
    private static Dictionary<Guid, string> Warnings(Snapshot snapshot, IReadOnlyList<IDeviceInfo> devices)
    {
        var warnings = new Dictionary<Guid, string>();
        foreach (var (kind, policies) in snapshot.ByKind)
        {
            if (!PolicyKinds.HasDevices(kind) || policies.Count < 2)
            {
                continue;
            }

            var matchers = policies.Select(p => PolicyMatcher.Of(p, snapshot.TagsProtected)).ToArray();
            var pairs = new Dictionary<(int, int), (IDeviceInfo First, int Count)>();
            var holders = new List<int>(4);
            foreach (var device in devices)
            {
                holders.Clear();
                for (var i = 0; i < matchers.Length; i++)
                {
                    if (matchers[i].Holds(device))
                    {
                        holders.Add(i);
                    }
                }

                for (var a = 0; a < holders.Count; a++)
                {
                    for (var b = a + 1; b < holders.Count; b++)
                    {
                        if (matchers[holders[a]].HoldsEveryDevice || matchers[holders[b]].HoldsEveryDevice)
                        {
                            continue; // below: an "All devices" policy overlaps every other of its kind
                        }

                        var key = (holders[a], holders[b]);
                        pairs[key] = pairs.TryGetValue(key, out var known) ? (known.First, known.Count + 1) : (device, 1);
                    }
                }
            }

            var texts = new List<(int A, int B, string Text)>();
            foreach (var ((a, b), (first, count)) in pairs)
            {
                texts.Add((a, b, PolicyTexts.Overlap(DeviceLabel(first), count - 1, policies[a].Name, policies[b].Name)));
            }

            for (var a = 0; a < policies.Count; a++)
            {
                for (var b = a + 1; b < policies.Count; b++)
                {
                    if (matchers[a].HoldsEveryDevice || matchers[b].HoldsEveryDevice)
                    {
                        var (all, other) = matchers[a].HoldsEveryDevice ? (a, b) : (b, a);
                        texts.Add((a, b, PolicyTexts.OverlapAll(policies[all].Name, policies[other].Name)));
                    }
                }
            }

            foreach (var (a, b, overlap) in texts.OrderBy(t => t.A).ThenBy(t => t.B))
            {
                var text = overlap;
                if (Applies(policies[a], policies[b]) is { } applies)
                {
                    text += " " + PolicyTexts.Applies(applies.Name);
                }

                foreach (var id in new[] { policies[a].Id, policies[b].Id })
                {
                    warnings[id] = warnings.TryGetValue(id, out var existing) ? existing + " " + text : text;
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// Which of two overlapping policies applies (<paramref name="older"/> is the older): the older VLAN, the longer warning;
    /// of two Enrollment policies the one that allows fewer profiles (a device may use only the profiles every policy that
    /// holds it allows), null when each allows one the other does not.
    /// </summary>
    private static PkiPolicy? Applies(PkiPolicy older, PkiPolicy newer) => older.Kind switch
    {
        PolicyKinds.Vlan => older,
        PolicyKinds.ExpiryWarning => (newer.Days ?? 0) > (older.Days ?? 0) ? newer : older,
        PolicyKinds.Enrollment => Narrower(older, newer),
        _ => null,
    };

    /// <summary>The Enrollment policy whose profiles are within the other's; null when neither is.</summary>
    private static PkiPolicy? Narrower(PkiPolicy a, PkiPolicy b)
    {
        static HashSet<string>? Allowed(PkiPolicy p) => p.ProfileScope switch
        {
            PolicyProfiles.All => null,
            PolicyProfiles.Chosen => new HashSet<string>(p.Profiles ?? [], StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

        var (first, second) = (Allowed(a), Allowed(b));
        if (first is null)
        {
            return b; // all profiles: the other is within it
        }

        if (second is null || first.IsSubsetOf(second))
        {
            return a;
        }

        return second.IsSubsetOf(first) ? b : null;
    }

    private static string DeviceLabel(IDeviceInfo device) => string.IsNullOrWhiteSpace(device.Address) ? device.Serial : device.Address;

    /// <summary>
    /// The first device the saved policy shares with another of its kind that it did not share before this save (an overlap
    /// stored earlier, e.g. by the conversion, never blocks an edit of something else); null when there is none. One pass over
    /// the devices per other policy of the kind, only over the devices the policy holds.
    /// </summary>
    private string? NewOverlap(PkiPolicy policy, PkiPolicy? before, List<PkiPolicy> sameKind, IReadOnlyList<IDeviceInfo> devices)
    {
        if (sameKind.Count == 0)
        {
            return null;
        }

        var now = PolicyMatcher.Of(policy, TagsProtected);
        var then = before is null ? null : PolicyMatcher.Of(before, TagsProtected);
        var others = sameKind.Select(p => PolicyMatcher.Of(p, TagsProtected)).ToArray();

        // "All devices" overlaps every other policy of its kind, devices or not; refused unless that overlap was there before.
        for (var i = 0; i < others.Length; i++)
        {
            var overlapsNow = now.HoldsEveryDevice || others[i].HoldsEveryDevice;
            var overlappedBefore = then is not null && (then.HoldsEveryDevice || others[i].HoldsEveryDevice);
            if (overlapsNow && !overlappedBefore)
            {
                return now.HoldsEveryDevice ? PolicyTexts.OverlapAll(policy.Name, sameKind[i].Name) : PolicyTexts.OverlapAll(sameKind[i].Name, policy.Name);
            }
        }

        var first = new IDeviceInfo?[others.Length];
        var more = new int[others.Length];
        foreach (var device in devices)
        {
            if (!now.Holds(device))
            {
                continue;
            }

            var heldBefore = then?.Holds(device) ?? false;
            for (var i = 0; i < others.Length; i++)
            {
                if (others[i].Holds(device) && !heldBefore)
                {
                    if (first[i] is null)
                    {
                        first[i] = device;
                    }
                    else
                    {
                        more[i]++;
                    }
                }
            }
        }

        for (var i = 0; i < others.Length; i++)
        {
            if (first[i] is { } device && !now.HoldsEveryDevice && !others[i].HoldsEveryDevice)
            {
                return PolicyTexts.Overlap(DeviceLabel(device), more[i], policy.Name, sameKind[i].Name);
            }
        }

        return null;
    }

    /// <summary>
    /// A converted policy the list already has in its own way (a conversion that runs after policies were made): an Allow CA
    /// of the same CA merges into the one there (only devices OADM manages when either was: the stricter), a second 802.1X
    /// server policy and a second "All devices" VLAN policy (the Default VLAN converted before) are not added. True when
    /// <paramref name="policy"/> is not to be added.
    /// </summary>
    private static bool MergedInto(List<PkiPolicy> list, PkiPolicy policy)
    {
        switch (policy.Kind)
        {
            case PolicyKinds.AllowCa:
                var index = list.FindIndex(p => p.Kind == PolicyKinds.AllowCa && string.Equals(p.CaId, policy.CaId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return false;
                }

                var onlyManaged = (list[index].OnlyManaged ?? true) || (policy.OnlyManaged ?? true);
                list[index] = list[index] with { OnlyManaged = onlyManaged };
                return true;
            case PolicyKinds.Dot1xServer:
                return list.Any(p => p.Kind == PolicyKinds.Dot1xServer);
            case PolicyKinds.EstServer:
                return list.Any(p => p.Kind == PolicyKinds.EstServer);
            case PolicyKinds.Vlan when policy.Devices?.AllDevices == true:
                return list.Any(p => p.Kind == PolicyKinds.Vlan
                    && (p.Devices?.AllDevices == true || string.Equals(p.Name.Trim(), PolicyLimits.AllDevicesName, StringComparison.OrdinalIgnoreCase)));
            case PolicyKinds.SignInCa:
                var signIn = list.FindIndex(p => p.Kind == PolicyKinds.SignInCa && string.Equals(p.CaId, policy.CaId, StringComparison.OrdinalIgnoreCase));
                if (signIn < 0)
                {
                    return false;
                }

                // The same CA once: for renewals only when either was (the stricter).
                var renewalOnly = list[signIn].SignInFor == PolicySignIn.Renewal || policy.SignInFor == PolicySignIn.Renewal;
                list[signIn] = list[signIn] with { SignInFor = renewalOnly ? PolicySignIn.Renewal : PolicySignIn.New };
                return true;
            case PolicyKinds.Enrollment:
                // Enrollment policies made before the conversion decide: an "All devices" one would let every device in.
                return list.Any(p => p.Kind == PolicyKinds.Enrollment);
            default:
                return false;
        }
    }

    /// <summary>A name not taken by another policy of the same kind: "All devices", "All devices (2)".</summary>
    private static string UniqueName(List<PkiPolicy> list, PkiPolicy policy)
    {
        var name = string.IsNullOrWhiteSpace(policy.Name) ? PolicyKinds.Name(policy.Kind) : policy.Name.Trim();
        var baseName = name.Length > PolicyLimits.MaxNameLength - 5 ? name[..(PolicyLimits.MaxNameLength - 5)] : name;
        var candidate = name.Length <= PolicyLimits.MaxNameLength ? name : baseName;
        for (var n = 2; list.Any(p => p.Kind == policy.Kind && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{baseName} ({n})");
        }

        return candidate;
    }

    private static List<PkiPolicy> WithUniqueIds(IReadOnlyList<PkiPolicy> list)
    {
        var seen = new HashSet<Guid>();
        return [.. list.Where(p => p is not null).Select(p => p.Id != Guid.Empty && seen.Add(p.Id) ? p : p with { Id = Guid.NewGuid() })];
    }

    private static PolicySaveResult Refused(string field, string message) => new(null, new PolicyFieldError(field, message));

    /// <summary>Writes and swaps the list (inside the gate); readers see the new one only after the write.</summary>
    private async Task StoreAsync(StoredPolicies stored, CancellationToken ct)
    {
        await _settings.SetAsync(SettingsKey, PkiJson.Serialize(stored), ct).ConfigureAwait(false);
        _snapshot = new Snapshot(stored, _snapshot.Version + 1, TagsProtected);
    }

    /// <summary>The tags of the policies are protected.</summary>
    private void Protect(IEnumerable<PkiPolicy> policies) =>
        _protectedTags?.Protect(PolicyMatcher.TagsOf(policies), PolicyTexts.TagRefusal, PolicyTexts.TagChangeRefusal);

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Error, Message = "PKI: the stored policies cannot be read; nothing is converted or written, the 802.1X server refuses every device. Restore a backup.")]
    private partial void LogUnreadable();

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: {Count} policies made from the settings of {Owner}")]
    private partial void LogConverted(int count, string owner);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: policy {Name} ({Kind}) saved by {User}")]
    private partial void LogSaved(string name, string kind, string user);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: policy {Name} ({Kind}) deleted by {User}")]
    private partial void LogDeleted(string name, string kind, string user);

    private sealed class Snapshot
    {
        private readonly (PolicyMatcher Matcher, int Days)[] _expiry;

        public Snapshot(StoredPolicies stored, long version, bool tagsProtected, bool unreadable = false)
        {
            Unreadable = unreadable;
            Stored = stored;
            Version = version;
            TagsProtected = tagsProtected;
            ByKind = stored.Policies.GroupBy(p => p.Kind, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<PkiPolicy>)[.. g], StringComparer.Ordinal);
            _expiry = [.. (ByKind.GetValueOrDefault(PolicyKinds.ExpiryWarning) ?? []).Select(p => (PolicyMatcher.Of(p, tagsProtected), p.Days ?? PolicyLimits.DefaultExpiryDays))];
            DefaultExpiryDays = _expiry.Length == 0 ? PolicyLimits.DefaultExpiryDays : _expiry.Max(e => e.Days);
        }

        public StoredPolicies Stored { get; }

        public bool Unreadable { get; }

        public long Version { get; }

        public bool TagsProtected { get; }

        public Dictionary<string, IReadOnlyList<PkiPolicy>> ByKind { get; }

        public int DefaultExpiryDays { get; }

        public int ExpiryDaysFor(IDeviceInfo device)
        {
            var days = 0;
            foreach (var (matcher, policyDays) in _expiry)
            {
                if (policyDays > days && matcher.Holds(device))
                {
                    days = policyDays;
                }
            }

            return days == 0 ? PolicyLimits.DefaultExpiryDays : days;
        }
    }
}
