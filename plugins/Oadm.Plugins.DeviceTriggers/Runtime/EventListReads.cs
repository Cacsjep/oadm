using System.Collections.Concurrent;

using Oadm.Plugins.DeviceTriggers.Device;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers.Runtime;

/// <summary>
/// The editor's live reads of event lists (always read from the devices when a trigger is created or
/// edited, never from a cache: an app that is stopped or removed has no events). One job per read, 16 devices at a time,
/// kept in memory for <see cref="DeviceTriggersOptions.ReadJobLifetime"/>. A read belongs to the user who started it
/// (<see cref="ICorePluginContext.Jobs"/>): it reaches devices through that user's devices, and another user's job id is
/// answered like an unknown one.
/// </summary>
internal sealed class EventListReads(ICorePluginContext ctx, DeviceTriggersOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    /// <summary>Starts a read of <paramref name="deviceIds"/> (checked against the caller's devices before) for the caller.</summary>
    public ReadStatusReply Start(ICallContext caller, IReadOnlyList<Guid> deviceIds, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(deviceIds);
        Expire();
        var ids = deviceIds.Distinct().Take(DeviceTriggersPluginInfo.MaxDevicesPerTrigger).ToList();
        if (UsesJobs(caller, out var jobs))
        {
            // The host's job: owned by the caller, run detached with the caller's devices, ended when the plugin stops.
            var ready = new TaskCompletionSource<Job>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = jobs.Start(caller, "read", async (owned, ct) =>
            {
                var job = await ready.Task.ConfigureAwait(false);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
                await RunAsync(job, owned.Owner, linked.Token).ConfigureAwait(false);
            });
            var read = new Job(started.Id, ids, time.GetUtcNow()) { Task = started.Completion };
            _jobs[read.Id] = read;
            ready.SetResult(read);
            return Status(read);
        }

        var job = new Job(Guid.NewGuid().ToString("N"), ids, time.GetUtcNow());
        _jobs[job.Id] = job;
        job.Task = Task.Run(() => RunAsync(job, caller, stopping), CancellationToken.None);
        return Status(job);
    }

    /// <summary>The progress of a read of the caller; null for an unknown id and for another user's read.</summary>
    public ReadStatusReply? StatusOf(ICallContext caller, string jobId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Expire();
        if ((UsesJobs(caller, out var jobs) && jobs.Find(caller, jobId) is null) || !_jobs.TryGetValue(jobId, out var job))
        {
            return null;
        }

        job.LastUse = time.GetUtcNow();
        return Status(job);
    }

    /// <summary>Waits for every running read (plugin stop).</summary>
    public async Task StopAsync()
    {
        foreach (var job in _jobs.Values)
        {
            try
            {
                await (job.Task ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _jobs.Clear();
    }

    private static ReadStatusReply Status(Job job)
    {
        if (!job.Finished)
        {
            return new ReadStatusReply(job.Id, job.DeviceIds.Count, Volatile.Read(ref job.Done), false, [], []);
        }

        // Every event once (by topic), the values every device lists merged; per device the indexes of its events.
        var events = new List<DeviceEvent>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var devices = new List<DeviceReadResult>();
        foreach (var id in job.DeviceIds)
        {
            var result = job.Results.GetValueOrDefault(id);
            if (result.Error is not null || result.Events is null)
            {
                devices.Add(new DeviceReadResult(id, result.Error ?? "Not read", []));
                continue;
            }

            var mine = new List<int>();
            foreach (var e in result.Events)
            {
                if (index.TryGetValue(e.Topic, out var i))
                {
                    events[i] = MergeValues(events[i], e);
                }
                else
                {
                    i = events.Count;
                    index[e.Topic] = i;
                    events.Add(e);
                }

                mine.Add(i);
            }

            devices.Add(new DeviceReadResult(id, null, mine));
        }

        return new ReadStatusReply(job.Id, job.DeviceIds.Count, job.DeviceIds.Count, true, events, devices);
    }

    private static DeviceEvent MergeValues(DeviceEvent a, DeviceEvent b) => a with
    {
        Sources = [.. a.Sources.Select(item => b.Sources.FirstOrDefault(o => o.Name == item.Name) is { } other ? MergeItem(item, other) : item)],
    };

    private static EventItem MergeItem(EventItem a, EventItem b)
    {
        if (b.Values.All(v => a.Values.Any(x => x.Value == v.Value)))
        {
            return a;
        }

        return a with
        {
            Values = [.. a.Values.Concat(b.Values).DistinctBy(v => v.Value)
                .OrderBy(v => int.TryParse(v.Value, out var n) ? n : int.MaxValue).ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)],
        };
    }

    /// <summary>
    /// Reads go through the host's jobs when it has them and the call is a user's; in-process calls without a user (and
    /// hosts before SDK 0.4.0) keep the plugin's own reads.
    /// </summary>
    private bool UsesJobs(ICallContext caller, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPluginJobs? jobs)
    {
        jobs = caller is ContextCall ? null : ctx.Jobs;
        return jobs is not null;
    }

    private async Task RunAsync(Job job, ICallContext owner, CancellationToken stopping)
    {
        try
        {
            await Parallel.ForEachAsync(job.DeviceIds, new ParallelOptions { MaxDegreeOfParallelism = options.ReadParallelism, CancellationToken = stopping }, async (id, ct) =>
            {
                job.Results[id] = await ReadOneAsync(owner, id, ct).ConfigureAwait(false);
                Interlocked.Increment(ref job.Done);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            job.Finished = true;
        }
    }

    private async Task<(string? Error, IReadOnlyList<DeviceEvent>? Events)> ReadOneAsync(ICallContext owner, Guid id, CancellationToken ct)
    {
        var device = await owner.Devices.FindAsync(id, ct).ConfigureAwait(false);
        if (device is null)
        {
            return (DeviceMessages.Removed, null);
        }

        if (DeviceConnection.RefusalFor(device) is { } refusal)
        {
            return (refusal, null);
        }

        // An empty API list is not read yet (a device just added): try it, like the runtime and the editor do.
        if (device.Apis.Count > 0 && !device.Apis.Supports(DeviceTriggersPluginInfo.ApiId, DeviceTriggersPluginInfo.ApiVersion))
        {
            return (DeviceTriggersPluginInfo.NotSupportedReason, null);
        }

        try
        {
            var vapix = await owner.Vapix.CreateAsync(id, ct).ConfigureAwait(false);
            return (null, await EventDeclarations.ReadAsync(vapix, options.RequestTimeout, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A device that cannot be read is shown with the reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (DeviceConnection.Describe(ex, options.RequestTimeout), null);
        }
    }

    private void Expire()
    {
        var now = time.GetUtcNow();
        foreach (var job in _jobs.Values.Where(j => j.Finished && now - j.LastUse > options.ReadJobLifetime).ToList())
        {
            _jobs.TryRemove(job.Id, out _);
        }
    }

    private sealed class Job(string id, IReadOnlyList<Guid> deviceIds, DateTimeOffset created)
    {
#pragma warning disable SA1401, CA1051 // Interlocked counter.
        public int Done;
#pragma warning restore SA1401, CA1051

        public string Id { get; } = id;

        public IReadOnlyList<Guid> DeviceIds { get; } = deviceIds;

        public ConcurrentDictionary<Guid, (string? Error, IReadOnlyList<DeviceEvent>? Events)> Results { get; } = new();

        public volatile bool Finished;

        public DateTimeOffset LastUse { get; set; } = created;

        public Task? Task { get; set; }
    }
}
