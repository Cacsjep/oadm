using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Backup;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>The Settings page's backup card and the restore dialog against the fake backend.</summary>
public sealed class BackupCardTests
{
    private static (BackupViewModel Vm, FakeOadmApi Api, IDialogService Dialogs) Create()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(true);
        return (new BackupViewModel(api, dialogs), api, dialogs);
    }

    [Fact]
    public async Task Loads_the_backups_on_the_server_and_the_schedule()
    {
        var (vm, api, _) = Create();
        using (api)
        {
            await vm.LoadAsync();

            Assert.Equal(2, vm.Backups.Count);
            Assert.Equal("Automatic", vm.Backups[0].KindText);
            Assert.EndsWith("MB", vm.Backups[0].SizeText, StringComparison.Ordinal);
            Assert.True(vm.ScheduleEnabled);
            Assert.Equal("02:00", vm.TimeOfDay);
            Assert.Equal("Unchanged", vm.PasswordPlaceholder);
            Assert.StartsWith("Last automatic backup: ", vm.LastAutomaticText, StringComparison.Ordinal);
            Assert.False(vm.HasLastRestore);
        }
    }

    [Fact]
    public async Task The_schedule_checks_time_and_passwords_below_the_fields()
    {
        var (vm, api, _) = Create();
        using (api)
        {
            await vm.LoadAsync();
            vm.TimeOfDay = "25:00";
            Assert.Equal("Enter a time like 02:00.", vm.ErrorOf(nameof(vm.TimeOfDay)));
            Assert.False(vm.SaveScheduleCommand.CanExecute(null));

            vm.TimeOfDay = "03:30";
            vm.SchedulePassword = "short";
            Assert.Equal("Use at least 8 characters.", vm.ErrorOf(nameof(vm.SchedulePassword)));
            vm.SchedulePassword = "a new password";
            vm.ScheduleConfirmPassword = "another one";
            Assert.Equal("The passwords do not match.", vm.ErrorOf(nameof(vm.ScheduleConfirmPassword)));
            vm.ScheduleConfirmPassword = "a new password";

            await vm.SaveScheduleCommand.ExecuteAsync(null);

            Assert.Equal("Automatic backups saved.", vm.Status);
            Assert.Equal("03:30", vm.TimeOfDay);
            Assert.Empty(vm.SchedulePassword); // never kept in the form
        }
    }

    [Fact]
    public async Task Back_up_now_asks_for_the_password_and_saves_the_file()
    {
        var (vm, api, dialogs) = Create();
        using (api)
        {
            dialogs.ShowBackupPasswordAsync(Arg.Do<BackupPasswordViewModel>(p => { p.Password = "correct horse"; p.ConfirmPassword = "correct horse"; })).Returns(true);
            var target = new MemoryStream();
            dialogs.CreateFileAsync(default!, default!, default!).ReturnsForAnyArgs(new SavedFile("site-a.oadmbackup", target));

            await vm.BackUpNowCommand.ExecuteAsync(null);

            Assert.Equal("Backup saved as site-a.oadmbackup.", vm.Status);
            Assert.False(vm.StatusIsError);
            Assert.StartsWith("OADMBACKUP", System.Text.Encoding.UTF8.GetString(target.ToArray()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_failed_backup_removes_the_cut_file_and_says_why()
    {
        var (vm, api, dialogs) = Create();
        using (api)
        {
            // The fake refuses a password under 8 characters, like the server.
            dialogs.ShowBackupPasswordAsync(Arg.Do<BackupPasswordViewModel>(p => p.Password = "short")).Returns(true);
            var deleted = false;
            dialogs.CreateFileAsync(default!, default!, default!).ReturnsForAnyArgs(new SavedFile("x.oadmbackup", new MemoryStream()) { Delete = () => { deleted = true; return Task.CompletedTask; } });

            await vm.BackUpNowCommand.ExecuteAsync(null);

            Assert.True(deleted);
            Assert.True(vm.StatusIsError);
            Assert.Equal("Use at least 8 characters.", vm.Status);
        }
    }

    [Fact]
    public void The_backup_password_is_checked_and_confirmed()
    {
        var vm = new BackupPasswordViewModel();
        Assert.False(vm.OkCommand.CanExecute(null));
        vm.Password = "correct horse";
        vm.ConfirmPassword = "correct hors";
        Assert.False(vm.OkCommand.CanExecute(null));
        vm.ConfirmPassword = "correct horse";
        Assert.True(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public async Task Restore_checks_the_backup_first_then_asks_and_restores()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!).ReturnsForAnyArgs(true);
        var restore = new RestoreViewModel(api, dialogs, new RestoreRequest { Name = "oadm-backup-20261013-020000-automatic.oadmbackup" }, "oadm-backup-20261013-020000-automatic.oadmbackup", activeTasks: 2);
        bool? closed = null;
        restore.CloseRequested += (_, ok) => closed = ok;

        Assert.False(restore.RestoreCommand.CanExecute(null));
        restore.Password = "wrong";
        await restore.CheckCommand.ExecuteAsync(null);
        Assert.Equal("The password is wrong.", restore.ErrorOf(nameof(restore.Password)));
        Assert.False(restore.IsChecked);

        restore.Password = "correct horse";
        await restore.CheckCommand.ExecuteAsync(null);
        Assert.True(restore.IsChecked);
        Assert.Contains("Devices: 24", restore.Lines);
        Assert.StartsWith("Backup of site-a from ", restore.Summary, StringComparison.Ordinal);
        Assert.Contains("2 running tasks are cancelled", restore.Consequences, StringComparison.Ordinal);

        await restore.RestoreCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal("oadm-backup-20261013-020000-automatic.oadmbackup", Assert.Single(api.Restores).Name);
        Assert.Equal("The server restarts and restores the backup. Log in again in a minute.", restore.ResultMessage);
        await dialogs.Received(1).ConfirmAsync("Restore backup", Arg.Any<string>(), "Restore");
    }

    [Fact]
    public async Task A_backup_of_a_newer_oadm_is_named_below_the_password()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var restore = new RestoreViewModel(api, Substitute.For<IDialogService>(), new RestoreRequest { FileId = "future-1" }, "future.oadmbackup", 0);
        restore.Password = "correct horse";

        await restore.CheckCommand.ExecuteAsync(null);

        Assert.Contains("Update OADM to 9.0.0 or later first.", restore.ErrorOf(nameof(restore.Password)), StringComparison.Ordinal);
        Assert.False(restore.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_asks_first_and_reloads_the_list()
    {
        var (vm, api, dialogs) = Create();
        using (api)
        {
            await vm.LoadAsync();
            await vm.DeleteCommand.ExecuteAsync(vm.Backups[1]);
            Assert.Single(vm.Backups);
            await dialogs.Received(1).ConfirmAsync("Delete backup", Arg.Any<string>(), "Delete");
        }
    }

    [Fact]
    public async Task The_card_and_the_restore_dialog_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var dialogs = Substitute.For<IDialogService>();
            var vm = new BackupViewModel(api, dialogs);
            await vm.LoadAsync();
            var window = new Window { Width = 820, Height = 900, Content = new ScrollViewer { Content = new BackupCardView { DataContext = vm, Margin = new Avalonia.Thickness(16) } } };
            window.Show();
            Pump();
            foreach (var card in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Oadm.Client.Controls.CollapsibleCard>())
            {
                card.IsExpanded = true; // the card starts closed; the screenshot shows it open, as a user opens it
            }

            Pump();
            Save(window, outDir, "settings-backup.png");
            window.Close();

            var restore = new RestoreViewModel(api, dialogs, new RestoreRequest { Name = vm.Backups[0].Name }, vm.Backups[0].Name, 2) { Password = "correct horse" };
            await restore.CheckCommand.ExecuteAsync(null);
            var dialog = new RestoreWindow();
            dialog.Attach(restore);
            dialog.Show();
            Pump();
            Save(dialog, outDir, "settings-restore.png");
            dialog.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_settings_page_shows_backup_and_restore_in_a_second_column()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new Oadm.Client.Shell.ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Shell.ServerConnection>.Instance);
            var session = new Oadm.Client.Shell.UserSession();
            session.SignIn(new UserInfo { UserName = "anna", Role = UserRole.Admin }, "fake");
            var settings = new Oadm.Client.Settings.SettingsViewModel(f.Api, connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Settings.SettingsViewModel>.Instance,
                session, backup: new BackupViewModel(api, Substitute.For<IDialogService>()));
            await settings.LoadAsync();
            Assert.True(settings.ShowBackup);
            Assert.Equal(2, settings.Backup!.Backups.Count); // loaded with the page
            var window = new Window { Width = 1520, Height = 1000, Content = new Oadm.Client.Settings.SettingsView { DataContext = settings, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            Save(window, outDir, "client-page-settings.png");
            var card = System.Linq.Enumerable.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<BackupCardView>());
            Assert.True(card.Bounds.Width > 600); // its own column, not squeezed
            window.Width = 2200;
            Pump();
            Save(window, outDir, "client-page-settings-wide.png");
            window.Width = 1520;
            window.Height = 1350;
            Pump();
            Save(window, outDir, "client-page-settings-server.png"); // the Server card with the server ID
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
