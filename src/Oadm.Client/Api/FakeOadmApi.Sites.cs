using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.Sites;
using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode: two sample sites, "Building B" online and "Warehouse" offline, so the client shows Site offline devices; one
/// node waiting to be bound ("site-pc-3", pairing code ABCD-EFGH); Accept site nodes on.
/// </summary>
public sealed partial class FakeOadmApi
{
    /// <summary>The online sample site.</summary>
    public static readonly Guid FakeSiteOnlineId = Guid.Parse("5b1e0c3a-7d42-4f6e-9a10-2c8d4e6f0b11");

    /// <summary>The offline sample site: its devices show Site offline.</summary>
    public static readonly Guid FakeSiteOfflineId = Guid.Parse("9e2d4f60-1a3b-4c5d-8e7f-0a1b2c3d4e22");

    /// <summary>The node id of the sample node that waits to be bound.</summary>
    public const string FakeWaitingNodeId = "node-site-pc-3";

    /// <summary>The pairing code the fake waiting nodes accept.</summary>
    public const string FakePairingCode = "ABCD-EFGH";

    private static readonly Dictionary<Guid, string> FakeSites = new()
    {
        [FakeSiteOnlineId] = "Building B",
        [FakeSiteOfflineId] = "Warehouse",
    };

    private readonly List<Site> _sites = [];
    private readonly List<Site> _waitingNodes = [];
    private readonly Broadcast<SiteList> _siteEvents = new();
    private bool _siteAccept = true;

    /// <summary>The name of a sample site, or null.</summary>
    public static string? FakeSiteName(string? siteId) =>
        Guid.TryParse(siteId, out var id) && FakeSites.TryGetValue(id, out var name) ? name : null;

    /// <summary>"Site offline: Warehouse" for a device of the offline sample site.</summary>
    private static string FakeSiteOfflineText(Device device) => Oadm.Sdk.Sites.SiteTexts.SiteOffline(FakeSiteName(device.SiteId));

    /// <summary>Devices at the sample sites (added after the other sample devices, so their positions stay).</summary>
    private void AddSiteSampleDevices()
    {
        Device atSite = CreateDevice("B8A44F70B001", "192.168.0.20", "AXIS P3265-V", "12.11.77", DeviceStatus.Ok);
        atSite.SiteId = FakeSiteOnlineId.ToString();
        atSite.SiteName = FakeSites[FakeSiteOnlineId];
        _devices.Add(atSite);
        foreach (var (serial, address, model) in new[] { ("B8A44F70C001", "192.168.5.20", "AXIS M3106-L Mk II"), ("B8A44F70C002", "192.168.5.21", "AXIS P1468-LE") })
        {
            Device offline = CreateDevice(serial, address, model, "12.6.94", DeviceStatus.SiteOffline);
            offline.SiteId = FakeSiteOfflineId.ToString();
            offline.SiteName = FakeSites[FakeSiteOfflineId];
            _devices.Add(offline);
        }

        var now = DateTime.UtcNow;
        _sites.Add(new Site
        {
            Id = FakeSiteOnlineId.ToString(), Name = FakeSites[FakeSiteOnlineId], State = SiteState.Online,
            NodeId = "node-building-b", NodeAddress = "10.0.4.12", Version = "3.7.0", RoundTripMs = 12,
            ShortConnections = 3, PermanentConnections = 1, PortsWaiting = 1, LastSeen = Timestamp.FromDateTime(now),
            Bound = Timestamp.FromDateTime(now.AddDays(-12)), NodeFingerprint = new string('A', 64),
        });
        _sites.Add(new Site
        {
            Id = FakeSiteOfflineId.ToString(), Name = FakeSites[FakeSiteOfflineId], State = SiteState.Offline,
            NodeId = "node-warehouse", NodeAddress = "10.20.0.5", Version = "3.6.9", UpdateAvailable = true,
            LastSeen = Timestamp.FromDateTime(now.AddHours(-3)), Bound = Timestamp.FromDateTime(now.AddDays(-40)),
            Message = SiteTexts.NodeNotAnswering, NodeFingerprint = new string('B', 64),
        });
        _waitingNodes.Add(new Site
        {
            Name = "site-pc-3", State = SiteState.Waiting, NodeId = FakeWaitingNodeId,
            NodeAddress = "10.0.7.30", Version = "3.7.0", LastSeen = Timestamp.FromDateTime(now),
        });
    }

    public Task<SiteList> ListSitesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(SiteListLocked());
        }
    }

    public async IAsyncEnumerable<SiteList> WatchSitesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<SiteList> channel;
        SiteList first;
        lock (_gate)
        {
            ThrowIfOffline();
            channel = _siteEvents.Subscribe();
            first = SiteListLocked();
        }

        try
        {
            yield return first;
            await foreach (var list in channel.Reader.ReadAllAsync(ct))
            {
                yield return list;
            }
        }
        finally
        {
            _siteEvents.Unsubscribe(channel);
        }
    }

    public Task<SiteList> SetSiteAcceptAsync(bool accept, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _siteAccept = accept;
            return Task.FromResult(PublishSitesLocked());
        }
    }

    public Task<Site> BindSiteAsync(string nodeId, string nodeFingerprint, string name, string pairingCode, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var node = _waitingNodes.Find(n => n.NodeId == nodeId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, SiteTexts.NodeGone));
            var trimmed = (name ?? "").Trim();
            if (SiteTexts.NameProblem(trimmed) is { } problem)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, problem));
            }

            if (_sites.Exists(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, SiteTexts.NameTaken));
            }

            if (PairingCodeText.Normalize(pairingCode) is null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, SiteTexts.CodeFormat));
            }

            if (PairingCodeText.Normalize(pairingCode) != PairingCodeText.Normalize(FakePairingCode))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, SiteTexts.WrongCode));
            }

            _waitingNodes.Remove(node);
            var site = node.Clone();
            site.Id = Guid.NewGuid().ToString();
            site.Name = trimmed;
            site.State = SiteState.Online;
            site.Bound = Timestamp.FromDateTime(DateTime.UtcNow);
            site.NodeFingerprint = new string('C', 64);
            _sites.Add(site);
            PublishSitesLocked();
            return Task.FromResult(site.Clone());
        }
    }

    public Task<Site> RenameSiteAsync(string id, string name, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var site = FindSiteLocked(id);
            var trimmed = (name ?? "").Trim();
            if (SiteTexts.NameProblem(trimmed) is { } problem)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, problem));
            }

            if (_sites.Exists(s => s != site && string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, SiteTexts.NameTaken));
            }

            site.Name = trimmed;
            foreach (var device in _devices.Where(d => d.SiteId == id))
            {
                device.SiteName = trimmed;
                PublishUpdated(device);
            }

            PublishSitesLocked();
            return Task.FromResult(site.Clone());
        }
    }

    public Task<ReplaceSiteReply> ReplaceSiteAsync(string id, string newSiteId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var site = FindSiteLocked(id);
            var target = FindSiteLocked(newSiteId);
            var reply = new ReplaceSiteReply();
            foreach (var device in _devices.Where(d => d.SiteId == id))
            {
                device.SiteId = target.Id;
                device.SiteName = target.Name;
                device.Status = target.State == SiteState.Online ? DeviceStatus.Ok : DeviceStatus.SiteOffline;
                reply.Moved++;
                PublishUpdated(device);
            }

            if (site.State == SiteState.Removed)
            {
                _sites.Remove(site); // its last device moved
            }

            PublishSitesLocked();
            return Task.FromResult(reply);
        }
    }

    public Task<RemoveSiteReply> RemoveSiteAsync(string id, bool keepDevices, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var site = FindSiteLocked(id);
            var removedAlready = site.State == SiteState.Removed;
            if (removedAlready && keepDevices)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, SiteTexts.RemovedKeepsDevices));
            }

            var devices = _devices.Where(d => d.SiteId == id).ToList();
            var reply = new RemoveSiteReply { NodeAnswered = removedAlready || site.State == SiteState.Online };
            if (keepDevices && devices.Count > 0)
            {
                // Like the server: the site stays listed as Removed until its devices are replaced or removed.
                var name = site.Name;
                site.State = SiteState.Removed;
                site.Message = SiteTexts.RemovedMessage;
                site.NodeAddress = string.Empty;
                site.Version = string.Empty;
                site.UpdateAvailable = false;
                site.RoundTripMs = 0;
                site.PortsWaiting = 0;
                foreach (var device in devices)
                {
                    device.Status = DeviceStatus.SiteOffline;
                    device.SiteName = Oadm.Sdk.Sites.SiteTexts.RemovedSite(name);
                    PublishUpdated(device);
                }
            }
            else
            {
                _sites.Remove(site);
                foreach (var device in devices)
                {
                    _devices.Remove(device);
                    reply.DevicesRemoved++;
                    _deviceEvents.Publish(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Removed, Device = new Device { Id = device.Id } });
                }
            }

            PublishSitesLocked();
            return Task.FromResult(reply);
        }
    }

    private Site FindSiteLocked(string id) =>
        _sites.Find(s => s.Id == id) ?? throw new RpcException(new Status(StatusCode.NotFound, SiteTexts.SiteGone));

    private SiteList SiteListLocked()
    {
        var list = new SiteList { Accept = _siteAccept, ListenAddress = "10.0.0.5:5081" };
        // A user with a device group sees only the sites its rules name, with its own devices there (like the server).
        DeviceGroupRules? rules = ViewRulesLocked();
        foreach (var site in _sites.Where(s => rules is null || rules.SiteIds.Contains(s.Id)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var copy = site.Clone();
            copy.DeviceCount = _devices.Count(d => d.SiteId == site.Id && InViewLocked(d));
            copy.GroupNames.AddRange(GroupsUsingSiteLocked(site.Id));
            list.Sites.Add(copy);
        }

        // Nodes that connected wait only while the server accepts them.
        if (_siteAccept && rules is null)
        {
            list.Sites.Add(_waitingNodes.Select(n => n.Clone()));
        }

        return list;
    }

    private SiteList PublishSitesLocked()
    {
        var list = SiteListLocked();
        _siteEvents.Publish(list);
        return list;
    }
}
