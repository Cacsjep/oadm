using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Auth;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// The jobs of the core plugins (<see cref="ICorePluginContext.Jobs"/>, docs/spec/device-scope.md, Plugins): work a method
/// starts for its user (a report, a scan, an export, a monitor). A job belongs to the user who started it and the session
/// it was started in; <see cref="IPluginJobs.Find"/> answers null for another user's or session's id, exactly like for an
/// unknown id. A shared job (<see cref="IPluginJobs.StartShared"/>, one scan per scope) belongs to every user with the
/// owner's device scope: they all find, follow and cancel it. A job started in a call whose user's access, session or
/// group changed after the call began is refused (<see cref="AccessEpochs"/>), so no job keeps the old access. Jobs run detached (no caller: the background boundary) with the owner's call as their device access, and
/// with the owner's plugin call mark, so a job of a user with a device group refuses <c>ctx.Devices</c> like the call.
/// A job ends when its work ends, when it is cancelled, when its plugin stops and when its owner's devices, role or
/// sessions change (<see cref="CancelOwner"/>, <see cref="CancelSession"/>, <see cref="CancelGroup"/>: such a job is also
/// forgotten, so a finished result made for the old devices is no longer found). A shared job belongs to its scope, not to
/// the user who started it: the starter's logout or new role leaves it running for the scope's other users; it ends only
/// when the scope's rules change (<see cref="CancelGroup"/>), when it is cancelled or its plugin stops (lead decision
/// 2026-10-23). When devices move into or out of a group (a tag, site or
/// membership change: <see cref="ScopeDevicesChanged"/>) the finished jobs of its users are forgotten and the running ones
/// when they end. <see cref="IPluginJobs.Find"/> answers only a caller with the job's device scope. Each user keeps at most <see cref="MaxJobsPerUser"/> jobs per plugin.
/// </summary>
public sealed partial class PluginJobRegistry
{
    /// <summary>Jobs one user keeps per plugin (finished ones are dropped first, oldest first).</summary>
    public const int MaxJobsPerUser = 50;

    /// <summary>The refusal when a user's jobs of one plugin are all still running.</summary>
    public const string TooManyJobs = "Wait until your earlier work has finished.";

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Job>> _byPlugin = new(StringComparer.OrdinalIgnoreCase);

    // Plugins stopped or turned off (until they start again): a call still running then starts no job that would survive.
    private readonly ConcurrentDictionary<string, byte> _stopped = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private long _sequence;

    public PluginJobRegistry(ILogger<PluginJobRegistry>? logger = null, AccessEpochs? epochs = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Epochs = epochs ?? new AccessEpochs();
    }

    /// <summary>The access changes the registry records and checks a new job against (the auth interceptor captures them).</summary>
    public AccessEpochs Epochs { get; }

    /// <summary>The registry a plugin gets as <see cref="ICorePluginContext.Jobs"/>.</summary>
    public IPluginJobs For(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return new PluginJobs(this, pluginId);
    }

    /// <summary>The jobs of a plugin that still run (tests, diagnostics).</summary>
    public int RunningCount(string pluginId) => Jobs(pluginId).Values.Count(j => !j.Completion.IsCompleted);

    /// <summary>
    /// Cancels and forgets every job of a plugin (it stopped or was turned off); until <see cref="Resume"/> the plugin starts
    /// no job (a call that still runs is refused).
    /// </summary>
    public void CancelPlugin(string pluginId)
    {
        _stopped[pluginId] = 0; // first: a job registered meanwhile is either refused or found below
        if (_byPlugin.TryRemove(pluginId, out var jobs))
        {
            lock (jobs)
            {
                foreach (var job in jobs.Values)
                {
                    job.Cancel();
                }
            }
        }
    }

    /// <summary>The plugin starts (again): it may start jobs.</summary>
    public void Resume(string pluginId) => _stopped.TryRemove(pluginId, out _);

    /// <summary>The refusal of a job of a plugin that stopped or was turned off.</summary>
    public const string PluginStopped = "The plugin was turned off.";

    /// <summary>
    /// Cancels and forgets every job of a user (other devices, another role, the user was disabled or deleted): their ids
    /// answer like unknown ids from now on, also those of finished jobs (a report made for the old devices).
    /// </summary>
    public void CancelOwner(Guid userId)
    {
        Epochs.UserChanged(userId); // first: a job registered meanwhile is either refused or seen by Forget
        Forget(j => !j.Shared && j.UserId == userId);
    }

    /// <summary>
    /// Cancels and forgets every job whose owner has the devices of a group (the group's rules changed or it was deleted).
    /// </summary>
    public void CancelGroup(Guid groupId)
    {
        var key = groupId.ToString();
        Epochs.ScopeChanged(key);
        Forget(j => string.Equals(j.Owner.DeviceScopeKey, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Devices moved into or out of the group with <paramref name="scopeKey"/> (<see cref="HostDeviceScopes.Changed"/>, the
    /// coalesced membership signal): the finished jobs of its users are forgotten at once (a report or a read that may name a
    /// device that left), the running ones keep running (their reads go through the owner's view now) and are forgotten
    /// when they end. O(jobs); jobs of users with all devices are never touched.
    /// </summary>
    public void ScopeDevicesChanged(string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || string.Equals(scopeKey, ICallContext.AllDevicesScopeKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var jobs in _byPlugin.Values)
        {
            foreach (var job in jobs.Values.Where(j => string.Equals(j.Owner.DeviceScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                job.MarkStale();
                if (job.Completion.IsCompleted)
                {
                    jobs.TryRemove(job.Id, out _);
                }
            }
        }
    }

    private void Forget(Func<Job, bool> which)
    {
        foreach (var jobs in _byPlugin.Values)
        {
            foreach (var job in jobs.Values.Where(which).ToList())
            {
                if (jobs.TryRemove(job.Id, out _))
                {
                    job.Cancel();
                }
            }
        }
    }

    /// <summary>Cancels and forgets every job started in a session (the session ended), finished ones too; shared jobs stay.</summary>
    public void CancelSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Epochs.SessionEnded(sessionId);
        Forget(j => !j.Shared && string.Equals(j.Owner.SessionId, sessionId, StringComparison.Ordinal));
    }

    private ConcurrentDictionary<string, Job> Jobs(string pluginId) =>
        _byPlugin.GetOrAdd(pluginId, _ => new ConcurrentDictionary<string, Job>(StringComparer.Ordinal));

    private Job Start(string pluginId, ICallContext owner, string kind, Func<IPluginJob, CancellationToken, Task> work, bool shared)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(work);

        // Only a call the host made has an owner the host can check later (fail closed for anything else).
        if (owner is not HostCallContext call)
        {
            throw new ArgumentException("A job is started with the call the host passed to the method.", nameof(owner));
        }

        var jobs = Jobs(pluginId);
        lock (jobs)
        {
            var mine = jobs.Values.Where(j => j.UserId == call.UserId).OrderBy(j => j.Sequence).ToList();
            if (mine.Count >= MaxJobsPerUser)
            {
                var finished = mine.FirstOrDefault(j => j.Completion.IsCompleted) ?? throw new InvalidOperationException(TooManyJobs);
                jobs.TryRemove(finished.Id, out _);
            }

            var mark = CallerContext.PluginCall;
            var job = new Job(NewId(), kind, call, Interlocked.Increment(ref _sequence), shared, ex => LogCancelFailed(ex, pluginId, kind));
            jobs[job.Id] = job;
            if (_stopped.ContainsKey(pluginId))
            {
                // Registered first, then checked: a stop either came before (refused here) or comes after and cancels it.
                jobs.TryRemove(job.Id, out _);
                job.Dispose();
                throw new InvalidOperationException(PluginStopped);
            }

            // Registered first, then checked: an access change either came before (refused here) or comes after and
            // finds the job (CancelOwner, CancelGroup, CancelSession record the change before they look).
            if (Epochs.ChangedSince(call.Caller.Caller.AccessEpoch, call.UserId, call.SessionId, call.DeviceScopeKey))
            {
                jobs.TryRemove(job.Id, out _);
                job.Dispose();
                throw new DeviceAccessRefusedException(DeviceGroups.DeviceGroupTexts.DevicesChanged);
            }
            var jobMark = new PluginCallMark(pluginId, "job:" + kind, call.HasAllDevices, mark is { SystemAllowed: true } && string.Equals(mark.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                ScopeKey = call.DeviceScopeKey,
            };

            // The background boundary: the job never carries the caller of the call that started it, only its own mark.
            using (CallerContext.Suppress())
            using (CallerContext.UsePluginCall(jobMark))
            {
                job.Run(work, ex => LogJobFailed(ex, pluginId, kind));
            }

            return job;
        }
    }

    private Job? Find(string pluginId, ICallContext caller, string? jobId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return jobId is not null && caller is HostCallContext call
            && _byPlugin.TryGetValue(pluginId, out var jobs) && jobs.TryGetValue(jobId, out var job) && job.BelongsTo(call)
            && !ForgetIfStale(jobs, job)
            ? job
            : null;
    }

    /// <summary>A job that ended after its group's devices moved is forgotten when next asked for.</summary>
    private static bool ForgetIfStale(ConcurrentDictionary<string, Job> jobs, Job job)
    {
        if (!job.IsStale || !job.Completion.IsCompleted)
        {
            return false;
        }

        jobs.TryRemove(job.Id, out _);
        return true;
    }

    private IReadOnlyList<IPluginJob> List(string pluginId, ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller is HostCallContext call && _byPlugin.TryGetValue(pluginId, out var jobs)
            ? [.. jobs.Values.Where(j => j.BelongsTo(call) && !ForgetIfStale(jobs, j)).OrderByDescending(j => j.Sequence)]
            : [];
    }

    /// <summary>128 random bits, URL safe: never guessable.</summary>
    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Kind} of core plugin {PluginId} failed")]
    private partial void LogJobFailed(Exception ex, string pluginId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cancelling job {Kind} of core plugin {PluginId}: a callback of the plugin failed")]
    private partial void LogCancelFailed(Exception ex, string pluginId, string kind);

    private sealed class PluginJobs(PluginJobRegistry registry, string pluginId) : IPluginJobs
    {
        public IPluginJob Start(ICallContext owner, string kind, Func<IPluginJob, CancellationToken, Task> work) =>
            registry.Start(pluginId, owner, kind, work, shared: false);

        public IPluginJob StartShared(ICallContext owner, string kind, Func<IPluginJob, CancellationToken, Task> work) =>
            registry.Start(pluginId, owner, kind, work, shared: true);

        public IPluginJob? Find(ICallContext caller, string jobId) => registry.Find(pluginId, caller, jobId);

        public IReadOnlyList<IPluginJob> List(ICallContext caller) => registry.List(pluginId, caller);
    }

    private sealed class Job(string id, string kind, HostCallContext owner, long sequence, bool shared, Action<Exception> cancelFailed) : IPluginJob, IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();

        /// <summary>Started with <see cref="IPluginJobs.StartShared"/>: it belongs to the owner's scope, not to the owner.</summary>
        public bool Shared => shared;

        public string Id { get; } = id;

        public string Kind { get; } = kind;

        public ICallContext Owner => owner;

        public Guid UserId => owner.UserId;

        /// <summary>
        /// The caller has the devices the job was started with (the same scope) and is the job's user in the job's session;
        /// for a shared job any user of the scope.
        /// </summary>
        public bool BelongsTo(HostCallContext call) =>
            string.Equals(call.DeviceScopeKey, owner.DeviceScopeKey, StringComparison.OrdinalIgnoreCase)
            && (shared || (call.UserId == owner.UserId && string.Equals(call.SessionId, owner.SessionId, StringComparison.Ordinal)));

        public long Sequence { get; } = sequence;

        private int _stale;

        /// <summary>The devices of the job's group moved while it ran: its result is not shown once it ended.</summary>
        public bool IsStale => Volatile.Read(ref _stale) != 0;

        public void MarkStale() => Volatile.Write(ref _stale, 1);

        public Task Completion { get; private set; } = Task.CompletedTask;

        /// <summary>
        /// Cancels the work. Never throws: the callers are the host's event handlers (a logout, a role change), and a
        /// callback the plugin registered on the token that throws (an <see cref="AggregateException"/>, also one of a
        /// source the plugin disposed) only gets logged.
        /// </summary>
        public void Cancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ended meanwhile.
            }
            catch (AggregateException ex)
            {
                cancelFailed(ex);
            }
        }

        public void Dispose() => _cancel.Dispose();

        public void Run(Func<IPluginJob, CancellationToken, Task> work, Action<Exception> failed)
        {
            var token = _cancel.Token;
            Completion = Task.Run(
                async () =>
                {
                    try
                    {
                        await work(this, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Cancelled: an end like any other.
                    }
#pragma warning disable CA1031 // A failing job ends the job, never the plugin or the host.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        failed(ex);
                    }
                    finally
                    {
                        Dispose();
                    }
                },
                CancellationToken.None);
        }
    }
}
