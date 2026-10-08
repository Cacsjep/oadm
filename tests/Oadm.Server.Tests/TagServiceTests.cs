using Grpc.Core;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>TagService: definitions, validation, uniqueness, tagging many devices, rename / delete rewrites, roles, audit.</summary>
public sealed class TagServiceTests
{
    private static async Task<StatusCode> CodeOf(Func<Task> call)
    {
        try
        {
            await call();
            return StatusCode.OK;
        }
        catch (RpcException ex)
        {
            return ex.StatusCode;
        }
    }

    private static async Task<List<string>> TagsOf(TestServerHost host, Guid id) =>
        (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!.Tags;

    private static Proto.SetDeviceTagsRequest Request(IEnumerable<Guid> ids, string[] add, string[]? remove = null)
    {
        var request = new Proto.SetDeviceTagsRequest();
        request.DeviceIds.AddRange(ids.Select(i => i.ToString()));
        request.Add.AddRange(add);
        request.Remove.AddRange(remove ?? []);
        return request;
    }

    [Fact]
    public async Task CreateValidatesTrimsAndKeepsNamesUniqueCaseInsensitive()
    {
        await using var host = await TestServerHost.StartAsync();

        var created = await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "  Building A  ", Color = Proto.TagColor.Blue });
        Assert.Equal("Building A", created.Name);
        Assert.Equal(Proto.TagColor.Blue, created.Color);
        Assert.True(created.Defined);
        Assert.True(Guid.TryParse(created.Id, out _));

        Assert.Equal(StatusCode.AlreadyExists, await CodeOf(async () => await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "building a" })));
        var empty = await Assert.ThrowsAsync<RpcException>(async () => await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "   " }));
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
        Assert.Equal("Enter a tag name.", empty.Status.Detail);
        var tooLong = await Assert.ThrowsAsync<RpcException>(async () => await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = new string('x', 33) }));
        Assert.Equal("A tag name has at most 32 characters.", tooLong.Status.Detail);
        Assert.Equal(StatusCode.InvalidArgument, await CodeOf(async () => await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "A;B" })));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = new string('x', 32) })));

        // Without a color: the next palette color.
        var next = await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "PTZ" });
        Assert.Equal(Proto.TagColor.Teal, next.Color);

        var list = (await host.Tags.ListAsync(new Proto.Empty())).Tags;
        Assert.Equal(["Building A", "PTZ", new string('x', 32)], list.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task SetDeviceTagsAddsAndRemovesOnTheSelectionAndCreatesMissingDefinitions()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        var c = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.3", 3);
        await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "PTZ", Color = Proto.TagColor.Amber });

        var reply = await host.Tags.SetDeviceTagsAsync(Request([a.Id, b.Id], ["ptz", "Outdoor"]));
        Assert.Equal(2, reply.DevicesChanged);
        var created = Assert.Single(reply.Created);
        Assert.Equal("Outdoor", created.Name);
        Assert.NotEqual(Proto.TagColor.Unspecified, created.Color);

        // The definition's spelling, sorted.
        Assert.Equal(["Outdoor", "PTZ"], await TagsOf(host, a.Id));
        Assert.Equal(["Outdoor", "PTZ"], await TagsOf(host, b.Id));
        Assert.Empty(await TagsOf(host, c.Id));

        // Adding again changes nothing; remove + add in one call.
        Assert.Equal(0, (await host.Tags.SetDeviceTagsAsync(Request([a.Id], ["PTZ"]))).DevicesChanged);
        Assert.Equal(1, (await host.Tags.SetDeviceTagsAsync(Request([b.Id], ["Building A"], ["PTZ"]))).DevicesChanged);
        Assert.Equal(["Building A", "Outdoor"], await TagsOf(host, b.Id));

        var list = (await host.Tags.ListAsync(new Proto.Empty())).Tags.ToDictionary(t => t.Name);
        Assert.Equal(2, list["Outdoor"].DeviceCount);
        Assert.Equal(1, list["PTZ"].DeviceCount);
        Assert.Equal(1, list["Building A"].DeviceCount);

        // The SDK sees the tags too.
        IDeviceInfo info = (await host.Get<DeviceRepository>().GetAsync(b.Id, CancellationToken.None))!;
        Assert.Equal(["Building A", "Outdoor"], info.Tags);

        // A name both added and removed, a bad name: nothing changes.
        Assert.Equal(StatusCode.InvalidArgument, await CodeOf(async () => await host.Tags.SetDeviceTagsAsync(Request([a.Id], ["X"], ["x"]))));
        Assert.Equal(StatusCode.InvalidArgument, await CodeOf(async () => await host.Tags.SetDeviceTagsAsync(Request([a.Id], ["a;b"]))));
        Assert.Equal(["Outdoor", "PTZ"], await TagsOf(host, a.Id));
    }

    [Fact]
    public async Task TaggingPublishesOneUpdatedChangePerChangedDevice()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        using var subscription = host.Get<DeviceRepository>().Changes.Subscribe();

        await host.Tags.SetDeviceTagsAsync(Request([a.Id, b.Id], ["Building A"]));

        var changes = new List<DeviceChange>();
        while (subscription.Reader.TryRead(out var change))
        {
            changes.Add(change);
        }

        Assert.Equal(2, changes.Count(c => c.Kind == DeviceChangeKind.Updated && c.Device!.Tags.SequenceEqual(["Building A"])));
    }

    [Fact]
    public async Task ADeviceHasAtMostTwentyTagsAndAFailedCallChangesNothing()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        await host.Tags.SetDeviceTagsAsync(Request([a.Id], [.. Enumerable.Range(1, 19).Select(i => $"T{i}")]));

        var error = await Assert.ThrowsAsync<RpcException>(async () => await host.Tags.SetDeviceTagsAsync(Request([b.Id, a.Id], ["New 1", "New 2"])));
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
        Assert.Equal("A device can have at most 20 tags. Nothing was changed.", error.Status.Detail);

        // Rolled back: device b untouched, no definition created.
        Assert.Empty(await TagsOf(host, b.Id));
        Assert.DoesNotContain((await host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name.StartsWith("New", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenameRewritesEveryDeviceAndRecolorKeepsTheName()
    {
        await using var host = await TestServerHost.StartAsync();
        var devices = new List<Device>();
        for (var i = 1; i <= 4; i++)
        {
            devices.Add(await DeviceServiceTests.AddDeviceAsync(host, $"10.9.0.{i}", i));
        }

        await host.Tags.SetDeviceTagsAsync(Request(devices.Take(3).Select(d => d.Id), ["Building A", "PTZ"]));
        await host.Tags.SetDeviceTagsAsync(Request([devices[3].Id], ["PTZ"]));

        var renamed = await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "building a", NewName = "Building B" });
        Assert.Equal("Building B", renamed.Name);
        foreach (var device in devices.Take(3))
        {
            Assert.Equal(["Building B", "PTZ"], await TagsOf(host, device.Id));
        }

        Assert.Equal(["PTZ"], await TagsOf(host, devices[3].Id));

        // A rename onto another tag's name fails and changes nothing.
        Assert.Equal(StatusCode.AlreadyExists, await CodeOf(async () => await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "Building B", NewName = "ptz" })));
        Assert.Equal(["Building B", "PTZ"], await TagsOf(host, devices[0].Id));

        var recolored = await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "PTZ", Color = Proto.TagColor.Red });
        Assert.Equal("PTZ", recolored.Name);
        Assert.Equal(Proto.TagColor.Red, recolored.Color);
        Assert.Equal(StatusCode.NotFound, await CodeOf(async () => await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "Nope", NewName = "X" })));

        // The definitions table and the device rows agree (one transaction each).
        await using var db = await host.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        Assert.Equal(["Building B", "PTZ"], (await db.TagDefinitions.Select(t => t.Name).ToListAsync()).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task DeleteRemovesTheTagFromEveryDevice()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        await host.Tags.SetDeviceTagsAsync(Request([a.Id, b.Id], ["PTZ", "Outdoor"]));

        var reply = await host.Tags.DeleteAsync(new Proto.TagName { Name = "ptz" });

        Assert.Equal(2, reply.DevicesChanged);
        Assert.Equal(["Outdoor"], await TagsOf(host, a.Id));
        Assert.Equal(["Outdoor"], await TagsOf(host, b.Id));
        Assert.DoesNotContain((await host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name == "PTZ");
        Assert.Equal(StatusCode.NotFound, await CodeOf(async () => await host.Tags.DeleteAsync(new Proto.TagName { Name = "PTZ" })));
    }

    [Fact]
    public async Task TagsOnDevicesWithoutADefinitionAreListedAsUndefinedAndCanBeDefined()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        await using (var db = await host.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Devices.SingleAsync(d => d.Id == a.Id);
            row.Tags = ["Legacy"];
            await db.SaveChangesAsync();
        }

        var legacy = Assert.Single((await host.Tags.ListAsync(new Proto.Empty())).Tags);
        Assert.Equal("Legacy", legacy.Name);
        Assert.False(legacy.Defined);
        Assert.Equal(Proto.TagColor.Unspecified, legacy.Color);
        Assert.Equal(1, legacy.DeviceCount);

        var defined = await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "legacy", Color = Proto.TagColor.Pink });
        Assert.True(defined.Defined);
        Assert.Equal(Proto.TagColor.Pink, defined.Color);
        Assert.True(Assert.Single((await host.Tags.ListAsync(new Proto.Empty())).Tags).Defined);
    }

    [Fact]
    public async Task WatchSendsTheListAndAgainAfterEveryDefinitionChange()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "PTZ" });
        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        using var call = host.Tags.Watch(new Proto.Empty(), cancellationToken: cts.Token);

        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(["PTZ"], call.ResponseStream.Current.Tags.Select(t => t.Name).ToArray());

        await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "Outdoor", Color = Proto.TagColor.Teal });
        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(["Outdoor", "PTZ"], call.ResponseStream.Current.Tags.Select(t => t.Name).ToArray());

        await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "PTZ", NewName = "Dome PTZ" });
        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(["Dome PTZ", "Outdoor"], call.ResponseStream.Current.Tags.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task OperatorsTagDevicesAndCreateTagsButOnlyAdministratorsRenameOrDelete()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var op = new Proto.TagService.TagServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));

        Assert.Equal(StatusCode.OK, await CodeOf(async () => await op.ListAsync(new Proto.Empty())));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await op.CreateAsync(new Proto.CreateTagRequest { Name = "PTZ" })));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await op.SetDeviceTagsAsync(Request([a.Id], ["PTZ", "Outdoor"]))));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await op.UpdateAsync(new Proto.UpdateTagRequest { Name = "PTZ", NewName = "X" })));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await op.UpdateAsync(new Proto.UpdateTagRequest { Name = "PTZ", Color = Proto.TagColor.Red })));
        Assert.Equal(StatusCode.PermissionDenied, await CodeOf(async () => await op.DeleteAsync(new Proto.TagName { Name = "PTZ" })));

        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "PTZ", Color = Proto.TagColor.Red })));
        Assert.Equal(StatusCode.OK, await CodeOf(async () => await host.Tags.DeleteAsync(new Proto.TagName { Name = "PTZ" })));
    }

    [Fact]
    public async Task TagChangesAreAudited()
    {
        await using var host = await TestServerHost.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var b = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2);
        await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "PTZ", Color = Proto.TagColor.Amber });
        await host.Tags.SetDeviceTagsAsync(Request([a.Id, b.Id], ["PTZ"]));
        await host.Tags.SetDeviceTagsAsync(Request([a.Id, b.Id], ["Building A"], ["PTZ"]));
        await host.Tags.UpdateAsync(new Proto.UpdateTagRequest { Name = "Building A", NewName = "Building B", Color = Proto.TagColor.Red });
        await host.Tags.DeleteAsync(new Proto.TagName { Name = "Building B" });

        var entries = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Reverse().ToList();
        Assert.Contains(entries, e => e.Action == AuditActions.TagCreated && e.Target == "PTZ" && e.Detail == "amber");
        Assert.Contains(entries, e => e.Action == AuditActions.TagCreated && e.Target == "Building A");
        Assert.Contains(entries, e => e.Action == AuditActions.DevicesTagged && e.Detail == "Tagged 2 devices: +PTZ");
        Assert.Contains(entries, e => e.Action == AuditActions.DevicesTagged && e.Detail == "Tagged 2 devices: +Building A -PTZ");
        Assert.Contains(entries, e => e.Action == AuditActions.TagRenamed && e.Target == "Building A" && e.Detail == "to Building B, 2 devices");
        Assert.Contains(entries, e => e.Action == AuditActions.TagRecolored && e.Target == "Building B" && e.Detail == "green to red");
        Assert.Contains(entries, e => e.Action == AuditActions.TagDeleted && e.Target == "Building B" && e.Detail == "removed from 2 devices");
        Assert.All(entries.Where(e => e.Action.Contains("tag", StringComparison.OrdinalIgnoreCase)), e => Assert.Equal("admin", e.UserName));
    }

    [Fact]
    public void TagNameRulesAndDefaultColors()
    {
        Assert.Null(TagNames.Problem("Building A"));
        Assert.Equal("Enter a tag name.", TagNames.Problem(null));
        Assert.Equal("A tag name cannot contain control characters.", TagNames.Problem("a\tb"));
        Assert.Equal(TagColor.Blue, TagNames.DefaultColor(0));
        Assert.Equal(TagColor.Violet, TagNames.DefaultColor(7));
        Assert.Equal(TagColor.Blue, TagNames.DefaultColor(8));
        Assert.Equal(["a", "B", "c"], TagNames.Canonical(["c", " B ", "a", "A", "b"]));
        Assert.Equal("+Building A -PTZ", TagNames.ChangeText(["Building A"], ["PTZ"]));
    }
}
