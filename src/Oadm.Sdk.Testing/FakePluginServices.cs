using System.Collections.Concurrent;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>The services core plugins offer each other, in memory: a test offers a fake (e.g. a CA) before the plugin starts.</summary>
public sealed class FakePluginServices : IPluginServices
{
    private readonly ConcurrentDictionary<Type, object> _offers = new();

    /// <inheritdoc />
    public void Offer<T>(T service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        _offers[typeof(T)] = service;
    }

    /// <inheritdoc />
    public void Withdraw<T>()
        where T : class => _offers.TryRemove(typeof(T), out _);

    /// <inheritdoc />
    public T? Find<T>()
        where T : class => _offers.TryGetValue(typeof(T), out var service) ? (T)service : null;
}
