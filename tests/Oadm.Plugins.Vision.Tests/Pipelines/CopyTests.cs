using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Vision.Tests.Pipelines;

/// <summary>Copy a pipeline to other cameras (docs/spec/vision-center.md "Copy to cameras"): independent copies, refused per camera.</summary>
public sealed class CopyTests
{
    /// <summary>A camera that offers its video sources and sizes.</summary>
    private sealed class OptionsCamera(params Resolution[] sizes) : FakeCamera, IVapixClient
    {
        public Task<IReadOnlyList<VideoSource>> GetVideoSourcesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VideoSource>>([new VideoSource(1, "View 1", 0, [.. sizes.Select(s => new VideoResolution(s.Width, s.Height))]), new VideoSource(2, "View 2", 0, [])]);
    }

    private static async Task<CopyPipelineReply> CopyAsync(VisionHarness vision, string pipelineId, IEnumerable<Guid> cameras, FakeCallContext? caller = null) =>
        VisionJson.Deserialize<CopyPipelineReply>(await vision.CallAsync(VisionMethods.CopyPipeline, new CopyPipelineRequest { PipelineId = pipelineId, DeviceIds = [.. cameras] }, caller));

    private static Pipeline Lined(VisionHarness vision)
    {
        var line = VisionHarness.Node("ln", NodeKinds.Line, "Door", new JsonObject
        {
            [NodeKinds.PointsKey] = new JsonArray(new JsonObject { ["x"] = 0.2, ["y"] = 0.5 }, new JsonObject { ["x"] = 0.8, ["y"] = 0.5 }),
        });
        return vision.NewPipeline("Gate 1 people", tail: [line, VisionHarness.Node("ev", NodeKinds.Event, "Crossed")]) with
        {
            VideoSource = 2,
            Resolution = new Resolution(1920, 1080),
            Fps = 4,
        };
    }

    [Fact]
    public async Task EachCameraGetsAnIndependentCopyWithItsNameAndItsOwnPictureSize()
    {
        await using var vision = await VisionHarness.StartAsync();
        var offers = vision.Context.Add(new FakeDevice { Address = "10.0.0.3", HostName = "lobby-cam" }, new OptionsCamera(new Resolution(1920, 1080), new Resolution(1280, 720)));
        var smaller = vision.Context.Add(new FakeDevice { Address = "10.0.0.4" }, new OptionsCamera(new Resolution(1024, 768), new Resolution(800, 600)));
        var unknown = vision.Context.Add(new FakeDevice { Address = "10.0.0.5" });
        var original = await vision.SaveAsync(Lined(vision));
        var changes = 0;
        vision.Plugin.EventTypesChanged += (_, _) => changes++;
        var listsBefore = vision.Context.PublishedEvents.Count(e => e.Topic == VisionMethods.PipelinesTopic);

        var reply = await CopyAsync(vision, original.Id, [offers.Id, smaller.Id, unknown.Id]);

        Assert.Equal(["Gate 1 people (lobby-cam)", "Gate 1 people (10.0.0.4)", "Gate 1 people (10.0.0.5)"], reply.Created.Select(p => p.Name));
        Assert.Empty(reply.Refused);
        Assert.All(reply.Created, p =>
        {
            Assert.True(PipelineRules.IsId(p.Id));
            Assert.NotEqual(original.Id, p.Id);
            Assert.Equal(original.Fps, p.Fps);
            Assert.Equal(original.Mode, p.Mode);
            Assert.Equal(original.Nodes.Select(n => n.Id), p.Nodes.Select(n => n.Id));
            Assert.Equal(original.Links, p.Links);
            Assert.Equal(0.2, p.Nodes.Single(n => n.Id == "ln").Settings[NodeKinds.PointsKey]![0]!["x"]!.GetValue<double>());
        });
        Assert.Equal((2, new Resolution(1920, 1080)), (reply.Created[0].VideoSource, reply.Created[0].Resolution)); // offered: kept
        Assert.Equal((2, new Resolution(1024, 768)), (reply.Created[1].VideoSource, reply.Created[1].Resolution)); // the largest below 1280x720
        Assert.Equal((1, Resolution.Default), (reply.Created[2].VideoSource, reply.Created[2].Resolution)); // no options: the defaults
        Assert.Equal(4, vision.Plugin.AllPipelines.Count);
        Assert.Equal(1, changes);
        Assert.Equal(listsBefore + 1, vision.Context.PublishedEvents.Count(e => e.Topic == VisionMethods.PipelinesTopic));
        Assert.Equal("Copied vision pipeline Gate 1 people to 3 cameras", vision.Plugin.AuditDetail(VisionMethods.CopyPipeline, VisionJson.Serialize(new CopyPipelineRequest { PipelineId = original.Id })));
    }

    [Fact]
    public async Task NamesStayUniqueAndItsOwnCameraIsRefused()
    {
        await using var vision = await VisionHarness.StartAsync();
        var original = await vision.SaveAsync(vision.NewPipeline("Gate"));
        await vision.SaveAsync(vision.NewPipeline("Gate (10.0.0.2)", PipelineMode.OnAction));

        var reply = await CopyAsync(vision, original.Id, [vision.Inside.Id, vision.Outside.Id, vision.Outside.Id]);
        Assert.Equal(PipelineRules.OwnCamera, reply.Refused[vision.Inside.Id]);
        Assert.Equal(["Gate (10.0.0.2) 2"], reply.Created.Select(p => p.Name));
    }

    [Fact]
    public async Task AsManyCopiesAsFitTheLimitAreMade()
    {
        await using var vision = await VisionHarness.StartAsync();
        var original = await vision.SaveAsync(vision.NewPipeline("Gate", PipelineMode.OnAction));
        for (var i = 0; i < VisionLimits.MaxPipelines - 3; i++)
        {
            await vision.SaveAsync(vision.NewPipeline("P" + i, PipelineMode.OnAction));
        }

        var cameras = Enumerable.Range(0, 4).Select(i => vision.Context.Add(new FakeDevice { Address = "10.0.4." + i }).Id).ToList();
        var reply = await CopyAsync(vision, original.Id, cameras);
        Assert.Equal(2, reply.Created.Count);
        Assert.Equal([PipelineRules.CopyLimit, PipelineRules.CopyLimit], cameras.Skip(2).Select(c => reply.Refused[c]));
        Assert.Equal(VisionLimits.MaxPipelines, vision.Plugin.AllPipelines.Count);
    }

    [Fact]
    public async Task ACopyRunsTheChecksOfASave()
    {
        await using var vision = await VisionHarness.StartAsync();
        var original = await vision.SaveAsync(vision.NewPipeline());
        await vision.Models.DeleteAsync("m1", [], CancellationToken.None);

        var reply = await CopyAsync(vision, original.Id, [vision.Outside.Id]);
        Assert.Empty(reply.Created);
        Assert.Equal("Choose a model.", reply.Refused[vision.Outside.Id]);
    }

    [Fact]
    public async Task AGroupCopiesOnlyItsPipelinesToItsCameras()
    {
        await using var vision = await VisionHarness.StartAsync();
        var third = vision.Context.Add(new FakeDevice { Address = "10.0.0.6" });
        var key = vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id, third.Id]);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id, third.Id], "operator", key);
        var mine = await vision.SaveAsync(vision.NewPipeline("Gate"), group);
        var admins = await vision.SaveAsync(vision.NewPipeline("Lobby", device: vision.Outside.Id));

        var reply = await CopyAsync(vision, mine.Id, [third.Id, vision.Outside.Id], group);
        Assert.Equal([third.Id], reply.Created.Select(p => p.DeviceId));
        Assert.Equal(PipelineRules.CameraNotAvailable, reply.Refused[vision.Outside.Id]);
        Assert.Equal(key, vision.Plugin.FindPipeline(reply.Created[0].Id)!.OwnerScopeKey);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => CopyAsync(vision, admins.Id, [third.Id], group));
        Assert.Equal(PipelineRules.Gone, error.Message);
    }

    [Fact]
    public async Task ACopyBeyondTheWatchingLimitIsStoredAndSaysWhy()
    {
        await using var vision = await VisionHarness.StartAsync(processorCount: 2);
        var original = await vision.SaveAsync(vision.NewPipeline("Gate"));
        var cameras = Enumerable.Range(0, 4).Select(i => vision.Context.Add(new FakeDevice { Address = "10.0.5." + i }).Id).ToList();

        var reply = await CopyAsync(vision, original.Id, cameras);
        Assert.Equal(4, reply.Created.Count);
        Assert.Single(vision.Plugin.AllPipelines, p => vision.Runtime.ProblemOf(p.Id) == PipelineRules.TooManyWatching(4));
    }

    [Fact]
    public async Task AtMost100CamerasPerCallAndTheRightIsChange()
    {
        await using var vision = await VisionHarness.StartAsync();
        var original = await vision.SaveAsync(vision.NewPipeline());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => CopyAsync(vision, original.Id, Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())));
        Assert.Equal(PipelineRules.TooManyCopyCameras, error.Message);
        Assert.Equal(VisionInfo.ChangePermission, vision.Plugin.RequiredPermission(VisionMethods.CopyPipeline));
        Assert.True(vision.Plugin.IsAudited(VisionMethods.CopyPipeline));
    }
}
