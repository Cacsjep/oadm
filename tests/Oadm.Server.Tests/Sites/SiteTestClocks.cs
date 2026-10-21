using Microsoft.Extensions.Time.Testing;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// A fake clock whose wall time can jump (a time sync, a user changing the clock) while its timestamps and timers keep
/// going as <see cref="Fake"/> advances: the waiting times of the sites must follow the timestamps only.
/// </summary>
internal sealed class JumpingClock(FakeTimeProvider fake) : TimeProvider
{
    private long _offsetTicks;

    public FakeTimeProvider Fake { get; } = fake;

    /// <summary>Moves the wall clock by <paramref name="by"/> (negative = back) without moving the timestamps.</summary>
    public void Jump(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override long GetTimestamp() => Fake.GetTimestamp();

    public override long TimestampFrequency => Fake.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        Fake.CreateTimer(callback, state, dueTime, period);
}
