using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Pipelines;

/// <summary>The save of a pipeline (docs/spec/vision-center.md "Pipelines"): fields, the graph check, scopes and limits; nothing stored on an error.</summary>
public sealed class PipelineStoreTests
{
    [Fact]
    public async Task ASaveChecksEveryFieldAndStoresNothingOnAnError()
    {
        await using var vision = await VisionHarness.StartAsync();
        var reply = await vision.TrySaveAsync(vision.NewPipeline(" ") with { Fps = 7, VideoSource = 0, Resolution = new Resolution(20, 20) });

        Assert.Null(reply.Pipeline);
        Assert.Equal(PipelineRules.NameMissing, reply.Errors[PipelineFields.Name]);
        Assert.Equal(PipelineRules.FpsNotAllowed(), reply.Errors[PipelineFields.Fps]);
        Assert.Equal(PipelineRules.SourceMissing, reply.Errors[PipelineFields.VideoSource]);
        Assert.Equal(PipelineRules.ResolutionMissing, reply.Errors[PipelineFields.Resolution]);
        Assert.Empty(vision.Plugin.AllPipelines);
        Assert.Equal(0, vision.Context.PluginRecords.WrittenRecords);
    }

    [Fact]
    public async Task NodeErrorsComeByNodeAndGraphErrorsApartAndNothingIsStored()
    {
        await using var vision = await VisionHarness.StartAsync();
        var broken = vision.NewPipeline(tail: VisionHarness.Node("ev", NodeKinds.Event, "Gate", new JsonObject { ["error"] = "Link an input." }));
        var reply = await vision.TrySaveAsync(broken);
        Assert.Equal("Link an input.", reply.Errors[PipelineFields.Node("ev")]);

        var noCamera = broken with { Nodes = [.. broken.Nodes.Where(n => n.Kind != NodeKinds.Camera && n.Id != "ev")] };
        reply = await vision.TrySaveAsync(noCamera);
        Assert.Equal(["Add a camera."], reply.GraphErrors);
        Assert.Empty(vision.Plugin.AllPipelines);
    }

    [Fact]
    public async Task ANewPipelineGetsAnIdOf32HexDigitsAndKeepsItsCamera()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        Assert.True(PipelineRules.IsId(saved.Id));

        var reply = await vision.TrySaveAsync(saved with { DeviceId = vision.Outside.Id });
        Assert.Equal(PipelineRules.CameraFixed, reply.Errors[PipelineFields.Camera]);
        Assert.Equal(vision.Inside.Id, vision.Plugin.FindPipeline(saved.Id)!.DeviceId);

        var renamed = await vision.SaveAsync(saved with { Name = "Gate 2" });
        Assert.Equal("Gate 2", vision.Plugin.FindPipeline(saved.Id)!.Name);
        Assert.Equal(saved.Id, renamed.Id);
    }

    [Fact]
    public async Task NamesAreUniquePerOwner()
    {
        await using var vision = await VisionHarness.StartAsync();
        await vision.SaveAsync(vision.NewPipeline("Gate"));
        var reply = await vision.TrySaveAsync(vision.NewPipeline("gate"));
        Assert.Equal(PipelineRules.NameTaken("gate"), reply.Errors[PipelineFields.Name]);
    }

    [Fact]
    public async Task AGroupSeesOnlyItsPipelinesAndAnotherOnesAnswersLikeAnUnknownId()
    {
        await using var vision = await VisionHarness.StartAsync();
        var key = vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", key);
        var admins = await vision.SaveAsync(vision.NewPipeline("Lobby", PipelineMode.OnAction, vision.Outside.Id));
        var mine = await vision.SaveAsync(vision.NewPipeline("Gate"), group);

        Assert.Equal(["Gate"], (await vision.ListAsync(group)).Pipelines.Select(p => p.Name));
        Assert.Equal(["Gate", "Lobby"], (await vision.ListAsync()).Pipelines.Select(p => p.Name));
        Assert.Equal(key, vision.Plugin.FindPipeline(mine.Id)!.OwnerScopeKey);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => vision.CallAsync(VisionMethods.GetPipeline, new PipelineRequest { PipelineId = admins.Id }, group));
        Assert.Equal(PipelineRules.Gone, error.Message);
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => vision.SaveAsync(vision.NewPipeline("Out", device: vision.Outside.Id), group));

        await vision.CallAsync(VisionMethods.DeletePipeline, new PipelineRequest { PipelineId = admins.Id }, group);
        Assert.NotNull(vision.Plugin.FindPipeline(admins.Id));
    }

    [Fact]
    public async Task AtMost50Pipelines()
    {
        await using var vision = await VisionHarness.StartAsync();
        for (var i = 0; i < VisionLimits.MaxPipelines; i++)
        {
            await vision.SaveAsync(vision.NewPipeline("P" + i, PipelineMode.OnAction));
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() => vision.SaveAsync(vision.NewPipeline("One more", PipelineMode.OnAction)));
        Assert.Equal(PipelineRules.TooManyPipelines, error.Message);
    }

    [Fact]
    public async Task StoredPipelinesComeBackAfterARestart()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline());
        await vision.Plugin.StopAsync(CancellationToken.None);
        await vision.Plugin.StartAsync(vision.Context, CancellationToken.None);

        var back = vision.Plugin.FindPipeline(saved.Id);
        Assert.NotNull(back);
        Assert.Equal(saved.Name, back.Name);
        Assert.Equal(["cam", "det", "ev"], back.Nodes.Select(n => n.Id));
        Assert.Equal("m1", PipelineRules.Text(back.Nodes[1], NodeKinds.ModelKey));
    }

    [Fact]
    public async Task ListingPipelinesWith100000DevicesFindsOnlyTheirCameras()
    {
        await using var vision = await VisionHarness.StartAsync();
        for (var i = 0; i < DeviceLimits.MaxDevices; i++)
        {
            vision.Context.DeviceList.Add(new FakeDevice());
        }

        for (var i = 0; i < 20; i++)
        {
            await vision.SaveAsync(vision.NewPipeline("P" + i, PipelineMode.OnAction));
        }

        var list = await vision.ListAsync();
        Assert.Equal(20, list.Pipelines.Count);
        Assert.All(list.Pipelines, p => Assert.Equal(vision.Inside.Id, p.DeviceId));
    }
}
