using System.Globalization;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays;

/// <summary>
/// One camera: Check the overlay support, Read the overlay limits, Read the overlays (and the camera's sensors), Check the
/// overlays (every limit of every sensor's view area before the first write), one step per addition and per removal
/// (additions first when the free slots allow it, else removals first), Verify the overlays. Every sensor gets the same
/// list in its first view area. The overlays are listed right before the writes and matched by content: identities are
/// never kept.
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

        var (current, viewAreas) = await ctx.StepAsync(StepReadOverlays, async step =>
        {
            var areas = await DynamicOverlayApi.SensorViewAreasAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var read = await DynamicOverlayApi.ListAsync(ctx.Vapix, ct).ConfigureAwait(false);
            step.Complete(DescribeTexts(read, areas));
            return (read, areas);
        }).ConfigureAwait(false);

        var plans = await ctx.StepAsync(StepCheck, step =>
        {
            var built = OverlayPlanner.BuildAll(payload, current, limits, viewAreas);
            step.Complete(DescribePlan(built));
            return Task.FromResult(built);
        }).ConfigureAwait(false);

        if (plans.All(p => p.ChangesNothing))
        {
            ctx.SkipStep(StepVerify, "Nothing was changed.");
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        int removed = 0, added = 0;
        foreach (var plan in plans)
        {
            // Step names name the view area only on a camera with several sensors.
            var where = viewAreas.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" in view area {plan.ViewArea}") : string.Empty;
            async Task RemoveAllAsync()
            {
                foreach (var overlay in plan.Remove)
                {
                    using (ctx.BeginStep(Unique(names, $"Remove the overlay \"{OverlayNames.Brief(overlay.Overlay.Text)}\"{where}")))
                    {
                        await WriteAsync(() => DynamicOverlayApi.RemoveAsync(ctx.Vapix, overlay.Identity, ct), removed, added).ConfigureAwait(false);
                    }

                    removed++;
                }
            }

            async Task AddAllAsync()
            {
                foreach (var overlay in plan.Add)
                {
                    using (var step = ctx.BeginStep(Unique(names, $"Add the overlay \"{OverlayNames.Brief(overlay.Text)}\"{where}")))
                    {
                        await WriteAsync(() => DynamicOverlayApi.AddTextAsync(ctx.Vapix, overlay, plan.ViewArea, ct), removed, added).ConfigureAwait(false);
                        step.Complete(OverlayNames.Of(overlay.Position));
                    }

                    added++;
                }
            }

            // A refused add after the removals would leave the view area without text overlays: add first while the slots allow it.
            if (plan.AddFirst)
            {
                await AddAllAsync().ConfigureAwait(false);
                await RemoveAllAsync().ConfigureAwait(false);
            }
            else
            {
                await RemoveAllAsync().ConfigureAwait(false);
                await AddAllAsync().ConfigureAwait(false);
            }
        }

        ctx.Log(TaskLogLevel.Info, string.Create(CultureInfo.InvariantCulture,
            $"Removed {removed}, added {added} text overlays in view area {string.Join(", ", viewAreas)}."));
        await ctx.StepAsync(StepVerify, async step =>
        {
            var after = await DynamicOverlayApi.ListAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var problem = plans.Select(p => Verify(payload, p, after, viewAreas.Count > 1)).FirstOrDefault(p => p is not null);
            if (problem is null)
            {
                step.Complete(DescribeTexts(after, viewAreas));
            }
            else
            {
                step.Warn(problem);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>"2 text overlays in view area 1", one part per view area on a camera with several sensors.</summary>
    internal static string DescribeTexts(DeviceOverlays overlays, IReadOnlyList<int> viewAreas) =>
        string.Join(", ", viewAreas.Select(v => Count(overlays.TextsOf(v).Count, "text overlay") + string.Create(CultureInfo.InvariantCulture, $" in view area {v}")));

    /// <summary>The plan's view area after the writes: null when it shows what was planned, else what differs.</summary>
    internal static string? Verify(OverlaysPayload payload, OverlayPlan plan, DeviceOverlays after, bool nameViewArea = false)
    {
        var where = nameViewArea ? string.Create(CultureInfo.InvariantCulture, $" in view area {plan.ViewArea}") : string.Empty;
        var shown = after.TextsOf(plan.ViewArea).Select(t => t.Overlay).ToList();
        if (payload.Mode == OverlaysMode.RemoveAll)
        {
            return shown.Count == 0 ? null : $"The camera still shows \"{OverlayNames.Brief(shown[0].Text)}\"{where}.";
        }

        if (payload.Mode == OverlaysMode.RemoveMatching)
        {
            var gone = plan.Remove.Select(r => OverlayText.Normalize(r.Overlay.Text)).ToHashSet(StringComparer.Ordinal);
            var still = shown.FirstOrDefault(s => gone.Contains(OverlayText.Normalize(s.Text)));
            return still is null ? null : $"The camera still shows \"{OverlayNames.Brief(still.Text)}\"{where}.";
        }

        var unused = shown.ToList();
        foreach (var wanted in payload.Overlays)
        {
            var match = unused.FirstOrDefault(s => OverlayPlanner.Same(s, wanted));
            if (match is null)
            {
                return $"The camera does not show \"{OverlayNames.Brief(wanted.Text)}\" as set{where}.";
            }

            unused.Remove(match);
        }

        return payload.Mode == OverlaysMode.Replace && unused.Count > 0
            ? $"The camera still shows \"{OverlayNames.Brief(unused[0].Text)}\"{where}."
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

    internal static string DescribePlan(OverlayPlan plan) => DescribePlan([plan]);

    /// <summary>The plans of every view area summed up: "2 to remove, 4 to add".</summary>
    internal static string DescribePlan(IReadOnlyList<OverlayPlan> plans)
    {
        var parts = new List<string>();
        var remove = plans.Sum(p => p.Remove.Count);
        var add = plans.Sum(p => p.Add.Count);
        var there = plans.Sum(p => p.AlreadyThere.Count);
        var notFound = plans.Select(p => p.NotFound.Count).DefaultIfEmpty(0).Max();
        if (remove > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{remove} to remove"));
        }

        if (add > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{add} to add"));
        }

        if (there > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{there} already on the camera"));
        }

        if (notFound > 0)
        {
            parts.Add(notFound == 1 ? "1 text is not on the camera" : string.Create(CultureInfo.InvariantCulture, $"{notFound} texts are not on the camera"));
        }

        return parts.Count == 0 ? "Nothing to change" : string.Join(", ", parts);
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? string.Empty : "s")}");
}
