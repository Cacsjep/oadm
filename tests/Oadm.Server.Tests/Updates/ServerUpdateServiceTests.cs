using System.Collections.Concurrent;
using System.Text;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Updates;
using Oadm.Core.Auth;
using Oadm.Core.Events;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Server.Tests.Support;
using Oadm.Server.Updates;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Updates;

/// <summary>
/// UpdateService through the in-process server (docs/spec/self-update.md): refusals, the steps, the database copy, the
/// record, the helper start, no new tasks and calls once it started, and the record across a restart. GitHub, the disk and
/// the helper are fakes; nothing is installed.
/// </summary>
public sealed class ServerUpdateServiceTests
{
    private const string Installer = "OADM-4.9.0-win-x64.msi";

    private sealed class RecordingStarter : IServerHelperStarter
    {
        public ConcurrentQueue<(string Helper, IReadOnlyList<string> Args)> Starts { get; } = new();

        public bool Fails { get; set; }

        /// <summary>Runs before a failing start (e.g. takes the update folder away).</summary>
        public Action? BeforeFailing { get; set; }

        public Task StartAsync(string helper, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            if (Fails)
            {
                BeforeFailing?.Invoke();
                throw new InvalidOperationException("no");
            }

            Starts.Enqueue((helper, arguments));
            return Task.CompletedTask;
        }
    }

    private sealed class Fakes
    {
        public FakeReleaseFeed Feed { get; set; } = FakeReleaseFeed.With(Installer, Encoding.UTF8.GetBytes("the msi"));

        public FakeDiskSpace Disk { get; } = new(long.MaxValue);

        public RecordingStarter Starter { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));

        public string Version { get; set; } = "4.8.0";

        public bool Installed { get; set; } = true;
    }

    private static Task<TestServerHost> StartAsync(Fakes fakes, string? dataDirectory = null) =>
        TestServerHost.StartAsync(dataDirectory: dataDirectory, configureServices: services =>
        {
            services.RemoveAll<IReleaseFeed>();
            services.AddSingleton<IReleaseFeed>(fakes.Feed);
            services.RemoveAll<IDiskSpace>();
            services.AddSingleton<IDiskSpace>(fakes.Disk);
            services.RemoveAll<IServerHelperStarter>();
            services.AddSingleton<IServerHelperStarter>(fakes.Starter);
            services.RemoveAll<ServerUpdateEnvironment>();
            services.AddSingleton(sp => new ServerUpdateEnvironment
            {
                ServerVersion = fakes.Version,
                Platform = new UpdatePlatform(UpdateOs.Windows, System.Runtime.InteropServices.Architecture.X64),
                Installed = fakes.Installed,
                ConnectionString = sp.GetRequiredService<OadmPaths>().ConnectionString,
                DataDirectory = sp.GetRequiredService<OadmPaths>().DataDirectory,
                MakeHelper = folder => Path.Combine(folder, "helper-test", "Oadm.Server.exe"),
                StatusUrl = () => new Uri("https://127.0.0.1:5080"),
            });
            services.RemoveAll<ServerUpdateCoordinator>();
            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ServerUpdateCoordinator>(sp, (TimeProvider)fakes.Time));
        });

    private static Proto.UpdateService.UpdateServiceClient Client(TestServerHost host) => new(host.Invoker);

    private static async Task<string> RefusalAsync(TestServerHost host, string version)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client(host).StartAsync(new Proto.StartUpdateRequest { Version = version }));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        return ex.Status.Detail;
    }

    private static async Task<IReadOnlyList<AuditRecord>> AuditAsync(TestServerHost host) =>
        (await host.Get<AuditLog>().ListAsync(100, 0, CancellationToken.None)).Entries;

    [Theory]
    [InlineData("4.9.0-rc.1", UpdateTexts.PreRelease)]
    [InlineData("4.7.0", UpdateTexts.Downgrade)]
    [InlineData("4.8.0", UpdateTexts.SameVersion)]
    [InlineData("4.10.0", UpdateTexts.NotLatest)]
    public async Task OnlyTheLatestStableNewerReleaseIsInstalled(string version, string reason)
    {
        var fakes = new Fakes();
        await using var host = await StartAsync(fakes);

        Assert.Equal(reason, await RefusalAsync(host, version));

        Assert.Empty(fakes.Starter.Starts);
        Assert.Null(host.Get<TaskEngine>().HeldReason);
        Assert.False(host.Get<ActionRunner>().IsHeld);
        Assert.Null(host.Get<UpdateRecordFile>().Read());
    }

    [Fact]
    public async Task AServerNotInstalledWithTheInstallerKeepsTheDownloadPage()
    {
        var fakes = new Fakes { Installed = false };
        await using var host = await StartAsync(fakes);

        Assert.False((await Client(host).GetAsync(new Proto.Empty())).Installed);
        Assert.Equal(UpdateTexts.NotInstalled, await RefusalAsync(host, "4.9.0"));
    }

    [Fact]
    public async Task ADevelopmentBuildIsNeverUpdated()
    {
        var fakes = new Fakes { Version = "0.1.0-dev" };
        await using var host = await StartAsync(fakes);

        Assert.Equal(UpdateTexts.DevelopmentBuild, await RefusalAsync(host, "4.9.0"));
    }

    [Fact]
    public async Task TooLittleDiskSpaceIsRefusedBeforeTheDownload()
    {
        var fakes = new Fakes();
        fakes.Disk.Free = 10;
        await using var host = await StartAsync(fakes);

        Assert.Equal(UpdateTexts.NotEnoughSpace, await RefusalAsync(host, "4.9.0"));
        Assert.Equal(0, fakes.Feed.Downloads);
        Assert.Null(host.Get<TaskEngine>().HeldReason);
    }

    [Fact]
    public async Task GitHubNotReachableChangesNothing()
    {
        var fakes = new Fakes();
        fakes.Feed.Unreachable = true;
        await using var host = await StartAsync(fakes);

        Assert.Equal(UpdateTexts.DownloadFailed, await RefusalAsync(host, "4.9.0"));
        Assert.Empty(fakes.Starter.Starts);
    }

    [Fact]
    public async Task AWrongChecksumIsRefusedAndTheServerGoesOn()
    {
        var fakes = new Fakes { Feed = FakeReleaseFeed.With(Installer, Encoding.UTF8.GetBytes("the msi"), sumsOf: Encoding.UTF8.GetBytes("another")) };
        await using var host = await StartAsync(fakes);

        Assert.Equal(UpdateTexts.WrongChecksum, await RefusalAsync(host, "4.9.0"));

        Assert.False(File.Exists(Path.Combine(host.Get<UpdateRecordFile>().Folder, Installer)));
        Assert.Null(host.Get<TaskEngine>().HeldReason);
        Assert.NotNull(await host.Devices.ListAsync(new Proto.Empty())); // calls go on
    }

    [Fact]
    public async Task TheDatabaseCountsForTheFreeSpace()
    {
        var fakes = new Fakes();
        fakes.Disk.Free = (UpdateDownloader.SpaceFactor * "the msi".Length) + 1; // enough for the installer alone
        await using var host = await StartAsync(fakes);

        Assert.Equal(UpdateTexts.NotEnoughSpace, await RefusalAsync(host, "4.9.0"));
        Assert.Equal(0, fakes.Feed.Downloads);
    }

    [Fact]
    public async Task AnUnexpectedFailureBeforeTheRecordReleasesEverything()
    {
        var fakes = new Fakes();
        fakes.Feed.Throws = new InvalidOperationException("unexpected");
        await using var host = await StartAsync(fakes);

        Assert.Equal(UpdateTexts.DownloadFailed, await RefusalAsync(host, "4.9.0"));
        Assert.Null(host.Get<TaskEngine>().HeldReason);
        Assert.False(host.Get<ActionRunner>().IsHeld);
        Assert.False(host.Get<ServerUpdateCoordinator>().IsHolding);
    }

    [Fact]
    public async Task ARecordThatCannotBeReadForAMomentIsFollowedOn()
    {
        var fakes = new Fakes();
        await using var host = await StartAsync(fakes);
        await Client(host).StartAsync(new Proto.StartUpdateRequest { Version = "4.9.0" });
        var records = host.Get<UpdateRecordFile>();
        var running = records.Read()!;

        File.Delete(records.FilePath); // the helper replaces it right now
        for (var i = 0; i < 3; i++)
        {
            fakes.Time.Advance(ServerUpdateCoordinator.FollowInterval);
        }

        Assert.Equal("4.9.0", host.Get<UpdateGate>().ClosedFor); // still closed: the follow goes on
        records.Write(running with { Result = UpdateRecord.Failed, Reason = UpdateTexts.InstallerBusy });
        await Eventually.TrueAsync(
            () =>
            {
                fakes.Time.Advance(ServerUpdateCoordinator.FollowInterval);
                return host.Get<UpdateGate>().ClosedFor is null;
            },
            "the server reads the helper's result");
        Assert.Null(host.Get<TaskEngine>().HeldReason);
    }

    [Fact]
    public async Task AnOldServerThatStartsWhileTheHelperWorksHoldsTasksUntilTheResult()
    {
        var data = Seed(Running() with { StartedUtc = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero) });
        var fakes = new Fakes(); // 4.8.0: the MSI rolled back and started the old service again
        await using var host = await StartAsync(fakes, data);

        Assert.NotNull(host.Get<TaskEngine>().HeldReason);
        Assert.True(host.Get<ActionRunner>().IsHeld);
        Assert.Equal("4.9.0", host.Get<UpdateGate>().UpdatingTo); // the client's helper waits on
        Assert.NotNull(await host.Devices.ListAsync(new Proto.Empty())); // calls work: only starts are held

        var records = host.Get<UpdateRecordFile>();
        records.Write(records.Read()! with { Result = UpdateRecord.Failed, Reason = "The installer failed (code 1603)." });
        await Eventually.TrueAsync(
            () =>
            {
                fakes.Time.Advance(ServerUpdateCoordinator.FollowInterval);
                return host.Get<TaskEngine>().HeldReason is null;
            },
            "the server reads the helper's result");
        Assert.False(host.Get<ActionRunner>().IsHeld);
        await Eventually.TrueAsync(() => records.Read()!.Audited, "the result is audited");
    }

    [Fact]
    public async Task ANewVersionTurnsAGiveUpIntoDone()
    {
        var data = Seed(Running() with { Result = UpdateRecord.Failed, Reason = UpdateTexts.InstallerDidNotFinish, Audited = true });
        await using var host = await StartAsync(new Fakes { Version = "4.9.0" }, data);

        var record = host.Get<UpdateRecordFile>().Read()!;
        Assert.Equal((UpdateRecord.Done, (string?)null, true), (record.Result, record.Reason, record.Audited));
        Assert.Single(await AuditAsync(host), e => e.Action == AuditActions.ServerUpdated);
    }

    [Fact]
    public async Task ARunningTaskRefusesTheUpdate()
    {
        var fakes = new Fakes();
        await using var host = await StartAsync(fakes);
        var plugin = new HoldingPlugin();
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin(HoldingPlugin.PluginId, "1.0.0", null)));
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.0.9.1", 901);
        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = HoldingPlugin.PluginId, DeviceIds = { device.Id.ToString() } });
        await plugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, (await Client(host).GetAsync(new Proto.Empty())).ActiveTasks);
        Assert.Equal("1 task is running.", await RefusalAsync(host, "4.9.0"));

        plugin.Release.SetResult();
        await TestHelpers.WaitForTaskAsync(host, run.TaskIds[0]);
    }

    [Fact]
    public async Task InstallNowCopiesTheDatabaseWritesTheRecordAndStartsTheHelper()
    {
        var fakes = new Fakes();
        await using var host = await StartAsync(fakes);
        var steps = new List<Proto.UpdateProgress>();
        using var watch = Client(host).Watch(new Proto.Empty());
        var reading = Task.Run(async () =>
        {
            await foreach (var p in watch.ResponseStream.ReadAllAsync())
            {
                lock (steps)
                {
                    steps.Add(p);
                }
            }
        });
        await Eventually.TrueAsync(() => WatcherCount(host) == 1, "the client watches");

        var info = await Client(host).StartAsync(new Proto.StartUpdateRequest { Version = "4.9.0" });

        Assert.Equal("4.9.0", info.Running.ToVersion);
        Assert.Equal(Proto.UpdateStepKind.UpdateStepInstall, info.Running.Step);
        var record = host.Get<UpdateRecordFile>().Read()!;
        Assert.Equal(("4.8.0", "4.9.0", UpdateRecord.Running, UpdateRecord.StepInstall), (record.From, record.To, record.Result, record.Step));
        Assert.Equal("admin@testpc", record.RequestedBy);
        Assert.Equal("the msi", await File.ReadAllTextAsync(record.Installer!));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("the msi"))), record.InstallerSha256);
        Assert.Equal(Path.Combine(host.Get<UpdateRecordFile>().Folder, "oadm-4.8.0.db"), record.DatabaseCopy);
        Assert.True(new FileInfo(record.DatabaseCopy!).Length > 0);

        var (helper, args) = Assert.Single(fakes.Starter.Starts);
        Assert.EndsWith("Oadm.Server.exe", helper, StringComparison.Ordinal);
        Assert.Equal(new HelperArguments(host.DataDirectory, "4.9.0", new Uri("https://127.0.0.1:5080")), HelperArguments.Parse(args));

        var started = Assert.Single(await AuditAsync(host), e => e.Action == AuditActions.ServerUpdateStarted);
        Assert.Equal(("admin", "OADM 4.9.0", "From 4.8.0"), (started.UserName, started.Target, started.Detail));

        // No task, no event rule action and no other call from now on; Status stays open for the helper.
        Assert.NotNull(host.Get<TaskEngine>().HeldReason);
        Assert.True(host.Get<ActionRunner>().IsHeld);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await host.Devices.ListAsync(new Proto.Empty()));
        Assert.Equal(StatusCode.Unavailable, refused.StatusCode);
        Assert.Equal("Updating the server to V4.9.0", refused.Status.Detail);
        Assert.Equal("4.9.0", (await new Proto.AuthService.AuthServiceClient(host.Channel).StatusAsync(new Proto.Empty())).UpdatingTo); // the update helpers read it

        await Eventually.TrueAsync(() => { lock (steps) { return steps.Any(s => s.Step == Proto.UpdateStepKind.UpdateStepInstall); } }, "the clients hear of the install");
        lock (steps)
        {
            Assert.Equal(
                [Proto.UpdateStepKind.UpdateStepDownload, Proto.UpdateStepKind.UpdateStepCheck, Proto.UpdateStepKind.UpdateStepInstall],
                steps.Select(s => s.Step).Distinct());
        }

        watch.Dispose();
        await Eventually.TrueAsync(() => reading.IsCompleted, "the watch ends");
    }

    [Fact]
    public async Task OnlyTheLastDatabaseCopyIsKept()
    {
        using var folder = new TempFolder();
        var db = Path.Combine(folder.Path, "source.db");
        await using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('kept');";
            await cmd.ExecuteNonQueryAsync();
        }

        var copies = Path.Combine(folder.Path, "update");
        await DatabaseCopy.CopyAsync($"Data Source={db}", copies, "4.7.0", CancellationToken.None);
        var copy = await DatabaseCopy.CopyAsync($"Data Source={db}", copies, "4.8.0", CancellationToken.None);

        Assert.Equal([Path.Combine(copies, "oadm-4.8.0.db")], Directory.GetFiles(copies, "*.db"));
        await using var read = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False");
        await read.OpenAsync();
        await using var query = read.CreateCommand();
        query.CommandText = "SELECT v FROM t";
        Assert.Equal("kept", await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AFailedCopyKeepsTheCopyOfTheLastUpdate()
    {
        using var folder = new TempFolder();
        var copies = Path.Combine(folder.Path, "update");
        Directory.CreateDirectory(copies);
        var earlier = Path.Combine(copies, "oadm-4.7.0.db");
        await File.WriteAllTextAsync(earlier, "the copy of 4.7.0");

        var missing = Path.Combine(folder.Path, "missing.db");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => DatabaseCopy.CopyAsync($"Data Source={missing};Mode=ReadOnly", copies, "4.8.0", CancellationToken.None));

        Assert.Equal([earlier], Directory.GetFiles(copies));
    }

    [Fact]
    public async Task ACancelledCopyChangesNothing()
    {
        using var folder = new TempFolder();
        var copies = Path.Combine(folder.Path, "update");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseCopy.CopyAsync($"Data Source={Path.Combine(folder.Path, "source.db")}", copies, "4.8.0", cancelled.Token));

        Assert.Empty(Directory.GetFiles(copies));
    }

    [Fact]
    public async Task AHelperThatCannotStartEndsTheUpdate()
    {
        var fakes = new Fakes();
        fakes.Starter.Fails = true;
        await using var host = await StartAsync(fakes);

        Assert.Equal(UpdateTexts.HelperFailed, await RefusalAsync(host, "4.9.0"));

        var record = host.Get<UpdateRecordFile>().Read()!;
        Assert.Equal((UpdateRecord.Failed, UpdateTexts.HelperFailed, true), (record.Result, record.Reason, record.Audited));
        Assert.Null(host.Get<TaskEngine>().HeldReason);
        Assert.NotNull(await host.Devices.ListAsync(new Proto.Empty()));
        Assert.Contains(await AuditAsync(host), e => e.Action == AuditActions.ServerUpdateFailed);
    }

    [Fact]
    public async Task AHelperThatCannotStartOpensTheServerEvenWhenTheRecordCannotBeWritten()
    {
        var fakes = new Fakes();
        fakes.Starter.Fails = true;
        await using var host = await StartAsync(fakes);
        var records = host.Get<UpdateRecordFile>();
        fakes.Starter.BeforeFailing = () =>
        {
            // The update folder is gone and a file has its name: the record can be neither read nor written.
            Directory.Delete(records.Folder, recursive: true);
            File.WriteAllText(records.Folder, "in the way");
        };

        Assert.Equal(UpdateTexts.HelperFailed, await RefusalAsync(host, "4.9.0"));

        Assert.Null(host.Get<UpdateGate>().ClosedFor);
        Assert.Null(host.Get<TaskEngine>().HeldReason);
        Assert.False(host.Get<ActionRunner>().IsHeld);
        Assert.NotNull(await host.Devices.ListAsync(new Proto.Empty()));
    }

    [Fact]
    public async Task TheGiveUpNeverOverwritesTheHelpersResult()
    {
        using var folder = new TempFolder();
        var records = new UpdateRecordFile(folder.Path);
        records.Write(Running());

        // The helper's result lands between the server's look and its change: the change decides on what the file holds now.
        records.Write(Running() with { Result = UpdateRecord.Done });
        Assert.Null(await records.UpdateRunningAsync(r => r with { Result = UpdateRecord.Failed, Reason = UpdateTexts.InstallerDidNotFinish }, CancellationToken.None));

        Assert.Equal((UpdateRecord.Done, (string?)null), (records.Read()!.Result, records.Read()!.Reason));
    }

    [Fact]
    public async Task ARecordHeldOpenByTheOtherProcessIsWrittenOnceItIsFree()
    {
        using var folder = new TempFolder();
        var time = new FakeTimeProvider();
        var records = new UpdateRecordFile(folder.Path, time);
        records.Write(Running());
        var reader = new FileStream(records.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read); // the helper reads it right now
        var write = records.WriteAsync(Running() with { Result = UpdateRecord.Done }, CancellationToken.None);

        await reader.DisposeAsync();
        await Eventually.TrueAsync(
            () =>
            {
                time.Advance(TimeSpan.FromMilliseconds(50));
                return write.IsCompleted;
            },
            "the write goes through once the file is free");
        await write;

        Assert.Equal(UpdateRecord.Done, (await records.ReadAsync(CancellationToken.None))!.Result);
    }

    [Fact]
    public async Task AnInstallerThatFailsWhileTheServerRunsLetsItGoOn()
    {
        var fakes = new Fakes();
        await using var host = await StartAsync(fakes);
        await Client(host).StartAsync(new Proto.StartUpdateRequest { Version = "4.9.0" });
        var records = host.Get<UpdateRecordFile>();

        // The helper's result: the installer found another installation running (the service never stopped).
        records.Write(records.Read()! with { Result = UpdateRecord.Failed, Reason = UpdateTexts.InstallerBusy });
        await Eventually.TrueAsync(
            () =>
            {
                fakes.Time.Advance(ServerUpdateCoordinator.FollowInterval);
                return records.Read()!.Audited;
            },
            "the server reads the helper's result");

        Assert.Null(host.Get<UpdateGate>().ClosedFor);
        Assert.Null(host.Get<TaskEngine>().HeldReason);
        Assert.NotNull(await host.Devices.ListAsync(new Proto.Empty()));
        Assert.Equal(string.Empty, (await new Proto.AuthService.AuthServiceClient(host.Channel).StatusAsync(new Proto.Empty())).UpdatingTo); // the client's helper starts the client
        var failed = Assert.Single(await AuditAsync(host), e => e.Action == AuditActions.ServerUpdateFailed);
        Assert.Equal("From 4.8.0 to 4.9.0: " + UpdateTexts.InstallerBusy, failed.Detail);
        var last = (await Client(host).GetAsync(new Proto.Empty())).Last;
        Assert.Equal((UpdateRecord.Failed, UpdateTexts.InstallerBusy), (last.Result, last.Reason));
    }

    private static UpdateRecord Running(string to = "4.9.0") => new()
    {
        From = "4.8.0",
        To = to,
        StartedUtc = new DateTimeOffset(2026, 11, 3, 9, 59, 0, TimeSpan.Zero),
        Step = UpdateRecord.StepWait,
        RequestedBy = "admin@PC-1",
        ClientAddress = "10.0.0.9",
    };

    private static string Seed(UpdateRecord record)
    {
        var data = TestServerHost.NewDataDirectory();
        new UpdateRecordFile(data).Write(record);
        return data;
    }

    [Fact]
    public async Task TheNewVersionCompletesTheRecordAfterTheRestart()
    {
        var data = Seed(Running());
        var fakes = new Fakes { Version = "4.9.0" };
        await using var host = await StartAsync(fakes, data);

        var record = host.Get<UpdateRecordFile>().Read()!;
        Assert.Equal((UpdateRecord.Done, true), (record.Result, record.Audited));
        var done = Assert.Single(await AuditAsync(host), e => e.Action == AuditActions.ServerUpdated);
        Assert.Equal(("admin", "10.0.0.9", "From 4.8.0 to 4.9.0"), (done.UserName, done.ClientAddress, done.Detail));
        Assert.Equal("done", (await Client(host).GetAsync(new Proto.Empty())).Last.Result);
    }

    [Fact]
    public async Task AFailureTheHelperWroteIsAuditedOnceAcrossRestarts()
    {
        var data = Seed(Running() with { Result = UpdateRecord.Failed, Reason = "The installer failed (code 1603).", InstallerLog = @"C:\ProgramData\OADM\update\install.log" });
        var fakes = new Fakes();
        await using (var host = await StartAsync(fakes, data))
        {
            host.KeepDataDirectory = true;
            Assert.Single(await AuditAsync(host), e => e.Action == AuditActions.ServerUpdateFailed);
        }

        await using var again = await StartAsync(fakes, data);
        Assert.Single(await AuditAsync(again), e => e.Action == AuditActions.ServerUpdateFailed);
        var last = (await Client(again).GetAsync(new Proto.Empty())).Last;
        Assert.Equal(("failed", @"C:\ProgramData\OADM\update\install.log"), (last.Result, last.InstallerLog));
    }

    [Fact]
    public async Task ANewVersionThatCameBackLateIsDone()
    {
        var data = Seed(Running() with { Result = UpdateRecord.NotBack, Reason = UpdateTexts.NewVersionSilent });
        await using var host = await StartAsync(new Fakes { Version = "4.9.0" }, data);

        var record = host.Get<UpdateRecordFile>().Read()!;
        Assert.Equal((UpdateRecord.Done, null), (record.Result, record.Reason));
    }

    [Fact]
    public async Task TheOldServerWaitsForTheHelperAndGivesUpAfterTwentyMinutes()
    {
        var data = Seed(Running() with { StartedUtc = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero) });
        var fakes = new Fakes();
        await using var host = await StartAsync(fakes, data);
        var records = host.Get<UpdateRecordFile>();
        Assert.True(records.Read()!.IsRunning); // the helper may still write

        await Eventually.TrueAsync(
            () =>
            {
                fakes.Time.Advance(TimeSpan.FromMinutes(1));
                return records.Read()!.Audited;
            },
            "the server gives up");

        var record = records.Read()!;
        Assert.Equal((UpdateRecord.Failed, UpdateTexts.InstallerDidNotFinish), (record.Result, record.Reason));
    }

    [Fact]
    public async Task OnlyAdministratorsSeeAndStartTheUpdateEveryoneHearsOfIt()
    {
        await using var host = await StartAsync(new Fakes());
        var operatorInvoker = await host.InvokerForUserAsync("op", UserRole.Operator);
        var client = new Proto.UpdateService.UpdateServiceClient(operatorInvoker);

        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await client.GetAsync(new Proto.Empty()))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await client.StartAsync(new Proto.StartUpdateRequest { Version = "4.9.0" }))).StatusCode);
        using var watch = client.Watch(new Proto.Empty());
        await Eventually.TrueAsync(() => WatcherCount(host) == 1, "the operator's client watches");
    }

    [Fact]
    public async Task GetTellsTheAdministratorWhatTheDialogNeeds()
    {
        await using var host = await StartAsync(new Fakes());

        var info = await Client(host).GetAsync(new Proto.Empty());

        Assert.Equal(("4.8.0", "windows", "4.9.0", true, false), (info.ServerVersion, info.Platform, info.LatestVersion, info.Installed, info.SameComputer));
        Assert.Null(info.Running);
        Assert.Null(info.Last);
    }

    [Theory]
    [InlineData("127.0.0.1", "PC-1", "PC-1", true)]
    [InlineData("::1", "pc-1", "PC-1", true)]
    [InlineData("10.0.0.5", "PC-1", "PC-1", true)]
    [InlineData("10.0.0.6", "PC-1", "PC-1", false)]
    [InlineData("127.0.0.1", "PC-2", "PC-1", false)]
    public void AClientIsOnTheServersComputerByAddressAndName(string remote, string client, string server, bool same) =>
        Assert.Equal(same, UpdateGrpcService.SameComputer(System.Net.IPAddress.Parse(remote), client, server, [System.Net.IPAddress.Parse("10.0.0.5")]));

    private static int WatcherCount(TestServerHost host) => host.Get<ServerUpdateCoordinator>().WatcherCount;

    /// <summary>A task that runs until the test lets it end.</summary>
    private sealed class HoldingPlugin : ITaskPlugin
    {
        public const string PluginId = "test.holding";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => PluginId;

        public string DisplayName => "Holding";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => true;

        public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }
}
