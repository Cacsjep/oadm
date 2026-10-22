using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// The services core plugins offer each other (<see cref="IPluginServices"/>): one offer per SDK interface, owned by the
/// plugin that offered it. <see cref="CorePluginHost"/> hands each plugin its view (<see cref="For"/>) and withdraws a
/// plugin's offers when it stops (<see cref="RemoveOwner"/>). Thread-safe.
/// </summary>
public sealed class PluginServiceRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, (string Owner, object Service)> _offers = [];
    private readonly List<OwnerServices> _views = [];

    /// <summary>The <see cref="IPluginServices"/> of one core plugin.</summary>
    public IPluginServices For(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var view = new OwnerServices(this, ownerId);
        lock (_gate)
        {
            _views.Add(view);
        }

        return view;
    }

    /// <summary>Withdraws every offer of <paramref name="ownerId"/> (the plugin stopped).</summary>
    public void RemoveOwner(string ownerId)
    {
        lock (_gate)
        {
            foreach (var type in _offers.Where(o => string.Equals(o.Value.Owner, ownerId, StringComparison.OrdinalIgnoreCase)).Select(o => o.Key).ToList())
            {
                _offers.Remove(type);
            }

            // The stopped plugin's views offer nothing any more (a late background task cannot bring a service back).
            foreach (var view in _views.Where(v => string.Equals(v.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                view.Closed = true;
                _views.Remove(view);
            }
        }
    }

    /// <summary>The offer of <typeparamref name="T"/>; null when none.</summary>
    public T? Get<T>()
        where T : class
    {
        lock (_gate)
        {
            return _offers.TryGetValue(typeof(T), out var offer) ? (T)offer.Service : null;
        }
    }

    private void Offer<T>(OwnerServices view, T service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        var ownerId = view.OwnerId;
        lock (_gate)
        {
            if (view.Closed)
            {
                return;
            }

            if (_offers.TryGetValue(typeof(T), out var offer) && !string.Equals(offer.Owner, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The plugin '{offer.Owner}' already offers {typeof(T).Name}.");
            }

            _offers[typeof(T)] = (ownerId, service);
        }
    }

    private void Withdraw<T>(string ownerId)
        where T : class
    {
        lock (_gate)
        {
            if (_offers.TryGetValue(typeof(T), out var offer) && string.Equals(offer.Owner, ownerId, StringComparison.OrdinalIgnoreCase))
            {
                _offers.Remove(typeof(T));
            }
        }
    }

    private sealed class OwnerServices(PluginServiceRegistry registry, string ownerId) : IPluginServices
    {
        public string OwnerId => ownerId;

        /// <summary>The plugin stopped: offers are ignored (set under the registry's lock).</summary>
        public bool Closed { get; set; }

        public void Offer<T>(T service)
            where T : class => registry.Offer(this, service);

        public void Withdraw<T>()
            where T : class => registry.Withdraw<T>(ownerId);

        public T? Find<T>()
            where T : class => registry.Get<T>();
    }
}
