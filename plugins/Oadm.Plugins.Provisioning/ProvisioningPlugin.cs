using Oadm.Plugins.Network;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.Provisioning.Tools;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning;

/// <summary>
/// Core plugin "Provisioning" (docs/spec/provisioning.md, the plugin README): profiles, flows of nodes that set up every
/// factory-new camera OADM finds while provisioning is on, one task per camera (<see cref="ProvisioningTaskPlugin"/>). No rail
/// page: the client part is the Devices toolbar's Provisioning button and its dialog. Free, on by default; on changes nothing
/// by itself (provisioning is off until an Administrator turns it on). Rights "show" (reads) and "manage" (everything else),
/// both off for Operator and Viewer; users with all devices only, except <see cref="ProvisioningMethods.State"/> (whether it
/// is on), which every signed-in user may read (the Add devices page).
/// </summary>
public sealed class ProvisioningPlugin : ICorePlugin, IAsyncDisposable
{
    public const string ChooseActiveFirst = "Choose the active profile first.";

    public const string ActiveWhileOn = "Turn provisioning off first, or make another profile active.";

    internal const string ProfileGone = "The profile no longer exists.";

    private readonly ProvisioningOptions _options;
    private readonly TimeProvider _time;
    private readonly IAddressProbe? _probe;
    private readonly bool _ownRegistry;
    private NodeRegistry _registry;
    private ICorePluginContext? _ctx;
    private volatile ProvisioningService? _service;
    private volatile ProfileStore? _profiles;
    private volatile NodeVault? _vault;
    private Task? _prune;

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
        TaskPlugins = [new ProvisioningTaskPlugin(() => _service, _options.MaxParallel)];
    }

    public string Id => ProvisioningInfo.PluginId;

    public string DisplayName => ProvisioningInfo.DisplayName;

    public string? IconKey => ProvisioningInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Automation;

    /// <summary>No rail page: the Devices toolbar's Provisioning button opens Provisioning management.</summary>
    public bool HasPage => false;

    /// <summary>The task "Provision with &lt;profile&gt;", one per camera.</summary>
    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The tools of this plugin are named <c>oadm_provisioning_*</c>.</summary>
    public string? ToolArea => "provisioning";

    /// <summary>The tools (docs/spec/mcp.md "Plugins"): the dialog's reads and changes with its checks and rights.</summary>
    public IReadOnlyList<PluginTool> Tools => ProvisioningTools.Set.Tools;

    /// <summary>The running service (null while the plugin is off).</summary>
    internal ProvisioningService? Service => _service;

    internal ProfileStore? Profiles => _profiles;

    internal NodeRegistry Registry => _registry;

    /// <summary>Administrators only by default: seeing provisioning and changing anything.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show provisioning", "See whether provisioning is on, its profiles and how the last run of a camera ended.") { OperatorDefault = false, ViewerDefault = false },
        new(PermissionKeys.Manage, "Run provisioning", "Turn provisioning on and off and change its profiles; it sets up every new camera OADM finds.")
        {
            OperatorDefault = false,
            FullDeviceControl = true,
        },
    ];

    /// <summary>Whether it is on: a login only. Reads: "show". Everything else: "manage".</summary>
    public string? RequiredPermission(string method)
    {
        var rules = ProvisioningTools.Set.RulesMethodOf(method);
        return rules == ProvisioningMethods.State ? null : ProvisioningMethods.IsRead(rules) ? PermissionKeys.Show : PermissionKeys.Manage;
    }

    public UserRole RequiredRole(string method) =>
        ProvisioningMethods.IsRead(ProvisioningTools.Set.RulesMethodOf(method)) ? UserRole.Operator : UserRole.Admin;

    /// <summary>Server-wide, and it writes to every new camera: users with all devices only (whether it is on: everyone).</summary>
    public bool RequiresAllDevices(string method) => ProvisioningTools.Set.RulesMethodOf(method) != ProvisioningMethods.State;

    public bool IsAudited(string method) => !PluginTool.IsPreviewMethod(method) && !ProvisioningMethods.IsRead(ProvisioningTools.Set.RulesMethodOf(method));

    /// <summary>The audit text: what was done, never a password.</summary>
    public string? AuditDetail(string method, string? payloadJson) => ProvisioningTools.Set.RulesMethodOf(method) switch
    {
        ProvisioningMethods.TurnOn => "Turned provisioning on",
        ProvisioningMethods.TurnOff => "Turned provisioning off",
        ProvisioningMethods.SaveProfile => "Saved a profile",
        ProvisioningMethods.DeleteProfile => "Deleted a profile",
        ProvisioningMethods.ActivateProfile => "Chose the active profile",
        ProvisioningMethods.RunAgain => "Ran provisioning again",
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
        _profiles = profiles;
        _vault = vault;
        var services = new EngineServices(
            ctx.AutoAdd ?? new NoAutoAdd(),
            ctx.Tasks,
            ctx.Devices,
            ctx.Settings,
            ctx.Records ?? new MemoryPluginRecords(),
            () => ctx.Services?.Find<IDhcpServerLeases>(),
            _probe ?? (ctx.Network?.ForSite(null) is { } network ? new NetworkAddressProbe(network) : new NoProbe()),
            ctx.Vapix,
            vault);
        var service = new ProvisioningService(services, RunnableProfileAsync, _registry, _options, _time, ctx.Logger);
        await service.LoadAsync(ct).ConfigureAwait(false);
        _service = service;
        await PruneAsync(ct).ConfigureAwait(false);
        _prune = Task.Run(() => service.PruneRunsAsync(CancellationToken.None), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_prune is { } prune)
        {
            _prune = null;
            await prune.ConfigureAwait(false);
        }

        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
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
        throw new NotSupportedException("The plugin needs the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (method == ProvisioningMethods.State)
        {
            var on = RunningService.IsOn;
            return PluginJson.Serialize(new StateReply(on, on && await RunnableProfileAsync(ct).ConfigureAwait(false) is not null), ProvisioningJson.Options);
        }

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
            ProvisioningMethods.Policies => PluginJson.Serialize(await PoliciesAsync(ct).ConfigureAwait(false) ?? [], json),
            ProvisioningMethods.VapixCommands => PluginJson.Serialize(VapixCommands(), json),
            ProvisioningMethods.SaveProfile => PluginJson.Serialize(await SaveProfileAsync(PluginJson.Parse<SaveProfileRequest>(payloadJson, json), ct).ConfigureAwait(false), json),
            ProvisioningMethods.DeleteProfile => PluginJson.Serialize(await DeleteProfileAsync(PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            ProvisioningMethods.ActivateProfile => PluginJson.Serialize(await ActivateAsync(PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            ProvisioningMethods.TurnOn => PluginJson.Serialize(await TurnOnAsync(caller, ct).ConfigureAwait(false), json),
            ProvisioningMethods.TurnOff => PluginJson.Serialize(await TurnOffAsync(ct).ConfigureAwait(false), json),
            ProvisioningMethods.RunAgain => PluginJson.Serialize(await RunAgainAsync(PluginJson.Parse<DeviceIdsRequest>(payloadJson, json).DeviceIds ?? [], ct).ConfigureAwait(false), json),
            ProvisioningMethods.GetRun => PluginJson.Serialize(await RunAsync(PluginJson.Parse<DeviceIdRequest>(payloadJson, json).DeviceId, ct).ConfigureAwait(false), json),
            ProvisioningMethods.StoreSecret => PluginJson.Serialize(await StoreSecretAsync(PluginJson.Parse<SecretRequest>(payloadJson, json), ct).ConfigureAwait(false), json),
            ProvisioningMethods.StoreFile => PluginJson.Serialize(await StoreFileAsync(caller, PluginJson.Parse<FileRequest>(payloadJson, json).UploadId, ct).ConfigureAwait(false), json),
            ProvisioningMethods.ReadConfigFile => PluginJson.Serialize(await ReadConfigFileAsync(PluginJson.Parse<IdRequest>(payloadJson, json).Id, ct).ConfigureAwait(false), json),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    // ---- the dialog's operations (also the tools') ----

    internal ProvisioningService RunningService => _service ?? throw new InvalidOperationException("The Provisioning plugin is not running.");

    internal ProfileStore RunningProfiles => _profiles ?? throw new InvalidOperationException("The Provisioning plugin is not running.");

    internal NodeVault RunningVault => _vault ?? throw new InvalidOperationException("The Provisioning plugin is not running.");

    internal DhcpServerSummary? Dhcp() => _ctx?.Services?.Find<IDhcpServerLeases>()?.Summary();

    /// <summary>PKI's HTTPS and IEEE 802.1X certificate policies; null when PKI does not run.</summary>
    internal async Task<IReadOnlyList<IssuingPolicyInfo>?> PoliciesAsync(CancellationToken ct)
    {
        if (_ctx?.Services?.Find<IIssuingPolicies>() is not { } pki)
        {
            return null;
        }

        return [.. await pki.ListAsync(IssuingPolicyUse.Https, ct).ConfigureAwait(false), .. await pki.ListAsync(IssuingPolicyUse.Dot1x, ct).ConfigureAwait(false)];
    }

    internal async Task<StatusReply> StatusAsync(CancellationToken ct)
    {
        var service = RunningService;
        var state = service.State;
        var active = RunningProfiles.Active;
        var dhcp = Dhcp();
        var warnings = new List<string>();
        string? refusal = null;
        var valid = false;
        if (active is null)
        {
            refusal = ChooseActiveFirst;
        }
        else
        {
            var check = await CheckAsync(active, ct).ConfigureAwait(false);
            valid = check.IsValid;
            if (!valid)
            {
                refusal = ProfileCheck.FixFirst + " " + ProfileCheck.FirstError(check, active);
            }

            warnings.AddRange(check.NodeWarnings.Values.Distinct(StringComparer.Ordinal));
        }

        if (state.On && refusal is not null)
        {
            warnings.Insert(0, "No camera gets a task now. " + refusal);
        }

        return new StatusReply
        {
            On = state.On,
            OnBy = state.On ? state.OnBy : null,
            OnAt = state.On ? state.OnAt : null,
            ActiveProfileId = active?.Id,
            ActiveProfileName = active?.Name,
            ActiveProfileValid = valid,
            TurnOnRefusal = state.On ? null : refusal,
            Dhcp = dhcp,
            RunningTasks = service.ActiveTasks,
            Warnings = warnings,
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

    /// <summary>The check with the credential list and the DHCP server as they are now.</summary>
    internal async Task<ProfileCheckResult> CheckAsync(Profile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ProfileCheck.Check(ProfileStore.Normalize(profile, _registry), _registry, await ContextAsync(ct).ConfigureAwait(false), RunningProfiles.Book.Profiles);
    }

    /// <summary>The active profile when it has no errors (the tasks run it); null: no task starts.</summary>
    private async Task<Profile?> RunnableProfileAsync(CancellationToken ct)
    {
        if (_profiles?.Active is not { } active)
        {
            return null;
        }

        return (await CheckAsync(active, ct).ConfigureAwait(false)).IsValid ? active : null;
    }

    /// <summary>The commands a VAPIX commands node can run, by category and name.</summary>
    internal IReadOnlyList<ProfileCommand> VapixCommands() =>
        _registry.Find(NodeKind.VapixCommands) is VapixCommandsNode node
            ? [.. node.Library.Commands
                .Where(c => !c.Dangerous && !c.Fields.Any(f => f.IsSecret || f.IsFile))
                .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ProfileCommand(c.Id, c.Name, c.Category, c.Description, c.Fields))]
            : [];

    /// <summary>The kinds with their default settings; a new Network node starts in the DHCP server's network.</summary>
    internal IReadOnlyList<NodeKindInfo> NodeKinds()
    {
        var dhcp = Dhcp();
        return [.. _registry.All.Select(n => new NodeKindInfo(n.Kind, n.Name, n.IsFixed, n.AlwaysStops,
            NodeSettings.Write(n.Kind == NodeKind.Network ? NetworkNode.DefaultsFor(dhcp) : n.DefaultSettings)))];
    }

    internal async Task<IReadOnlyList<DeviceCredentialName>> CredentialsAsync(CancellationToken ct)
    {
        try
        {
            return await RunningService.AutoAdd.ListCredentialsAsync(ct).ConfigureAwait(false);
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
            var credential = await RunningService.AutoAdd.AddCredentialAsync("root", password, ct).ConfigureAwait(false);
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
            // No active profile yet (the first one): the saved profile becomes active, so On is the next click.
            await profiles.ActivateAsync(profile.Id, ct).ConfigureAwait(false);
        }

        await PruneAsync(ct).ConfigureAwait(false);
        await PublishStatusAsync(ct).ConfigureAwait(false);
        return new ProfileReply(profile, check);
    }

    /// <summary>The profile that would be deleted, refused as Delete refuses (the tool's preview). Never writes.</summary>
    internal Profile CheckDelete(Guid id)
    {
        var profile = RunningProfiles.Find(id) ?? throw new KeyNotFoundException(ProfileGone);
        return RunningService.IsOn && RunningProfiles.Book.ActiveId == id ? throw new InvalidOperationException(ActiveWhileOn) : profile;
    }

    internal async Task<ProfilesReply> DeleteProfileAsync(Guid id, CancellationToken ct)
    {
        CheckDelete(id);
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

    /// <summary>The profile On would run, refused as On refuses (the tool's preview). Never writes.</summary>
    internal async Task<Profile> CheckTurnOnAsync(CancellationToken ct)
    {
        if (RunningService.IsOn)
        {
            throw new InvalidOperationException(ProvisioningService.AlreadyOn);
        }

        var active = RunningProfiles.Active ?? throw new InvalidOperationException(ChooseActiveFirst);
        return await CheckActivateAsync(active.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Turns provisioning on with the caller's tasks kept for the nodes that need the caller's MyAxis sign-in.</summary>
    internal async Task<StatusReply> TurnOnAsync(ICallContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await CheckTurnOnAsync(ct).ConfigureAwait(false);
        await RunningService.TurnOnAsync(caller.UserName, caller.Tasks, ct).ConfigureAwait(false);
        return await PublishStatusAsync(ct).ConfigureAwait(false);
    }

    internal async Task<StatusReply> TurnOffAsync(CancellationToken ct)
    {
        await RunningService.TurnOffAsync(ct).ConfigureAwait(false);
        return await PublishStatusAsync(ct).ConfigureAwait(false);
    }

    internal Task<RunAgainReply> RunAgainAsync(IReadOnlyList<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        return deviceIds.Count > DeviceLimits.MaxDevices
            ? throw new ArgumentException($"At most {DeviceLimits.MaxDevices:N0} cameras at once.")
            : RunningService.RunAgainAsync(deviceIds, ct);
    }

    /// <summary>The last provisioning run of a camera with each node's name.</summary>
    internal async Task<RunReply> RunAsync(Guid deviceId, CancellationToken ct)
    {
        var service = RunningService;
        var run = await service.ReadRunAsync(deviceId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(NoRun);
        var again = await service.PlanAgainAsync(deviceId, ct).ConfigureAwait(false);
        var names = again?.Profile.Nodes.Select(n => _registry.Find(n.Kind)?.Name ?? n.Kind.ToString()).ToList();
        return new RunReply(
            deviceId,
            run.TaskId,
            run.ProfileName,
            run.Started,
            run.Looked,
            [.. run.Results.Select((r, i) => new RunNodeReply(names is not null && i < names.Count ? names[i] : $"Node {i + 1}", r))],
            again is not null);
    }

    public const string NoRun = "Provisioning has not run on this camera.";

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

    /// <summary>Largest configuration file the editor's preview reads (Configure devices reads files up to 1 MB).</summary>
    public const long MaxConfigFileBytes = DeviceConfig.AdmConfigFile.MaxFileBytes;

    /// <summary>A kept configuration file (<c>.cfg</c>, at most 1 MB) for the editor's file preview; read only.</summary>
    internal async Task<ConfigFileReply> ReadConfigFileAsync(Guid fileId, CancellationToken ct)
    {
        var vault = RunningVault;
        if (vault.Files.GetValueOrDefault(fileId) is not { } file)
        {
            throw new KeyNotFoundException(NodeVault.FileGone);
        }

        if (!ConfigurationNode.IsConfigFile(file.Name))
        {
            throw new ArgumentException(ConfigurationNode.WrongFile);
        }

        if (file.Size > MaxConfigFileBytes)
        {
            throw new ArgumentException(ConfigurationNode.TooLarge);
        }

        return new ConfigFileReply(file.Name, await vault.ReadTextAsync(fileId, MaxConfigFileBytes, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The secrets and files nothing names go after a day: neither a stored profile nor the profile of a queued or running task
    /// or of a run Run again starts (their snapshots), so saving or deleting a profile never takes what a task still needs.
    /// </summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        if (_vault is not { } vault || _profiles is not { } profiles || _service is not { } service)
        {
            return;
        }

        await service.WithProfilesInUseAsync((inUse, token) => vault.PruneAsync([.. profiles.Book.Profiles, .. inUse], token), ct).ConfigureAwait(false);
    }

    private async Task<ProfileCheckContext> ContextAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, string>? credentials = null;
        try
        {
            if (_service is { } service)
            {
                credentials = (await service.AutoAdd.ListCredentialsAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Id, c => c.UserName);
            }
        }
        catch (NotSupportedException)
        {
            credentials = null;
        }

        return new ProfileCheckContext(credentials, Dhcp()) { Files = _vault?.Files, Secrets = _vault?.SecretIds, Policies = await PoliciesAsync(ct).ConfigureAwait(false) };
    }

    private async Task<StatusReply> PublishStatusAsync(CancellationToken ct)
    {
        var status = await StatusAsync(ct).ConfigureAwait(false);
        _ctx?.Events?.Publish(ProvisioningMethods.StatusTopic, PluginJson.Serialize(status, ProvisioningJson.Options), PluginEventAudience.AllDevices);
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

    /// <summary>A host without network checks: a pool refuses (nothing is written).</summary>
    private sealed class NoProbe : IAddressProbe
    {
        public Task<AddressProbeResult> ProbeAsync(string address, CancellationToken ct) =>
            throw new InvalidOperationException(NetworkAddressProbe.NoNetworkText);
    }
}
