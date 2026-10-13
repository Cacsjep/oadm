using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Server.Tests.Support;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// PTZ of the live view and the RTSP port of a camera behind a port forward, through the in-process server: reads the
/// presets, pans with a continuous move that stops by itself, zooms, centers, goes home, and streams video over the
/// forwarded RTSP port. Verified with an AXIS Q6411-LE of the AXIS Virtual Loan Tool (AXIS OS 13.0.88). Moves the camera:
/// runs only with <c>OADM_PTZ_URL</c> (e.g. http://camera.example:12090), <c>OADM_PTZ_USER</c>, <c>OADM_PTZ_PASSWORD</c> and
/// <c>OADM_PTZ_RTSP_PORT</c> set.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class PtzHardwareWriteTests(ITestOutputHelper output)
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_PTZ_URL");

    public sealed class PtzFactAttribute : FactAttribute
    {
        public PtzFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_PTZ_URL, OADM_PTZ_USER, OADM_PTZ_PASSWORD and OADM_PTZ_RTSP_PORT are needed (the test moves the camera).";
            }
        }
    }

    [PtzFact]
    public async Task PresetsMovesZoomAndTheForwardedRtspPortWork()
    {
        var uri = new Uri(Url!);
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = "B8A44F00F0F0", Address = $"{uri.Host}:{uri.Port}", Model = "Q6411-LE", Status = DeviceStatus.Ok, Scheme = DeviceScheme.Http },
            CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, Environment.GetEnvironmentVariable("OADM_PTZ_USER")!, Environment.GetEnvironmentVariable("OADM_PTZ_PASSWORD")!, CancellationToken.None);
        var id = device.Id.ToString();

        var state = await host.LiveView.GetPtzAsync(new Proto.PtzRequest { DeviceId = id, Camera = 1 });
        output.WriteLine($"PTZ: supported {state.Supported}, move {state.CanMove}, zoom {state.CanZoom} ({state.Zoom}), center {state.CanCenter}, home {state.CanGoHome}, presets {string.Join(", ", state.Presets)}");
        Assert.True(state.Supported && state.CanMove && state.CanZoom && state.CanCenter);
        Assert.False((await host.LiveView.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = id })).Supported); // PTZ cameras have no Optics Control

        Task Move(Proto.PtzMove move, double x = 0, double y = 0, int value = 0, string preset = "") =>
            host.LiveView.MovePtzAsync(new Proto.MovePtzRequest { DeviceId = id, Camera = 1, Move = move, X = x, Y = y, Value = value, Preset = preset }).ResponseAsync;

        await Move(Proto.PtzMove.Continuous, 40, 0);
        await Task.Delay(500);
        await Move(Proto.PtzMove.Stop);
        await Move(Proto.PtzMove.ZoomBy, value: 1200);
        await Task.Delay(2000);
        Assert.True((await host.LiveView.GetPtzAsync(new Proto.PtzRequest { DeviceId = id, Camera = 1 })).Zoom > 1);
        await Move(Proto.PtzMove.Center, 0.75, 0.5);
        await Task.Delay(1500);
        // A move without the next one stops by itself after a second.
        await Move(Proto.PtzMove.Continuous, -30, 10);
        await Task.Delay(2500);
        await Move(Proto.PtzMove.Preset, preset: state.Presets.FirstOrDefault() ?? "Home");
        await Task.Delay(4000);
        output.WriteLine($"back at the preset, zoom {(await host.LiveView.GetPtzAsync(new Proto.PtzRequest { DeviceId = id, Camera = 1 })).Zoom}");
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => Move(Proto.PtzMove.Preset, preset: "No such preset"));
        output.WriteLine("unknown preset: " + refused.Status.Detail);

        // Video over the forwarded RTSP port, set like "OADM > RTSP port".
        var rtsp = int.Parse(Environment.GetEnvironmentVariable("OADM_PTZ_RTSP_PORT")!, System.Globalization.CultureInfo.InvariantCulture);
        await host.LiveView.SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { id }, Port = rtsp });
        var request = new Proto.LiveViewRequest { DeviceId = id, MaxWidth = 640, MaxHeight = 360, Fps = 10, AcceptedCodecs = { Proto.VideoCodec.H264 } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = host.LiveView.Watch(request, cancellationToken: timeout.Token);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        output.WriteLine($"video over RTSP port {rtsp}: {call.ResponseStream.Current.Width}x{call.ResponseStream.Current.Height}");
    }
}
