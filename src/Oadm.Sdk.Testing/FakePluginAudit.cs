using System.Collections.Concurrent;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The audit log of what a plugin does by itself, in memory (<see cref="IPluginAudit"/>, SDK 0.17.0): <see cref="Entries"/>
/// lists the details written, in order.
/// </summary>
public sealed class FakePluginAudit : IPluginAudit
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>The details written so far.</summary>
    public IReadOnlyList<string> Entries => [.. _entries];

    /// <inheritdoc />
    public Task WriteAsync(string detail, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ct.ThrowIfCancellationRequested();
        _entries.Enqueue(detail);
        return Task.CompletedTask;
    }
}
