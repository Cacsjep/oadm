using Grpc.Core;

using Oadm.Contracts.SiteNode;

namespace Oadm.Server.Sites;

/// <summary>Relays (docs/sites.md, "Relay"): a node <c>Relay</c> call on a worker link, open as long as the relay runs.</summary>
internal sealed partial class SiteSession
{
    /// <summary>
    /// True when the node offers <paramref name="feature"/> ("relay.udp", "relay.tcp"). A node that sent no feature list
    /// is asked anyway (its answer says).
    /// </summary>
    public bool Supports(string feature) =>
        Control.Hello is not { } hello || hello.Features.Count == 0 || hello.Features.Contains(feature, StringComparer.Ordinal);

    /// <summary>
    /// Opens a <c>Relay</c> call on the least busy worker link (a new one when none is free). The call counts as one open
    /// connection of that worker until it is disposed; it ends with the session.
    /// </summary>
    public async Task<RelayCall> OpenRelayAsync(string siteName, CancellationToken ct)
    {
        var worker = await WorkerAsync(siteName, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        var life = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, ct);
        try
        {
            var call = worker.Link.Client.Relay(cancellationToken: life.Token);
            Interlocked.Increment(ref worker.Open);
            return new RelayCall(call, life, () => Interlocked.Decrement(ref worker.Open));
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // The worker link closed just now: the node is gone.
            life.Dispose();
            throw Lost(siteName);
        }
        catch
        {
            life.Dispose();
            throw;
        }
    }
}

/// <summary>One open <c>Relay</c> call; disposing ends it (the node stops listening).</summary>
internal sealed class RelayCall(AsyncDuplexStreamingCall<RelayMessage, RelayMessage> call, CancellationTokenSource life, Action closed) : IDisposable
{
    private int _disposed;

    public IClientStreamWriter<RelayMessage> Requests => call.RequestStream;

    public IAsyncStreamReader<RelayMessage> Responses => call.ResponseStream;

    public CancellationToken Lifetime => life.Token;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            life.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        call.Dispose();
        life.Dispose();
        closed();
    }
}
