using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Events;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Api;

/// <summary>The fixes of the review round: bounded checks, the Device event node's devices, guards, the audit, the workers.</summary>
public sealed class ReviewFixesTests
{
    private const string Tamper = "oadm.device-triggers.tamper";
    private const string NtpDown = "oadm.ntp.down";

    private static PipelineNode DeviceEvent(string type, Guid? device = null) =>
        VisionHarness.Node("dev", NodeKinds.DeviceEvent, "Tampering", new JsonObject
        {
            [NodeKinds.EventKey] = new JsonObject { ["type"] = type, ["device"] = device?.ToString() ?? string.Empty },
        });

    private static Pipeline WithDeviceEvent(VisionHarness vision, string type, Guid? device = null, string name = "Gate 1")
    {
        var pipeline = vision.NewPipeline(name);
        return pipeline with { Nodes = [.. pipeline.Nodes, DeviceEvent(type, device)] };
    }

    private static void AddTypes(VisionHarness vision) => vision.Subscriptions.TypeList.AddRange(
    [
        new(new EventTypeInfo(Tamper, "Tampering", "Device triggers", EventKind.Stateful), "oadm.device-triggers", "Device triggers", true),
        new(new EventTypeInfo(NtpDown, "NTP down", "NTP", EventKind.Stateful, EventScope.Server), "oadm.ntp", "NTP", true),
    ]);

    private static Pipeline Oversized(VisionHarness vision, int nodes) => vision.NewPipeline() with
    {
        Nodes = [.. Enumerable.Range(0, nodes).Select(i => VisionHarness.Node("n" + i, NodeKinds.Filter, "Filter " + i))],
    };

    // (2) An oversized graph is never checked node by node, and a draft beyond the limits is refused before any check.
    [Fact]
    public void TheCheckStopsAtTooManyNodesOrLinks()
    {
        var nodes = Enumerable.Range(0, 2_000).Select(i => VisionHarness.Node("n" + i, NodeKinds.Filter, "Filter " + i)).ToList();
        var check = VisionGraph.Check(new Pipeline { Nodes = nodes }, _ => null);
        Assert.Equal([GraphTexts.TooManyNodes], check.GraphErrors);
        Assert.Empty(check.NodeErrors);

        var links = Enumerable.Range(0, VisionLimits.MaxLinks + 1).Select(i => new PipelineLink("a", "b")).ToList();
        check = VisionGraph.Check(new Pipeline { Nodes = [VisionHarness.Node("cam", NodeKinds.Camera, "Camera")], Links = links }, _ => null);
        Assert.Equal([GraphTexts.TooManyLinks], check.GraphErrors);
    }

    [Fact]
    public async Task AnOversizedDraftIsRefusedBeforeAnyCheck()
    {
        var checks = 0;
        var vision = new VisionHarness();
        var plugin = new VisionPlugin(vision.Parts with
        {
            Check = (p, find) =>
            {
                Interlocked.Increment(ref checks);
                return FakeCheck.Check(p, find);
            },
        });
        await plugin.StartAsync(vision.Context, CancellationToken.None);
        try
        {
            var call = vision.Context.CallAsAllDevices();
            var save = VisionJson.Deserialize<SavePipelineReply>(await vision.Context.InvokeAsync(plugin, call, VisionMethods.SavePipeline, VisionJson.Serialize(new SavePipelineRequest { Pipeline = Oversized(vision, 1_000) })));
            Assert.Equal([GraphTexts.TooManyNodes], save.GraphErrors);
            var graph = VisionJson.Deserialize<GraphCheck>(await vision.Context.InvokeAsync(plugin, call, VisionMethods.CheckGraph, VisionJson.Serialize(new CheckGraphRequest { Pipeline = Oversized(vision, 1_000) })));
            Assert.Equal([GraphTexts.TooManyNodes], graph.GraphErrors);
            var preview = await Assert.ThrowsAsync<ArgumentException>(() => vision.Context.InvokeAsync(plugin, call, VisionMethods.StartPreview, VisionJson.Serialize(new PreviewRequest { Pipeline = Oversized(vision, 1_000) })));
            Assert.Equal(GraphTexts.TooManyNodes, preview.Message);

            var huge = vision.NewPipeline();
            huge = huge with { Nodes = [.. huge.Nodes.Select(n => n.Id == "det" ? n with { Settings = new JsonObject { [NodeKinds.ModelKey] = "m1", ["x"] = new string('a', VisionLimits.MaxSettingsLength) } } : n)] };
            save = VisionJson.Deserialize<SavePipelineReply>(await vision.Context.InvokeAsync(plugin, call, VisionMethods.SavePipeline, VisionJson.Serialize(new SavePipelineRequest { Pipeline = huge })));
            Assert.Equal([PipelineRules.SettingsTooLarge], save.GraphErrors);
            Assert.Equal(0, Volatile.Read(ref checks));
        }
        finally
        {
            await plugin.DisposeAsync();
        }
    }

    // (3) A preview runs the Device event checks of a save.
    [Fact]
    public async Task APreviewChecksItsDeviceEventsLikeASave()
    {
        await using var vision = await VisionHarness.StartAsync();
        AddTypes(vision);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]));

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => vision.PreviewAsync(WithDeviceEvent(vision, Tamper, vision.Outside.Id), Guid.NewGuid().ToString("N"), group));
        var own = await Assert.ThrowsAsync<ArgumentException>(() => vision.PreviewAsync(WithDeviceEvent(vision, VisionInfo.AnyPipelineProblemTypeId, vision.Inside.Id), Guid.NewGuid().ToString("N")));
        Assert.Equal(EventStateBook.OwnEventRefused, own.Message);
        var server = await Assert.ThrowsAsync<ArgumentException>(() => vision.PreviewAsync(WithDeviceEvent(vision, NtpDown), Guid.NewGuid().ToString("N"), group));
        Assert.Equal(EventStateBook.OwnEventRefused, server.Message);
        Assert.Empty(vision.Runtime.Previews);
    }

    // (4) A per-device event names its device; without one it follows no device at all.
    [Fact]
    public async Task APerDeviceEventNeedsItsDevice()
    {
        await using var vision = await VisionHarness.StartAsync();
        AddTypes(vision);
        var reply = await vision.TrySaveAsync(WithDeviceEvent(vision, Tamper));
        Assert.Equal(EventStateBook.DeviceMissing, reply.Errors[PipelineFields.Node("dev")]);
        Assert.NotNull((await vision.TrySaveAsync(WithDeviceEvent(vision, NtpDown))).Pipeline); // a server event has none

        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => vision.PreviewAsync(WithDeviceEvent(vision, Tamper, name: "Draft"), Guid.NewGuid().ToString("N"), group));
        Assert.Equal(EventStateBook.DeviceMissing, refused.Message);

        var subscriptions = new FakeSubscriptions();
        using var book = new EventStateBook(() => subscriptions);
        book.Follow(true);
        subscriptions.Publish(Tamper, vision.Inside.Id, PublishedEventKind.On);
        subscriptions.Publish(NtpDown, null, PublishedEventKind.On);
        Assert.False(book.IsOn(Tamper, null));
        Assert.True(book.IsOn(NtpDown, null));
    }

    // (10) Run pipeline's own picture of a watching pipeline: one at a time.
    [Fact]
    public async Task RunPipelineTakesOneOwnPictureAtATime()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();
        using var gate = new SemaphoreSlim(0);
        vision.Frames.SingleGate = gate;

        var first = vision.Plugin.RunOnceAsync(saved, CancellationToken.None);
        await Eventually.TrueAsync(() => vision.Frames.Singles == 1, "the first run takes its picture");
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => vision.Plugin.RunOnceAsync(saved, CancellationToken.None));
        Assert.Equal(PipelineRules.AlreadyRunning, second.Message);

        gate.Release(5);
        Assert.NotNull(await first);
        Assert.NotNull(await vision.Plugin.RunOnceAsync(saved, CancellationToken.None)); // free again
    }

    // (11) A refused save is audited as not saved, and the audit's names never pile up.
    [Fact]
    public async Task ARefusedSaveIsNotAuditedAsSavedAndTheNamesStayBounded()
    {
        await using var vision = await VisionHarness.StartAsync();
        var refused = vision.NewPipeline("Gate 1") with { Fps = 7 };
        await vision.TrySaveAsync(refused);
        Assert.Equal("Could not save vision pipeline Gate 1", vision.Plugin.AuditDetail(VisionMethods.SavePipeline, VisionJson.Serialize(new SavePipelineRequest { Pipeline = refused })));

        var saved = vision.NewPipeline("Gate 2");
        await vision.SaveAsync(saved);
        Assert.Equal("Saved vision pipeline Gate 2", vision.Plugin.AuditDetail(VisionMethods.SavePipeline, VisionJson.Serialize(new SavePipelineRequest { Pipeline = saved })));
        Assert.Equal(0, vision.Plugin.AuditNamesKept);

        for (var i = 0; i < VisionPlugin.MaxAuditNames + 50; i++)
        {
            await vision.TrySaveAsync(vision.NewPipeline("P" + i) with { Fps = 7 });
        }

        Assert.True(vision.Plugin.AuditNamesKept <= VisionPlugin.MaxAuditNames);
    }

    // (12) A throwing step never leaves the worker stuck, and its stop never throws.
    [Fact]
    public async Task AFailingStepEndsTheWorkerAndTheNextKickRunsAgain()
    {
        var gate = new object();
        var failures = new List<Exception>();
        var worker = new LatestWinsWorker(gate, failures.Add);
        var runs = 0;
        var due = true;
        worker.Kick(() => due, _ =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("broken");
        });
        await Eventually.TrueAsync(() => failures.Count == 1, "the step failed and was told");

        var ran = new TaskCompletionSource();
        worker.Kick(() => due, _ =>
        {
            due = false;
            ran.TrySetResult();
            return Task.CompletedTask;
        });
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await worker.StopAsync();
        Assert.Equal(1, runs);
    }

    // (13) An administrator's edit keeps a group's pipeline within the group's devices, its Device event nodes too.
    [Fact]
    public async Task AnAdministratorCannotPutAnotherDeviceIntoAGroupsPipeline()
    {
        await using var vision = await VisionHarness.StartAsync();
        AddTypes(vision);
        var key = vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", key);
        var mine = await vision.SaveAsync(WithDeviceEvent(vision, Tamper, vision.Inside.Id), group);

        var edit = mine with { Nodes = [.. mine.Nodes.Where(n => n.Id != "dev"), DeviceEvent(Tamper, vision.Outside.Id)] };
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => vision.SaveAsync(edit));
        Assert.Equal(vision.Inside.Id, EventStateBook.ChoiceOf(vision.Plugin.FindPipeline(mine.Id)!.Nodes.Single(n => n.Id == "dev")).Device);
    }

    // (15) A late event of an old subscription never lands after a new start.
    [Fact]
    public void ALateEventOfAnOldSubscriptionIsIgnored()
    {
        var subscriptions = new FakeSubscriptions();
        using var book = new EventStateBook(() => subscriptions);
        var camera = Guid.NewGuid();
        book.Follow(true);
        var old = subscriptions.Ever[^1];
        book.Follow(false);
        book.Follow(true);

        old(new PublishedEvent(Tamper, camera, PublishedEventKind.On, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch));
        Assert.False(book.IsOn(Tamper, camera));
        subscriptions.Publish(Tamper, camera, PublishedEventKind.On);
        Assert.True(book.IsOn(Tamper, camera));
    }
}
