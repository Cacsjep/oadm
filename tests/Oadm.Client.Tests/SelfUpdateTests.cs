using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Updates;
using Oadm.Contracts.Updates;
using Oadm.Contracts.V1;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// Self update in the client (docs/spec/self-update.md): which button the new version dialog offers, its steps, the server
/// away for its installer, the results, Update client and the client's helper. Never a real installer or download.
/// </summary>
public sealed class SelfUpdateTests
{
    private static readonly ReleaseInfo Latest = new(new Version(4, 9, 0), "V4.9.0", "### Added\n\n- Administrators update the server from inside OADM.\n");

    private static UpdateInfo Server(string version = "4.8.0", bool installed = true, int tasks = 0, bool sameComputer = false) =>
        new() { ServerVersion = version, Platform = "windows", Installed = installed, ActiveTasks = tasks, SameComputer = sameComputer, LatestVersion = "4.9.0" };

    private static InstallFacts Facts(UpdateOs os, string folder, params string[] files) => new(os, folder, @"C:\Program Files", false, f => files.Contains(f));

    private static ClientUpdater Updater(InstallFacts facts, IClientHelperLauncher? launcher = null, FakeFeed? feed = null, string folder = "update", UpdatePlatform? platform = null)
    {
        feed ??= FakeFeed.With("OADM-4.9.0-win-x64.msi", "the msi");
        return new ClientUpdater(feed, new UpdateDownloader(feed, new FakeDisk()), launcher ?? new FakeLauncher(), facts,
            platform ?? new UpdatePlatform(facts.Os, Architecture.X64), folder, "4.8.0");
    }

    private static ClientUpdater InstalledWindowsClient(IClientHelperLauncher? launcher = null, FakeFeed? feed = null, string folder = "update") =>
        Updater(Facts(UpdateOs.Windows, @"C:\Program Files\OADM\Client\"), launcher, feed, folder);

    private static UpdateNotifier Notifier(ClientUpdater? client = null, IOadmApi? api = null, bool admin = true, ServerUpdateTracker? tracker = null, IDialogService? dialogs = null)
    {
        var session = new Shell.UserSession();
        session.SignIn(new UserInfo { UserName = admin ? "admin" : "op", Role = admin ? UserRole.Admin : UserRole.Operator, AllPermissions = admin }, "srv");
        var settings = Substitute.For<IClientSettingsStore>();
        settings.Current.Returns(new ClientSettings());
        return new UpdateNotifier(Substitute.For<IReleaseCheck>(), settings, dialogs ?? Substitute.For<IDialogService>(), Substitute.For<IUrlLauncher>(),
            NullLogger<UpdateNotifier>.Instance, null, api, session, tracker, client) { CurrentVersion = "4.8.0" };
    }

    [Fact]
    public void An_administrator_of_an_installed_older_server_gets_Install_now()
    {
        var vm = Notifier(InstalledWindowsClient()).Offer(Latest, Server())!;

        Assert.Equal(UpdateOffer.Server, vm.Offer);
        Assert.True(vm.ShowsInstall);
        Assert.Equal("Install now", vm.InstallText);
        Assert.True(vm.ShowsDownloadSecondary); // Open download page becomes secondary
        Assert.False(vm.ShowsDownloadPrimary);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void A_server_not_installed_with_the_installer_keeps_Open_download_page()
    {
        var vm = Notifier(InstalledWindowsClient()).Offer(Latest, Server(installed: false))!;
        Assert.Equal(UpdateOffer.DownloadPage, vm.Offer);
        Assert.True(vm.ShowsDownloadPrimary);
        Assert.False(vm.ShowsInstall);
    }

    [Fact]
    public void Other_users_see_the_dialog_as_before()
    {
        // Get is Administrator only: the notifier asks nothing and offers the download page.
        var vm = Notifier(InstalledWindowsClient(), admin: false).Offer(Latest, server: null)!;
        Assert.Equal(UpdateOffer.DownloadPage, vm.Offer);
    }

    [Fact]
    public void A_running_task_disables_Install_now_and_says_why()
    {
        var vm = Notifier(InstalledWindowsClient()).Offer(Latest, Server(tasks: 3))!;

        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Equal("3 tasks are running", vm.InstallTip);
        vm.ActiveTasks = 0;
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.Null(vm.InstallTip);
    }

    [Fact]
    public void A_newer_server_offers_Update_client_to_its_version()
    {
        var vm = Notifier(InstalledWindowsClient()).Offer(Latest, Server(version: "4.9.0"))!;

        Assert.Equal(UpdateOffer.Client, vm.Offer);
        Assert.Equal("Update client", vm.InstallText);
        Assert.Equal("OADM V4.9.0 is available", vm.Title);
        Assert.True(vm.HasNotes); // the latest release is the server's version
    }

    [Fact]
    public void Update_client_needs_an_installed_client_and_on_Linux_pkexec()
    {
        Assert.Equal(UpdateOffer.DownloadPage, Notifier(Updater(Facts(UpdateOs.Windows, @"D:\portable\"))).Offer(Latest, Server(version: "4.9.0"))!.Offer);
        var linux = Facts(UpdateOs.Linux, "/opt/oadm/client/", "/var/lib/dpkg/info/oadm-client.list");
        Assert.Equal(UpdateOffer.DownloadPage, Notifier(Updater(linux)).Offer(Latest, Server(version: "4.9.0"))!.Offer);
        var withPkexec = Facts(UpdateOs.Linux, "/opt/oadm/client/", "/var/lib/dpkg/info/oadm-client.list", "/usr/bin/pkexec");
        Assert.Equal(UpdateOffer.Client, Notifier(Updater(withPkexec)).Offer(Latest, Server(version: "4.9.0"))!.Offer);
    }

    [Fact]
    public void A_client_on_the_servers_computer_updates_with_the_server_not_by_itself()
    {
        // Windows: the MSI holds the client too; Update client is not offered there, the dialog keeps the download page.
        var vm = Notifier(InstalledWindowsClient()).Offer(Latest, Server(version: "4.9.0", sameComputer: true))!;
        Assert.Equal(UpdateOffer.DownloadPage, vm.Offer);
    }

    private sealed class FakeActions : IUpdateActions
    {
        public TaskCompletionSource Server { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Fails { get; set; }

        public Task StartServerAsync(string version, CancellationToken ct) => Fails is { } ex ? Task.FromException(ex) : Server.Task;

        public Task UpdateClientAsync(string version, Action<UpdateStep, int> progress, CancellationToken ct)
        {
            progress(UpdateStep.Download, 50);
            return Fails is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Install_now_turns_into_the_steps_then_the_server_goes_away()
    {
        var actions = new FakeActions();
        var vm = new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Server, actions);

        var install = vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdatePhase.Working, vm.Phase);
        vm.ApplyServerProgress(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepDownload, Percent = 42 });
        Assert.Equal([UpdateStepState.Current, UpdateStepState.Pending, UpdateStepState.Pending], vm.Steps.Select(s => s.State));
        Assert.Equal("42 %", vm.Steps[0].Detail);
        vm.ApplyServerProgress(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepCheck });
        Assert.Equal([UpdateStepState.Done, UpdateStepState.Current, UpdateStepState.Pending], vm.Steps.Select(s => s.State));
        Assert.Equal(["Download the update", "Check the download", "Install the update"], vm.Steps.Select(s => s.Name));

        actions.Server.SetResult();
        await install;

        Assert.Equal(UpdatePhase.Away, vm.Phase);
        Assert.Equal("Updating the server to V4.9.0", vm.Title);
        Assert.Equal(UpdateStepState.Current, vm.Steps[2].State);
        Assert.Equal("Close", vm.CloseText);
        Assert.False(vm.ShowsInstall);
    }

    private sealed class ThreadPoolActions(Exception? fails = null) : IUpdateActions
    {
        public Task StartServerAsync(string version, CancellationToken ct) => Task.CompletedTask;

        public async Task UpdateClientAsync(string version, Action<UpdateStep, int> progress, CancellationToken ct)
        {
            // Like the real download: progress from a thread-pool thread.
            await Task.Run(() => progress(UpdateStep.Download, 40), ct);
            await Task.Run(() => progress(UpdateStep.Check, 0), ct);
            if (fails is not null)
            {
                throw fails;
            }
        }
    }

    [Fact]
    public async Task Download_progress_from_other_threads_changes_the_dialog_on_its_thread_only()
    {
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var vm = new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Client, new ThreadPoolActions());
            var offThread = 0;
            foreach (var row in vm.Steps)
            {
                row.PropertyChanged += (_, _) =>
                {
                    if (!Dispatcher.UIThread.CheckAccess())
                    {
                        Interlocked.Increment(ref offThread);
                    }
                };
            }

            await vm.InstallCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, Volatile.Read(ref offThread));
            Assert.Equal(UpdateStepState.Current, vm.Steps[2].State); // the helper takes over
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Theory]
    [InlineData(typeof(IOException), UpdateTexts.DownloadFailed)]
    [InlineData(typeof(UnauthorizedAccessException), UpdateTexts.DownloadFailed)]
    [InlineData(typeof(System.ComponentModel.Win32Exception), UpdateTexts.HelperFailed)]
    public async Task An_unexpected_failure_is_shown_never_a_crash(System.Type exception, string reason)
    {
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var vm = new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Client, new ThreadPoolActions((Exception)Activator.CreateInstance(exception)!));

            await vm.InstallCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(UpdatePhase.Result, vm.Phase);
            Assert.Equal([reason], vm.ResultLines);
            Assert.True(vm.ShowsDownloadSecondary);
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public void A_computer_with_its_own_server_updates_through_Install_now_not_Update_client()
    {
        // The MSI would update the local server too, without the server's safeguards.
        var windows = Facts(UpdateOs.Windows, @"C:\Program Files\OADM\Client\", Path.Combine(@"C:\Program Files", "OADM", "Server", "Oadm.Server.exe"));
        Assert.Equal(UpdateOffer.DownloadPage, Notifier(Updater(windows)).Offer(Latest, Server(version: "4.9.0"))!.Offer);
        var mac = Facts(UpdateOs.MacOs, "/Applications/OADM.app/Contents/MacOS/", Installation.MacServerDaemon);
        Assert.False(Updater(mac).CanUpdateClient);
        Assert.True(Updater(Facts(UpdateOs.MacOs, "/Applications/OADM.app/Contents/MacOS/")).CanUpdateClient);
    }

    [Fact]
    public void The_client_helper_copy_lies_in_the_clients_own_data_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-client-update-tests", Guid.NewGuid().ToString("N"), "update");
        var processes = new FakeProcesses();
        var quit = 0;
        try
        {
            new ClientHelperLauncher(processes, folder, () => quit++).StartAndQuit(["--update-helper", "wait"]);

            var (file, _) = Assert.Single(processes.Starts);
            Assert.StartsWith(Path.Combine(folder, "helper-"), file, StringComparison.Ordinal); // never a shared temporary folder
            Assert.True(File.Exists(file));
            Assert.Equal(1, quit);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task The_result_is_asked_for_again_while_the_server_still_waits_for_its_helper()
    {
        var time = new FakeTimeProvider();
        var api = Substitute.For<IOadmApi>();
        var running = Server(version: "4.8.0", sameComputer: true);
        running.Last = Record("running");
        var failed = Server(version: "4.8.0", sameComputer: true);
        failed.Last = Record("failed", UpdateTexts.InstallerBusy);
        api.GetUpdateAsync(Arg.Any<CancellationToken>()).Returns(running, running, failed);
        var shown = new TaskCompletionSource<UpdateViewModel>();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowUpdateAsync(Arg.Any<UpdateViewModel>()).Returns(ci =>
        {
            shown.TrySetResult(ci.Arg<UpdateViewModel>());
            return Task.CompletedTask;
        });
        var tracker = new ServerUpdateTracker(new ImmediateUiDispatcher(), new FakeTimeProvider());
        var session = new Shell.UserSession();
        session.SignIn(new UserInfo { UserName = "admin", Role = UserRole.Admin, AllPermissions = true }, "srv");
        var settings = Substitute.For<IClientSettingsStore>();
        settings.Current.Returns(new ClientSettings { TellAboutNewVersions = false });
        var notifier = new UpdateNotifier(Substitute.For<IReleaseCheck>(), settings, dialogs, Substitute.For<IUrlLauncher>(), NullLogger<UpdateNotifier>.Instance,
            null, api, session, tracker, InstalledWindowsClient(), null, time) { CurrentVersion = "4.8.0" };
        await notifier.ShowIfNewerAsync(CancellationToken.None);

        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall });
        tracker.Connected();
        await Eventually.TrueAsync(
            () =>
            {
                time.Advance(UpdateNotifier.ResultPoll);
                return shown.Task.IsCompleted;
            },
            "the result is shown");

        var vm = await shown.Task;
        Assert.Equal("The update failed. The server runs V4.8.0.", vm.Title);
        await api.Received(3).GetUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GitHub_not_reachable_says_so_and_keeps_the_download_page()
    {
        var actions = new FakeActions { Fails = new RpcException(new Status(StatusCode.FailedPrecondition, UpdateTexts.DownloadFailed)) };
        var vm = new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Server, actions);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdatePhase.Result, vm.Phase);
        Assert.Equal([UpdateTexts.DownloadFailed], vm.ResultLines);
        Assert.Equal(UpdateStepState.Failed, vm.Steps[0].State);
        Assert.True(vm.ShowsDownloadSecondary);
    }

    private static UpdateRecord Record(string result, string reason = "") => new()
    {
        FromVersion = "4.8.0",
        ToVersion = "4.9.0",
        Started = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero)),
        Finished = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 3, 10, 4, 0, TimeSpan.Zero)),
        Result = result,
        Reason = reason,
        InstallerLog = result == "done" ? "" : @"C:\ProgramData\OADM\update\install.log",
        DatabaseCopy = @"C:\ProgramData\OADM\update\oadm-4.8.0.db",
    };

    [Fact]
    public void The_results_say_what_happened_and_what_to_do()
    {
        var done = UpdateOutcome.OfServer(Record("done"), "4.9.0", olderSiteNodes: 2)!;
        Assert.Equal(("Server updated to V4.9.0", true), (done.Title, done.IsDone));
        Assert.Equal(["Update the site nodes on their computers"], done.Lines);
        Assert.Empty(UpdateOutcome.OfServer(Record("done"), "4.9.0", 0)!.Lines);
        Assert.Equal(["Restart the computer to finish the update"], UpdateOutcome.OfServer(Record("done", UpdateTexts.RestartComputer), "4.9.0", 0)!.Lines);

        var failed = UpdateOutcome.OfServer(Record("failed", "The installer failed (code 1603)."), "4.8.0", 0)!;
        Assert.Equal("The update failed. The server runs V4.8.0.", failed.Title);
        Assert.Equal(
            ["The installer failed (code 1603).", @"Installer log on the server: C:\ProgramData\OADM\update\install.log", @"Copy of the database before the update: C:\ProgramData\OADM\update\oadm-4.8.0.db"],
            failed.Lines);
        Assert.Null(UpdateOutcome.OfServer(Record("running"), "4.8.0", 0));

        Assert.Equal("The server did not come back", UpdateOutcome.NotBack().Title);
        Assert.Equal(("OADM updated to V4.9.0", true), (UpdateOutcome.OfClient("4.9.0", "4.9.0", null).Title, UpdateOutcome.OfClient("4.9.0", "4.9.0", null).IsDone));
        var client = UpdateOutcome.OfClient("4.8.0", "4.9.0", UpdateTexts.Cancelled);
        Assert.Equal(("The update failed", false), (client.Title, client.IsDone));
        Assert.Equal([UpdateTexts.Cancelled], client.Lines);
    }

    [Fact]
    public async Task The_Logs_page_shows_the_last_update()
    {
        var api = Substitute.For<IOadmApi>();
        var info = Server();
        info.Last = Record("failed", UpdateTexts.InstallerBusy);
        api.GetUpdateAsync(Arg.Any<CancellationToken>()).Returns(info);
        api.ListAuditAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new AuditList());
        var audit = new Logging.AuditLogViewModel(api);

        await audit.RefreshAsync();

        Assert.Equal("The update failed. The server runs V4.8.0.", audit.LastUpdate);
        Assert.True(audit.LastUpdateFailed);
        Assert.StartsWith("Last server update: V4.8.0 to V4.9.0, ", audit.LastUpdateDetail, StringComparison.Ordinal);
        Assert.Contains(UpdateTexts.InstallerBusy, audit.LastUpdateDetail, StringComparison.Ordinal);

        api.GetUpdateAsync(Arg.Any<CancellationToken>()).Returns<UpdateInfo>(_ => throw new RpcException(new Status(StatusCode.Unimplemented, "")));
        await audit.RefreshAsync();
        Assert.Null(audit.LastUpdate); // an older server: nothing
    }

    [Fact]
    public void The_tracker_says_updating_then_not_back_after_ten_minutes_and_asks_for_the_result_after_the_reconnect()
    {
        var time = new FakeTimeProvider();
        var tracker = new ServerUpdateTracker(new ImmediateUiDispatcher(), time);
        string? here = null;
        string? back = null;
        tracker.InstallerStartsHere += (_, v) => here = v;
        tracker.ServerBack += (_, v) => back = v;

        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepDownload });
        Assert.Null(tracker.AwayText); // the server still runs: no notice yet
        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall, SameComputer = true });
        Assert.Equal("4.9.0", here);
        Assert.Equal("Updating the server to V4.9.0", tracker.AwayText);

        tracker.Disconnected();
        time.Advance(ServerUpdateTracker.NotBackAfter - TimeSpan.FromSeconds(1));
        Assert.False(tracker.IsNotBack);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.IsNotBack);
        Assert.StartsWith("The server did not come back. Check that the service OADM Server runs", tracker.AwayText, StringComparison.Ordinal);

        tracker.Connected();
        Assert.Equal("4.9.0", back);
        Assert.Null(tracker.AwayText);
    }

    [Fact]
    public async Task The_banner_says_updating_instead_of_unreachable()
    {
        using var api = new GrpcOadmApi("http://127.0.0.1:1");
        var devices = new Devices.DeviceStore();
        var ui = new ImmediateUiDispatcher();
        var tracker = new ServerUpdateTracker(ui, new FakeTimeProvider());
        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall });
        using var connection = new Shell.ServerConnection(api, devices, new Tasks.TaskStore(devices), ui, NullLogger<Shell.ServerConnection>.Instance, updates: tracker);

        connection.Start();

        await Eventually.TrueAsync(() => connection.State == Shell.ConnectionState.Disconnected, "the server is away");
        Assert.Equal("Updating the server to V4.9.0", connection.StatusText);
        connection.Stop();
    }

    [Fact]
    public void An_update_that_ended_without_a_restart_clears_the_notice()
    {
        var tracker = new ServerUpdateTracker(new ImmediateUiDispatcher(), new FakeTimeProvider());
        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall });
        UpdateProgress? ended = null;
        tracker.EndedWithoutRestart += (_, p) => ended = p;

        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Finished = true, Failed = UpdateTexts.InstallerBusy });

        Assert.Null(tracker.AwayText);
        Assert.Equal(UpdateTexts.InstallerBusy, ended!.Failed);
    }

    [Fact]
    public async Task After_the_reconnect_the_open_dialog_shows_the_servers_result()
    {
        var api = Substitute.For<IOadmApi>();
        var info = Server(version: "4.9.0", sameComputer: true); // updated with the server: no Update client offer at start
        info.Last = Record("done");
        info.OlderSiteNodes = 1;
        api.GetUpdateAsync(Arg.Any<CancellationToken>()).Returns(info);
        var tracker = new ServerUpdateTracker(new ImmediateUiDispatcher(), new FakeTimeProvider());
        var shown = new TaskCompletionSource<UpdateViewModel>();
        var closed = new TaskCompletionSource();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowUpdateAsync(Arg.Any<UpdateViewModel>()).Returns(ci =>
        {
            shown.SetResult(ci.Arg<UpdateViewModel>());
            return closed.Task;
        });
        var notifier = Notifier(InstalledWindowsClient(), api, tracker: tracker, dialogs: dialogs);
        var check = notifier.ShowIfNewerAsync(CancellationToken.None); // hooks the tracker; GitHub gives nothing here
        await check;

        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall });
        tracker.Connected();

        var vm = await shown.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Server updated to V4.9.0", vm.Title);
        Assert.Equal(["Update the site nodes on their computers"], vm.ResultLines);
        closed.SetResult();
    }

    [Fact]
    public void The_client_on_the_servers_computer_closes_for_the_installer()
    {
        var api = Substitute.For<IOadmApi>();
        api.ServerAddress.Returns("localhost:5080");
        var launcher = new FakeLauncher();
        var tracker = new ServerUpdateTracker(new ImmediateUiDispatcher(), new FakeTimeProvider());
        _ = Notifier(InstalledWindowsClient(launcher), api, tracker: tracker).ShowIfNewerAsync(CancellationToken.None);

        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall, SameComputer = true });

        var args = ClientHelperArguments.Parse(Assert.Single(launcher.Starts))!;
        Assert.Equal((ClientHelperArguments.Wait, "4.9.0", "4.8.0", "localhost:5080"), (args.Mode, args.To, args.From, args.Server));
    }

    [Fact]
    public void A_Linux_client_on_the_servers_computer_stays_open()
    {
        var api = Substitute.For<IOadmApi>();
        var launcher = new FakeLauncher();
        var tracker = new ServerUpdateTracker(new ImmediateUiDispatcher(), new FakeTimeProvider());
        var linux = Updater(Facts(UpdateOs.Linux, "/opt/oadm/client/", "/var/lib/dpkg/info/oadm-client.list", "/usr/bin/pkexec"), launcher);
        _ = Notifier(linux, api, tracker: tracker).ShowIfNewerAsync(CancellationToken.None);

        tracker.Apply(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall, SameComputer = true });

        Assert.Empty(launcher.Starts); // its own package: it updates like a client on another computer
    }

    [Fact]
    public async Task Update_client_downloads_checks_and_hands_over_to_the_helper()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-client-update-tests", Guid.NewGuid().ToString("N"));
        var launcher = new FakeLauncher();
        var steps = new List<UpdateStep>();
        try
        {
            await InstalledWindowsClient(launcher, folder: folder).UpdateAsync("4.9.0", (s, _) => steps.Add(s), CancellationToken.None);

            var args = ClientHelperArguments.Parse(Assert.Single(launcher.Starts))!;
            Assert.Equal(ClientHelperArguments.Install, args.Mode);
            Assert.Equal(Path.Combine(folder, "OADM-4.9.0-win-x64.msi"), args.Installer);
            Assert.Equal("the msi", await File.ReadAllTextAsync(args.Installer!));
            Assert.Equal([UpdateStep.Download, UpdateStep.Check, UpdateStep.Install], steps.Distinct());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Update_client_with_a_wrong_checksum_starts_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-client-update-tests", Guid.NewGuid().ToString("N"));
        var launcher = new FakeLauncher();
        var feed = FakeFeed.With("OADM-4.9.0-win-x64.msi", "the msi", sumsOf: "another");
        try
        {
            var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
                InstalledWindowsClient(launcher, feed, folder).UpdateAsync("4.9.0", (_, _) => { }, CancellationToken.None));
            Assert.Equal(UpdateTexts.WrongChecksum, refused.Message);
            Assert.Empty(launcher.Starts);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("4.8.0", UpdateTexts.SameVersion)]
    [InlineData("4.7.0", UpdateTexts.Downgrade)]
    [InlineData("4.9.0-rc.1", UpdateTexts.PreRelease)]
    public async Task Update_client_never_installs_the_same_an_older_or_a_pre_release(string version, string reason)
    {
        var launcher = new FakeLauncher();

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() => InstalledWindowsClient(launcher).UpdateAsync(version, (_, _) => { }, CancellationToken.None));

        Assert.Equal(reason, refused.Message);
        Assert.Empty(launcher.Starts);
    }

    [Fact]
    public async Task Update_client_without_the_release_on_GitHub_starts_nothing()
    {
        var launcher = new FakeLauncher();
        var feed = FakeFeed.With("OADM-4.9.0-win-x64.msi", "the msi");
        feed.Unreachable = true;

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() => InstalledWindowsClient(launcher, feed).UpdateAsync("4.9.0", (_, _) => { }, CancellationToken.None));

        Assert.Equal(UpdateTexts.DownloadFailed, refused.Message);
        Assert.Empty(launcher.Starts);
    }

    private sealed class FakeInstaller(int code, string output = "") : IInstallerRunner
    {
        public ConcurrentQueue<InstallerCommand> Runs { get; } = new();

        public Task<(int ExitCode, string Output)> RunAsync(InstallerCommand command, CancellationToken ct)
        {
            Runs.Enqueue(command);
            return Task.FromResult((code, output));
        }
    }

    private sealed class FakeProcesses : IProcessLauncher
    {
        public ConcurrentQueue<(string File, IReadOnlyList<string> Args)> Starts { get; } = new();

        public void Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null) => Starts.Enqueue((fileName, arguments));
    }

    private sealed class FakeProbe(params string?[] answers) : IServerVersionProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<string?> VersionAsync(Uri server, CancellationToken ct) => Task.FromResult(answers[Math.Min(Interlocked.Increment(ref _calls) - 1, answers.Length - 1)]);
    }

    private static ClientHelperArguments InstallArgs(string installer) =>
        new() { Mode = ClientHelperArguments.Install, Start = @"C:\Program Files\OADM\Client\Oadm.Client.exe", From = "4.8.0", To = "4.9.0", Installer = installer };

    [Theory]
    [InlineData(UpdateOs.Windows, 0, "", null)]
    [InlineData(UpdateOs.Windows, 1602, "", "The update was cancelled.")]
    [InlineData(UpdateOs.Windows, 1618, "", "Another installation is running. Try again later.")]
    [InlineData(UpdateOs.Linux, 126, "", "The update was cancelled.")]
    [InlineData(UpdateOs.MacOs, 1, "User canceled. (-128)", "The update was cancelled.")]
    public async Task The_helper_runs_the_installer_with_the_prompt_and_starts_the_installed_client(UpdateOs os, int code, string output, string? reason)
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-client-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var installer = new FakeInstaller(code, output);
            var processes = new FakeProcesses();
            var helper = new ClientUpdateHelper(os, Facts(os, "/"), installer, new FakeProbe(), processes, new FakeTimeProvider());

            await helper.RunAsync(InstallArgs(Path.Combine(folder, "OADM-4.9.0.msi")), CancellationToken.None);

            var run = Assert.Single(installer.Runs);
            Assert.Equal(InstallerCommands.For(os, InstallMode.Visible, Path.Combine(folder, "OADM-4.9.0.msi"), run.LogPath, os == UpdateOs.MacOs ? Path.Combine(folder, "choices.xml") : null).Arguments, run.Arguments);
            var (file, args) = Assert.Single(processes.Starts);
            Assert.Equal(@"C:\Program Files\OADM\Client\Oadm.Client.exe", file);
            var updated = UpdatedArguments.Parse(args)!;
            Assert.Equal((UpdatedArguments.Client, "4.8.0", "4.9.0", reason), (updated.Kind, updated.From, updated.To, updated.Reason));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_helper_of_a_client_only_Mac_leaves_the_server_out()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-client-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var installer = new FakeInstaller(0);
            await new ClientUpdateHelper(UpdateOs.MacOs, Facts(UpdateOs.MacOs, "/"), installer, new FakeProbe(), new FakeProcesses(), new FakeTimeProvider())
                .RunAsync(InstallArgs(Path.Combine(folder, "OADM-4.9.0-osx-x64.pkg")), CancellationToken.None);

            var run = Assert.Single(installer.Runs);
            Assert.Equal(InstallerCommands.Osascript, run.FileName);
            Assert.Contains("-applyChoiceChangesXML", run.Arguments[1], StringComparison.Ordinal);
            Assert.Contains(InstallerCommands.MacServerChoice, await File.ReadAllTextAsync(Path.Combine(folder, "choices.xml")), StringComparison.Ordinal);

            var withServer = new FakeInstaller(0);
            await new ClientUpdateHelper(UpdateOs.MacOs, Facts(UpdateOs.MacOs, "/", Installation.MacServerDaemon), withServer, new FakeProbe(), new FakeProcesses(), new FakeTimeProvider())
                .RunAsync(InstallArgs(Path.Combine(folder, "OADM-4.9.0-osx-x64.pkg")), CancellationToken.None);
            Assert.DoesNotContain("-applyChoiceChangesXML", Assert.Single(withServer.Runs).Arguments[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_helper_on_the_servers_computer_waits_for_the_new_server_then_starts_the_client()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProbe(null, "4.8.0", "4.9.0");
        var processes = new FakeProcesses();
        var helper = new ClientUpdateHelper(UpdateOs.Windows, Facts(UpdateOs.Windows, "/"), new FakeInstaller(0), probe, processes, time);
        var args = new ClientHelperArguments { Mode = ClientHelperArguments.Wait, Start = "Oadm.Client.exe", From = "4.8.0", To = "4.9.0", Server = "localhost:5080" };

        var run = helper.RunAsync(args, CancellationToken.None);
        await Eventually.TrueAsync(
            () =>
            {
                if (!run.IsCompleted)
                {
                    time.Advance(ClientUpdateHelper.Poll);
                }

                return run.IsCompleted;
            },
            "the helper ends");
        await run;

        Assert.Equal(3, probe.Calls);
        var updated = UpdatedArguments.Parse(Assert.Single(processes.Starts).Args)!;
        Assert.Equal(UpdatedArguments.Server, updated.Kind);
    }

    [Fact]
    public void The_helper_and_the_restarted_client_read_their_command_lines()
    {
        var helper = new ClientHelperArguments { Mode = ClientHelperArguments.Install, Start = "x", From = "4.8.0", To = "4.9.0", Installer = "i.msi" };
        Assert.Equal(helper, AppOptions.Parse(helper.ToList(), _ => null).UpdateHelper);
        var updated = new UpdatedArguments(UpdatedArguments.Client, "4.8.0", "4.9.0", "The update was cancelled.");
        Assert.Equal(updated, AppOptions.Parse(updated.ToList(), _ => null).Updated);
        Assert.Null(AppOptions.Parse(["--update-helper", "install"], _ => null).UpdateHelper); // incomplete: a normal start
    }

    // ---------- headless: the dialog's states ----------

    [Fact]
    public async Task The_dialog_states_render_headless()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var owner = new Window { Width = 800, Height = 900 };
            owner.Show();

            async Task<Window> ShowAsync(UpdateViewModel vm, string name, params string[] expected)
            {
                var window = new UpdateWindow { DataContext = vm };
                Oadm.Client.App.ApplyCrispText(window);
                window.Show(owner);
                Dispatcher.UIThread.RunJobs();
                await Task.Yield();
                Dispatcher.UIThread.RunJobs();
                var texts = Texts(window);
                foreach (var text in expected)
                {
                    Assert.Contains(text, texts);
                }

                Capture(window, outDir, name);
                return window;
            }

            // The offer: Install now (filled accent) and Open download page (secondary).
            var offer = new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Server, new FakeActions());
            var w = await ShowAsync(offer, "update-install-now.png", "OADM V4.9.0 is available", "You have V4.8.0", "Install now", "Open download page");
            Assert.Contains("primary", Buttons(w).Single(b => Texts(b).Contains("Install now")).Classes);
            Assert.Contains("secondary", Buttons(w).Single(b => Texts(b).Contains("Open download page")).Classes);
            w.Close();

            var busy = new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Server, new FakeActions()) { ActiveTasks = 3 };
            w = await ShowAsync(busy, "update-install-now-tasks-running.png", "Install now");
            Assert.False(Buttons(w).Single(b => Texts(b).Contains("Install now")).IsEffectivelyEnabled);
            Assert.Equal("3 tasks are running", ToolTip.GetTip(Buttons(w).Single(b => Texts(b).Contains("Install now"))));
            w.Close();

            // The steps while the server downloads, then the server away for its installer.
            var actions = new FakeActions();
            var steps = new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Server, actions);
            _ = steps.InstallCommand.ExecuteAsync(null);
            steps.ApplyServerProgress(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepDownload, Percent = 42 });
            w = await ShowAsync(steps, "update-steps.png", "Download the update: 42 %", "Check the download", "Install the update");
            Assert.DoesNotContain("Install now", Texts(w));
            steps.ApplyServerProgress(new UpdateProgress { ToVersion = "4.9.0", Step = UpdateStepKind.UpdateStepInstall });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Updating the server to V4.9.0", Texts(w));
            Capture(w, outDir, "update-updating.png");

            // The results after the reconnect, and not back after 10 minutes.
            steps.ShowResult(UpdateOutcome.OfServer(Record("done"), "4.9.0", 2)!);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Server updated to V4.9.0", Texts(w));
            Assert.Contains("Update the site nodes on their computers", Texts(w));
            Capture(w, outDir, "update-done.png");
            w.Close();

            w = await ShowAsync(UpdateViewModel.ForResult(UpdateOutcome.OfServer(Record("failed", "The installer failed (code 1603)."), "4.8.0", 0)!),
                "update-failed.png", "The update failed. The server runs V4.8.0.", "The installer failed (code 1603).");
            w.Close();

            w = await ShowAsync(UpdateViewModel.ForResult(UpdateOutcome.NotBack()), "update-not-back.png", "The server did not come back", UpdateTexts.NotBackHelp);
            w.Close();

            // Update client on another computer, and its result after the helper.
            w = await ShowAsync(new UpdateViewModel(Latest, "4.8.0", UpdateOffer.Client, new FakeActions()), "update-client.png", "Update client");
            w.Close();
            w = await ShowAsync(UpdateViewModel.ForResult(UpdateOutcome.OfClient("4.9.0", "4.9.0", null)), "update-client-done.png", "OADM updated to V4.9.0");
            w.Close();
            w = await ShowAsync(UpdateViewModel.ForResult(UpdateOutcome.OfClient("4.8.0", "4.9.0", UpdateTexts.Cancelled), offersDownloadPage: true),
                "update-client-failed.png", "The update failed", "The update was cancelled.", "Open download page");
            w.Close();

            // The helper's own window.
            var helperWindow = new UpdateHelperWindow { DataContext = new UpdateHelperViewModel(InstallArgs("x.msi")) };
            helperWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Installing OADM V4.9.0", Texts(helperWindow));
            Capture(helperWindow, outDir, "update-helper.png");
            helperWindow.Close();
            Assert.True(helperWindow.IsVisible); // the helper cannot be closed while it runs
            helperWindow.KeepsOpen = false;
            helperWindow.Close();

            owner.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static List<string> Texts(Avalonia.Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();

    private static IEnumerable<Button> Buttons(Window window) => window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible);

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }

    // ---------- fakes ----------

    private sealed class FakeLauncher : IClientHelperLauncher
    {
        public ConcurrentQueue<IReadOnlyList<string>> Starts { get; } = new();

        public void StartAndQuit(IReadOnlyList<string> arguments) => Starts.Enqueue(arguments);
    }

    private sealed class FakeDisk : IDiskSpace
    {
        public long? AvailableBytes(string folder) => long.MaxValue;
    }

    internal sealed class FakeFeed : IReleaseFeed
    {
        private readonly Dictionary<Uri, byte[]> _files = [];

        public required ReleaseOffer Release { get; init; }

        public bool Unreachable { get; set; }

        public static FakeFeed With(string name, string content, string? sumsOf = null)
        {
            var prefix = GitHubReleaseFeed.DownloadPrefix + "v4.9.0/";
            var bytes = Encoding.UTF8.GetBytes(content);
            var sums = Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sumsOf ?? content))) + "  " + name + "\n");
            var feed = new FakeFeed
            {
                Release = new ReleaseOffer(new Version(4, 9, 0), "v4.9.0", "", [
                    new ReleaseAsset(name, bytes.Length, new Uri(prefix + name)),
                    new ReleaseAsset(UpdateDownloader.ChecksumsName, sums.Length, new Uri(prefix + UpdateDownloader.ChecksumsName)),
                ]),
            };
            feed._files[new Uri(prefix + name)] = bytes;
            feed._files[new Uri(prefix + UpdateDownloader.ChecksumsName)] = sums;
            return feed;
        }

        public Task<ReleaseOffer?> LatestAsync(CancellationToken ct) => Task.FromResult(Unreachable ? null : Release);

        public Task<ReleaseOffer?> ByVersionAsync(Version version, CancellationToken ct) => Task.FromResult(Unreachable || version != Release.Version ? null : Release);

        public async Task DownloadAsync(Uri url, Stream target, long maxBytes, IProgress<long>? progress, CancellationToken ct)
        {
            await target.WriteAsync(_files[url], ct);
            progress?.Report(_files[url].Length);
        }
    }
}
