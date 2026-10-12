using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Tests.Hardware;
using Oadm.Server.Tests.Support;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// Zoom, focus, autofocus area, autofocus and reset focus of the live view against the development camera 10.0.0.48 (AXIS
/// P3265-V) through the in-process server, then puts zoom, focus and the autofocus area back. Moves the lens: runs only with
/// <c>OADM_OPTICS_WRITE=1</c> and the dev cameras configured, never in <c>manage test hardware</c>.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class OpticsHardwareWriteTests(ITestOutputHelper output)
{
    public sealed class OpticsWriteFactAttribute : FactAttribute
    {
        public OpticsWriteFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OADM_OPTICS_WRITE") != "1" || !DevCameras.Any)
            {
                Skip = "OADM_OPTICS_WRITE=1 and the dev cameras are needed (the test moves the lens).";
            }
        }
    }

    [OpticsWriteFact]
    public async Task ZoomFocusAndAutofocusWorkAndTheLensIsPutBack()
    {
        var camera = DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device
            {
                Serial = "B8A44F631339",
                Address = camera.Address,
                Model = "P3265-V",
                Status = DeviceStatus.Ok,
                Scheme = camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
            },
            CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, camera.User, camera.Password, CancellationToken.None);
        var id = device.Id.ToString();
        Proto.SetOpticsRequest Change() => new() { DeviceId = id };

        var before = await host.LiveView.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = id });
        output.WriteLine($"before: zoom {before.Magnification} of {before.MaxMagnification}, focus {before.FocusPosition}, area {before.FocusWindow}");
        Assert.True(before.Supported && before.HasZoom && before.HasFocus);
        try
        {
            var zoom = Change();
            zoom.Magnification = 1.5;
            var state = await host.LiveView.SetOpticsAsync(zoom);
            output.WriteLine($"zoom 1.5 -> {state.Magnification} (moving {state.ZoomMoving})");
            await Task.Delay(3000);

            var focus = Change();
            focus.FocusPosition = 0.4;
            state = await host.LiveView.SetOpticsAsync(focus);
            output.WriteLine($"focus 0.4 -> {state.FocusPosition}");
            await Task.Delay(1500);

            var area = Change();
            area.FocusWindow = new Proto.OpticsWindow { X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 };
            state = await host.LiveView.SetOpticsAsync(area);
            output.WriteLine($"area + autofocus -> focus {state.FocusPosition}, area {state.FocusWindow}");
            Assert.Equal(0.25, state.FocusWindow.X, 2);

            var reset = Change();
            reset.ResetFocus = true;
            state = await host.LiveView.SetOpticsAsync(reset);
            output.WriteLine($"reset focus + autofocus -> {state.FocusPosition}");
        }
        finally
        {
            var zoom = Change();
            zoom.Magnification = before.Magnification;
            await host.LiveView.SetOpticsAsync(zoom);
            await Task.Delay(3000);
            if (before.FocusWindow is { } w)
            {
                // The area alone (the autofocus it starts is followed by the stored focus position below).
                var area = Change();
                area.FocusWindow = w;
                await host.LiveView.SetOpticsAsync(area);
            }

            var focus = Change();
            focus.FocusPosition = before.FocusPosition;
            await host.LiveView.SetOpticsAsync(focus);
            await Task.Delay(2000);
            var after = await host.LiveView.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = id });
            output.WriteLine($"after: zoom {after.Magnification}, focus {after.FocusPosition}, area {after.FocusWindow}");
        }
    }
}
