using System.Globalization;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays;

/// <summary>
/// One camera: Check the overlay support, Read the overlay limits, Read the overlays, Check the overlays (every limit before the
/// first write), one step per removal and per addition, Verify the overlays. The overlays are listed right before the
/// writes and matched by content: identities are never kept.
/// </summary>
internal static class OverlaysTaskRunner
{
    internal const string StepCheckSupport = "Check the overlay support";
    internal const string StepReadLimits = "Read the overlay limits";
    internal const string StepReadOverlays = "Read the overlays";
    internal const string StepCheck = "Check the overlays";
    internal const string StepVerify = "Verify the overlays";

    public static async Task RunAsync(ITaskExecutionContext ctx, OverlaysPayload payload, CancellationToken ct)
    {
        ctx.PlanSteps(StepCheckSupport, StepReadLimits, StepReadOverlays, StepCheck, StepVerify);

        await ctx.StepAsync(StepCheckSupport, async step =>
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            if (!CachedApiCheck.Supports(apis, DynamicOverlayApi.ApiId, DynamicOverlayApi.Min))
            {
                throw new DeviceNotCompatibleException(OverlaysTaskPlugin.NotSupportedNothingChanged);
            }

            step.Complete("Text overlays supported");
        }).ConfigureAwait(false);

        var limits = await ctx.StepAsync(StepReadLimits, async step =>
        {
            var read = await DynamicOverlayApi.GetCapabilitiesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            step.Complete(DescribeLimits(read));
            return read;
        }).ConfigureAwait(false);

        var current = await ctx.StepAsync(StepReadOverlays, async step =>
        {
            var read = await DynamicOverlayApi.ListAsync(ctx.Vapix, ct).ConfigureAwait(false);
            step.Complete(Count(read.TextsOf(DynamicOverlayApi.ViewArea).Count, "text overlay") + " in view area 1");
            return read;
        }).ConfigureAwait(false);

        var plan = await ctx.StepAsync(StepCheck, step =>
        {
            var built = OverlayPlanner.Build(payload, current, limits);
            step.Complete(DescribePlan(built));
            return Task.FromResult(built);
        }).ConfigureAwait(false);

        if (plan.ChangesNothing)
        {
            ctx.SkipStep(StepVerify, "Nothing was changed.");
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        int removed = 0, added = 0;
        foreach (var overlay in plan.Remove)
        {
            using (ctx.BeginStep(Unique(names, $"Remove the overlay \"{OverlayNames.Brief(overlay.Overlay.Text)}\"")))
            {
                await WriteAsync(() => DynamicOverlayApi.RemoveAsync(ctx.Vapix, overlay.Identity, ct), removed, added).ConfigureAwait(false);
            }

            removed++;
        }

        foreach (var overlay in plan.Add)
        {
            using (var step = ctx.BeginStep(Unique(names, $"Add the overlay \"{OverlayNames.Brief(overlay.Text)}\"")))
            {
                await WriteAsync(() => DynamicOverlayApi.AddTextAsync(ctx.Vapix, overlay, ct), removed, added).ConfigureAwait(false);
                step.Complete(OverlayNames.Of(overlay.Position));
            }

            added++;
        }

        ctx.Log(TaskLogLevel.Info, string.Create(CultureInfo.InvariantCulture, $"Removed {removed}, added {added} text overlays in view area 1."));
        await ctx.StepAsync(StepVerify, async step =>
        {
            var after = await DynamicOverlayApi.ListAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var problem = Verify(payload, plan, after);
            if (problem is null)
            {
                step.Complete(Count(after.TextsOf(DynamicOverlayApi.ViewArea).Count, "text overlay") + " in view area 1");
            }
            else
            {
                step.Warn(problem);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>The camera's list after the writes: null when it shows what was planned, else what differs.</summary>
    internal static string? Verify(OverlaysPayload payload, OverlayPlan plan, DeviceOverlays after)
    {
        var shown = after.TextsOf(DynamicOverlayApi.ViewArea).Select(t => t.Overlay).ToList();
        if (payload.Mode == OverlaysMode.RemoveAll)
        {
            return shown.Count == 0 ? null : $"The camera still shows \"{OverlayNames.Brief(shown[0].Text)}\".";
        }

        if (payload.Mode == OverlaysMode.RemoveMatching)
        {
            var gone = plan.Remove.Select(r => OverlayText.Normalize(r.Overlay.Text)).ToHashSet(StringComparer.Ordinal);
            var still = shown.FirstOrDefault(s => gone.Contains(OverlayText.Normalize(s.Text)));
            return still is null ? null : $"The camera still shows \"{OverlayNames.Brief(still.Text)}\".";
        }

        var unused = shown.ToList();
        foreach (var wanted in payload.Overlays)
        {
            var match = unused.FirstOrDefault(s => OverlayPlanner.Same(s, wanted));
            if (match is null)
            {
                return $"The camera does not show \"{OverlayNames.Brief(wanted.Text)}\" as set.";
            }

            unused.Remove(match);
        }

        return payload.Mode == OverlaysMode.Replace && unused.Count > 0
            ? $"The camera still shows \"{OverlayNames.Brief(unused[0].Text)}\"."
            : null;
    }

    private static async Task WriteAsync(Func<Task> write, int removed, int added)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (OverlayDeviceException ex)
        {
            var done = removed == 0 && added == 0
                ? "Nothing was changed."
                : string.Create(CultureInfo.InvariantCulture, $"Already done: {removed} removed, {added} added.");
            throw new OverlayDeviceException(ex.Code, ex.Message.TrimEnd() + " " + done);
        }
    }

    private static string Unique(HashSet<string> names, string name)
    {
        var unique = name;
        for (var n = 2; !names.Add(unique); n++)
        {
            unique = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
        }

        return unique;
    }

    internal static string DescribeLimits(OverlayCapabilities limits)
    {
        var parts = new List<string>();
        if (limits.Slots is { } slots)
        {
            parts.Add(Count(slots, "slot") + " per view area");
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"text up to {limits.MaxTextLength} characters"));
        if (limits.MinFontSize is { } min && limits.MaxFontSize is { } max)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"sizes {min} to {max}"));
        }

        return string.Join(", ", parts);
    }

    internal static string DescribePlan(OverlayPlan plan)
    {
        var parts = new List<string>();
        if (plan.Remove.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{plan.Remove.Count} to remove"));
        }

        if (plan.Add.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{plan.Add.Count} to add"));
        }

        if (plan.AlreadyThere.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{plan.AlreadyThere.Count} already on the camera"));
        }

        if (plan.NotFound.Count > 0)
        {
            parts.Add(plan.NotFound.Count == 1 ? "1 text is not on the camera" : string.Create(CultureInfo.InvariantCulture, $"{plan.NotFound.Count} texts are not on the camera"));
        }

        return parts.Count == 0 ? "Nothing to change" : string.Join(", ", parts);
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? string.Empty : "s")}");
}
