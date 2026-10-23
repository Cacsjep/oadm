using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>A migrated database with the device group services, sites that a test adds and removes, and helpers.</summary>
public abstract class DeviceGroupTestBase : IAsyncLifetime
{
    protected TestDatabase Db { get; private set; } = null!;

    protected TestSites Sites { get; } = new();

    protected DeviceGroupStore Groups => Db.Get<DeviceGroupStore>();

    protected DeviceViewFactory Views => Db.Get<DeviceViewFactory>();

    protected DeviceRepository Devices => Db.Get<DeviceRepository>();

    protected DeviceTagStore Tags => Db.Get<DeviceTagStore>();

    protected UserStore Users => Db.Get<UserStore>();

    public async Task InitializeAsync()
    {
        Db = await TestDatabase.CreateAsync(configure: s =>
        {
            s.AddSingleton<ISiteStates>(Sites);
            s.AddSingleton(new PasswordHasher(1_000));
        });
    }

    public async Task DisposeAsync() => await Db.DisposeAsync();

    protected static CancellationToken Ct => CancellationToken.None;

    protected async Task<Device> AddDeviceAsync(string serial, Guid? siteId = null, string[]? tags = null, bool addedByAllDevices = true, Guid? addedByGroupId = null)
    {
        var device = await Devices.AddAsync(new Device
        {
            Serial = serial,
            Address = "10.0.0." + serial[^2..],
            SiteId = siteId,
            Status = DeviceStatus.Ok,
            AddedByAllDevices = addedByAllDevices,
            AddedByGroupId = addedByGroupId,
        }, Ct);
        if (tags is { Length: > 0 })
        {
            await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([device.Id], Ct), tags, [], Ct);
        }

        return (await Devices.GetAsync(device.Id, Ct))!;
    }

    protected async Task<Guid> TagIdAsync(string name) =>
        (await Tags.ListDefinitionsAsync(Ct)).Single(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).Id;

    protected Task<DeviceGroupSaveResult> SaveGroupAsync(string name, bool thisServer = false, Guid[]? sites = null, Guid[]? tags = null, string[]? serials = null, string[]? except = null, Guid? id = null, long revision = 0) =>
        Groups.SaveAsync(id, name, "", new DeviceGroupRules(thisServer, sites ?? [], tags ?? [], serials ?? [], except ?? []), revision, Ct);

    protected AuthenticatedCaller CallerOf(UserRecord user, Guid? roleId = null) =>
        new(user.Id, user.UserName, roleId ?? user.RoleId, PermissionSet.All, "local", "testpc", "hash-" + user.Id)
        {
            Devices = new CallerDevices(user.DeviceAccess, Groups.RevisionOf(user.DeviceAccess.EffectiveGroupId)),
        };

    /// <summary>Sites that exist while a test says so; <see cref="Remove"/> raises Changed like the site manager.</summary>
    protected sealed class TestSites : ISiteStates
    {
        private readonly Dictionary<Guid, string> _names = [];

        public event EventHandler<SiteStatesChangedEventArgs>? Changed;

        public Guid Add(string name)
        {
            var id = Guid.NewGuid();
            lock (_names)
            {
                _names[id] = name;
            }

            Changed?.Invoke(this, new SiteStatesChangedEventArgs([id]));
            return id;
        }

        public void Remove(Guid id)
        {
            lock (_names)
            {
                _names.Remove(id);
            }

            Changed?.Invoke(this, new SiteStatesChangedEventArgs([id]));
        }

        public string? NameOf(Guid siteId)
        {
            lock (_names)
            {
                return _names.GetValueOrDefault(siteId);
            }
        }

        public bool IsOnline(Guid siteId) => NameOf(siteId) is not null;

        public TimeSpan RoundTrip(Guid siteId) => TimeSpan.Zero;
    }
}
