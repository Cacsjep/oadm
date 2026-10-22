namespace Oadm.Sdk.Plugins;

/// <summary>
/// Services core plugins offer each other (<see cref="ICorePluginContext.Services"/>). The contract is an interface of the
/// SDK: plugins load in assembly contexts of their own, so only SDK types are shared between them (e.g.
/// <see cref="Certificates.IDeviceCertificateAuthority"/>). An offer lives while the offering plugin runs: the host withdraws
/// it when the plugin stops or its package is turned off. Thread-safe.
/// </summary>
public interface IPluginServices
{
    /// <summary>
    /// Offers <paramref name="service"/> as <typeparamref name="T"/> (usually from <see cref="ICorePlugin.StartAsync"/>);
    /// replaces this plugin's earlier offer of the same type. Throws <see cref="InvalidOperationException"/> when another
    /// running plugin already offers <typeparamref name="T"/>.
    /// </summary>
    void Offer<T>(T service) where T : class;

    /// <summary>Withdraws this plugin's offer of <typeparamref name="T"/>; nothing happens without one.</summary>
    void Withdraw<T>() where T : class;

    /// <summary>The offer of <typeparamref name="T"/> by a running plugin; null when none. Ask again each time, never keep it.</summary>
    T? Find<T>() where T : class;
}
