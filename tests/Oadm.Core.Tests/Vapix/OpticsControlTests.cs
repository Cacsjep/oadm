using System.Net;
using System.Text.Json;

using Oadm.Core.LiveView;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>Optics Control (zoom and focus of the live view) against answers recorded on an AXIS P3265-V (AXIS OS 12.11).</summary>
public sealed class OpticsControlTests
{
    private static readonly Uri Base = new("https://10.0.0.48/");

    [Fact]
    public async Task ZoomFocusAndTheAutofocusAreaAreReadFromTheCamera()
    {
        using var client = new VapixClient(Base, Camera(out _));
        var state = await client.GetOpticsAsync(CancellationToken.None);

        Assert.True(state.IsSupported);
        Assert.True(state.HasZoom);
        Assert.True(state.HasFocus);
        Assert.Equal("0", state.OpticsId);
        Assert.Equal(2.7057, state.MaxMagnification);
        Assert.Equal(2.3799, state.Magnification);
        Assert.Equal(0.5367, state.FocusPosition);
        Assert.Equal(new OpticsWindow(0.1797, 0.1080, 0.5342, 0.8215), state.FocusWindow);
    }

    [Fact]
    public async Task AFixedLensOrAMissingApiIsNotSupportedWithoutAnError()
    {
        using var fixedLens = new VapixClient(Base, new FakeHttpMessageHandler((_, _) => Fixtures.Json(Fixtures.Read("opticscontrol-getCapabilities-fixed-lens.json"))));
        Assert.False((await fixedLens.GetOpticsAsync(CancellationToken.None)).IsSupported);
        using var old = new VapixClient(Base, new FakeHttpMessageHandler((_, _) => Fixtures.Text("", HttpStatusCode.NotFound)));
        Assert.Same(OpticsState.None, await old.GetOpticsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ChangesAreClampedAndSentAsTheApiExpects()
    {
        var handler = Camera(out var sent);
        using var client = new VapixClient(Base, handler);
        var state = await client.GetOpticsAsync(CancellationToken.None);

        await AxisDeviceOptics.ApplyAsync(client, state, new OpticsChange { Magnification = 9 }, CancellationToken.None);
        await AxisDeviceOptics.ApplyAsync(client, state, new OpticsChange { FocusPosition = -1 }, CancellationToken.None);
        await AxisDeviceOptics.ApplyAsync(client, state, new OpticsChange { FocusWindow = new OpticsWindow(0.9, 0.9, 0.5, 0.5) }, CancellationToken.None);
        await AxisDeviceOptics.ApplyAsync(client, state, new OpticsChange { ResetFocus = true }, CancellationToken.None);

        Assert.Equal(["setMagnification", "setFocus", "setFocusWindow", "performAutofocus", "reset", "performAutofocus"], sent.Select(s => s.Method));
        Assert.Equal(2.7057, sent[0].Optics.GetProperty("magnification").GetDouble());
        Assert.Equal(0, sent[1].Optics.GetProperty("position").GetDouble());
        // The area stays inside the picture with at least 5 % per side.
        Assert.Equal(0.9, sent[2].Optics.GetProperty("upperLeftX").GetDouble());
        Assert.Equal(0.1, sent[2].Optics.GetProperty("width").GetDouble());
        Assert.True(sent[4].Optics.GetProperty("focus").GetBoolean());
        Assert.All(sent, s => Assert.Equal("0", s.Optics.GetProperty("opticsId").GetString()));
    }

    [Fact]
    public async Task TheCamerasRefusalAndMissingOpticsAreErrorsWithAText()
    {
        using var refusing = new VapixClient(Base, new FakeHttpMessageHandler((_, _) => Fixtures.Json(Fixtures.Read("opticscontrol-error-2104.json"))));
        var ex = await Assert.ThrowsAsync<OpticsException>(() => refusing.SetMagnificationAsync("0", 9.9, CancellationToken.None));
        Assert.Equal(OpticsException.InvalidValue, ex.Code);
        Assert.Equal("The camera refused the value.", ex.UserMessage);

        using var client = new VapixClient(Base, Camera(out _));
        var focusOnly = (await client.GetOpticsAsync(CancellationToken.None)) with { HasZoom = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AxisDeviceOptics.ApplyAsync(client, focusOnly, new OpticsChange { Magnification = 2 }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AxisDeviceOptics.ApplyAsync(client, OpticsState.None, new OpticsChange { Autofocus = true }, CancellationToken.None));
    }

    [Fact]
    public void ChangesAreDescribedForTheAuditLog()
    {
        Assert.Equal("Zoom 2.10", new OpticsChange { Magnification = 2.1 }.Describe());
        Assert.Equal("Focus 53", new OpticsChange { FocusPosition = 0.531 }.Describe());
        Assert.Equal("Autofocus area", new OpticsChange { FocusWindow = new OpticsWindow(0, 0, 1, 1) }.Describe());
        Assert.Equal("Reset focus", new OpticsChange { ResetFocus = true }.Describe());
        Assert.Equal("Autofocus", new OpticsChange { Autofocus = true }.Describe());
    }

    /// <summary>The development camera's recorded answers; set calls answer with empty data and are recorded.</summary>
    private static FakeHttpMessageHandler Camera(out List<(string Method, JsonElement Optics)> sent)
    {
        var calls = new List<(string Method, JsonElement Optics)>();
        sent = calls;
        return new FakeHttpMessageHandler((request, body) =>
        {
            Assert.Equal("/axis-cgi/opticscontrol.cgi", request.RequestUri!.AbsolutePath);
            using var doc = JsonDocument.Parse(body!);
            var method = doc.RootElement.GetProperty("method").GetString()!;
            switch (method)
            {
                case "getCapabilities":
                    return Fixtures.Json(Fixtures.Read("opticscontrol-getCapabilities-P3265-V.json"));
                case "getOptics":
                    return Fixtures.Json(Fixtures.Read("opticscontrol-getOptics-P3265-V.json"));
                default:
                    calls.Add((method, doc.RootElement.GetProperty("params").GetProperty("optics")[0].Clone()));
                    return Fixtures.Json("{\"apiVersion\": \"1.3\", \"method\": \"" + method + "\", \"data\": {}}");
            }
        });
    }
}
