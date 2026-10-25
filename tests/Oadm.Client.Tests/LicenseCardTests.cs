using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Backup;
using Oadm.Client.Dialogs;
using Oadm.Client.Licensing;
using Oadm.Contracts.V1;
using Oadm.Sdk.Licensing;

namespace Oadm.Client.Tests;

/// <summary>The Settings page's License card: the server ID, the OADM PRO state, the trial, import, remove and the oadm.app link.</summary>
public sealed class LicenseCardTests
{
    private static string Sign(bool trial = false, string server = FakeOadmApi.FakeServerId) =>
        ProLicenseFile.Sign(FakeOadmApi.SampleLicenseKeys.PrivateKeyPem, server, "ACME GmbH", "Up to 500 devices", 500,
            DateOnly.FromDateTime(DateTime.Today), new DateOnly(2099, 12, 31), trial);

    private static Oadm.Client.Shell.UserSession Session(UserRole role)
    {
        var session = new Oadm.Client.Shell.UserSession();
        session.SignIn(new UserInfo { UserName = "anna", Role = role }, "fake");
        return session;
    }

    [Fact]
    public async Task Import_shows_the_plan_and_remove_asks_first()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(false, true);
        var launcher = Substitute.For<IUrlLauncher>();
        var license = new LicenseViewModel(api, dialogs, launcher: launcher, session: Session(UserRole.Admin));

        await license.LoadAsync();
        Assert.Equal((FakeOadmApi.FakeServerId, ProLicenseTexts.NoLicense), (license.ServerId, license.StatusText));
        Assert.False(license.HasLicense);
        Assert.True(license.ShowStartTrial);
        Assert.True(license.ImportCommand.CanExecute(null));

        await license.ImportTextAsync(Sign());

        Assert.Equal("Up to 500 devices, until 2099-12-31", license.StatusText);
        Assert.Equal("Licensed to ACME GmbH", license.CustomerText);
        Assert.True(license.IsActive);
        Assert.False(license.ShowActivate);
        Assert.False(license.ShowStartTrial);
        Assert.True(license.CanRemove);
        Assert.False(license.MessageIsError);

        await license.RemoveCommand.ExecuteAsync(null); // "Keep"
        Assert.True(license.IsActive);
        await license.RemoveCommand.ExecuteAsync(null);
        Assert.False(license.HasLicense);
        await dialogs.Received(2).ConfirmAsync("Remove license", Arg.Any<string>(), "Remove license");

        await license.ActivateCommand.ExecuteAsync(null);
        await launcher.Received(1).OpenAsync(new Uri("https://oadm.app/activate/?server=" + FakeOadmApi.FakeServerId));
        Assert.Single(launcher.ReceivedCalls()); // the trial never goes online
    }

    [Fact]
    public async Task Start_trial_runs_once_in_the_app_and_others_are_told_who_can()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var operatorCard = new LicenseViewModel(api, Substitute.For<IDialogService>(), session: Session(UserRole.Operator));
        await operatorCard.LoadAsync();
        Assert.Equal((true, false, true), (operatorCard.ShowStartTrial, operatorCard.ShowTrialButton, operatorCard.ShowTrialAdminLine));
        Assert.False(operatorCard.StartTrialCommand.CanExecute(null));

        var license = new LicenseViewModel(api, Substitute.For<IDialogService>(), session: Session(UserRole.Admin));
        await license.LoadAsync();
        Assert.Equal((true, true, false), (license.ShowStartTrial, license.ShowTrialButton, license.ShowTrialAdminLine));
        var changed = 0;
        license.LicenseChanged += (_, _) => changed++;

        await license.StartTrialCommand.ExecuteAsync(null);

        var ends = LicenseFile.Day(DateOnly.Parse(license.State.Expires, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Trial, until " + ends, license.StatusText);
        Assert.Equal((true, false, false, true), (license.IsTrial, license.ShowStartTrial, license.CanRemove, license.ShowActivate));
        Assert.Equal(1, changed); // the plugin list and the task catalog follow

        // A plugin page (the 802.1X server's off state) asks through the same card: refused, the card keeps the trial.
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => license.StartTrialFromPageAsync(CancellationToken.None));
        Assert.Equal(ProLicenseTexts.TrialUsed, refused.Status.Detail);
        Assert.True(license.IsTrial);
        await operatorCard.LoadAsync();
        Assert.False(operatorCard.ShowTrialAdminLine);
    }

    [Fact]
    public async Task A_page_starts_the_trial_through_the_card()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var license = new LicenseViewModel(api, Substitute.For<IDialogService>(), session: Session(UserRole.Admin));
        var changed = 0;
        license.LicenseChanged += (_, _) => changed++;

        var info = await license.StartTrialFromPageAsync(CancellationToken.None);

        Assert.Equal((Oadm.Sdk.Licensing.ProLicenseStatus.Active, true, false), (info.Status, info.IsTrial, info.TrialAvailable));
        Assert.Equal(license.State.Expires, info.Expires is { } day ? LicenseFile.Day(day) : null);
        Assert.True(license.IsTrial);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task A_refused_key_says_why_and_a_trial_shows_as_trial()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var license = new LicenseViewModel(api, Substitute.For<IDialogService>(), session: Session(UserRole.Admin));
        await license.LoadAsync();

        await license.ImportTextAsync(Sign(server: "11111111-2222-3333-4444-555555555555"));
        Assert.Equal((ProLicenseTexts.WrongServer, true), (license.Message, license.MessageIsError));
        Assert.False(license.HasLicense);

        await license.ImportTextAsync(Sign(trial: true));
        Assert.Equal((true, false, true), (license.IsTrial, license.IsActive, license.ShowActivate));
        Assert.Equal("Trial, until 2099-12-31", license.StatusText);
    }

    [Fact]
    public async Task Operators_read_the_card_without_import_or_remove()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        await api.ImportLicenseAsync(Sign(), CancellationToken.None);
        var license = new LicenseViewModel(api, Substitute.For<IDialogService>(), session: Session(UserRole.Operator));
        await license.LoadAsync();

        Assert.True(license.IsActive);
        Assert.False(license.IsAdmin);
        Assert.False(license.ImportCommand.CanExecute(null));
        Assert.False(license.CanRemove);
        Assert.False(license.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_settings_page_shows_the_license_card_below_backup_and_restore()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new Oadm.Client.Shell.ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Shell.ServerConnection>.Instance);
            var session = Session(UserRole.Admin);
            var license = new LicenseViewModel(api, Substitute.For<IDialogService>(), session: session);
            var settings = new Oadm.Client.Settings.SettingsViewModel(f.Api, connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Settings.SettingsViewModel>.Instance,
                session, backup: new BackupViewModel(api, Substitute.For<IDialogService>()), license: license);
            await settings.LoadAsync();
            Assert.Equal(FakeOadmApi.FakeServerId, license.ServerId); // loaded with the page
            var window = new Window { Width = 1520, Height = 1000, Content = new Oadm.Client.Settings.SettingsView { DataContext = settings, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<LicenseCardView>(), c => c.IsEffectivelyVisible);
            foreach (var card in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Oadm.Client.Controls.CollapsibleCard>().Where(c => c.Title == "License"))
            {
                card.IsExpanded = true; // the card opens like a user would
            }

            Pump();
            Save(window, outDir, "client-page-settings-license-none.png"); // with Start 30-day trial
            Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == ProLicenseTexts.TrialOffer);

            await license.StartTrialCommand.ExecuteAsync(null);
            Pump();
            Assert.True(license.IsTrial);
            Save(window, outDir, "client-page-settings-license-trial-started.png");

            await license.ImportTextAsync(Sign());
            Pump();
            Save(window, outDir, "client-page-settings-license-active.png");

            await license.ImportTextAsync(Sign(trial: true));
            Pump();
            Assert.True(license.IsTrial);
            Save(window, outDir, "client-page-settings-license-trial.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
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
