using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Server.AddDevices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

/// <summary>
/// The first set up of a device for core plugins (SDK 0.15.0, Provisioning): the look without a password, the first password
/// from a credential list entry with the add page's checks, the add with one entry and tags, the credential list's names.
/// </summary>
public sealed class DeviceFirstSetupTests
{
    private const string Password = "s3cret-Pass";

    private static async Task<(TestServerHost Host, IDeviceAutoAdd AutoAdd, Guid Root)> StartAsync(FakeAxisNetwork network)
    {
        var host = await TestServerHost.StartAsync(network);
        var autoAdd = host.Get<IDeviceAutoAdd>();
        var root = await autoAdd.AddCredentialAsync("root", Password, CancellationToken.None);
        return (host, autoAdd, root.Id);
    }

    [Fact]
    public async Task TheLookTellsAFactoryNewCameraFromOneWithAPasswordWithoutSendingOne()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.1", FakeSerials.Make(1), password: null);
        network.Add("10.9.2.2", FakeSerials.Make(2), Password);
        var (host, autoAdd, _) = await StartAsync(network);
        await using var _ = host;

        Assert.Equal(DeviceLookResult.FactoryNew, (await autoAdd.LookAsync("10.9.2.1", FakeSerials.Make(1), CancellationToken.None)).Result);
        Assert.Equal(DeviceLookResult.HasPassword, (await autoAdd.LookAsync("10.9.2.2", FakeSerials.Make(2), CancellationToken.None)).Result);
        Assert.Equal(DeviceLookResult.SerialMismatch, (await autoAdd.LookAsync("10.9.2.2", FakeSerials.Make(3), CancellationToken.None)).Result);
        Assert.Equal(DeviceLookResult.Unreachable, (await autoAdd.LookAsync("10.9.2.9", FakeSerials.Make(9), CancellationToken.None)).Result);
        Assert.Empty(network["10.9.2.1"].CredentialRequests);
        Assert.Empty(network["10.9.2.2"].CredentialRequests);
        Assert.Equal(0, network["10.9.2.1"].PwdgrpCalls);
    }

    [Fact]
    public async Task TheFirstPasswordReachesAFactoryNewCameraAndNeverOneWithAPassword()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.3", FakeSerials.Make(3), password: null);
        network.Add("10.9.2.4", FakeSerials.Make(4), "other-password");
        var (host, autoAdd, root) = await StartAsync(network);
        await using var _ = host;

        var set = await autoAdd.SetFirstPasswordAsync("10.9.2.3", FakeSerials.Make(3), root, "Provisioning", CancellationToken.None);
        Assert.True(set.Ok, set.Message);
        Assert.Equal(Password, network["10.9.2.3"].Password);

        var refused = await autoAdd.SetFirstPasswordAsync("10.9.2.4", FakeSerials.Make(4), root, "Provisioning", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal(0, network["10.9.2.4"].PwdgrpCalls);
        Assert.Equal("other-password", network["10.9.2.4"].Password);

        var gone = await autoAdd.SetFirstPasswordAsync("10.9.2.3", FakeSerials.Make(3), Guid.NewGuid(), "Provisioning", CancellationToken.None);
        Assert.Equal(DeviceFirstSetupTexts.CredentialGone, gone.Message);

        var entries = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(entries, e => e.Action == AuditActions.FirstPasswordSet && e.Detail.StartsWith("Provisioning:", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => (e.Detail + e.Target).Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyACredentialOfRootSetsTheFirstPassword()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.5", FakeSerials.Make(5), password: null);
        var (host, autoAdd, _) = await StartAsync(network);
        await using var _ = host;
        var operatorEntry = await autoAdd.AddCredentialAsync("operator", "op-Pass-1", CancellationToken.None);

        var refused = await autoAdd.SetFirstPasswordAsync("10.9.2.5", FakeSerials.Make(5), operatorEntry.Id, "Provisioning", CancellationToken.None);

        Assert.False(refused.Ok);
        Assert.Equal(DeviceFirstSetupTexts.RootOnly, refused.Message);
        Assert.Equal(0, network["10.9.2.5"].PwdgrpCalls);
        var listed = Assert.Single(await autoAdd.ListCredentialsAsync(CancellationToken.None), c => c.UserName == "operator");
        Assert.Equal(operatorEntry.Id, listed.Id); // the ids of SettingsService.RevealCredential
        Assert.NotNull(listed.Added); // tells equal user names apart
        Assert.Equal(TimeSpan.Zero, listed.Added.Value.Offset);
        Assert.Equal(operatorEntry.Added, listed.Added);
    }

    [Fact]
    public async Task TheAddWithOneEntryLogsInWithItAndSetsTheTags()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.6", FakeSerials.Make(6), Password);
        var (host, autoAdd, root) = await StartAsync(network);
        await using var _ = host;

        var outcome = await autoAdd.AddWithLoginAsync("10.9.2.6", FakeSerials.Make(6), root, ["Building A"], "Provisioning", CancellationToken.None);

        Assert.Equal(DeviceAutoAddResult.Added, outcome.Result);
        var device = await host.Get<DeviceRepository>().GetAsync(outcome.DeviceId!.Value, CancellationToken.None);
        Assert.Equal(SdkDeviceStatus.Ok, device!.Status);
        Assert.Contains("Building A", device.Tags);
        var stored = await host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(("root", Password), (stored!.UserName, stored.Password));

        // Managed already: the record stays, the tags are added.
        var again = await autoAdd.AddWithLoginAsync("10.9.2.6", FakeSerials.Make(6), root, ["Floor 2"], "Provisioning", CancellationToken.None);
        Assert.Equal(DeviceAutoAddResult.AlreadyManaged, again.Result);
        Assert.Equal(device.Id, again.DeviceId);
        Assert.Contains("Floor 2", (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Tags);
        Assert.Equal(DeviceLookResult.HasPassword, (await autoAdd.LookAsync("10.9.2.6", FakeSerials.Make(6), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task TheLookAsksTheCameraEvenWhenOadmManagesIt()
    {
        // The "Not new" of a real camera: OADM had it with a password, then it was reset to factory default.
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.7", FakeSerials.Make(7), Password);
        var (host, autoAdd, root) = await StartAsync(network);
        await using var _ = host;
        var added = await autoAdd.AddWithLoginAsync("10.9.2.7", FakeSerials.Make(7), root, [], "Provisioning", CancellationToken.None);
        Assert.Equal(DeviceLookResult.HasPassword, (await autoAdd.LookAsync("10.9.2.7", FakeSerials.Make(7), CancellationToken.None)).Result);

        network["10.9.2.7"].Password = null; // reset

        var look = await autoAdd.LookAsync("10.9.2.7", FakeSerials.Make(7), CancellationToken.None);
        Assert.Equal(DeviceLookResult.FactoryNew, look.Result);
        Assert.Equal(added.DeviceId, look.DeviceId);
    }

    [Fact]
    public async Task ACameraThatIsStillStartingIsAskedAgainAndNeverTakenAsConfigured()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero));
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.8", FakeSerials.Make(8), password: null);
        network.Add("10.9.2.9", FakeSerials.Make(9), password: null);
        network["10.9.2.8"].StartingAnswers = 3;
        network["10.9.2.9"].StartingAnswers = int.MaxValue;
        var host = await TestServerHost.StartAsync(network, configureServices: s => s.AddSingleton(new DeviceLookOptions { Time = time }));
        await using var _ = host;
        var autoAdd = host.Get<IDeviceAutoAdd>();

        async Task<DeviceLook> LookAsync(string address, string serial)
        {
            var look = autoAdd.LookAsync(address, serial, CancellationToken.None);
            await Eventually.TrueAsync(() =>
            {
                time.Advance(TimeSpan.FromSeconds(5));
                return look.IsCompleted;
            }, "the look ended");
            return await look;
        }

        Assert.Equal(DeviceLookResult.FactoryNew, (await LookAsync("10.9.2.8", FakeSerials.Make(8))).Result);
        var starting = await LookAsync("10.9.2.9", FakeSerials.Make(9));
        Assert.Equal(DeviceLookResult.Starting, starting.Result); // after 2 minutes: unknown, never "has a password"
        Assert.Equal(DeviceAutoAddService.StillStarting, starting.Message);
        Assert.Empty(network["10.9.2.9"].CredentialRequests);
    }

    [Fact]
    public async Task TheServerReportsADeviceStoredAsPasswordNotSetAndAManagedOneThatWasReset()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.2.10", FakeSerials.Make(10), password: null);
        network.Add("10.9.2.11", FakeSerials.Make(11), Password);
        var (host, autoAdd, _) = await StartAsync(network);
        await using var _ = host;
        var found = new System.Collections.Concurrent.ConcurrentQueue<FactoryNewDevice>();
        using (autoAdd.WatchFactoryNew(found.Enqueue))
        {
            var factoryNew = await autoAdd.AddAsync("10.9.2.10", FakeSerials.Make(10), "DHCP server", CancellationToken.None);
            var configured = await autoAdd.AddAsync("10.9.2.11", FakeSerials.Make(11), "DHCP server", CancellationToken.None);
            Assert.Equal(DeviceAutoAddResult.AddedPasswordNotSet, factoryNew.Result);
            Assert.Equal(DeviceAutoAddResult.Added, configured.Result);
            var reported = Assert.Single(found);
            Assert.Equal((factoryNew.DeviceId!.Value, FakeSerials.Make(10), false, true), (reported.DeviceId, reported.Serial, reported.AtSite, reported.Added));

            // The polling finds the configured one reset to factory default: new again.
            network["10.9.2.11"].Password = null;
            var polled = await host.Get<DevicePollingService>().PollAsync(configured.DeviceId!.Value, CancellationToken.None);
            Assert.Equal(SdkDeviceStatus.PasswordNotSet, polled!.Status);
            Assert.Contains(found, d => d.DeviceId == configured.DeviceId && !d.Added);
            Assert.Single(found, d => d.Added);
        }

        network.Add("10.9.2.12", FakeSerials.Make(12), password: null);
        await autoAdd.AddAsync("10.9.2.12", FakeSerials.Make(12), "DHCP server", CancellationToken.None);
        Assert.DoesNotContain(found, d => d.Serial == FakeSerials.Make(12)); // the watch ended
    }
}
