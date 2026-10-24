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

            // A server keeps its policy: it cannot be moved to the other server, which would leave this one without a certificate.
            if (before is { Kind: PolicyKinds.ServerCertificate } && before.Server != policy.Server)
            {
                return Refused(PolicyFields.Server, PolicyTexts.ServerKept);
            }

            // One Server certificate policy per server (each server gets its certificate from exactly one).
            if (policy.Kind == PolicyKinds.ServerCertificate && sameKind.Any(p => p.Server == policy.Server))
            {
                return Refused(PolicyFields.Server, PolicyTexts.ServerTaken(policy.Server));
            }

            if (policy.Kind == PolicyKinds.AllowCa)
            {
                if (!_authorities().Any(a => a.Kind != CertificateAuthorityKind.PreviousOadm && string.Equals(a.Id, policy.CaId, StringComparison.OrdinalIgnoreCase)))
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

            policy = policy with { Id = before?.Id ?? Guid.NewGuid(), SavedBy = userName };
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

    /// <summary>The Server certificate policy of <paramref name="server"/> (<see cref="PolicyServers"/>); null when there is none.</summary>
    public PkiPolicy? ServerCertificateFor(string server) => PoliciesOf(PolicyKinds.ServerCertificate).FirstOrDefault(p => p.Server == server);

    /// <summary>
    /// Why <paramref name="policy"/> cannot be deleted: a Server certificate policy (its server needs it), the last policy of a
    /// device issuing kind (the task needs one), an issuing policy an EST profile names. Null when it can.
    /// </summary>
    private async Task<string?> DeleteRefusalAsync(PkiPolicy policy, IReadOnlyList<PkiPolicy> list, CancellationToken ct)
    {
        if (policy.Kind == PolicyKinds.ServerCertificate)
        {
            return PolicyTexts.ServerNeedsPolicy(policy.Server);
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

        return list.Count(p => p.Kind == policy.Kind) <= 1 ? PolicyTexts.LastIssuing(policy.Kind) : null;
    }

    /// <summary>
    /// The rows of the card: every policy with its summary and the warning of an overlap a tag or site change made later
    /// (checked against <paramref name="devices"/>: both rows warn and name the one that applies).
    /// </summary>
    public IReadOnlyList<PolicyRow> Rows(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var snapshot = _snapshot;
        var names = CaNames();
        var warnings = Warnings(snapshot, devices);
        return
        [
            .. snapshot.Stored.Policies.Select(p => new PolicyRow(p, PolicyKinds.Name(p.Kind), PolicyKinds.GroupOf(p.Kind), Summary(p, names), warnings.GetValueOrDefault(p.Id))),
        ];
    }

    /// <summary>The CAs an Allow CA policy can name (previous OADM CAs follow the OADM CA).</summary>
    public IReadOnlyList<PolicyCaChoice> CaChoices()
    {
        var all = _authorities();
        var previous = all.Count(a => a.Kind == CertificateAuthorityKind.PreviousOadm);
        return
        [
            .. all.Where(a => a.Kind != CertificateAuthorityKind.PreviousOadm).Select(a => new PolicyCaChoice(a.Id, a.Name, a.Kind)
            {
                PreviousCount = a.Kind == CertificateAuthorityKind.Oadm ? previous : 0,
            }),
        ];
    }

    /// <summary>
    /// The CAs a Sign-in CA policy can name: the Axis device ID, the OADM CA and each previous OADM CA (EST reads a camera's
    /// serial only from the Axis device ID or the axis name the OADM CA put in its certificates; imported CAs never sign a
    /// camera in). The OADM CA is listed while it has no certificate yet.
    /// </summary>
    public IReadOnlyList<PolicyCaChoice> SignInCaChoices()
    {
        var all = _authorities();
        var choices = all.Where(a => a.Kind is CertificateAuthorityKind.AxisDeviceId or CertificateAuthorityKind.Oadm or CertificateAuthorityKind.PreviousOadm)
            .OrderBy(a => a.Kind switch { CertificateAuthorityKind.AxisDeviceId => 0, CertificateAuthorityKind.Oadm => 1, _ => 2 })
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(a => new PolicyCaChoice(a.Id, string.IsNullOrWhiteSpace(a.Name) ? DefaultCaName(a.Id) : a.Name, a.Kind)).ToList();
        if (!choices.Any(c => c.Kind == CertificateAuthorityKind.AxisDeviceId))
        {
            choices.Insert(0, new PolicyCaChoice(CertificateAuthorityInfo.AxisDeviceIdId, AxisDeviceIdName, CertificateAuthorityKind.AxisDeviceId));
        }

        if (!choices.Any(c => c.Kind == CertificateAuthorityKind.Oadm))
        {
            choices.Insert(1, new PolicyCaChoice(CertificateAuthorityInfo.OadmId, IssuingCaName, CertificateAuthorityKind.Oadm));
        }

        return choices;
    }

    /// <summary>The name of the Axis device ID CAs.</summary>
    public const string AxisDeviceIdName = "Axis device ID";

    private static string DefaultCaName(string id) =>
        string.Equals(id, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.OrdinalIgnoreCase) ? AxisDeviceIdName : IssuingCaName;

    /// <summary>The Allow CA policies that name <paramref name="authorityId"/> (a previous OADM CA: those of the OADM CA).</summary>
    public IReadOnlyList<PkiPolicy> UsingCa(string authorityId, CertificateAuthorityKind kind)
    {
        var id = kind == CertificateAuthorityKind.PreviousOadm ? CertificateAuthorityInfo.OadmId : authorityId;
        return [.. PoliciesOf(PolicyKinds.AllowCa).Where(p => string.Equals(p.CaId, id, StringComparison.OrdinalIgnoreCase))];
    }

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

    /// <summary>The row's summary: "VLAN 30", "OADM CA, only devices OADM manages", "TLS 1.2, 1,020 bytes, switch decides reauthentication", "30 days".</summary>
    public static string Summary(PkiPolicy policy, IReadOnlyDictionary<string, string> caNames)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caNames);
        return policy.Kind switch
        {
            PolicyKinds.Vlan => string.Create(CultureInfo.InvariantCulture, $"VLAN {policy.Vlan}"),
            PolicyKinds.Block => "Refused by the 802.1X server",
            PolicyKinds.Revoke => "IEEE 802.1X certificates revoked",
            PolicyKinds.ExpiryWarning => string.Create(CultureInfo.CurrentCulture, $"{policy.Days:N0} {(policy.Days == 1 ? "day" : "days")}"),
            PolicyKinds.AllowCa => (policy.CaId is { } id && caNames.TryGetValue(id, out var name) ? name : PolicyTexts.RemovedCa) + ", "
                + PolicyTexts.AllowCaScope(policy.OnlyManaged ?? true),
            PolicyKinds.Dot1xServer => string.Create(CultureInfo.CurrentCulture,
                $"TLS {policy.MinimumTls} or later, {policy.LargestEapPacket:N0} bytes, {PolicyTexts.Reauth(policy.ReauthMinutes)}"),
            PolicyKinds.HttpsCertificate or PolicyKinds.Dot1xCertificate => IssuingSummary(policy, caNames),
            PolicyKinds.ServerCertificate => IssuingSummary(policy, caNames) + ", " + PolicyServers.Name(policy.Server),
            PolicyKinds.Enrollment => PolicyTexts.EnrollmentSummary(policy),
            PolicyKinds.SignInCa => (policy.CaId is { } signIn && caNames.TryGetValue(signIn, out var signInName) ? signInName
                : string.Equals(policy.CaId, CertificateAuthorityInfo.AxisDeviceIdId, StringComparison.OrdinalIgnoreCase) ? AxisDeviceIdName
                : string.Equals(policy.CaId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase) ? IssuingCaName
                : PolicyTexts.RemovedCa) + ", " + PolicyTexts.SignInForText(policy.SignInFor),
            _ => PolicyKinds.Name(policy.Kind),
        };
    }

    /// <summary>"OADM Root CA SERVER01, 365 days, ECC".</summary>
    public static string IssuingSummary(PkiPolicy policy, IReadOnlyDictionary<string, string> caNames)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caNames);
        var ca = policy.CaId is { } id && caNames.TryGetValue(id, out var name) ? name
            : string.Equals(policy.CaId, CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase) ? IssuingCaName : PolicyTexts.RemovedCa;
        return $"{ca}, {PolicyTexts.DaysText(policy.ValidDays)}, {PolicyKeys.Name(policy.Key)}";
    }

    /// <summary>The OADM CA in a summary while it has no certificate (being made).</summary>
    public const string IssuingCaName = "OADM CA";

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
            case PolicyKinds.ServerCertificate:
                return list.Any(p => p.Kind == PolicyKinds.ServerCertificate && p.Server == policy.Server);
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

    /// <summary>The tags of the policies are protected (Revoke never matches by tag).</summary>
    private void Protect(IEnumerable<PkiPolicy> policies) =>
        _protectedTags?.Protect(PolicyMatcher.TagsOf(policies.Where(p => !PolicyKinds.NoTags(p.Kind))), PolicyTexts.TagRefusal, PolicyTexts.TagChangeRefusal);

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
