using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>Zoom and focus of the live view and "OADM > RTSP port": the service, the permission "Optics Control", the audit.</summary>
public sealed class LiveViewOpticsTests
{
    [Fact]
    public async Task OpticsAreReadForTheLiveViewAndChangedWithOpticsControl()
    {
        var optics = new FakeOptics();
        await using var host = await StartAsync(optics);
        var device = await AddDeviceAsync(host, "B8A44F631339", "10.0.0.48");

        var state = await host.LiveView.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = device.ToString() });
        Assert.True(state.Supported);
        Assert.Equal(2.7057, state.MaxMagnification);
        Assert.Equal(0.25, state.FocusWindow.X);

        state = await host.LiveView.SetOpticsAsync(new Proto.SetOpticsRequest { DeviceId = device.ToString(), Magnification = 1.5 });
        Assert.Equal(1.5, state.Magnification);
        state = await host.LiveView.SetOpticsAsync(new Proto.SetOpticsRequest
        {
            DeviceId = device.ToString(),
            FocusWindow = new Proto.OpticsWindow { X = 0.1, Y = 0.1, Width = 0.3, Height = 0.3 },
        });
        Assert.Equal(0.1, state.FocusWindow.X);
        Assert.Equal(["Zoom 1.50", "Autofocus area"], optics.Changes.Select(c => c.Describe()));

        // Exactly one change per call; the camera's refusal is the message.
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.LiveView.SetOpticsAsync(
            new Proto.SetOpticsRequest { DeviceId = device.ToString(), Magnification = 2, Autofocus = true }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        optics.Refuse = new OpticsException(OpticsException.FocusNotFound, "Focus not found");
        ex = await Assert.ThrowsAsync<RpcException>(async () => await host.LiveView.SetOpticsAsync(
            new Proto.SetOpticsRequest { DeviceId = device.ToString(), Autofocus = true }));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.StartsWith("The camera could not find focus.", ex.Status.Detail, StringComparison.Ordinal);

        var audit = await host.Get<AuditLog>().ListAsync(100, 0, CancellationToken.None);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.OpticsChanged && e.Detail == "Zoom 1.50" && e.Target == "10.0.0.48 (B8A44F631339)");
    }

    [Fact]
    public async Task AViewerSeesTheStateButCannotChangeIt()
    {
        var optics = new FakeOptics();
        await using var host = await StartAsync(optics);
        var device = await AddDeviceAsync(host, "B8A44F631339", "10.0.0.48");
        var viewer = new Proto.LiveViewService.LiveViewServiceClient(await host.InvokerForRoleAsync("guard", BuiltInRoles.ViewerId));
        var op = new Proto.LiveViewService.LiveViewServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));

        Assert.True((await viewer.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = device.ToString() })).Supported);
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await viewer.SetOpticsAsync(new Proto.SetOpticsRequest { DeviceId = device.ToString(), FocusPosition = 0.4 }));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal("Your role does not allow: Optics Control.", ex.Status.Detail);
        ex = await Assert.ThrowsAsync<RpcException>(async () => await viewer.SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { device.ToString() }, Port = 8554 }));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        // Operators have Optics Control by default.
        Assert.Equal(0.4, (await op.SetOpticsAsync(new Proto.SetOpticsRequest { DeviceId = device.ToString(), FocusPosition = 0.4 })).FocusPosition);
    }

    [Fact]
    public async Task ADeviceWithoutOpticsOrOneOadmCannotLogInToIsNotSupportedWithoutAnError()
    {
        var optics = new FakeOptics { State = OpticsState.None };
        await using var host = await StartAsync(optics);
        var plain = await AddDeviceAsync(host, "B8A44F000001", "10.0.0.202");
        var locked = await AddDeviceAsync(host, "B8A44F000002", "10.0.0.203", DeviceStatus.CredentialsRequired);

        Assert.False((await host.LiveView.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = plain.ToString() })).Supported);
        optics.State = FakeOptics.Lens;
        Assert.False((await host.LiveView.GetOpticsAsync(new Proto.OpticsRequest { DeviceId = locked.ToString() })).Supported);
        Assert.Equal(1, optics.Reads); // the locked device was not asked
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.LiveView.SetOpticsAsync(new Proto.SetOpticsRequest { DeviceId = locked.ToString(), Autofocus = true }));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task TheRtspPortIsStoredInOadmForTheLiveViewAndTheEvents()
    {
        await using var host = await StartAsync(new FakeOptics());
        var a = await AddDeviceAsync(host, "B8A44F000001", "10.0.0.48");
        var b = await AddDeviceAsync(host, "B8A44F000002", "10.0.0.49");
        var both = new Proto.RtspPortRequest { DeviceIds = { a.ToString(), b.ToString() } };

        Assert.Equal((554, false), Read(await host.LiveView.GetRtspPortAsync(both)));
        await host.LiveView.SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { a.ToString() }, Port = 8554 });
        Assert.Equal((554, true), Read(await host.LiveView.GetRtspPortAsync(both)));
        Assert.Equal(8554, await host.Get<IDeviceEventStreams>().GetRtspPortAsync(a, CancellationToken.None));

        await host.LiveView.SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { a.ToString(), b.ToString() }, Port = 10554 });
        Assert.Equal((10554, false), Read(await host.LiveView.GetRtspPortAsync(both)));
        await host.LiveView.SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { a.ToString(), b.ToString() }, Port = 554 });
        Assert.Null(await host.Get<IDeviceEventStreams>().GetRtspPortAsync(a, CancellationToken.None));

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.LiveView.SetRtspPortAsync(new Proto.SetRtspPortRequest { DeviceIds = { a.ToString() }, Port = 70000 }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        var audit = await host.Get<AuditLog>().ListAsync(100, 0, CancellationToken.None);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.RtspPortChanged && e.Target == "2 devices" && e.Detail == "10554");

        static (int, bool) Read(Proto.RtspPortReply reply) => (reply.Port, reply.Mixed);
    }

    private static Task<TestServerHost> StartAsync(FakeOptics optics) =>
        TestServerHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IDeviceOptics>();
            services.AddSingleton<IDeviceOptics>(optics);
        });

    private static async Task<Guid> AddDeviceAsync(TestServerHost host, string serial, string address, DeviceStatus status = DeviceStatus.Ok) =>
        (await host.Get<DeviceRepository>().AddAsync(new Device { Serial = serial, Address = address, Model = "P3265-V", Status = status }, CancellationToken.None)).Id;

    private sealed class FakeOptics : IDeviceOptics
    {
        public static readonly OpticsState Lens = new()
        {
            OpticsId = "0",
            HasZoom = true,
            HasFocus = true,
            MaxMagnification = 2.7057,
            Magnification = 2.38,
            FocusPosition = 0.54,
            FocusWindow = new OpticsWindow(0.25, 0.25, 0.5, 0.5),
        };

        public OpticsState State { get; set; } = Lens;

        public List<OpticsChange> Changes { get; } = [];

        public Exception? Refuse { get; set; }

        public int Reads { get; private set; }

        public Task<OpticsState> GetAsync(Guid deviceId, int sensor, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(State);
        }

        public Task<OpticsState> SetAsync(Guid deviceId, int sensor, OpticsChange change, CancellationToken ct)
        {
            if (Refuse is { } refuse)
            {
                throw refuse;
            }

            Changes.Add(change);
            State = State with
            {
                Magnification = change.Magnification ?? State.Magnification,
                FocusPosition = change.FocusPosition ?? State.FocusPosition,
                FocusWindow = change.FocusWindow ?? State.FocusWindow,
            };
            return Task.FromResult(State);
        }
    }
}
