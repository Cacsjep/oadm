using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Backup;
using Oadm.Client.Controls;
using Oadm.Client.Dialogs;
using Oadm.Client.Licensing;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Client.Support;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Licensing;

namespace Oadm.Client.Tests;

/// <summary>
/// The Settings page (docs/spec/settings-backup.md "Settings page"): the search, expanders that open on an error, the one
/// Save, the alphabetical order, the operator page, the backup result chips and the screenshots (OADM_SCREENSHOT_DIR;
/// OADM_TEST_APPEARANCE=Light for the light ones).
/// </summary>
public sealed class SettingsPageTests
{
    [Fact]
    public async Task The_search_shows_matching_rows_opens_their_expanders_and_says_when_nothing_matches()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var page = await SettingsPage.OpenAsync(UserRole.Admin);
            var vm = page.Vm;

            // A row's title: the row and its expander show, the expander opens, everything else hides.
            vm.Filter = "timeout";
            SettingsPage.Pump();
            Assert.True(page.Card("Scan timeout").IsEffectivelyVisible);
            Assert.True(page.Expander("Discovery").IsOpen);
            Assert.False(page.Expander("Discovery").IsExpanded); // open for the search only
            Assert.False(page.Card("Addresses scanned at once").IsEffectivelyVisible);
            Assert.False(page.Card("Listen URL").IsEffectivelyVisible);
            Assert.False(page.Group("Client").IsVisible);
            Assert.True(page.List.HasMatches);

            // An expander's title: all of its rows.
            vm.Filter = "live view";
            SettingsPage.Pump();
            Assert.True(page.Card("Frame rate (adaptive quality)").IsEffectivelyVisible);
            Assert.True(page.Card("Decode video on the graphics card").IsEffectivelyVisible);
            Assert.False(page.Card("Appearance").IsEffectivelyVisible);

            // A group's title: all of its rows.
            vm.Filter = "General";
            SettingsPage.Pump();
            Assert.True(page.Card("Allow Basic login over HTTP").IsEffectivelyVisible);
            Assert.True(page.Card("Parallel tasks per plugin").IsEffectivelyVisible);
            Assert.True(page.Expander("Vulnerabilities").IsEffectivelyVisible);
            Assert.False(page.Group("Server").IsVisible);

            // A hint counts too.
            vm.Filter = "unencrypted";
            SettingsPage.Pump();
            Assert.True(page.Card("Allow Basic login over HTTP").IsEffectivelyVisible);
            Assert.False(page.Card("Parallel tasks per plugin").IsEffectivelyVisible);

            vm.Filter = "no such setting";
            SettingsPage.Pump();
            Assert.False(page.List.HasMatches);
            Assert.Contains(page.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "No setting matches.");
            page.Capture("settings-page-no-match");

            vm.Filter = "";
            SettingsPage.Pump();
            Assert.True(page.List.HasMatches);
            Assert.True(page.Card("Appearance").IsEffectivelyVisible);
            Assert.True(page.Card("Listen URL").IsEffectivelyVisible);
            Assert.All(page.Expanders(), e => Assert.False(e.IsOpen)); // closed again
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_expander_opens_by_itself_when_an_input_inside_shows_an_error()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var page = await SettingsPage.OpenAsync(UserRole.Admin);
            Assert.All(page.Expanders(), e => Assert.False(e.IsExpanded)); // closed at the start

            page.Vm.ScanTimeoutMs = 50;
            SettingsPage.Pump();
            Assert.True(page.Expander("Discovery").IsExpanded);
            Assert.True(page.HasError("Scan timeout"));
            Assert.False(page.Expander("Live view").IsExpanded);
            Assert.False(page.Expander("Automatic backups").IsExpanded);

            page.Vm.Backup!.TimeOfDay = "25:00";
            SettingsPage.Pump();
            Assert.True(page.Expander("Automatic backups").IsExpanded);
            Assert.True(page.HasError("Time"));
            page.Capture("settings-page-errors");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_one_save_saves_the_server_settings_and_the_automatic_backups()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var page = await SettingsPage.OpenAsync(UserRole.Admin);
            var vm = page.Vm;
            var backup = vm.Backup!;
            Assert.True(vm.SaveCommand.CanExecute(null));
            Assert.Null(vm.SaveBlockedReason);

            // Nothing of the schedule changed: only the server settings are written.
            vm.ClockToleranceSeconds = 5;
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(5, (await page.Api.GetSettingsAsync(CancellationToken.None)).ClockToleranceSeconds);
            Assert.Equal(0, page.Api.BackupScheduleSaves);
            Assert.Equal(SettingsViewModel.SavedText, vm.ServerMessage);
            SettingsPage.Pump();
            Assert.True(page.SaveResult.IsEffectivelyVisible);
            Assert.Equal(SettingsViewModel.SavedText, page.SaveResult.Text);

            // An edit takes "Saved." away; a changed schedule goes with the next Save.
            backup.Keep = 9;
            Assert.Null(vm.ServerMessage);
            Assert.True(backup.IsScheduleChanged);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(1, page.Api.BackupScheduleSaves);
            Assert.Equal(9, (await page.Api.GetBackupStateAsync(CancellationToken.None)).Schedule.Keep);
            Assert.False(backup.IsScheduleChanged);
            Assert.Equal(SettingsViewModel.SavedText, vm.ServerMessage);

            // An error of the schedule disables Save and is its tooltip; a server field's error comes first.
            backup.TimeOfDay = "25:00";
            SettingsPage.Pump();
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.False(page.SaveButton.IsEffectivelyEnabled);
            Assert.Equal("Enter a time like 02:00.", vm.SaveBlockedReason);
            Assert.Equal(vm.SaveBlockedReason, ToolTip.GetTip(page.SaveButton));
            vm.ClockToleranceSeconds = null;
            Assert.Equal("Enter a whole number from 1 to 3600.", vm.SaveBlockedReason);
            vm.ClockToleranceSeconds = 2;
            backup.TimeOfDay = "03:00";
            backup.Keep = 400;
            Assert.Equal("Enter a whole number from 1 to 365.", vm.SaveBlockedReason);
            backup.Keep = 7;
            Assert.True(vm.SaveCommand.CanExecute(null));
            Assert.Null(vm.SaveBlockedReason);

            // The server name is not on the page; the loaded one goes back unchanged.
            string name = (await page.Api.GetSettingsAsync(CancellationToken.None)).ServerName;
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(name, (await page.Api.GetSettingsAsync(CancellationToken.None)).ServerName);
            Assert.DoesNotContain(page.Cards(), c => c.Title == "Server name");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Turning_on_automatic_backups_without_a_password_says_so_below_the_password()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var page = await SettingsPage.OpenAsync(UserRole.Admin, api, hasPassword: false);
            var backup = page.Vm.Backup!;
            Assert.False(backup.ScheduleEnabled);

            page.Expander("Automatic backups").GetVisualDescendants().OfType<OnOffSwitch>().First().IsChecked = true;
            SettingsPage.Pump();
            Assert.True(backup.ScheduleEnabled);
            Assert.True(page.Expander("Automatic backups").IsExpanded);
            Assert.True(page.HasError("Password"));
            Assert.Equal("Enter a password for the automatic backups.", page.Vm.SaveBlockedReason);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Every_group_and_expander_is_in_alphabetical_order()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var page = await SettingsPage.OpenAsync(UserRole.Admin);
            var groups = page.Groups().ToList();
            Assert.Equal(["Client", "General", "Server", "Support"], groups.Select(g => g.Title));
            foreach (var group in groups)
            {
                AssertAlphabetical(group.Title!, group.Children.Select(TitleOf).OfType<string>().ToList());
            }

            foreach (var expander in page.Expanders())
            {
                // The backups of the server are a list, newest first (not titles of settings).
                AssertAlphabetical(expander.Title!, expander.Items.OfType<SettingsCard>().Where(c => c.IsShown).Select(c => c.Title!).ToList());
            }

            Assert.Equal(["Backups kept", "Password", "Confirm password", "Time"], page.Expander("Automatic backups").Items.OfType<SettingsCard>().Select(c => c.Title));
            return true;
        }, CancellationToken.None);

        static string? TitleOf(Control control) => control switch
        {
            SettingsCard card => card.Title,
            SettingsExpander expander => expander.Title,
            _ => null,
        };

        static void AssertAlphabetical(string where, List<string> titles)
        {
            var expected = titles.Where(t => t != "Confirm password").Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (titles.Contains("Confirm password"))
            {
                expected.Insert(expected.IndexOf("Password") + 1, "Confirm password"); // the one exception: it follows Password
            }

            Assert.True(expected.SequenceEqual(titles), $"{where}: {string.Join(", ", titles)}");
        }
    }

    [Fact]
    public async Task Operators_see_the_server_values_as_text_without_save_backups_or_support()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var page = await SettingsPage.OpenAsync(UserRole.Operator);
            page.OpenAll();

            Assert.False(page.SaveButton.IsVisible);
            Assert.Contains(page.Group("General").Children.OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Only administrators can change server settings.");
            Assert.True(page.Card("Allowed clock difference (NTP)").IsEffectivelyVisible);
            Assert.Equal("Seconds a device clock may be off before it is reported", page.Card("Allowed clock difference (NTP)").Hint); // hints stay
            Assert.Contains(page.Card("Allowed clock difference (NTP)").GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "2 sec");
            Assert.Contains(page.Card("Allow Basic login over HTTP").GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == OnOffSwitch.OffText);
            Assert.Contains(page.Card("Listen URL").GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == page.Vm.ListenUrl);

            // No input of a server setting, no button but the client's own settings.
            var serverGroups = new[] { page.Group("General"), page.Group("Server") };
            Assert.DoesNotContain(serverGroups.SelectMany(g => g.GetVisualDescendants()).OfType<NumericUpDown>(), n => n.IsEffectivelyVisible);
            Assert.DoesNotContain(serverGroups.SelectMany(g => g.GetVisualDescendants()).OfType<TextBox>(), t => t.IsEffectivelyVisible);
            Assert.DoesNotContain(serverGroups.SelectMany(g => g.GetVisualDescendants()).OfType<OnOffSwitch>(), s => s.IsEffectivelyVisible);
            Assert.DoesNotContain(serverGroups.SelectMany(g => g.GetVisualDescendants()).OfType<Button>(),
                b => b.IsEffectivelyVisible && !b.Classes.Contains("iconOnly")); // only the chevrons and Copy server ID

            Assert.False(page.Expander("Automatic backups").IsVisible);
            Assert.False(page.Card("Backup file").IsVisible);
            Assert.False(page.Expander("Backups on the server").IsVisible);
            Assert.False(page.Group("Support").IsVisible);
            Assert.True(page.Expander("OADM PRO").IsEffectivelyVisible);
            Assert.False(page.Card("License key").IsVisible);
            Assert.Contains(page.Card("Try OADM PRO for 30 days").GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == ProLicenseTexts.TrialAdminLine);
            page.Capture("settings-page-operator", height: 1500);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Backup_results_show_as_chips_on_their_rows_only_when_there_is_something_to_say()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var page = await SettingsPage.OpenAsync(UserRole.Admin, api);

            // A good automatic backup and no restore: no chip.
            Assert.DoesNotContain(page.Expander("Automatic backups").GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible);
            Assert.DoesNotContain(page.Card("Backup file").GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible);

            api.LastAutomaticError = "The disk is full.";
            api.LastRestore = new RestoreReport
            {
                Source = "site-a.oadmbackup",
                FromVersion = "5.1.0",
                RequestedBy = "anna@PC-1",
                Applied = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 18, 14, 2, 0, TimeSpan.Zero)),
                Lines = { "Devices: 24", "Users: 4" },
            };
            await page.Vm.Backup!.LoadAsync();
            SettingsPage.Pump();

            StatusChip failed = page.Expander("Automatic backups").GetVisualDescendants().OfType<StatusChip>().Single(c => c.IsEffectivelyVisible);
            Assert.True(failed.IsError);
            Assert.Equal("The last automatic backup failed: The disk is full.", failed.Text);
            StatusChip restored = page.Card("Backup file").GetVisualDescendants().OfType<StatusChip>().Single(c => c.IsEffectivelyVisible);
            Assert.True(restored.IsOk);
            Assert.StartsWith("Last restore: site-a.oadmbackup", restored.Text, StringComparison.Ordinal);
            Assert.Equal("Devices: 24" + Environment.NewLine + "Users: 4", ToolTip.GetTip(restored));
            page.Capture("settings-page-backup-chips");

            api.LastRestore.Error = "The password is wrong.";
            await page.Vm.Backup.LoadAsync();
            SettingsPage.Pump();
            Assert.True(page.Card("Backup file").GetVisualDescendants().OfType<StatusChip>().Single(c => c.IsEffectivelyVisible).IsError);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_backup_and_a_support_report_show_their_progress_on_their_row_with_the_buttons_disabled()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var page = await SettingsPage.OpenAsync(UserRole.Admin, api);
            var backup = page.Vm.Backup!;

            // The file being written waits at a gate: the backup runs until it opens.
            using var backupFile = new GateStream();
            var dialogs = page.Dialogs;
            dialogs.ShowBackupPasswordAsync(Arg.Do<BackupPasswordViewModel>(p => { p.Password = "correct horse"; p.ConfirmPassword = "correct horse"; })).Returns(true);
            dialogs.CreateFileAsync(default!, default!, default!).ReturnsForAnyArgs(new SavedFile("site-a.oadmbackup", backupFile));
            Task running = backup.BackUpNowCommand.ExecuteAsync(null);
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => backupFile.IsWaiting, "the backup writing its file");
            SettingsPage.Pump();
            Assert.True(backup.IsBusy);
            Assert.True(page.Card("Backup file").GetVisualDescendants().OfType<ProgressRow>().Single().IsEffectivelyVisible);
            Assert.All(page.Card("Backup file").GetVisualDescendants().OfType<Button>(), b => Assert.False(b.IsEffectivelyEnabled));
            page.Capture("settings-page-backup-running");
            backupFile.Open();
            await running;
            SettingsPage.Pump();
            Assert.False(page.Card("Backup file").GetVisualDescendants().OfType<ProgressRow>().Single().IsVisible);
            Assert.Contains(page.Card("Backup file").GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Backup saved as site-a.oadmbackup.");

            using var reportFile = new GateStream();
            Task report = page.Vm.Support!.SaveAsync(new SavedFile("oadm-support.zip", reportFile));
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => reportFile.IsWaiting, "the report writing its file");
            SettingsPage.Pump();
            Assert.True(page.Card("Support report").GetVisualDescendants().OfType<ProgressRow>().Single().IsEffectivelyVisible);
            Assert.All(page.Card("Support report").GetVisualDescendants().OfType<Button>(), b => Assert.False(b.IsEffectivelyEnabled));
            reportFile.Open();
            await report;
            SettingsPage.Pump();
            Assert.Contains(page.Card("Support report").GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Saved oadm-support.zip.");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_page_renders_closed_and_with_every_expander_open()
    {
        await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var page = await SettingsPage.OpenAsync(UserRole.Admin);
            page.Capture("settings-page-closed", height: 1000);

            page.OpenAll();
            Assert.All(page.Cards().Where(c => c.IsShown), c => Assert.True(c.IsEffectivelyVisible, c.Title));
            page.Capture("settings-page-open", height: 2400);
            return true;
        }, CancellationToken.None);
    }

    /// <summary>A file whose first write waits until <see cref="Open"/>: work that writes it stays running meanwhile.</summary>
    private sealed class GateStream : MemoryStream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _waiting;

        public bool IsWaiting => _waiting;

        public void Open() => _gate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _waiting = true;
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _waiting = true;
            _gate.Task.Wait(TimeSpan.FromSeconds(30));
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _waiting = true;
            _gate.Task.Wait(TimeSpan.FromSeconds(30));
            base.Write(buffer);
        }
    }

    /// <summary>The page in a headless window against the fake backend, as the app builds it.</summary>
    internal sealed class SettingsPage : IDisposable
    {
        private readonly DevicesFixture _fixture;
        private readonly ServerConnection _connection;

        private SettingsPage(FakeOadmApi api, DevicesFixture fixture, ServerConnection connection, SettingsViewModel vm, Window window, IDialogService dialogs)
        {
            Api = api;
            Dialogs = dialogs;
            _fixture = fixture;
            _connection = connection;
            Vm = vm;
            Window = window;
        }

        public FakeOadmApi Api { get; }

        /// <summary>The dialogs of the page's backup, license and support parts.</summary>
        public IDialogService Dialogs { get; }

        public SettingsViewModel Vm { get; }

        public Window Window { get; }

        public SettingsList List => Window.GetVisualDescendants().OfType<SettingsList>().Single();

        public Button SaveButton => Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveButton");

        public TextBlock SaveResult => Window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "SaveResult");

        public static async Task<SettingsPage> OpenAsync(UserRole role, FakeOadmApi? api = null, bool hasPassword = true)
        {
            api ??= new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            if (!hasPassword)
            {
                api.BackupHasPassword = false;
                await api.SetBackupScheduleAsync(new BackupSchedule { Enabled = false, TimeOfDay = "02:00", Keep = 7 }, CancellationToken.None);
            }

            var fixture = new DevicesFixture(api);
            var connection = new ServerConnection(fixture.Api, fixture.Store, fixture.Tasks, fixture.Ui, NullLogger<ServerConnection>.Instance);
            var session = new UserSession();
            session.SignIn(new UserInfo { UserName = "anna", Role = role }, "fake");
            var dialogs = Substitute.For<IDialogService>();
            var backup = new BackupViewModel(api, dialogs);
            var vm = new SettingsViewModel(fixture.Api, connection, NullLogger<SettingsViewModel>.Instance, session, ui: new Infrastructure.ImmediateUiDispatcher(),
                backup: backup, license: new LicenseViewModel(api, dialogs, session: session),
                support: new SupportViewModel(api, dialogs, Path.GetTempPath()), devices: fixture.Store); // every part, as the app builds the page
            await vm.LoadAsync();
            var window = new Window { Width = 1100, Height = 1000, Content = new SettingsView { DataContext = vm, Margin = new Thickness(24) } };
            App.ApplyCrispText(window);
            window.Show();
            var page = new SettingsPage(api, fixture, connection, vm, window, dialogs);
            SettingsPage.Pump();
            return page;
        }

        public IEnumerable<SettingsGroup> Groups() => Window.GetVisualDescendants().OfType<SettingsGroup>();

        public SettingsGroup Group(string title) => Groups().Single(g => g.Title == title);

        public IEnumerable<SettingsExpander> Expanders() => Window.GetVisualDescendants().OfType<SettingsExpander>();

        public SettingsExpander Expander(string title) => Expanders().Single(e => e.Title == title);

        /// <summary>The rows of the page (not the headers of the expanders).</summary>
        public IEnumerable<SettingsCard> Cards() => Window.GetVisualDescendants().OfType<SettingsCard>().Where(c => !c.Classes.Contains("header"));

        public SettingsCard Card(string title) => Cards().Single(c => c.Title == title);

        /// <summary>The row's input shows a validation error (red, directly below it).</summary>
        public bool HasError(string title) => Card(title).GetVisualDescendants().OfType<Control>().Any(DataValidationErrors.GetHasErrors);

        public void OpenAll()
        {
            foreach (var expander in Expanders())
            {
                expander.IsExpanded = true;
            }

            Pump();
        }

        public static void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Saves the window as <c>{name}-{dark|light}.png</c> in OADM_SCREENSHOT_DIR (rendered also without it).</summary>
        public void Capture(string name, double? height = null)
        {
            string appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            CaptureFile($"{name}-{appearance}.png", height);
        }

        /// <summary>Saves the window under exactly <paramref name="file"/> (the website's screenshots, website/scripts/screens.json).</summary>
        public void CaptureFile(string file, double? height = null)
        {
            if (height is { } h)
            {
                Window.Height = h;
            }

            Pump();
            WriteableBitmap? frame = Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, file), PngBitmapEncoderOptions.Default);
            }
        }

        public void Dispose()
        {
            Window.Close();
            _connection.Dispose();
            _fixture.Dispose();
        }
    }
}
