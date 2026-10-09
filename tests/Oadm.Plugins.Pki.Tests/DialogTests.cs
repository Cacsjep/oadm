using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The dialog context: queries answered by a function, uploads recorded.</summary>
internal sealed class FakeDialogContext(Func<Guid, Task<string?>> query) : ITaskDialogContext
{
    public List<string> Uploaded { get; } = [];

    public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        Assert.Equal(PkiQueries.ListCertificates, method);
        return query(deviceId);
    }

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        Uploaded.Add(localPath);
        progress?.Report(1);
        return Task.FromResult(new UploadedFile("id-" + Path.GetFileName(localPath), Path.GetFileName(localPath), 1, "00"));
    }
}

internal static class DialogData
{
    /// <summary>The list 10.0.0.48 reports (4 certificates, 34 CA certificates), as the query answers it.</summary>
    public static string FixtureReply()
    {
        var certificates = CertApi.ParseList(System.Text.Json.Nodes.JsonNode.Parse(PkiFixture.Read(PkiFixture.Certificates))!["data"]);
        var cas = CertApi.ParseList(System.Text.Json.Nodes.JsonNode.Parse(PkiFixture.Read(PkiFixture.CaCertificates))!["data"]);
        var list = CertificateInventory.Describe(certificates, cas, WebServerTls.Parse(PkiFixture.Read(PkiFixture.WebServer)), NetworkInfoApi.Parse(PkiFixture.Read(PkiFixture.NetworkInfo)).Dot1x, [], null);
        return PkiJson.Serialize(new CertificateListReply { Certificates = list });
    }

    public static List<IDeviceInfo> Devices(int count) =>
        [.. Enumerable.Range(0, count).Select(i => (IDeviceInfo)new PkiFakeDevice(Guid.NewGuid(), $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}", $"ACCC8E{i:X6}"))];
}

public sealed class CertificatesViewModelTests(ITestOutputHelper output)
{
    [Fact]
    public async Task View_reads_every_device_groups_rows_and_shows_unreadable_devices()
    {
        var reply = DialogData.FixtureReply();
        var devices = DialogData.Devices(3);
        var ctx = new FakeDialogContext(id => id == devices[2].Id ? throw new InvalidOperationException("Timeout: the device did not answer") : Task.FromResult<string?>(reply));
        var vm = new CertificatesViewModel(ctx, devices, deleteMode: false);

        await vm.LoadAsync();

        Assert.False(vm.IsLoading);
        Assert.Equal(2 * 38 + 1, vm.AllRows.Count);
        Assert.Equal("76 certificates on 2 devices · 6 client · 2 server · 68 CA · 1 devices could not be read", vm.Summary);
        Assert.Equal("Client certificates", vm.VisibleRows[0].Group);
        Assert.Equal("Devices that could not be read", vm.VisibleRows[^1].Group);
        Assert.Equal("Timeout: the device did not answer", vm.VisibleRows[^1].Error);

        vm.SearchText = "trustlix device";
        Assert.Equal(2, vm.VisibleRows.Count);
        Assert.All(vm.VisibleRows, r => Assert.Equal("HTTPS", r.InUse));
        vm.SearchText = devices[1].Address;
        Assert.Equal(38, vm.VisibleRows.Count);
    }

    [Fact]
    public async Task Delete_mode_checks_only_free_certificates_and_returns_aliases_per_device()
    {
        var devices = DialogData.Devices(2);
        var vm = new CertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(DialogData.FixtureReply())), devices, deleteMode: true);
        string? payload = null;
        vm.CloseRequested += (_, p) => payload = p;
        var confirmations = new List<string>();
        vm.Confirm = (_, message, _) =>
        {
            confirmations.Add(message);
            return Task.FromResult(true);
        };
        await vm.LoadAsync();
        Assert.False(vm.CanDelete);
        Assert.Equal("Check the certificates to delete.", vm.DeleteBlockedReason);

        var used = vm.AllRows.First(r => r.Alias == "Trustlix Device HTTPS lUZuIXuu");
        used.IsChecked = true;
        Assert.False(used.IsChecked);
        Assert.Equal("In use by HTTPS: it cannot be deleted.", used.BlockedReason);
        var factory = vm.AllRows.First(r => r.Alias == "Axis device ID RSA-4096 (802.1AR)");
        Assert.False(factory.IsSelectable);

        vm.SearchText = "COMODO ECC";
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(2, vm.CheckedCount);
        Assert.Equal("Delete 2 certificates", vm.DeleteText);
        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal("Delete 2 certificates from 2 devices? A deleted certificate and its key cannot be restored.", Assert.Single(confirmations));
        var result = PkiJson.Deserialize<DeletePayload>(payload);
        Assert.Equal(2, result.Devices.Count);
        Assert.All(result.Devices.Values, refs => Assert.Equal([new CertificateRef("COMODO ECC Certification Authority", true)], refs));

        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(0, vm.CheckedCount);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Five_thousand_devices_load_search_and_select_quickly()
    {
        var reply = DialogData.FixtureReply();
        var devices = DialogData.Devices(5000);
        var vm = new CertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(reply)), devices, deleteMode: true);

        var watch = Stopwatch.StartNew();
        await vm.LoadAsync();
        var load = watch.Elapsed;
        Assert.Equal(5000 * 38, vm.AllRows.Count);

        watch.Restart();
        vm.SearchText = "COMODO";
        var search = watch.Elapsed;
        Assert.Equal(5000 * 2, vm.VisibleRows.Count);

        watch.Restart();
        vm.SelectAllCommand.Execute(null);
        var select = watch.Elapsed;
        Assert.Equal(5000 * 2, vm.CheckedCount);

        watch.Restart();
        vm.SearchText = null;
        var clear = watch.Elapsed;

        output.WriteLine($"5000 devices / 190,000 certificates: load {load.TotalMilliseconds:0} ms, search {search.TotalMilliseconds:0} ms, select all {select.TotalMilliseconds:0} ms, clear search {clear.TotalMilliseconds:0} ms");
        Assert.True(load < TimeSpan.FromSeconds(20), $"load took {load}");
        Assert.True(search < TimeSpan.FromMilliseconds(1000), $"search took {search}");
        Assert.True(select < TimeSpan.FromMilliseconds(500), $"select all took {select}");
        Assert.True(clear < TimeSpan.FromMilliseconds(1000), $"clear took {clear}");
    }

    [Fact]
    public async Task Five_thousand_devices_summary_and_filter_stay_fast()
    {
        // The unit-run variant of the scale check: 5,000 devices with a short certificate list each.
        var reply = PkiJson.Serialize(new CertificateListReply
        {
            Certificates =
            [
                new InstalledCertificate { Alias = "OADM HTTPS 1", Kind = CertificateKind.Server, IssuedBy = "OADM Root CA", IssuedTo = "cam", InUse = ["HTTPS"], FromOadm = true },
                new InstalledCertificate { Alias = "OADM CA 12345678", Kind = CertificateKind.Ca, IssuedBy = "OADM Root CA", IssuedTo = "OADM Root CA", FromOadm = true },
            ],
        });
        var devices = DialogData.Devices(5000);
        var vm = new CertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(reply)), devices, deleteMode: true);
        await vm.LoadAsync();

        var watch = Stopwatch.StartNew();
        vm.SearchText = "10.0.19.";
        vm.SelectAllCommand.Execute(null);
        vm.SearchText = null;
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"took {watch.Elapsed}");
        Assert.Equal(10_000, vm.AllRows.Count);
        Assert.Equal(136, vm.CheckedCount); // the free CA certificate of the 136 devices 10.0.19.0-135
    }
}

public sealed class InstallViewModelTests
{
    [Fact]
    public void Files_are_matched_to_devices_and_problems_block_install()
    {
        using var ca = TestCa.Root("Customer CA");
        using var leaf48 = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.48");
        using var leaf99 = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.99");
        var files = new Dictionary<string, byte[]>
        {
            ["c:/certs/cam48.pfx"] = TestCa.Pfx("secret", leaf48),
            ["c:/certs/cam48-copy.pfx"] = TestCa.Pfx("secret", leaf48),
            ["c:/certs/cam99.pfx"] = TestCa.Pfx("secret", leaf99),
        };
        var devices = new List<IDeviceInfo> { new PkiFakeDevice(Guid.NewGuid()), new PkiFakeDevice(Guid.NewGuid(), "10.0.0.50", "ACCC8E000050") };
        var vm = new InstallCertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(null)), devices, path => files[path]);

        vm.SetFiles([.. files.Keys]);
        Assert.Equal(CertificateFiles.WrongPassword, vm.PasswordError); // no password yet
        Assert.False(vm.CanInstall);

        vm.Password = "secret";
        Assert.Null(vm.PasswordError);
        Assert.Equal("Matched", vm.Files[0].Status);
        Assert.Equal("B8A44F631339 · 10.0.0.48", vm.Files[0].DeviceText);
        Assert.Equal("Same device as another file", vm.Files[1].Status);
        Assert.Equal("No matching device", vm.Files[2].Status);
        Assert.True(vm.Files[2].IsError);
        Assert.Equal("1 of 3 files matched · 1 selected devices get no certificate", vm.Summary);
        Assert.Equal("Some files cannot be installed; see the Status column.", vm.InstallBlockedReason);

        vm.SetFiles(["c:/certs/cam48.pfx"]);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task Install_uploads_the_matched_files_and_returns_the_payload()
    {
        using var ca = TestCa.Root("Customer CA");
        using var leaf = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.48");
        var device = new PkiFakeDevice(Guid.NewGuid());
        var ctx = new FakeDialogContext(_ => Task.FromResult<string?>(null));
        var vm = new InstallCertificatesViewModel(ctx, [device], _ => TestCa.Pfx("pw", leaf)) { Password = "pw" };
        string? payload = null;
        vm.CloseRequested += (_, p) => payload = p;
        vm.Confirm = (_, _, _) => Task.FromResult(true);
        vm.SetFiles(["c:/certs/cam48.pfx"]);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["c:/certs/cam48.pfx"], ctx.Uploaded);
        var result = PkiJson.Deserialize<InstallPayload>(payload);
        Assert.Equal(InstallPurpose.Https, result.Purpose);
        Assert.Equal("pw", result.Password);
        Assert.Equal([new InstallFile(device.Id, "id-cam48.pfx", "cam48.pfx")], result.Files);

        // CA only: no matching, every file for every selected device.
        using var publicCa = X509CertificateLoader.LoadCertificate(ca.RawData);
        var caVm = new InstallCertificatesViewModel(ctx, [device], _ => TestCa.Pfx("pw", publicCa)) { Password = "pw" };
        caVm.SelectedPurpose = caVm.Purposes.Single(p => p.Value == InstallPurpose.CaOnly);
        caVm.CloseRequested += (_, p) => payload = p;
        caVm.SetFiles(["c:/certs/ca.p12"]);
        Assert.Equal("Ready", caVm.Files[0].Status);
        await caVm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(Guid.Empty, PkiJson.Deserialize<InstallPayload>(payload).Files[0].DeviceId);
    }
}

public sealed class InstallCaViewModelTests(ITestOutputHelper output)
{
    [Fact]
    public void Files_become_one_row_per_certificate_with_duplicates_merged_and_problems_in_the_row()
    {
        using var root = TestCa.Root("Acme Root CA");
        using var issuing = TestCa.Intermediate(root);
        using var expired = TestCa.Root("Old CA", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-3));
        using var leaf = TestCa.Leaf(issuing);
        var files = new Dictionary<string, byte[]>
        {
            ["c:/certs/acme-bundle.pem"] = TestCa.Pem(root, issuing),
            ["c:/certs/acme-root.cer"] = root.RawData, // DER, same as the bundle's root
            ["c:/certs/old.crt"] = TestCa.Pem(expired, leaf),
            ["c:/certs/notes.txt"] = TestCa.Ascii("not a certificate"),
        };
        var vm = new InstallCaCertificatesViewModel([new PkiFakeDevice(Guid.NewGuid())], path => files[path]);
        Assert.False(vm.CanInstall);
        Assert.Equal("Choose CA certificate files.", vm.InstallBlockedReason);

        vm.Add(["c:/certs/acme-bundle.pem", "c:/certs/acme-root.cer"]);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("acme-bundle.pem, acme-root.cer", vm.Rows[0].Files);
        Assert.Equal("Acme Root CA", vm.Rows[1].IssuedBy);
        Assert.All(vm.Rows, r => Assert.Equal("Ready", r.Status));
        Assert.True(vm.CanInstall);

        vm.Add(["c:/certs/old.crt", "c:/certs/notes.txt"]);
        Assert.Equal(["Acme Root CA", "Acme Issuing CA", "Old CA", "10.0.0.48", ""], vm.Rows.Select(r => r.Name));
        Assert.Equal(["Ready", "Ready", "Expired", "Not a CA certificate", "Cannot be read"], vm.Rows.Select(r => r.Status));
        Assert.Equal(CaCertificateFiles.NotACertificate, vm.Rows[4].StatusDetail);
        Assert.Equal("notes.txt", vm.Rows[4].Files);
        Assert.Equal("2 CA certificates ready · 3 with a problem (left out)", vm.Summary);
        Assert.True(vm.CanInstall); // rows with a problem are left out, they do not block

        // Choosing the same files again adds nothing; removing rows works for certificates and file errors.
        vm.Add(["c:/certs/acme-bundle.pem", "c:/certs/notes.txt"]);
        Assert.Equal(5, vm.Rows.Count);
        vm.RemoveCommand.Execute(vm.Rows[0]);
        vm.RemoveCommand.Execute(vm.Rows[0]);
        Assert.False(vm.CanInstall);
        Assert.Equal("None of the certificates can be installed; see the Status column.", vm.InstallBlockedReason);
        vm.Add(["c:/certs/acme-root.cer"]); // a removed certificate can be added again
        Assert.True(vm.CanInstall);
        Assert.Equal("Acme Root CA", Assert.Single(vm.UsableCertificates).Name);
    }

    [Fact]
    public async Task Install_confirms_and_returns_the_usable_certificates_for_5000_devices()
    {
        using var root = TestCa.Root("Acme Root CA");
        using var issuing = TestCa.Intermediate(root);
        using var plain = TestCa.Root("Plain", ca: false);
        var devices = DialogData.Devices(5000);
        var watch = Stopwatch.StartNew();
        var vm = new InstallCaCertificatesViewModel(devices, _ => TestCa.Pem(root, issuing, plain));
        string? payload = null;
        (string Title, string Message, string Confirm)? asked = null;
        var answer = false;
        vm.Confirm = (t, m, c) =>
        {
            asked = (t, m, c);
            return Task.FromResult(answer);
        };
        vm.CloseRequested += (_, p) => payload = p;
        vm.Add(["c:/certs/bundle.pem"]);
        Assert.Equal("5,000 selected devices", vm.Scope);

        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Null(payload); // cancelled in the confirmation
        Assert.Equal(("Install CA certificates", "Install 2 CA certificates on 5,000 devices? The devices then trust certificates these CAs issued. 1 row with a problem is left out.", "Install"), asked);

        answer = true;
        await vm.InstallCommand.ExecuteAsync(null);
        watch.Stop();
        var result = PkiJson.Deserialize<InstallCaPayload>(payload);
        Assert.Equal(["Acme Root CA", "Acme Issuing CA"], result.Certificates.Select(c => c.Name));
        Assert.Equal(Oadm.Plugins.Pki.Ca.CaCertificates.ToPem(root), result.Certificates[0].Pem);
        Assert.True(payload!.Length < 10_000); // the payload does not grow with the device count
        output.WriteLine($"5000 devices: add, confirm and payload {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void More_than_the_maximum_blocks_install_with_the_reason_below_the_table()
    {
        var certificates = Enumerable.Range(0, CaCertificateFiles.MaxCertificates + 2).Select(i => TestCa.Root("CA " + i)).ToList();
        try
        {
            var vm = new InstallCaCertificatesViewModel([new PkiFakeDevice(Guid.NewGuid())], _ => TestCa.Pem([.. certificates]));
            vm.Add(["c:/certs/many.pem"]);
            Assert.Equal("At most 150 CA certificates at once; remove 2.", vm.TableError);
            Assert.Equal(vm.TableError, vm.InstallBlockedReason);
            vm.RemoveCommand.Execute(vm.Rows[0]);
            vm.RemoveCommand.Execute(vm.Rows[0]);
            Assert.True(vm.CanInstall);
            Assert.False(vm.HasTableError);
        }
        finally
        {
            certificates.ForEach(c => c.Dispose());
        }
    }
}

/// <summary>The Security dialogs rendered offscreen with the host theme. OADM_SCREENSHOT_DIR writes PNGs.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class HeadlessDialogTests
{
    [Fact]
    public async Task Certificate_dialogs_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable)); // an OADM certificate in use on the first camera
        var view = h.Task<ViewCertificatesTask>(PkiTaskIds.View);
        var cameras = new Dictionary<Guid, FakeCamera>();
        var devices = new List<IDeviceInfo>();
        for (var i = 0; i < 3; i++)
        {
            var device = new PkiFakeDevice(i == 0 ? h.Device.Id : Guid.NewGuid(), $"10.0.0.{48 + i}", i == 0 ? "B8A44F631339" : $"ACCC8E00000{i}");
            devices.Add(device);
            cameras[device.Id] = i == 0 ? h.Camera : new FakeCamera { HasCertApi = i != 2 };
        }

        var ctx = new FakeDialogContext(id => view.QueryAsync(new PkiTaskContext(cameras[id]), devices.Single(d => d.Id == id), PkiQueries.ListCertificates, null, CancellationToken.None));
        using var radiusCa = TestCa.Root("Customer CA");
        using var leaf = RenewDeleteInstallTests.LeafWithKey(radiusCa, "10.0.0.48");
        var pfx = TestCa.Pfx("secret", leaf);

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        try
        {
            await session.Dispatch(async () =>
            {
                // View certificates: 3 devices, one on old firmware.
                var viewWindow = new CertificatesWindow { Width = 1280, Height = 820 };
                var viewVm = new CertificatesViewModel(ctx, devices, deleteMode: false);
                viewWindow.Attach(viewVm);
                Oadm.Client.App.ApplyCrispText(viewWindow);
                viewWindow.Show();
                await viewVm.LoadAsync();
                Pump();
                Capture(viewWindow, outDir, "pki-view-certificates.png");
                Assert.Equal("Needs AXIS OS 11.11 or later.", viewVm.AllRows.Single(r => r.IsError).Error);
                viewWindow.Close();

                // Delete certificates: CA certificates checked, the used and factory ones greyed.
                var deleteWindow = new CertificatesWindow { Width = 1280, Height = 820 };
                var deleteVm = new CertificatesViewModel(ctx, devices.Take(2).ToList(), deleteMode: true);
                deleteWindow.Attach(deleteVm);
                Oadm.Client.App.ApplyCrispText(deleteWindow);
                deleteWindow.Show();
                await deleteVm.LoadAsync();
                foreach (var row in deleteVm.AllRows.Where(r => r.Alias.StartsWith("COMODO", StringComparison.Ordinal)))
                {
                    row.IsChecked = true;
                }

                Pump();
                Capture(deleteWindow, outDir, "pki-delete-certificates.png");
                Assert.True(deleteVm.CanDelete);
                var boxes = deleteWindow.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is CertificateRow).ToList();
                Assert.Contains(boxes, c => !((CertificateRow)c.DataContext!).IsSelectable);
                Assert.All(boxes, c => Assert.Equal(((CertificateRow)c.DataContext!).IsSelectable, c.IsEnabled)); // in use / factory greyed
                Assert.True(deleteWindow.FindControl<DataGrid>("CertificateGrid")!.Columns[0].IsVisible); // check box column
                deleteWindow.Close();

                // Install certificates: one matched file, one without a device.
                var installVm = new InstallCertificatesViewModel(ctx, devices, path => path.EndsWith("cam48.pfx", StringComparison.Ordinal) ? pfx : TestCa.Pfx("secret", RenewDeleteInstallTests.LeafWithKey(radiusCa, "10.0.0.77")));
                var installWindow = new InstallCertificatesWindow { Width = 1040, Height = 680 };
                installWindow.Attach(installVm);
                Oadm.Client.App.ApplyCrispText(installWindow);
                installWindow.Show();
                installVm.SetFiles(["c:/certs/cam48.pfx", "c:/certs/cam77.pfx"]);
                installVm.Password = "secret";
                Pump();
                Capture(installWindow, outDir, "pki-install-manual.png");
                Assert.False(installVm.CanInstall);
                installWindow.Close();

                // Install CA certificates: a bundle, a DER file with the same root (merged), an expired CA and a file that is no certificate.
                using var bundleRoot = TestCa.Root("Customer Root CA");
                using var bundleIssuing = TestCa.Intermediate(bundleRoot, "Customer Issuing CA");
                using var oldCa = TestCa.Root("Old Site CA", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-3));
                var caFiles = new Dictionary<string, byte[]>
                {
                    ["c:/certs/customer-bundle.pem"] = TestCa.Pem(bundleRoot, bundleIssuing),
                    ["c:/certs/customer-root.cer"] = bundleRoot.RawData,
                    ["c:/certs/radius-ca.crt"] = TestCa.Pem(radiusCa),
                    ["c:/certs/old-site.crt"] = TestCa.Pem(oldCa),
                    ["c:/certs/readme.txt"] = TestCa.Ascii("Install these on every camera."),
                };
                var caVm = new InstallCaCertificatesViewModel(devices, path => caFiles[path]);
                var caWindow = new InstallCaCertificatesWindow { Width = 1100, Height = 620 };
                caWindow.Attach(caVm);
                Oadm.Client.App.ApplyCrispText(caWindow);
                caWindow.Show();
                caVm.Add([.. caFiles.Keys]);
                Pump();
                Capture(caWindow, outDir, "pki-install-ca.png");
                Assert.True(caVm.CanInstall);
                Assert.Equal(5, caVm.Rows.Count);
                Assert.Equal(2, caVm.Rows.Count(r => r.IsError));
                caWindow.Close();

                // The 802.1X confirmation (the shared popup).
                var (title, message, confirm) = PkiConfirmations.Dot1xEnable(3);
                var popup = new MessageWindow { Heading = title, Message = message, ConfirmText = confirm, CancelText = "Cancel", Width = 560, Height = 260 };
                Oadm.Client.App.ApplyCrispText(popup);
                popup.Show();
                Pump();
                Capture(popup, outDir, "pki-dot1x-confirm.png");
                Assert.Contains(PkiTaskIds.Dot1xWarning, message, StringComparison.Ordinal);
                popup.Close();
            }, CancellationToken.None);
        }
        finally
        {
            GC.KeepAlive(session); // not disposed: Avalonia's headless Dispose can throw a NullReferenceException on CI
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}
