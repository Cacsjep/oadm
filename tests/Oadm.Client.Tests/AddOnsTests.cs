using System.Security.Cryptography;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Client.Plugins.AddOns;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts;
using Oadm.Contracts.V1;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// Add-ons in the client (docs/spec/add-ons.md rules 5 and 6): the client part checked against the server's SHA-256 and
/// digest, cached per server and version, loaded after the plugin list is announced, removed files deleted at the next
/// start; the Optional plugins card with Install, its progress, Remove and the refusals.
/// </summary>
public sealed class AddOnsTests : IDisposable
{
    private const string Server = AddOnClientSync.UnpinnedServer;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oadm-addons-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private AddOnClientCache Cache() => new(_root, NullLogger<AddOnClientCache>.Instance);

    private string VersionFolder(string server = Server, string version = FakeOadmApi.FakeServerVersion) =>
        Path.Combine(_root, server, FakeOadmApi.TextRecognitionAddOnId, version);

    private static AddOnFile File(string path, string text, string? sha = null)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
        return new AddOnFile { Path = path, Data = ByteString.CopyFrom(data), Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(data)) };
    }

    private static AddOnClientPart Part(params AddOnFile[] files) => Part(FakeOadmApi.FakeServerVersion, files);

    private static AddOnClientPart Part(string version, params AddOnFile[] files)
    {
        var part = new AddOnClientPart { Id = FakeOadmApi.TextRecognitionAddOnId, Version = version };
        part.Files.AddRange(files);
        part.Digest = AddOnLayout.Digest(files.Select(f => (f.Path, f.Sha256)));
        return part;
    }

    private static async Task InstallAsync(FakeOadmApi api)
    {
        await foreach (AddOnProgress progress in api.InstallAddOnAsync(FakeOadmApi.TextRecognitionAddOnId, CancellationToken.None))
        {
            Assert.True(string.IsNullOrEmpty(progress.Error), progress.Error);
        }
    }

    /// <summary>A loader that records what it was asked to load.</summary>
    private sealed class RecordingLoader : IAddOnPartLoader
    {
        public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Id, string Folder)> Loads { get; } = [];

        public bool HasPackage(string packageId) => Packages.Contains(packageId);

        public Task<int> LoadAddOnAsync(string packageId, string folder)
        {
            Loads.Add((packageId, folder));
            Packages.Add(packageId);
            return Task.FromResult(1);
        }
    }

    [Fact]
    public async Task The_client_part_is_checked_cached_per_server_and_version_and_loaded()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"), File("Oadm.Plugins.Ocr.Server.dll", "server"), File("client/Model.dll", "model"), File("plugin.json", "{}"));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);

        Assert.Equal(1, await sync.SyncAsync(CancellationToken.None));

        string folder = VersionFolder();
        Assert.Equal((FakeOadmApi.TextRecognitionAddOnId, folder), Assert.Single(loader.Loads));
        Assert.Equal("client", System.IO.File.ReadAllText(Path.Combine(folder, "Oadm.Plugins.Ocr.Client.dll")));
        Assert.Equal("model", System.IO.File.ReadAllText(Path.Combine(folder, "client", "Model.dll")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(folder)!, ".download-*"));

        // Loaded once per run: a second sync asks for nothing.
        Assert.Equal(0, await sync.SyncAsync(CancellationToken.None));
        Assert.Equal(1, api.AddOnClientPartCalls);

        // The next start takes the files of this version from the cache of this server.
        var restarted = new RecordingLoader();
        using var next = new AddOnClientSync(api, Cache(), restarted, NullLogger<AddOnClientSync>.Instance);
        Assert.Equal(1, await next.SyncAsync(CancellationToken.None));
        Assert.Equal(folder, Assert.Single(restarted.Loads).Folder);
        Assert.Equal(1, api.AddOnClientPartCalls);

        // Another server (another pinned certificate) has its own cache: fetched from it.
        var other = new RecordingLoader();
        using var otherServer = new AddOnClientSync(api, Cache(), other, NullLogger<AddOnClientSync>.Instance, () => "AB12");
        Assert.Equal(1, await otherServer.SyncAsync(CancellationToken.None));
        Assert.Equal(VersionFolder("ab12"), Assert.Single(other.Loads).Folder);
        Assert.Equal(2, api.AddOnClientPartCalls);
    }

    [Fact]
    public async Task A_cached_part_that_no_longer_matches_the_server_is_fetched_again()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        using (var first = new AddOnClientSync(api, Cache(), new RecordingLoader(), NullLogger<AddOnClientSync>.Instance))
        {
            await first.SyncAsync(CancellationToken.None);
        }

        System.IO.File.WriteAllText(Path.Combine(VersionFolder(), "Oadm.Plugins.Ocr.Client.dll"), "changed on disk");
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);

        Assert.Equal(1, await sync.SyncAsync(CancellationToken.None));

        Assert.Equal(2, api.AddOnClientPartCalls);
        Assert.Equal("client", System.IO.File.ReadAllText(Path.Combine(VersionFolder(), "Oadm.Plugins.Ocr.Client.dll")));
    }

    [Fact]
    public async Task A_newer_version_while_the_older_is_loaded_waits_for_the_next_start()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);
        await sync.SyncAsync(CancellationToken.None);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance, sync);
        var vm = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance, sync: sync);
        await vm.LoadAsync(CancellationToken.None);

        // The server was updated: its add-on is of the new version.
        api.AddOnVersion = "4.12.0";
        api.AddOnClientPart = Part("4.12.0", File("Oadm.Plugins.Ocr.Client.dll", "client 4.12"));
        Assert.Equal(0, await sync.SyncAsync(CancellationToken.None));

        Assert.Single(loader.Loads); // the old one stays loaded
        Assert.True(Directory.Exists(VersionFolder(version: "4.12.0")));
        Assert.Contains(FakeOadmApi.TextRecognitionAddOnId, sync.RestartNeeded);
        Assert.True(Assert.Single(vm.AddOns).RestartNeeded);

        // The next start keeps only the version in use.
        Cache().DeleteRemoved();
        Assert.False(Directory.Exists(VersionFolder()));
        Assert.True(Directory.Exists(VersionFolder(version: "4.12.0")));
    }

    [Fact]
    public async Task A_wrong_hash_is_refused_and_nothing_is_written_or_loaded()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"), File("Other.dll", "changed on the way", sha: new string('0', 64)));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);

        Assert.Equal(0, await sync.SyncAsync(CancellationToken.None));

        Assert.Empty(loader.Loads);
        Assert.False(Directory.Exists(VersionFolder()));
        var damaged = Assert.Throws<AddOnPartException>(() => Cache().Store(Server, FakeOadmApi.TextRecognitionAddOnId, api.AddOnClientPart));
        Assert.Equal("The client part of oadm.ocr is damaged.", damaged.Message);

        // Every file right, but not the part the server lists.
        var part = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        part.Digest = new string('1', 64);
        Assert.Throws<AddOnPartException>(() => Cache().Store(Server, FakeOadmApi.TextRecognitionAddOnId, part));
        Assert.False(Directory.Exists(VersionFolder()));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("lib/../../evil.dll")]
    [InlineData("/etc/evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("lib\\evil.dll")]
    [InlineData("")]
    public void A_file_outside_the_add_on_folder_is_refused(string path)
    {
        var refused = Assert.Throws<AddOnPartException>(() => Cache().Store(Server, FakeOadmApi.TextRecognitionAddOnId, Part(File(path, "x"))));

        Assert.Equal("The client part of oadm.ocr has a file outside its folder.", refused.Message);
        Assert.False(Directory.Exists(VersionFolder()));
    }

    [Theory]
    [InlineData("oadm.other", "1.0.0")]
    [InlineData("oadm.ocr", "../1.0.0")]
    [InlineData("oadm.ocr", "")]
    public void Another_part_or_an_unsafe_version_is_refused(string id, string version)
    {
        var part = new AddOnClientPart { Id = id, Version = version };
        part.Files.Add(File("a.dll", "a"));

        Assert.Throws<AddOnPartException>(() => Cache().Store(Server, FakeOadmApi.TextRecognitionAddOnId, part));
        Assert.Null(Cache().Find(Server, id, version));
    }

    [Fact]
    public async Task A_bundled_copy_wins_and_nothing_is_fetched()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        var loader = new RecordingLoader();
        loader.Packages.Add(FakeOadmApi.TextRecognitionAddOnId); // a plugin folder (a checkout) has it
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);

        Assert.Equal(0, await sync.SyncAsync(CancellationToken.None));
        Assert.Equal(0, api.AddOnClientPartCalls);
        Assert.Empty(loader.Loads);
    }

    [Fact]
    public async Task A_server_without_add_ons_or_an_error_never_stops_the_plugin_list()
    {
        var api = Substitute.For<IOadmApi>();
        api.ListPluginPackagesAsync(Arg.Any<CancellationToken>()).Returns(new PluginPackageList());
        api.ListAddOnsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unimplemented, "")));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance, sync);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        await store.LoadAsync(CancellationToken.None);
        await store.AddOnsSynced;
        api.ListAddOnsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "down")));
        await store.LoadAsync(CancellationToken.None);
        await store.AddOnsSynced;

        Assert.Equal(2, changed);
        Assert.Empty(loader.Loads);
        await api.DidNotReceive().ListAddOnsAsync(Arg.Is(true), Arg.Any<CancellationToken>()); // the sync never waits for GitHub
    }

    [Fact]
    public async Task The_plugin_list_comes_first_and_again_once_the_client_part_is_loaded()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance, sync);
        var seen = new List<bool>();
        store.Changed += (_, _) => seen.Add(loader.HasPackage(FakeOadmApi.TextRecognitionAddOnId)
            && store.Packages.Any(p => p.Id == FakeOadmApi.TextRecognitionAddOnId));

        await store.LoadAsync(CancellationToken.None);
        await store.AddOnsSynced;

        // The list does not wait for the client parts; the shell's page sync (on Changed) finds the page the second time.
        Assert.Equal([false, true], seen);
    }

    [Fact]
    public async Task While_the_server_installs_an_add_on_the_client_asks_again_and_loads_it_when_done()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        api.TextRecognitionState = AddOnState.AddOnInstalling; // the server downloads it after its update
        var loader = new RecordingLoader();
        var time = new FakeTimeProvider();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance, sync, time);

        await store.LoadAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => sync.AnyInstalling && !sync.IsSyncing, "the first sync saw the install");
        Assert.Empty(loader.Loads);

        api.TextRecognitionState = AddOnState.AddOnInstalled;
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        await Eventually.TrueAsync(() =>
        {
            time.Advance(PluginPackageStore.AddOnPollInterval);
            return loader.HasPackage(FakeOadmApi.TextRecognitionAddOnId);
        }, "the client asked again and loaded the part");
        Assert.False(sync.AnyInstalling);
    }

    [Fact]
    public async Task A_removed_add_on_keeps_its_files_until_the_next_start()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        using var sync = new AddOnClientSync(api, Cache(), new RecordingLoader(), NullLogger<AddOnClientSync>.Instance);
        await sync.SyncAsync(CancellationToken.None);
        string folder = Path.GetDirectoryName(VersionFolder())!;

        await api.RemoveAddOnAsync(FakeOadmApi.TextRecognitionAddOnId, CancellationToken.None);
        await sync.SyncAsync(CancellationToken.None);

        Assert.True(System.IO.File.Exists(Path.Combine(VersionFolder(), "Oadm.Plugins.Ocr.Client.dll"))); // may be loaded now
        Assert.True(System.IO.File.Exists(Path.Combine(folder, AddOnClientCache.RemovedMark)));

        // Installed again before the restart: the mark goes, the files stay.
        Cache().Unmark(Server, FakeOadmApi.TextRecognitionAddOnId);
        Cache().DeleteRemoved();
        Assert.True(Directory.Exists(VersionFolder()));

        // Removed, next start: the files go before anything loads.
        Cache().MarkRemoved(Server, FakeOadmApi.TextRecognitionAddOnId);
        Cache().DeleteRemoved();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_client_part_loads_into_the_registry_like_a_plugin_folder()
    {
        string sample = Path.Combine(Oadm.Tests.Shared.TestRepository.TestPlugins, "oadm.sample");
        Assert.True(Directory.Exists(sample), $"The sample client plugin is built into {sample}.");
        string folder = Directory.CreateDirectory(Path.Combine(_root, "oadm.sample-addon", "1.0.0")).FullName;
        foreach (string file in Directory.GetFiles(sample))
        {
            System.IO.File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        var loader = new ClientPluginLoader([], NullLogger<ClientPluginLoader>.Instance);
        Assert.False(loader.HasPackage("oadm.sample-addon"));

        Assert.Equal(1, await loader.LoadAddOnAsync("oadm.sample-addon", folder));

        Assert.Equal("oadm.sample.toolbar", Assert.Single(loader.ToolbarPlugins).Id);
        Assert.True(loader.HasPackage("oadm.sample-addon"));
        Assert.Equal(0, await loader.LoadAddOnAsync("oadm.sample-addon", folder)); // never twice
    }

    [Fact]
    public async Task Install_shows_each_step_and_brings_the_package_into_the_plugin_list()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var gate = new SemaphoreSlim(0);
        var reached = new List<(AddOnStep, int)>();
        api.AddOnInstallGate = async (step, percent) =>
        {
            lock (reached)
            {
                reached.Add((step, percent));
            }

            await gate.WaitAsync();
        };
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance);
        await vm.LoadAsync(CancellationToken.None);
        AddOnRowViewModel row = Assert.Single(vm.AddOns);
        Assert.True(vm.HasAddOns);
        Assert.Equal(("Text recognition", "V0.0.0-fake", true, true), (row.Name, row.MetaText, row.ShowInstall, row.CanInstall));
        Assert.False(row.IsInstalled);
        Assert.Equal(1, api.AddOnReachChecks); // the card asks whether the server reaches GitHub

        Task install = vm.InstallCommand.ExecuteAsync(row);
        Assert.True(row.IsInstalling);
        Assert.False(row.ShowInstall);
        gate.Release(); // Download 0 %
        await Eventually.TrueAsync(() => row.ProgressText == "Download 0 %", "the first step shows");
        gate.Release(); // Download 42 %
        await Eventually.TrueAsync(() => row.ProgressText == "Download 42 %", "the download shows its percent");
        Assert.Equal(42, row.Percent);
        gate.Release(3); // Download 100, Check, Install
        await install;

        Assert.True(row.IsInstalled);
        Assert.False(row.IsInstalling);
        Assert.Null(row.Error);
        Assert.True(row.ShowRemove);
        PluginPackageInfo package = Assert.Single(store.Packages, p => p.Id == FakeOadmApi.TextRecognitionAddOnId);
        Assert.False(package.Enabled); // installed off; Turn on is the plugin list's check box
        Assert.Equal("Vision", package.Group);
    }

    [Fact]
    public async Task A_failed_install_says_why_in_its_row_and_offers_install_again()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero) { NextAddOnInstallError = "The download is damaged. Try again." };
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance);
        await vm.LoadAsync(CancellationToken.None);
        AddOnRowViewModel row = Assert.Single(vm.AddOns);

        await vm.InstallCommand.ExecuteAsync(row);

        Assert.Equal("The download is damaged. Try again.", row.Error);
        Assert.True(row.ShowInstall);
        Assert.True(row.CanInstall);
        Assert.False(row.IsInstalling);
        await store.LoadAsync(CancellationToken.None);
        Assert.DoesNotContain(store.Packages, p => p.Id == FakeOadmApi.TextRecognitionAddOnId);
    }

    [Fact]
    public async Task An_add_on_not_loaded_after_an_update_offers_install_and_remove()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero) { TextRecognitionState = AddOnState.AddOnFailed };
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var vm = new AddOnsViewModel(api, new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance), NullLogger<AddOnsViewModel>.Instance, dialogs);
        await vm.LoadAsync(CancellationToken.None);
        AddOnRowViewModel row = Assert.Single(vm.AddOns);

        // The server's sentence names Install; both buttons are there.
        Assert.Equal("Could not download Text recognition V0.0.0-fake. Install it again.", row.Message);
        Assert.Equal((true, true, true), (row.MessageIsError, row.ShowInstall, row.ShowRemove));

        await vm.RemoveCommand.ExecuteAsync(row);

        Assert.Equal(AddOnState.AddOnNotInstalled, row.State);
        Assert.False(row.ShowRemove);
    }

    [Fact]
    public async Task Without_github_install_stays_off_and_the_row_says_why()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero) { AddOnsUnreachable = true };
        var vm = new AddOnsViewModel(api, new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance), NullLogger<AddOnsViewModel>.Instance);
        await vm.LoadAsync(CancellationToken.None);
        AddOnRowViewModel row = Assert.Single(vm.AddOns);

        Assert.Equal(("The server cannot reach github.com.", true, false, false), (row.Message, row.ShowInstall, row.CanInstall, row.MessageIsError));
        Assert.Equal("The server cannot reach github.com.", row.InstallTip);
        await vm.InstallCommand.ExecuteAsync(row);
        Assert.False(row.IsInstalled);
    }

    [Fact]
    public async Task Remove_asks_first_turns_the_add_on_off_and_offers_install_again()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        await api.SetPluginPackageEnabledAsync(FakeOadmApi.TextRecognitionAddOnId, true, CancellationToken.None);
        var dialogs = Substitute.For<IDialogService>();
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance, dialogs);
        await vm.LoadAsync(CancellationToken.None);
        AddOnRowViewModel row = Assert.Single(vm.AddOns);
        Assert.True(row.IsInstalled);

        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await vm.RemoveCommand.ExecuteAsync(row);
        Assert.True(row.IsInstalled);

        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await vm.RemoveCommand.ExecuteAsync(row);

        await dialogs.Received().ConfirmAsync("Remove optional plugin", "Remove Text recognition? It turns off now. Its settings are kept.", "Remove");
        Assert.True(row.IsRemoved);
        Assert.False(row.ShowRemove);
        Assert.True(row.ShowInstall); // the server keeps the loaded one, nothing is downloaded
        Assert.True(row.CanInstall);
        Assert.Equal("Removed. The files go at the next restart of the server.", row.Message);
        Assert.False(Assert.Single(store.Packages, p => p.Id == FakeOadmApi.TextRecognitionAddOnId).Enabled);
    }

    [Fact]
    public async Task Users_who_are_not_administrators_see_only_the_list()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = "tech", Role = UserRole.Operator, RoleName = "Operator" }, "fake");
        var vm = new AddOnsViewModel(api, new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance), NullLogger<AddOnsViewModel>.Instance, session: session);
        await vm.LoadAsync(CancellationToken.None);
        AddOnRowViewModel row = Assert.Single(vm.AddOns);

        Assert.False(vm.IsAdmin);
        Assert.Equal((false, true), (row.ShowInstall, row.ShowNotInstalled));
        await vm.InstallCommand.ExecuteAsync(row);
        Assert.False(row.IsInstalling);
        Assert.Equal(AddOnState.AddOnNotInstalled, (await api.ListAddOnsAsync(checkReach: false, CancellationToken.None)).AddOns[0].State);
    }

    [Fact]
    public async Task An_older_server_hides_the_card_and_the_plugins_page_reads_both_lists()
    {
        var api = Substitute.For<IOadmApi>();
        api.ListPluginPackagesAsync(Arg.Any<CancellationToken>()).Returns(new PluginPackageList());
        api.ListAddOnsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unimplemented, "")));
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var addOns = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance);
        var page = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance, addOns: addOns);

        await page.RefreshAsync();

        Assert.Same(addOns, page.AddOns);
        Assert.False(addOns.HasAddOns);
        Assert.False(addOns.HasError);
        await api.Received(1).ListAddOnsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());

        api.ListAddOnsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "The server is not reachable.")));
        await addOns.LoadAsync(CancellationToken.None);
        Assert.Equal("The optional plugins could not be read: The server is not reachable.", addOns.ErrorText);
    }

    [Fact]
    public void The_version_reads_like_the_release_page()
    {
        Assert.Equal("V4.11.0", OptionalPluginTexts.Meta("4.11.0"));
        Assert.Equal(string.Empty, OptionalPluginTexts.Meta(string.Empty));
    }
}
