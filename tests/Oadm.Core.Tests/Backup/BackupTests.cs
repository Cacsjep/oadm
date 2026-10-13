using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Backup;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Tests.Persistence;

namespace Oadm.Core.Tests.Backup;

/// <summary>The backup file format, a backup of one server restored on another, older backups, and refused ones.</summary>
public sealed class BackupTests
{
    private const string Password = "correct horse";

    private static BackupHeader Header(string kind = "manual") => new() { Kind = kind, OadmVersion = "2.7.0", ServerName = "site-a" };

    private static byte[] Write(byte[] body, int chunkSize = 1024, string password = Password)
    {
        using var output = new MemoryStream();
        using (var stream = BackupFile.Create(output, Header(), password, iterations: 1000, chunkSize: chunkSize))
        {
            stream.Write(body);
        }

        return output.ToArray();
    }

    private static byte[] Read(byte[] file, string password = Password)
    {
        var (_, body) = BackupFile.Open(new MemoryStream(file), password);
        using (body)
        {
            using var copy = new MemoryStream();
            body.CopyTo(copy);
            return copy.ToArray();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(3 * 1024)]
    [InlineData(5000)]
    public void AnyBodySizeComesBackUnchanged(int size)
    {
        var body = RandomNumberGenerator.GetBytes(size);
        Assert.Equal(body, Read(Write(body)));
    }

    [Fact]
    public void TheHeaderIsReadableWithoutThePasswordAndSaysNothingSecret()
    {
        var file = Write(Encoding.UTF8.GetBytes("secret body"));
        var header = BackupFile.ReadHeader(new MemoryStream(file));
        Assert.Equal("2.7.0", header.OadmVersion);
        Assert.Equal("site-a", header.ServerName);
        Assert.Equal(1000, header.Iterations);
        Assert.DoesNotContain("secret body", Encoding.UTF8.GetString(file), StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongPasswordIsNamedAsSuch()
    {
        var ex = Assert.Throws<BackupFileException>(() => Read(Write([1, 2, 3]), "wrong password"));
        Assert.True(ex.IsWrongPassword);
        Assert.Equal("The password is wrong.", ex.Message);
    }

    [Fact]
    public void AChangedCutOrForeignFileIsRefused()
    {
        var file = Write(RandomNumberGenerator.GetBytes(3000));

        var flipped = (byte[])file.Clone();
        flipped[^100] ^= 1;
        Assert.Throws<BackupFileException>(() => Read(flipped));

        // Cut after the first full chunk: the last chunk is missing.
        Assert.Throws<BackupFileException>(() => Read(file[..^((3000 - (2 * 1024)) + 16)]));
        Assert.Throws<BackupFileException>(() => Read(file[..^1]));
        Assert.Throws<BackupFileException>(() => Read([.. file, 0])); // data after the end

        // A changed header (another server name) no longer matches the chunks.
        var text = Encoding.UTF8.GetString(file);
        var renamed = Encoding.UTF8.GetBytes(text.Replace("site-a", "site-b", StringComparison.Ordinal));
        if (renamed.Length == file.Length)
        {
            Assert.Throws<BackupFileException>(() => Read(renamed));
        }

        var notABackup = Assert.Throws<BackupFileException>(() => BackupFile.ReadHeader(new MemoryStream(Encoding.UTF8.GetBytes("PK this is a zip"))));
        Assert.Equal("The file is not an OADM backup.", notABackup.Message);
    }

    [Fact]
    public void ShortPasswordsAreRefused()
    {
        Assert.Equal("Enter a password.", BackupFile.PasswordProblem(""));
        Assert.Equal("Use at least 8 characters.", BackupFile.PasswordProblem("short"));
        Assert.Null(BackupFile.PasswordProblem(Password));
        Assert.Throws<ArgumentException>(() => BackupFile.Create(new MemoryStream(), Header(), "short"));
    }

    [Fact]
    public void ABackupOfANewerOadmIsRefusedWithWhatToDo()
    {
        string[] known = ["20260101000000_InitialCreate", "20261012071736_Roles"];
        Assert.Null(BackupArchive.VersionProblem(new BackupHeader { Migration = "20261012071736_Roles" }, known, "2.7.0"));
        Assert.Null(BackupArchive.VersionProblem(new BackupHeader { Migration = "20260101000000_InitialCreate" }, known, "2.7.0"));
        Assert.Equal(
            "This backup is from OADM 3.0.0, newer than this server (2.7.0). Update OADM to 3.0.0 or later first.",
            BackupArchive.VersionProblem(new BackupHeader { Migration = "20270101000000_Later", OadmVersion = "3.0.0" }, known, "2.7.0"));
    }

    private static async Task<Device> AddDeviceAsync(TestDatabase db, string password)
    {
        var device = await db.Get<DeviceRepository>().AddAsync(new Device { Serial = "B8A44F631339", Address = "10.0.0.48", Model = "P3265-V" }, CancellationToken.None);
        await db.Get<CredentialStore>().SetAsync(device.Id, "root", password, CancellationToken.None);
        return device;
    }

    [Fact]
    public async Task ABackupRestoredOnAnotherServerBringsDevicesPasswordsAndPluginData()
    {
        var sourceDir = TestDatabase.NewTempDirectory();
        var targetDir = TestDatabase.NewTempDirectory();
        try
        {
            // Server A: a device with its password and a plugin's working file.
            var a = await TestDatabase.CreateAsync(sourceDir);
            var device = await AddDeviceAsync(a, "pass-of-a");
            Directory.CreateDirectory(Path.Combine(a.Paths.PluginDataDirectory, "oadm.pki"));
            await File.WriteAllTextAsync(Path.Combine(a.Paths.PluginDataDirectory, "oadm.pki", "note.txt"), "kept");
            var file = new MemoryStream();
            var contents = await new BackupArchive(a.Paths) { Iterations = 1000 }.CreateAsync(file, Password, Header(), [new BackupPlugin("oadm.pki", "2.7.0")], CancellationToken.None);
            await a.CloseAsync();
            Assert.Equal(1, contents.Manifest.Counts.Devices);
            Assert.Equal(1, contents.Manifest.Counts.DeviceLogins);
            Assert.Equal(["oadm.pki"], contents.Manifest.PluginData);
            Assert.EndsWith("_Roles", contents.Header.Migration, StringComparison.Ordinal);

            // Server B: another master key, another device; the restore replaces both.
            var b = await TestDatabase.CreateAsync(targetDir);
            await AddDeviceAsync(b, "pass-of-b");
            var keyOfB = await File.ReadAllBytesAsync(b.Paths.MasterKeyPath);
            await b.CloseAsync();
            var archive = new BackupArchive(b.Paths);

            file.Position = 0;
            var inspected = await archive.InspectAsync(file, Password, CancellationToken.None);
            Assert.Equal(contents.Manifest.Counts, inspected.Manifest.Counts);

            file.Position = 0;
            await archive.StageAsync(file, Password, "site-a.oadmbackup", "admin@PC", "10.0.0.5", CancellationToken.None);
            Assert.True(archive.HasPending);
            byte[]? changedFrom = null;
            var outcome = archive.ApplyPending((oldKey, _) => changedFrom = oldKey);
            Assert.NotNull(outcome);
            Assert.Null(outcome.Error);
            Assert.False(archive.HasPending);
            Assert.Equal(keyOfB, changedFrom);
            Assert.Equal("admin@PC", archive.ReadOutcome()!.Restore.RequestedBy);
            Assert.NotNull(archive.ReadOutcome()); // kept until the server recorded it
            archive.ForgetOutcome();
            Assert.Null(archive.ReadOutcome());

            var restored = await TestDatabase.CreateAsync(targetDir);
            var devices = await restored.Get<DeviceRepository>().ListDevicesAsync(CancellationToken.None);
            Assert.Equal(device.Id, Assert.Single(devices).Id);
            Assert.Equal("pass-of-a", (await restored.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None))!.Password);
            Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(restored.Paths.PluginDataDirectory, "oadm.pki", "note.txt")));
            Assert.Single(Directory.GetDirectories(archive.RestoreDirectory, "previous-*"));
            await restored.CloseAsync();
        }
        finally
        {
            TestDatabase.DeleteDirectory(sourceDir);
            TestDatabase.DeleteDirectory(targetDir);
        }
    }

    [Fact]
    public async Task ABackupOfAnOlderOadmIsUpgradedAndTheOldDatabaseCopiedFirst()
    {
        var sourceDir = TestDatabase.NewTempDirectory();
        var targetDir = TestDatabase.NewTempDirectory();
        try
        {
            // A database of an older OADM: migrated only up to the event rules (before roles).
            var olderPaths = new OadmPaths(sourceDir);
            olderPaths.EnsureDirectories();
            using (var provider = new ServiceCollection().AddOadmPersistence(olderPaths).BuildServiceProvider())
            {
                await using var db = await provider.GetRequiredService<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
                var older = db.Database.GetMigrations().Single(m => m.EndsWith("_EventRules", StringComparison.Ordinal));
                await db.GetService<IMigrator>().MigrateAsync(older);
                _ = provider.GetRequiredService<CredentialProtector>(); // writes master.key
            }

            SqliteConnection.ClearAllPools();
            var file = new MemoryStream();
            var contents = await new BackupArchive(olderPaths) { Iterations = 1000 }.CreateAsync(file, Password, Header(), [], CancellationToken.None);
            Assert.EndsWith("_EventRules", contents.Header.Migration, StringComparison.Ordinal);
            Assert.Equal(0, contents.Manifest.Counts.Roles); // no roles table yet

            var target = await TestDatabase.CreateAsync(targetDir);
            await target.CloseAsync();
            var archive = new BackupArchive(target.Paths);
            file.Position = 0;
            await archive.StageAsync(file, Password, "old.oadmbackup", "admin", "", CancellationToken.None);
            Assert.Null(archive.ApplyPending()!.Error);

            var upgraded = await TestDatabase.CreateAsync(targetDir);
            await using (var ctx = await upgraded.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
            {
                Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
                Assert.NotEmpty(await ctx.Roles.ToListAsync()); // the Roles migration ran on the restored database
            }

            Assert.Single(Directory.GetFiles(Path.Combine(targetDir, "backups", "before-upgrade"), "oadm-*-*_EventRules.db"));
            await upgraded.CloseAsync();
        }
        finally
        {
            TestDatabase.DeleteDirectory(sourceDir);
            TestDatabase.DeleteDirectory(targetDir);
        }
    }

    [Fact]
    public async Task ASwapCutOffByADyingProcessIsPutBackAtTheNextStart()
    {
        var dir = TestDatabase.NewTempDirectory();
        try
        {
            var db = await TestDatabase.CreateAsync(dir);
            var device = await AddDeviceAsync(db, "own");
            await db.CloseAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var key = await File.ReadAllBytesAsync(db.Paths.MasterKeyPath);

            // The process died after moving the server's files out, before the staged ones came in.
            var archive = new BackupArchive(db.Paths);
            var previous = Path.Combine(archive.RestoreDirectory, "previous-20261013-120000");
            Directory.CreateDirectory(previous);
            File.Move(db.Paths.DatabasePath, Path.Combine(previous, OadmPaths.DatabaseFileName));
            File.Move(db.Paths.MasterKeyPath, Path.Combine(previous, OadmPaths.MasterKeyFileName));
            await File.WriteAllTextAsync(Path.Combine(archive.RestoreDirectory, BackupArchive.SwapMarkerFile), previous);

            Assert.Null(archive.ApplyPending()); // nothing staged, but the interrupted swap is put back

            Assert.Equal(key, await File.ReadAllBytesAsync(db.Paths.MasterKeyPath));
            Assert.False(File.Exists(Path.Combine(archive.RestoreDirectory, BackupArchive.SwapMarkerFile)));
            var reopened = await TestDatabase.CreateAsync(dir);
            Assert.Equal("own", (await reopened.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None))!.Password);
            await reopened.CloseAsync();
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task AFailureWhileSwappingPutsTheServersOwnFilesBack()
    {
        var dir = TestDatabase.NewTempDirectory();
        try
        {
            var db = await TestDatabase.CreateAsync(dir);
            var device = await AddDeviceAsync(db, "own");
            var file = new MemoryStream();
            await new BackupArchive(db.Paths) { Iterations = 1000 }.CreateAsync(file, Password, Header(), [], CancellationToken.None);
            await db.CloseAsync();
            var key = await File.ReadAllBytesAsync(db.Paths.MasterKeyPath);

            var archive = new BackupArchive(db.Paths);
            file.Position = 0;
            await archive.StageAsync(file, Password, "x", "admin", "", CancellationToken.None);
            var outcome = archive.ApplyPending((_, _) => throw new IOException("disk gone"));

            Assert.Equal("disk gone", outcome!.Error);
            Assert.Equal(key, await File.ReadAllBytesAsync(db.Paths.MasterKeyPath));
            var reopened = await TestDatabase.CreateAsync(dir);
            Assert.Equal("own", (await reopened.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None))!.Password);
            await reopened.CloseAsync();
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task StagingUnpacksOnlyTheBackupPartsAndNeverOutsideItsFolder()
    {
        var dir = TestDatabase.NewTempDirectory();
        try
        {
            var paths = new OadmPaths(dir);
            paths.EnsureDirectories();
            var file = new MemoryStream();
            using (var body = BackupFile.Create(file, Header(), Password, iterations: 1000))
            using (var zip = new ZipArchive(body, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Entry(string name, byte[] data)
                {
                    using var s = zip.CreateEntry(name).Open();
                    s.Write(data);
                }

                Entry("manifest.json", Encoding.UTF8.GetBytes("{}"));
                Entry("oadm.db", [1]);
                Entry("master.key", new byte[32]);
                Entry("plugin-data/../../evil.txt", [1]);
                Entry("plugin-data/x/../../../evil2.txt", [1]);
                Entry("other.txt", [1]);
                Entry("plugin-data/oadm.pki/ok.txt", [1]);
            }

            var archive = new BackupArchive(paths);
            file.Position = 0;
            await archive.StageAsync(file, Password, "x", "admin", "", CancellationToken.None);

            var staged = Path.Combine(archive.RestoreDirectory, "staged");
            var files = Directory.GetFiles(staged, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staged, f).Replace('\\', '/')).Order(StringComparer.Ordinal);
            Assert.Equal(["master.key", "oadm.db", "plugin-data/oadm.pki/ok.txt"], files);
            Assert.Empty(Directory.GetFiles(dir, "evil*", SearchOption.AllDirectories));
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task AWrongPasswordStagesNothing()
    {
        var dir = TestDatabase.NewTempDirectory();
        try
        {
            var db = await TestDatabase.CreateAsync(dir);
            var file = new MemoryStream();
            await new BackupArchive(db.Paths) { Iterations = 1000 }.CreateAsync(file, Password, Header(), [], CancellationToken.None);
            await db.CloseAsync();
            var archive = new BackupArchive(db.Paths);
            file.Position = 0;

            var ex = await Assert.ThrowsAsync<BackupFileException>(() => archive.StageAsync(file, "another password", "x", "admin", "", CancellationToken.None));

            Assert.True(ex.IsWrongPassword);
            Assert.False(archive.HasPending);
            Assert.Null(archive.ApplyPending());
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }
}
