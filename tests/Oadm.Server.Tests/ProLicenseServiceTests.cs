using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Auth;
using Oadm.Core.Licensing;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>LicenseService: the server's OADM PRO license, imported and removed over gRPC, audited, handed to the plugins.</summary>
public sealed class ProLicenseServiceTests
{
    private static readonly (string PrivateKeyPem, string PublicKey) Key = LicenseFile.CreateKeys();

    private static Task<TestServerHost> StartAsync() =>
        TestServerHost.StartAsync(configureServices: services => services.AddSingleton(sp => new ProLicense(
            sp.GetRequiredService<ServerSettingsStore>(),
            () => sp.GetRequiredService<PluginLicenses>().ServerId,
            publicKeys: [Key.PublicKey])));

    private static string Sign(string server, DateOnly? expires = null, bool trial = false) =>
        ProLicenseFile.Sign(Key.PrivateKeyPem, server, "ACME GmbH", "Up to 500 devices", 500, DateOnly.FromDateTime(DateTime.Today), expires ?? new DateOnly(2099, 12, 31), trial);

    [Fact]
    public async Task ImportAndRemoveAreAuditedAndReachThePlugins()
    {
        await using var host = await StartAsync();
        var licenses = new Proto.LicenseService.LicenseServiceClient(host.Invoker);
        var state = await licenses.GetLicenseAsync(new Proto.Empty());
        Assert.Equal(host.Get<PluginLicenses>().ServerId, state.ServerId); // the same server ID plugin licenses name
        Assert.Equal((Proto.ProLicenseStatus.ProLicenseNone, ProLicenseTexts.NoLicense), (state.Status, state.Reason));
        var pro = host.Get<PluginRegistry>().Pro;
        Assert.NotNull(pro);
        Assert.False(pro.IsActive);

        state = await licenses.ImportLicenseAsync(new Proto.ImportLicenseRequest { License = Sign(state.ServerId) });

        Assert.Equal(Proto.ProLicenseStatus.ProLicenseActive, state.Status);
        Assert.Equal(("ACME GmbH", "Up to 500 devices", 500L, "2099-12-31", false), (state.Customer, state.Plan, state.MaxDevices, state.Expires, state.Trial));
        Assert.Equal("Up to 500 devices, until 2099-12-31", state.Reason);
        Assert.True(pro.IsActive);

        state = await licenses.RemoveLicenseAsync(new Proto.Empty());
        Assert.Equal(Proto.ProLicenseStatus.ProLicenseNone, state.Status);
        Assert.False(state.HasMaxDevices);
        Assert.False(pro.IsActive);

        var audit = await host.Audit.ListAsync(new Proto.ListAuditRequest());
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.ProLicenseImported && e.Target == "OADM PRO");
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.ProLicenseRemoved && e.Target == "OADM PRO");
    }

    [Fact]
    public async Task TheTrialStartsOnceIsAuditedAndReachesThePlugins()
    {
        await using var host = await StartAsync();
        var licenses = new Proto.LicenseService.LicenseServiceClient(host.Invoker);
        var state = await licenses.GetLicenseAsync(new Proto.Empty());
        Assert.Equal((true, false), (state.TrialAvailable, state.HasKey));
        var pro = host.Get<PluginRegistry>().Pro!;
        var changed = 0;
        pro.Changed += (_, _) => Interlocked.Increment(ref changed);

        state = await licenses.StartTrialAsync(new Proto.Empty());

        Assert.Equal((Proto.ProLicenseStatus.ProLicenseActive, true, false, false), (state.Status, state.Trial, state.TrialAvailable, state.HasKey));
        Assert.Equal("Trial, until " + state.Expires, state.Reason); // the days themselves: ProTrialTests (fake clock)
        Assert.True(pro.IsActive);
        Assert.Equal(1, Volatile.Read(ref changed)); // 802.1X and EST can be turned on at once

        var again = await Assert.ThrowsAsync<RpcException>(async () => await licenses.StartTrialAsync(new Proto.Empty()));
        Assert.Equal((StatusCode.FailedPrecondition, ProLicenseTexts.TrialUsed), (again.StatusCode, again.Status.Detail));

        // Remove license never ends the trial (there is no key); an imported key wins and can be removed again.
        await licenses.RemoveLicenseAsync(new Proto.Empty());
        Assert.True(pro.IsActive);
        state = await licenses.ImportLicenseAsync(new Proto.ImportLicenseRequest { License = Sign(state.ServerId) });
        Assert.Equal((false, true), (state.Trial, state.HasKey));
        state = await licenses.RemoveLicenseAsync(new Proto.Empty());
        Assert.Equal((Proto.ProLicenseStatus.ProLicenseActive, true), (state.Status, state.Trial));

        var audit = await host.Audit.ListAsync(new Proto.ListAuditRequest());
        Assert.Single(audit.Entries, e => e.Action == AuditActions.ProTrialStarted && e.Target == "OADM PRO");
        Assert.Single(audit.Entries, e => e.Action == AuditActions.ProLicenseRemoved); // only the removal of the key
    }

    [Fact]
    public async Task KeysThatDoNotFitAreRefusedWithTheReason()
    {
        await using var host = await StartAsync();
        var licenses = new Proto.LicenseService.LicenseServiceClient(host.Invoker);
        var server = (await licenses.GetLicenseAsync(new Proto.Empty())).ServerId;

        async Task<RpcException> Refused(string text) =>
            await Assert.ThrowsAsync<RpcException>(async () => await licenses.ImportLicenseAsync(new Proto.ImportLicenseRequest { License = text }));

        var other = await Refused(Sign("11111111-2222-3333-4444-555555555555"));
        Assert.Equal((StatusCode.InvalidArgument, ProLicenseTexts.WrongServer), (other.StatusCode, other.Status.Detail));
        Assert.Equal("Expired on 2020-01-31.", (await Refused(Sign(server, new DateOnly(2020, 1, 31)))).Status.Detail);
        Assert.Equal(ProLicenseTexts.NotValid, (await Refused("hello")).Status.Detail);
        Assert.Equal(ProLicenseTexts.NotValid, (await Refused("")).Status.Detail);
        Assert.Equal(Proto.ProLicenseStatus.ProLicenseNone, (await licenses.GetLicenseAsync(new Proto.Empty())).Status);
    }

    [Fact]
    public async Task OperatorsReadTheLicenseButCannotChangeIt()
    {
        await using var host = await StartAsync();
        var admin = new Proto.LicenseService.LicenseServiceClient(host.Invoker);
        var server = (await admin.GetLicenseAsync(new Proto.Empty())).ServerId;
        await admin.ImportLicenseAsync(new Proto.ImportLicenseRequest { License = Sign(server, trial: true) });
        var operatorClient = new Proto.LicenseService.LicenseServiceClient(await host.InvokerForUserAsync("op", UserRole.Operator));

        var state = await operatorClient.GetLicenseAsync(new Proto.Empty());
        Assert.True(state.Trial);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () =>
            await operatorClient.ImportLicenseAsync(new Proto.ImportLicenseRequest { License = Sign(server) }))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () =>
            await operatorClient.RemoveLicenseAsync(new Proto.Empty()))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () =>
            await operatorClient.StartTrialAsync(new Proto.Empty()))).StatusCode);
        Assert.Equal(Proto.ProLicenseStatus.ProLicenseActive, (await admin.GetLicenseAsync(new Proto.Empty())).Status);
    }
}
