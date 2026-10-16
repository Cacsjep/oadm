using Oadm.Sdk.Events;

namespace Oadm.Core.Plugins;

/// <summary>
/// The event subscriptions of one core plugin run (<c>ICorePluginContext.EventSubscriptions</c>): remembers every
/// subscription the plugin starts and ends them all when the plugin stops, fails to start or is turned off, so a plugin
/// that forgets to dispose leaves nothing running.
/// </summary>
internal sealed class PluginEventSubscriptions(IEventSubscriptions inner) : IEventSubscriptions, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    /// <inheritdoc />
    public IDisposable Subscribe(Action<PublishedEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = inner.Subscribe(handler);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<IDisposable> subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }
}
