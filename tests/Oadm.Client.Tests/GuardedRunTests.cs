using Grpc.Core;

using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>The guarded server call of pages and dialogs (<see cref="GuardedRun"/>): what went wrong comes back as a text.</summary>
public sealed class GuardedRunTests
{
    [Fact]
    public async Task Work_that_ends_gives_no_text()
    {
        var ran = false;

        var error = await GuardedRun.RunAsync(async () =>
        {
            await Task.Yield();
            ran = true;
        });

        Assert.Null(error);
        Assert.True(ran);
    }

    [Fact]
    public async Task A_refusal_of_the_server_gives_its_reason()
    {
        var error = await GuardedRun.RunAsync(() => throw new RpcException(new Status(StatusCode.FailedPrecondition, "Another check runs.")));

        Assert.Equal("Another check runs.", error);
    }

    [Fact]
    public async Task An_unexpected_exception_gives_its_message()
    {
        var error = await GuardedRun.RunAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("The answer cannot be read.");
        });

        Assert.Equal("The answer cannot be read.", error);
    }

    [Fact]
    public async Task A_cancellation_is_a_failure_with_a_text_unless_asked_otherwise()
    {
        var error = await GuardedRun.RunAsync(() => throw new OperationCanceledException("Stopped."));

        Assert.Equal("Stopped.", error);
    }

    [Fact]
    public async Task An_ignored_cancellation_gives_no_text()
    {
        var error = await GuardedRun.RunAsync(() => throw new OperationCanceledException("Stopped."), GuardedCancel.Ignore);

        Assert.Null(error);
    }

    [Fact]
    public async Task With_a_token_only_its_cancellation_is_ignored()
    {
        using var mine = new CancellationTokenSource();

        var foreign = await GuardedRun.RunAsync(() => throw new OperationCanceledException("Timed out."), GuardedCancel.Ignore, mine.Token);
        await mine.CancelAsync();
        var own = await GuardedRun.RunAsync(() => throw new OperationCanceledException("Stopped."), GuardedCancel.Ignore, mine.Token);

        Assert.Equal("Timed out.", foreign);
        Assert.Null(own);
    }

    [Fact]
    public async Task A_thrown_cancellation_leaves_the_run()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => GuardedRun.RunAsync(() => throw new OperationCanceledException("Stopped."), GuardedCancel.Throw));
    }

    [Fact]
    public async Task With_a_token_only_its_cancellation_is_thrown()
    {
        using var mine = new CancellationTokenSource();

        var foreign = await GuardedRun.RunAsync(() => throw new OperationCanceledException("Timed out."), GuardedCancel.Throw, mine.Token);
        await mine.CancelAsync();

        Assert.Equal("Timed out.", foreign);
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => GuardedRun.RunAsync(() => Task.Delay(Timeout.Infinite, mine.Token), GuardedCancel.Throw, mine.Token));
    }

    // The host's channel throws an RpcException with the status Cancelled when the token of a call is cancelled, not
    // an OperationCanceledException.
    [Fact]
    public async Task A_cancelled_server_call_is_a_failure_with_a_text_unless_asked_otherwise()
    {
        var error = await GuardedRun.RunAsync(() => throw CancelledCall());

        Assert.Equal("Call canceled by the client.", error);
    }

    [Fact]
    public async Task An_ignored_cancelled_server_call_gives_no_text()
    {
        using var mine = new CancellationTokenSource();

        var withoutToken = await GuardedRun.RunAsync(() => throw CancelledCall(), GuardedCancel.Ignore);
        var foreign = await GuardedRun.RunAsync(() => throw CancelledCall(), GuardedCancel.Ignore, mine.Token);
        await mine.CancelAsync();
        var own = await GuardedRun.RunAsync(() => throw CancelledCall(), GuardedCancel.Ignore, mine.Token);

        Assert.Null(withoutToken);
        Assert.Equal("Call canceled by the client.", foreign);
        Assert.Null(own);
    }

    [Fact]
    public async Task A_thrown_cancelled_server_call_leaves_the_run_as_a_cancellation()
    {
        using var mine = new CancellationTokenSource();

        var withoutToken = await Assert.ThrowsAsync<OperationCanceledException>(
            () => GuardedRun.RunAsync(() => throw CancelledCall(), GuardedCancel.Throw));
        var foreign = await GuardedRun.RunAsync(() => throw CancelledCall(), GuardedCancel.Throw, mine.Token);
        await mine.CancelAsync();
        var own = await Assert.ThrowsAsync<OperationCanceledException>(
            () => GuardedRun.RunAsync(() => throw CancelledCall(), GuardedCancel.Throw, mine.Token));

        Assert.IsType<RpcException>(withoutToken.InnerException);
        Assert.Equal("Call canceled by the client.", foreign);
        Assert.Equal(mine.Token, own.CancellationToken);
    }

    [Fact]
    public async Task Whatever_fails_while_the_token_is_cancelled_counts_as_its_cancellation()
    {
        using var mine = new CancellationTokenSource();
        await mine.CancelAsync();

        var error = await GuardedRun.RunAsync(() => throw new IOException("The connection was closed."), GuardedCancel.Ignore, mine.Token);

        Assert.Null(error);
    }

    [Fact]
    public async Task Other_failures_are_texts_also_when_cancellations_are_thrown()
    {
        var error = await GuardedRun.RunAsync(() => throw new RpcException(new Status(StatusCode.PermissionDenied, "Not allowed.")), GuardedCancel.Throw);

        Assert.Equal("Not allowed.", error);
    }

    private static RpcException CancelledCall() => new(new Status(StatusCode.Cancelled, "Call canceled by the client."));
}
