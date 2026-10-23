using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.NtpServer.Serving;

/// <summary>
/// What a user limited to a device group sees of the server-wide NTP server (docs/spec/device-scope.md, server-wide
/// services): the requests of the group's devices (by address on their route: the server's network or their site) and the
/// sites of the group's devices; the rest of the state (status, interfaces, upstream) is the same for everyone.
/// </summary>
internal sealed class NtpGroupView
{
    private readonly ViewedSet _view;
    private readonly HashSet<(Guid? Site, string Address)> _addresses;

    private NtpGroupView(ViewedSet view, HashSet<(Guid?, string)> addresses)
    {
        _view = view;
        _addresses = addresses;
    }

    /// <summary>The view of <paramref name="devices"/> (one pass).</summary>
    public static NtpGroupView From(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var addresses = new HashSet<(Guid?, string)>(devices.Count);
        foreach (var device in devices)
        {
            addresses.Add((device.SiteId, device.Address));
        }

        return new NtpGroupView(ViewedSet.From(devices), addresses);
    }

    /// <summary>True when the request came from a device of the group (its address on its route).</summary>
    public bool Covers(RequestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _addresses.Contains((entry.SiteId, entry.Client));
    }

    /// <summary>The requests of the group's devices, newest first.</summary>
    public IReadOnlyList<RequestEntry> Filter(IReadOnlyList<RequestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.Where(Covers)];
    }

    /// <summary>The state with the requests and sites of the group only.</summary>
    public NtpState Filter(NtpState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var choices = state.SiteChoices.Where(s => _view.CoversSite(s.Id)).ToList();
        return state with
        {
            Config = state.Config with { ServeSites = [.. state.Config.ServeSites.Where(id => _view.CoversSite(id))] },
            Requests = Filter(state.Requests),
            Sites = [.. state.Sites.Where(s => _view.CoversSite(s.SiteId))],
            SiteChoices = choices,
            HasSites = choices.Count > 0,
        };
    }
}
