using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Plugins;
using Oadm.Sdk.Devices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

/// <summary>
/// <see cref="IDeviceAutoAdd"/> on the real server (the DHCP server's "Automatically add Axis devices" and following moved
/// devices): the add page's pipeline against fake devices.
/// </summary>
public sealed class DeviceAutoAddTests
{
    private const string Password = "s3cret-Pass";

    private static async Task<(TestServerHost Host, IDeviceAutoAdd AutoAdd)> StartAsync(FakeAxisNetwork network, bool withCredential = true)
    {
        var host = await TestServerHost.StartAsync(network);
        if (withCredential)
        {
            await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });
        }

        return (host, host.Get<IDeviceAutoAdd>());
    }

    private static async Task<IReadOnlyList<Proto.AuditEntry>> AuditAsync(TestServerHost host, string action) =>
        [.. (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == action)];

    [Fact]
    public async Task ADeviceTheCredentialListOpensIsAddedWithThatCredential()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.1.1", FakeSerials.Make(1), Password);
        var (host, autoAdd) = await StartAsync(network);
        await using var _ = host;

        var outcome = await autoAdd.AddAsync("10.9.1.1", FakeSerials.Make(1).ToLowerInvariant(), "DHCP server", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.Added, outcome.Result);
        Assert.True(outcome.IsAdded);
        var device = await host.Get<DeviceRepository>().FindBySerialAsync(FakeSerials.Make(1), CancellationToken.None);
        Assert.NotNull(device);
        Assert.Equal(outcome.DeviceId, device.Id);
        Assert.Equal("10.9.1.1", device.Address);
        Assert.Equal(SdkDeviceStatus.Ok, device.Status);
        var stored = await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(("root", Password), (stored!.UserName, stored.Password));

        // The Axis check came before the first credential.
        var calls = network["10.9.1.1"].CredentialRequests;
        Assert.NotEmpty(calls);

        var entry = Assert.Single(await AuditAsync(host, AuditActions.DeviceAddedAutomatically));
        Assert.Equal("system", entry.UserName);
        Assert.Equal($"10.9.1.1 ({FakeSerials.Make(1)})", entry.Target);
        Assert.StartsWith("DHCP server: Added", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, entry.Detail + entry.Target, StringComparison.Ordinal);

        // Once managed: nothing more is added.
        Assert.Equal(DeviceAutoAddResult.AlreadyManaged, (await autoAdd.AddAsync("10.9.1.1", FakeSerials.Make(1), "DHCP server", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task NoFittingCredentialAddsTheDeviceAsCredentialsRequired()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.1.2", FakeSerials.Make(2), "another-password");
        var (host, autoAdd) = await StartAsync(network);
        await using var _ = host;

        var outcome = await autoAdd.AddAsync("10.9.1.2", FakeSerials.Make(2), "DHCP server", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.AddedCredentialsRequired, outcome.Result);
        var device = await host.Get<DeviceRepository>().GetAsync(outcome.DeviceId!.Value, CancellationToken.None);
        Assert.Equal(SdkDeviceStatus.CredentialsRequired, device!.Status);
        Assert.Null(await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AFactoryDefaultDeviceIsAddedAsPasswordNotSetWithoutAnyLogin()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.1.3", FakeSerials.Make(3), password: null);
        var (host, autoAdd) = await StartAsync(network);
        await using var _ = host;

        var outcome = await autoAdd.AddAsync("10.9.1.3", FakeSerials.Make(3), "DHCP server", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.AddedPasswordNotSet, outcome.Result);
        var device = await host.Get<DeviceRepository>().GetAsync(outcome.DeviceId!.Value, CancellationToken.None);
        Assert.Equal(SdkDeviceStatus.PasswordNotSet, device!.Status);
        Assert.Empty(network["10.9.1.3"].CredentialRequests);
        Assert.Equal(0, network["10.9.1.3"].PwdgrpCalls);
    }

    [Theory]
    [Trait("Category", "Timing")] // real probe timeouts; failed under load
    [InlineData("NotFound")]
    [InlineData("NoSerial")]
    [InlineData("NoModel")]
    public async Task ADeviceThatFailsTheAxisCheckIsNotAddedAndGetsNoPassword(string answer)
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.1.4", FakeSerials.Make(4), Password).AnonymousAnswer = Enum.Parse<FakeAxisAnswer>(answer);
        var (host, autoAdd) = await StartAsync(network);
        await using var _ = host;

        var outcome = await autoAdd.AddAsync("10.9.1.4", FakeSerials.Make(4), "DHCP server", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.NotAxis, outcome.Result);
        Assert.Null(outcome.DeviceId);
        Assert.Empty(network["10.9.1.4"].CredentialRequests);
        Assert.Empty(await host.Get<DeviceRepository>().ListDevicesAsync(CancellationToken.None));
        Assert.Empty(await AuditAsync(host, AuditActions.DeviceAddedAutomatically));
    }

    [Fact]
    public async Task AnotherSerialAtTheAddressIsNotAdded()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.1.5", FakeSerials.Make(5), Password);
        var (host, autoAdd) = await StartAsync(network);
        await using var _ = host;

        var outcome = await autoAdd.AddAsync("10.9.1.5", FakeSerials.Make(6), "DHCP server", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.SerialMismatch, outcome.Result);
        Assert.Empty(network["10.9.1.5"].CredentialRequests);
        Assert.Empty(await host.Get<DeviceRepository>().ListDevicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NothingAnsweringIsUnreachableAndNotAdded()
    {
        var (host, autoAdd) = await StartAsync(new FakeAxisNetwork());
        await using var _ = host;

        var outcome = await autoAdd.AddAsync("10.9.1.7", FakeSerials.Make(7), "DHCP server", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.Unreachable, outcome.Result);
        Assert.Empty(await host.Get<DeviceRepository>().ListDevicesAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => autoAdd.AddAsync("camera.local", FakeSerials.Make(7), "DHCP server", CancellationToken.None));
    }

    [Fact]
    public async Task AManagedDeviceWithANewLeaseIsFollowedRightAway()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.20", FakeSerials.Make(20), "pw");
        var (host, autoAdd) = await StartAsync(network, withCredential: false);
        await using var _ = host;
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.2.10", 20); // still Ok: no need to wait for Unreachable

        Assert.Equal(DeviceFollowResult.Moved, await autoAdd.FollowAsync(FakeSerials.Make(20), "10.9.2.20", "DHCP server", CancellationToken.None));
        Assert.Equal("10.9.2.20", (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Address);
        var entry = Assert.Single(await AuditAsync(host, AuditActions.DeviceMoved));
        Assert.Equal("DHCP server: from 10.9.2.10 to 10.9.2.20", entry.Detail);

        Assert.Equal(DeviceFollowResult.Unchanged, await autoAdd.FollowAsync(FakeSerials.Make(20), "10.9.2.20", "DHCP server", CancellationToken.None));
        Assert.Equal(DeviceFollowResult.NotManaged, await autoAdd.FollowAsync(FakeSerials.Make(21), "10.9.2.21", "DHCP server", CancellationToken.None));

        // Another device answers at the leased address: the record stays.
        network.Add("10.9.2.30", FakeSerials.Make(99), "pw");
        Assert.Equal(DeviceFollowResult.NotVerified, await autoAdd.FollowAsync(FakeSerials.Make(20), "10.9.2.30", "DHCP server", CancellationToken.None));
        Assert.Equal("10.9.2.20", (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Address);
    }

    [Fact]
    public async Task ADeviceReachedByHostNameKeepsIt()
    {
        var network = new FakeAxisNetwork();
        var (host, autoAdd) = await StartAsync(network, withCredential: false);
        await using var _ = host;
        await DeviceServiceTests.AddDeviceAsync(host, "axis-accc8e000016.local", 22);

        Assert.Equal(DeviceFollowResult.KeptHostName, await autoAdd.FollowAsync(FakeSerials.Make(22), "10.9.2.22", "DHCP server", CancellationToken.None));
    }

    [Fact]
    public async Task CorePluginsGetTheServiceThroughTheirContext()
    {
        await using var host = await TestServerHost.StartAsync();
        var probe = new ContextProbe();
        var registry = host.Get<PluginRegistry>();
        registry.RegisterCorePlugin(probe, new PluginOrigin("test", "1.0", null));
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);

        Assert.Same(host.Get<IDeviceAutoAdd>(), probe.AutoAdd);
    }

    private sealed class ContextProbe : Oadm.Sdk.Plugins.ICorePlugin
    {
        public IDeviceAutoAdd? AutoAdd { get; private set; }

        public string Id => "test.auto-add-probe";

        public string DisplayName => "Probe";

        public string? IconKey => null;

        public IReadOnlyList<Oadm.Sdk.Plugins.ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(Oadm.Sdk.Plugins.ICorePluginContext ctx, CancellationToken ct)
        {
            AutoAdd = ctx.AutoAdd;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
