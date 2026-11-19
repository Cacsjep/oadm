using Oadm.Plugins.Network;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.Provisioning.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning;

/// <summary>
/// Core plugin "Provisioning" (docs/spec/provisioning.md, the plugin README): profiles, flows of nodes that set up
/// factory-new cameras that get an address from OADM's DHCP server, while a session is started. Free, on by default; on
/// changes nothing by itself. Administrators only (rights "show" and "manage", both off for Operator and Viewer), users
/// with all devices only: a flow writes to every new camera. The DHCP server is read only (<see cref="IDhcpServerLeases"/>).
/// </summary>
public sealed class ProvisioningPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly ProvisioningOptions _options;
    private readonly TimeProvider _time;
    private readonly IAddressProbe? _probe;
    private readonly bool _ownRegistry;
    private NodeRegistry _registry;
    private ICorePluginContext? _ctx;
    private volatile ProvisioningEngine? _engine;
    private volatile ProfileStore? _profiles;
    private volatile NodeVault? _vault;

    public ProvisioningPlugin()
        : this(new ProvisioningOptions(), TimeProvider.System, null)
    {
    }

    /// <param name="options">Timings and limits.</param>
    /// <param name="time">The clock (tests pass a fake one).</param>
    /// <param name="probe">Is an address taken (ping, TCP 80/443)? Null: on the server's own network; tests pass a fake.</param>
    /// <param name="registry">The node kinds; null: <see cref="NodeRegistry.Default"/>.</param>
    public ProvisioningPlugin(ProvisioningOptions options, TimeProvider time, IAddressProbe? probe, NodeRegistry? registry = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _probe = probe;
        _registry = registry ?? NodeRegistry.Default;
        _ownRegistry = registry is null;
    }

    public string Id => ProvisioningInfo.PluginId;

    public string DisplayName => ProvisioningInfo.DisplayName;

    public string? IconKey => ProvisioningInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Automation;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

    /// <summary>The tools of this plugin are named <c>oadm_provisioning_*</c>.</summary>
    public string? ToolArea => "provisioning";

    /// <summary>The tools (docs/spec/mcp.md "Plugins"): the page's reads and changes with the page's checks and rights.</summary>
    public IReadOnlyList<PluginTool> Tools => ProvisioningTools.Set.Tools;

    /// <summary>The running engine (null while stopped).</summary>
    internal ProvisioningEngine? Engine => _engine;

    internal ProfileStore? Profiles => _profiles;

    internal NodeRegistry Registry => _registry;

    /// <summary>Administrators only by default: seeing the page and changing anything.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show the page", "See the Provisioning page, its profiles and cameras.") { OperatorDefault = false, ViewerDefault = false },
        new(PermissionKeys.Manage, "Run provisioning", "Change profiles and start or stop provisioning, which sets up every new camera.")
        {
            OperatorDefault = false,
            FullDeviceControl = true,
        },
    ];

    public string? RequiredPermission(string method) =>
        ProvisioningMethods.IsRead(ProvisioningTools.Set.RulesMethodOf(method)) ? PermissionKeys.Show : PermissionKeys.Manage;

    public UserRole RequiredRole(string method) =>
        ProvisioningMethods.IsRead(ProvisioningTools.Set.RulesMethodOf(method)) ? UserRole.Operator : UserRole.Admin;

    /// <summary>Server-wide, and a flow writes to every new camera: users with all devices only.</summary>
    public bool RequiresAllDevices(string method) => true;

    public bool IsAudited(string method) => !PluginTool.IsPreviewMethod(method) && !ProvisioningMethods.IsRead(ProvisioningTools.Set.RulesMethodOf(method));

    /// <summary>The audit text: what was done, never a password.</summary>
    public string? AuditDetail(string method, string? payloadJson) => ProvisioningTools.Set.RulesMethodOf(method) switch
    {
        ProvisioningMethods.Start => "Started provisioning",
        ProvisioningMethods.Stop => "Stopped provisioning",
        ProvisioningMethods.SaveProfile => "Saved a profile",
        ProvisioningMethods.DeleteProfile => "Deleted a profile",
        ProvisioningMethods.ActivateProfile => "Chose the active profile",
        ProvisioningMethods.RunFailedAgain => "Ran failed nodes again",
        ProvisioningMethods.Continue => "Pressed Continue",
        ProvisioningMethods.RemoveCameras => "Removed camera rows",
        ProvisioningMethods.StoreSecret => "Stored a password for a profile",
        ProvisioningMethods.StoreFile => "Stored a file for a profile",
        _ => null,
    };

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        if (_ownRegistry && ctx.PluginDirectory is { } folder)
        {
            // The VAPIX Commander library copied into this plugin's folder (the same commands as the Commander page).
            _registry = NodeRegistry.Create(VapixCommander.CommandLibrary.Load(Path.Combine(folder, "Library")));
        }

        var profiles = new ProfileStore(ctx.Settings);
        await profiles.LoadAsync(ct).ConfigureAwait(false);
        var vault = new NodeVault(ctx.Settings, ctx.Secrets, ctx.DataDirectory, ctx.Uploads, _time);
        await vault.LoadAsync(ct).ConfigureAwait(false);
        var services = new EngineServices(
            ctx.AutoAdd ?? new NoAutoAdd(),
            ctx.Tasks,
            ctx.Devices,
            ctx.Settings,
            ctx.Records ?? new MemoryPluginRecords(),
            ctx.Events,
            () => ctx.Services?.Find<IDhcpServerLeases>(),
            _probe ?? (ctx.Network?.ForSite(null) is { } network ? new NetworkAddressProbe(network) : new NoProbe()),
            ctx.Vapix,
            vault);
        var engine = new ProvisioningEngine(services, _registry, _options, _time, ctx.Logger);
        await engine.LoadAsync(ct).ConfigureAwait(false);
        _profiles = profiles;
        _vault = vault;
        _engine = engine;
        await PruneAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_engine is { } engine)
        {
            _engine = null;
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        if (_profiles is { } profiles)
        {
            _profiles = null;
            profiles.Dispose();
        }

        if (_vault is { } vault)
        {
            _vault = null;
            vault.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("The page needs the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.HasAllDevices)
        {
            throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices)
        }

        if (ProvisioningTools.Set.Handles(method))
        {
            return await ProvisioningTools.Set.InvokeAsync(this, caller, method, payloadJson, ct).ConfigureAwait(false);
        }

        var json = ProvisioningJson.Options;
        return method switch
        {
            ProvisioningMethods.GetStatus => PluginJson.Serialize(await StatusAsync(ct).ConfigureAwait(false), json),
            ProvisioningMethods.ListProfiles => PluginJson.Serialize(ListProfiles(), json),
            ProvisioningMethods.GetProfile => PluginJson.Serialize(await GetProfileAsync(PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            ProvisioningMethods.CheckProfile => PluginJson.Serialize(await CheckAsync(PluginJson.Parse<ProfileRequest>(payloadJson, json).Profile, ct).ConfigureAwait(false), json),
            ProvisioningMethods.NodeKinds => PluginJson.Serialize(NodeKinds(), json),
            ProvisioningMethods.Credentials => PluginJson.Serialize(await CredentialsAsync(ct).ConfigureAwait(false), json),
            ProvisioningMethods.VapixCommands => PluginJson.Serialize(VapixCommands(), json),
            ProvisioningMethods.SaveProfile => PluginJson.Serialize(await SaveProfileAsync(PluginJson.Parse<SaveProfileRequest>(payloadJson, json), ct).ConfigureAwait(false), json),
            ProvisioningMethods.DeleteProfile => PluginJson.Serialize(await DeleteProfileAsync(PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            ProvisioningMethods.ActivateProfile => PluginJson.Serialize(await ActivateAsync(PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            ProvisioningMethods.Start => PluginJson.Serialize(await StartSessionAsync(caller, ct).ConfigureAwait(false), json),
            ProvisioningMethods.StoreSecret => PluginJson.Serialize(await StoreSecretAsync(PluginJson.Parse<SecretRequest>(payloadJson, json), ct).ConfigureAwait(false), json),
            ProvisioningMethods.StoreFile => PluginJson.Serialize(await StoreFileAsync(caller, PluginJson.Parse<FileRequest>(payloadJson, json).UploadId, ct).ConfigureAwait(false), json),
            ProvisioningMethods.Stop => PluginJson.Serialize(await StopSessionAsync(ct).ConfigureAwait(false), json),
            ProvisioningMethods.ListCameras => PluginJson.Serialize(Cameras(PluginJson.ParseOrEmpty<CamerasRequest>(payloadJson, json)), json),
            ProvisioningMethods.GetCamera => PluginJson.Serialize(Camera(PluginJson.Parse<MacRequest>(payloadJson, json).Mac), json),
            ProvisioningMethods.RunFailedAgain => PluginJson.Serialize(new CountReply(RunningEngine.RunFailedAgain(Macs(payloadJson))), json),
            ProvisioningMethods.Continue => PluginJson.Serialize(new CountReply(RunningEngine.Continue(Macs(payloadJson), caller.UserName)), json),
            ProvisioningMethods.RemoveCameras => PluginJson.Serialize(new CountReply(RunningEngine.Remove(Macs(payloadJson))), json),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    // ---- the page's operations (also the tools') ----

    internal ProvisioningEngine RunningEngine => _engine ?? throw new InvalidOperationException("The Provisioning plugin is not running.");

    internal ProfileStore RunningProfiles => _profiles ?? throw new InvalidOperationException("The Provisioning plugin is not running.");

    internal DhcpServerSummary? Dhcp() => _ctx?.Services?.Find<IDhcpServerLeases>()?.Summary();

    internal Task<StatusReply> StatusAsync(CancellationToken ct) => Task.FromResult(Status());

    internal StatusReply Status()
    {
        var engine = RunningEngine;
        var profiles = RunningProfiles;
        var session = engine.Session;
        var active = profiles.Active;
        var dhcp = Dhcp();
        var warnings = new List<string>();
        if ((session?.Profile ?? active) is { } profile)
        {
            var context = new ProfileCheckContext(null, dhcp);
            foreach (var node in profile.Nodes)
            {
                if (_registry.Find(node.Kind)?.Warning(node.Settings, context) is { } warning && !warnings.Contains(warning))
                {
                    warnings.Add(warning);
                }
            }
        }

        return new StatusReply
        {
            Started = session is not null,
            StartedAt = session?.StartedAt,
            StartedBy = session?.StartedBy,
            ActiveProfileId = active?.Id,
            ActiveProfileName = active?.Name,
            SessionProfileName = session?.Profile.Name,
            SessionProfile = session?.Profile,
            Dhcp = dhcp,
            Counts = engine.Counts(),
            Warnings = warnings,
            Version = engine.Version,
        };
    }

    internal ProfilesReply ListProfiles()
    {
        var book = RunningProfiles.Book;
        var context = ProfileCheckContext.Empty;
        return new ProfilesReply(
            [.. book.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new ProfileSummary(p.Id, p.Name, p.Nodes.Count, p.Id == book.ActiveId, ProfileCheck.Check(p, _registry, context).IsValid))],
            book.ActiveId);
    }

    internal async Task<ProfileReply> GetProfileAsync(Guid id, CancellationToken ct)
    {
        var profile = RunningProfiles.Find(id) ?? throw new KeyNotFoundException(ProfileGone);
        return new ProfileReply(profile, await CheckAsync(profile, ct).ConfigureAwait(false));
    }

    internal const string ProfileGone = "The profile no longer exists.";

    /// <summary>The check with the credential list and the DHCP server as they are now.</summary>
    internal async Task<ProfileCheckResult> CheckAsync(Profile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ProfileCheck.Check(ProfileStore.Normalize(profile, _registry), _registry, await ContextAsync(ct).ConfigureAwait(false), RunningProfiles.Book.Profiles);
    }

    /// <summary>The commands a VAPIX commands node can run, by category and name.</summary>
    internal IReadOnlyList<ProfileCommand> VapixCommands() =>
        _registry.Find(NodeKind.VapixCommands) is VapixCommandsNode node
            ? [.. node.Library.Commands
                .Where(c => !c.Dangerous && !c.Fields.Any(f => f.IsSecret || f.IsFile))
                .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ProfileCommand(c.Id, c.Name, c.Category, c.Description, c.Fields))]
            : [];

    internal IReadOnlyList<NodeKindInfo> NodeKinds() =>
        [.. _registry.All.Select(n => new NodeKindInfo(n.Kind, n.Name, n.IsFixed, n.AlwaysStops, NodeSettings.Write(n.DefaultSettings)))];

    internal async Task<IReadOnlyList<DeviceCredentialName>> CredentialsAsync(CancellationToken ct)
    {
        try
        {
            return await RunningEngine.AutoAdd.ListCredentialsAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The check of a save (the tool's preview too): the profile's own errors refuse it; a typed password must be one the
    /// cameras take. The active profile must stay without errors. Never writes.
    /// </summary>
    internal async Task<(Profile Profile, ProfileCheckResult Check)> CheckSaveAsync(SaveProfileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = ProfileStore.Normalize(request.Profile ?? throw new ArgumentException("The request has no profile."), _registry);
        if (request.Password is { } password && (password.Length is < 1 or > 64 || password.Any(c => c is < (char)0x20 or > (char)0x7E)))
        {
            throw new PluginToolInputException("password", "The password must be 1 to 64 printable ASCII characters.");
        }

        var check = await CheckAsync(profile, ct).ConfigureAwait(false);
        if (check.ProfileErrors.Count > 0)
        {
            throw new ArgumentException(check.ProfileErrors[0]);
        }

        return (profile, check);
    }

    internal async Task<ProfileReply> SaveProfileAsync(SaveProfileRequest request, CancellationToken ct)
    {
        var (profile, _) = await CheckSaveAsync(request, ct).ConfigureAwait(false);
        if (request.Password is { } password)
        {
            // A typed password goes into the credential list (encrypted with the server's key); the profile keeps its id only.
            var credential = await RunningEngine.AutoAdd.AddCredentialAsync("root", password, ct).ConfigureAwait(false);
            profile = profile with
            {
                Nodes = [.. profile.Nodes.Select(n => n.Kind == NodeKind.SetPassword ? n with { Settings = NodeSettings.Write(new SetPasswordSettings(credential.Id)) } : n)],
            };
        }

        var check = await CheckAsync(profile, ct).ConfigureAwait(false);
        var profiles = RunningProfiles;
        if (profiles.Book.ActiveId == profile.Id && !check.IsValid)
        {
            throw new InvalidOperationException("The active profile must have no errors. " + ProfileCheck.FirstError(check, profile));
        }

        await profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        if (profiles.Book.ActiveId is null && check.IsValid)
        {
            // No active profile yet (the first one): the saved profile becomes active, so Start is the next click.
            await profiles.ActivateAsync(profile.Id, ct).ConfigureAwait(false);
        }

        await PruneAsync(ct).ConfigureAwait(false);
        await PublishStatusAsync(ct).ConfigureAwait(false);
        return new ProfileReply(profile, check);
    }

    internal async Task<ProfilesReply> DeleteProfileAsync(Guid id, CancellationToken ct)
    {
        if (!await RunningProfiles.DeleteAsync(id, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException(ProfileGone);
        }

        await PruneAsync(ct).ConfigureAwait(false);
        await PublishStatusAsync(ct).ConfigureAwait(false);
        return ListProfiles();
    }

    /// <summary>The profile, if it has no errors (with the credential list and DHCP server as they are now).</summary>
    internal async Task<Profile> CheckActivateAsync(Guid id, CancellationToken ct)
    {
        var profile = RunningProfiles.Find(id) ?? throw new KeyNotFoundException(ProfileGone);
        var check = await CheckAsync(profile, ct).ConfigureAwait(false);
        return check.IsValid ? profile : throw new InvalidOperationException(ProfileCheck.FixFirst + " " + ProfileCheck.FirstError(check, profile));
    }

    internal async Task<StatusReply> ActivateAsync(Guid id, CancellationToken ct)
    {
        var profile = await CheckActivateAsync(id, ct).ConfigureAwait(false);
        await RunningProfiles.ActivateAsync(profile.Id, ct).ConfigureAwait(false);
        return await PublishStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>What Start would run, refused as Start refuses (the tool's preview). Never writes.</summary>
    internal async Task<Profile> CheckStartAsync(CancellationToken ct)
    {
        var engine = RunningEngine;
        if (engine.Session is not null)
        {
            throw new InvalidOperationException(ProvisioningEngine.StartedAlready);
        }

        if (Dhcp() is not { IsRunning: true })
        {
            throw new InvalidOperationException(ProvisioningEngine.DhcpFirst);
        }

        var active = RunningProfiles.Active ?? throw new InvalidOperationException("Choose the active profile first.");
        return await CheckActivateAsync(active.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Starts with the caller's tasks kept for the nodes that need the caller's MyAxis sign-in.</summary>
    internal async Task<StatusReply> StartSessionAsync(ICallContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var profile = await CheckStartAsync(ct).ConfigureAwait(false);
        await RunningEngine.StartAsync(profile, caller.UserName, caller.Tasks, ct).ConfigureAwait(false);
        return await PublishStatusAsync(ct).ConfigureAwait(false);
    }

    internal NodeVault RunningVault => _vault ?? throw new InvalidOperationException("The Provisioning plugin is not running.");

    /// <summary>Keeps a typed password or passphrase for a node (encrypted with the server's key) and answers its id only.</summary>
    internal async Task<SecretReply> StoreSecretAsync(SecretRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new SecretReply(await RunningVault.AddSecretAsync(request.Value ?? string.Empty, ct).ConfigureAwait(false));
    }

    /// <summary>Keeps a file the caller uploaded (their own uploads only) for a node and answers it.</summary>
    internal async Task<StoredFile> StoreFileAsync(ICallContext caller, string uploadId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var upload = await caller.Uploads.FindAsync(uploadId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The uploaded file is no longer on the server. Upload it again.");
        await using var stream = await caller.Uploads.OpenReadAsync(upload.Id, ct).ConfigureAwait(false);
        return await RunningVault.AddFileAsync(upload.Name, stream, ct).ConfigureAwait(false);
    }

    /// <summary>The secrets and files no profile and no session names go after a day.</summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        if (_vault is not { } vault || _profiles is not { } profiles)
        {
            return;
        }

        var named = profiles.Book.Profiles.ToList();
        if (_engine?.Session?.Profile is { } running)
        {
            named.Add(running);
        }

        await vault.PruneAsync(named, ct).ConfigureAwait(false);
    }

    internal async Task<StatusReply> StopSessionAsync(CancellationToken ct)
    {
        await RunningEngine.StopAsync(ct).ConfigureAwait(false);
        return await PublishStatusAsync(ct).ConfigureAwait(false);
    }

    internal CamerasPage Cameras(CamerasRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunningEngine.Page(request.After, request.Limit);
    }

    internal CameraRow Camera(string mac) => RunningEngine.Find(mac) ?? throw new KeyNotFoundException("The camera is no longer in the list.");

    private static IReadOnlyList<string> Macs(string? payloadJson) =>
        PluginJson.Parse<MacsRequest>(payloadJson, ProvisioningJson.Options).Macs ?? [];

    private async Task<ProfileCheckContext> ContextAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, string>? credentials = null;
        try
        {
            if (_engine is { } engine)
            {
                credentials = (await engine.AutoAdd.ListCredentialsAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Id, c => c.UserName);
            }
        }
        catch (NotSupportedException)
        {
            credentials = null;
        }

        return new ProfileCheckContext(credentials, Dhcp()) { Files = _vault?.Files, Secrets = _vault?.SecretIds };
    }

    private async Task<StatusReply> PublishStatusAsync(CancellationToken ct)
    {
        var status = await StatusAsync(ct).ConfigureAwait(false);
        _ctx?.Events?.Publish(ProvisioningMethods.StatusTopic, PluginJson.Serialize(status, ProvisioningJson.Options));
        return status;
    }

    /// <summary>A host without the first set up of devices: every call says so.</summary>
    private sealed class NoAutoAdd : IDeviceAutoAdd
    {
        public Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct) =>
            throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);

        public Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct) =>
            throw new NotSupportedException(DeviceFirstSetupTexts.NotSupported);
    }

    /// <summary>A host without network checks: Set IP refuses (nothing is written).</summary>
    private sealed class NoProbe : IAddressProbe
    {
        public Task<AddressProbeResult> ProbeAsync(string address, CancellationToken ct) =>
            throw new InvalidOperationException(NetworkAddressProbe.NoNetworkText);
    }
}
