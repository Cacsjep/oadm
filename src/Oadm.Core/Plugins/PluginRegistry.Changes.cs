using Microsoft.Extensions.Logging;

namespace Oadm.Core.Plugins;

/// <summary>
/// Packages added while the server runs (docs/spec/add-ons.md, rule 5): an add-on installed after the start is loaded
/// (<see cref="PluginLoader.LoadPackage"/>) and must look exactly like a package that was there at the start, with its
/// state from the stored choice or its manifest (an add-on: off). There is no removal at run time.
/// </summary>
public sealed partial class PluginRegistry
{
    private readonly HashSet<string> _addedAtRunTime = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;

    /// <summary>Raised after a package was added to the registry while the server runs (<see cref="PackageRegistered"/>).</summary>
    public event EventHandler<PluginPackageAddedEventArgs>? PackageAdded;

    /// <summary>Whether the start's loading is over (<see cref="MarkStarted"/>): a package registered now is added at run time (tests).</summary>
    internal bool IsStarted
    {
        get
        {
            lock (_sync)
            {
                return _started;
            }
        }
    }

    /// <summary>
    /// The server start loaded its packages and every reader of the registry follows its events (server start, after the
    /// core plugins started). From now on <see cref="PackageRegistered"/> raises <see cref="PackageAdded"/>.
    /// </summary>
    public void MarkStarted()
    {
        lock (_sync)
        {
            _started = true;
        }
    }

    /// <summary>
    /// Every plugin of the package <paramref name="packageId"/> is registered (<see cref="PluginLoader.LoadPackage"/> calls it
    /// once per package). After <see cref="MarkStarted"/> it raises <see cref="EnabledChanged"/> (the catalogues of events,
    /// actions and compliance types rebuild, the event rules take the new sources) and then <see cref="PackageAdded"/>
    /// (permissions, licenses and core plugins, see <c>PluginActivation</c>). Nothing happens before the start ends, for a
    /// package without registered plugins, or for a package id reported a second time.
    /// </summary>
    /// <returns>Whether <see cref="PackageAdded"/> was raised.</returns>
    public bool PackageRegistered(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        lock (_sync)
        {
            if (!_started || FindOriginLocked(packageId) is null || !_addedAtRunTime.Add(packageId))
            {
                return false;
            }
        }

        LogPackageAdded(packageId);
        Oadm.Core.Auth.CallerContext.Raise(EnabledChanged, this, EventArgs.Empty);
        Oadm.Core.Auth.CallerContext.Raise(PackageAdded, this, new PluginPackageAddedEventArgs(packageId));
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin package {PackageId} added while the server runs")]
    private partial void LogPackageAdded(string packageId);
}

/// <summary>The package <see cref="PluginRegistry.PackageAdded"/> reports.</summary>
/// <param name="packageId">The plugin.json id of the added package.</param>
public sealed class PluginPackageAddedEventArgs(string packageId) : EventArgs
{
    /// <summary>The plugin.json id of the added package.</summary>
    public string PackageId { get; } = packageId;
}
