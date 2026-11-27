using Oadm.Plugins.LicensePlates.Alpv;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Tasks;

/// <summary>
/// The task "Set up License Plate Verifier" (one per camera; only the page starts it, so its permission is the page's Set up
/// cameras). Before the first write: ALPV read fresh (ALPV 3, licensed, running, first sensor), every value checked, the
/// areas, the configuration and the event switches read. Then only what differs is written, each part read back: both
/// areas (<c>config_json.cgi</c>), the changed recognition fields the camera has (<c>config.cgi</c>), and New, Update and
/// Lost turned on (<c>vapix_events.cgi</c>, only the switches that are off; nothing is turned off). Secrets of ALPV's
/// configuration never leave the parse.
/// </summary>
internal sealed class SetupTask(Action<Guid>? afterTask = null) : ITaskPlugin
{
    public const string CheckStep = "Check License Plate Verifier";
    public const string AreasStep = "Save the areas";
    public const string RecognitionStep = "Save the recognition settings";
    public const string EventsStep = "Turn on the events";

    public string Id => LicensePlatesInfo.SetupTaskId;

    public string DisplayName => "Set up License Plate Verifier";

    public string? IconKey => LicensePlatesInfo.IconKey;

    public string Group => TaskGroups.Video;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public bool CanRun(IDeviceInfo device) => device is not null && device.HasVideo;

    public string? NotSupportedReason(IDeviceInfo device) => "Needs a camera with License Plate Verifier";

    public IReadOnlyList<PayloadError> ValidatePayload(string? payloadJson, ICallContext caller)
    {
        var payload = PluginJson.TryParse<SetupPayload>(payloadJson, LicensePlatesJson.Options);
        return payload is null
            ? [new PayloadError(string.Empty, PluginJson.UnreadableRequest)]
            : [.. SetupRules.Check(payload).Select(e => new PayloadError(e.Key, e.Value))];
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = PluginJson.Parse<SetupPayload>(payloadJson, LicensePlatesJson.Options);
        if (SetupRules.Check(payload).Values.FirstOrDefault() is { } invalid)
        {
            throw new ArgumentException(invalid + " " + LicensePlateTexts.NothingChanged);
        }

        ctx.PlanSteps(CheckStep, AreasStep, RecognitionStep, EventsStep);
        try
        {
            await RunAsync(ctx, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            afterTask?.Invoke(device.Id);
        }
    }

    private static async Task RunAsync(ITaskExecutionContext ctx, SetupPayload payload, CancellationToken ct)
    {
        var alpv = new AlpvClient(ctx.Vapix);

        // Every read and check before the first write.
        var (config, areas, events) = await ctx.StepAsync(CheckStep, async _ =>
        {
            var app = await alpv.RequireAsync(ct).ConfigureAwait(false);
            var config = await alpv.ReadConfigAsync(ct).ConfigureAwait(false);
            if (!config.ReadsFirstSensor)
            {
                throw new DeviceNotCompatibleException(LicensePlateTexts.OtherSensor + ". " + LicensePlateTexts.NothingChanged);
            }

            if (payload.Areas is not null && config.Rotated)
            {
                throw new DeviceNotCompatibleException(LicensePlateTexts.Rotated + ". " + LicensePlateTexts.NothingChanged);
            }

            var areas = payload.Areas is null ? null : await alpv.ReadAreasAsync(ct).ConfigureAwait(false);
            if (areas is not null && areas.All(a => a.Area.Id != 1))
            {
                throw new AlpvException("ALPV has no area 1. " + LicensePlateTexts.NothingChanged);
            }

            var events = await alpv.ReadEventsAsync(ct).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, "License Plate Verifier " + app.Version);
            return (config, areas, events);
        }).ConfigureAwait(false);

        var changes = SetupRules.Changes(config, payload.Recognition);
        if (payload.Areas is { } wanted && areas is not null)
        {
            if (Same(areas, wanted))
            {
                ctx.SkipStep(AreasStep, "The areas are as chosen.");
            }
            else
            {
                await ctx.StepAsync(AreasStep, async _ =>
                {
                    await alpv.WriteAreasAsync(AlpvClient.AreasBody(areas, wanted), ct).ConfigureAwait(false);
                    var back = await alpv.ReadAreasAsync(ct).ConfigureAwait(false);
                    if (!Same(back, wanted))
                    {
                        throw new AlpvException("ALPV did not keep the areas.");
                    }

                    ctx.Log(TaskLogLevel.Info, "Saved the areas.");
                }).ConfigureAwait(false);
            }
        }
        else
        {
            ctx.SkipStep(AreasStep, "The areas stay as they are.");
        }

        if (AlpvClient.ConfigBody(changes) is { } body)
        {
            await ctx.StepAsync(RecognitionStep, async _ =>
            {
                await alpv.WriteConfigAsync(body, ct).ConfigureAwait(false);
                var back = await alpv.ReadConfigAsync(ct).ConfigureAwait(false);
                if (SetupRules.NotKept(back, changes) is { Count: > 0 } lost)
                {
                    throw new AlpvException("ALPV did not keep " + string.Join(", ", lost) + ".");
                }

                foreach (var (path, value) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    ctx.Log(TaskLogLevel.Info, $"{path}: {config.Get(path)} -> {value}");
                }
            }).ConfigureAwait(false);
        }
        else
        {
            ctx.SkipStep(RecognitionStep, "The recognition settings are as chosen.");
        }

        if (AlpvClient.EventsBody(events) is { } switches)
        {
            await ctx.StepAsync(EventsStep, async _ =>
            {
                await alpv.WriteEventsAsync(switches, ct).ConfigureAwait(false);
                var back = AlpvClient.SwitchesOf(await alpv.ReadEventsAsync(ct).ConfigureAwait(false));
                if (!back.AllOn)
                {
                    throw new AlpvException("ALPV did not turn on its events New, Update and Lost.");
                }

                ctx.Log(TaskLogLevel.Info, "Turned on the events New, Update and Lost.");
            }).ConfigureAwait(false);
        }
        else
        {
            ctx.SkipStep(EventsStep, "The events are on.");
        }
    }

    /// <summary>Whether the camera's areas are the chosen ones (area 2 not chosen: it is off).</summary>
    internal static bool Same(IReadOnlyList<AreaJson> read, IReadOnlyList<AlpvArea> wanted)
    {
        foreach (var current in read)
        {
            var want = wanted.FirstOrDefault(w => w.Id == current.Area.Id);
            if (want is null)
            {
                if (current.Area.Id != 1 && current.Area.Active)
                {
                    return false;
                }

                continue;
            }

            if ((want.Id == 1 || want.Active) != current.Area.Active || want.Angle != current.Area.Angle || !want.Corners.SequenceEqual(current.Area.Corners))
            {
                return false;
            }
        }

        return wanted.All(w => read.Any(r => r.Area.Id == w.Id));
    }
}
