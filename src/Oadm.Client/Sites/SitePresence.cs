using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.V1;

namespace Oadm.Client.Sites;

/// <summary>
/// Whether the server has sites (docs/spec/sites.md, Client: one rule for every Site column): the device grid, the add page and
/// the waiting list show their Site column only while it does. Fed by <see cref="Shell.ServerConnection"/> from the site
/// list (roles with "See sites"); a role without it falls back to the devices (one carries a site). UI thread only.
/// </summary>
public sealed partial class SitePresence : ObservableObject
{
    private bool? _fromServer;
    private bool _fromDevices;

    /// <summary>The server has at least one site (bound, not bound or removed with devices).</summary>
    [ObservableProperty]
    public partial bool HasSites { get; private set; }

    /// <summary>The site list of the server decides (a role with "See sites" while the stream runs); the devices do not.</summary>
    public bool DecidedByServer => _fromServer is not null;

    /// <summary>A list of the server (WatchSites): the waiting nodes are not sites yet.</summary>
    public void Apply(SiteList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _fromServer = list.Sites.Any(s => s.Id.Length > 0);
        Update();
    }

    /// <summary>Without "See sites": whether a device of the device list is at a site.</summary>
    public void SetFromDevices(bool anyAtSite)
    {
        _fromDevices = anyAtSite;
        Update();
    }

    /// <summary>The site list stream ended (disconnected, role changed): the devices decide until it is back.</summary>
    public void Clear()
    {
        _fromServer = null;
        Update();
    }

    private void Update() => HasSites = _fromServer ?? _fromDevices;
}
