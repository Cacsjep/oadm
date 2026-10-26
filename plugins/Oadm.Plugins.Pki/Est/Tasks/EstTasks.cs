using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceClocks;
using Oadm.Plugins.Est.Device;
using Oadm.Plugins.Est.Serving;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Est.Tasks;

/// <summary>Ids, names and texts of the EST tasks.</summary>
public static class EstTaskIds
{
    public const string Setup = "oadm.est.setup";

    /// <summary>The menu entry (Security) and the dialog.</summary>
    public const string MenuName = "EST - Certificate Enrollment";

    /// <summary>The name of a run that sets up.</summary>
    public const string SetupName = "Set up certificate enrollment";

    /// <summary>The dialog's read-only query: the camera's enrollment profiles (<see cref="CameraProfileList"/>).</summary>
    public const string ListProfilesMethod = "listProfiles";

    /// <summary>The name of a run that removes profiles.</summary>
    public static string RemoveName(IReadOnlyList<string> profiles) =>
        profiles.Count == 1 ? "Remove enrollment profile " + profiles[0] : string.Create(CultureInfo.InvariantCulture, $"Remove {profiles.Count} enrollment profiles");

    public const string RemoveStep = "Remove the enrollment profiles";
    public const string BadOptions = "The task options cannot be read. Nothing was changed.";
    public const string OnlyDot1x = "IEEE 802.1X is on: its profile was skipped and no other profile applies. Nothing was changed.";
    public const string KeptOldCertificate = "The camera kept its previous certificate: the EST server issued no new one within 2 minutes. The Enrollments list shows what reached the server.";
    public const string CameraNotRead = "OADM could not read the camera's enrollment status within 2 minutes.";
    public const string NoRoom = "The camera's other enrollment profiles leave no room (two at most). Remove one in Certificate enrollment first. Nothing was changed.";
    public const string Dot1xParameter = "Network.Interface.I0.dot1x.Enabled";

    public static string ServedByNetworkAccessProfile(string profile, string kept) =>
        $"The camera's IEEE 802.1X profile {kept} also serves the {profile} profile's use: the {profile} profile was skipped.";
    public const string NothingToRemove = "The camera has none of these enrollment profiles. Nothing was changed.";

    /// <summary>Camera profiles OADM writes are named "oadm_&lt;profile&gt;" (or <see cref="AlternatePrefix"/>).</summary>
    public const string ProfilePrefix = "oadm_";

    /// <summary>
    /// The second name of OADM's profile ("oadm2_https"): the camera stores the certificate under the profile's name and
    /// keeps an older certificate of that name bound to its use after the profile is gone ("Certificate is in use", seen on
    /// 10.0.0.48), so a profile added again takes the other name. Never produced by a profile name (prefix "oadm_").
    /// </summary>
    public const string AlternatePrefix = "oadm2_";

    /// <summary>A camera profile OADM wrote (either name).</summary>
    public static bool IsOadm(string id) =>
        id.StartsWith(ProfilePrefix, StringComparison.Ordinal) || id.StartsWith(AlternatePrefix, StringComparison.Ordinal);

    /// <summary>The EST server profile of one of OADM's camera profiles ("oadm2_https" -> "https").</summary>
    public static string ProfileNameOf(string id) =>
        id.StartsWith(AlternatePrefix, StringComparison.Ordinal) ? id[AlternatePrefix.Length..]
        : id.StartsWith(ProfilePrefix, StringComparison.Ordinal) ? id[ProfilePrefix.Length..]
        : id;

    /// <summary>Both names OADM may give the camera profile of <paramref name="profile"/>.</summary>
    public static IReadOnlyList<string> IdsOf(string profile) => [ProfilePrefix + profile, AlternatePrefix + profile];

    public static string NoFreeName(string profile) =>
        $"The camera still uses its older certificates of the {profile} profile and keeps no name free for a new one. The other profiles were set up.";

    public const string NotStored = "The EST server issued the certificate, but the camera did not store it. The previous certificate stays in use.";

    /// <summary>"OADM CA 3FA2B4C1": the same aliases as the PKI plugin, so both reuse a CA certificate already on the camera.</summary>
    public const string CaAliasPrefix = "OADM CA ";

    public const string MinimumFirmware = "12.10";

    /// <summary>Profiles one camera holds at most.</summary>
    public const int MaxProfilesPerCamera = 2;

    public const string NotRunning = "The EST server is off. Turn it on on the EST server page. Nothing was changed.";
    public const string NoPro = "OADM PRO is not active. Nothing was changed.";
    public const string NoCa = "The PKI has no certificate authority. Create one on the PKI page. Nothing was changed.";
    public const string Dot1xSkipped = "IEEE 802.1X is on: the IEEE 802.1X profile was skipped, so the camera keeps the certificate it uses for network access.";
    public const string Dot1xReplaced = "IEEE 802.1X is on: the IEEE 802.1X profile replaces the certificate the camera uses for network access, as chosen when the task started.";
    /// <summary>At a site the camera reaches the site node, else the OADM server.</summary>
    public static string NotReached(int port, bool atSite = false) => string.Create(CultureInfo.InvariantCulture,
        $"The camera could not reach the EST server within 2 minutes. Check that a firewall lets the camera reach {(atSite ? "the site node" : "the OADM server")} on TCP {port}.");

    public const string NoCertificate = "The camera reached the EST server but got no certificate within 2 minutes. The Enrollments list shows what reached the server.";
    public const string AlreadySetUp = "The camera already has these enrollment profiles. No profile was written.";

    /// <summary>The right to run the EST tasks: not for operators unless an administrator gives it.</summary>
    public const string EnrollPermission = "enroll";
    public const string NeedsApis = "The camera has no certificate enrollment. Needs AXIS OS 12.10 or later. Nothing was changed.";
    public const string NoDeviceId = "The camera has no Axis device ID certificate. Nothing was changed.";
    public const string NoProfile = "The camera supports none of the EST server's profiles. Nothing was changed.";

    /// <summary>No Enrollment policy of PKI holds the camera.</summary>
    public const string NoEnrollmentPolicy = "No enrollment policy for this device. Add one on the PKI page. Nothing was changed.";

    /// <summary>The Enrollment policies allow none of the profiles the camera supports.</summary>
    public const string NoAllowedProfile = "The enrollment policies allow none of the profiles the camera supports. Nothing was changed.";
    public const string NoAddress = "OADM has no address towards the camera. Nothing was changed.";
    public static string InterfaceMissing(string notAvailable) => $"The EST server does not listen: {notAvailable}. Nothing was changed.";

    public static string NoInterfaceAddress(string name, AddressFamily family) =>
        $"The EST server listens on {name}, which has no {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address for this camera. Nothing was changed.";

    public const string NoSiteRelay = "The EST server cannot listen at the camera's site. Nothing was changed.";

    /// <summary>The CA that issued the EST server's certificate is not in PKI's CA list: the camera could not check the server.</summary>
    public const string ServerIssuerUnknown = "The CA that issued the EST server's certificate is not in the CA list. Choose another certificate in the EST server policy. Nothing was changed.";

    /// <summary>The EST server cannot use its certificate (none, ended, revoked, key unreadable): its reason.</summary>
    public static string ServerCertificateProblem(string problem) => problem.TrimEnd() + " Nothing was changed.";

    /// <summary>The address the camera would get is not a name of the EST server's certificate: the camera would refuse the server.</summary>
    public static string NotInCertificate(string address) =>
        $"{address} is not in the EST server's certificate. Create a certificate with it on the Certificates tab and choose it in the EST server policy. Nothing was changed.";

    // Steps: imperative, with the article.
    public const string CheckStep = "Check compatibility";
    public const string ReadStep = "Read the camera's certificates";
    public const string CaStep = "Install the CA certificates";
    public const string RemoveOldStep = "Remove the camera's enrollment profiles";
    public const string PinStep = "Pin the new HTTPS certificate";

    /// <summary>With the dot1x profile (decision 2026-10-25): the camera checks the RADIUS server with the policy's CA.</summary>
    public const string RadiusCaInstallStep = "Install the RADIUS server CA";

    public const string RadiusCaStep = "Set the RADIUS server CA";

    public static string AddStep(string profile) => $"Add the {profile} profile";

    public static string WaitStep(string profile) => $"Wait for the {profile} certificate";

    /// <summary>AXIS OS 12.10 or later ("12.11.77"); false for unknown versions.</summary>
    public static bool FirmwareSupported(string? firmware)
    {
        var parts = (firmware ?? string.Empty).Trim().Split('.');
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(new string([.. parts[1].TakeWhile(char.IsDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && (major > 12 || (major == 12 && minor >= 10));
    }
}

/// <summary>Common part of the EST tasks: group Security, AXIS OS 12.10+, not at sites yet (stage 4).</summary>
public abstract class EstTaskBase(Func<EstService?> service, Func<bool>? shown = null) : ITaskPlugin
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public string? IconKey => EstPluginInfo.IconKey;

    public string Group => TaskGroups.Security;

    public bool ShowInToolbar => false;

    /// <summary>In the device menu only with OADM PRO (decision 2026-10-25); null (tests): always.</summary>
    public bool ShowInMenus => shown?.Invoke() ?? true;

    /// <summary>The dialog: the cameras' enrollment profiles, Set up and Remove.</summary>
    public bool RequiresDialog => true;

    /// <summary>Cameras at a site enroll through their site node (a relay of the EST port).</summary>
    public Sdk.Sites.SiteSupport? SiteSupport => Sdk.Sites.SiteSupport.Full;

    protected EstService? Service => service();

    /// <summary>Setting up and removing change how cameras get certificates: a right of its own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(EstTaskIds.EnrollPermission, "Set up certificate enrollment", "Set up and remove certificate enrollment on cameras.") { OperatorDefault = false },
    ];

    public IReadOnlyList<string>? RequiredPermissions(string? payloadJson) => [EstTaskIds.EnrollPermission];

    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return EstTaskIds.FirmwareSupported(device.FirmwareVersion);
    }

    public string? NotSupportedReason(IDeviceInfo device) =>
        CanRun(device) ? null : TaskSupportReasons.NeedsFirmware(EstTaskIds.MinimumFirmware, device);

    public abstract Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);

    /// <summary>Fresh API list: cert v1 and cert-est v1, else "Nothing was changed".</summary>
    protected static async Task RequireApisAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        var apis = await CameraEstApi.DiscoverAsync(ctx.Vapix, ct).ConfigureAwait(false);
        if (!apis.HasCert || !apis.HasCertEst)
        {
            throw new DeviceNotCompatibleException(EstTaskIds.NeedsApis);
        }
    }
}

/// <summary>
/// "Set up certificate enrollment" (docs/spec/est-server.md "Setting up a camera"): the camera's own EST client gets the
/// EST server's profiles (at most two, the uses the camera supports), trusts the OADM CA, signs in with its Axis device
/// ID, enrolls and renews by itself. Safe to run again: the same set writes nothing.
/// </summary>
public sealed class EstSetupTask(Func<EstService?> service, Func<bool>? shown = null) : EstTaskBase(service, shown), ITaskPlugin, ITaskPluginQuery
{
    private static readonly TimeSpan EnrollTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PinTimeout = TimeSpan.FromSeconds(60);

    public override string Id => EstTaskIds.Setup;

    public override string DisplayName => EstTaskIds.MenuName;

    /// <summary>"Set up certificate enrollment" or "Remove enrollment profile oadm_https" in the tasks pane.</summary>
    string ITaskPlugin.GetTaskName(string? payloadJson) =>
        SetupPayload.Read(payloadJson) is { Action: SetupAction.Remove } remove ? EstTaskIds.RemoveName(remove.Profiles) : EstTaskIds.SetupName;

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // Set up and Remove share the task: options that cannot be read change nothing (never taken for Set up).
        var payload = SetupPayload.Read(payloadJson) ?? throw new ArgumentException(EstTaskIds.BadOptions, nameof(payloadJson));
        if (payload.Action == SetupAction.Remove)
        {
            await RemoveAsync(ctx, device, payload.Profiles, ct).ConfigureAwait(false);
            return;
        }

        ArgumentNullException.ThrowIfNull(device);
        var protectNetworkAccess = false;
        var dot1xService = CameraEstApi.ServiceOf(ProfileUse.Dot1x);
        IReadOnlyList<(string Alias, string Pem, string Fingerprint)>? radiusCas = null;
        var (est, chain, profiles, address) = await ctx.StepAsync(EstTaskIds.CheckStep, async step =>
        {
            var est = Service ?? throw new InvalidOperationException(ctx.Pro?.IsActive == true ? EstTaskIds.NotRunning : EstTaskIds.NoPro);
            if (!est.ProActive)
            {
                throw new InvalidOperationException(EstTaskIds.NoPro);
            }

            if (est.Authority is not { IsReady: true } ca)
            {
                throw new InvalidOperationException(EstTaskIds.NoCa);
            }

            // The camera checks the EST server with the issuer chain of the certificate the EST server policy picks (that
            // may be a previous or an imported CA), never simply the OADM CA; the address it gets must be in that certificate.
            var (chain, serverNames) = ServerTrust(ca);
            await RequireApisAsync(ctx, ct).ConfigureAwait(false);
            await CheckClockAsync(ctx, device, est.Time, payload.ClockConfirmed, ct).ConfigureAwait(false);
            // supportedServices lists the free services only: those of the camera's profiles count too.
            var (free, existing) = await CameraEstApi.ReadEstAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var services = free.Concat(existing.SelectMany(p => p.Services)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = est.Store.Config.Profiles.Where(p => p.Enabled && services.Contains(CameraEstApi.ServiceOf(p.Use))).ToList();

            // Every profile issues with its policy (valid days, key): one that cannot be read stops the run before the first write.
            if (candidates.FirstOrDefault(p => est.IssuingOf(p) is null) is { } orphan)
            {
                throw new InvalidOperationException(EstTexts.ProfileWithoutPolicy(orphan.Name));
            }

            // PKI's Enrollment policies, before the first write: a camera none holds is not set up; profiles a policy does
            // not allow it are skipped (the EST server would refuse them).
            var rules = est.Rules;
            var refusedProfiles = 0;
            foreach (var profile in candidates.ToList())
            {
                var check = rules.Check(device, profile.Name);
                if (check is { Allowed: false, Status: 403 })
                {
                    throw new DeviceNotCompatibleException(EstTaskIds.NoEnrollmentPolicy);
                }

                if (!check.Allowed)
                {
                    candidates.Remove(profile);
                    refusedProfiles++;
                    step.Warn(check.Reason ?? EstTaskIds.NoAllowedProfile);
                }
            }

            if (refusedProfiles > 0 && candidates.Count == 0)
            {
                throw new DeviceNotCompatibleException(EstTaskIds.NoAllowedProfile);
            }

            // IEEE 802.1X read fresh from the camera, never the cached state; unknown counts as on (no lock-out).
            var dot1xOn = await Dot1xOnAsync(ctx, ct).ConfigureAwait(false);
            var before = candidates.Count;
            if (dot1xOn && payload.ReplaceDot1x)
            {
                ctx.Log(TaskLogLevel.Info, EstTaskIds.Dot1xReplaced); // the user chose it in the dialog
            }
            else if (dot1xOn)
            {
                // No lock-out: a new network access certificate the RADIUS server may not trust would take the camera off
                // the network, and a removed profile would stop renewing the one it has. Its profiles stay untouched.
                if (candidates.RemoveAll(p => p.Use == ProfileUse.Dot1x) > 0)
                {
                    step.Warn(EstTaskIds.Dot1xSkipped);
                }

                // A use a kept network access profile serves too: the camera takes one profile per use.
                var kept = existing.Where(e => e.Services.Contains(dot1xService, StringComparer.OrdinalIgnoreCase)).ToList();
                foreach (var taken in candidates.Where(c => kept.Any(k => k.Services.Contains(CameraEstApi.ServiceOf(c.Use), StringComparer.OrdinalIgnoreCase))).ToList())
                {
                    candidates.Remove(taken);
                    step.Warn(EstTaskIds.ServedByNetworkAccessProfile(taken.Name, kept.First(k => k.Services.Contains(CameraEstApi.ServiceOf(taken.Use), StringComparer.OrdinalIgnoreCase)).Id));
                }
            }

            // The camera holds at most two profiles (a third is refused, seen on 10.0.0.48): those that stay (another EST
            // server's for other uses, a kept network access profile) count. The most profiles that fit, in the order of the
            // EST server page; decided here, before the first write. HTTPS last: its certificate restarts the web server.
            // With IEEE 802.1X on, its profiles stay untouched unless this run writes OADM's IEEE 802.1X profile (replace chosen):
            // a removal without a replacement would stop the renewal of the network access certificate.
            bool Protect(IReadOnlyList<EstProfile> set) => dot1xOn && !set.Any(p => p.Use == ProfileUse.Dot1x);
            var fitting = Fitting(candidates, existing, Protect, dot1xService);
            protectNetworkAccess = Protect(fitting);
            var profiles = fitting.OrderBy(p => p.Use == ProfileUse.Https).ToList();
            if (profiles.Count == 0)
            {
                throw new DeviceNotCompatibleException(
                    candidates.Count > 0 ? EstTaskIds.NoRoom
                    : before > 0 && dot1xOn ? EstTaskIds.OnlyDot1x
                    : EstTaskIds.NoProfile);
            }

            var address = ctx.Network is { } network ? await network.LocalAddressTowardsAsync(device.Address, ct).ConfigureAwait(false) : null;
            if (address is null)
            {
                throw new DeviceNotCompatibleException(EstTaskIds.NoAddress);
            }

            if (device.SiteId is null && !InterfaceOptions.IsAll(est.Store.Config.InterfaceId))
            {
                // "Listen on" one interface: the camera gets that interface's address of its address family.
                var nic = est.ListenInterface() ?? throw new DeviceNotCompatibleException(EstTaskIds.InterfaceMissing(EstService.NotAvailable(est.Store.Config)));
                var own = EstService.ListenAddresses(nic).Where(a => a.AddressFamily == address.AddressFamily).ToList();
                address = own.FirstOrDefault(a => a.Equals(address))
                    ?? own.FirstOrDefault()
                    ?? throw new DeviceNotCompatibleException(EstTaskIds.NoInterfaceAddress(nic.Name, address.AddressFamily));
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            {
                address = new IPAddress(address.GetAddressBytes()); // the server's own scope id means nothing to the camera
            }

            // At a site the camera reaches the node: its address goes into the site's server certificate, its relay opens.
            if (device.SiteId is { } siteId && await est.ServeSiteAsync(siteId, address.ToString(), ct).ConfigureAwait(false) is null)
            {
                throw new DeviceNotCompatibleException(EstTaskIds.NoSiteRelay);
            }

            if (serverNames is not null && !serverNames.Any(n => IPAddress.TryParse(n, out var named) && named.Equals(address)))
            {
                throw new DeviceNotCompatibleException(EstTaskIds.NotInCertificate(address.ToString()));
            }

            // The dot1x profile with IEEE 802.1X on: the RADIUS server CA of its IEEE 802.1X policy, found before the first write
            // (else nothing changes). With IEEE 802.1X off nothing of it is written, so it is not needed.
            if (dot1xOn && profiles.FirstOrDefault(p => p.Use == ProfileUse.Dot1x) is { } dot1x && est.Authority is Oadm.Plugins.Pki.Ca.IRadiusCaSource pki
                && est.PolicyOf(dot1x) is { } dot1xPolicy)
            {
                radiusCas = pki.RadiusCas(dot1xPolicy);
            }

            step.Complete(string.Join(", ", profiles.Select(p => p.Name)));
            return (est, chain, profiles, address);
        }).ConfigureAwait(false);

        var deviceIds = await ctx.StepAsync(EstTaskIds.ReadStep, async step =>
        {
            var certificates = await CameraEstApi.ListCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var found = FindDeviceIds(certificates, device.Serial, est);
            if (found.Count == 0)
            {
                throw new DeviceNotCompatibleException(EstTaskIds.NoDeviceId);
            }

            step.Complete(string.Join(", ", found.Select(f => f.Alias)));
            return found;
        }).ConfigureAwait(false);

        var caAliases = await ctx.StepAsync(EstTaskIds.CaStep, async step =>
        {
            var aliases = await EnsureCaCertificatesAsync(ctx, chain, ct).ConfigureAwait(false);
            step.Complete(string.Join(", ", aliases));
            return aliases;
        }).ConfigureAwait(false);

        var wantedServices = profiles.Select(p => CameraEstApi.ServiceOf(p.Use)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // OADM's profiles and those that would serve the same services; other profiles of the camera stay, and with IEEE
        // 802.1X on (not replaced) every network access profile stays untouched (no lock-out).
        var current = (await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false))
            .Where(c => !Stays(c, wantedServices, protectNetworkAccess, dot1xService))
            .ToList();
        // OADM's profile already on the camera keeps its name (either one), so it is compared and changed in place.
        var wanted = profiles.Select(p =>
        {
            var w = Wanted(p, device, est.InAnyGroup(device), address, est.Store.Config.Port, caAliases, Pick(deviceIds, est.IssuingOf(p)?.Key ?? p.Key));
            if (current.FirstOrDefault(c => EstTaskIds.IsOadm(c.Id) && EstTaskIds.ProfileNameOf(c.Id) == p.Name) is { } have)
            {
                w.Json["id"] = have.Id;
            }

            return w;
        }).ToList();
        var started = est.Time.GetUtcNow().UtcDateTime;
        if (Same(current, wanted))
        {
            ctx.Log(TaskLogLevel.Info, EstTaskIds.AlreadySetUp);
            foreach (var (profile, json) in wanted)
            {
                await WaitEnrolledAsync(ctx, est, device, profile, json, started, requireNew: false, ct).ConfigureAwait(false);
            }

            await SetRadiusCaAsync(ctx, radiusCas, ct).ConfigureAwait(false);
            return;
        }

        // Per profile: an identical one stays as it is (no new certificate, no web server restart); every other one in the
        // way goes, also OADM's own profile that differs: the camera never stores a certificate over an older one of the
        // same name, not after a change in place either (it reported ENROLLED and kept the old one, 10.0.0.48), so the
        // profile comes back under its other name (Add) and takes the use from the older certificate.
        var unchanged = wanted.Where(w => current.Any(c => Matches(c, w.Json))).ToList();
        var unchangedIds = unchanged.Select(w => w.Json["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var remove = current.Where(c => !unchangedIds.Contains(c.Id)).ToList();
        if (remove.Count > 0)
        {
            await ctx.StepAsync(EstTaskIds.RemoveOldStep, async step =>
            {
                foreach (var old in remove)
                {
                    await CameraEstApi.RemoveProfileAsync(ctx.Vapix, old.Id, ct).ConfigureAwait(false);
                }

                step.Complete(string.Join(", ", remove.Select(c => c.Id)));
            }).ConfigureAwait(false);

            // A removed profile of OADM that is not set up again (renamed, turned off): its certificate is not renewed any more.
            var wantedIds = wanted.Select(w => w.Json["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var wantedNames = profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in remove.Where(c => EstTaskIds.IsOadm(c.Id) && !wantedIds.Contains(c.Id) && !wantedNames.Contains(EstTaskIds.ProfileNameOf(c.Id))))
            {
                est.Store.StopRenewals(device.Id, started, EstTaskIds.ProfileNameOf(gone.Id));
            }
        }

        // One at a time: writing a profile restarts the camera's EST client. A written profile is done once the camera
        // holds the new certificate (never the previous one it still has under the same name). The RADIUS server CA is set
        // before the HTTPS profile: its new certificate restarts the camera's web server.
        string? noFreeName = null;
        var radiusSet = false;
        foreach (var (profile, json) in wanted)
        {
            var id = json["id"]!.GetValue<string>();
            var written = !unchangedIds.Contains(id);
            if (written && profile.Use == ProfileUse.Https && !radiusSet)
            {
                await SetRadiusCaAsync(ctx, radiusCas, ct).ConfigureAwait(false);
                radiusSet = true;
            }

            if (written)
            {
                var free = await ctx.StepAsync(EstTaskIds.AddStep(profile.Name), async step =>
                {
                    // The camera stores the certificate under the profile's name and never over an older one of that name.
                    if (await FreeNameAsync(ctx, profile.Name, ct).ConfigureAwait(false) is not { } freeId)
                    {
                        step.Fail(EstTaskIds.NoFreeName(profile.Name));
                        return false;
                    }

                    json["id"] = freeId;
                    await AddProfileOnceAsync(ctx, json, ct).ConfigureAwait(false);
                    step.Complete(freeId + " · " + json["server"]!.GetValue<string>());
                    return true;
                }).ConfigureAwait(false);
                if (!free)
                {
                    noFreeName ??= EstTaskIds.NoFreeName(profile.Name);
                    continue;
                }
            }

            await WaitEnrolledAsync(ctx, est, device, profile, json, started, requireNew: written, ct).ConfigureAwait(false);
            if (written)
            {
                // The new certificate took the use: the older one is free now and goes.
                await DeleteFreedCertificatesAsync(ctx, ct).ConfigureAwait(false);
            }
        }

        if (noFreeName is not null)
        {
            throw new InvalidOperationException(noFreeName);
        }

        if (!radiusSet)
        {
            await SetRadiusCaAsync(ctx, radiusCas, ct).ConfigureAwait(false);
        }

        // Pin only when OADM talks HTTPS to the camera: the task never changes how OADM connects.
        if (profiles.Any(p => p.Use == ProfileUse.Https) && string.Equals(ctx.Vapix.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            await PinAsync(ctx, est, device, profiles.First(p => p.Use == ProfileUse.Https), started, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What the camera checks the EST server with: the issuer chain of the EST server policy's certificate and the names in
    /// it; without PKI's server certificates (a CA of another plugin) the CA's chain and no names to check.
    /// </summary>
    private static (IReadOnlyList<byte[]> Chain, IReadOnlyList<string>? Names) ServerTrust(Sdk.Certificates.IDeviceCertificateAuthority ca)
    {
        if (ca is not Oadm.Plugins.Pki.Ca.IServerCertificateSource source)
        {
            return (ca.Chain, null);
        }

        var load = source.ServerCertificate(Oadm.Plugins.Pki.Policies.PolicyKinds.EstServer);
        if (load.Material is not { } material)
        {
            throw new InvalidOperationException(EstTaskIds.ServerCertificateProblem(load.Problem ?? EstTaskIds.ServerIssuerUnknown));
        }

        CryptographicOperations.ZeroMemory(material.Pkcs8);
        return material.IssuerChain.Count > 0 ? (material.IssuerChain, material.Names) : throw new InvalidOperationException(EstTaskIds.ServerIssuerUnknown);
    }

    /// <summary>The camera's device ID certificates (valid Axis device ID of this serial): alias and key kind.</summary>
    internal static IReadOnlyList<(string Alias, ProfileKey Key, int Size)> FindDeviceIds(IReadOnlyList<CameraCertificate> certificates, string serial, EstService est)
    {
        var found = new List<(string, ProfileKey, int)>();
        foreach (var entry in certificates)
        {
            var collection = new X509Certificate2Collection();
            try
            {
                collection.ImportFromPem(entry.Pem);
                if (collection.Count == 0)
                {
                    continue;
                }

                var leaf = collection[0];
                if (!string.Equals(est.DeviceIds.Verify(leaf, [.. collection.Skip(1)], est.Time.GetUtcNow()), serial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var rsa = leaf.GetRSAPublicKey();
                if (rsa is not null)
                {
                    found.Add((entry.Alias, ProfileKey.Rsa, rsa.KeySize));
                }
                else if (leaf.GetECDsaPublicKey() is { } ecc)
                {
                    using (ecc)
                    {
                        found.Add((entry.Alias, ProfileKey.Ecc, ecc.KeySize));
                    }
                }
            }
            catch (CryptographicException)
            {
                // not a certificate the set-up can use
            }
            finally
            {
                foreach (var certificate in collection)
                {
                    certificate.Dispose();
                }
            }
        }

        return found;
    }

    /// <summary>The device ID of the profile's key kind (the smallest key), else any.</summary>
    internal static string Pick(IReadOnlyList<(string Alias, ProfileKey Key, int Size)> deviceIds, ProfileKey key) =>
        (deviceIds.Where(d => d.Key == key).OrderBy(d => d.Size).FirstOrDefault() is { Alias: not null } match ? match : deviceIds.OrderBy(d => d.Size).First()).Alias;

    /// <summary>The camera profile OADM writes (cert-est v1 <c>profiles</c>).</summary>
    internal static (EstProfile Profile, JsonObject Json) Wanted(EstProfile profile, IDeviceInfo device, bool inAnyGroup, IPAddress address, int port, IReadOnlyList<string> caAliases, string deviceId)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var names = new JsonArray();
        foreach (var ip in EstEndpoint.IpAddresses(device, new RequestOrigin(null, null)))
        {
            names.Add("IP:" + ip);
        }

        foreach (var dns in EstEndpoint.DnsNames(device, inAnyGroup))
        {
            names.Add("DNS:" + dns);
        }

        var json = new JsonObject
        {
            ["id"] = EstTaskIds.ProfilePrefix + profile.Name,
            ["server"] = string.Create(CultureInfo.InvariantCulture, $"{host}:{port}{EstPluginInfo.PathPrefix}{profile.Name}"),
            ["caCertificates"] = new JsonArray([.. caAliases.Select(a => (JsonNode)a)]),
            ["certificate"] = deviceId,
            ["services"] = new JsonArray(CameraEstApi.ServiceOf(profile.Use)),
            ["subject"] = new JsonObject { ["cn"] = Truncate(EstEndpoint.CommonName(device), 64) },
            ["subjectAlternativeNames"] = names,
        };
        return (profile, json);
    }

    /// <summary>The camera already has exactly these profiles (the subject cannot be read back, so it is not compared).</summary>
    internal static bool Same(IReadOnlyList<CameraEstProfile> current, IReadOnlyList<(EstProfile Profile, JsonObject Json)> wanted) =>
        current.Count == wanted.Count && wanted.All(w => current.Any(c => Matches(c, w.Json)));

    /// <summary>The camera's profile is the one OADM would write (the subject cannot be read back, so it is not compared).</summary>
    internal static bool Matches(CameraEstProfile have, JsonObject json) =>
        have.Id == json["id"]!.GetValue<string>()
        && have.Server == json["server"]!.GetValue<string>()
        && have.Certificate == json["certificate"]!.GetValue<string>()
        && SameSet(have.CaCertificates, json["caCertificates"]!.AsArray())
        && SameSet(have.Services, json["services"]!.AsArray())
        && SameSet(have.SubjectAlternativeNames, json["subjectAlternativeNames"]!.AsArray());

    /// <summary>
    /// Adds a profile; when the answer got lost (the camera's port went down for a moment, e.g. while it re-authenticates
    /// on the network), reads the profiles and adds again only when it is not there.
    /// </summary>
    private static async Task AddProfileOnceAsync(ITaskExecutionContext ctx, JsonObject json, CancellationToken ct)
    {
        try
        {
            await CameraEstApi.AddProfileAsync(ctx.Vapix, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            var id = json["id"]!.GetValue<string>();
            if (!(await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false)).Any(p => p.Id == id))
            {
                await CameraEstApi.AddProfileAsync(ctx.Vapix, json, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The name a new camera profile of <paramref name="profile"/> takes: its first name, else the second, whichever has no
    /// certificate on the camera. An older certificate of a name is deleted first when the camera lets it go (no use holds
    /// it); one a use still holds stays. Null when both names hold certificates in use.
    /// </summary>
    internal static async Task<string?> FreeNameAsync(ITaskExecutionContext ctx, string profile, CancellationToken ct)
    {
        var aliases = (await CameraEstApi.ListCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false)).Select(c => c.Alias).ToHashSet(StringComparer.Ordinal);
        foreach (var id in EstTaskIds.IdsOf(profile))
        {
            if (!aliases.Contains(id))
            {
                return id;
            }

            if (await CameraEstApi.TryDeleteCertificateAsync(ctx.Vapix, id, ct).ConfigureAwait(false))
            {
                ctx.Log(TaskLogLevel.Info, $"Older certificate \"{id}\" deleted.");
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// After a new profile enrolled: OADM's EST certificates that no profile of the camera uses any more, when the camera
    /// lets them go (one a use still holds stays). Only EST names them "oadm_" / "oadm2_" (PKI: "OADM HTTPS ...").
    /// </summary>
    private static async Task DeleteFreedCertificatesAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        try
        {
            var used = (await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var leftovers = (await CameraEstApi.ListCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false))
                .Select(c => c.Alias).Where(a => EstTaskIds.IsOadm(a) && !used.Contains(a)).ToList();
            foreach (var other in leftovers)
            {
                if (await CameraEstApi.TryDeleteCertificateAsync(ctx.Vapix, other, ct).ConfigureAwait(false))
                {
                    ctx.Log(TaskLogLevel.Info, $"Older certificate \"{other}\" deleted.");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or CameraApiException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            ctx.Log(TaskLogLevel.Info, "The older certificate stays on the camera: " + ex.Message); // tidying only
        }
    }

    private static bool SameSet(IReadOnlyList<string> have, JsonArray want) =>
        have.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(want.Select(n => n!.GetValue<string>()));

    /// <summary>Installs the CA chain the camera does not have (matched by fingerprint); returns the aliases of the whole chain.</summary>
    private static async Task<IReadOnlyList<string>> EnsureCaCertificatesAsync(ITaskExecutionContext ctx, IReadOnlyList<byte[]> chain, CancellationToken ct)
    {
        var existing = await CameraEstApi.ListCaCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
        var byFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliases = existing.Select(c => c.Alias).ToHashSet(StringComparer.Ordinal);
        foreach (var certificate in existing)
        {
            if (FingerprintOfPem(certificate.Pem) is { } fingerprint)
            {
                byFingerprint.TryAdd(fingerprint, certificate.Alias);
            }
        }

        var result = new List<string>();
        foreach (var der in chain)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(der));
            if (byFingerprint.TryGetValue(fingerprint, out var have))
            {
                result.Add(have);
                continue;
            }

            var alias = EstTaskIds.CaAliasPrefix + fingerprint[..8];
            for (var n = 2; aliases.Contains(alias); n++)
            {
                alias = EstTaskIds.CaAliasPrefix + fingerprint[..8] + " " + n.ToString(CultureInfo.InvariantCulture); // another certificate holds that alias
            }

            await CameraEstApi.AddCaCertificateAsync(ctx.Vapix, alias, new string(PemEncoding.Write("CERTIFICATE", der)) + "\n", ct).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"CA certificate \"{alias}\" installed.");
            aliases.Add(alias);
            result.Add(alias);
        }

        return result;
    }

    private static string? FingerprintOfPem(string pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits until the camera reports ENROLLED (up to 2 minutes); a refusal by the EST server for this device and profile
    /// ends the wait at once with its reason.
    /// </summary>
    /// <summary>
    /// The clock check of docs/spec/pki.md "Clock check", before the first write: a camera still behind the certificate's
    /// start is refused unless the user answered the dialog's question with Yes.
    /// </summary>
    private static async Task CheckClockAsync(ITaskExecutionContext ctx, IDeviceInfo device, TimeProvider time, bool confirmed, CancellationToken ct)
    {
        var deviceNow = await CameraClock.ReadAsync(ctx.Vapix, device.Apis, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(CameraClock.Unreadable);
        var behind = time.GetUtcNow() - deviceNow;
        if (!CameraClock.NeedsQuestion(behind))
        {
            return;
        }

        if (!confirmed)
        {
            throw new InvalidOperationException(CameraClock.Refusal(behind));
        }

        ctx.Log(TaskLogLevel.Warning, CameraClock.Late(behind) + " The user chose to continue.");
    }

    private static async Task WaitEnrolledAsync(ITaskExecutionContext ctx, EstService est, IDeviceInfo device, EstProfile profile, JsonObject json, DateTime started,
        bool requireNew, CancellationToken ct)
    {
        var id = json["id"]!.GetValue<string>();
        await ctx.StepAsync(EstTaskIds.WaitStep(profile.Name), async step =>
        {
            var until = est.Time.GetUtcNow() + EnrollTimeout;
            string? status = null;
            var readFailed = false;
            while (true)
            {
                try
                {
                    var profiles = await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false);
                    status = profiles.FirstOrDefault(p => p.Id == id)?.Status;
                    readFailed = false;
                }
                catch (Exception ex) when ((ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) && est.Time.GetUtcNow() < until)
                {
                    status = null; // the web server restarts with a new certificate: ask again
                    readFailed = true;
                }

                var issued = Newest(est, device, profile, started);
                if (string.Equals(status, "ENROLLED", StringComparison.OrdinalIgnoreCase) && (issued is not null || !requireNew)
                    && (issued is null || await HoldsAsync(ctx, id, issued, ct).ConfigureAwait(false)))
                {
                    step.Complete(issued is null ? "Enrolled" : "Valid until " + issued.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    return;
                }

                // Refusals of a camera OADM could not match carry its serial instead of the device.
                if (est.Store.Enrollments().FirstOrDefault(e => (e.DeviceId == device.Id || string.Equals(e.Device, device.Serial, StringComparison.OrdinalIgnoreCase)) && e.TimeUtc >= started
                        && string.Equals(e.Profile, profile.Name, StringComparison.OrdinalIgnoreCase)) is { Ok: false } refused)
                {
                    throw new InvalidOperationException("The EST server refused the camera: " + refused.Reason);
                }

                if (est.Time.GetUtcNow() >= until)
                {
                    // At a site the relay is the usual reason: the node app still asks to allow the port, or the node is offline.
                    if (device.SiteId is { } site && est.SiteRelay(site) is { State: not Sdk.Sites.RelayState.Running } relay)
                    {
                        throw new TimeoutException("The camera did not get its certificate. " + relay.StateText + ".");
                    }

                    // The camera's own status tells the cases apart (it sends no event for a failed enrollment).
                    throw new TimeoutException(
                        readFailed ? EstTaskIds.CameraNotRead
                        : issued is not null ? EstTaskIds.NotStored
                        : string.Equals(status, "ENROLLED", StringComparison.OrdinalIgnoreCase) ? EstTaskIds.KeptOldCertificate
                        : string.Equals(status, "CONNECTED", StringComparison.OrdinalIgnoreCase) ? EstTaskIds.NoCertificate
                        : EstTaskIds.NotReached(est.Store.Config.Port, device.SiteId is not null));
                }

                await Task.Delay(PollInterval, est.Time, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The camera holds <paramref name="issued"/> under <paramref name="alias"/> (it may report ENROLLED while it keeps an older
    /// certificate it could not replace), so none under that name is not held. Unknown (no fingerprint, the read failed):
    /// taken as held.
    /// </summary>
    private static async Task<bool> HoldsAsync(ITaskExecutionContext ctx, string alias, IssuedCertificate issued, CancellationToken ct)
    {
        if (issued.Fingerprint is not { Length: > 0 } fingerprint)
        {
            return true;
        }

        try
        {
            return await CameraEstApi.ReadCertificateAsync(ctx.Vapix, alias, ct).ConfigureAwait(false) is { } held
                && string.Equals(FingerprintOfPem(held.Pem), fingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or CameraApiException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return true;
        }
    }

    /// <summary>The certificate EST issued this camera for the profile since <paramref name="started"/>; O(1) per call.</summary>
    private static IssuedCertificate? Newest(EstService est, IDeviceInfo device, EstProfile profile, DateTime started) =>
        est.Store.NewestOf(device.Id, profile.Name) is { } newest && newest.IssuedUtc >= started ? newest : null;

    /// <summary>OADM pins the HTTPS certificate EST issued, once the camera's web server presents it (it may restart).</summary>
    private static async Task PinAsync(ITaskExecutionContext ctx, EstService est, IDeviceInfo device, EstProfile profile, DateTime started, CancellationToken ct)
    {
        if (Newest(est, device, profile, started) is not { Fingerprint: { } fingerprint })
        {
            return; // nothing new was issued (already set up): the pin stays
        }

        await ctx.StepAsync(EstTaskIds.PinStep, async step =>
        {
            var until = est.Time.GetUtcNow() + PinTimeout;
            while (true)
            {
                try
                {
                    await ctx.UpdateDeviceTlsAsync(Uri.UriSchemeHttps, fingerprint, ct).ConfigureAwait(false);
                    step.Complete(fingerprint[..16]);
                    return;
                }
                catch (DeviceIdentityException) when (est.Time.GetUtcNow() < until)
                {
                    await Task.Delay(PollInterval, est.Time, ct).ConfigureAwait(false);
                }
                catch (DeviceIdentityException ex)
                {
                    step.Warn("The camera does not present the new certificate yet. OADM pins it when it does: " + ex.Message);
                    return;
                }
            }
        }).ConfigureAwait(false);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>The largest set of <paramref name="candidates"/> (in their order) that fits next to the profiles that stay.</summary>
    internal static IReadOnlyList<EstProfile> Fitting(IReadOnlyList<EstProfile> candidates, IReadOnlyList<CameraEstProfile> existing,
        Func<IReadOnlyList<EstProfile>, bool> protectNetworkAccess, string dot1xService)
    {
        for (var size = Math.Min(EstTaskIds.MaxProfilesPerCamera, candidates.Count); size > 0; size--)
        {
            foreach (var subset in Subsets(candidates, size))
            {
                var chosen = subset.Select(p => CameraEstApi.ServiceOf(p.Use)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var protect = protectNetworkAccess(subset);
                if (existing.Count(e => Stays(e, chosen, protect, dot1xService)) + size <= EstTaskIds.MaxProfilesPerCamera)
                {
                    return subset;
                }
            }
        }

        return [];
    }

    /// <summary>The subsets of <paramref name="size"/> items, in the items' order (a few profiles: at most one per use).</summary>
    private static IEnumerable<IReadOnlyList<EstProfile>> Subsets(IReadOnlyList<EstProfile> items, int size, int start = 0)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }

        for (var i = start; i <= items.Count - size; i++)
        {
            foreach (var rest in Subsets(items, size - 1, i + 1))
            {
                yield return [items[i], .. rest];
            }
        }
    }

    /// <summary>
    /// A camera profile Set up leaves alone: another EST server's for other uses, and with <paramref name="protectNetworkAccess"/>
    /// every profile that serves IEEE 802.1X. OADM's other profiles and those serving a wanted use are replaced.
    /// </summary>
    private static bool Stays(CameraEstProfile profile, ISet<string> wanted, bool protectNetworkAccess, string dot1xService) =>
        (protectNetworkAccess && profile.Services.Contains(dot1xService, StringComparer.OrdinalIgnoreCase))
        || (!EstTaskIds.IsOadm(profile.Id) && !profile.Services.Any(wanted.Contains));

    /// <summary>
    /// With the dot1x profile (decision 2026-10-25): the camera checks the RADIUS server with the RADIUS server CA of the
    /// profile's IEEE 802.1X policy, like Enable IEEE 802.1X sets it, so a new CA never leaves it trusting the old one (TLS
    /// alert 48). Only the CA list of the camera's EAP-TLS settings changes, and only while they are on with a client
    /// certificate; without the network settings API, 802.1X off or nothing to change the step says so and nothing is written.
    /// </summary>
    private static async Task SetRadiusCaAsync(ITaskExecutionContext ctx, IReadOnlyList<(string Alias, string Pem, string Fingerprint)>? radiusCas, CancellationToken ct)
    {
        if (radiusCas is not { Count: > 0 })
        {
            return;
        }

        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        if (!Oadm.Plugins.Pki.Tasks.PkiCompatibility.HasNetworkSettings(apis))
        {
            ctx.SkipStep(EstTaskIds.RadiusCaStep, "The camera has no network settings API");
            return;
        }

        // How the camera keeps its 802.1X certificates (network settings before 1.38 do not bind them: the certificate set).
        var state = await Oadm.Plugins.Pki.Device.Dot1xSettings.ReadAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
        var dot1x = state.Dot1x;
        if (dot1x is not { Enabled: true } || !string.Equals(dot1x.Mode, Oadm.Plugins.Pki.Device.NetworkInfoApi.EapTlsMode, StringComparison.Ordinal)
            || string.IsNullOrEmpty(dot1x.CertClient))
        {
            ctx.SkipStep(EstTaskIds.RadiusCaStep, "IEEE 802.1X with certificates is off on the camera");
            return;
        }

        var aliases = await Oadm.Plugins.Pki.Tasks.CertificateDeployment.InstallCaCertificatesAsync(ctx, EstTaskIds.RadiusCaInstallStep, radiusCas, Oadm.Plugins.Pki.Device.CertificateApiKind.Rest, ct).ConfigureAwait(false);
        await ctx.StepAsync(EstTaskIds.RadiusCaStep, async step =>
        {
            if (aliases.Count == dot1x.CertsCa.Count && aliases.All(a => dot1x.CertsCa.Contains(a, StringComparer.Ordinal)))
            {
                step.Complete("Already set: " + string.Join(", ", aliases));
                return;
            }

            await Oadm.Plugins.Pki.Device.Dot1xSettings.SetCaCertificatesAsync(ctx.Vapix, state, apis, aliases, ct).ConfigureAwait(false);
            step.Complete(string.Join(", ", aliases));
            ctx.Log(TaskLogLevel.Info, $"The camera now checks the RADIUS server with {string.Join(", ", aliases)} (was {(dot1x.CertsCa.Count > 0 ? string.Join(", ", dot1x.CertsCa) : "none")}).");
        }).ConfigureAwait(false);
    }

    /// <summary>IEEE 802.1X on the camera, read now (<c>Network.Interface.I0.dot1x.Enabled</c>); unknown counts as on.</summary>
    private static async Task<bool> Dot1xOnAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        try
        {
            var parameters = await ctx.Vapix.ListParametersAsync([EstTaskIds.Dot1xParameter], ct).ConfigureAwait(false);
            return !parameters.TryGetValue(EstTaskIds.Dot1xParameter, out var value) || !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            return true;
        }
    }

    /// <summary>
    /// Removes the chosen profiles from the camera, any profile (also of another EST server); profiles the camera does not
    /// have are skipped. OADM's own profiles stop the Not renewed check of their certificates.
    /// </summary>
    private async Task RemoveAsync(ITaskExecutionContext ctx, IDeviceInfo device, IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        await ctx.StepAsync(EstTaskIds.CheckStep, async step =>
        {
            await RequireApisAsync(ctx, ct).ConfigureAwait(false);
            step.Complete();
        }).ConfigureAwait(false);

        var present = (await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false))
            .Where(p => ids.Contains(p.Id, StringComparer.Ordinal))
            .ToList();
        if (present.Count == 0)
        {
            ctx.ReportWarning(EstTaskIds.NothingToRemove); // tells these cameras apart in a run on many
            return;
        }

        var est = Service;
        if (est is null && present.Any(p => EstTaskIds.IsOadm(p.Id)))
        {
            throw new InvalidOperationException(ctx.Pro?.IsActive == true ? EstTaskIds.NotRunning : EstTaskIds.NoPro); // it has to stop the renewal check of their certificates
        }

        await ctx.StepAsync(EstTaskIds.RemoveStep, async step =>
        {
            foreach (var profile in present)
            {
                await CameraEstApi.RemoveProfileAsync(ctx.Vapix, profile.Id, ct).ConfigureAwait(false);
            }

            step.Complete(string.Join(", ", present.Select(p => p.Id)));
        }).ConfigureAwait(false);

        // The camera no longer renews these: their certificates are never flagged Not renewed.
        if (est is not null)
        {
            foreach (var ours in present.Where(p => EstTaskIds.IsOadm(p.Id)))
            {
                est.Store.StopRenewals(device.Id, est.Time.GetUtcNow().UtcDateTime, EstTaskIds.ProfileNameOf(ours.Id));
            }
        }
    }

    /// <summary>The dialog's read-only query: the camera's enrollment profiles.</summary>
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (string.Equals(method, CameraClock.QueryMethod, StringComparison.Ordinal))
        {
            return await CameraClock.QueryAsync(ctx.Vapix, device, Service?.Time ?? TimeProvider.System, ct).ConfigureAwait(false); // the dialog's clock check
        }

        if (!string.Equals(method, EstTaskIds.ListProfilesMethod, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }

        var apis = await CameraEstApi.DiscoverAsync(ctx.Vapix, ct).ConfigureAwait(false);
        if (!apis.HasCert || !apis.HasCertEst)
        {
            return EstJson.Serialize(new CameraProfileList(false, []));
        }

        var profiles = await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false);
        var (radius, issuing) = Dot1xCas(Service);
        return EstJson.Serialize(new CameraProfileList(true, [.. profiles.Select(p => new CameraProfileInfo(p.Id, p.Services, p.Server, p.Status))])
        {
            RadiusServerCa = radius,
            NetworkAccessCa = issuing,
        });
    }

    /// <summary>The names of the RADIUS server CA and the issuing CA of the dot1x profile's IEEE 802.1X policy (the dialog's hint).</summary>
    internal static (string? Radius, string? Issuing) Dot1xCas(EstService? est)
    {
        if (est?.Store.Config.Profiles.FirstOrDefault(p => p.Enabled && p.Use == ProfileUse.Dot1x) is not { } profile || est.PolicyOf(profile) is not { } policy)
        {
            return (null, null);
        }

        var authorities = est.Authority?.Authorities ?? [];
        string? Name(string? id) => authorities.FirstOrDefault(a => a.Kind != Sdk.Certificates.CertificateAuthorityKind.PreviousOadm
            && string.Equals(a.Id, id ?? Sdk.Certificates.CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase))?.Name;
        return (Name(policy.RadiusCaId), Name(policy.CaId));
    }
}
