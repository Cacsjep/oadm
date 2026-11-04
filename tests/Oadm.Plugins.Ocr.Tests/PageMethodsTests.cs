using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>
/// The page methods beyond the readers (docs/spec/ocr.md "GPU", "Readers", "Accuracy"): the engine and Use GPU,
/// the camera's sources and sizes, the field errors of a save, the frame rate in the list and the too-small flag.
/// </summary>
public sealed class PageMethodsTests
{
    [Fact]
    public async Task WithoutARecognizerTheEngineIsTheCpu()
    {
        var ctx = new FakeCoreContext();
        await using var plugin = new OcrPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);

        var json = await plugin.InvokeAsync(OcrMethods.GetEngine, "{}", CancellationToken.None);

        Assert.Equal(
            $$"""{"runsOn":"{{ReaderRules.CpuText(Environment.ProcessorCount)}}","gpuAvailable":false,"useGpu":true,"fpsChoices":[1,2,4,8,12,15]}""",
            json);
        Assert.Equal("CPU (8 cores)", ReaderRules.CpuText(8));
        Assert.Equal("CPU (1 core)", ReaderRules.CpuText(1));
    }

    [Fact]
    public async Task OnAGraphicsCardTheEngineNamesItAndOffersTheHigherRates()
    {
        await using var ocr = new OcrHarness(onGpu: true);
        ocr.Recognizer.RunsOn = new(true, "NVIDIA GeForce RTX 5070 Ti");
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);

        var engine = OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.GetEngine));

        Assert.Equal(new EngineInfo { RunsOn = "NVIDIA GeForce RTX 5070 Ti", GpuAvailable = true, UseGpu = true }, engine with { FpsChoices = OcrLimits.FpsChoices });
        Assert.Equal([1, 2, 4, 8, 12, 15], engine.FpsChoices);
        Assert.True(ocr.Recognizer.UsesGpu);
        Assert.Equal(1, ocr.Recognizer.SetUseGpuCalls); // handed over at the start
    }

    [Fact]
    public async Task WithTheSettingOffTheBoxStaysUsableAndTurningItOnFindsOutAboutTheCard()
    {
        await using var ocr = await OcrHarness.StartAsync(onGpu: false);

        var off = OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.SetUseGpu, new UseGpuRequest { UseGpu = false }));
        Assert.False(off.UseGpu);
        Assert.True(off.GpuAvailable);

        var on = OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.SetUseGpu, new UseGpuRequest { UseGpu = true }));
        Assert.True(on.UseGpu);
        Assert.False(on.GpuAvailable);
    }

    [Fact]
    public async Task UseTheGraphicsCardIsStoredHandedToTheRecognizerAndKeptOverARestart()
    {
        await using var ocr = await OcrHarness.StartAsync(onGpu: true);
        var publishedBefore = ocr.Context.PublishedEvents.Count(e => e.Topic == OcrMethods.ReadersTopic);

        var engine = OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.SetUseGpu, new UseGpuRequest { UseGpu = false }));

        Assert.False(engine.UseGpu);
        Assert.True(engine.GpuAvailable);
        Assert.Equal([1, 2, 4, 8, 12, 15], engine.FpsChoices); // the same rates: the CPU reads every fixed area anyway
        Assert.False(ocr.Recognizer.UsesGpu);
        Assert.Equal("false", ocr.Context.PluginSettings.Values["useGpu"]);
        Assert.True(ocr.Context.PublishedEvents.Count(e => e.Topic == OcrMethods.ReadersTopic) > publishedBefore, "the pages get the list again");
        Assert.Equal(16, (await ocr.ListAsync()).MaxWatching); // the limits follow the cores, not the card

        // A restart reads the setting and hands it over again.
        await ocr.Plugin.StopAsync(CancellationToken.None);
        ocr.Recognizer.SetUseGpu(true);
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        Assert.False(ocr.Recognizer.UsesGpu);
        Assert.False(OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.GetEngine)).UseGpu);
    }

    [Fact]
    public async Task UseTheGraphicsCardIsServerWideAndOnlyForUsersWithAllDevices()
    {
        await using var ocr = await OcrHarness.StartAsync(onGpu: true);
        var group = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", ocr.Context.FakeScopes.Add("Building A", [ocr.Inside.Id]));
        Assert.True(ocr.Plugin.RequiresAllDevices(OcrMethods.SetUseGpu));
        Assert.False(ocr.Plugin.RequiresAllDevices(OcrMethods.GetEngine));
        Assert.False(ocr.Plugin.RequiresAllDevices(OcrMethods.SaveReader));

        // The host refuses first; the plugin fails closed on its own too.
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ocr.CallAsync(OcrMethods.SetUseGpu, new UseGpuRequest { UseGpu = false }, group));
        Assert.Equal(DeviceGroupMessages.AllDevicesOnly, refused.Message);
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() =>
            ocr.Plugin.InvokeAsync(group, OcrMethods.SetUseGpu, OcrJson.Serialize(new UseGpuRequest { UseGpu = false }), CancellationToken.None));

        Assert.True(ocr.Recognizer.UsesGpu);
        Assert.False(ocr.Context.PluginSettings.Values.ContainsKey("useGpu"));
        Assert.True(OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.GetEngine, caller: group)).UseGpu); // reading stays open
    }

    [Fact]
    public void ChangingTheEngineNeedsTheChangeRightAndIsAudited()
    {
        var plugin = new OcrPlugin();
        Assert.Equal(OcrInfo.ChangePermission, plugin.RequiredPermission(OcrMethods.SetUseGpu));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(OcrMethods.GetEngine));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(OcrMethods.GetCameraOptions));
        Assert.True(plugin.IsAudited(OcrMethods.SetUseGpu));
        Assert.False(plugin.IsAudited(OcrMethods.GetCameraOptions));
    }

    [Fact]
    public async Task TheCameraOptionsAreItsSourcesAndSizesLargestFirstReadOnceInAWhile()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var camera = new SourcesCamera
        {
            Sources =
            [
                new VideoSource(1, "View area 1", 0, [new(1920, 1080), new(3840, 2160), new(1280, 720), new(160, 90)]),
                new VideoSource(2, "View area 2", 0, [new(2688, 1512), new(1920, 1080)]),
            ],
        };
        var device = ocr.Context.Add(new FakeDevice { Address = "10.0.0.5" }, camera);

        var json = await ocr.CallAsync(OcrMethods.GetCameraOptions, new CameraOptionsRequest { DeviceId = device.Id });

        Assert.Equal(
            """{"videoSources":[{"number":1,"name":"View area 1"},{"number":2,"name":"View area 2"}],"resolutions":[{"width":3840,"height":2160},{"width":2688,"height":1512},{"width":1920,"height":1080},{"width":1280,"height":720}]}""",
            json);
        Assert.Equal(0, camera.Writes);

        // Kept for a while: the next open reads nothing from the camera; later it reads again.
        await ocr.CallAsync(OcrMethods.GetCameraOptions, new CameraOptionsRequest { DeviceId = device.Id });
        Assert.Equal(1, camera.SourceReads);
        ocr.Time.Advance(OcrPlugin.CameraOptionsLifetime);
        await ocr.CallAsync(OcrMethods.GetCameraOptions, new CameraOptionsRequest { DeviceId = device.Id });
        Assert.Equal(2, camera.SourceReads);
    }

    [Fact]
    public async Task ACameraOadmCannotContactIsNotAskedForItsOptions()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var camera = new SourcesCamera();
        var device = ocr.Context.Add(new FakeDevice { Address = "10.0.0.6", Status = DeviceStatus.CertificateChanged }, camera);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.CallAsync(OcrMethods.GetCameraOptions, new CameraOptionsRequest { DeviceId = device.Id }));

        Assert.Equal(DeviceMessages.Refusal(device), refused.Message);
        Assert.Equal(0, camera.SourceReads);
    }

    [Fact]
    public async Task ASaveRefusedByItsFieldsAnswersTheirKeysAndStoresNothing()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = ocr.NewReader(" ", areas: [new Area { Name = "Number", Expected = new ExpectedText { Kind = ExpectedTextKind.Format, Format = "XYZ" } }]);

        var json = await ocr.CallAsync(OcrMethods.SaveReader, new SaveReaderRequest { Reader = reader });

        Assert.Equal(
            """{"errors":[{"field":"name","message":"Enter a name."},{"field":"areas[0].format","message":"A format needs at least one #, @ or *."}]}""",
            json);
        Assert.Empty(await ocr.Context.PluginRecords.ListAsync(ReaderStore.Collection, CancellationToken.None));

        var saved = OcrJson.Deserialize<SaveReaderReply>(await ocr.CallAsync(OcrMethods.SaveReader, new SaveReaderRequest { Reader = ocr.NewReader() }));
        Assert.NotNull(saved.Reader);
        Assert.Empty(saved.Errors);
    }

    [Fact]
    public async Task TheListCarriesEachReadersFrameRate()
    {
        await using var ocr = await OcrHarness.StartAsync();
        await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching) with { Fps = 4 });
        await ocr.SaveAsync(ocr.NewReader("Meter"));

        var list = await ocr.ListAsync();

        Assert.Equal([("Gate", 4), ("Meter", OcrLimits.DefaultFps)], list.Readers.Select(r => (r.Name, r.Fps)));
    }

    [Fact]
    public async Task TextTooSmallReachesThePageWithTheReading()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader(areas: [new Area { Name = "Lane", Box = new(0, 0, 0.5, 0.5) }, new Area { Name = "Number", Box = new(0.5, 0.5, 0.5, 0.5) }]));
        ocr.Frames.Single = "G3";
        ocr.Recognizer.TooSmall = [true, false];

        var reply = await ocr.ReadOnceAsync(reader.Id);

        Assert.Equal([true, false], reply.Details.Select(d => d.TooSmall));
        var live = OcrJson.Deserialize<ReadingEvent>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.ReadingTopic).PayloadJson);
        Assert.Equal([true, false], live.Details.Select(d => d.TooSmall));

        // Without the flag from the recognizer: false.
        ocr.Recognizer.TooSmall = [];
        reply = await ocr.ReadOnceAsync(reader.Id);
        Assert.All(reply.Details, d => Assert.False(d.TooSmall));
    }

    /// <summary>A camera with video sources (the host's param.cgi reading).</summary>
    private sealed class SourcesCamera : FakeCamera, IVapixClient
    {
        public IReadOnlyList<VideoSource> Sources { get; init; } = [new VideoSource(1, "Camera", 0, [new(1920, 1080)])];

        public int SourceReads { get; private set; }

        Task<IReadOnlyList<VideoSource>> IVapixClient.GetVideoSourcesAsync(CancellationToken ct)
        {
            SourceReads++;
            return Task.FromResult(Sources);
        }
    }
}
