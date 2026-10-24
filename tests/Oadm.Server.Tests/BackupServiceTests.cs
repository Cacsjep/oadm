using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Backup;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Server.Backup;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>BackupService through the in-process server: back up, check, restore with a restart, automatic backups.</summary>
public sealed class BackupServiceTests
{
    private const string Password = "correct horse";

    private sealed class RecordingRestart : IServerRestart
    {
        public int Requests { get; private set; }

        public void Request() => Requests++;
    }

    private static Task<TestServerHost> StartAsync(RecordingRestart restart, string? dataDirectory = null) =>
        TestServerHost.StartAsync(dataDirectory: dataDirectory, configureServices: services =>
        {
            services.RemoveAll<BackupArchive>();
            services.AddSingleton(sp => new BackupArchive(sp.GetRequiredService<OadmPaths>()) { Iterations = 1000 });
            services.RemoveAll<IServerRestart>();
            services.AddSingleton<IServerRestart>(restart);
        });

    private static Proto.BackupService.BackupServiceClient Client(TestServerHost host) => new(host.Invoker);

    private static async Task<(string Name, byte[] Content)> ReadAllAsync(AsyncServerStreamingCall<Proto.DownloadChunk> call)
    {
        using (call)
        {
            Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
            var name = call.ResponseStream.Current.FileName;
            var length = call.ResponseStream.Current.Length;
            using var content = new MemoryStream();
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                call.ResponseStream.Current.Data.WriteTo(content);
            }

            Assert.Equal(length, content.Length);
            return (name, content.ToArray());
        }
    }

    private static async Task<string> UploadAsync(TestServerHost host, string name, byte[] content)
    {
        var files = new Proto.FileService.FileServiceClient(host.Invoker);
        using var call = files.Upload();
        await call.RequestStream.WriteAsync(new Proto.UploadChunk { Header = new Proto.UploadHeader { Name = name, Size = content.Length } });
        for (var i = 0; i < content.Length; i += 256 * 1024)
        {
            await call.RequestStream.WriteAsync(new Proto.UploadChunk { Data = ByteString.CopyFrom(content, i, Math.Min(256 * 1024, content.Length - i)) });
        }

        await call.RequestStream.CompleteAsync();
        return (await call.ResponseAsync).Id;
    }

    [Fact]
    public async Task ABackupMadeNowIsCheckedAndRestoredAfterTheRestart()
    {
        var restart = new RecordingRestart();
        var host = await StartAsync(restart);
        host.KeepDataDirectory = true;
        var dataDirectory = host.DataDirectory;
        var identity = "";
        try
        {
            var backup = Client(host);
            var device = await host.Get<DeviceRepository>().AddAsync(new Device { Serial = "B8A44F631339", Address = "10.0.0.48" }, CancellationToken.None);
            await host.Get<CredentialStore>().SetAsync(device.Id, "root", "pass-1", CancellationToken.None);
            // A site and the server's site identity travel in oadm.db: a restored server keeps its sites.
            await using (var db = await host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Oadm.Core.Persistence.OadmDbContext>>().CreateDbContextAsync())
            {
                db.Sites.Add(new Oadm.Core.Sites.Site { Name = "Building B", NormalizedName = "BUILDING B", NodeKeyFingerprint = "AB", NodeId = "node-1", BoundUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            identity = host.Get<Oadm.Server.Sites.SiteIdentityStore>().FingerprintHex;

            var refused = await Assert.ThrowsAsync<RpcException>(() => ReadAllAsync(backup.Create(new Proto.CreateBackupRequest { Password = "short" })));
            Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);

            var (name, content) = await ReadAllAsync(backup.Create(new Proto.CreateBackupRequest { Password = Password }));
            Assert.StartsWith("oadm-backup-", name, StringComparison.Ordinal);
            Assert.EndsWith(".oadmbackup", name, StringComparison.Ordinal);

            // The state changes after the backup; the restore brings back the device without the new one.
            await host.Get<DeviceRepository>().AddAsync(new Device { Serial = "B8A44F000002", Address = "10.0.0.49" }, CancellationToken.None);
            var fileId = await UploadAsync(host, name, content);

            var wrong = await backup.InspectAsync(new Proto.RestoreRequest { FileId = fileId, Password = "wrong password" });
            Assert.True(wrong.WrongPassword);
            var info = await backup.InspectAsync(new Proto.RestoreRequest { FileId = fileId, Password = Password });
            Assert.Empty(info.Problem);
            Assert.Contains("Devices: 1", info.Lines);
            Assert.Contains("Sites: 1", info.Lines);

            var reply = await backup.RestoreAsync(new Proto.RestoreRequest { FileId = fileId, Password = Password });
            Assert.Equal(1, restart.Requests);
            Assert.EndsWith("-before-restore.oadmbackup", reply.SafetyBackup, StringComparison.Ordinal);
            Assert.Contains((await backup.GetStateAsync(new Proto.Empty())).Backups, b => b.Name == reply.SafetyBackup && b.Kind == "before-restore");
        }
        finally
        {
            await host.DisposeAsync();
        }

        // The restart: the new start applies the backup before the database opens.
        var restarted = await StartAsync(restart, dataDirectory);
        try
        {
            var devices = await restarted.Get<DeviceRepository>().ListDevicesAsync(CancellationToken.None);
            var device = Assert.Single(devices);
            Assert.Equal("pass-1", (await restarted.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None))!.Password);
            var state = await Client(restarted).GetStateAsync(new Proto.Empty());
            Assert.Empty(state.LastRestore.Error);
            Assert.Contains("Devices: 1 restored", state.LastRestore.Lines);
            Assert.Contains("Sites: 1 restored", state.LastRestore.Lines);
            Assert.Equal(identity, restarted.Get<Oadm.Server.Sites.SiteIdentityStore>().FingerprintHex);
            Assert.Equal("Building B", Assert.Single(restarted.Get<Oadm.Server.Sites.SiteManager>().Sites).Name);
            Assert.StartsWith("admin", state.LastRestore.RequestedBy, StringComparison.Ordinal);
            var audit = await restarted.Audit.ListAsync(new Proto.ListAuditRequest { Limit = 50 });
            Assert.Contains(audit.Entries, e => e.Action == "Restored backup");
        }
        finally
        {
            restarted.KeepDataDirectory = false;
            await restarted.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheProtectedTagsOfAnInstalledPluginComeBackWithTheRestore()
    {
        var restart = new RecordingRestart();
        var host = await TestServerHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<BackupArchive>();
            services.AddSingleton(sp => new BackupArchive(sp.GetRequiredService<OadmPaths>()) { Iterations = 1000 });
            services.RemoveAll<IServerRestart>();
            services.AddSingleton<IServerRestart>(restart);
            DeviceGroups.SharedCore.TagScopeTests.InstalledVlanPlugin.Install(services);
        });
        host.KeepDataDirectory = true;
        var dataDirectory = host.DataDirectory;
        try
        {
            var registry = host.Get<ProtectedTagRegistry>();
            registry.For(DeviceGroups.SharedCore.TagScopeTests.InstalledVlanPlugin.PluginId).Protect(["Vlan 20"], name => $"The tag {name} sets a VLAN.");
            await registry.FlushAsync();
            var (name, content) = await ReadAllAsync(Client(host).Create(new Proto.CreateBackupRequest { Password = Password }));

            // After the backup the rule is gone; the restore brings the protected tag back.
            registry.For(DeviceGroups.SharedCore.TagScopeTests.InstalledVlanPlugin.PluginId).Protect([], name => name);
            await registry.FlushAsync();
            var fileId = await UploadAsync(host, name, content);
            await Client(host).RestoreAsync(new Proto.RestoreRequest { FileId = fileId, Password = Password });
            Assert.Equal(1, restart.Requests);
        }
        finally
        {
            await host.DisposeAsync();
        }

        var restarted = await TestServerHost.StartAsync(dataDirectory: dataDirectory, configureServices: services =>
        {
            services.RemoveAll<BackupArchive>();
            services.AddSingleton(sp => new BackupArchive(sp.GetRequiredService<OadmPaths>()) { Iterations = 1000 });
            DeviceGroups.SharedCore.TagScopeTests.InstalledVlanPlugin.Install(services);
        });
        try
        {
            Assert.Equal("The tag Vlan 20 sets a VLAN.", restarted.Get<ProtectedTagRegistry>().RefusalFor(["Vlan 20"]));
        }
        finally
        {
            restarted.KeepDataDirectory = false;
            await restarted.DisposeAsync();
        }
    }

    [Fact]
    public async Task ABackupOfANewerOadmOrADamagedFileIsRefusedAndNothingChanges()
    {
        var restart = new RecordingRestart();
        await using var host = await StartAsync(restart);
        var backup = Client(host);

        var newer = new MemoryStream();
        using (var body = BackupFile.Create(newer, new BackupHeader { Migration = "99991231000000_Future", OadmVersion = "9.0.0" }, Password, iterations: 1000))
        {
            body.Write([1, 2, 3]);
        }

        var newerId = await UploadAsync(host, "future.oadmbackup", newer.ToArray());
        var info = await backup.InspectAsync(new Proto.RestoreRequest { FileId = newerId, Password = Password });
        Assert.Contains("Update OADM to 9.0.0 or later first.", info.Problem, StringComparison.Ordinal);
        var ex = await Assert.ThrowsAsync<RpcException>(() => backup.RestoreAsync(new Proto.RestoreRequest { FileId = newerId, Password = Password }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);

        var junkId = await UploadAsync(host, "junk.oadmbackup", [1, 2, 3, 4]);
        Assert.Equal("The file is not an OADM backup.", (await backup.InspectAsync(new Proto.RestoreRequest { FileId = junkId, Password = Password })).Problem);

        Assert.Equal(0, restart.Requests);
        Assert.False(host.Get<BackupArchive>().HasPending);
        Assert.Empty((await backup.GetStateAsync(new Proto.Empty())).Backups);
    }

    [Fact]
    public async Task AutomaticBackupsNeedAPasswordAreKeptListedDownloadedAndDeleted()
    {
        await using var host = await StartAsync(new RecordingRestart());
        var backup = Client(host);

        var noPassword = await Assert.ThrowsAsync<RpcException>(() => backup.SetScheduleAsync(new Proto.BackupSchedule { Enabled = true, TimeOfDay = "02:00", Keep = 2 }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, noPassword.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => backup.SetScheduleAsync(new Proto.BackupSchedule { Enabled = true, TimeOfDay = "25:00", Keep = 2, Password = Password }).ResponseAsync)).StatusCode);

        var state = await backup.SetScheduleAsync(new Proto.BackupSchedule { Enabled = true, TimeOfDay = "02:00", Keep = 2, Password = Password });
        Assert.True(state.HasPassword);
        Assert.Empty(state.Schedule.Password);

        var manager = host.Get<BackupManager>();
        for (var i = 0; i < 3; i++)
        {
            await manager.RunAutomaticAsync(CancellationToken.None);
            await Task.Delay(1100); // one name per second
        }

        state = await backup.GetStateAsync(new Proto.Empty());
        Assert.Equal(2, state.Backups.Count); // the oldest beyond Keep removed
        Assert.Empty(state.LastAutomaticError);
        var name = state.Backups[0].Name;
        var (downloaded, content) = await ReadAllAsync(backup.Download(new Proto.BackupName { Name = name }));
        Assert.Equal(name, downloaded);
        Assert.Equal(state.Backups[0].Size, content.Length);

        var info = await backup.InspectAsync(new Proto.RestoreRequest { Name = name, Password = Password });
        Assert.Equal("automatic", info.Kind);

        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(() => backup.DeleteAsync(new Proto.BackupName { Name = "../oadm.db" }).ResponseAsync)).StatusCode);
        state = await backup.DeleteAsync(new Proto.BackupName { Name = name });
        Assert.Single(state.Backups);
    }

    [Theory]
    [InlineData("2026-10-13T01:00:00+02:00", null, false)] // before the time, never made
    [InlineData("2026-10-13T02:00:30+02:00", null, true)]
    [InlineData("2026-10-13T09:00:00+02:00", "2026-10-13T02:00:40+02:00", false)] // made today
    [InlineData("2026-10-13T09:00:00+02:00", "2026-10-12T02:00:40+02:00", true)] // yesterday's: today's is due
    [InlineData("2026-10-13T01:00:00+02:00", "2026-10-11T02:00:40+02:00", true)] // the server was off: yesterday's is made up
    [InlineData("2026-10-13T01:00:00+02:00", "2026-10-12T02:00:40+02:00", false)]
    public void TheAutomaticBackupIsDueOncePerDayAfterItsTime(string now, string? last, bool due)
    {
        var schedule = new BackupScheduleSetting { Enabled = true, TimeOfDay = "02:00" };
        Assert.Equal(due, BackupScheduleHostedService.IsDue(schedule, DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), last is null ? null : DateTimeOffset.Parse(last, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(BackupScheduleHostedService.IsDue(schedule with { Enabled = false }, DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), null));
    }
}
