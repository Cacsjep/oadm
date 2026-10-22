using System.IO.Compression;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Backup;
using Oadm.Client.Dialogs;
using Oadm.Client.Licensing;
using Oadm.Client.Support;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>The Settings page's Support card: the server's support report plus this computer's logs in one ZIP.</summary>
public sealed class SupportCardTests
{
    private static Oadm.Client.Shell.UserSession Session(UserRole role)
    {
        var session = new Oadm.Client.Shell.UserSession();
        session.SignIn(new UserInfo { UserName = "anna", Role = role }, "fake");
        return session;
    }

    [Fact]
    public async Task TheReportHasTheServerPartAndTheClientLogs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-support-client-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(folder, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(logs, "client-20261022.log"), "2026-10-22 09:00:00.000 +02:00 [INF] Oadm.Client: started\n");
            await File.WriteAllTextAsync(Path.Combine(logs, "other.txt"), "not a log");
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var support = new SupportViewModel(api, Substitute.For<IDialogService>(), logs);
            var target = Path.Combine(folder, "report.zip");

            await support.SaveAsync(new SavedFile("report.zip", new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite)));

            Assert.Equal(("Saved report.zip.", false, false), (support.Status, support.StatusIsError, support.IsBusy));
            using var zip = ZipFile.OpenRead(target);
            Assert.Equal(["client-logs/client-20261022.log", "logs/oadm-server-20261022.log", "summary.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AFailedReportSaysWhyAndRemovesTheFile()
    {
        var api = Substitute.For<IOadmApi>();
        api.CreateSupportReportAsync(Arg.Any<CancellationToken>()).Returns<Task<Oadm.Sdk.Plugins.PluginDownload>>(_ =>
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "Only administrators can do this.")));
        var support = new SupportViewModel(api, Substitute.For<IDialogService>(), Path.GetTempPath());
        var deleted = false;

        await support.SaveAsync(new SavedFile("report.zip", new MemoryStream()) { Delete = () => { deleted = true; return Task.CompletedTask; } });

        Assert.True(support.StatusIsError);
        Assert.False(string.IsNullOrEmpty(support.Status));
        Assert.True(deleted);
        Assert.True(support.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSettingsPageShowsTheSupportCardToAdministratorsOnly()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new Oadm.Client.Shell.ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Shell.ServerConnection>.Instance);
            var session = Session(UserRole.Admin);
            var support = new SupportViewModel(api, Substitute.For<IDialogService>(), Path.GetTempPath());
            var settings = new Oadm.Client.Settings.SettingsViewModel(f.Api, connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Settings.SettingsViewModel>.Instance,
                session, backup: new BackupViewModel(api, Substitute.For<IDialogService>()),
                license: new LicenseViewModel(api, Substitute.For<IDialogService>(), session: session), support: support);
            await settings.LoadAsync();
            var window = new Window { Width = 1520, Height = 1100, Content = new Oadm.Client.Settings.SettingsView { DataContext = settings, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<SupportCardView>(), c => c.IsEffectivelyVisible);
            Save(window, outDir, "client-page-settings-support.png");

            var target = Path.Combine(Path.GetTempPath(), "oadm-support-shot-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await support.SaveAsync(new SavedFile("oadm-support-acs-20261022-0930.zip", new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite)));
                Pump();
                Save(window, outDir, "client-page-settings-support-saved.png");
            }
            finally
            {
                File.Delete(target);
            }

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);

        // Operators do not see it.
        var api2 = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
        using var f2 = new DevicesFixture(api2);
        using var connection2 = new Oadm.Client.Shell.ServerConnection(f2.Api, f2.Store, f2.Tasks, f2.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Shell.ServerConnection>.Instance);
        var operatorSettings = new Oadm.Client.Settings.SettingsViewModel(f2.Api, connection2, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Settings.SettingsViewModel>.Instance,
            Session(UserRole.Operator), support: new SupportViewModel(api2, Substitute.For<IDialogService>(), Path.GetTempPath()));
        Assert.False(operatorSettings.ShowSupport);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
