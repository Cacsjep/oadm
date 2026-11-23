using System.Globalization;

using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>
/// The event "Schedule run ended" for the event rules (docs/spec/schedules.md "Event for rules"): one per run, never per device,
/// also for Missed and Skipped. A rule sends an email on every run or, with a field check on Result, only when it is not Done.
/// </summary>
public static class RunEndedEvent
{
    public const string ScheduleField = "schedule";
    public const string ResultField = "result";
    public const string DoneField = "done";
    public const string FailedField = "failed";
    public const string NotReachableField = "notReachable";
    public const string NotStartedField = "notStarted";
    public const string FirstFailuresField = "firstFailures";

    /// <summary>Lines of <see cref="FirstFailuresField"/>.</summary>
    public const int MaxFirstFailures = 5;

    /// <summary>The texts of the results (the choices of the field Result).</summary>
    public static IReadOnlyList<string> Results { get; } = ["Done", "Done with warnings", "Failed", "Stopped", "Missed", "Skipped"];

    /// <summary>"Done with warnings".</summary>
    public static string ResultText(RunResult result) => result switch
    {
        RunResult.Done => "Done",
        RunResult.DoneWithWarnings => "Done with warnings",
        RunResult.Failed => "Failed",
        RunResult.Stopped => "Stopped",
        RunResult.Missed => "Missed",
        RunResult.Skipped => "Skipped",
        _ => "Running",
    };

    /// <summary>The event type: stateless, for the server; schedules of every owner, so it is for users with all devices.</summary>
    public static EventTypeInfo Type { get; } = new(
        SchedulesInfo.RunEndedEvent,
        "Schedule run ended",
        SchedulesInfo.DisplayName,
        EventKind.Stateless,
        EventScope.Server,
        [
            new EventField(ScheduleField, "Schedule"),
            new EventField(ResultField, "Result", EventFieldType.Choice, Results),
            new EventField(DoneField, "Done", EventFieldType.Number),
            new EventField(FailedField, "Failed", EventFieldType.Number),
            new EventField(NotReachableField, "Not reachable", EventFieldType.Number),
            new EventField(NotStartedField, "Not started", EventFieldType.Number),
            new EventField(FirstFailuresField, "First failures") { Filter = false },
        ])
    {
        Description = "A schedule's run ended.\n\nValues:\n• {schedule}\n• {result}\n• {done}\n• {failed}\n• {notReachable}\n• {notStarted}\n• {firstFailures}",
        OwnerScopeKey = ICallContext.AllDevicesScopeKey,
    };

    /// <summary>The values of one run's event.</summary>
    public static IReadOnlyDictionary<string, string> Data(string schedule, ScheduleRun run, IReadOnlyList<RunFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(failures);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ScheduleField] = schedule ?? string.Empty,
            [ResultField] = ResultText(run.Result),
            [DoneField] = Number(run.Done + run.Warnings),
            [FailedField] = Number(run.Failed),
            [NotReachableField] = Number(run.NotReachable.Values.Sum()),
            [NotStartedField] = Number(run.NotStarted),
            [FirstFailuresField] = string.Join('\n', failures.Take(MaxFirstFailures).Select(Line)),
        };
    }

    /// <summary>"10.0.0.12 (ACCC8E012345): Restart the device: The device did not come back."</summary>
    public static string Line(RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return string.IsNullOrWhiteSpace(failure.Step) ? $"{failure.Device}: {failure.Message}" : $"{failure.Device}: {failure.Step}: {failure.Message}";
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
