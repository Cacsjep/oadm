using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Events;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Events;

/// <summary>The runtime side of the Device event node: the event rules' states, followed only while needed, never its own.</summary>
public sealed class DeviceEventTests
{
    private const string Tamper = "oadm.device-triggers.tamper";

    private static PipelineNode DeviceEvent(string type, Guid? device = null) =>
        VisionHarness.Node("dev", NodeKinds.DeviceEvent, "Tampering", new JsonObject
        {
            [NodeKinds.EventKey] = new JsonObject { ["type"] = type, ["device"] = device?.ToString() ?? string.Empty },
        });

    private static Pipeline WithDeviceEvent(VisionHarness vision, string type, Guid? device = null)
    {
        var pipeline = vision.NewPipeline();
        return pipeline with { Nodes = [.. pipeline.Nodes, DeviceEvent(type, device)] };
    }

    [Fact]
    public void TheBookKeepsOnAndOffPerTypeAndDeviceAndNeverItsOwnEvents()
    {
        var subscriptions = new FakeSubscriptions();
        using var book = new EventStateBook(() => subscriptions);
        book.Follow(true);
        var camera = Guid.NewGuid();

        subscriptions.Publish(Tamper, camera, PublishedEventKind.On);
        subscriptions.Publish("oadm.vision.event.p.ev.objectspresent", camera, PublishedEventKind.On);
        subscriptions.Publish("oadm.device.unreachable", null, PublishedEventKind.Fired);
        Assert.True(book.IsOn(Tamper, camera));
        Assert.True(book.IsOn(Tamper, null));
        Assert.False(book.IsOn(Tamper, Guid.NewGuid()));
        Assert.False(book.IsOn("oadm.vision.event.p.ev.objectspresent", camera));

        subscriptions.Publish(Tamper, camera, PublishedEventKind.Off);
        Assert.False(book.IsOn(Tamper, null));

        book.Follow(false);
        Assert.Equal(0, subscriptions.Open);
    }

    [Fact]
    public async Task TheEventsAreFollowedOnlyWhileAPipelineHasADeviceEventNode()
    {
        await using var vision = await VisionHarness.StartAsync();
        await vision.SaveAsync(vision.NewPipeline());
        Assert.Equal(0, vision.Subscriptions.Open);

        var saved = await vision.SaveAsync(WithDeviceEvent(vision, Tamper, vision.Inside.Id) with { Name = "Gate 2" });
        Assert.Equal(1, vision.Subscriptions.Open);
        vision.Subscriptions.Publish(Tamper, vision.Inside.Id, PublishedEventKind.On);
        Assert.True(vision.Runtime.EventStates.IsOn(Tamper, vision.Inside.Id));

        await vision.CallAsync(VisionMethods.DeletePipeline, new PipelineRequest { PipelineId = saved.Id });
        Assert.Equal(0, vision.Subscriptions.Open);
    }

    [Fact]
    public async Task APreviewWithADeviceEventNodeFollowsTheEventsToo()
    {
        await using var vision = await VisionHarness.StartAsync();
        await vision.PreviewAsync(WithDeviceEvent(vision, Tamper), Guid.NewGuid().ToString("N"));
        Assert.Equal(1, vision.Subscriptions.Open);
    }

    [Fact]
    public async Task ItsOwnEventsAreRefusedAtTheSave()
    {
        await using var vision = await VisionHarness.StartAsync();
        var reply = await vision.TrySaveAsync(WithDeviceEvent(vision, VisionInfo.AnyPipelineProblemTypeId));
        Assert.Equal(EventStateBook.OwnEventRefused, reply.Errors[PipelineFields.Node("dev")]);
        Assert.Empty(vision.Plugin.AllPipelines);
    }

    [Fact]
    public async Task TheEvaluatorsGetTheStatesThroughTheSeam()
    {
        IEventStates? handed = null;
        var vision = new VisionHarness();
        var plugin = new VisionPlugin(vision.Parts with
        {
            EvaluatorWithStates = (p, find, inference, mode, states, preview) =>
            {
                handed = states;
                return vision.Evaluators.Create(p, find, inference, mode);
            },
        });
        await plugin.StartAsync(vision.Context, CancellationToken.None);
        try
        {
            await vision.Context.InvokeAsync(plugin, vision.Context.CallAsAllDevices(), VisionMethods.SavePipeline, VisionJson.Serialize(new SavePipelineRequest { Pipeline = WithDeviceEvent(vision, Tamper) }));
            await Eventually.TrueAsync(() => handed is not null, "the evaluator got the states");
            Assert.Same(plugin.Runtime!.EventStates, handed);
        }
        finally
        {
            await plugin.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheListOffersTheStatefulEventsOfOthersAndAGroupOnlyThoseOfItsDevices()
    {
        await using var vision = await VisionHarness.StartAsync();
        vision.Subscriptions.TypeList.AddRange(
        [
            new(new EventTypeInfo(Tamper, "Tampering", "Device triggers", EventKind.Stateful), "oadm.device-triggers", "Device triggers", true),
            new(new EventTypeInfo("oadm.device.offline", "Device offline", "Devices", EventKind.Stateful, EventScope.Device) { DeviceIds = [vision.Outside.Id] }, "oadm.device", "Built in", true),
            new(new EventTypeInfo("oadm.device.restarted", "Restarted", "Devices", EventKind.Stateless), "oadm.device", "Built in", true),
            new(new EventTypeInfo("oadm.ntp.down", "NTP down", "NTP", EventKind.Stateful, EventScope.Server), "oadm.ntp", "NTP", false),
            new(new EventTypeInfo(VisionInfo.AnyPipelineProblemTypeId, "Any pipeline problem", "Vision Center", EventKind.Stateful, EventScope.Server), "oadm.vision", "Vision Center", true),
        ]);

        var all = VisionJson.Deserialize<EventTypeList>(await vision.CallAsync(VisionMethods.ListEventTypes));
        Assert.Equal([Tamper, "oadm.device.offline", "oadm.ntp.down"], all.Types.Select(t => t.Id));
        Assert.False(all.Types.Single(t => t.Id == "oadm.ntp.down").PerDevice);
        Assert.False(all.Types.Single(t => t.Id == "oadm.ntp.down").Available);
        Assert.Equal(["Device triggers"], all.Types[0].Path);

        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]));
        var mine = VisionJson.Deserialize<EventTypeList>(await vision.CallAsync(VisionMethods.ListEventTypes, caller: group));
        Assert.Equal([Tamper], mine.Types.Select(t => t.Id));

        var reply = await vision.TrySaveAsync(WithDeviceEvent(vision, "oadm.ntp.down"), group);
        Assert.Equal(EventStateBook.OwnEventRefused, reply.Errors[PipelineFields.Node("dev")]);
    }
}
