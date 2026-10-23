using Grpc.Core;

using Oadm.Core.Compliance;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.DeviceGroups.SharedCore;

/// <summary>ComplianceService for users of a device group: own checks only, owner field, refusals, Check now and results of the view.</summary>
public sealed class ComplianceScopeTests
{
    private static Proto.ComplianceCheckDetail Reachable(string name, Proto.ComplianceDevices? devices = null, Proto.DeviceOwner? owner = null) => new()
    {
        Summary = new Proto.ComplianceCheckSummary
        {
            Name = name,
            TypeId = BuiltInComplianceTypes.ReachableId,
            Severity = Proto.ComplianceSeverity.ComplianceProblem,
            Enabled = true,
            Devices = devices ?? new Proto.ComplianceDevices { All = true },
            Owner = owner,
        },
        SettingsJson = "{}",
    };

    [Fact]
    public async Task AGroupUserSeesOnlyTheGroupsChecksWithTheOwnerAndOnlyTheirResults()
    {
        await using var host = await TestServerHost.StartAsync();
        var admin = new Proto.ComplianceService.ComplianceServiceClient(host.Invoker);
        await admin.SetEnabledAsync(new Proto.SetComplianceEnabledRequest { Enabled = true });
        var inside = await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var outside = await GroupCallers.DeviceAsync(host, 2, "Yard");
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var client = new Proto.ComplianceService.ComplianceServiceClient(user.Invoker);

        Assert.Empty((await client.ListChecksAsync(new Proto.Empty())).Checks); // the defaults belong to all devices
        var saved = await client.SaveCheckAsync(Reachable("Lobby reachable"));
        Assert.False(saved.Owner.AllDevices);
        Assert.Equal(user.GroupId.ToString(), saved.Owner.GroupId);
        Assert.Equal("Group Lobby", saved.Owner.GroupName);
        Assert.Equal(1, saved.Passed);

        var adminList = (await admin.ListChecksAsync(new Proto.Empty())).Checks;
        Assert.Equal(3, adminList.Count);
        Assert.True(adminList.Single(c => c.BuiltIn && c.Name == "Reachable").Owner.AllDevices);

        // The results stream of the group user carries only the group's check, on the group's device.
        using var stream = client.WatchResults(new Proto.Empty());
        var results = new List<Proto.ComplianceResultEntry>();
        while (await stream.ResponseStream.MoveNext())
        {
            results.AddRange(stream.ResponseStream.Current.Results);
            if (stream.ResponseStream.Current.SnapshotEnd)
            {
                break;
            }
        }

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(saved.Id, r.CheckId));
        Assert.All(results, r => Assert.Equal(inside.Id.ToString(), r.DeviceId));
        Assert.DoesNotContain(results, r => r.DeviceId == outside.Id.ToString());
    }

    [Fact]
    public async Task SavingRefusesOutsideDevicesHiddenChecksAndAnotherOwner()
    {
        await using var host = await TestServerHost.StartAsync();
        var admin = new Proto.ComplianceService.ComplianceServiceClient(host.Invoker);
        await admin.SetEnabledAsync(new Proto.SetComplianceEnabledRequest { Enabled = true });
        var inside = await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var outside = await GroupCallers.DeviceAsync(host, 2, "Yard");
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var client = new Proto.ComplianceService.ComplianceServiceClient(user.Invoker);

        foreach (var id in new[] { outside.Id, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.SaveCheckAsync(Reachable("Some", new Proto.ComplianceDevices { DeviceIds = { inside.Id.ToString(), id.ToString() } })));
            Assert.Equal(StatusCode.NotFound, refused.StatusCode);
            Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Status.Detail);
        }

        var other = await Assert.ThrowsAsync<RpcException>(async () => await client.SaveCheckAsync(Reachable("Mine", owner: new Proto.DeviceOwner { AllDevices = true })));
        Assert.Equal(StatusCode.PermissionDenied, other.StatusCode);
        Assert.Equal(OwnerRules.OnlyAdministrators, other.Status.Detail);

        // The default checks are hidden: like unknown ones.
        var hidden = BuiltInComplianceTypes.ReachableCheckId.ToString();
        foreach (var call in new Func<Task>[]
        {
            async () => await client.GetCheckAsync(new Proto.ComplianceCheckId { Id = hidden }),
            async () => await client.DeleteCheckAsync(new Proto.ComplianceCheckId { Id = hidden }),
            async () => await client.SetCheckEnabledAsync(new Proto.SetComplianceCheckEnabledRequest { Id = hidden, Enabled = false }),
            async () => await client.CheckNowAsync(new Proto.ComplianceCheckNowRequest { CheckIds = { hidden } }),
        })
        {
            Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(call)).StatusCode);
        }

        var checkNow = await Assert.ThrowsAsync<RpcException>(async () => await client.CheckNowAsync(new Proto.ComplianceCheckNowRequest { DeviceIds = { outside.Id.ToString() } }));
        Assert.Equal(StatusCode.NotFound, checkNow.StatusCode);
        var choices = await Assert.ThrowsAsync<RpcException>(async () => await client.ListChoicesAsync(new Proto.ComplianceChoicesRequest
        {
            TypeId = BuiltInComplianceTypes.FirmwareId, FieldKey = "minimum", DeviceId = outside.Id.ToString(),
        }));
        Assert.Equal(StatusCode.NotFound, choices.StatusCode);

        // An administrator gives a check to the group; the group user then sees it.
        var given = await admin.SaveCheckAsync(Reachable("Given", owner: new Proto.DeviceOwner { GroupId = user.GroupId.ToString() }));
        Assert.Equal(user.GroupId.ToString(), given.Owner.GroupId);
        Assert.Contains((await client.ListChecksAsync(new Proto.Empty())).Checks, c => c.Id == given.Id);
    }

    [Fact]
    public async Task DeletingTheGroupLeavesItsChecksOffWithoutAnOwner()
    {
        await using var host = await TestServerHost.StartAsync();
        var admin = new Proto.ComplianceService.ComplianceServiceClient(host.Invoker);
        await admin.SetEnabledAsync(new Proto.SetComplianceEnabledRequest { Enabled = true });
        await GroupCallers.DeviceAsync(host, 1, "Lobby");
        var user = await GroupCallers.ForTagAsync(host, "lobby", "Lobby");
        var saved = await new Proto.ComplianceService.ComplianceServiceClient(user.Invoker).SaveCheckAsync(Reachable("Lobby reachable"));

        var deleted = await new Proto.DeviceGroupService.DeviceGroupServiceClient(host.Invoker).DeleteAsync(new Proto.DeviceGroupIdRequest { Id = user.GroupId.ToString() });
        Assert.Contains("1 compliance check", deleted.Disabled);
        await Eventually.TrueAsync(async () => (await admin.ListChecksAsync(new Proto.Empty())).Checks.Single(c => c.Id == saved.Id) is { Enabled: false, Owner.AllDevices: false, Owner.GroupId: "" },
            "the check is off without an owner");
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await admin.SetCheckEnabledAsync(new Proto.SetComplianceCheckEnabledRequest { Id = saved.Id, Enabled = true }));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(DeviceGroupTexts.ChooseDevices, refused.Status.Detail);
    }
}
