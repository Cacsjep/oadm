using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays;

/// <summary>
/// "Text overlays" on the device context menu (Video): add text overlays to view area 1 of every selected camera, replace
/// its text overlays with the dialog's list, or remove the ones with the list's texts. Text only: image overlays, widgets
/// and privacy masks are never touched. One task per camera (<see cref="OverlaysTaskRunner"/>).
/// </summary>
public sealed class OverlaysTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.overlays";

    /// <summary>The greyed entry's reason and the query's message.</summary>
    public const string NeedsOverlays = "text overlays";

    /// <summary>A camera without the overlay API in the fresh list.</summary>
    public const string NotSupportedNothingChanged = "The camera does not support text overlays. Nothing was changed.";

    /// <summary>The plan's note of Replace.</summary>
    public const string ReplaceNote = "Every other text overlay of view area 1 is removed. Image overlays stay.";

    /// <summary>The plan's note of Add.</summary>
    public const string AddNote = "An overlay a camera already shows is skipped.";

    public string Id => PluginId;

    public string DisplayName => "Text overlays";

    public string Group => TaskGroups.Video;

    public string? IconKey => "text";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    /// <summary>The payload (SDK 0.13.0): its schema is the input of the task for AI agents.</summary>
    public Type? PayloadType => typeof(OverlaysPayload);

    public JsonSerializerOptions? PayloadJsonOptions => OverlaysJson.Options;

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
            return OverlayRules.Check(OverlaysJson.ParsePayload(payloadJson));
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
        var payload = OverlaysJson.ParsePayload(payloadJson);
        var plan = devices.Select(d => CanRunOn(d) ? new TaskPlanDevice(d.Id) : new TaskPlanDevice(d.Id, NotSupportedReason(d) ?? TaskSupportReasons.Default)).ToList();
        IReadOnlyList<string> warnings = payload.Mode switch
        {
            OverlaysMode.Replace when payload.Overlays.Count == 0 => ["Removes every text overlay of view area 1. Image overlays stay."],
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
        var payload = OverlaysJson.ParsePayload(payloadJson);
        OverlayRules.ThrowIfInvalid(payload);
        await OverlaysTaskRunner.RunAsync(ctx, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The task's name: "Add text overlay "Lobby"", "Add 3 text overlays", "Set 2 text overlays", "Remove all text overlays",
    /// "Remove text overlay "Lobby"".
    /// </summary>
    public string GetTaskName(string? payloadJson)
    {
        OverlaysPayload payload;
        try
        {
            payload = OverlaysJson.ParsePayload(payloadJson);
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
        var one = count == 1 ? $"\"{OverlayNames.Brief(payload.Overlays[0].Text, 24)}\"" : null;
        var many = string.Create(CultureInfo.InvariantCulture, $"{count} text overlays");
        return payload.Mode switch
        {
            OverlaysMode.Replace when count == 0 => "Remove all text overlays",
            OverlaysMode.Replace => one is null ? "Set " + many : "Set text overlay " + one,
            OverlaysMode.RemoveMatching => one is null ? "Remove " + many : "Remove text overlay " + one,
            _ => one is null ? "Add " + many : "Add text overlay " + one,
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
