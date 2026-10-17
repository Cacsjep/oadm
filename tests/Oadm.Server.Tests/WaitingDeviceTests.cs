using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Server.AddDevices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

/// <summary>
/// The import's waiting list on the real server: lines whose device did not answer are kept, tried again by
/// <see cref="WaitingDeviceRunner"/> through the add page's pipeline and added once they answer.
/// </summary>
public sealed class WaitingDeviceTests
{
    private const string FilePassword = "file-Pass-1";

    private static Proto.WaitingDeviceService.WaitingDeviceServiceClient Client(TestServerHost host) => new(host.Invoker);

    private static Proto.WaitingLine Line(string address, string? password = FilePassword, string serial = "", params string[] tags)
    {
        var line = new Proto.WaitingLine { Address = address, UserName = password is null ? "" : "root", Password = password ?? "", ExpectedSerial = serial };
        line.Tags.AddRange(tags);
        return line;
    }

    private static async Task KeepAsync(TestServerHost host, params Proto.WaitingLine[] lines)
    {
        var request = new Proto.AddWaitingRequest { Source = "Import from file site.csv" };
        request.Lines.AddRange(lines);
        await Client(host).AddAsync(request);
    }

    [Fact]
    public async Task ADeviceThatComesOnlineIsAddedWithTheLoginAndTagsOfItsLine()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.5.1", FakeSerials.Make(51), FilePassword);
        camera.Offline = true;
        await using var host = await TestServerHost.StartAsync(network);
        await KeepAsync(host, Line("10.9.5.1", tags: "Building A"));
        var runner = host.Get<WaitingDeviceRunner>();

        // Still off: it stays, with what the try found.
        Assert.Equal(0, await runner.RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None));
        var list = await Client(host).ListAsync(new Proto.ListWaitingRequest());
        var entry = Assert.Single(list.Devices);
        Assert.Equal(1, entry.Tries);
        Assert.Equal("No Axis device answered at 10.9.5.1.", entry.LastResult);
        Assert.True(entry.HasLogin);
        Assert.False(entry.LoginFailed);
        Assert.DoesNotContain(FilePassword, list.ToString(), StringComparison.Ordinal);

        // On: added with the line's login and tags, and the entry is gone.
        camera.Offline = false;
        Assert.Equal(1, await runner.RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None));
        var device = await host.Get<DeviceRepository>().FindBySerialAsync(FakeSerials.Make(51), CancellationToken.None);
        Assert.NotNull(device);
        Assert.Equal("10.9.5.1", device.Address);
        Assert.Equal(["Building A"], device.Tags);
        var stored = await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(("root", FilePassword), (stored!.UserName, stored.Password));
        Assert.Equal(0, (await Client(host).ListAsync(new Proto.ListWaitingRequest())).TotalCount);

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        var added = Assert.Single(audit, e => e.Action == AuditActions.DeviceAddedAutomatically);
        Assert.Equal("system", added.UserName);
        Assert.Contains("Import from file site.csv", added.Detail, StringComparison.Ordinal);
        Assert.Contains(audit, e => e.Action == AuditActions.WaitingAdded);
        Assert.All(audit, e => Assert.DoesNotContain(FilePassword, e.Detail + e.Target, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedLoginIsNotTriedAgainUntilTryNow()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.5.2", FakeSerials.Make(52), "the-real-password");
        await using var host = await TestServerHost.StartAsync(network);
        await KeepAsync(host, Line("10.9.5.2"));
        var runner = host.Get<WaitingDeviceRunner>();

        await runner.RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);
        var entry = Assert.Single((await Client(host).ListAsync(new Proto.ListWaitingRequest())).Devices);
        Assert.True(entry.LoginFailed);
        using (var watch = Client(host).Watch(new Proto.Empty()))
        {
            Assert.True(await watch.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal(1, watch.ResponseStream.Current.LoginFailed);
        }

        int tries = camera.CredentialRequests.Count;
        Assert.True(tries > 0);

        // The scheduled round leaves it alone: no repeated wrong passwords.
        await runner.RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);
        Assert.Equal(tries, camera.CredentialRequests.Count);

        // Try now (all) tries it once more; with the right password in the credential list it is added.
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "the-real-password" });
        Assert.Equal(1, await runner.RunRoundAsync(null, includeLoginFailed: true, CancellationToken.None));
        var device = await host.Get<DeviceRepository>().FindBySerialAsync(FakeSerials.Make(52), CancellationToken.None);
        Assert.Equal(SdkDeviceStatus.Ok, device!.Status);
    }

    [Fact]
    public async Task AnotherDeviceAtTheAddressGetsNoPasswordAndTheLineKeepsWaiting()
    {
        var network = new FakeAxisNetwork();
        var other = network.Add("10.9.5.3", FakeSerials.Make(53), FilePassword);
        await using var host = await TestServerHost.StartAsync(network);
        await KeepAsync(host, Line("10.9.5.3", serial: FakeSerials.Make(54)));

        await host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);

        Assert.Empty(other.CredentialRequests);
        var entry = Assert.Single((await Client(host).ListAsync(new Proto.ListWaitingRequest())).Devices);
        Assert.False(entry.LoginFailed);
        Assert.Contains("Another device answers", entry.LastResult, StringComparison.Ordinal);
        Assert.Empty(await host.Get<DeviceRepository>().ListDevicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnAddressKeptAgainIsReplacedAndDevicesAddedAnotherWayLeaveTheList()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.5.4", FakeSerials.Make(55), FilePassword).Offline = true;
        await using var host = await TestServerHost.StartAsync(network);
        await KeepAsync(host, Line("10.9.5.4", tags: "Old"), Line("10.9.5.5"));
        await KeepAsync(host, Line(" 10.9.5.4 ", password: null, tags: "New"));
        var list = await Client(host).ListAsync(new Proto.ListWaitingRequest());
        Assert.Equal(2, list.TotalCount);
        var kept = list.Devices.Single(d => d.Address == "10.9.5.4");
        Assert.Equal(["New"], kept.Tags);
        Assert.False(kept.HasLogin);

        // 10.9.5.5 is added another way (here directly): the next round drops its entry without trying it.
        await host.Get<DeviceRepository>().AddAsync(new Device { Serial = FakeSerials.Make(56), Address = "10.9.5.5" }, CancellationToken.None);
        await host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);
        Assert.Equal(["10.9.5.4"], (await Client(host).ListAsync(new Proto.ListWaitingRequest())).Devices.Select(d => d.Address));

        // Remove and Remove all.
        var summary = await Client(host).RemoveAsync(new Proto.WaitingIds { All = true });
        Assert.Equal(0, summary.Count);
    }

    [Fact]
    public async Task LinesAreCheckedAndARoleWithoutAddAndRemoveIsRefused()
    {
        await using var host = await TestServerHost.StartAsync(new FakeAxisNetwork());
        var empty = await Assert.ThrowsAsync<RpcException>(async () => await KeepAsync(host, Line(" ")));
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
        var mac = await Assert.ThrowsAsync<RpcException>(async () => await KeepAsync(host, Line("10.9.5.9", serial: "XYZ")));
        Assert.Equal(StatusCode.InvalidArgument, mac.StatusCode);

        var viewer = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(
            await host.InvokerForRoleAsync("viewer1", Oadm.Core.Auth.BuiltInRoles.ViewerId));
        var denied = await Assert.ThrowsAsync<RpcException>(async () => await viewer.ListAsync(new Proto.ListWaitingRequest()));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }
}
