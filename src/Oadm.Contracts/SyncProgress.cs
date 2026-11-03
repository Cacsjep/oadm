namespace Oadm.Contracts;

/// <summary>
/// An <see cref="IProgress{T}"/> that reports at once on the caller's thread (<see cref="Progress{T}"/> posts to a
/// synchronization context or the thread pool and may reorder the reports).
/// </summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Action<T> _report = report ?? throw new ArgumentNullException(nameof(report));

    public void Report(T value) => _report(value);
}
