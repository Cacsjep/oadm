using System.Collections.Concurrent;

using Oadm.Sdk.Network;

namespace Oadm.Tests.Shared;

/// <summary>Fake host firewall: records every open and close ("open OADM Server (NTP, UDP 123)"), optionally fails.</summary>
internal sealed class RecordingFirewall : IFirewallRules
{
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Thrown by the next calls while set.</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<string> CallList => [.. Calls];

    public Task OpenAsync(FirewallRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Calls.Enqueue("open " + rule.Name);
        return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public Task CloseAsync(FirewallRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Calls.Enqueue("close " + rule.Name);
        return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
