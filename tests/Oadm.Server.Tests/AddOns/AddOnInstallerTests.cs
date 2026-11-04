using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using Oadm.Contracts.Updates;
using Oadm.Core.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Server.AddOns;

[assembly: AssemblyMetadata(Oadm.Server.AddOns.AssemblyAddOnHashes.MetadataPrefix + "oadm.test", "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
[assembly: AssemblyMetadata(Oadm.Server.AddOns.AssemblyAddOnHashes.MetadataPrefix + "oadm.short", "abcdef")]

namespace Oadm.Server.Tests.AddOns;

/// <summary>
/// The add-on installer (docs/spec/add-ons.md "Server"): only the genuine ZIP installs, safe unpacking, swap, load, remove at
/// the next start, every refusal, and the state after an update or a restore. The hash is injected, no real release needed.
/// </summary>
public sealed class AddOnInstallerTests
{
    private const string Id = AddOnCatalog.TextRecognitionId;
    private const string Name = "Text recognition";

    private static async Task<AddOnRig> StartedAsync(string version = AddOnRig.Version)
    {
        var rig = new AddOnRig(version);
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        return rig;
    }

    /// <summary>A server after an update to <see cref="AddOnRig.Version"/>: the folder and list of 4.10.0.</summary>
    private static async Task<AddOnRig> UpdatedAsync()
    {
        var rig = new AddOnRig();
        rig.PlaceFolder(version: "4.10.0");
        rig.List.Items = [new InstalledAddOn(Id, "4.10.0")];
        rig.Activation.Choices.Add(Id); // the administrator had chosen on or off
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        return rig;
    }

    [Fact]
    public async Task TheGenuineZipIsCheckedSwappedInAndLoadedOff()
    {
        using var rig = await StartedAsync();
        var progress = new ProgressLog();

        var status = await rig.InstallAsync(progress: progress);

        Assert.Equal((AddOnState.Installed, AddOnRig.Version), (status.State, status.Version));
        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Contains(rig.Loader.Packages, p => p.Manifest.Id == Id && p.Directory == Path.GetFullPath(rig.Folder));
        Assert.Equal(["off " + Id], rig.Activation.Calls);
        Assert.Equal([Id], rig.Added);
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);

        // Steps in order, the upload with its percent.
        Assert.Equal([AddOnStep.Upload, AddOnStep.Check, AddOnStep.Install], progress.Items.Select(p => p.Step).Distinct());
        Assert.Contains(new AddOnProgress(AddOnStep.Upload, 100), progress.Items);
        Assert.Equal(new AddOnProgress(AddOnStep.Install, 100), progress.Items[^1]);

        // Only the add-on's folder is left: no upload, no staging folder.
        Assert.All(rig.Leftovers(), path => Assert.True(path == Id || path.StartsWith(Id + "/", StringComparison.Ordinal) || path == AddOnFolders.UploadFolderName, path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(rig.Root, AddOnFolders.UploadFolderName)));
        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json")));
        Assert.Equal(AddOnState.Installed, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AZipWithOneTopFolderUnpacksFromThatFolder()
    {
        using var rig = await StartedAsync();
        var zip = AddOnRig.Zip(topFolder: "oadm.ocr");
        rig.Hashes.Allow(zip);

        await rig.InstallAsync(zip);

        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json")));
        Assert.True(rig.Registry.HasPackage(Id));
    }

    [Fact]
    public async Task ARenamedGenuineZipInstalls()
    {
        using var rig = await StartedAsync();

        var status = await rig.InstallAsync(fileName: "oadm-addon-oadm.ocr-4.11.0-linux-x64 (1).zip");

        Assert.Equal(AddOnState.Installed, status.State);
    }

    [Fact]
    public async Task APreReleaseInstallsItsOwnZip()
    {
        using var rig = await StartedAsync("4.11.0-rc.1");

        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        var status = await rig.InstallAsync();

        Assert.True(row.CanInstall);
        Assert.Null(row.Message);
        Assert.Equal((AddOnState.Installed, "4.11.0-rc.1"), (status.State, status.Version));
    }

    // ----- Refusals: nothing happens -----

    [Fact]
    public async Task AWrongHashIsRefusedBeforeAnythingIsUnpacked()
    {
        using var rig = await StartedAsync();

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(AddOnRig.Zip(extraBytes: 10)));

        Assert.Equal(AddOnTexts.WrongFile, failed.Message);
        AssertNothingInstalled(rig);
        Assert.Equal(AddOnState.NotInstalled, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AHashInCapitalsIsTheSameHash()
    {
        using var rig = await StartedAsync();
        rig.Hashes.Clear();
        var upper = new AddOnInstaller(
            new AddOnInstallerOptions(rig.DataDirectory, AddOnRig.Version, AddOnRig.Runtime),
            new FixedHash(Convert.ToHexString(SHA256.HashData(rig.GenuineZip))),
            rig.List,
            rig.Activation,
            rig.Loader,
            rig.Gate,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AddOnInstaller>.Instance,
            rig.Time,
            rig.Disk);
        using (upper)
        {
            await upper.LoadInstalledAsync(CancellationToken.None);
            Assert.Equal(AddOnState.Installed, (await upper.InstallFileAsync(rig.UploadOf(rig.GenuineZip), new ProgressLog(), CancellationToken.None)).State);
        }
    }

    [Fact]
    public async Task AWrongHashLeavesThePreviousState()
    {
        using var rig = await UpdatedAsync();
        var before = rig.Leftovers();

        await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(AddOnRig.Zip(extraBytes: 10)));

        Assert.Equal(before, rig.Leftovers().Where(p => p != AddOnFolders.UploadFolderName));
        Assert.Equal([new InstalledAddOn(Id, "4.10.0")], rig.List.Items);
        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.Equal((AddOnState.Failed, AddOnTexts.InstallAgain), (row.State, row.Message));
        Assert.Equal("4.10.0", AddOnFolders.ReadVersion(rig.Folder));
    }

    [Fact]
    public async Task ABuildWithoutTheHashRefusesAndShowsNoLink()
    {
        using var rig = await StartedAsync("0.1.0-dev");
        rig.Hashes.Clear();

        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.InstallAsync());

        Assert.Equal((false, AddOnTexts.NoHash, null), (row.CanInstall, row.Message, row.DownloadUrl));
        Assert.Equal(AddOnTexts.NoHash, refused.Message);
        AssertNothingInstalled(rig);
    }

    [Theory]
    [InlineData("oadm-addon-oadm.other-4.11.0-linux-x64.zip", "wrong plugin")]
    [InlineData("oadm-addon-oadm.ocr-4.10.0-linux-x64.zip", "wrong version")]
    [InlineData("oadm-addon-oadm.ocr-4.11.0-rc.1-linux-x64.zip", "wrong version")]
    [InlineData("oadm-addon-oadm.ocr-4.11.0-win-x64.zip", "wrong system")]
    public async Task AFileNamedForAnotherPluginVersionOrSystemIsRefusedBeforeItIsSent(string fileName, string reason)
    {
        using var rig = await StartedAsync();
        var read = 0;

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallFileAsync(
            new AddOnUpload(Id, fileName, rig.GenuineZip.Length, Counting(rig.GenuineZip, () => read++)), new ProgressLog(), CancellationToken.None));

        Assert.Equal(
            reason switch
            {
                "wrong plugin" => AddOnTexts.WrongPlugin(Name),
                "wrong version" => AddOnTexts.WrongVersion(AddOnRig.Version),
                _ => AddOnTexts.WrongSystem(AddOnRig.Runtime),
            },
            failed.Message);
        Assert.Equal(0, read);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AnotherPluginInTheZipIsRefused()
    {
        using var rig = await StartedAsync();
        var zip = AddOnRig.Zip(id: "oadm.other");
        rig.Hashes.Allow(zip);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(zip));

        Assert.Equal(AddOnTexts.WrongPlugin(Name), failed.Message);
        AssertNothingInstalled(rig);
    }

    [Theory]
    [InlineData("4.10.0")]
    [InlineData("4.11.0-rc.1")]
    [InlineData(null)]
    public async Task AnotherVersionInTheZipIsRefused(string? version)
    {
        using var rig = await StartedAsync();
        var zip = AddOnRig.Zip(version: version);
        rig.Hashes.Allow(zip);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(zip));

        Assert.Equal(AddOnTexts.WrongVersion(AddOnRig.Version), failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AZipWithoutPluginJsonIsRefused()
    {
        using var rig = await StartedAsync();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("notes.txt");
        }

        var bytes = buffer.ToArray();
        rig.Hashes.Allow(bytes);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(bytes));

        Assert.Equal(AddOnTexts.WrongFile, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AnAddOnThatNeedsANewerSdkIsRefused()
    {
        using var rig = await StartedAsync();
        var zip = AddOnRig.Zip(minSdk: "99.0.0");
        rig.Hashes.Allow(zip);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(zip));

        Assert.Equal(AddOnTexts.NeedsNewerServer, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Theory]
    [InlineData("../escaped.dll")]
    [InlineData("models/../../escaped.dll")]
    [InlineData("..\\escaped.dll")]
    [InlineData("/escaped.dll")]
    [InlineData("a:b.dll")]
    [InlineData("C:/escaped.dll")]
    [InlineData("plugin.json")] // a file twice
    public async Task AFileOutsideTheFolderOrTwiceIsRefused(string extraEntry)
    {
        using var rig = await StartedAsync();
        var zip = AddOnRig.Zip(extraEntry: extraEntry);
        rig.Hashes.Allow(zip);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(zip));

        Assert.Equal(AddOnTexts.Damaged, failed.Message);
        AssertNothingInstalled(rig);
        Assert.False(File.Exists(Path.Combine(rig.DataDirectory, "escaped.dll")));
        Assert.False(File.Exists(Path.Combine(rig.Root, "escaped.dll")));
    }

    [Fact]
    public async Task AZipThatUnpacksToMoreThanTheLimitIsRefused()
    {
        using var rig = new AddOnRig();
        rig.Limits = o => o with { MaxUnpackedBytes = 100_000 };
        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        var zip = AddOnRig.Zip(extraBytes: 200_000);
        rig.Hashes.Allow(zip);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(zip));

        Assert.Equal(AddOnTexts.Damaged, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AZipWithMoreFilesThanTheLimitIsRefused()
    {
        using var rig = new AddOnRig();
        rig.Limits = o => o with { MaxEntries = 3 };
        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync());

        Assert.Equal(AddOnTexts.Damaged, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AFileOverTheSizeLimitIsRefusedBeforeItIsSent()
    {
        using var rig = await StartedAsync();
        var read = 0;

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallFileAsync(
            new AddOnUpload(Id, "x.zip", AddOnFolders.MaxZipBytes + 1, Counting(rig.GenuineZip, () => read++)), new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.WrongFile, failed.Message);
        Assert.Equal(0, read);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task MoreBytesThanAnnouncedAreRefusedAndFewerSayTheUploadStopped()
    {
        using var rig = await StartedAsync();

        var more = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallFileAsync(rig.UploadOf(rig.GenuineZip, size: 10), new ProgressLog(), CancellationToken.None));
        var fewer = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallFileAsync(rig.UploadOf(rig.GenuineZip, size: rig.GenuineZip.Length + 1), new ProgressLog(), CancellationToken.None));

        Assert.Equal((AddOnTexts.WrongFile, AddOnTexts.UploadStopped), (more.Message, fewer.Message));
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task TooLittleFreeSpaceIsRefusedBeforeItIsSent()
    {
        using var rig = await StartedAsync();
        rig.Disk.Free = rig.GenuineZip.Length * 2L;

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync());

        Assert.Equal(AddOnTexts.NotEnoughSpace, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AClientThatStandsStillEndsTheUpload()
    {
        using var rig = await StartedAsync();
        var run = rig.Installer.InstallFileAsync(new AddOnUpload(Id, "x.zip", 1000, Stalled()), new ProgressLog(), CancellationToken.None);

        await Eventually.TrueAsync(() =>
        {
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            return run.IsCompleted;
        }, "the upload ended");

        Assert.Equal(AddOnTexts.UploadStopped, (await Assert.ThrowsAsync<AddOnException>(() => run)).Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task ACancelledUploadLeavesNothingAndTheNextInstallRuns()
    {
        using var rig = await StartedAsync();
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = rig.Installer.InstallFileAsync(new AddOnUpload(Id, "x.zip", 1000, Stalled(started)), new ProgressLog(), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        AssertNothingInstalled(rig);
        Assert.Equal(AddOnState.Installed, (await rig.InstallAsync()).State);
    }

    [Fact]
    public async Task AnInstallPastItsUploadRunsToItsEndWhenTheCallerGoes()
    {
        using var rig = await StartedAsync();
        using var cancel = new CancellationTokenSource();

        // The caller goes in the moment the last byte is in.
        async IAsyncEnumerable<ReadOnlyMemory<byte>> ThenGone()
        {
            await foreach (var chunk in AddOnRig.ChunksOf(rig.GenuineZip))
            {
                yield return chunk;
            }

            await cancel.CancelAsync();
        }

        var status = await rig.Installer.InstallFileAsync(rig.UploadOf(rig.GenuineZip) with { Data = ThenGone() }, new ProgressLog(), cancel.Token);

        Assert.Equal(AddOnState.Installed, status.State);
        Assert.True(rig.Registry.HasPackage(Id));
    }

    [Fact]
    public async Task AFolderTheLoaderRefusesBringsThePreviousFolderBack()
    {
        using var rig = await UpdatedAsync();
        var zip = AddOnRig.Zip(brokenServerPart: true);
        rig.Hashes.Allow(zip);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.InstallAsync(zip));

        Assert.Equal(AddOnTexts.CouldNotLoad(Name), failed.Message);
        Assert.Equal("4.10.0", AddOnFolders.ReadVersion(rig.Folder));
        Assert.DoesNotContain(rig.Leftovers(), p => p.StartsWith(".new-", StringComparison.Ordinal) || p.StartsWith(".old-", StringComparison.Ordinal));
        Assert.Equal(AddOnTexts.InstallAgain, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).Message);
    }

    [Fact]
    public async Task InstallAndRemoveAreRefusedWhileTheServerUpdates()
    {
        using var rig = await StartedAsync();
        await rig.InstallAsync();
        rig.Gate.Begin("4.12.0");

        var install = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.InstallAsync());
        var remove = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.RemoveAsync(Id, CancellationToken.None));

        Assert.Equal((AddOnTexts.Updating, AddOnTexts.Updating), (install.Message, remove.Message));
        Assert.False(Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).CanInstall);
        Assert.Equal(["off " + Id], rig.Activation.Calls); // the remove changed nothing
    }

    [Fact]
    public async Task OneInstallOrRemoveAtATime()
    {
        using var rig = await StartedAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = rig.Installer.InstallFileAsync(
            new AddOnUpload(Id, "x.zip", rig.GenuineZip.Length, Held(rig.GenuineZip, started, hold)), new ProgressLog(), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var installing = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.InstallAsync());
        var remove = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.RemoveAsync(Id, CancellationToken.None));
        hold.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AddOnState.Installing, installing.State);
        Assert.Equal((AddOnTexts.Busy, AddOnTexts.Busy), (second.Message, remove.Message));
        Assert.True(rig.Registry.HasPackage(Id));
    }

    [Fact]
    public async Task InstallingAnInstalledAddOnIsRefused()
    {
        using var rig = await StartedAsync();
        await rig.InstallAsync();

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.InstallAsync());

        Assert.Equal(AddOnTexts.AlreadyInstalled(Name), again.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Installer.InstallFileAsync(
            new AddOnUpload("oadm.nope", "x.zip", 1, AddOnRig.ChunksOf([1])), new ProgressLog(), CancellationToken.None));
    }

    [Fact]
    public async Task TheOldInstallWithoutAFileSaysToUpdateTheClient()
    {
        using var rig = await StartedAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.OlderClient, refused.Message);
        AssertNothingInstalled(rig);
    }

    // ----- Remove -----

    [Fact]
    public async Task RemoveTurnsOffKeepsItLoadedAndDeletesTheFolderAtTheNextStart()
    {
        using var rig = await StartedAsync();
        await rig.InstallAsync();

        var removed = await rig.Installer.RemoveAsync(Id, CancellationToken.None);

        Assert.Equal((AddOnState.Removed, AddOnTexts.Removed, AddOnRig.Version), (removed.State, removed.Message, removed.Version));
        Assert.Equal(["off " + Id, "off " + Id], rig.Activation.Calls);
        Assert.True(rig.Registry.HasPackage(Id)); // never an unload at run time
        Assert.True(Directory.Exists(rig.Folder));
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version, Removed: true)], rig.List.Items);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Installer.GetClientPartAsync(Id, CancellationToken.None));
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.RemoveAsync(Id, CancellationToken.None));
        Assert.Equal(AddOnTexts.NotInstalled(Name), twice.Message);

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.False(Directory.Exists(rig.Folder));
        Assert.Empty(rig.List.Items);
        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Equal(AddOnState.NotInstalled, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task InstallingAgainBeforeTheRestartKeepsTheLoadedAddOnWithoutAFile()
    {
        using var rig = await StartedAsync();
        await rig.InstallAsync();
        await rig.Installer.RemoveAsync(Id, CancellationToken.None);
        var withFile = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.InstallAsync());

        var again = await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        Assert.Equal(AddOnTexts.Removed, withFile.Message);
        Assert.Equal(AddOnState.Installed, again.State);
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
    }

    [Fact]
    public async Task ABuildWithoutTheHashKeepsARemovedAddOn()
    {
        using var rig = new AddOnRig("0.1.0-dev");
        rig.Hashes.Clear();
        rig.PlaceFolder(version: "0.1.0-dev");
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        await rig.Installer.RemoveAsync(Id, CancellationToken.None);

        var removed = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        var again = await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        Assert.True(removed.CanInstall);
        Assert.Equal(AddOnState.Installed, again.State);
    }

    // ----- Server start -----

    [Fact]
    public async Task AnInstalledAddOnLoadsAtTheNextStart()
    {
        using var rig = await StartedAsync();
        await rig.InstallAsync();

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Empty(rig.Added); // the start reads the registry itself
        Assert.Equal(AddOnState.Installed, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AfterAnUpdateTheAddOnStaysUnloadedAndAsksForTheNewZip()
    {
        using var rig = await UpdatedAsync();

        Assert.False(rig.Registry.HasPackage(Id)); // never the old one
        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.Equal((AddOnState.Failed, AddOnTexts.InstallAgain, true, "4.10.0"), (row.State, row.Message, row.CanInstall, row.Version));
        Assert.Equal(AddOnCatalog.DownloadUrl(Id, AddOnRig.Version, AddOnRig.Runtime), row.DownloadUrl);
        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json"))); // the old files stay until the new ones are in
        Assert.Equal([new InstalledAddOn(Id, "4.10.0")], rig.List.Items);

        // Install again takes the new ZIP, in place of the old folder, and keeps the stored choice.
        var installed = await rig.InstallAsync();

        Assert.Equal((AddOnState.Installed, AddOnRig.Version), (installed.State, installed.Version));
        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Equal(AddOnRig.Version, AddOnFolders.ReadVersion(rig.Folder));
        Assert.Empty(rig.Activation.Calls); // never turned off
        Assert.Equal([Id], rig.Added);
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
    }

    [Fact]
    public async Task AfterARestoreTheListStaysAndTheRowAsksForTheZip()
    {
        using var rig = new AddOnRig();
        rig.List.Items = [new InstalledAddOn(Id, AddOnRig.Version)];

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.Equal((AddOnState.Failed, AddOnTexts.InstallAgain, true), (row.State, row.Message, row.CanInstall));
        Assert.False(rig.Registry.HasPackage(Id));

        Assert.Equal(AddOnState.Installed, (await rig.InstallAsync()).State);
        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json")));
    }

    [Fact]
    public async Task ARemoveAfterAnUpdateDropsTheOldFolderAtOnce()
    {
        using var rig = await UpdatedAsync();

        var removed = await rig.Installer.RemoveAsync(Id, CancellationToken.None);

        Assert.Equal(AddOnState.NotInstalled, removed.State);
        Assert.False(Directory.Exists(rig.Folder));
        Assert.Empty(rig.List.Items);
    }

    [Fact]
    public async Task ARemovedFailedAddOnWhoseFolderStaysIsNeverTakenUpAgain()
    {
        var rig = new AddOnRig { Limits = o => o with { DeleteFolder = _ => false } };
        using (rig)
        {
            rig.Restart();
            rig.PlaceFolder(version: "4.10.0");
            rig.List.Items = [new InstalledAddOn(Id, "4.10.0")];
            await rig.Installer.LoadInstalledAsync(CancellationToken.None);

            var removed = await rig.Installer.RemoveAsync(Id, CancellationToken.None);

            Assert.Equal(AddOnState.NotInstalled, removed.State);
            Assert.True(Directory.Exists(rig.Folder));
            Assert.Equal([new InstalledAddOn(Id, "4.10.0", Removed: true)], rig.List.Items);

            // The next start tries the folder again and loads nothing.
            rig.Restart();
            await rig.Installer.LoadInstalledAsync(CancellationToken.None);

            Assert.False(rig.Registry.HasPackage(Id));
            Assert.Equal(AddOnState.NotInstalled, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
        }
    }

    [Fact]
    public async Task APreReleaseFolderIsNotTheAddOnOfTheRelease()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(version: "4.11.0-rc.1");
        rig.List.Items = [new InstalledAddOn(Id, "4.11.0-rc.1")];

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Equal(AddOnTexts.InstallAgain, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).Message);
    }

    [Fact]
    public async Task AFolderPutInByHandIsInstalledAtTheNextStart()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder();

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
    }

    [Theory]
    [InlineData("4.11.0")]
    [InlineData("0.1.0-dev")]
    public async Task AFolderPutInByHandWorksWithoutTheHashWhenItIsOfThisVersion(string version)
    {
        using var rig = new AddOnRig(version);
        rig.Hashes.Clear();
        rig.PlaceFolder(version: version);

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Equal(AddOnState.Installed, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Theory]
    [InlineData("4.10.0")]
    [InlineData(null)]
    public async Task AFolderPutInByHandOfAnotherVersionIsNotLoaded(string? version)
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(version: version);

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Equal([new InstalledAddOn(Id, version ?? string.Empty)], rig.List.Items);
        Assert.Equal(AddOnTexts.InstallAgain, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).Message);
    }

    [Fact]
    public async Task AFolderReplacedByHandBehindTheListIsReadFromItsOwnVersionFile()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(version: "4.10.0");
        rig.List.Items = [new InstalledAddOn(Id, AddOnRig.Version)];

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id));
    }

    [Fact]
    public async Task AVersionMissingFromTheListIsCorrectedFromTheFolder()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder();
        rig.List.Items = [new InstalledAddOn(Id, "4.10.0")];

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
    }

    [Fact]
    public async Task FoldersPutInByHandAreNotTakenUpWhenTheDataFolderWasFixedAtThisStart()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder();

        await rig.Installer.LoadInstalledAsync(foldersTrusted: false, CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Empty(rig.List.Items);
        Assert.False(Directory.Exists(rig.Folder)); // the next start must not take it up either
        Assert.Equal(AddOnState.NotInstalled, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AListedAddOnIsNotLoadedWhenTheDataFolderWasFixedAtThisStartNorAtTheNext()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder();
        rig.List.Items = [new InstalledAddOn(Id, AddOnRig.Version)];

        await rig.Installer.LoadInstalledAsync(foldersTrusted: false, CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id));
        Assert.False(Directory.Exists(rig.Folder));
        var status = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.Equal((AddOnState.Failed, AddOnTexts.InstallAgain), (status.State, status.Message));

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Equal(AddOnState.Failed, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AFolderThatCannotGoWhenTheDataFolderWasFixedIsNeverLoaded()
    {
        var rig = new AddOnRig { Limits = o => o with { DeleteFolder = _ => false } };
        using (rig)
        {
            rig.Restart();
            rig.PlaceFolder();
            rig.List.Items = [new InstalledAddOn(Id, AddOnRig.Version)];

            await rig.Installer.LoadInstalledAsync(foldersTrusted: false, CancellationToken.None);
            Assert.False(rig.Registry.HasPackage(Id));
            Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version, Removed: true)], rig.List.Items);

            rig.Restart();
            await rig.Installer.LoadInstalledAsync(CancellationToken.None);
            Assert.False(rig.Registry.HasPackage(Id));
        }
    }

    [Fact]
    public async Task AnInstallOrRemoveCountsForTheUpdateOnlyWhileItRuns()
    {
        using var rig = new AddOnRig();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        var zip = AddOnRig.Zip();
        rig.Hashes.Allow(zip);

        await rig.InstallAsync(zip);
        Assert.False(rig.Gate.AddOnWorkRuns);

        await rig.Installer.RemoveAsync(Id, CancellationToken.None);
        Assert.False(rig.Gate.AddOnWorkRuns);

        Assert.True(rig.Gate.TryBeginAddOnWork());
        rig.Gate.EndAddOnWork();
    }

    [Fact]
    public async Task ARemoveAfterAnUpdateStoresOffSoALaterInstallStartsOff()
    {
        using var rig = await UpdatedAsync();

        await rig.Installer.RemoveAsync(Id, CancellationToken.None);

        Assert.Contains("off " + Id, rig.Activation.Calls);
    }

    [Fact]
    public async Task TheLeftoversOfAStoppedInstallGoAtTheNextStart()
    {
        using var rig = new AddOnRig();
        Directory.CreateDirectory(Path.Combine(rig.Root, AddOnFolders.UploadFolderName));
        await File.WriteAllBytesAsync(Path.Combine(rig.Root, AddOnFolders.UploadFolderName, "a.zip"), [1]);
        Directory.CreateDirectory(Path.Combine(rig.Root, ".new-oadm.ocr-1"));

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.Empty(rig.Leftovers());
    }

    [Fact]
    public async Task ABundledPluginWithTheSameIdWins()
    {
        using var rig = new AddOnRig();
        var bundled = Path.Combine(rig.DataDirectory, "bundled", Id);
        rig.PlaceFolder(bundled);
        rig.PlaceFolder();
        rig.List.Items = [new InstalledAddOn(Id, AddOnRig.Version)];
        rig.Restart(loader => loader.LoadPackage(bundled));

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.Equal(Path.GetFullPath(bundled), Assert.Single(rig.Loader.Packages).Directory);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.InstallAsync());
        Assert.Equal(AddOnTexts.PartOfServer(Name), refused.Message);
        Assert.False(Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).CanInstall);
    }

    [Fact]
    public async Task UnknownIdsAndTheirFoldersGoAtTheStart()
    {
        using var rig = new AddOnRig();
        var unknown = Path.Combine(rig.Root, "oadm.gone");
        Directory.CreateDirectory(unknown);
        await File.WriteAllTextAsync(Path.Combine(unknown, "plugin.json"), "{}");
        rig.List.Items = [new InstalledAddOn("oadm.gone", "4.10.0")];

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.False(Directory.Exists(unknown));
        Assert.Empty(rig.List.Items);
    }

    // ----- Client part -----

    [Fact]
    public async Task TheClientPartHoldsTheClientFilesAndTheManagedServerPartWithTheirHashes()
    {
        using var rig = await StartedAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Installer.GetClientPartAsync(Id, CancellationToken.None));
        await rig.InstallAsync();

        var part = await rig.Installer.GetClientPartAsync(Id, CancellationToken.None);

        Assert.Equal((Id, AddOnRig.Version), (part.Id, part.Version));
        // The client part references its server project: the server part goes along, never its dependencies, natives or models.
        Assert.Equal(
            ["plugin.json", "Oadm.Plugins.Ocr.Client.dll", "Oadm.Plugins.Ocr.Server.dll", "Oadm.Plugins.Ocr.Server.pdb", "client/Ocr.Helpers.dll"],
            part.Files.Select(f => f.RelativePath));
        Assert.All(part.Files, f => Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(f.Content.Span)), f.Sha256));
    }

    [Fact]
    public async Task TheClientPartIsReadOncePerVersion()
    {
        using var rig = await StartedAsync();
        await rig.InstallAsync();

        var first = await rig.Installer.GetClientPartAsync(Id, "session-a", CancellationToken.None);
        var second = await rig.Installer.GetClientPartAsync(Id, "session-a", CancellationToken.None);

        Assert.Same(first, second);
    }

    // ----- The list, the file name, the hash in the assembly -----

    [Fact]
    public async Task TheRowLinksTheZipOfThisVersionAndSystem()
    {
        using var rig = await StartedAsync("4.11.0-rc.1");

        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));

        Assert.Equal(
            GitHubReleaseFeed.DownloadPrefix + "v4.11.0-rc.1/oadm-addon-oadm.ocr-4.11.0-rc.1-linux-x64.zip",
            row.DownloadUrl);
    }

    [Theory]
    [InlineData("oadm-addon-oadm.ocr-4.11.0-win-x64.zip", "oadm.ocr", "4.11.0", "win-x64")]
    [InlineData("oadm-addon-oadm.ocr-4.11.0-rc.1-linux-arm64.zip", "oadm.ocr", "4.11.0-rc.1", "linux-arm64")]
    [InlineData("oadm-addon-oadm.foo-bar-4.11.0-osx-x64.zip", "oadm.foo-bar", "4.11.0", "osx-x64")]
    public void AReleaseFileNameIsRead(string fileName, string id, string version, string runtimeId) =>
        Assert.Equal(new AddOnFileName(id, version, runtimeId), AddOnCatalog.ParseReleaseFileName(fileName));

    [Theory]
    [InlineData("oadm-addon-oadm.ocr-4.11.0-win-x64 (1).zip")]
    [InlineData("oadm-addon-oadm.ocr-4.11.0-freebsd-x64.zip")]
    [InlineData("plugin.zip")]
    [InlineData("")]
    public void AnyOtherNameSaysNothing(string fileName) => Assert.Null(AddOnCatalog.ParseReleaseFileName(fileName));

    [Fact]
    public void TheNameIsTheOneTheReleaseUses()
    {
        var name = AddOnCatalog.ReleaseFileName(Id, "4.11.0", "win-x64");

        Assert.Equal(new AddOnFileName(Id, "4.11.0", "win-x64"), AddOnCatalog.ParseReleaseFileName(name));
    }

    [Fact]
    public void TheHashIsReadBackFromTheAssemblyMetadata()
    {
        var hashes = new AssemblyAddOnHashes(typeof(AddOnInstallerTests).Assembly);

        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", hashes.Find("oadm.test"));
        Assert.Equal(hashes.Find("oadm.test"), hashes.Find("OADM.TEST"));
        Assert.Null(hashes.Find("oadm.short")); // no SHA-256: as good as none
        Assert.Null(hashes.Find("oadm.nope"));
    }

    [Fact]
    public void ADevelopmentBuildOfTheServerHasNoHash() => Assert.Null(new AssemblyAddOnHashes().Find(Id));

    private static void AssertNothingInstalled(AddOnRig rig)
    {
        Assert.False(Directory.Exists(rig.Folder));
        Assert.All(rig.Leftovers(), path => Assert.Equal(AddOnFolders.UploadFolderName, path));
        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Empty(rig.List.Items);
        Assert.Empty(rig.Added);
        Assert.Empty(rig.Activation.Calls);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Counting(byte[] data, Action read, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in AddOnRig.ChunksOf(data, ct))
        {
            read();
            yield return chunk;
        }
    }

    /// <summary>One chunk, then nothing until cancelled.</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Stalled(TaskCompletionSource? started = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new byte[10];
        started?.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Held(byte[] data, TaskCompletionSource started, TaskCompletionSource hold, [EnumeratorCancellation] CancellationToken ct = default)
    {
        started.TrySetResult();
        await hold.Task.WaitAsync(ct);
        await foreach (var chunk in AddOnRig.ChunksOf(data, ct))
        {
            yield return chunk;
        }
    }

    private sealed class FixedHash(string hash) : IAddOnHashes
    {
        public string? Find(string id) => hash;
    }
}
