using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

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
    public const string MenuName = "Certificate enrollment";

    /// <summary>The name of a run that sets up.</summary>
    public const string SetupName = "Set up certificate enrollment";

    /// <summary>The dialog's read-only query: the camera's enrollment profiles (<see cref="CameraProfileList"/>).</summary>
    public const string ListProfilesMethod = "listProfiles";

    /// <summary>The name of a run that removes profiles.</summary>
    public static string RemoveName(IReadOnlyList<string> profiles) =>
        profiles.Count == 1 ? "Remove enrollment profile " + profiles[0] : string.Create(CultureInfo.InvariantCulture, $"Remove {profiles.Count} enrollment profiles");

    public const string RemoveStep = "Remove the enrollment profiles";
    public const string NothingToRemove = "The camera has none of these enrollment profiles. Nothing was changed.";

    /// <summary>Camera profiles OADM writes are named "oadm_&lt;profile&gt;".</summary>
    public const string ProfilePrefix = "oadm_";

    /// <summary>"OADM CA 3FA2B4C1": the same aliases as the PKI plugin, so both reuse a CA certificate already on the camera.</summary>
    public const string CaAliasPrefix = "OADM CA ";

    public const string MinimumFirmware = "12.10";

    /// <summary>Profiles one camera holds at most.</summary>
    public const int MaxProfilesPerCamera = 2;

    public const string NotRunning = "The EST server is not running. Turn it on on the Plugins page. Nothing was changed.";
    public const string NoPro = "OADM PRO is not active. Nothing was changed.";
    public const string NoCa = "The PKI has no certificate authority. Create one on the PKI page. Nothing was changed.";
    public const string Dot1xSkipped = "IEEE 802.1X is on: the IEEE 802.1X profile was skipped, so the camera keeps the certificate it uses for network access.";
    public const string Dot1xReplaced = "IEEE 802.1X is on: the IEEE 802.1X profile replaces the certificate the camera uses for network access, as chosen when the task started.";
    public static string NotReached(int port) => string.Create(CultureInfo.InvariantCulture,
        $"The camera could not reach the EST server within 2 minutes. Check that a firewall lets the camera reach this computer on TCP {port}.");

    public const string NoCertificate = "The camera reached the EST server but got no certificate within 2 minutes. The Enrollments list shows what reached the server.";
    public const string AlreadySetUp = "The camera already has these enrollment profiles. No profile was written.";

    /// <summary>The right to run the EST tasks: not for operators unless an administrator gives it.</summary>
    public const string EnrollPermission = "enroll";
    public const string NeedsApis = "The camera has no certificate enrollment. Needs AXIS OS 12.10 or later. Nothing was changed.";
    public const string NoDeviceId = "The camera has no Axis device ID certificate. Nothing was changed.";
    public const string NoProfile = "The camera supports none of the EST server's profiles. Nothing was changed.";
    public const string NoAddress = "OADM has no address towards the camera. Nothing was changed.";
    public static string InterfaceMissing(string notAvailable) => $"The EST server does not listen: {notAvailable}. Nothing was changed.";

    public static string NoInterfaceAddress(string name, AddressFamily family) =>
        $"The EST server listens on {name}, which has no {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address for this camera. Nothing was changed.";

    public const string NoSiteRelay = "The EST server cannot listen at the camera's site. Nothing was changed.";

    // Steps: imperative, with the article.
    public const string CheckStep = "Check compatibility";
    public const string ReadStep = "Read the camera's certificates";
    public const string CaStep = "Install the CA certificates";
    public const string RemoveOldStep = "Remove the camera's enrollment profiles";
    public const string PinStep = "Pin the new HTTPS certificate";

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
public abstract class EstTaskBase(Func<EstService?> service) : ITaskPlugin
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public string? IconKey => EstPluginInfo.IconKey;

    public string Group => TaskGroups.Security;

    public bool ShowInToolbar => false;

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
public sealed class EstSetupTask(Func<EstService?> service) : EstTaskBase(service), ITaskPlugin, ITaskPluginQuery
{
    private static readonly TimeSpan EnrollTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PinTimeout = TimeSpan.FromSeconds(60);

    public override string Id => EstTaskIds.Setup;

    public override string DisplayName => EstTaskIds.MenuName;

    /// <summary>"Set up certificate enrollment" or "Remove enrollment profile oadm_https" in the tasks pane.</summary>
    string ITaskPlugin.GetTaskName(string? payloadJson) =>
        SetupPayload.Parse(payloadJson) is { Action: SetupAction.Remove } remove ? EstTaskIds.RemoveName(remove.Profiles) : EstTaskIds.SetupName;

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (SetupPayload.Parse(payloadJson) is { Action: SetupAction.Remove } remove)
        {
            await RemoveAsync(ctx, device, remove.Profiles, ct).ConfigureAwait(false);
            return;
        }

        ArgumentNullException.ThrowIfNull(device);

        var (est, chain, profiles, address) = await ctx.StepAsync(EstTaskIds.CheckStep, async step =>
        {
            var est = Service ?? throw new InvalidOperationException(EstTaskIds.NotRunning);
            if (!est.ProActive)
            {
                throw new InvalidOperationException(EstTaskIds.NoPro);
            }

            var chain = est.Authority is { IsReady: true } ca ? ca.Chain : throw new InvalidOperationException(EstTaskIds.NoCa);
            await RequireApisAsync(ctx, ct).ConfigureAwait(false);
            // supportedServices lists the free services only: those of the camera's profiles count too.
            var (free, existing) = await CameraEstApi.ReadEstAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var services = free.Concat(existing.SelectMany(p => p.Services)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = est.Store.Config.Profiles.Where(p => p.Enabled && services.Contains(CameraEstApi.ServiceOf(p.Use))).ToList();
            if (device.Dot1xEnabled == true && candidates.Any(p => p.Use == ProfileUse.Dot1x))
            {
                if (SetupPayload.Parse(payloadJson).ReplaceDot1x)
                {
                    ctx.Log(TaskLogLevel.Info, EstTaskIds.Dot1xReplaced); // the user chose it in the dialog
                }
                else
                {
                    // No lock-out: a new network access certificate the RADIUS server may not trust would take the camera off the network.
                    candidates.RemoveAll(p => p.Use == ProfileUse.Dot1x);
                    step.Warn(EstTaskIds.Dot1xSkipped);
                }
            }

            // HTTPS last: its certificate restarts the camera's web server.
            var profiles = candidates.Take(EstTaskIds.MaxProfilesPerCamera).OrderBy(p => p.Use == ProfileUse.Https).ToList();
            if (profiles.Count == 0)
            {
                throw new DeviceNotCompatibleException(EstTaskIds.NoProfile);
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

        var wanted = profiles.Select(p => Wanted(p, device, address, est.Store.Config.Port, caAliases, Pick(deviceIds, p.Key))).ToList();
        var wantedServices = profiles.Select(p => CameraEstApi.ServiceOf(p.Use)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // OADM's profiles and those that would serve the same services; other profiles of the camera stay.
        var current = (await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false))
            .Where(c => c.Id.StartsWith(EstTaskIds.ProfilePrefix, StringComparison.Ordinal) || c.Services.Any(wantedServices.Contains))
            .ToList();
        var started = est.Time.GetUtcNow().UtcDateTime;
        if (Same(current, wanted))
        {
            ctx.Log(TaskLogLevel.Info, EstTaskIds.AlreadySetUp);
            foreach (var (profile, json) in wanted)
            {
                await WaitEnrolledAsync(ctx, est, device, profile, json, started, ct).ConfigureAwait(false);
            }

            return;
        }

        if (current.Count > 0)
        {
            await ctx.StepAsync(EstTaskIds.RemoveOldStep, async step =>
            {
                foreach (var old in current)
                {
                    await CameraEstApi.RemoveProfileAsync(ctx.Vapix, old.Id, ct).ConfigureAwait(false);
                }

                step.Complete(string.Join(", ", current.Select(c => c.Id)));
            }).ConfigureAwait(false);
        }

        // One at a time: writing a profile restarts the camera's EST client.
        foreach (var (profile, json) in wanted)
        {
            await ctx.StepAsync(EstTaskIds.AddStep(profile.Name), async step =>
            {
                await CameraEstApi.AddProfileAsync(ctx.Vapix, json, ct).ConfigureAwait(false);
                step.Complete(json["server"]!.GetValue<string>());
            }).ConfigureAwait(false);
            await WaitEnrolledAsync(ctx, est, device, profile, json, started, ct).ConfigureAwait(false);
        }

        // Pin only when OADM talks HTTPS to the camera: the task never changes how OADM connects.
        if (profiles.Any(p => p.Use == ProfileUse.Https) && string.Equals(ctx.Vapix.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            await PinAsync(ctx, est, device, profiles.First(p => p.Use == ProfileUse.Https), started, ct).ConfigureAwait(false);
        }
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
    internal static (EstProfile Profile, JsonObject Json) Wanted(EstProfile profile, IDeviceInfo device, IPAddress address, int port, IReadOnlyList<string> caAliases, string deviceId)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var names = new JsonArray();
        foreach (var ip in EstEndpoint.IpAddresses(device, new RequestOrigin(null, null)))
        {
            names.Add("IP:" + ip);
        }

        foreach (var dns in EstEndpoint.DnsNames(device))
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
    internal static bool Same(IReadOnlyList<CameraEstProfile> current, IReadOnlyList<(EstProfile Profile, JsonObject Json)> wanted)
    {
        if (current.Count != wanted.Count)
        {
            return false;
        }

        foreach (var (_, json) in wanted)
        {
            var id = json["id"]!.GetValue<string>();
            if (current.FirstOrDefault(c => c.Id == id) is not { } have
                || have.Server != json["server"]!.GetValue<string>()
                || have.Certificate != json["certificate"]!.GetValue<string>()
                || !SameSet(have.CaCertificates, json["caCertificates"]!.AsArray())
                || !SameSet(have.Services, json["services"]!.AsArray())
                || !SameSet(have.SubjectAlternativeNames, json["subjectAlternativeNames"]!.AsArray()))
            {
                return false;
            }
        }

        return true;
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
    private static async Task WaitEnrolledAsync(ITaskExecutionContext ctx, EstService est, IDeviceInfo device, EstProfile profile, JsonObject json, DateTime started, CancellationToken ct)
    {
        var id = json["id"]!.GetValue<string>();
        await ctx.StepAsync(EstTaskIds.WaitStep(profile.Name), async step =>
        {
            var until = est.Time.GetUtcNow() + EnrollTimeout;
            string? status = null;
            while (true)
            {
                try
                {
                    var profiles = await CameraEstApi.ReadProfilesAsync(ctx.Vapix, ct).ConfigureAwait(false);
                    status = profiles.FirstOrDefault(p => p.Id == id)?.Status;
                }
                catch (HttpRequestException) when (est.Time.GetUtcNow() < until)
                {
                    status = null; // the web server restarts with a new certificate: ask again
                }

                if (string.Equals(status, "ENROLLED", StringComparison.OrdinalIgnoreCase))
                {
                    var issued = Newest(est, device, profile, started);
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
                        throw new TimeoutException("The camera did not get its certificate: " + relay.StateText + ".");
                    }

                    // The camera's own status tells the two cases apart (it sends no event for a failed enrollment).
                    throw new TimeoutException(string.Equals(status, "CONNECTED", StringComparison.OrdinalIgnoreCase)
                        ? EstTaskIds.NoCertificate
                        : EstTaskIds.NotReached(est.Store.Config.Port));
                }

                await Task.Delay(PollInterval, est.Time, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static IssuedCertificate? Newest(EstService est, IDeviceInfo device, EstProfile profile, DateTime started) =>
        est.Store.Current()
            .Where(c => c.DeviceId == device.Id && c.IssuedUtc >= started && string.Equals(c.Profile, profile.Name, StringComparison.OrdinalIgnoreCase))
            .MaxBy(c => c.IssuedUtc);

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
            ctx.Log(TaskLogLevel.Info, EstTaskIds.NothingToRemove);
            return;
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
        if (Service is { } est)
        {
            foreach (var ours in present.Where(p => p.Id.StartsWith(EstTaskIds.ProfilePrefix, StringComparison.Ordinal)))
            {
                est.Store.StopRenewals(device.Id, est.Time.GetUtcNow().UtcDateTime, ours.Id[EstTaskIds.ProfilePrefix.Length..]);
            }
        }
    }

    /// <summary>The dialog's read-only query: the camera's enrollment profiles.</summary>
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
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
        return EstJson.Serialize(new CameraProfileList(true, [.. profiles.Select(p => new CameraProfileInfo(p.Id, p.Services, p.Server, p.Status))]));
    }
}
