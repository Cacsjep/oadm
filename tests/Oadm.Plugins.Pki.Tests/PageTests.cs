using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.TrustStore;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>File pickers that answer from the test and record what was saved.</summary>
internal sealed class FakeFiles : IPkiFiles
{
    public Queue<PickedFile?> ToOpen { get; } = new();

    public List<(string Title, string FileName, PkiFileKind Kind, byte[] Data, string? StartFolder)> Saved { get; } = [];

    /// <summary>Folder returned by a save; null = the user cancelled.</summary>
    public string? SaveFolder { get; set; } = "C:/exports";

    public Task<PickedFile?> OpenAsync(string title, PkiFileKind kind) => Task.FromResult(ToOpen.Count > 0 ? ToOpen.Dequeue() : null);

    public Task<string?> SaveAsync(string title, string fileName, PkiFileKind kind, byte[] data, string? startFolder)
    {
        if (SaveFolder is not null)
        {
            Saved.Add((title, fileName, kind, data, startFolder));
        }

        return Task.FromResult(SaveFolder);
    }
}

/// <summary>Dialogs that run the dialog view model in the test instead of a window.</summary>
internal sealed class FakeDialogs : IPkiDialogs
{
    public IPkiFiles Files { get; } = new FakeFiles();

    public FakeFiles FakeFiles => (FakeFiles)Files;

    /// <summary>Answer of the confirmation popups the dialogs show.</summary>
    public bool ConfirmAnswer { get; set; } = true;

    public List<(string Title, string Message)> Confirmations { get; } = [];

    public Func<PkiDialogViewModel, Task>? Drive { get; set; }

    public PkiDialogViewModel? Last { get; private set; }

    public Task ShowGenerateAsync(GenerateCaViewModel dialog) => RunAsync(dialog);

    public Task ShowImportAsync(ImportCaViewModel dialog) => RunAsync(dialog);

    public Task ShowBackupAsync(BackupViewModel dialog) => RunAsync(dialog);

    private async Task RunAsync(PkiDialogViewModel dialog)
    {
        Last = dialog;
        dialog.Files = Files;
        dialog.Confirm = (title, message, _) =>
        {
            Confirmations.Add((title, message));
            return Task.FromResult(ConfirmAnswer);
        };
        if (Drive is { } drive)
        {
            await drive(dialog);
        }
    }
}

/// <summary>This computer's trust store in memory (never the real one).</summary>
internal sealed class FakeClientTrust : ITrustStoreInstaller
{
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? FailWith { get; set; }

    public Task<bool> IsInstalledAsync(X509Certificate2 ca, CancellationToken ct) => Task.FromResult(Installed.Contains(ca.Thumbprint));

    public Task<TrustStoreResult> InstallAsync(X509Certificate2 ca, CancellationToken ct)
    {
        if (FailWith is { } error)
        {
            return Task.FromResult(new TrustStoreResult(false, error));
        }

        Installed.Add(ca.Thumbprint);
        return Task.FromResult(new TrustStoreResult(true));
    }

    public Task<TrustStoreResult> RemoveAsync(X509Certificate2 ca, CancellationToken ct) => Task.FromResult(new TrustStoreResult(false));
}

/// <summary>The page view model against the in-process plugin and the real event hub.</summary>
public sealed class PageViewModelTests : IAsyncLifetime, IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), "oadm-pki-client-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly FakeClientTrust _clientTrust = new();
    private readonly FakeDialogs _dialogs = new();
    private PkiHarness _pki = null!;
    private PluginPageContext _ctx = null!;

    public async Task InitializeAsync()
    {
        _pki = await PkiHarness.StartAsync();
        _ctx = new PluginPageContext(_pki.Plugin, _pki.Hub);
    }

    public async Task DisposeAsync() => await _pki.DisposeAsync();

    public void Dispose() => File.Delete(_settingsFile);

    private PkiViewModel CreateViewModel() => new(_ctx, _clientTrust, new PkiClientSettingsStore(_settingsFile)) { Dialogs = _dialogs };

    [Fact]
    public async Task Activate_reads_the_state_and_fills_the_cards()
    {
        using var vm = CreateViewModel();
        vm.Activate();
        await Wait.UntilAsync(() => vm.HasCa);

        Assert.Equal("OADM Root CA SERVER01", vm.CaName);
        Assert.Equal("Generated", vm.CaSourceText);
        Assert.Equal("RSA 2048", vm.KeyText);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", vm.Fingerprint);
        Assert.True(vm.IsStatusOk);
        Assert.StartsWith("CA valid until ", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("Not installed on the server", vm.ServerTrustText);
        await Wait.UntilAsync(() => vm.ClientTrustText == "Not installed on this computer");
        Assert.Equal("365", vm.DeviceCertValidityDays);
        Assert.Equal("MAC address", vm.Identity.Label);
        Assert.False(vm.HasPreviousCas);
        Assert.False(vm.HasChain);
        Assert.False(vm.HasErrors);
        vm.Deactivate();
    }

    [Fact]
    public async Task Install_in_trusted_root_store_installs_on_the_server_and_on_this_computer()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();

        _clientTrust.FailWith = TrustStoreTexts.Cancelled;
        await vm.InstallTrustCommand.ExecuteAsync(null);
        Assert.True(vm.IsServerTrustOk);
        Assert.Equal("Installed on the server", vm.ServerTrustText);
        Assert.True(vm.IsClientTrustError);
        Assert.Equal("Failed on this computer", vm.ClientTrustText);
        Assert.Equal(TrustStoreTexts.Cancelled, vm.ClientTrustTip);

        _clientTrust.FailWith = null;
        await vm.InstallTrustCommand.ExecuteAsync(null);
        Assert.Equal("Installed on this computer", vm.ClientTrustText);
        Assert.True(vm.IsClientTrustOk);
        Assert.Single(_clientTrust.Installed);
    }

    [Fact]
    public async Task Install_in_trusted_root_store_asks_with_the_ca_fingerprint_first()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        var fingerprint = vm.State!.Ca!.Fingerprint;
        _ctx.ConfirmAnswer = false;

        await vm.InstallTrustCommand.ExecuteAsync(null);

        var (title, message, confirm) = Assert.Single(_ctx.Confirmations);
        Assert.Equal("Install in trusted root store", title);
        Assert.Equal("Install", confirm);
        Assert.Contains(fingerprint, message, StringComparison.Ordinal);
        Assert.Empty(_clientTrust.Installed);
        Assert.NotEqual("Installed on the server", vm.ServerTrustText);
    }

    [Fact]
    public async Task Generate_asks_with_the_device_count_and_replaces_the_ca()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        var oldId = _pki.Service.Ca!.Id;
        await _pki.SetIssuedAsync([
            new IssuedCertificate { SerialNumber = "01", DeviceId = Guid.NewGuid(), CaId = oldId, IssuedUtc = DateTime.UtcNow, NotAfterUtc = DateTime.UtcNow.AddDays(300) },
            new IssuedCertificate { SerialNumber = "02", DeviceId = Guid.NewGuid(), CaId = oldId, IssuedUtc = DateTime.UtcNow, NotAfterUtc = DateTime.UtcNow.AddDays(300) },
        ]);

        _dialogs.ConfirmAnswer = false;
        _dialogs.Drive = async d =>
        {
            var dialog = (GenerateCaViewModel)d;
            Assert.Equal("OADM Root CA SERVER01", dialog.CommonName);
            Assert.Equal("10", dialog.ValidityYears);
            dialog.ValidityYears = "40";
            Assert.Equal("Enter a whole number from 1 to 30.", dialog.ErrorOf(nameof(dialog.ValidityYears)));
            Assert.False(dialog.GenerateCommand.CanExecute(null));
            dialog.ValidityYears = "5";
            dialog.Organization = "Acme";
            await dialog.GenerateCommand.ExecuteAsync(null);
        };
        await vm.GenerateCommand.ExecuteAsync(null);
        var (title, message) = Assert.Single(_dialogs.Confirmations);
        Assert.Equal(PkiViewModel.ReplaceTitle, title);
        Assert.Equal("2 devices have certificates from the current CA. They keep working until they are renewed. Replace the CA?", message);
        Assert.False(_dialogs.Last!.Completed);
        Assert.Equal(oldId, _pki.Service.Ca!.Id); // cancelled: nothing changed

        _dialogs.ConfirmAnswer = true;
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(_dialogs.Last!.Completed);
        Assert.NotEqual(oldId, _pki.Service.Ca!.Id);
        Assert.Equal("OADM Root CA SERVER01", vm.CaName);
        var row = Assert.Single(vm.PreviousCas);
        Assert.Equal(oldId, row.Id);
        Assert.Contains("2 devices", row.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_shows_server_errors_under_the_fields_then_confirms_and_imports()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        using var rsa = RSA.Create(2048);
        using var root = TestCa.Root("Customer Root CA", rsa: rsa);

        // A PEM certificate without key: the key row appears and is required.
        _dialogs.Drive = async d =>
        {
            var dialog = (ImportCaViewModel)d;
            await dialog.ImportCommand.ExecuteAsync(null);
            Assert.Equal("Choose a file.", dialog.FileError);

            dialog.SetFile(new PickedFile("customer.crt", TestCa.Pem(root)));
            Assert.True(dialog.NeedsKeyFile);
            Assert.Equal("Choose the private key of this certificate.", dialog.KeyFileError);
            Assert.False(dialog.ImportCommand.CanExecute(null));

            var encrypted = rsa.ExportEncryptedPkcs8PrivateKeyPem("keypass", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
            dialog.SetKeyFile(new PickedFile("customer.key", TestCa.Ascii(encrypted)));
            dialog.KeyPassword = "wrong";
            await dialog.ImportCommand.ExecuteAsync(null);
            Assert.Equal(CaImporter.WrongKeyPassword, dialog.ErrorOf(nameof(dialog.KeyPassword)));
            Assert.Empty(_dialogs.Confirmations); // checked first, nothing asked yet

            dialog.KeyPassword = "keypass";
            Assert.Null(dialog.ErrorOf(nameof(dialog.KeyPassword)));
            await dialog.ImportCommand.ExecuteAsync(null);
        };
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal("Replace the CA?", Assert.Single(_dialogs.Confirmations).Message);
        Assert.True(_dialogs.Last!.Completed);
        Assert.Equal("Customer Root CA", vm.CaName);
        Assert.Equal("Imported", vm.CaSourceText);
    }

    [Fact]
    public async Task Backup_checks_the_passwords_warns_and_saves_the_file_in_the_remembered_folder()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();

        _dialogs.Drive = async d =>
        {
            var dialog = (BackupViewModel)d;
            dialog.Password = "short";
            Assert.Equal("Use at least 8 characters.", dialog.ErrorOf(nameof(dialog.Password)));
            dialog.Password = "long enough";
            dialog.ConfirmPassword = "long enougH";
            Assert.Equal("The passwords do not match.", dialog.ErrorOf(nameof(dialog.ConfirmPassword)));
            Assert.False(dialog.BackupCommand.CanExecute(null));
            dialog.ConfirmPassword = "long enough";
            await dialog.BackupCommand.ExecuteAsync(null);
        };
        await vm.BackupCommand.ExecuteAsync(null);

        Assert.Equal((BackupViewModel.Title, BackupViewModel.Warning), Assert.Single(_dialogs.Confirmations));
        var saved = Assert.Single(_dialogs.FakeFiles.Saved);
        Assert.Equal("OADM Root CA SERVER01.pfx", saved.FileName);
        Assert.Equal(PkiFileKind.Backup, saved.Kind);
        Assert.Null(saved.StartFolder);
        using (var restored = X509CertificateLoader.LoadPkcs12(saved.Data, "long enough"))
        {
            Assert.True(restored.HasPrivateKey);
        }

        Assert.True(_dialogs.Last!.Completed);

        // The folder is remembered for the next export.
        await vm.ExportCommand.ExecuteAsync("der");
        Assert.Equal("C:/exports", _dialogs.FakeFiles.Saved[^1].StartFolder);
        Assert.Equal("OADM Root CA SERVER01.cer", _dialogs.FakeFiles.Saved[^1].FileName);
        Assert.Equal(PkiFileKind.CertificateDer, _dialogs.FakeFiles.Saved[^1].Kind);
    }

    [Fact]
    public async Task Settings_errors_stay_under_their_fields_and_save_persists()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        Assert.False(vm.HasErrors);

        vm.DeviceCertValidityDays = "abc";
        Assert.Equal("Enter a whole number from 1 to 3650.", vm.ErrorOf(nameof(vm.DeviceCertValidityDays)));
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal("Enter a whole number from 1 to 3650.", vm.FormError);
        vm.DeviceCertValidityDays = "730";

        vm.Identity = vm.IdentityChoices.Single(c => c.Value == Dot1xIdentity.Custom);
        Assert.True(vm.IsCustomIdentity);
        vm.CustomIdentity = "cam-{mac}";
        Assert.Equal("Unknown placeholder {mac}. Use {serial} or {hostName}.", vm.ErrorOf(nameof(vm.CustomIdentity)));
        vm.CustomIdentity = "cam-{serial}";

        vm.RadiusCa = vm.RadiusChoices.Single(c => c.Value == RadiusCaSource.Imported);
        Assert.Equal("Import the CA certificate of the RADIUS server.", vm.ErrorOf(nameof(vm.RadiusCa)));
        await vm.ApplyRadiusFileAsync(new PickedFile("x.crt", [1, 2, 3]));
        Assert.Equal("The file is not a certificate (.crt or .cer, PEM or DER).", vm.ErrorOf(nameof(vm.RadiusCa)));
        using var radius = TestCa.Root("Corp RADIUS CA");
        await vm.ApplyRadiusFileAsync(new PickedFile("radius.cer", radius.RawData));
        Assert.Null(vm.ErrorOf(nameof(vm.RadiusCa)));
        Assert.StartsWith("Corp RADIUS CA, valid until ", vm.RadiusCaLine, StringComparison.Ordinal);

        vm.EapolVersion = vm.EapolChoices[1];
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Saved", vm.SaveResult);
        Assert.False(vm.HasErrors);
        var stored = PkiJson.Deserialize<PkiConfig>(await _pki.Settings.GetAsync(PkiStore.ConfigKey, CancellationToken.None));
        Assert.Equal(730, stored.DeviceCertValidityDays);
        Assert.Equal(2, stored.Dot1x.EapolVersion);
        Assert.Equal("cam-{serial}", stored.Dot1x.CustomIdentity);
        Assert.NotNull(stored.Dot1x.RadiusCaPem);

        await vm.ViewRadiusCaCommand.ExecuteAsync(null);
        Assert.Contains("Subject: CN=Corp RADIUS CA, O=Acme", Assert.Single(_ctx.Messages).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Previous_cas_are_exported_and_removed_after_confirmation()
    {
        await _pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("Second", null, 10, true));
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        var row = Assert.Single(vm.PreviousCas);
        Assert.Equal("OADM Root CA SERVER01", row.Name);

        await vm.ExportPreviousCommand.ExecuteAsync(row);
        Assert.Equal("OADM Root CA SERVER01.crt", Assert.Single(_dialogs.FakeFiles.Saved).FileName);

        _ctx.ConfirmAnswer = false;
        await vm.RemovePreviousCommand.ExecuteAsync(row);
        Assert.Single(vm.PreviousCas);
        _ctx.ConfirmAnswer = true;
        await vm.RemovePreviousCommand.ExecuteAsync(row);
        Assert.Empty(vm.PreviousCas);
        Assert.False(vm.HasPreviousCas);
        Assert.Equal(2, _ctx.Confirmations.Count);
    }

    [Fact]
    public async Task Live_state_events_update_the_page_but_keep_what_the_user_typed()
    {
        using var vm = CreateViewModel();
        vm.Activate();
        await Wait.UntilAsync(() => vm.HasCa && _pki.Hub.WatcherCount(PkiPluginInfo.PluginId) == 1);
        vm.DeviceCertValidityDays = "100";

        await _pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("From another client", null, 10, true));
        await Wait.UntilAsync(() => vm.CaName == "From another client");
        Assert.Equal("100", vm.DeviceCertValidityDays);
        vm.Deactivate();
    }

    [Fact]
    public async Task Fake_mode_simulates_the_server_and_disables_this_computer()
    {
        using var vm = CreateViewModel();
        var state = await _pki.StateAsync();
        await vm.ApplyStateAsync(state with { Simulated = true });
        Assert.Equal("Not installed on this computer", vm.ClientTrustText);
        Assert.Contains("Fake mode", vm.ClientTrustTip, StringComparison.Ordinal);
        Assert.Empty(_clientTrust.Installed);
    }
}

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Pki.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the page and the dialogs offscreen like the client shell. OADM_SCREENSHOT_DIR writes PNGs.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class HeadlessPageTests
{
    [Fact]
    public async Task Page_and_dialogs_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await PkiHarness.StartAsync();
        await pki.InvokeAsync<InstallReply>(PkiMethods.InstallServerTrust);
        var oldId = pki.Service.Ca!.Id;
        var devices = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToList();
        await pki.SetIssuedAsync(devices.Select((d, i) => new IssuedCertificate
        {
            SerialNumber = i.ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
            DeviceId = d,
            CaId = oldId,
            IssuedUtc = DateTime.UtcNow.AddDays(-300),
            NotAfterUtc = DateTime.UtcNow.AddDays(i < 3 ? 20 : 65),
        }));
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);

        // The imported intermediate (customer CA) for the second screenshot.
        using var root = TestCa.Root("Acme Root CA");
        using var intermediate = TestCa.Intermediate(root, "Acme Issuing CA");
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);
        var pfx = TestCa.Pfx("secret", intermediate, rootPublic);
        using var radius = TestCa.Root("Corp RADIUS CA");
        var radiusPem = CaCertificates.ToPem(radius);

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var settingsFile = Path.Combine(Path.GetTempPath(), "oadm-pki-client-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var checks = await session.Dispatch(async () =>
            {
                var view = new PkiView();
                using var vm = new PkiViewModel(ctx, new FakeClientTrust(), new PkiClientSettingsStore(settingsFile));
                var window = Host(view);
                window.Show();
                view.DataContext = vm; // after attaching: no live watch, deterministic content
                await vm.LoadAsync();
                Pump();
                Capture(window, outDir, "pki-page.png");
                // The Device certificates card shows no "Issued: N devices" summary line (user decision).
                var firstSummary = window.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").StartsWith("Issued:", StringComparison.Ordinal));

                // Imported intermediate, previous CA, imported RADIUS CA with a custom identity.
                await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(pfx), "acme.pfx", "secret", null, null, true));
                await pki.InvokeAsync<PkiReply>(PkiMethods.SaveSettings, new SaveSettingsRequest(new PkiConfig
                {
                    Dot1x = new Dot1xConfig { EapolVersion = 2, Identity = Dot1xIdentity.Custom, CustomIdentity = "axis-{serial}", RadiusCa = RadiusCaSource.Imported, RadiusCaPem = radiusPem },
                }));
                using var vm2 = new PkiViewModel(ctx, new FakeClientTrust(), new PkiClientSettingsStore(settingsFile));
                view.DataContext = vm2;
                await vm2.LoadAsync();
                Pump();
                Capture(window, outDir, "pki-page-imported-intermediate.png");
                window.Close();

                // Generate dialog with its defaults.
                var generate = new GenerateCaWindow { Width = 640, Height = 420 };
                PkiDialogs.Attach(generate, new GenerateCaViewModel((_, _) => Task.FromResult(false), "OADM Root CA SERVER01"));
                Oadm.Client.App.ApplyCrispText(generate);
                generate.Show();
                Pump();
                Capture(generate, outDir, "pki-generate-dialog.png");
                generate.Close();

                // Import dialog: a PEM certificate without key, the key's password rejected by the server.
                var importVm = new ImportCaViewModel((_, _) => Task.FromResult(false));
                var import = new ImportCaWindow { Width = 720, Height = 480 };
                PkiDialogs.Attach(import, importVm);
                Oadm.Client.App.ApplyCrispText(import);
                import.Show();
                importVm.SetFile(new PickedFile("acme-issuing-ca.crt", TestCa.Pem(intermediate)));
                await importVm.ImportCommand.ExecuteAsync(null);
                importVm.SetKeyFile(new PickedFile("acme-issuing-ca.key", TestCa.Ascii("-----BEGIN ENCRYPTED PRIVATE KEY-----\nAAAA\n-----END ENCRYPTED PRIVATE KEY-----\n")));
                importVm.KeyPassword = "secret";
                importVm.SetServerErrors(new Dictionary<string, string> { [PkiFields.KeyPassword] = CaImporter.WrongKeyPassword }, null, PkiFields.File);
                Pump();
                Capture(import, outDir, "pki-import-dialog-errors.png");
                var keyPasswordError = importVm.ErrorOf(nameof(ImportCaViewModel.KeyPassword));
                import.Close();

                // Backup dialog: the confirmation does not match.
                var backupVm = new BackupViewModel((_, _) => Task.FromResult(false));
                var backup = new BackupWindow { Width = 640, Height = 380 };
                PkiDialogs.Attach(backup, backupVm);
                Oadm.Client.App.ApplyCrispText(backup);
                backup.Show();
                backupVm.Password = "correct horse";
                backupVm.ConfirmPassword = "correct hose";
                Pump();
                Capture(backup, outDir, "pki-backup-dialog.png");
                var backupError = backupVm.ErrorOf(nameof(BackupViewModel.ConfirmPassword));
                backup.Close();

                return (firstSummary, vm2.HasChain, vm2.ChainText, vm2.HasPreviousCas, keyPasswordError, backupError);
            }, CancellationToken.None);

            Assert.False(checks.firstSummary);
            Assert.True(checks.HasChain);
            Assert.Equal("Issued by Acme Root CA", checks.ChainText);
            Assert.True(checks.HasPreviousCas);
            Assert.Equal(CaImporter.WrongKeyPassword, checks.keyPasswordError);
            Assert.Equal("The passwords do not match.", checks.backupError);
        }
        finally
        {
            // The awaited dispatch may continue on the session's UI thread; Dispose waits for that thread, so dispose elsewhere.
            GC.KeepAlive(session); // not disposed: Avalonia's headless Dispose can throw a NullReferenceException on CI
            File.Delete(settingsFile);
        }
    }

    private static Window Host(Control view)
    {
        // The real host page (title, ui:PageHeader subtitle and status, own cards), like in the client.
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(PkiPluginInfo.PluginId, PkiPluginInfo.DisplayName, view, hasOwnCards: true),
        };
        var window = new Window { Width = 1280, Height = 1240, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window); // same text rendering as the real app windows
        return window;
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
        if (string.IsNullOrEmpty(outDir))
        {
            frame.Dispose();
            return;
        }

        Directory.CreateDirectory(outDir);
        using (frame)
        {
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
