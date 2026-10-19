using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Compliance;
using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>ComplianceService over gRPC: the default checks, saving with every rule, results stream, roles, audit, events.</summary>
public sealed class ComplianceServiceTests
{
    private static Proto.ComplianceService.ComplianceServiceClient Client(CallInvoker invoker) => new(invoker);

    private static async Task<Device> AddDeviceAsync(TestServerHost host, string address, DeviceStatus status = DeviceStatus.Ok) =>
        await host.Get<DeviceRepository>().AddAsync(new Device
        {
            Serial = "ACCC8E" + Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture),
            Address = address,
            Model = "P3265-V",
            FirmwareVersion = "11.9.60",
            Status = status,
            Category = DeviceCategory.Camera,
        }, CancellationToken.None);

    private static Proto.ComplianceCheckDetail Firmware(string name, string minimum) => new()
    {
        Summary = new Proto.ComplianceCheckSummary
        {
            Name = name,
            TypeId = BuiltInComplianceTypes.FirmwareId,
            Severity = Proto.ComplianceSeverity.ComplianceWarning,
            Enabled = true,
            Devices = new Proto.ComplianceDevices { All = true },
        },
        SettingsJson = $$"""{"minimum":"{{minimum}}"}""",
    };

    [Fact]
    public async Task ChecksAreSavedListedAndStreamedWithTheirResults()
    {
        await using var host = await TestServerHost.StartAsync();
        var client = Client(host.Invoker);
        var old = await AddDeviceAsync(host, "10.0.0.48");
        var gone = await AddDeviceAsync(host, "10.0.0.49", DeviceStatus.Unreachable);

        var catalog = await client.GetCatalogAsync(new Proto.Empty());
        Assert.Contains(catalog.Entries, t => t.Id == BuiltInComplianceTypes.CertificateId && !t.ReadsDevices && t.Fields.Any(f => f.Key == "days"));

        var saved = await client.SaveCheckAsync(Firmware("AXIS OS 12", "12.0"));
        Assert.Equal("AXIS OS 12.0 or later", saved.Summary);
        Assert.Equal(0, saved.IntervalMinutes);

        var checks = (await client.ListChecksAsync(new Proto.Empty())).Checks;
        Assert.Equal(["Login works", "Reachable", "AXIS OS 12"], checks.Select(c => c.Name)); // the default checks first
        Assert.True(checks.Single(c => c.Name == "Reachable").BuiltIn);

        using var watch = client.WatchResults(new Proto.Empty());
        var results = new Dictionary<(string, string), Proto.ComplianceResultEntry>();
        var reachable = BuiltInComplianceTypes.ReachableCheckId.ToString();
        while (await watch.ResponseStream.MoveNext(CancellationToken.None))
        {
            foreach (var r in watch.ResponseStream.Current.Results)
            {
                results[(r.CheckId, r.DeviceId)] = r;
            }

            if (results.TryGetValue((saved.Id, old.Id.ToString()), out var firmware) && firmware.Outcome == Proto.ComplianceOutcome.ComplianceFailed
                && results.TryGetValue((reachable, gone.Id.ToString()), out var down) && down.Outcome == Proto.ComplianceOutcome.ComplianceFailed)
            {
                break;
            }
        }

        Assert.Equal("AXIS OS 11.9.60, needs 12.0", results[(saved.Id, old.Id.ToString())].Detail);
        await Eventually.TrueAsync(async () => (await client.ListChecksAsync(new Proto.Empty())).Checks.Single(c => c.Id == saved.Id).Failed == 2, "counts follow");

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.ComplianceCheckCreated && e.Target == "AXIS OS 12");
    }

    [Fact]
    public async Task EveryRuleIsCheckedAgainOnTheServer()
    {
        await using var host = await TestServerHost.StartAsync();
        var client = Client(host.Invoker);

        var noName = await Assert.ThrowsAsync<RpcException>(async () => await client.SaveCheckAsync(Firmware(" ", "12.0")));
        Assert.Equal((StatusCode.InvalidArgument, "Enter a name."), (noName.StatusCode, noName.Status.Detail));

        var noDevices = Firmware("x", "12.0");
        noDevices.Summary.Devices = new Proto.ComplianceDevices();
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.SaveCheckAsync(noDevices));
        Assert.Equal("Choose at least one device or tag.", refused.Status.Detail);

        var saved = await client.SaveCheckAsync(Firmware("x", "12.0"));
        var stale = Firmware("x", "12.5");
        stale.Summary.Id = saved.Id;
        stale.Summary.Version = saved.Version + 7;
        Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(async () => await client.SaveCheckAsync(stale))).StatusCode);

        var deleteDefault = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.DeleteCheckAsync(new Proto.ComplianceCheckId { Id = BuiltInComplianceTypes.ReachableCheckId.ToString() }));
        Assert.Equal(StatusCode.FailedPrecondition, deleteDefault.StatusCode);

        await client.DeleteCheckAsync(new Proto.ComplianceCheckId { Id = saved.Id });
        Assert.DoesNotContain((await client.ListChecksAsync(new Proto.Empty())).Checks, c => c.Id == saved.Id);
    }

    [Fact]
    public async Task ViewersSeeComplianceButDoNotChangeChecks()
    {
        await using var host = await TestServerHost.StartAsync();
        var viewer = Client(await host.InvokerForRoleAsync("viewer", BuiltInRoles.ViewerId));
        var op = Client(await host.InvokerForRoleAsync("op", BuiltInRoles.OperatorId));

        Assert.NotEmpty((await viewer.ListChecksAsync(new Proto.Empty())).Checks);
        await viewer.CheckNowAsync(new Proto.ComplianceCheckNowRequest());
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await viewer.SaveCheckAsync(Firmware("x", "12.0")))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await op.SaveCheckAsync(Firmware("x", "12.0")))).StatusCode);
    }

    [Fact]
    public async Task EachCheckIsAnEventOfTheEventRules()
    {
        await using var host = await TestServerHost.StartAsync();
        var saved = await Client(host.Invoker).SaveCheckAsync(Firmware("AXIS OS 12", "12.0"));
        var events = new Proto.EventService.EventServiceClient(host.Invoker);

        await Eventually.TrueAsync(async () => (await events.GetCatalogAsync(new Proto.Empty())).EventTypes
            .Any(t => t.Id == ComplianceEngine.EventTypeId(Guid.Parse(saved.Id)) && t.DisplayName == "Compliance: AXIS OS 12" && t.Stateful && t.Available),
            "the check is an event");
        Assert.Contains(host.Get<EventCatalog>().EventTypes, t => t.SourceId == ComplianceEngine.EventSourceId);
    }
}
