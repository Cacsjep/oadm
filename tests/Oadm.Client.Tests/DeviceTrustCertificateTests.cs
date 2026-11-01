using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>"Trust new certificate" for devices with Certificate changed: context menu entry, dialog and fake mode.</summary>
public sealed class DeviceTrustCertificateTests
{
    private const string NewFingerprint = "3F9A61C2";

    private static DevicesFixture CreateWithDevices()
    {
        var f = new DevicesFixture();
        f.SeedDevices(
            TestSupport.Device("1", "B8A44F11AA22", "10.0.0.33", "AXIS Q3538-LVE", DeviceStatus.CertificateChanged),
            TestSupport.Device("2", "ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II"),
            TestSupport.Device("3", "00408CA1B2C3", "10.0.0.30", "AXIS Q1798-LE", DeviceStatus.CertificateChanged));
        return f;
    }

    private static List<DeviceRowViewModel> Rows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new DeviceRowViewModel(TestSupport.Device(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"B8A44F11{i:D4}", $"10.0.{i / 250}.{i % 250}", "AXIS Q3538-LVE", DeviceStatus.CertificateChanged)))
            .ToList();

    private static PresentedCertificate Presented(string id, string? message = null) => message is null
        ? new PresentedCertificate
        {
            DeviceId = id,
            Fingerprint = NewFingerprint,
            Subject = "CN=axis-b8a44f11aa22",
            Issuer = "CN=axis-b8a44f11aa22",
            NotAfterUnixMs = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Trust = CertificateTrust.SelfSigned,
        }
        : new PresentedCertificate { DeviceId = id, Message = message };

    private static IOadmApi Api(params PresentedCertificate[] presented)
    {
        var api = Substitute.For<IOadmApi>();
        api.GetPresentedCertificatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<PresentedCertificate>)[.. presented.Where(p => call.Arg<IReadOnlyCollection<string>>().Contains(p.DeviceId))]);
        return api;
    }

    // ------------------------------------------------------------------ context menu

    [Fact]
    public void The_entry_shows_only_for_a_selection_with_Certificate_changed()
    {
        using DevicesFixture f = CreateWithDevices();
        f.Select("2");
        Assert.DoesNotContain(DevicesViewModel.TrustCertificateMenuHeader, f.Devices.ContextMenuEntries.Select(e => e.Header));

        f.Select("1", "2", "3");
        MenuEntryViewModel entry = f.Devices.ContextMenuEntries.Single(e => e.Header == DevicesViewModel.TrustCertificateMenuHeader);
        Assert.Same(f.Devices.TrustCertificateCommand, entry.Command);
        Assert.Equal(["1", "3"], f.Devices.TrustCertificateTargets().Select(d => d.Id));

        // The entry follows the status: trusted, it goes away.
        f.Select("1");
        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = TestSupport.Device("1", "B8A44F11AA22", "10.0.0.33", "AXIS Q3538-LVE") }]);
        Assert.DoesNotContain(DevicesViewModel.TrustCertificateMenuHeader, f.Devices.ContextMenuEntries.Select(e => e.Header));
    }

    [Fact]
    public async Task The_dialog_opens_for_the_devices_with_Certificate_changed_only()
    {
        using DevicesFixture f = CreateWithDevices();
        DeviceTrustCertificateViewModel? shown = null;
        f.Dialogs.ShowDeviceTrustCertificateAsync(Arg.Do<DeviceTrustCertificateViewModel>(vm => shown = vm)).Returns(false);
        f.Select("1", "2", "3");

        await f.Devices.TrustCertificateCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(["1", "3"], shown.Rows.Select(r => r.Id));
    }

    // ------------------------------------------------------------------ dialog view model

    [Fact]
    public async Task The_dialog_shows_the_presented_certificate_and_why_another_has_none()
    {
        var vm = new DeviceTrustCertificateViewModel(Api(Presented("1"), Presented("2", "The device presents the trusted certificate again.")), Rows(2));
        Assert.All(vm.Rows, r => Assert.Equal(DeviceTrustCertificateViewModel.ReadingText, r.StatusText));

        await vm.LoadAsync(CancellationToken.None);

        TrustCertificateRowViewModel first = vm.Rows[0];
        Assert.True(first.CanTrust);
        Assert.False(first.HasStatus);
        Assert.Equal("3F:9A:61:C2", first.FingerprintText);
        Assert.Equal("CN=axis-b8a44f11aa22", first.IssuedTo);
        Assert.NotEmpty(first.ValidUntil);
        Assert.False(vm.Rows[1].CanTrust);
        Assert.Equal("The device presents the trusted certificate again.", vm.Rows[1].StatusText);
        Assert.True(vm.TrustCommand.CanExecute(null));
    }

    [Fact]
    public async Task Trust_sends_the_shown_fingerprints_and_keeps_the_devices_that_failed()
    {
        IOadmApi api = Api(Presented("1"), Presented("2"));
        IReadOnlyDictionary<string, string>? sent = null;
        api.TrustNewCertificatesAsync(Arg.Do<IReadOnlyDictionary<string, string>>(d => sent = d), Arg.Any<CancellationToken>())
            .Returns(new TrustNewCertificatesReply
            {
                Results =
                {
                    new TrustNewCertificateResult { DeviceId = "1", Ok = true },
                    new TrustNewCertificateResult { DeviceId = "2", Message = "The device now presents another certificate than shown. Nothing was changed." },
                },
            });
        var vm = new DeviceTrustCertificateViewModel(api, Rows(2));
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        await vm.LoadAsync(CancellationToken.None);

        await vm.TrustCommand.ExecuteAsync(null);

        Assert.NotNull(sent);
        Assert.Equal(new Dictionary<string, string> { ["1"] = NewFingerprint, ["2"] = NewFingerprint }, sent);
        Assert.Equal(1, vm.TrustedCount);
        TrustCertificateRowViewModel left = Assert.Single(vm.Rows);
        Assert.Equal("2", left.Id);
        Assert.Equal("The device now presents another certificate than shown. Nothing was changed.", left.StatusText);
        Assert.False(vm.TrustCommand.CanExecute(null)); // nothing left to trust
        Assert.Null(closed);
    }

    [Fact]
    public async Task Every_device_trusted_closes_the_dialog()
    {
        IOadmApi api = Api(Presented("1"));
        api.TrustNewCertificatesAsync(Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new TrustNewCertificatesReply { Results = { new TrustNewCertificateResult { DeviceId = "1", Ok = true } } });
        var vm = new DeviceTrustCertificateViewModel(api, Rows(1));
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;
        await vm.LoadAsync(CancellationToken.None);

        await vm.TrustCommand.ExecuteAsync(null);

        Assert.True(closed);
    }

    [Fact]
    public async Task Many_devices_are_read_in_pages()
    {
        int count = (DeviceTrustCertificateViewModel.PageSize * 2) + 50;
        List<DeviceRowViewModel> rows = Rows(count);
        IOadmApi api = Api([.. rows.Select(r => Presented(r.Id))]);
        var vm = new DeviceTrustCertificateViewModel(api, rows);

        await vm.LoadAsync(CancellationToken.None);

        await api.Received(3).GetPresentedCertificatesAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count <= DeviceTrustCertificateViewModel.PageSize), Arg.Any<CancellationToken>());
        Assert.All(vm.Rows, r => Assert.True(r.CanTrust));
    }

    [Fact]
    public async Task A_server_error_shows_below_the_table()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetPresentedCertificatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PresentedCertificate>>(_ => throw new RpcException(new Status(StatusCode.PermissionDenied, "Your role does not allow this.")));
        var vm = new DeviceTrustCertificateViewModel(api, Rows(1));

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("The certificates could not be read: Your role does not allow this.", vm.ErrorText);
        Assert.False(vm.Rows[0].HasStatus);
        Assert.False(vm.TrustCommand.CanExecute(null));
    }

    [Fact]
    public async Task Fake_mode_trusts_the_camera_with_Certificate_changed_in_memory()
    {
        using var fake = new FakeOadmApi();
        var devices = new List<Device>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (DeviceChanged change in fake.WatchDevicesAsync(cts.Token))
        {
            if (change.Kind == DeviceChanged.Types.Kind.SnapshotEnd)
            {
                break;
            }

            devices.Add(change.Device);
        }

        Device camera = devices.First(d => d.Status == DeviceStatus.CertificateChanged);
        Device other = devices.First(d => d.Status == DeviceStatus.Ok);
        IReadOnlyList<PresentedCertificate> presented = await fake.GetPresentedCertificatesAsync([camera.Id, other.Id], CancellationToken.None);
        Assert.Equal(FakeOadmApi.FakeNewFingerprint, presented[0].Fingerprint);
        Assert.NotEmpty(presented[1].Message);

        TrustNewCertificatesReply reply = await fake.TrustNewCertificatesAsync(new Dictionary<string, string> { [camera.Id] = presented[0].Fingerprint }, CancellationToken.None);

        Assert.True(Assert.Single(reply.Results).Ok);
    }

    // ------------------------------------------------------------------ headless screenshot

    [Fact]
    public async Task Trust_new_certificate_dialog_renders()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var vm = new DeviceTrustCertificateViewModel(
                Api(Presented("1"), Presented("2", "Unreachable - the device did not answer in time")), Rows(2));
            var window = new DeviceTrustCertificateWindow();
            window.Attach(vm);
            window.Show();
            await Eventually.TrueAsync(() => !vm.IsBusy && vm.Rows[0].CanTrust, "the certificates are read");
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("3F:9A:61:C2", texts);
            Assert.Contains("Unreachable - the device did not answer in time", texts);
            Assert.DoesNotContain(texts, t => t.EndsWith("...", StringComparison.Ordinal));

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-trust-certificate.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
