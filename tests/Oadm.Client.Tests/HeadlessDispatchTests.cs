namespace Oadm.Client.Tests;

/// <summary>
/// How <c>HeadlessUnitTestSession.Dispatch</c> treats an async test body. It has overloads for <c>Action</c>,
/// <c>Func&lt;T&gt;</c> and <c>Func&lt;Task&lt;T&gt;&gt;</c>, none for <c>Func&lt;Task&gt;</c>: an async body without a value
/// binds to <c>Func&lt;T&gt;</c> with <c>T = Task</c>, and Dispatch hands back that Task unawaited. The rest of the body
/// after its first await never runs within the test, and a failed assertion anywhere in it (also before the first await)
/// only faults the returned Task. Headless tests therefore end an async body with <c>return true;</c>
/// (guarded by <c>HeadlessDispatchConventionTests</c> in Oadm.ApiDocs.Tests).
/// </summary>
public sealed class HeadlessDispatchTests
{
    [Fact]
    public async Task An_async_body_without_a_value_is_not_awaited()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedEnd = false;

        // The body waits for the gate, which opens only after Dispatch returned: Dispatch did not await the body.
        var started = await HeadlessSession.Shared.Dispatch(async () =>
        {
            await gate.Task;
            reachedEnd = true;
        }, CancellationToken.None);

        Assert.False(reachedEnd);
        Assert.False(started.IsCompleted);
        gate.SetResult();
    }

    [Fact]
    public async Task A_failed_assertion_in_an_async_body_without_a_value_is_lost()
    {
        var started = await HeadlessSession.Shared.Dispatch(async () =>
        {
            Assert.Fail("never reaches the test");
            await Task.Yield();
        }, CancellationToken.None);

        Assert.True(started.IsFaulted);
        _ = started.Exception; // observed
    }

    [Fact]
    public async Task An_async_body_with_a_value_is_awaited_to_its_end()
    {
        var reachedEnd = false;

        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            await Task.Yield();
            await Task.Delay(1, CancellationToken.None);
            reachedEnd = true;
            return true;
        }, CancellationToken.None);

        Assert.True(done);
        Assert.True(reachedEnd);
    }
}
