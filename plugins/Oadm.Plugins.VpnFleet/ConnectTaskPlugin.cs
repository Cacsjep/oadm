using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VpnFleet;

/// <summary>
/// A camera's own WireGuard values, made on the server for this camera only. <see cref="PrivateKey"/> is null when the
/// camera keeps the key it has (its public key is <see cref="PublicKey"/>).
/// </summary>
public sealed record WireGuardCamera(string? PrivateKey, string ClientIp)
{
    /// <summary>The public key of the camera's key pair, the one its WireGuard server knows.</summary>
    public string? PublicKey { get; init; }
}

/// <summary>
/// What one run of <see cref="ConnectTaskPlugin"/> does. Kept in memory only (it holds keys and passwords): the host
/// never stores a task payload.
/// </summary>
public sealed record ConnectPayload
{
    private static readonly ConditionalWeakTable<string, ConnectPayload> Parsed = new();

    /// <summary>Set for the run that adds the cameras to a WireGuard Server hub; null for the cameras' run.</summary>
    public IReadOnlyList<HubPeer>? HubPeers { get; init; }

    /// <summary>The hub run: peers of cameras OADM no longer manages, taken off the hub when name and key still match.</summary>
    public IReadOnlyList<HubPeer>? RemovedPeers { get; init; }

    public ConnectRequest Settings { get; init; } = new();

    /// <summary>WireGuard: the values of each camera of the run, by device id.</summary>
    public IReadOnlyDictionary<Guid, WireGuardCamera>? Cameras { get; init; }

    /// <summary>WireGuard: the server, its key and the networks reached through it, the same for every camera.</summary>
    public string? Endpoint { get; init; }

    public string? PeerPublicKey { get; init; }

    public string? AllowedIps { get; init; }

    /// <summary>WireGuard with a hub: the task that adds the cameras to the hub; a camera changes nothing before it succeeded.</summary>
    public Guid? HubTaskId { get; init; }

    /// <summary>
    /// The payload of a task, parsed once per payload string: every task of a run gets the same string, so a run on many
    /// cameras parses its camera list once, not once per camera. Never changed after parsing.
    /// </summary>
    public static ConnectPayload ParseShared(string? json)
    {
        if (json is not null && Parsed.TryGetValue(json, out var cached))
        {
            return cached;
        }

        var payload = JsonSerializer.Deserialize<ConnectPayload>(json ?? "null", Fleet.Json)
            ?? throw new ArgumentException("The task has no settings. Nothing was changed.");
        if (json is not null)
        {
            Parsed.AddOrUpdate(json, payload);
        }

        return payload;
    }
}

/// <summary>
/// The task the page starts, one per device: saves the VPN settings in the camera's VPN app, starts or restarts the app and
/// waits for the tunnel. The hub run adds the cameras to the peers of a WireGuard Server camera. Started only from the page.
/// </summary>
public sealed class ConnectTaskPlugin : ITaskPlugin
{
    public const string PluginId = Fleet.PluginId + ".connect";

    public const string CheckStep = "Check the device";
    public const string HubStep = "Wait for the WireGuard server to add the camera";
    public const string ProfileStep = "Upload the VPN profile";
    public const string SaveStep = "Save the VPN settings";
    public const string StartStep = "Start the app";
    public const string WaitStep = "Wait for the VPN to connect";
    public const string ReadPeersStep = "Read the peers of the WireGuard server";
    public const string AddPeersStep = "Add the cameras as peers";

    public const string ParamApi = "param-cgi";

    private static readonly Version MinParamApi = new(1, 0);
    private static readonly Version MinApplicationApi = Version.Parse(AcapPlugin.ApplicationApiMinVersion);

    private readonly TimeProvider _time;
    private readonly Func<ITaskRunner?> _tasks;

    /// <summary>One change of a hub's peers at a time, per hub device: two runs never read the same list and lose a peer.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _hubGates = new();

    internal ConnectTaskPlugin(TimeProvider time, Func<ITaskRunner?>? tasks = null)
    {
        _time = time;
        _tasks = tasks ?? (() => null);
    }

    /// <summary>How long a task waits for the tunnel before it ends with a warning.</summary>
    internal TimeSpan ConnectWait { get; set; } = TimeSpan.FromSeconds(90);

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A status read this soon after the app (re)started may still be the one from before the save: "Connected" counts only
    /// after this time.
    /// </summary>
    internal TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a camera waits for the task that adds it to its WireGuard server.</summary>
    internal TimeSpan HubWait { get; set; } = TimeSpan.FromMinutes(30);

    public string Id => PluginId;

    public string DisplayName => "Connect to a VPN";

    public string? IconKey => "network";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            var payload = ConnectPayload.ParseShared(payloadJson ?? "{}");
            if (payload.HubPeers is { } peers)
            {
                return peers.Count == 1 ? "Add 1 camera to the WireGuard server" : $"Add {peers.Count} cameras to the WireGuard server";
            }

            return "Connect to " + VpnApps.NameOf(payload.Settings.Kind);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return DisplayName;
        }
    }

    /// <summary>Reachable with credentials, and the cached API list offers parameters and the Application API.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return DeviceMessages.Refusal(device) is null
            && CachedApiCheck.Supports(device.Apis, ParamApi, MinParamApi)
            && CachedApiCheck.Supports(device.Apis, AcapPlugin.ApplicationApiId, MinApplicationApi);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = ConnectPayload.ParseShared(payloadJson);
        if (payload.HubPeers is { } peers)
        {
            await AddHubPeersAsync(ctx, device, peers, payload.RemovedPeers ?? [], ct).ConfigureAwait(false);
            return;
        }

        await ConnectCameraAsync(ctx, device, payload, ct).ConfigureAwait(false);
    }

    private async Task ConnectCameraAsync(ITaskExecutionContext ctx, IDeviceInfo device, ConnectPayload payload, CancellationToken ct)
    {
        var settings = payload.Settings;
        var app = VpnApps.Of(settings.Kind);
        var problems = ConnectRules.Validate(settings);
        if (problems.Count > 0)
        {
            throw new ArgumentException(problems[0].Message + " Nothing was changed.");
        }

        var steps = new List<string> { CheckStep };
        if (payload.HubTaskId is not null)
        {
            steps.Add(HubStep);
        }

        steps.Add(SaveStep);
        if (settings.Kind == VpnKind.OpenVpn)
        {
            steps.Add(ProfileStep);
        }

        steps.AddRange([StartStep, WaitStep]);
        ctx.PlanSteps([.. steps]);

        WireGuardCamera? camera = null;
        var installed = await ctx.StepAsync(CheckStep, async step =>
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            apis.Require(ParamApi, "1.0");
            apis.Require(AcapPlugin.ApplicationApiId, AcapPlugin.ApplicationApiMinVersion);
            var apps = await DeviceVpn.ListAppsAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var found = apps.FirstOrDefault(a => string.Equals(a.Name, app.AppName, StringComparison.OrdinalIgnoreCase))
                ?? throw new VpnDeviceException($"The {app.DisplayName} app is not installed on this device. Install it on the Applications page first. Nothing was changed.");
            if (settings.Kind == VpnKind.WireGuard)
            {
                camera = await CheckWireGuardKeyAsync(ctx, device, payload, ct).ConfigureAwait(false);
            }

            step.Complete(app.DisplayName + (found.Version is { } v ? " " + v : ""));
            return found;
        }).ConfigureAwait(false);

        if (payload.HubTaskId is { } hubTask)
        {
            await WaitForHubAsync(ctx, hubTask, ct).ConfigureAwait(false);
        }

        // The settings first: a refused write leaves the profile (and so the camera) as it was.
        await ctx.StepAsync(SaveStep, _ => DeviceVpn.SetParametersAsync(ctx.Vapix, ParametersOf(app, payload, camera), ct)).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"Saved the {app.DisplayName} settings" + (camera is null ? "." : $", tunnel address {camera.ClientIp}."));
        if (settings.Kind == VpnKind.OpenVpn)
        {
            await ctx.StepAsync(ProfileStep, _ => DeviceVpn.UploadOpenVpnProfileAsync(ctx.Vapix, settings.Profile!, ct)).ConfigureAwait(false);
        }

        // A running app restarts, so the status the task waits for is one of the new settings.
        await ctx.StepAsync(StartStep, async step =>
        {
            if (installed.IsRunning)
            {
                await DeviceVpn.RestartAppAsync(ctx.Vapix, app.AppName, ct).ConfigureAwait(false);
                step.Complete("Restarted");
            }
            else
            {
                await DeviceVpn.StartAppAsync(ctx.Vapix, app.AppName, ct).ConfigureAwait(false);
                step.Complete("Started");
            }
        }).ConfigureAwait(false);

        await WaitForTunnelAsync(ctx, app, _time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// This camera's WireGuard values of the run. A camera that keeps its key must still have the key whose public key the
    /// run gave its WireGuard server.
    /// </summary>
    private static async Task<WireGuardCamera> CheckWireGuardKeyAsync(ITaskExecutionContext ctx, IDeviceInfo device, ConnectPayload payload, CancellationToken ct)
    {
        if (payload.Cameras is null || !payload.Cameras.TryGetValue(device.Id, out var camera))
        {
            throw new ArgumentException("This camera has no WireGuard values in the task. Set it up again on the VPN Fleet page. Nothing was changed.");
        }

        if (camera.PrivateKey is not null)
        {
            return camera;
        }

        var key = VpnApps.WireGuard.AppName + ".PrivateKey";
        var parameters = await ctx.Vapix.ListParametersAsync([key], ct).ConfigureAwait(false);
        var current = parameters.GetValueOrDefault(key);
        if (!WireGuardKeys.IsKey(current) || !string.Equals(WireGuardKeys.PublicKeyOf(current!), camera.PublicKey, StringComparison.Ordinal))
        {
            throw new VpnDeviceException("The camera's WireGuard key changed after the set up started. Set it up again. Nothing was changed.");
        }

        return camera;
    }

    /// <summary>Waits until the task that adds the cameras to the hub has ended; only a success lets the camera change.</summary>
    private async Task WaitForHubAsync(ITaskExecutionContext ctx, Guid hubTask, CancellationToken ct)
    {
        using var step = ctx.BeginStep(HubStep);
        var tasks = _tasks() ?? throw new InvalidOperationException("The task cannot see the task of the WireGuard server. Nothing was changed.");
        var end = _time.GetUtcNow() + HubWait;
        while (true)
        {
            // Not active is read before the outcome: a task that ends between the two reads is then seen with its outcome,
            // never as "ended without a result".
            var active = tasks.IsActive(hubTask);
            if (await tasks.GetOutcomeAsync(hubTask, ct).ConfigureAwait(false) is { } outcome)
            {
                if (outcome.State is TaskEndState.Done or TaskEndState.DoneWithWarnings)
                {
                    step.Complete("The server has the camera.");
                    return;
                }

                throw new VpnDeviceException("The WireGuard server did not add the cameras"
                    + (string.IsNullOrWhiteSpace(outcome.Message) ? "." : ": " + outcome.Message.TrimEnd('.') + ".") + " Nothing was changed.");
            }

            if (active == false)
            {
                throw new VpnDeviceException("The task of the WireGuard server ended without a result. Set the cameras up again. Nothing was changed.");
            }

            if (_time.GetUtcNow() >= end)
            {
                throw new VpnDeviceException($"The WireGuard server did not add the cameras within {HubWait.TotalMinutes:0} minutes. Nothing was changed.");
            }

            await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The app's parameters for param.cgi. Optional fields left empty keep what the app has.</summary>
    public static IReadOnlyDictionary<string, string> ParametersOf(VpnApp app, ConnectPayload payload, WireGuardCamera? camera)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(payload);
        var s = payload.Settings;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(string name, string? value, bool optional = false)
        {
            if (!optional || !string.IsNullOrWhiteSpace(value))
            {
                values[app.Parameter(name)] = value?.Trim() ?? "";
            }
        }

        switch (s.Kind)
        {
            case VpnKind.Tailscale:
                Set("AuthKey", s.AuthKey);
                Set("CustomServer", s.LoginServer, optional: true);
                break;
            case VpnKind.NetBird:
                Set("SetupKey", s.SetupKey);
                Set("ManagementURL", s.ManagementUrl, optional: true);
                break;
            case VpnKind.ZeroTier:
                Set("NetworkID", s.NetworkId?.Trim().ToLowerInvariant());
                break;
            case VpnKind.OpenVpn:
                Set("Username", s.UserName, optional: true);
                if (!string.IsNullOrEmpty(s.Password))
                {
                    values[app.Parameter("Password")] = s.Password;
                }

                break;
            case VpnKind.WireGuard:
                ArgumentNullException.ThrowIfNull(camera);
                Set("PrivateKey", camera.PrivateKey, optional: true); // null: the camera keeps its key
                Set("ClientIP", camera.ClientIp);
                Set("Endpoint", payload.Endpoint);
                Set("PeerPublicKey", payload.PeerPublicKey);
                Set("AllowedIPs", payload.AllowedIps);
                break;
        }

        return values;
    }

    /// <summary>Waits for the tunnel; a "Connected" read before <paramref name="started"/> plus <see cref="SettleTime"/> does not count.</summary>
    private async Task WaitForTunnelAsync(ITaskExecutionContext ctx, VpnApp app, DateTimeOffset started, CancellationToken ct)
    {
        using var step = ctx.BeginStep(WaitStep);
        var settled = started + SettleTime;
        var end = started + (ConnectWait > SettleTime ? ConnectWait : SettleTime);
        VpnStatus status;
        while (true)
        {
            try
            {
                status = await DeviceVpn.ReadStatusAsync(ctx.Vapix, app, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or VpnDeviceException)
            {
                status = new VpnStatus(VpnState.Unknown, null, ex.Message);
            }

            var now = _time.GetUtcNow();
            if (status.State == VpnState.Connected && now >= settled)
            {
                step.Complete(status.Address is null ? "Connected" : "Connected, " + status.Address);
                ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is connected" + (status.Address is null ? "." : $" with address {status.Address}."));
                return;
            }

            if (now >= end)
            {
                break;
            }

            await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
        }

        step.Warn($"Not connected after {(end - started).TotalSeconds:0} s" + (status.Detail is { } d ? ": " + d : ".") + " Check the VPN Fleet page later.");
    }

    private async Task AddHubPeersAsync(ITaskExecutionContext ctx, IDeviceInfo device, IReadOnlyList<HubPeer> peers, IReadOnlyList<HubPeer> removed, CancellationToken ct)
    {
        var app = VpnApps.WireGuardServerAppName;
        ctx.PlanSteps(ReadPeersStep, AddPeersStep);
        var gate = _hubGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var parameters = await ctx.StepAsync(ReadPeersStep, async step =>
            {
                (await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false)).Require(ParamApi, "1.0");
                var list = await ctx.Vapix.ListParametersAsync([app], ct).ConfigureAwait(false);
                if (!list.ContainsKey(app + ".Peers"))
                {
                    throw new VpnDeviceException("The WireGuard Server app is not installed on this device. Nothing was changed.");
                }

                step.Complete(WireGuardAddressing.ParsePeers(list[app + ".Peers"]).Count + " peers");
                return list;
            }).ConfigureAwait(false);

            await ctx.StepAsync(AddPeersStep, async step =>
            {
                var merged = WireGuardAddressing.MergePeers(parameters[app + ".Peers"], peers, removed);
                await DeviceVpn.SetParametersAsync(ctx.Vapix, new Dictionary<string, string> { ["root." + app + ".Peers"] = merged }, ct).ConfigureAwait(false);
                step.Complete(peers.Count == 1 ? "1 camera" : peers.Count + " cameras");
            }).ConfigureAwait(false);

            foreach (var peer in peers)
            {
                ctx.Log(TaskLogLevel.Info, $"Peer {peer.Name}: {peer.Address}");
            }

            foreach (var peer in removed)
            {
                ctx.Log(TaskLogLevel.Info, $"Removed peer {peer.Name}: OADM no longer manages it.");
            }

            if (!string.Equals(parameters.GetValueOrDefault(app + ".Enabled"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                ctx.ReportWarning("The WireGuard server is turned off in its app. The cameras connect once it is on.");
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
