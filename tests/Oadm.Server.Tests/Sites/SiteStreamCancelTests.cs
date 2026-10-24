using Grpc.Core;

using Oadm.Core.Sites;
using Oadm.Server.Sites;

namespace Oadm.Server.Tests.Sites;

/// <summary>Our own stop of a stream to a node is a cancellation, never a site failure (no warning with a stack trace).</summary>
public sealed class SiteStreamCancelTests
{
    [Fact]
    public async Task OurOwnCancelOfANodeStreamIsAPlainCancellation()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var stream = new ThrowingStream(new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client.")));

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => SiteManager.Next(stream, Guid.NewGuid(), "Building B", stop.Token));
        Assert.Equal(stop.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task ADroppedLinkWithoutOurCancelStaysASiteProblem()
    {
        var stream = new ThrowingStream(new RpcException(new Status(StatusCode.Unavailable, "gone")));
        await Assert.ThrowsAsync<SiteException>(() => SiteManager.Next(stream, Guid.NewGuid(), "Building B", CancellationToken.None));
    }

    private sealed class ThrowingStream(Exception error) : IAsyncStreamReader<int>
    {
        public int Current => 0;

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(error);
    }
}
