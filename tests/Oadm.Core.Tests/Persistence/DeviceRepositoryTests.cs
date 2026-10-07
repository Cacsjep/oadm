using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Persistence;

public sealed class DeviceRepositoryTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private DeviceRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _repo = _db.Get<DeviceRepository>();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static Device NewDevice(string serial = "accc8e-01-02-03", string address = "192.168.0.90") => new()
    {
        Serial = serial,
        Address = address,
        Model = "M3106-L Mk II",
        FirmwareVersion = "11.11.73",
        Status = DeviceStatus.Ok,
        DhcpEnabled = true,
        Tags = ["lobby", "floor-1"],
        LastSeenUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task AddNormalizesSerialAndRoundTripsAllColumns()
    {
        var added = await _repo.AddAsync(NewDevice(), CancellationToken.None);

        Assert.Equal("ACCC8E010203", added.Serial);
        var loaded = await _repo.GetAsync(added.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("192.168.0.90", loaded.Address);
        Assert.Equal(["lobby", "floor-1"], loaded.Tags);
        Assert.True(loaded.DhcpEnabled);
        Assert.Null(loaded.HttpsEnabled);
        Assert.Equal(DeviceStatus.Ok, loaded.Status);
        Assert.Equal(DeviceScheme.Https, loaded.Scheme);
        Assert.Equal(DateTimeKind.Utc, loaded.LastSeenUtc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc), loaded.LastSeenUtc);
    }

    [Fact]
    public async Task DuplicateSerialIsRejectedInAnyNotation()
    {
        await _repo.AddAsync(NewDevice("ACCC8E010203"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuplicateDeviceException>(
            () => _repo.AddAsync(NewDevice("ac:cc:8e:01:02:03", "10.0.0.2"), CancellationToken.None));
        Assert.Equal("ACCC8E010203", ex.Serial);
        Assert.Single(await _repo.ListDevicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpdateToExistingSerialIsRejected()
    {
        await _repo.AddAsync(NewDevice("ACCC8E000001"), CancellationToken.None);
        var second = await _repo.AddAsync(NewDevice("ACCC8E000002"), CancellationToken.None);

        second.Serial = "ACCC8E000001";
        await Assert.ThrowsAsync<DuplicateDeviceException>(() => _repo.UpdateAsync(second, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidSerialIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.AddAsync(NewDevice("not-a-mac"), CancellationToken.None));
    }

    [Fact]
    public async Task FindBySerialUpdateAndRemove()
    {
        var added = await _repo.AddAsync(NewDevice(), CancellationToken.None);

        var found = await _repo.FindBySerialAsync("ac-cc-8e-01-02-03", CancellationToken.None);
        Assert.Equal(added.Id, found!.Id);

        found.FirmwareVersion = "12.0.1";
        found.Tags.Add("new");
        await _repo.UpdateAsync(found, CancellationToken.None);

        var updated = await _repo.UpdateAsync(added.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        Assert.Equal(DeviceStatus.Unreachable, updated!.Status);

        var loaded = await _repo.GetAsync(added.Id, CancellationToken.None);
        Assert.Equal("12.0.1", loaded!.FirmwareVersion);
        Assert.Equal(DeviceStatus.Unreachable, loaded.Status);
        Assert.Contains("new", loaded.Tags);

        Assert.True(await _repo.RemoveAsync(added.Id, CancellationToken.None));
        Assert.False(await _repo.RemoveAsync(added.Id, CancellationToken.None));
        Assert.Null(await _repo.GetAsync(added.Id, CancellationToken.None));
        Assert.Null(await _repo.UpdateAsync(added.Id, d => d.Address = "x", CancellationToken.None));
    }

    [Fact]
    public async Task SdkInterfaceExposesDevices()
    {
        var added = await _repo.AddAsync(NewDevice(), CancellationToken.None);
        IDeviceRepository sdk = _db.Get<IDeviceRepository>();

        var list = await sdk.ListAsync(CancellationToken.None);
        Assert.Equal(added.Id, Assert.Single(list).Id);
        Assert.Equal("ACCC8E010203", (await sdk.FindAsync(added.Id, CancellationToken.None))!.Serial);
        Assert.Null(await sdk.FindAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task RemovingDeviceCascadesCredentials()
    {
        var added = await _repo.AddAsync(NewDevice(), CancellationToken.None);
        var credentials = _db.Get<CredentialStore>();
        await credentials.SetAsync(added.Id, "root", "pass", CancellationToken.None);

        await _repo.RemoveAsync(added.Id, CancellationToken.None);

        Assert.False(await credentials.HasCredentialsAsync(added.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ChangeFeedPublishesAddUpdateRemove()
    {
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();
        var events = new List<DeviceChange>();
        _repo.Changes.Changed += (_, change) => events.Add(change);

        var added = await _repo.AddAsync(NewDevice(), CancellationToken.None);
        await _repo.UpdateAsync(added.Id, d => d.Address = "10.1.1.1", CancellationToken.None);
        await _repo.RemoveAsync(added.Id, CancellationToken.None);

        var received = new List<DeviceChange>();
        while (subscription.Reader.TryRead(out var change))
        {
            received.Add(change);
        }

        Assert.Equal([DeviceChangeKind.Added, DeviceChangeKind.Updated, DeviceChangeKind.Removed], received.Select(c => c.Kind));
        Assert.All(received, c => Assert.Equal(added.Id, c.DeviceId));
        Assert.Equal("10.1.1.1", received[1].Device!.Address);
        Assert.Null(received[2].Device);
        Assert.Equal(received, events);
    }

    [Fact]
    public async Task DisposedSubscriptionCompletesAndStopsReceiving()
    {
        var feed = _db.Get<IDeviceChangeFeed>();
        var subscription = feed.Subscribe();
        subscription.Dispose();

        await _repo.AddAsync(NewDevice(), CancellationToken.None);

        Assert.False(subscription.Reader.TryRead(out _));
        await subscription.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ChangeSnapshotsAreDetached()
    {
        using var subscription = _db.Get<IDeviceChangeFeed>().Subscribe();
        var added = await _repo.AddAsync(NewDevice(), CancellationToken.None);
        added.Address = "mutated by caller";

        Assert.True(subscription.Reader.TryRead(out var change));
        Assert.Equal("192.168.0.90", change.Device!.Address);
    }

    [Fact]
    public async Task ApiListIsStoredAsJsonAndUpdated()
    {
        var added = await _repo.AddAsync(NewDevice(), CancellationToken.None);
        Assert.Empty((await _repo.GetAsync(added.Id, CancellationToken.None))!.Apis);

        await _repo.UpdateAsync(added.Id, d => d.Apis = [new("user-management", "1.2", "User Management", "official"), new("fwmgr", "1.10")], CancellationToken.None);
        var loaded = await _repo.GetAsync(added.Id, CancellationToken.None);
        Assert.Equal([new Oadm.Sdk.Vapix.DeviceApi("user-management", "1.2", "User Management", "official"), new("fwmgr", "1.10")], loaded!.Apis);

        await _repo.UpdateAsync(added.Id, d => d.Apis = [new("fwmgr", "1.11")], CancellationToken.None);
        Assert.Equal("1.11", Assert.Single((await _repo.GetAsync(added.Id, CancellationToken.None))!.Apis).Version);
    }
}
