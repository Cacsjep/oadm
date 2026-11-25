using System.Globalization;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Autofocus;

/// <summary>
/// Runs the autofocus of every lens of a camera (README): a multisensor camera has one lens per sensor, each is focused in
/// turn and has its own step ("Focus camera 2"). Optics Control API (<see cref="LensFocus"/>), checked in a fresh API list
/// before the first call. A lens that finds no focus is a warning and the others still run; only the lens moves, nothing
/// else is written.
/// </summary>
public sealed class AutofocusTaskPlugin : ITaskPlugin
{
    public const string PluginId = "oadm.autofocus";

    public const string StepCheck = "Check the lenses";

    /// <summary>"No lens of this camera has remote focus."</summary>
    public const string NoRemoteFocus = "No lens of this camera has remote focus.";

    /// <summary>The longest one lens may search (the camera answers when its search ended, usually after about 4 s).</summary>
    public static readonly TimeSpan DefaultLensTimeout = TimeSpan.FromMinutes(2);

    private static readonly Version Required = new(1, 0);

    private readonly TimeSpan _lensTimeout;
    private readonly TimeProvider _time;

    public AutofocusTaskPlugin()
        : this(DefaultLensTimeout, TimeProvider.System)
    {
    }

    public AutofocusTaskPlugin(TimeSpan lensTimeout, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lensTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _lensTimeout = lensTimeout;
        _time = timeProvider;
    }

    public string Id => PluginId;

    public string DisplayName => "Autofocus";

    /// <summary>No settings: its schema is an empty object, so AI agents may run it.</summary>
    public Type? PayloadType => typeof(AutofocusPayload);

    public string GetTaskName(string? payloadJson) => "Autofocus";

    public string Group => TaskGroups.Maintenance;

    public string? IconKey => "focus";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    /// <summary>The step of one lens: "Focus camera 1".</summary>
    public static string LensStep(string opticsId) => "Focus " + LensFocus.LensName(opticsId).ToLowerInvariant();

    /// <summary>A reachable camera whose cached API list has the Optics Control API.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && CachedApiCheck.Supports(device.Apis, LensFocus.OpticsApi, Required);
    }

    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device) ?? TaskSupportReasons.NeedsApi("remote focus", device);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(StepCheck);

        IReadOnlyList<string> lenses;
        using (var step = ctx.BeginStep(StepCheck))
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            apis.Require(LensFocus.OpticsApi, LensFocus.MinVersion); // a different major version is a different API
            lenses = await LensFocus.LensesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            if (lenses.Count == 0)
            {
                step.Warn(NoRemoteFocus);
                return;
            }

            step.Complete(lenses.Count == 1 ? "1 lens" : string.Create(CultureInfo.InvariantCulture, $"{lenses.Count} lenses"));
        }

        foreach (var lens in lenses)
        {
            using var step = ctx.BeginStep(LensStep(lens));
            using var limit = new CancellationTokenSource(_lensTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            string? error;
            try
            {
                error = await LensFocus.AutofocusAsync(ctx.Vapix, lens, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"The autofocus did not end within {_lensTimeout.TotalMinutes:0} minutes.");
            }

            if (error is null)
            {
                step.Complete("Focus found");
            }
            else
            {
                step.Warn(error); // the other lenses still focus
            }
        }
    }
}

/// <summary>The settings of Autofocus: none.</summary>
[System.ComponentModel.Description("Autofocus takes no settings.")]
public sealed record AutofocusPayload;
