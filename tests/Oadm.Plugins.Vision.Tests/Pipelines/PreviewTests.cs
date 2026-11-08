using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Pipelines;

/// <summary>The page's previews (docs/spec/vision-center.md "Previews"): leases, limits, sharing, no events, Pause.</summary>
public sealed class PreviewTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task APreviewRunsTheDraftWithEveryNodeAndPublishesNoEvent()
    {
        await using var vision = await VisionHarness.StartAsync();
        var draft = vision.NewPipeline();
        var id = NewId();
        var reply = await vision.PreviewAsync(draft, id);
        Assert.Equal(id, reply.PreviewId);
        Assert.Null(reply.Problem);
        await vision.StreamingAsync();

        vision.Frames.Send(vision.Inside.Id, "hit");
        await Eventually.TrueAsync(() => vision.Runtime.KeptPreviewResults >= 1, "the preview ran the frame");
        Assert.True(vision.Evaluators.Previews.Single().Preview, "the preview asks for every node's output");
        Assert.DoesNotContain(vision.Publisher.Published, e => e.TypeId.StartsWith(VisionInfo.PluginId + ".event.", StringComparison.Ordinal));

        var state = vision.Runtime.PreviewStateOf(id)!;
        Assert.Equal(["cam", "det", "ev"], state.Nodes.Select(n => n.NodeId));
        Assert.Equal("Fired · Direction In", state.RecentFires["ev"].Single().Text);
        Assert.True(state.Check!.Ok);
    }

    [Fact]
    public async Task ItsStateReachesOnlyItsUserAtMostEveryHalfSecond()
    {
        await using var vision = await VisionHarness.StartAsync();
        var me = vision.Context.CallAsAllDevices("anna");
        var id = NewId();
        await vision.PreviewAsync(vision.NewPipeline(), id, me);
        await vision.StreamingAsync();

        vision.Time.Advance(VisionLimits.LiveEvery); // the draft's check
        var before = vision.Runtime.SentPreviewEvents;
        vision.Frames.Send(vision.Inside.Id, "A");
        await Eventually.TrueAsync(() => vision.Runtime.KeptPreviewResults >= 1, "the first frame ran");
        vision.Frames.Send(vision.Inside.Id, "B");
        await Eventually.TrueAsync(() => vision.Runtime.KeptPreviewResults >= 2, "the second frame ran");
        Assert.Equal(before, vision.Runtime.SentPreviewEvents);

        vision.Time.Advance(VisionLimits.LiveEvery);
        Assert.Equal(before + 1, vision.Runtime.SentPreviewEvents);
        var sent = vision.Context.PublishedEvents.Last(e => e.Topic == VisionMethods.PreviewTopic);
        Assert.Equal(PluginEventAudienceKind.User, sent.Audience.Kind);
        Assert.Equal("anna", sent.Audience.UserName);
        Assert.Equal("B", VisionJson.Deserialize<PreviewEvent>(sent.PayloadJson).Nodes.Single(n => n.NodeId == "det").Live);
    }

    [Fact]
    public async Task AnUnchangedSavedWatchingPipelineSharesItsStream()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.StreamingAsync();

        await vision.PreviewAsync(saved with { Name = "Renamed in the page" }, NewId());
        Assert.Equal(saved.Id, vision.Runtime.Previews.Single().SharedWith);
        Assert.Equal(1, vision.Frames.Opened);

        var changed = saved with { Fps = 4 };
        await vision.PreviewAsync(changed, vision.Runtime.Previews.Single().Id);
        Assert.True(vision.Runtime.Previews.Single().OwnStream);
        await vision.StreamingAsync(2);
    }

    [Fact]
    public async Task ADraftWithAGraphErrorStreamsNothingButGetsItsCheck()
    {
        await using var vision = await VisionHarness.StartAsync();
        var draft = vision.NewPipeline() with { Nodes = [.. vision.NewPipeline().Nodes.Where(n => n.Kind != NodeKinds.Camera)] };
        var id = NewId();
        await vision.PreviewAsync(draft, id);

        Assert.Equal(0, vision.Frames.Opened);
        Assert.Equal(["Add a camera."], vision.Runtime.PreviewStateOf(id)!.Check!.GraphErrors);
    }

    [Fact]
    public async Task ThePreviewEndsThirtySecondsAfterThePagesLastCall()
    {
        await using var vision = await VisionHarness.StartAsync();
        var id = NewId();
        await vision.PreviewAsync(vision.NewPipeline(), id);
        vision.Time.Advance(TimeSpan.FromSeconds(20));
        await vision.PreviewAsync(vision.NewPipeline(), id); // the keep-alive
        vision.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Single(vision.Runtime.Previews);

        vision.Time.Advance(TimeSpan.FromSeconds(15));
        await Eventually.TrueAsync(() => vision.Runtime.Previews.Count == 0, "the lease ended");
        await Eventually.TrueAsync(() => vision.Frames.Open == 0, "its stream stopped");
    }

    [Fact]
    public async Task AUserHoldsTwoPreviewsAndTheServerEight()
    {
        await using var vision = await VisionHarness.StartAsync(processorCount: 64);
        var first = NewId();
        await vision.PreviewAsync(vision.NewPipeline(), first);
        await vision.PreviewAsync(vision.NewPipeline(), NewId());
        await vision.PreviewAsync(vision.NewPipeline(), NewId());
        Assert.Equal(2, vision.Runtime.Previews.Count);
        Assert.DoesNotContain(vision.Runtime.Previews, p => p.Id == first);

        for (var i = 0; i < 3; i++)
        {
            var user = vision.Context.CallAsAllDevices("user" + i);
            await vision.PreviewAsync(vision.NewPipeline(), NewId(), user);
            await vision.PreviewAsync(vision.NewPipeline(), NewId(), user);
        }

        Assert.Equal(VisionLimits.MaxPreviews, vision.Runtime.Previews.Count);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => vision.PreviewAsync(vision.NewPipeline(), NewId(), vision.Context.CallAsAllDevices("late")));
        Assert.Equal(PipelineRules.TooManyPreviews, error.Message);
    }

    [Fact]
    public async Task OwnStreamsCountAgainstTheWatchingLimitAfterTheSavedPipelines()
    {
        await using var vision = await VisionHarness.StartAsync(processorCount: 2);
        var cameras = Enumerable.Range(0, 4).Select(i => vision.Context.Add(new FakeDevice { Address = "10.0.3." + i })).ToList();
        for (var i = 0; i < 3; i++)
        {
            await vision.SaveAsync(vision.NewPipeline("P" + i, device: cameras[i].Id));
        }

        var own = await vision.PreviewAsync(vision.NewPipeline("Draft", device: cameras[3].Id), NewId());
        Assert.Null(own.Problem);
        var busy = await vision.PreviewAsync(vision.NewPipeline("Draft 2", device: vision.Outside.Id), NewId());
        Assert.Equal(PipelineRules.PreviewBusy, busy.Problem);
    }

    [Fact]
    public async Task PauseHoldsTheFrameAndRunsTheDraftAgainAfterEveryChange()
    {
        await using var vision = await VisionHarness.StartAsync();
        var id = NewId();
        var draft = vision.NewPipeline();
        await vision.PreviewAsync(draft, id);
        await vision.StreamingAsync();
        vision.Frames.Send(vision.Inside.Id, "frame 1");
        await Eventually.TrueAsync(() => vision.Runtime.KeptPreviewResults >= 1, "the frame ran");

        var pause = VisionJson.Deserialize<PauseReply>(await vision.CallAsync(VisionMethods.PausePreview, new PreviewRequest { PreviewId = id }));
        Assert.True(pause.Paused);
        Assert.Equal("frame 1", Frame.TextOf(pause.Picture));
        Assert.Equal("frame 1", pause.State!.Nodes.Single(n => n.NodeId == "det").Live);
        await Eventually.TrueAsync(() => vision.Frames.Open == 0, "its stream stopped");

        var tuned = draft with { Nodes = [.. draft.Nodes.Select(n => n.Id == "det" ? n with { Settings = new JsonObject { [NodeKinds.ModelKey] = "m1", [NodeKinds.MinScoreKey] = 80 } } : n)] };
        var kept = vision.Runtime.KeptPreviewResults;
        var reply = await vision.PreviewAsync(tuned, id);
        Assert.True(reply.Paused);
        await Eventually.TrueAsync(() => vision.Runtime.KeptPreviewResults > kept, "the new draft ran on the held frame");
        var rerun = vision.Evaluators.Previews[^1];
        Assert.Equal(["frame 1"], rerun.Frames);
        Assert.Equal(80, rerun.Pipeline.Nodes[1].Settings[NodeKinds.MinScoreKey]!.GetValue<int>());

        await vision.CallAsync(VisionMethods.ResumePreview, new PreviewRequest { PreviewId = id });
        await vision.StreamingAsync();
        Assert.False(vision.Runtime.PreviewStateOf(id)!.Paused);
    }

    [Fact]
    public async Task PauseNeedsLiveViewAndAPicture()
    {
        await using var vision = await VisionHarness.StartAsync();
        var id = NewId();
        await vision.PreviewAsync(vision.NewPipeline(), id);
        var none = VisionJson.Deserialize<PauseReply>(await vision.CallAsync(VisionMethods.PausePreview, new PreviewRequest { PreviewId = id }));
        Assert.Equal(PipelineRules.NothingToPause, none.Problem);

        var limited = new FakeCallContextWithout(vision.Context.CallAsAllDevices(), HostPermissionKeys.DevicesLiveView);
        var error = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => vision.Context.InvokeAsync(vision.Plugin, limited, VisionMethods.PausePreview, VisionJson.Serialize(new PreviewRequest { PreviewId = id })));
        Assert.Equal(PipelineRules.PauseNeedsLiveView, error.Message);
    }

    [Fact]
    public async Task AnotherUsersPreviewIsNotTheirs()
    {
        await using var vision = await VisionHarness.StartAsync();
        var id = NewId();
        await vision.PreviewAsync(vision.NewPipeline(), id, vision.Context.CallAsAllDevices("anna"));
        await vision.CallAsync(VisionMethods.StopPreview, new PreviewRequest { PreviewId = id }, vision.Context.CallAsAllDevices("bert"));
        Assert.Single(vision.Runtime.Previews);

        await vision.CallAsync(VisionMethods.StopPreview, new PreviewRequest { PreviewId = id }, vision.Context.CallAsAllDevices("anna"));
        Assert.Empty(vision.Runtime.Previews);
    }
}
