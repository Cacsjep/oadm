using Oadm.Plugins.DhcpServer.Leases;

namespace Oadm.Plugins.DhcpServer.Tests;

public sealed class LeaseStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AddressPool Pool = new(Ip.Of("10.0.0.100"), Ip.Of("10.0.0.109"), new HashSet<uint>());
    private static readonly ulong A = Mac.Of("AC:CC:8E:5F:60:71");
    private static readonly ulong B = Mac.Of("B8:A4:4F:63:13:39");
    private static readonly ulong C = Mac.Of("00:40:8C:12:34:56");

    [Fact]
    public void Addresses_are_handed_out_in_order_and_requested_ones_first()
    {
        var store = new LeaseStore();
        Assert.Equal(Ip.Of("10.0.0.100"), Bind(store, A));
        Assert.Equal(Ip.Of("10.0.0.105"), Bind(store, B, requested: Ip.Of("10.0.0.105")));
        Assert.Equal(Ip.Of("10.0.0.101"), Bind(store, C, requested: Ip.Of("10.0.0.100"))); // taken: the next free one
        var outside = store.ReserveOffer(Mac.Of("00:40:8C:00:00:01"), Ip.Of("10.0.0.50"), Pool, Now, Now.AddMinutes(1), 16, null, out _);
        Assert.Equal(Ip.Of("10.0.0.102"), outside); // a requested address outside the range is not honored
    }

    [Fact]
    public void Static_leases_collide_with_nothing()
    {
        var store = new LeaseStore();
        Bind(store, A); // 10.0.0.100 active
        var inUse = store.SetStatic(B, Ip.Of("10.0.0.100"), null, null, Now);
        Assert.Equal("In use by AC:CC:8E:5F:60:71.", inUse["Address"]);

        Assert.Empty(store.SetStatic(B, Ip.Of("10.0.0.48"), "Lobby", null, Now));
        var reserved = store.SetStatic(C, Ip.Of("10.0.0.48"), null, null, Now);
        Assert.Equal("Already reserved for B8:A4:4F:63:13:39.", reserved["Address"]);

        var twice = store.SetStatic(B, Ip.Of("10.0.0.49"), null, null, Now);
        Assert.Equal("This device already has a static lease (10.0.0.48).", twice["Mac"]);

        // Edit: change address and name of the same lease.
        Assert.Empty(store.SetStatic(B, Ip.Of("10.0.0.49"), "Gate", B, Now));
        var lease = store.Find(B)!;
        Assert.Equal("10.0.0.49", lease.Address);
        Assert.Equal("Gate", lease.Name);
        Assert.Equal(LeaseInfo.Reserved, lease.State);
    }

    [Fact]
    public void An_expired_lease_at_the_address_is_replaced_by_the_static_one()
    {
        var store = new LeaseStore();
        Bind(store, A);
        store.Sweep(Now.AddHours(25));
        Assert.Empty(store.SetStatic(B, Ip.Of("10.0.0.100"), null, null, Now.AddHours(25)));
        Assert.Null(store.Find(A));
    }

    [Fact]
    public void Make_static_keeps_address_and_names_it_by_the_host_name()
    {
        var store = new LeaseStore();
        Bind(store, A, hostName: "axis-accc8e5f6071");
        Assert.True(store.MakeStatic(A));
        var lease = store.Find(A)!;
        Assert.True(lease.IsStatic);
        Assert.Equal("axis-accc8e5f6071", lease.Name);
        Assert.Equal(LeaseInfo.Active, lease.State);
        Assert.False(store.MakeStatic(A));
        Assert.False(store.Forget(A)); // static leases are deleted, not released
        Assert.True(store.DeleteStatic(A));
        Assert.Null(store.Find(A));
    }

    [Fact]
    public void Leases_expire_and_static_ones_fall_back_to_reserved()
    {
        var store = new LeaseStore();
        Bind(store, A);
        store.SetStatic(B, Ip.Of("10.0.0.48"), null, null, Now);
        Assert.True(store.TryBind(B, Ip.Of("10.0.0.48"), Pool, Now, Now.AddHours(24), null, allowNew: false));
        store.Sweep(Now.AddHours(25));
        Assert.Equal(LeaseInfo.Expired, store.Find(A)!.State);
        Assert.Equal(LeaseInfo.Reserved, store.Find(B)!.State);
    }

    [Fact]
    public void Changes_are_versioned_for_the_page_and_offers_stay_hidden()
    {
        var store = new LeaseStore();
        store.ReserveOffer(A, null, Pool, Now, Now.AddMinutes(1), 16, null, out _);
        Assert.Empty(store.Snapshot(out _));
        var hidden = store.TakeChanges()!;
        Assert.Empty(hidden.Changed);
        Assert.True(store.TryBind(A, Ip.Of("10.0.0.100"), Pool, Now, Now.AddHours(24), "cam", allowNew: false));
        var changes = store.TakeChanges()!;
        Assert.Equal("AC:CC:8E:5F:60:71", Assert.Single(changes.Changed).Mac);
        Assert.Equal(store.Version, changes.Version);
        Assert.Null(store.TakeChanges());

        store.Forget(A);
        var removed = store.TakeChanges()!;
        Assert.Equal(["AC:CC:8E:5F:60:71"], removed.Removed);
    }

    [Fact]
    public void Stored_leases_load_back_without_offers()
    {
        var store = new LeaseStore();
        Bind(store, A, hostName: "cam-a");
        store.SetStatic(B, Ip.Of("10.0.0.48"), "Lobby", null, Now);
        store.ReserveOffer(C, null, Pool, Now, Now.AddMinutes(1), 16, null, out _);
        var stored = store.TakeDirty()!;
        Assert.Equal(2, stored.Count);
        Assert.Null(store.TakeDirty());

        var loaded = new LeaseStore();
        Assert.Equal(2, loaded.Load(stored));
        Assert.Equal("cam-a", loaded.Find(A)!.HostName);
        Assert.Equal(LeaseInfo.Active, loaded.Find(A)!.State);
        Assert.Equal("Lobby", loaded.Find(B)!.Name);
        Assert.True(loaded.Find(B)!.IsStatic);
        Assert.Null(loaded.Find(C));
        Assert.Equal(0, loaded.Load([new StoredLease("bad", "10.0.0.1", false, "Bound", null, null, null)]));
    }

    [Fact]
    public void Five_thousand_leases_allocate_fast()
    {
        var pool = new AddressPool(Ip.Of("10.0.0.1"), Ip.Of("10.0.255.254"), new HashSet<uint>());
        var store = new LeaseStore();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5_000; i++)
        {
            var mac = 0x0040_8C00_0000UL + (ulong)i;
            var address = store.ReserveOffer(mac, null, pool, Now, Now.AddMinutes(1), 100_000, null, out _)!.Value;
            Assert.True(store.TryBind(mac, address, pool, Now, Now.AddHours(24), null, allowNew: false));
        }

        Assert.Equal(5_000, store.Snapshot(out _).Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }

    private static uint Bind(LeaseStore store, ulong mac, uint? requested = null, string? hostName = null)
    {
        var address = store.ReserveOffer(mac, requested, Pool, Now, Now.AddMinutes(1), 16, hostName, out _)!.Value;
        Assert.True(store.TryBind(mac, address, Pool, Now, Now.AddHours(24), hostName, allowNew: false));
        return address;
    }
}
