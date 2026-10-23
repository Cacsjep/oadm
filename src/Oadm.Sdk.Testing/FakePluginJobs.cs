using System.Collections.Concurrent;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The job registry of a core plugin (<see cref="IPluginJobs"/>, SDK 0.4.0) in memory, with the host's rule: a job belongs
/// to the user who started it, and <see cref="Find"/> answers null for another user's id. The work runs on the thread pool.
/// </summary>
public sealed class FakePluginJobs : IPluginJobs
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    /// <summary>Every job started, any user.</summary>
    public IReadOnlyCollection<IPluginJob> All => [.. _jobs.Values];

    /// <inheritdoc />
    public IPluginJob Start(ICallContext owner, string kind, Func<IPluginJob, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(work);
        var job = new Job(Guid.NewGuid().ToString("N"), kind, owner);
        _jobs[job.Id] = job;
        job.Run(work);
        return job;
    }

    /// <inheritdoc />
    public IPluginJob? Find(ICallContext caller, string jobId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return jobId is not null && _jobs.TryGetValue(jobId, out var job) && string.Equals(job.Owner.UserName, caller.UserName, StringComparison.OrdinalIgnoreCase)
            ? job
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<IPluginJob> List(ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return [.. _jobs.Values.Where(j => string.Equals(j.Owner.UserName, caller.UserName, StringComparison.OrdinalIgnoreCase)).Reverse()];
    }

    private sealed class Job(string id, string kind, ICallContext owner) : IPluginJob, IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();

        public string Id { get; } = id;

        public string Kind { get; } = kind;

        public ICallContext Owner { get; } = owner;

        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Cancel() => _cancel.Cancel();

        public void Dispose() => _cancel.Dispose();

        public void Run(Func<IPluginJob, CancellationToken, Task> work) =>
            Completion = Task.Run(() => work(this, _cancel.Token), CancellationToken.None);
    }
}
