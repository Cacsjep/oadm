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

    public Task ShowCreateCaAsync(CreateCaViewModel dialog) => RunAsync(dialog);

    public Task ShowImportAsync(ImportCaViewModel dialog) => RunAsync(dialog);

    public Task ShowBackupAsync(BackupViewModel dialog) => RunAsync(dialog);

    public Task ShowImportAuthorityAsync(ImportAuthorityViewModel dialog) => RunAsync(dialog);

    public List<Oadm.Sdk.Client.Controls.CertificateDetails> Shown { get; } = [];

    public Task ShowCertificateAsync(Oadm.Sdk.Client.Controls.CertificateDetails certificate)
    {
        Shown.Add(certificate);
        return Task.CompletedTask;
    }

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

        Assert.True(vm.IsStatusOk);
        Assert.StartsWith("CA valid until ", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.HasErrors);
        vm.Deactivate();
    }

    [Fact]
    public async Task Install_in_trusted_root_store_asks_with_the_ca_fingerprint_first()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        await vm.Authorities.LoadAsync();
        var fingerprint = vm.State!.Ca!.Fingerprint;
        _ctx.ConfirmAnswer = false;

        await vm.InstallTrustCommand.ExecuteAsync(vm.Authorities.Rows.Single(r => r.AuthorityKind == Oadm.Sdk.Certificates.CertificateAuthorityKind.Oadm));

        var (title, message, confirm) = Assert.Single(_ctx.Confirmations);
        Assert.Equal("Install in trusted root store", title);
        Assert.Equal("Install", confirm);
        Assert.Contains(fingerprint, message, StringComparison.Ordinal);
        Assert.Empty(_clientTrust.Installed);
    }

    [Fact]
    public async Task Import_with_its_key_shows_server_errors_under_the_fields_then_adds_the_ca()
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

        // Decision 2026-10-25: the CA is added to the list and can issue; the OADM CA stays, so nothing is asked.
        Assert.Empty(_dialogs.Confirmations);
        Assert.True(_dialogs.Last!.Completed);
        var imported = Assert.Single(vm.Authorities.Rows, r => r.AuthorityKind == Oadm.Sdk.Certificates.CertificateAuthorityKind.Imported);
        Assert.Equal("Customer Root CA", imported.Name);
        Assert.Equal(["Root CA", "Imported", "Can issue"], imported.Badges);
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
        await vm.Authorities.LoadAsync();
        var oadmRow = vm.Authorities.Rows.Single(r => r.AuthorityKind == Oadm.Sdk.Certificates.CertificateAuthorityKind.Oadm);
        await vm.BackupCommand.ExecuteAsync(oadmRow);

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
        await vm.Authorities.ExportDerCommand.ExecuteAsync(oadmRow);
        Assert.Equal("C:/exports", _dialogs.FakeFiles.Saved[^1].StartFolder);
        Assert.Equal("OADM Root CA SERVER01.cer", _dialogs.FakeFiles.Saved[^1].FileName);
        Assert.Equal(PkiFileKind.CertificateDer, _dialogs.FakeFiles.Saved[^1].Kind);
    }

    [Fact]
    public async Task Live_state_events_update_the_page()
    {
        using var vm = CreateViewModel();
        vm.Activate();
        await Wait.UntilAsync(() => vm.HasCa && _pki.Hub.WatcherCount(PkiPluginInfo.PluginId) == 1);

        await _pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority, new CreateCaRequest("From another client", false, null, CaKeys.Default, 10));
        await Wait.UntilAsync(() => vm.State?.Authorities.Any(a => a.Name == "From another client") == true);
        vm.Deactivate();
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
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry)));

    /// <summary>One headless application per test process (a second one fails to register its resources); never disposed.</summary>
    public static HeadlessUnitTestSession Session => Shared.Value;

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

        var session = HeadlessEntry.Session;
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
                // The Device certificates card shows no "Issued: N devices" summary line.
                var firstSummary = window.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").StartsWith("Issued:", StringComparison.Ordinal));

                // Imported intermediate, previous CA, imported RADIUS CA with a custom identity.
                await pki.InvokeAsync<PkiReply>(PkiMethods.Import, new ImportRequest(Convert.ToBase64String(pfx), "acme.pfx", "secret", null, null));
                var dot1x = pki.Policies.FirstOf(Policies.PolicyKinds.Dot1xCertificate)!;
                await pki.InvokeAsync<Policies.PolicySaveReply>(Policies.PolicyMethods.SavePolicy, dot1x with
                {
                    EapolVersion = 2, Identity = Dot1xIdentity.Custom, CustomIdentity = "axis-{serial}", RadiusCa = RadiusCaSource.Imported, RadiusCaPem = radiusPem,
                });
                using var vm2 = new PkiViewModel(ctx, new FakeClientTrust(), new PkiClientSettingsStore(settingsFile));
                view.DataContext = vm2;
                await vm2.LoadAsync();
                await vm2.Authorities.LoadAsync();
                Pump();
                Capture(window, outDir, "pki-page-imported-intermediate.png");
                window.Close();

                // Create CA dialog with its defaults.
                var generate = new CreateCaWindow { Width = 640, Height = 420 };
                PkiDialogs.Attach(generate, new CreateCaViewModel((_, _) => Task.FromResult(false), [new Choice(Oadm.Sdk.Certificates.CertificateAuthorityInfo.OadmId, "OADM Root CA SERVER01")]));
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

                var importedRow = vm2.Authorities.Rows.FirstOrDefault(r => r.AuthorityKind == Oadm.Sdk.Certificates.CertificateAuthorityKind.Imported);
                return (firstSummary, Imported: importedRow?.Badges, ImportedIssuer: importedRow?.Issuer, keyPasswordError, backupError);
            }, CancellationToken.None);

            // The intermediate with its key is a CA of the list that can issue; the OADM CA stays as it was.
            Assert.False(checks.firstSummary);
            Assert.Equal(["Intermediate CA", "Imported", "Can issue"], checks.Imported);
            Assert.Equal("Acme Root CA", checks.ImportedIssuer);
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

    internal static Window Host(Control view, int width, int height)
    {
        var window = Host(view);
        window.Width = width;
        window.Height = height;
        return window;
    }

    internal static Window Host(Control view)
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

    internal static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    internal static void Capture(Window window, string? outDir, string name)
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
