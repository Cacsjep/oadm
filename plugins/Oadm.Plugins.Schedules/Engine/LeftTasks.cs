using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>
/// Device tasks of runs that ended while their tasks still ran (the plugin was turned off or restarted): kept by the plugin across
/// its engines, so a new run of the same schedule gives none of these devices a second task while its task still runs (rule 3).
/// </summary>
internal sealed class LeftTasks
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<(Guid TaskId, Guid DeviceId)>> _bySchedule = [];

    public void Add(Guid scheduleId, IEnumerable<(Guid TaskId, Guid DeviceId)> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_gate)
        {
            if (!_bySchedule.TryGetValue(scheduleId, out var list))
            {
                _bySchedule[scheduleId] = list = [];
            }

            list.AddRange(tasks);
        }
    }

    /// <summary>The devices of <paramref name="scheduleId"/> whose left task still runs (ended ones are forgotten).</summary>
    public IReadOnlySet<Guid> Busy(Guid scheduleId, ITaskRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        lock (_gate)
        {
            if (!_bySchedule.TryGetValue(scheduleId, out var list))
            {
                return new HashSet<Guid>();
            }

            list.RemoveAll(t => runner.IsActive(t.TaskId) == false);
            if (list.Count == 0)
            {
                _bySchedule.Remove(scheduleId);
            }

            return list.Select(t => t.DeviceId).ToHashSet();
        }
    }
}
