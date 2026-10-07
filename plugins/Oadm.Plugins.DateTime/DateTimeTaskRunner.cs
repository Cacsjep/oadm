using System.Globalization;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DateAndTime;

/// <summary>
/// The per-device task: Check compatibility, Read current time settings, Read NTP settings, Validate settings, Set time
/// zone, Set NTP configuration / Turn off NTP, Set date and time, Verify time settings, Verify NTP settings. One step
/// per device request; sections the user did not change are not planned, sections the device already has are
/// Skipped ("Already ...").
/// </summary>
internal sealed class DateTimeTaskRunner(TimeProvider time, Func<TimeZoneInfo> serverZone)
{
    internal const string StepCheckCompatibility = "Check compatibility";
    internal const string StepReadTime = "Read current time settings";
    internal const string StepReadNtp = "Read NTP settings";
    internal const string StepValidate = "Validate settings";
    internal const string StepVerifyTime = "Verify time settings";
    internal const string StepVerifyNtp = "Verify NTP settings";

    /// <summary>Largest difference between device and expected time that still counts as set (ACS alarms above 2 s).</summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public async Task RunAsync(ITaskExecutionContext ctx, IDeviceInfo device, DateTimePayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(payload);
        ctx.PlanSteps(PlannedSteps(payload));

        // 1. Compatibility against a fresh API list (Require per method happens in Validate, before any write).
        var apis = await ctx.StepAsync(StepCheckCompatibility, async step =>
        {
            var list = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            if (!TimeApis.CanConfigure(list))
            {
                list.Require(TimeApis.TimeService, TimeApis.TimeServiceBase);
            }

            step.Complete(TimeApis.Describe(list as IReadOnlyCollection<DeviceApi> ?? [.. list]));
            return list;
        }).ConfigureAwait(false);

        // 2. Read (one step per request).
        var current = await ReadTimeStepAsync(ctx, apis, StepReadTime, new CurrentTimeSettings { Source = TimeApis.Describe([.. apis]) }, ct).ConfigureAwait(false);
        current = await ReadNtpStepAsync(ctx, apis, StepReadNtp, current, ct).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, "Current: " + DescribeCurrent(current) + ".");

        // 3. Validate and plan.
        var plan = await ctx.StepAsync(StepValidate, step =>
        {
            var built = TimePlanner.Build(payload, apis, current, ServerTimeZone.Resolve(serverZone()), time.GetUtcNow().Year);
            var writes = built.Writes.Count();
            step.Complete(writes == 0 ? "Nothing to change" : writes == 1 ? "1 setting to change" : $"{writes} settings to change");
            return Task.FromResult(built);
        }).ConfigureAwait(false);

        // 4. Write, one step per request.
        var done = new List<string>();
        DateTimeOffset? setUtc = null;
        long setAt = 0;
        foreach (var section in plan.Sections)
        {
            if (section.SkipReason is { } reason)
            {
                if (section.SkipIsWarning)
                {
                    using var warn = ctx.BeginStep(section.StepName);
                    warn.Warn(reason);
                }
                else
                {
                    ctx.SkipStep(section.StepName, reason);
                }

                continue;
            }

            if (section.Kind == SectionKind.DateTime)
            {
                using var step = ctx.BeginStep(section.StepName);
                var utc = plan.UseServerTime ? time.GetUtcNow() : plan.ManualUtc!.Value;
                await WriteAsync(ctx, plan.DateTimeRequest(utc), section, done, ct).ConfigureAwait(false);
                setUtc = utc;
                setAt = time.GetTimestamp();
                step.Complete(plan.UseServerTime ? $"{TimePlan.FormatUtc(utc)} (server time)" : TimePlan.FormatUtc(utc));
                done.Add(section.Description);
                continue;
            }

            for (var r = 0; r < section.Requests.Count; r++)
            {
                using var step = ctx.BeginStep(r == 0 ? section.StepName : $"{section.StepName} ({r + 1} of {section.Requests.Count})");
                await WriteAsync(ctx, section.Requests[r], section, done, ct).ConfigureAwait(false);
                step.Complete(section.Description);
            }

            done.Add(section.Description);
            ctx.Log(TaskLogLevel.Info, section.Description + ": applied.");
        }

        // 5. Verify what was written.
        var wroteTime = plan.Writes.Any(s => s.Kind is SectionKind.TimeZone or SectionKind.DateTime);
        var wroteNtp = plan.Writes.Any(s => s.Kind == SectionKind.Ntp);
        if (!wroteTime)
        {
            ctx.SkipStep(StepVerifyTime, done.Count == 0 ? "Nothing was changed." : "Time zone and time were not changed.");
        }
        else
        {
            var after = await ReadTimeStepAsync(ctx, apis, StepVerifyTime, new CurrentTimeSettings(), ct, verify: (step, read) =>
                VerifyTime(step, plan, read, setUtc is { } u ? u + time.GetElapsedTime(setAt) : null)).ConfigureAwait(false);
            current = current with { DeviceUtc = after.DeviceUtc };
        }

        if (!wroteNtp)
        {
            if (payload.ChangesSync)
            {
                ctx.SkipStep(StepVerifyNtp, done.Count == 0 ? "Nothing was changed." : "NTP was not changed.");
            }
        }
        else
        {
            await ReadNtpStepAsync(ctx, apis, StepVerifyNtp, new CurrentTimeSettings(), ct, verify: (step, read) => VerifyNtp(step, plan.Expected, read)).ConfigureAwait(false);
        }
    }

    /// <summary>Steps shown as Pending from the start: only the sections the payload changes.</summary>
    internal static string[] PlannedSteps(DateTimePayload payload)
    {
        var steps = new List<string> { StepCheckCompatibility, StepReadTime, StepReadNtp, StepValidate };
        if (payload.ChangesTimeZone || payload.Mode == TimeMode.ServerTime)
        {
            steps.Add(TimePlanner.StepSetTimeZone);
        }

        if (payload.ChangesSync)
        {
            steps.Add(payload.Mode == TimeMode.Ntp ? TimePlanner.StepSetNtp : TimePlanner.StepTurnOffNtp);
        }

        if (payload.Mode is TimeMode.Manual or TimeMode.ServerTime)
        {
            steps.Add(TimePlanner.StepSetDateTime);
        }

        steps.Add(StepVerifyTime);
        if (payload.ChangesSync)
        {
            steps.Add(StepVerifyNtp);
        }

        return [.. steps];
    }

    private static async Task WriteAsync(ITaskExecutionContext ctx, TimeRequest request, PlannedSection section, List<string> done, CancellationToken ct)
    {
        try
        {
            await TimeClient.SendAsync(ctx.Vapix, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var applied = done.Count == 0 ? "Nothing was changed." : "Already applied: " + string.Join("; ", done) + ".";
            ctx.Log(TaskLogLevel.Error, $"{section.Description} failed: {ex.Message}");
            var message = ex.Message.TrimEnd();
            throw new InvalidOperationException($"{message}{(message.EndsWith('.') ? string.Empty : ".")} {applied}", ex);
        }
    }

    private async Task<CurrentTimeSettings> ReadTimeStepAsync(
        ITaskExecutionContext ctx, IReadOnlyList<DeviceApi> apis, string name, CurrentTimeSettings into, CancellationToken ct,
        Action<ITaskStep, CurrentTimeSettings>? verify = null)
    {
        return await ctx.StepAsync(name, async step =>
        {
            var read = TimeApis.HasTimeService(apis)
                ? await TimeClient.ReadDateTimeInfoAsync(ctx.Vapix, apis, into, time, ct).ConfigureAwait(false)
                : await TimeClient.ReadParametersAsync(ctx.Vapix, apis, into, ct).ConfigureAwait(false);
            if (verify is not null)
            {
                verify(step, read);
            }
            else
            {
                step.Complete(DescribeTime(read));
            }

            return read;
        }).ConfigureAwait(false);
    }

    private static async Task<CurrentTimeSettings> ReadNtpStepAsync(
        ITaskExecutionContext ctx, IReadOnlyList<DeviceApi> apis, string name, CurrentTimeSettings into, CancellationToken ct,
        Action<ITaskStep, CurrentTimeSettings>? verify = null)
    {
        var timeReadUsedParams = !TimeApis.HasTimeService(apis);
        if (!TimeApis.HasNtpApi(apis) && timeReadUsedParams)
        {
            if (verify is not null)
            {
                // param.cgi: the time read already returned the NTP parameters; read them again for the check.
                return await ctx.StepAsync(name, async step =>
                {
                    var read = await TimeClient.ReadParametersAsync(ctx.Vapix, apis, into, ct).ConfigureAwait(false);
                    verify(step, read);
                    return read;
                }).ConfigureAwait(false);
            }

            ctx.SkipStep(name, "Read with the current time settings (param.cgi)");
            return into;
        }

        return await ctx.StepAsync(name, async step =>
        {
            var read = TimeApis.HasNtpApi(apis)
                ? await TimeClient.ReadNtpInfoAsync(ctx.Vapix, apis, into, ct).ConfigureAwait(false)
                : await TimeClient.ReadParametersAsync(ctx.Vapix, apis, into, ct).ConfigureAwait(false);
            if (verify is not null)
            {
                verify(step, read);
            }
            else
            {
                step.Complete(DescribeNtp(read));
            }

            return read;
        }).ConfigureAwait(false);
    }

    internal static void VerifyTime(ITaskStep step, TimePlan plan, CurrentTimeSettings read, DateTimeOffset? expectedUtc)
    {
        var problems = new List<string>();
        var expected = plan.Expected;
        if (expected.TimeZone is { } zone && !string.Equals(read.TimeZone, zone, StringComparison.Ordinal))
        {
            problems.Add($"the device reports time zone {read.TimeZone ?? read.PosixTimeZone ?? "unknown"} instead of {zone}");
        }

        if (expected.PosixTimeZone is { } posix && read.PosixTimeZone is not null && !string.Equals(read.PosixTimeZone, posix, StringComparison.Ordinal))
        {
            problems.Add($"the device reports time zone {read.PosixTimeZone} instead of {posix}");
        }

        if (expected.DstEnabled is { } dst && read.DstEnabled is { } readDst && readDst != dst)
        {
            problems.Add(dst ? "daylight saving time is off" : "daylight saving time is still on");
        }

        if (expectedUtc is { } want && read.DeviceUtc is { } got)
        {
            var diff = got - want;
            if (diff.Duration() > Tolerance + TimeSpan.FromSeconds(1))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"the device time {TimePlan.FormatUtc(got)} differs by {diff.TotalSeconds:0.0} s from {TimePlan.FormatUtc(want)}"));
            }
        }

        if (problems.Count > 0)
        {
            step.Warn("Check failed: " + string.Join("; ", problems) + ".");
            return;
        }

        step.Complete(DescribeTime(read));
    }

    internal static void VerifyNtp(ITaskStep step, ExpectedState expected, CurrentTimeSettings read)
    {
        var problems = new List<string>();
        if (expected.NtpEnabled is { } on && read.NtpEnabled is { } readOn && on != readOn)
        {
            problems.Add(on ? "NTP is off" : "NTP is still on");
        }

        if (expected.NtpSource is { } source && read.NtpSource is { } readSource && source != readSource)
        {
            problems.Add(source == NtpSource.Dhcp ? "the servers do not come from DHCP" : "the servers still come from DHCP");
        }

        if (expected.NtpServers is { } servers && !servers.SequenceEqual(read.NtpServers, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("the device lists the NTP servers " + (read.NtpServers.Count == 0 ? "(none)" : string.Join(", ", read.NtpServers)));
        }

        if (expected.NtsEnabled is { } nts && read.NtsEnabled is { } readNts && nts != readNts)
        {
            problems.Add(nts ? "NTS is off" : "NTS is still on");
        }

        if (problems.Count > 0)
        {
            step.Warn("Check failed: " + string.Join("; ", problems) + ".");
            return;
        }

        var detail = DescribeNtp(read);
        if (expected.NtpEnabled == true && read.Synced == false)
        {
            detail += " (the device synchronizes within a few minutes)";
        }

        step.Complete(detail);
    }

    internal static string DescribeTime(CurrentTimeSettings s)
    {
        var parts = new List<string>();
        if (s.DeviceLocal is { } local)
        {
            parts.Add(local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (s.DeviceUtc is null ? string.Empty : " " + TimeZoneCatalog.FormatOffset(local.Offset)));
        }

        parts.Add(s.TimeZone ?? (s.PosixTimeZone is { } p ? "POSIX " + p : "time zone unknown"));
        if (s.TimeZone is null && s.DstEnabled is { } dst)
        {
            parts.Add(dst ? "daylight saving on" : "daylight saving off");
        }

        return string.Join(", ", parts);
    }

    internal static string DescribeNtp(CurrentTimeSettings s)
    {
        if (s.NtpEnabled == false)
        {
            return "NTP off";
        }

        if (s.NtsEnabled == true)
        {
            return "NTS KE servers " + (s.NtsServers.Count == 0 ? "(none)" : string.Join(", ", s.NtsServers)) + Synced(s);
        }

        var servers = s.NtpSource == NtpSource.Dhcp
            ? "NTP servers from DHCP" + (s.AdvertisedServers.Count == 0 ? string.Empty : " (" + string.Join(", ", s.AdvertisedServers) + ")")
            : "NTP servers " + (s.NtpServers.Count == 0 ? "(none)" : string.Join(", ", s.NtpServers));
        return servers + Synced(s);

        static string Synced(CurrentTimeSettings s) => s.Synced switch
        {
            true => ", synchronized",
            false => ", not synchronized",
            _ => string.Empty,
        };
    }

    internal static string DescribeCurrent(CurrentTimeSettings s) => DescribeTime(s) + "; " + DescribeNtp(s);
}
