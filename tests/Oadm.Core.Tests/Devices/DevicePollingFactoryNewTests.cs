using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Devices;

/// <summary>
/// The polling's report of a managed device that needs its first password again (docs/spec/provisioning.md "How a task starts"):
/// only a camera that had a password (reset to factory default), never one that was Password not set before it went offline.
/// </summary>
public sealed class DevicePollingFactoryNewTests : IAsyncLifetime, IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly FactoryNewDeviceNotices _notices = new();
    private readonly List<FactoryNewDevice> _reported = [];
    private TestDatabase _db = null!;
    private Camera _camera = null!;
    private VapixClientFactory _factory = null!;
    private DevicePollingService _polling = null!;
    private IDisposable? _watch;

    private DeviceRepository Devices => _db.Get<DeviceRepository>();

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _camera = new Camera();
        _factory = new VapixClientFactory(Devices, _db.Get<CredentialStore>(), _camera);
        _polling = NewPolling();
        _watch = _notices.Watch(_reported.Add);
    }

    public async Task DisposeAsync()
    {
        Dispose();
        await _db.DisposeAsync();
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _polling?.Dispose();
        _factory?.Dispose();
        _camera?.Dispose();
    }

    private DevicePollingService NewPolling() => new(Devices, _factory, _db.Get<ServerSettingsStore>(), _time, factoryNew: _notices);

    private async Task<Device> AddAsync(DeviceStatus status)
    {
        var device = await Devices.AddAsync(new Device
        {
            Serial = "ACCC8E" + Random.Shared.Next(0x100000, 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture),
            Address = "10.0.0.48",
            Status = status,
            Scheme = DeviceScheme.Http,
            Model = "P3265-V",
        }, CancellationToken.None);
        await _db.Get<CredentialStore>().SetAsync(device.Id, "root", "pass", CancellationToken.None);
        return device;
    }

    private async Task<DeviceStatus> PollAsync(Device device, CameraState state)
    {
        _camera.State = state;
        return (await _polling.PollAsync(device.Id, CancellationToken.None))!.Status;
    }

    [Fact]
    public async Task ACameraResetToFactoryDefaultIsReportedWithItsPasswordBefore()
    {
        var device = await AddAsync(DeviceStatus.Ok);
        Assert.Equal(DeviceStatus.Ok, await PollAsync(device, CameraState.Ok));

        Assert.Equal(DeviceStatus.Unreachable, await PollAsync(device, CameraState.Down)); // it restarts after the reset
        Assert.Equal(DeviceStatus.PasswordNotSet, await PollAsync(device, CameraState.NeedsSetup));

        var report = Assert.Single(_reported);
        Assert.Equal((device.Id, false, true), (report.DeviceId, report.Added, report.HadPassword));
    }

    [Fact]
    public async Task ACameraThatWasPasswordNotSetBeforeItWentOfflineIsNeverReportedAgain()
    {
        var device = await AddAsync(DeviceStatus.PasswordNotSet); // the add page stored it so (its own report)
        Assert.Equal(DeviceStatus.Unreachable, await PollAsync(device, CameraState.Down));
        Assert.Equal(DeviceStatus.PasswordNotSet, await PollAsync(device, CameraState.NeedsSetup));
        Assert.Equal(DeviceStatus.Unreachable, await PollAsync(device, CameraState.Down));
        Assert.Equal(DeviceStatus.PasswordNotSet, await PollAsync(device, CameraState.NeedsSetup));

        Assert.Empty(_reported);
    }

    [Fact]
    public async Task AfterARestartAnOfflineCameraWithAnUnknownPastIsReportedWithoutAPasswordBefore()
    {
        var device = await AddAsync(DeviceStatus.Unreachable);

        Assert.Equal(DeviceStatus.PasswordNotSet, await PollAsync(device, CameraState.NeedsSetup));

        var report = Assert.Single(_reported);
        Assert.False(report.HadPassword); // Provisioning keeps a failed run of such a camera for Run again
    }

    [Theory]
    [InlineData(DeviceStatus.PasswordNotSet, false)]
    [InlineData(DeviceStatus.Ok, true)]
    [InlineData(DeviceStatus.CredentialsRequired, true)]
    [InlineData(DeviceStatus.CertificateChanged, true)]
    [InlineData(DeviceStatus.Unreachable, null)]
    [InlineData(DeviceStatus.Unknown, null)]
    [InlineData(DeviceStatus.SiteOffline, null)]
    public void OnlyAnAnswerTellsWhetherTheDeviceHasAPassword(DeviceStatus status, bool? hasPassword) =>
        Assert.Equal(hasPassword, DevicePollingService.HasPassword(status));

    private enum CameraState
    {
        Ok,
        NeedsSetup,
        Down,
    }

    /// <summary>One camera: answers with its login, is factory-new (401 and systemready needsetup), or does not answer.</summary>
    private sealed class Camera : IVapixConnector, IDisposable
    {
        private readonly FakeHttpMessageHandler _handler;

        public Camera()
        {
            _handler = new FakeHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (State == CameraState.Down)
                {
                    throw new HttpRequestException("No route to host.", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable));
                }

                if (path.EndsWith("systemready.cgi", StringComparison.Ordinal))
                {
                    return Fixtures.Json(State == CameraState.NeedsSetup
                        ? """{"apiVersion":"1.5","method":"systemready","data":{"systemready":"yes","needsetup":"yes"}}"""
                        : """{"apiVersion":"1.5","method":"systemready","data":{"systemready":"yes","needsetup":"no"}}""");
                }

                if (path.EndsWith("basicdeviceinfo.cgi", StringComparison.Ordinal))
                {
                    return State == CameraState.NeedsSetup
                        ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) }
                        : Fixtures.Json(Fixtures.Read("basicdeviceinfo-getAllProperties.json"));
                }

                return Fixtures.Text("not found", HttpStatusCode.NotFound);
            });
        }

        public CameraState State { get; set; }

        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), _handler, new CertificatePinning(options.PinnedCertificateFingerprint), options.Timeout, disposeHandler: false);

        public void Dispose() => _handler.Dispose();
    }
}
