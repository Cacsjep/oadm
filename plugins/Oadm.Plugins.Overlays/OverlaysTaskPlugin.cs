using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Overlays;

/// <summary>
/// The context menu group Overlays: one task per action (<see cref="AddOverlaysTask"/>, <see cref="ReplaceOverlaysTask"/>,
/// <see cref="RemoveMatchingOverlaysTask"/>, <see cref="RemoveAllOverlaysTask"/>), each fixing its <see cref="OverlaysMode"/>.
/// Text overlays of view area 1 only: image overlays, widgets and privacy masks are never touched. One task per camera
/// (<see cref="OverlaysTaskRunner"/>).
/// </summary>
public abstract class OverlaysTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    /// <summary>The package (plugin.json); the tasks' ids start with it.</summary>
    public const string PackageId = "oadm.overlays";

    /// <summary>The greyed entry's reason and the query's message.</summary>
    public const string NeedsOverlays = "text overlays";

    /// <summary>A camera without the overlay API in the fresh list.</summary>
    public const string NotSupportedNothingChanged = "The camera does not support text overlays. Nothing was changed.";

    /// <summary>The note of Replace overlays.</summary>
    public const string ReplaceNote = "Every other text overlay of view area 1 is removed. Image overlays stay.";

    /// <summary>The note of Remove all overlays.</summary>
    public const string RemoveAllNote = "Every text overlay of view area 1 is removed. Image overlays stay.";

    /// <summary>The note of Add overlays.</summary>
    public const string AddNote = "An overlay a camera already shows is skipped.";

    /// <summary>The note of Remove matching overlays.</summary>
    public const string RemoveMatchingNote = "Removes the text overlays whose text is the same as one in the list.";

    protected OverlaysTaskPlugin(OverlaysMode mode) => Mode = mode;

    /// <summary>What this task does on each camera.</summary>
    public OverlaysMode Mode { get; }

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public string Group => TaskGroups.Overlays;

    public abstract string? IconKey { get; }

    public bool ShowInToolbar => false;

    /// <summary>Every entry opens something: the dialog, or for Remove all a confirmation.</summary>
    public bool RequiresDialog => true;

    public bool IsDestructive => Mode == OverlaysMode.RemoveAll;

    /// <summary>The payload (SDK 0.13.0): the list of overlays, none for Remove all; its schema is the input for AI agents.</summary>
    public Type? PayloadType => Mode == OverlaysMode.RemoveAll ? typeof(RemoveAllPayload) : typeof(OverlayListPayload);

    public JsonSerializerOptions? PayloadJsonOptions => OverlaysJson.Options;

    /// <summary>The run of this task from its payload: the task's mode with the payload's overlays.</summary>
    public OverlaysPayload Read(string? payloadJson) => Mode == OverlaysMode.RemoveAll
        ? new OverlaysPayload { Mode = Mode }
        : new OverlaysPayload { Mode = Mode, Overlays = OverlaysJson.ParseList(payloadJson).Overlays ?? [] };

    /// <summary>Working credentials and the overlay API (dynamicoverlay 1.x) in the cached API list. Cheap: no LINQ, no request.</summary>
    public bool CanRun(IDeviceInfo device) => CanRunOn(device);

    /// <summary>See <see cref="CanRun"/>; the dialog counts the compatible devices with it.</summary>
    public static bool CanRunOn(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && CachedApiCheck.Supports(device.Apis, DynamicOverlayApi.ApiId, DynamicOverlayApi.Min);
    }

    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device) ?? TaskSupportReasons.NeedsApi(NeedsOverlays, device);
    }

    public IReadOnlyList<PayloadError> ValidatePayload(string? payloadJson, ICallContext caller)
    {
        try
        {
            return OverlayRules.Check(Read(payloadJson));
        }
        catch (ArgumentException ex)
        {
            return [new PayloadError(string.Empty, ex.Message)];
        }
    }

    /// <summary>From the cached data: the cameras that can run it, and what the mode does.</summary>
    public Task<TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var payload = Read(payloadJson);
        var plan = devices.Select(d => CanRunOn(d) ? new TaskPlanDevice(d.Id) : new TaskPlanDevice(d.Id, NotSupportedReason(d) ?? TaskSupportReasons.Default)).ToList();
        IReadOnlyList<string> warnings = payload.Mode switch
        {
            OverlaysMode.RemoveAll => [RemoveAllNote],
            OverlaysMode.Replace => [ReplaceNote],
            OverlaysMode.Add => [AddNote],
            _ => [],
        };
        return Task.FromResult(new TaskPlan(plan) { Warnings = warnings });
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        // Validate the input before touching the device.
        var payload = Read(payloadJson);
        OverlayRules.ThrowIfInvalid(payload);
        await OverlaysTaskRunner.RunAsync(ctx, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The task's name: "Add text overlay "Lobby"", "Add 3 text overlays", "Set 2 text overlays", "Remove text overlay
    /// "Lobby"", "Remove 2 text overlays", "Remove all text overlays".
    /// </summary>
    public string GetTaskName(string? payloadJson)
    {
        OverlaysPayload payload;
        try
        {
            payload = Read(payloadJson);
        }
        catch (ArgumentException)
        {
            return DisplayName;
        }

        return TaskName(payload);
    }

    internal static string TaskName(OverlaysPayload payload)
    {
        var count = payload.Overlays.Count;
        var many = string.Create(CultureInfo.InvariantCulture, $"{count} text overlays");
        var prefix = payload.Mode switch
        {
            OverlaysMode.Replace => "Set text overlay ",
            OverlaysMode.RemoveMatching => "Remove text overlay ",
            _ => "Add text overlay ",
        };

        // One overlay: its whole text on one line, shortened only past the longest task name.
        var one = count == 1 ? $"\"{OverlayNames.OneLine(payload.Overlays[0].Text, TaskPluginNames.MaxTaskNameLength - prefix.Length - 2)}\"" : null;
        return payload.Mode switch
        {
            OverlaysMode.RemoveAll => "Remove all text overlays",
            OverlaysMode.Replace => one is null ? "Set " + many : prefix + one,
            OverlaysMode.RemoveMatching => one is null ? "Remove " + many : prefix + one,
            _ => one is null ? "Add " + many : prefix + one,
        };
    }

    /// <summary>The user's query (SDK 0.4.0): reads only the queried device, which the host checked against the user's view.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        QueryAsync(ctx, device, method, payloadJson, ct);

    /// <summary>Read-only. <c>listOverlays</c>: the limits and the text overlays of view area 1 (<see cref="OverlaysQueryResult"/>).</summary>
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, OverlaysJson.ListOverlaysMethod, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query \"{method}\".");
        }

        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        if (!CachedApiCheck.Supports(apis, DynamicOverlayApi.ApiId, DynamicOverlayApi.Min))
        {
            return OverlaysJson.Serialize(new OverlaysQueryResult(false, "This camera does not support text overlays.", null, []));
        }

        var limits = await DynamicOverlayApi.GetCapabilitiesAsync(ctx.Vapix, ct).ConfigureAwait(false);
        var list = await DynamicOverlayApi.ListAsync(ctx.Vapix, ct).ConfigureAwait(false);
        return OverlaysJson.Serialize(new OverlaysQueryResult(true, null, limits, [.. list.TextsOf(DynamicOverlayApi.ViewArea).Select(t => t.Overlay)]));
    }
}

/// <summary>"Add overlays": the overlays go next to the camera's own; one the camera already shows is skipped.</summary>
public sealed class AddOverlaysTask() : OverlaysTaskPlugin(OverlaysMode.Add)
{
    public const string PluginId = PackageId + ".add";

    public override string Id => PluginId;

    public override string DisplayName => "Add overlays";

    public override string? IconKey => "add";
}

/// <summary>"Replace overlays": the camera ends up with exactly these text overlays in view area 1.</summary>
public sealed class ReplaceOverlaysTask() : OverlaysTaskPlugin(OverlaysMode.Replace)
{
    public const string PluginId = PackageId + ".replace";

    public override string Id => PluginId;

    public override string DisplayName => "Replace overlays";

    public override string? IconKey => "text";
}

/// <summary>"Remove matching overlays": removes the text overlays with one of the list's texts.</summary>
public sealed class RemoveMatchingOverlaysTask() : OverlaysTaskPlugin(OverlaysMode.RemoveMatching)
{
    public const string PluginId = PackageId + ".remove-matching";

    public override string Id => PluginId;

    public override string DisplayName => "Remove matching overlays";

    public override string? IconKey => "eraser";
}

/// <summary>"Remove all overlays": removes every text overlay of view area 1 after a confirmation; last in the menu.</summary>
public sealed class RemoveAllOverlaysTask() : OverlaysTaskPlugin(OverlaysMode.RemoveAll)
{
    public const string PluginId = PackageId + ".remove-all";

    public override string Id => PluginId;

    public override string DisplayName => "Remove all overlays";

    public override string? IconKey => "deleteAll";
}
