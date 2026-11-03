using System.Security.Cryptography;

using Oadm.Core.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Server.AddOns;

namespace Oadm.Server.Tests.AddOns;

/// <summary>
/// The add-on installer (docs/spec/add-ons.md "Server"): install, swap, load, remove at the next start, the failures and the
/// fetch after an update, with fake release files (never a real download).
/// </summary>
public sealed class AddOnInstallerTests
{
    private const string Id = AddOnCatalog.TextRecognitionId;
    private static readonly string FileName = AddOnCatalog.ReleaseFileName(Id, AddOnRig.Version, AddOnRig.Runtime);

    private static async Task<AddOnRig> StartedAsync(string version = AddOnRig.Version)
    {
        var rig = new AddOnRig(version);
        rig.Files.Publish(version, AddOnRig.Runtime, AddOnRig.Zip());
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        return rig;
    }

    [Fact]
    public async Task InstallDownloadsChecksSwapsInAndLoadsTheAddOnOff()
    {
        using var rig = await StartedAsync();
        var progress = new ProgressLog();

        var status = await rig.Installer.InstallAsync(Id, progress, CancellationToken.None);

        Assert.Equal((AddOnState.Installed, AddOnRig.Version), (status.State, status.Version));
        Assert.Equal([FileName], rig.Files.Downloads);
        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Contains(rig.Loader.Packages, p => p.Manifest.Id == Id && p.Directory == Path.GetFullPath(rig.Folder));
        Assert.Equal(["off " + Id], rig.Activation.Calls);
        Assert.Equal([Id], rig.Added);
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);

        // Steps in order, each from 0 to 100.
        Assert.Equal([AddOnStep.Download, AddOnStep.Check, AddOnStep.Install], progress.Items.Select(p => p.Step).Distinct());
        Assert.Equal(new AddOnProgress(AddOnStep.Install, 100), progress.Items[^1]);

        // Only the add-on's folder is left: no download, no staging folder.
        Assert.All(rig.Leftovers(), path => Assert.True(path == Id || path.StartsWith(Id + "/", StringComparison.Ordinal) || path == ".download", path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(rig.Root, ".download")));
        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json")));

        var listed = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.Equal(AddOnState.Installed, listed.State);
    }

    [Fact]
    public async Task AZipWithOneTopFolderUnpacksFromThatFolder()
    {
        using var rig = new AddOnRig();
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip(topFolder: "oadm.ocr"));
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json")));
        Assert.True(rig.Registry.HasPackage(Id));
    }

    [Fact]
    public async Task AWrongChecksumLeavesNothingBehind()
    {
        using var rig = await StartedAsync();
        rig.Files.WrongHash = true;

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.Damaged, failed.Message);
        AssertNothingInstalled(rig);
        Assert.Equal(AddOnState.NotInstalled, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AnotherPluginInTheDownloadIsRefused()
    {
        using var rig = new AddOnRig();
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip(id: "oadm.other"));
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.WrongPackage, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AnAddOnThatNeedsANewerSdkIsRefused()
    {
        using var rig = new AddOnRig();
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip(minSdk: "99.0.0"));
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.NeedsNewerServer, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task AFileOutsideTheFolderMakesTheDownloadDamaged()
    {
        using var rig = new AddOnRig();
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip(extraEntry: "../escaped.dll"));
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.Damaged, failed.Message);
        AssertNothingInstalled(rig);
        Assert.False(File.Exists(Path.Combine(rig.DataDirectory, "escaped.dll")));
    }

    [Fact]
    public async Task WithoutNetworkTheRowSaysSoAndInstallFails()
    {
        using var rig = await StartedAsync();
        rig.Files.Reachable = false;

        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.False(row.CanInstall);
        Assert.Equal(AddOnTexts.CannotReach, row.Message);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));
        Assert.Equal(AddOnTexts.DownloadFailed, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task TheReachAnswerIsKeptForAFewMinutes()
    {
        using var rig = await StartedAsync();

        await rig.Installer.ListAsync(CancellationToken.None);
        await rig.Installer.ListAsync(CancellationToken.None);
        Assert.Equal(1, rig.Files.ReachCalls);

        rig.Time.Advance(AddOnInstallerOptions.ReachCacheTime);
        await rig.Installer.ListAsync(CancellationToken.None);
        Assert.Equal(2, rig.Files.ReachCalls);
    }

    [Fact]
    public async Task InstallAndRemoveAreRefusedWhileTheServerUpdates()
    {
        using var rig = await StartedAsync();
        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);
        rig.Gate.Begin("4.12.0");

        var install = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));
        var remove = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.RemoveAsync(Id, CancellationToken.None));

        Assert.Equal((AddOnTexts.Updating, AddOnTexts.Updating), (install.Message, remove.Message));
        Assert.False(Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).CanInstall);
        Assert.Equal(["off " + Id], rig.Activation.Calls); // the remove changed nothing
    }

    [Fact]
    public async Task OneInstallOrRemoveAtATime()
    {
        using var rig = await StartedAsync();
        rig.Files.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);
        await rig.Files.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var installing = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));
        var remove = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.RemoveAsync(Id, CancellationToken.None));
        rig.Files.Hold.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AddOnState.Installing, installing.State);
        Assert.Equal((AddOnTexts.Busy, AddOnTexts.Busy), (second.Message, remove.Message));
        Assert.Single(rig.Files.Downloads);
        Assert.True(rig.Registry.HasPackage(Id));
    }

    [Fact]
    public async Task InstallingAnInstalledAddOnIsRefused()
    {
        using var rig = await StartedAsync();
        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.AlreadyInstalled("Text recognition"), again.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Installer.InstallAsync("oadm.nope", new ProgressLog(), CancellationToken.None));
    }

    [Fact]
    public async Task RemoveTurnsOffKeepsItLoadedAndDeletesTheFolderAtTheNextStart()
    {
        using var rig = await StartedAsync();
        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        var removed = await rig.Installer.RemoveAsync(Id, CancellationToken.None);

        Assert.Equal((AddOnState.Removed, AddOnTexts.Removed, AddOnRig.Version), (removed.State, removed.Message, removed.Version));
        Assert.Equal(["off " + Id, "off " + Id], rig.Activation.Calls);
        Assert.True(rig.Registry.HasPackage(Id)); // never an unload at run time
        Assert.True(Directory.Exists(rig.Folder));
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version, Removed: true)], rig.List.Items);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Installer.GetClientPartAsync(Id, CancellationToken.None));
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.RemoveAsync(Id, CancellationToken.None));
        Assert.Equal(AddOnTexts.NotInstalled("Text recognition"), twice.Message);

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.False(Directory.Exists(rig.Folder));
        Assert.Empty(rig.List.Items);
        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Equal(AddOnState.NotInstalled, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task InstallingAgainBeforeTheRestartKeepsTheLoadedAddOn()
    {
        using var rig = await StartedAsync();
        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);
        await rig.Installer.RemoveAsync(Id, CancellationToken.None);

        var again = await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        Assert.Equal(AddOnState.Installed, again.State);
        Assert.Single(rig.Files.Downloads);
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
    }

    [Fact]
    public async Task AnInstalledAddOnLoadsAtTheNextStartWithoutADownload()
    {
        using var rig = await StartedAsync();
        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Single(rig.Files.Downloads);
        Assert.Empty(rig.Added); // the start reads the registry itself
        Assert.Equal(AddOnState.Installed, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task AFolderPutInByHandIsInstalledAtTheNextStart()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder();

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
        Assert.Empty(rig.Files.Downloads);
    }

    [Fact]
    public async Task TheLeftoversOfAStoppedInstallGoAtTheNextStart()
    {
        using var rig = new AddOnRig();
        Directory.CreateDirectory(Path.Combine(rig.Root, ".download"));
        await File.WriteAllBytesAsync(Path.Combine(rig.Root, ".download", FileName), [1]);
        Directory.CreateDirectory(Path.Combine(rig.Root, ".new-oadm.ocr-1"));

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.Empty(rig.Leftovers());
    }

    [Fact]
    public async Task AfterAnUpdateTheAddOnOfTheNewVersionIsFetchedBeforeItLoads()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(); // the add-on of 4.10.0
        rig.List.Items = [new InstalledAddOn(Id, "4.10.0")];
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip());

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id)); // not the old one
        Assert.Equal(AddOnState.Installing, Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).State);

        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);

        Assert.Equal([FileName], rig.Files.Downloads);
        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
        Assert.Empty(rig.Activation.Calls); // the administrator's choice stays: the registry's follow applies it
        Assert.Equal([Id], rig.Added);
        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.Equal((AddOnState.Installed, AddOnRig.Version), (row.State, row.Version));
    }

    [Fact]
    public async Task AfterARestoreTheMissingFolderIsFetched()
    {
        using var rig = new AddOnRig();
        rig.List.Items = [new InstalledAddOn(Id, AddOnRig.Version)];
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip());

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json")));
    }

    [Fact]
    public async Task AnAddOnThatCannotBeFetchedAfterAnUpdateStaysUnloaded()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(version: "4.10.0");
        rig.List.Items = [new InstalledAddOn(Id, "4.10.0")];
        rig.Files.Reachable = false;
        rig.Activation.Choices.Add(Id); // the administrator had turned it on

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id));
        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        Assert.Equal((AddOnState.Failed, AddOnTexts.NotLoadedAfterUpdate("Text recognition", AddOnRig.Version)), (row.State, row.Message));
        Assert.True(File.Exists(Path.Combine(rig.Folder, "plugin.json"))); // the old files stay until a download works
        Assert.Equal([new InstalledAddOn(Id, "4.10.0")], rig.List.Items);

        // Try again on the Plugins page.
        rig.Files.Reachable = true;
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip());
        rig.Time.Advance(AddOnInstallerOptions.ReachCacheTime);
        var installed = await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);
        Assert.Equal(AddOnState.Installed, installed.State);
        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Empty(rig.Activation.Calls); // Install again keeps the stored choice: never turned off
        Assert.Equal([Id], rig.Added);
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
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));
        Assert.Equal(AddOnTexts.PartOfServer("Text recognition"), refused.Message);
        Assert.False(Assert.Single(await rig.Installer.ListAsync(CancellationToken.None)).CanInstall);
    }

    [Fact]
    public async Task ADevelopmentBuildDownloadsNothing()
    {
        using var rig = await StartedAsync("0.1.0-dev");

        var row = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal((false, AddOnTexts.NoRelease), (row.CanInstall, row.Message));
        Assert.Equal(AddOnTexts.NoRelease, refused.Message);
        Assert.Equal(0, rig.Files.ReachCalls);
    }

    [Fact]
    public async Task TheClientPartHoldsTheClientFilesAndTheManagedServerPartWithTheirHashes()
    {
        using var rig = await StartedAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Installer.GetClientPartAsync(Id, CancellationToken.None));
        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        var part = await rig.Installer.GetClientPartAsync(Id, CancellationToken.None);

        Assert.Equal((Id, AddOnRig.Version), (part.Id, part.Version));
        // The client part references its server project: the server part goes along, never its dependencies, natives or models.
        Assert.Equal(
            ["plugin.json", "Oadm.Plugins.Ocr.Client.dll", "Oadm.Plugins.Ocr.Server.dll", "Oadm.Plugins.Ocr.Server.pdb", "client/Ocr.Helpers.dll"],
            part.Files.Select(f => f.RelativePath));
        Assert.All(part.Files, f => Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(f.Content.Span)), f.Sha256));
    }

    [Fact]
    public async Task TheClientSyncListNeverWaitsForTheReachCheck()
    {
        using var rig = await StartedAsync();
        rig.Files.ReachHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var row = Assert.Single(await rig.Installer.ListAsync(checkReach: false, CancellationToken.None));

        Assert.Equal(0, rig.Files.ReachCalls);
        Assert.Equal(AddOnState.NotInstalled, row.State);
    }

    [Fact]
    public async Task OneReachCheckAtATimeAndTheOthersTakeItsAnswer()
    {
        using var rig = await StartedAsync();
        rig.Files.ReachHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = rig.Installer.ListAsync(CancellationToken.None);
        var second = rig.Installer.ListAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Files.ReachCalls == 1, "the reach check started");
        rig.Files.ReachHold.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, rig.Files.ReachCalls);
        Assert.True(Assert.Single(await second).CanInstall);
    }

    [Fact]
    public async Task APreReleaseFolderIsNotTheAddOnOfTheRelease()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(version: "4.11.0-rc.1");
        rig.List.Items = [new InstalledAddOn(Id, "4.11.0-rc.1")];
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip());

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        Assert.False(rig.Registry.HasPackage(Id));
        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);

        Assert.Equal([FileName], rig.Files.Downloads);
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
    }

    [Fact]
    public async Task AFolderPutInByHandIsRecordedWithTheReleaseOfItsVersionFile()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(version: "4.10.0");
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip());

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.Equal([new InstalledAddOn(Id, "4.10.0")], rig.List.Items);
        Assert.False(rig.Registry.HasPackage(Id)); // not this release: downloaded first
        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);
        Assert.True(rig.Registry.HasPackage(Id));
        Assert.Equal([new InstalledAddOn(Id, AddOnRig.Version)], rig.List.Items);
    }

    [Fact]
    public async Task AFolderWithoutAVersionFileIsDownloadedAgain()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder(version: null);

        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        Assert.Equal([new InstalledAddOn(Id, string.Empty)], rig.List.Items);
        Assert.False(rig.Registry.HasPackage(Id));
    }

    [Fact]
    public async Task ADownloadOfAnotherReleaseIsRefused()
    {
        using var rig = new AddOnRig();
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip(version: "4.10.0"));
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.WrongPackage, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task FoldersPutInByHandAreNotTakenUpWhenTheDataFolderWasFixedAtThisStart()
    {
        using var rig = new AddOnRig();
        rig.PlaceFolder();

        await rig.Installer.LoadInstalledAsync(takeUpFolders: false, CancellationToken.None);

        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Empty(rig.List.Items);
        Assert.Equal(AddOnState.NotInstalled, Assert.Single(await rig.Installer.ListAsync(checkReach: false, CancellationToken.None)).State);
    }

    [Fact]
    public async Task AFetchedAddOnStaysLoadedWhenItCannotBeStored()
    {
        using var rig = new AddOnRig();
        rig.List.Items = [new InstalledAddOn(Id, "4.10.0")];
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip());
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        rig.List.FailWrites = true;

        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);

        Assert.True(rig.Registry.HasPackage(Id));
        var row = Assert.Single(await rig.Installer.ListAsync(checkReach: false, CancellationToken.None));
        Assert.Equal((AddOnState.Installed, AddOnRig.Version), (row.State, row.Version));
    }

    [Fact]
    public async Task ADevelopmentBuildKeepsARemovedAddOnWithoutADownload()
    {
        using var rig = new AddOnRig("0.1.0-dev");
        rig.PlaceFolder();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        await rig.Installer.RemoveAsync(Id, CancellationToken.None);

        var removed = Assert.Single(await rig.Installer.ListAsync(CancellationToken.None));
        var again = await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        Assert.True(removed.CanInstall);
        Assert.Equal(AddOnState.Installed, again.State);
        Assert.Empty(rig.Files.Downloads);
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

    [Theory]
    [InlineData("a:b.dll")]
    [InlineData("plugin.json")]
    public async Task ANameWithAColonOrAFileTwiceMakesTheDownloadDamaged(string extraEntry)
    {
        using var rig = new AddOnRig();
        rig.Files.Publish(AddOnRig.Version, AddOnRig.Runtime, AddOnRig.Zip(extraEntry: extraEntry));
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);

        var failed = await Assert.ThrowsAsync<AddOnException>(() => rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None));

        Assert.Equal(AddOnTexts.Damaged, failed.Message);
        AssertNothingInstalled(rig);
    }

    [Fact]
    public async Task TheClientPartIsReadOncePerVersion()
    {
        using var rig = await StartedAsync();
        await rig.Installer.InstallAsync(Id, new ProgressLog(), CancellationToken.None);

        var first = await rig.Installer.GetClientPartAsync(Id, "session-a", CancellationToken.None);
        var second = await rig.Installer.GetClientPartAsync(Id, "session-a", CancellationToken.None);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task AFetchAfterTheServerStoppedEndsQuietly()
    {
        using var rig = new AddOnRig();
        rig.List.Items = [new InstalledAddOn(Id, "4.10.0")];
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        rig.Installer.Dispose();

        await rig.Installer.FetchAfterUpdateAsync(CancellationToken.None);

        Assert.Empty(rig.Files.Downloads);
    }

    private static void AssertNothingInstalled(AddOnRig rig)
    {
        Assert.False(Directory.Exists(rig.Folder));
        Assert.All(rig.Leftovers(), path => Assert.Equal(".download", path));
        Assert.False(rig.Registry.HasPackage(Id));
        Assert.Empty(rig.List.Items);
        Assert.Empty(rig.Added);
        Assert.Empty(rig.Activation.Calls);
    }
}
