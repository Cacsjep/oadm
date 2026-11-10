using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Api;

/// <summary>The page backend (docs/spec/vision-center.md "Permissions", "Models", "Load"): rights, audit, models, stats, device groups.</summary>
public sealed class PageMethodsTests
{
    [Fact]
    public void ReadsNeedShowPipelinesChangeAndModelsTheirOwnRight()
    {
        var plugin = new VisionHarness().Plugin;
        foreach (var method in new[] { VisionMethods.ListPipelines, VisionMethods.GetPipeline, VisionMethods.CheckGraph, VisionMethods.StartPreview, VisionMethods.PausePreview, VisionMethods.ListModels, VisionMethods.GetStats, VisionMethods.GetEngine, VisionMethods.GetCameraOptions })
        {
            Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(method));
            Assert.False(plugin.IsAudited(method));
        }

        foreach (var method in new[] { VisionMethods.SavePipeline, VisionMethods.DeletePipeline, VisionMethods.SetUseGpu })
        {
            Assert.Equal(VisionInfo.ChangePermission, plugin.RequiredPermission(method));
            Assert.True(plugin.IsAudited(method));
        }

        foreach (var method in new[] { VisionMethods.BeginUpload, VisionMethods.UploadChunk, VisionMethods.FinishUpload, VisionMethods.CancelUpload, VisionMethods.UpdateModel, VisionMethods.DeleteModel })
        {
            Assert.Equal(VisionInfo.ModelsPermission, plugin.RequiredPermission(method));
            Assert.True(plugin.RequiresAllDevices(method));
        }

        Assert.True(plugin.RequiresAllDevices(VisionMethods.SetUseGpu));
        Assert.False(plugin.RequiresAllDevices(VisionMethods.SavePipeline));
        Assert.True(plugin.IsAudited(VisionMethods.FinishUpload));
        Assert.False(plugin.Permissions.Single(p => p.Key == VisionInfo.ModelsPermission).OperatorDefault);
    }

    [Fact]
    public async Task TheAuditNamesThePipelinesAndModels()
    {
        await using var vision = await VisionHarness.StartAsync();
        var saved = await vision.SaveAsync(vision.NewPipeline("Gate 1"));
        Assert.Equal("Saved vision pipeline Gate 1", vision.Plugin.AuditDetail(VisionMethods.SavePipeline, VisionJson.Serialize(new SavePipelineRequest { Pipeline = saved })));

        var delete = VisionJson.Serialize(new PipelineRequest { PipelineId = saved.Id });
        await vision.CallAsync(VisionMethods.DeletePipeline, new PipelineRequest { PipelineId = saved.Id });
        Assert.Equal("Deleted vision pipeline Gate 1", vision.Plugin.AuditDetail(VisionMethods.DeletePipeline, delete));

        var begun = VisionJson.Deserialize<BeginUploadReply>(await vision.CallAsync(VisionMethods.BeginUpload, new BeginUploadRequest { FileName = "cars.onnx", Size = 3 }));
        await vision.CallAsync(VisionMethods.FinishUpload, new UploadRequest { UploadId = begun.UploadId });
        Assert.Equal("Uploaded vision model Cars", vision.Plugin.AuditDetail(VisionMethods.FinishUpload, VisionJson.Serialize(new UploadRequest { UploadId = begun.UploadId })));

        await vision.CallAsync(VisionMethods.DeleteModel, new ModelRequest { ModelId = "m2" });
        Assert.Equal("Deleted vision model Cars", vision.Plugin.AuditDetail(VisionMethods.DeleteModel, VisionJson.Serialize(new ModelRequest { ModelId = "m2" })));
        Assert.Equal("Turned Use GPU off for Vision Center", vision.Plugin.AuditDetail(VisionMethods.SetUseGpu, """{"useGpu":false}"""));
    }

    [Fact]
    public async Task AModelUploadGoesToTheLibraryInChunks()
    {
        await using var vision = await VisionHarness.StartAsync();
        var begun = VisionJson.Deserialize<BeginUploadReply>(await vision.CallAsync(VisionMethods.BeginUpload, new BeginUploadRequest { FileName = "cars.onnx", Size = 3, Labels = "car" }));
        await vision.CallAsync(VisionMethods.UploadChunk, new UploadChunkRequest { UploadId = begun.UploadId, Offset = 0, Data = [1, 2, 3] });
        var finished = VisionJson.Deserialize<FinishUploadReply>(await vision.CallAsync(VisionMethods.FinishUpload, new UploadRequest { UploadId = begun.UploadId }));

        Assert.Equal(("cars.onnx", 3L, "car"), vision.Models.Begun.Single());
        Assert.Equal([1, 2, 3], vision.Models.Chunks.Single().Data);
        Assert.Equal("Cars", finished.Model!.Name);
        Assert.Contains(vision.Context.PublishedEvents, e => e.Topic == VisionMethods.ModelsTopic);
    }

    [Fact]
    public async Task TheModelListNamesThePipelinesThatUseEachAndADeleteHandsThemOver()
    {
        await using var vision = await VisionHarness.StartAsync();
        var key = vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", key);
        await vision.SaveAsync(vision.NewPipeline("Gate 1"));
        await vision.SaveAsync(vision.NewPipeline("Lane 2", PipelineMode.OnAction), group);

        var all = VisionJson.Deserialize<ModelList>(await vision.CallAsync(VisionMethods.ListModels));
        Assert.Equal(["Gate 1", "Lane 2"], all.UsedBy["m1"]);
        var mine = VisionJson.Deserialize<ModelList>(await vision.CallAsync(VisionMethods.ListModels, caller: group));
        Assert.Equal(["Lane 2"], mine.UsedBy["m1"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vision.CallAsync(VisionMethods.DeleteModel, new ModelRequest { ModelId = "m1" }));
        Assert.Equal(["Gate 1", "Lane 2"], vision.Models.Deleted.Single().UsedBy);
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => vision.CallAsync(VisionMethods.DeleteModel, new ModelRequest { ModelId = "m1" }, group));
    }

    [Fact]
    public async Task TheStatsShowTheTrafficLightOfTheCallersPipelines()
    {
        await using var vision = await VisionHarness.StartAsync(processorCount: 4);
        vision.Inference.Capacity = 2;
        var stats = VisionJson.Deserialize<VisionStats>(await vision.CallAsync(VisionMethods.GetStats));
        Assert.Equal(LoadState.Ok, stats.State);
        Assert.Equal(PipelineRules.KeepUp, stats.StateText);
        Assert.Equal(VisionLimits.MaxWatchingFor(4), stats.MaxWatching);
        Assert.Equal(VisionLimits.MaxPreviews, stats.MaxPreviews);

        // The models busy 85 % of 2 workers: orange with the load.
        vision.Time.Advance(PipelineStats.SampleInterval);
        vision.Inference.BusyTime = TimeSpan.FromSeconds(0.85 * 2 * 2);
        vision.Time.Advance(PipelineStats.SampleInterval);
        stats = VisionJson.Deserialize<VisionStats>(await vision.CallAsync(VisionMethods.GetStats));
        Assert.Equal(LoadState.Busy, stats.State);
        Assert.Equal("Busy: the models are at 85 %", stats.StateText);
        Assert.Contains(vision.Context.PublishedEvents, e => e.Topic == VisionMethods.StatsTopic);
    }

    [Fact]
    public void TheTrafficLightCountsSlowPipelines()
    {
        Assert.Equal((LoadState.Busy, "Busy: 2 pipelines run slower than set"), PipelineStats.Light(10, false, [(8, 5, true), (8, 6, true), (8, 8, true)]));
        Assert.Equal((LoadState.Busy, "Busy: 1 pipeline runs slower than set"), PipelineStats.Light(10, false, [(8, 5, true)]));
        Assert.Equal((LoadState.Overloaded, PipelineRules.Overloaded), PipelineStats.Light(99, true, [(8, 5, true)]));
        Assert.Equal((LoadState.Ok, PipelineRules.KeepUp), PipelineStats.Light(10, false, [(8, 5, false)]));
    }

    [Fact]
    public async Task CheckGraphAnswersTheServersCheckOfADraft()
    {
        await using var vision = await VisionHarness.StartAsync();
        var draft = vision.NewPipeline() with { Nodes = [.. vision.NewPipeline().Nodes.Where(n => n.Kind != NodeKinds.Camera)] };
        var check = VisionJson.Deserialize<GraphCheck>(await vision.CallAsync(VisionMethods.CheckGraph, new CheckGraphRequest { Pipeline = draft }));
        Assert.Equal(["Add a camera."], check.GraphErrors);
    }

    [Fact]
    public async Task UseGpuIsStoredAndHandedToTheInference()
    {
        await using var vision = await VisionHarness.StartAsync();
        var engine = VisionJson.Deserialize<EngineInfo>(await vision.CallAsync(VisionMethods.SetUseGpu, new UseGpuRequest { UseGpu = false }));
        Assert.False(engine.UseGpu);
        Assert.False(vision.Inference.UsesGpu);
        Assert.Equal("false", vision.Context.PluginSettings.Values[VisionPlugin.UseGpuSetting]);
    }

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        await using var vision = await VisionHarness.StartAsync();
        await new PluginCanary(vision.Context, vision.Plugin, vision.Inside, vision.Outside)
            .WithoutDevices(VisionMethods.ListPipelines, "the pipelines of the caller's own device scope")
            .WithoutDevices(VisionMethods.GetPipeline, "a pipeline of the caller's own device scope", """{"pipelineId":"p1"}""")
            .Method(VisionMethods.SavePipeline, """{"pipeline":{"name":"Gate","deviceId":"{device}","mode":"OnAction","nodes":[{"id":"cam","kind":"camera","name":"Camera"}]}}""")
            .WithoutDevices(VisionMethods.DeletePipeline, "a pipeline of the caller's own device scope", """{"pipelineId":"p1"}""")
            .WithoutDevices(VisionMethods.CheckGraph, "a check of the draft only", """{"pipeline":{"name":"Gate"}}""")
            .Method(VisionMethods.StartPreview, """{"pipeline":{"name":"Gate","deviceId":"{device}","nodes":[{"id":"cam","kind":"camera","name":"Camera"}]}}""")
            .WithoutDevices(VisionMethods.StopPreview, "a preview of the caller's own", """{"previewId":"p1"}""")
            .WithoutDevices(VisionMethods.PausePreview, "a preview of the caller's own", """{"previewId":"p1"}""")
            .WithoutDevices(VisionMethods.ResumePreview, "a preview of the caller's own", """{"previewId":"p1"}""")
            .WithoutDevices(VisionMethods.ListModels, "the server's models")
            .WithoutDevices(VisionMethods.GetEngine, "what runs the models on this server")
            .WithoutDevices(VisionMethods.GetStats, "the load and the caller's own pipelines")
            .Method(VisionMethods.GetCameraOptions, """{"deviceId":"{device}"}""")
            .AllDevicesOnly(VisionMethods.SetUseGpu, """{"useGpu":false}""")
            .AllDevicesOnly(VisionMethods.DeleteModel, """{"modelId":"m9"}""")
            .RunAsync();
    }
}
