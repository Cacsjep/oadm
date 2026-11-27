using Oadm.Plugins.LicensePlates.Alpv;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Tasks;

/// <summary>
/// The task "Use recommended image settings" (one per camera; only the page starts it): ALPV's own call
/// (<c>api.cgi?api=camera&amp;sensor=setoptimalparams</c>, a write although it is a GET), which changed only Contrast, Local
/// contrast, Max exposure time, Max gain and Tone mapping of the first sensor on the test camera. Before it: ALPV read fresh
/// (ALPV 3, licensed, running, first sensor) and the five parameters read; after it, read again and logged old and new.
/// Nothing is written when ALPV says they are as it recommends already.
/// </summary>
internal sealed class ImageSettingsTask(Action<Guid>? afterTask = null) : ITaskPlugin
{
    public const string CheckStep = "Check License Plate Verifier";
    public const string ReadStep = "Read the image settings";
    public const string ApplyStep = "Use the recommended image settings";
    public const string VerifyStep = "Check the image settings";

    /// <summary>The parameters the call changes (the first sensor's).</summary>
    public static readonly IReadOnlyList<string> Parameters =
    [
        "ImageSource.I0.Sensor.Contrast",
        "ImageSource.I0.Sensor.LocalContrast",
        "ImageSource.I0.Sensor.MaxExposureTime",
        "ImageSource.I0.Sensor.MaxGain",
        "ImageSource.I0.Sensor.ToneMapping",
    ];

    public string Id => LicensePlatesInfo.ImageSettingsTaskId;

    public string DisplayName => "Use recommended image settings";

    public string? IconKey => LicensePlatesInfo.IconKey;

    public string Group => TaskGroups.Video;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public bool CanRun(IDeviceInfo device) => device is not null && device.HasVideo;

    public string? NotSupportedReason(IDeviceInfo device) => "Needs a camera with License Plate Verifier";

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(CheckStep, ReadStep, ApplyStep, VerifyStep);
        try
        {
            await RunAsync(ctx, ct).ConfigureAwait(false);
        }
        finally
        {
            afterTask?.Invoke(device.Id);
        }
    }

    private static async Task RunAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        var alpv = new AlpvClient(ctx.Vapix);
        var config = await ctx.StepAsync(CheckStep, async _ =>
        {
            await alpv.RequireAsync(ct).ConfigureAwait(false);
            var config = await alpv.ReadConfigAsync(ct).ConfigureAwait(false);
            return config.ReadsFirstSensor
                ? config
                : throw new DeviceNotCompatibleException(LicensePlateTexts.OtherSensor + ". " + LicensePlateTexts.NothingChanged);
        }).ConfigureAwait(false);

        var before = await ctx.StepAsync(ReadStep, async _ => await ctx.Vapix.ListParametersAsync(["ImageSource.I0.Sensor"], ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (config.Image is { Recommended: true })
        {
            ctx.SkipStep(ApplyStep, "The image settings are as ALPV recommends.");
            ctx.SkipStep(VerifyStep, "Nothing was changed.");
            return;
        }

        await ctx.StepAsync(ApplyStep, async _ => await alpv.SetOptimalImageAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await ctx.StepAsync(VerifyStep, async _ =>
        {
            var after = await ctx.Vapix.ListParametersAsync(["ImageSource.I0.Sensor"], ct).ConfigureAwait(false);
            foreach (var name in Parameters)
            {
                var old = before.GetValueOrDefault(name) ?? "not set";
                var now = after.GetValueOrDefault(name) ?? "not set";
                ctx.Log(TaskLogLevel.Info, old == now ? $"{name}: {now}" : $"{name}: {old} -> {now}");
            }

            var check = await alpv.ReadConfigAsync(ct).ConfigureAwait(false);
            if (check.Image is { Recommended: false })
            {
                ctx.ReportWarning("ALPV still finds the image settings different from what it recommends.");
            }
        }).ConfigureAwait(false);
    }
}
