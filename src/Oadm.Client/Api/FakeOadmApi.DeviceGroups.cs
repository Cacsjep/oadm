using System.Globalization;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Device groups in fake mode: "Building B" (site Building B, tag PTZ, 3 devices, except 1), "Building A" (tag Building A,
/// except 1), "Head office" (This server) and "Lobby" (2 devices); the user tech2 has Building B, guard has Lobby. The
/// rules follow the server's: names unique and 1..64 characters, serials of 12 hex digits, a save of an older revision
/// refused, a delete leaves its users without devices.
/// </summary>
public sealed partial class FakeOadmApi
{
    private const int FakeMaxGroupName = 64;
    private const int FakeMaxGroupDescription = 200;

    private readonly List<DeviceGroupInfo> _groups = [];
    private bool _groupsSeeded;

    public Task<DeviceGroupList> ListDeviceGroupsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedGroupsLocked();
            var list = new DeviceGroupList { TotalDevices = _devices.Count };
            list.Groups.AddRange(_groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(GroupLocked));
            return Task.FromResult(list);
        }
    }

    public Task<DeviceGroupInfo> GetDeviceGroupAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedGroupsLocked();
            return Task.FromResult(GroupLocked(FindGroupLocked(id)));
        }
    }

    public Task<DeviceGroupInfo> SaveDeviceGroupAsync(SaveDeviceGroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            SeedGroupsLocked();
            string name = (request.Name ?? "").Trim();
            string description = (request.Description ?? "").Trim();
            if (name.Length == 0)
            {
                throw Invalid("Enter a name.");
            }

            if (name.Length > FakeMaxGroupName)
            {
                throw Invalid($"The name must have at most {FakeMaxGroupName} characters.");
            }

            if (description.Length > FakeMaxGroupDescription)
            {
                throw Invalid($"The description must have at most {FakeMaxGroupDescription} characters.");
            }

            DeviceGroupRules rules = CleanRules(request.Rules ?? new DeviceGroupRules());
            DeviceGroupInfo? group = string.IsNullOrEmpty(request.Id) ? null : FindGroupLocked(request.Id);
            if (_groups.Any(g => g != group && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"A device group named {name} exists already."));
            }

            if (group is not null && request.Revision != 0 && request.Revision != group.Revision)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Another administrator changed this device group meanwhile. Close it and make your changes again."));
            }

            Timestamp now = Timestamp.FromDateTime(DateTime.UtcNow);
            if (group is null)
            {
                if (_groups.Count >= 200)
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "OADM keeps at most 200 device groups."));
                }

                group = new DeviceGroupInfo { Id = Guid.NewGuid().ToString(), Revision = 0, Created = now };
                _groups.Add(group);
                AuditLocked("Created device group", name, "");
            }
            else
            {
                AuditLocked("Changed device group", name, "");
            }

            group.Name = name;
            group.Description = description;
            group.Rules = rules;
            group.Revision++;
            group.Changed = now;
            foreach (UserInfo user in _users.Where(u => u.DeviceGroupId == group.Id))
            {
                user.DeviceGroupName = name;
            }

            return Task.FromResult(GroupLocked(group));
        }
    }

    public Task<DeleteDeviceGroupReply> DeleteDeviceGroupAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedGroupsLocked();
            DeviceGroupInfo group = FindGroupLocked(id);
            var reply = new DeleteDeviceGroupReply();
            foreach (UserInfo user in _users.Where(u => u.DeviceGroupId == group.Id))
            {
                user.DeviceGroupId = "";
                user.DeviceGroupName = "";
                user.AllDevices = user.Role == UserRole.Admin;
                reply.UserNames.Add(user.UserName);
            }

            _groups.Remove(group);
            int turnedOff = 0;
            foreach (ComplianceCheckDetail check in _complianceChecks.Where(c => c.Summary.Owner is { AllDevices: false } o && o.GroupId == group.Id))
            {
                check.Summary.Owner = new DeviceOwner(); // no owner: off until an administrator chooses one
                check.Summary.Enabled = false;
                turnedOff++;
            }

            if (turnedOff > 0)
            {
                reply.Disabled.Add(turnedOff == 1 ? "1 compliance check" : string.Create(CultureInfo.InvariantCulture, $"{turnedOff} compliance checks"));
                RecomputeComplianceLocked();
            }

            AuditLocked("Deleted device group", group.Name, "");
            return Task.FromResult(reply);
        }
    }

    public Task<DeviceGroupPreview> PreviewDeviceGroupAsync(PreviewDeviceGroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            DeviceGroupRules rules = CleanRules(request.Rules ?? new DeviceGroupRules());
            return Task.FromResult(new DeviceGroupPreview { DeviceCount = _devices.Count(d => InGroup(rules, d)), TotalDevices = _devices.Count });
        }
    }

    /// <summary>The caller's devices for Your role (fake mode runs as the administrator: all devices).</summary>
    private MyDevices MyDevicesLocked(UserInfo user)
    {
        if (user.AllDevices || user.Role == UserRole.Admin)
        {
            return new MyDevices { AllDevices = true, DeviceCount = _devices.Count };
        }

        DeviceGroupInfo? group = _groups.Find(g => g.Id == user.DeviceGroupId);
        if (group is null)
        {
            return new MyDevices();
        }

        var mine = new MyDevices
        {
            GroupId = group.Id,
            GroupName = group.Name,
            Summary = SummaryLocked(group.Rules, withExcept: false),
            ThisServer = group.Rules.ThisServer,
            SingleDeviceCount = group.Rules.Serials.Count,
            DeviceCount = _devices.Count(d => InGroup(group.Rules, d)),
        };
        mine.SiteNames.AddRange(group.Rules.SiteIds.Select(FakeSiteName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase));
        mine.TagNames.AddRange(TagNamesLocked(group.Rules));
        return mine;
    }

    /// <summary>Sets the devices of a user from Add user / Edit user (all devices, or a group that exists).</summary>
    private void SetUserDevicesLocked(UserInfo user, bool? allDevices, string? groupId)
    {
        if (allDevices is null && string.IsNullOrEmpty(groupId))
        {
            return;
        }

        if (allDevices == true)
        {
            user.AllDevices = true;
            user.DeviceGroupId = "";
            user.DeviceGroupName = "";
            return;
        }

        if (string.IsNullOrEmpty(groupId))
        {
            throw Invalid("Choose All devices or a device group.");
        }

        SeedGroupsLocked();
        DeviceGroupInfo group = _groups.Find(g => g.Id == groupId)
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "The device group does not exist (any more)."));
        user.AllDevices = user.Role == UserRole.Admin;
        user.DeviceGroupId = group.Id;
        user.DeviceGroupName = group.Name;
    }

    private DeviceGroupInfo FindGroupLocked(string id) =>
        _groups.Find(g => g.Id == id) ?? throw new RpcException(new Status(StatusCode.NotFound, "The device group does not exist (any more)."));

    private DeviceGroupInfo GroupLocked(DeviceGroupInfo group)
    {
        DeviceGroupInfo copy = group.Clone();
        copy.DeviceCount = _devices.Count(d => InGroup(group.Rules, d));
        copy.Summary = SummaryLocked(group.Rules, withExcept: true);
        copy.UserNames.Clear();
        copy.UserNames.AddRange(_users.Where(u => u.DeviceGroupId == group.Id).Select(u => u.UserName).Order(StringComparer.OrdinalIgnoreCase));
        return copy;
    }

    private string SummaryLocked(DeviceGroupRules rules, bool withExcept)
    {
        var parts = new List<string>();
        if (rules.ThisServer)
        {
            parts.Add("This server");
        }

        var sites = rules.SiteIds.Select(FakeSiteName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (sites.Count > 0)
        {
            parts.Add((sites.Count == 1 ? "Site " : "Sites ") + string.Join(", ", sites));
        }

        var tags = TagNamesLocked(rules);
        if (tags.Count > 0)
        {
            parts.Add((tags.Count == 1 ? "tag " : "tags ") + string.Join(", ", tags));
        }

        if (rules.Serials.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{rules.Serials.Count:N0} {(rules.Serials.Count == 1 ? "device" : "devices")}"));
        }

        if (withExcept && rules.ExceptSerials.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"except {rules.ExceptSerials.Count:N0}"));
        }

        return parts.Count == 0 ? "No devices" : string.Join(", ", parts);
    }

    private List<string> TagNamesLocked(DeviceGroupRules rules) =>
        [.. rules.TagIds.Select(id => _tagDefinitions.Find(t => t.Id == id)?.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];

    private bool InGroup(DeviceGroupRules rules, Device device)
    {
        if (rules.ExceptSerials.Contains(device.Serial))
        {
            return false;
        }

        return (rules.ThisServer && string.IsNullOrEmpty(device.SiteId))
            || rules.SiteIds.Contains(device.SiteId)
            || rules.Serials.Contains(device.Serial)
            || rules.TagIds.Any(id => _tagDefinitions.Find(t => t.Id == id) is { } tag && device.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static DeviceGroupRules CleanRules(DeviceGroupRules rules)
    {
        if (rules.SiteIds.Count > 100 || rules.TagIds.Count > 500 || rules.Serials.Count > 30_000 || rules.ExceptSerials.Count > 30_000)
        {
            throw Invalid("A device group can have at most 100 sites, 500 tags and 30,000 single devices.");
        }

        var clean = new DeviceGroupRules { ThisServer = rules.ThisServer };
        clean.SiteIds.AddRange(rules.SiteIds.Where(s => Guid.TryParse(s, out _)).Distinct(StringComparer.OrdinalIgnoreCase));
        clean.TagIds.AddRange(rules.TagIds.Where(t => Guid.TryParse(t, out _)).Distinct(StringComparer.OrdinalIgnoreCase));
        clean.Serials.AddRange(Serials(rules.Serials));
        clean.ExceptSerials.AddRange(Serials(rules.ExceptSerials));
        return clean;

        static IEnumerable<string> Serials(IEnumerable<string> serials)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string serial in serials)
            {
                string hex = new([.. serial.Where(char.IsAsciiHexDigit)]);
                if (hex.Length != 12 || serial.Any(c => !char.IsAsciiHexDigit(c) && c is not ':' and not '-' and not '.'))
                {
                    throw Invalid($"{serial} is not a serial number (12 hex digits).");
                }

                set.Add(hex.ToUpperInvariant());
            }

            return set;
        }
    }

    private static RpcException Invalid(string text) => new(new Status(StatusCode.InvalidArgument, text));

    private void SeedGroupsLocked()
    {
        if (_groupsSeeded)
        {
            return;
        }

        _groupsSeeded = true;
        SeedAuthLocked();
        string? TagId(string name) => FindTagLocked(name)?.Id;
        string Serial(int index) => index < _devices.Count ? _devices[index].Serial : "";
        Timestamp created = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-14));
        DeviceGroupInfo Group(string name, string description, DeviceGroupRules rules) => new()
        {
            Id = Guid.NewGuid().ToString(), Name = name, Description = description, Rules = rules, Revision = 1, Created = created, Changed = created,
        };

        DeviceGroupRules Rules(bool thisServer, IEnumerable<string> sites, IEnumerable<string?> tags, IEnumerable<string> serials, IEnumerable<string> except)
        {
            var rules = new DeviceGroupRules { ThisServer = thisServer };
            rules.SiteIds.AddRange(sites);
            rules.TagIds.AddRange(tags.OfType<string>());
            rules.Serials.AddRange(serials.Where(s => s.Length > 0));
            rules.ExceptSerials.AddRange(except.Where(s => s.Length > 0));
            return rules;
        }

        if (_devices.Count > 0)
        {
            _groups.Add(Group("Building B", "Cameras of Building B for the site team.",
                Rules(false, [FakeSiteOnlineId.ToString()], [TagId("PTZ")], [Serial(5), Serial(8), Serial(9)], [Serial(6)])));
            _groups.Add(Group("Building A", "", Rules(false, [], [TagId("Building A")], [], [Serial(0)])));
            _groups.Add(Group("Head office", "Every device on the server's own network.", Rules(true, [], [], [], [])));
            _groups.Add(Group("Lobby", "", Rules(false, [], [], [Serial(1), Serial(10)], [])));
        }

        foreach (var (user, group) in new[] { ("tech2", "Building B"), ("guard", "Lobby") })
        {
            if (_users.Find(u => u.UserName == user) is { } info && _groups.Find(g => g.Name == group) is { } chosen)
            {
                info.AllDevices = false;
                info.DeviceGroupId = chosen.Id;
                info.DeviceGroupName = chosen.Name;
            }
        }
    }
}
