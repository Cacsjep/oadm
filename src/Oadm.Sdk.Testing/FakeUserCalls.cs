using System.Collections.Concurrent;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The current calls of stored users in memory (<see cref="IUserCalls"/>, SDK 0.16.0): a test sets the call of each user name
/// (names match case-insensitively, like OADM's), and removes it to play a deleted or disabled account. Set it as
/// <see cref="FakeCoreContext.UserCalls"/>. Thread safe.
/// </summary>
public sealed class FakeUserCalls : IUserCalls
{
    private readonly ConcurrentDictionary<string, ICallContext> _calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _asked = new();

    /// <summary>Every user name the plugin asked for, in order.</summary>
    public IReadOnlyList<string> Asked => [.. _asked];

    /// <summary>The user's call from now on (for example <see cref="FakeCallContext.AllDevices"/> or <see cref="FakeCallContext.WithGroup"/>); replaces an earlier one.</summary>
    public FakeUserCalls Set(string userName, ICallContext call)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(call);
        _calls[userName] = call;
        return this;
    }

    /// <summary>The account is gone or disabled: <see cref="ForUserAsync"/> answers null from now on.</summary>
    public void Remove(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        _calls.TryRemove(userName, out _);
    }

    /// <inheritdoc />
    public Task<ICallContext?> ForUserAsync(string userName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _asked.Enqueue(userName ?? string.Empty);
        return Task.FromResult(userName is not null && _calls.TryGetValue(userName, out var call) ? call : null);
    }
}
