using System.Security.Cryptography;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Client.Plugins.AddOns;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// Add-ons in the client (docs/spec/add-ons.md rules 5 and 6): the client part checked against the server's SHA-256,
/// cached per server version, loaded before the plugin list is announced, removed files deleted at the next start; the
/// Add-ons card with Install, its progress, Remove and the refusals.
/// </summary>
public sealed class AddOnsTests : IDisposable
{
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

    private static AddOnFile File(string path, string text, string? sha = null)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
        return new AddOnFile { Path = path, Data = ByteString.CopyFrom(data), Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(data)) };
    }

    private static AddOnClientPart Part(params AddOnFile[] files)
    {
        var part = new AddOnClientPart { Id = FakeOadmApi.TextRecognitionAddOnId, Version = FakeOadmApi.FakeServerVersion };
        part.Files.AddRange(files);
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

        public int LoadAddOn(string packageId, string folder)
        {
            Loads.Add((packageId, folder));
            Packages.Add(packageId);
            return 1;
        }
    }

    [Fact]
    public async Task The_client_part_is_checked_cached_per_server_version_and_loaded()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"), File("Oadm.Plugins.Ocr.Server.dll", "server"), File("client/Model.dll", "model"), File("plugin.json", "{}"));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);

        Assert.Equal(1, await sync.SyncAsync(CancellationToken.None));

        string folder = Path.Combine(_root, FakeOadmApi.TextRecognitionAddOnId, FakeOadmApi.FakeServerVersion);
        Assert.Equal((FakeOadmApi.TextRecognitionAddOnId, folder), Assert.Single(loader.Loads));
        Assert.Equal("client", System.IO.File.ReadAllText(Path.Combine(folder, "Oadm.Plugins.Ocr.Client.dll")));
        Assert.Equal("model", System.IO.File.ReadAllText(Path.Combine(folder, "client", "Model.dll")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, FakeOadmApi.TextRecognitionAddOnId), ".download-*"));

        // Loaded once per run: a second sync asks for nothing.
        Assert.Equal(0, await sync.SyncAsync(CancellationToken.None));
        Assert.Equal(1, api.AddOnClientPartCalls);

        // The next start takes the files of this version from the cache.
        var restarted = new RecordingLoader();
        using var next = new AddOnClientSync(api, Cache(), restarted, NullLogger<AddOnClientSync>.Instance);
        Assert.Equal(1, await next.SyncAsync(CancellationToken.None));
        Assert.Equal(folder, Assert.Single(restarted.Loads).Folder);
        Assert.Equal(1, api.AddOnClientPartCalls);
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
        Assert.False(Directory.Exists(Path.Combine(_root, FakeOadmApi.TextRecognitionAddOnId, FakeOadmApi.FakeServerVersion)));
        var damaged = Assert.Throws<AddOnPartException>(() => Cache().Store(FakeOadmApi.TextRecognitionAddOnId, api.AddOnClientPart));
        Assert.Equal("The client part of oadm.ocr is damaged.", damaged.Message);
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
        var refused = Assert.Throws<AddOnPartException>(() => Cache().Store(FakeOadmApi.TextRecognitionAddOnId, Part(File(path, "x"))));

        Assert.Equal("The client part of oadm.ocr has a file outside its folder.", refused.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, FakeOadmApi.TextRecognitionAddOnId, FakeOadmApi.FakeServerVersion)));
    }

    [Theory]
    [InlineData("oadm.other", "1.0.0")]
    [InlineData("oadm.ocr", "../1.0.0")]
    [InlineData("oadm.ocr", "")]
    public void Another_part_or_an_unsafe_version_is_refused(string id, string version)
    {
        var part = new AddOnClientPart { Id = id, Version = version };
        part.Files.Add(File("a.dll", "a"));

        Assert.Throws<AddOnPartException>(() => Cache().Store(FakeOadmApi.TextRecognitionAddOnId, part));
        Assert.Null(Cache().Find(id, version));
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
        api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unimplemented, "")));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance, sync);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        await store.LoadAsync(CancellationToken.None);
        api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "down")));
        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(2, changed);
        Assert.Empty(loader.Loads);
    }

    [Fact]
    public async Task The_plugin_list_is_announced_after_the_client_part_is_loaded()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        var loader = new RecordingLoader();
        using var sync = new AddOnClientSync(api, Cache(), loader, NullLogger<AddOnClientSync>.Instance);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance, sync);
        bool loadedFirst = false;
        store.Changed += (_, _) => loadedFirst = loader.HasPackage(FakeOadmApi.TextRecognitionAddOnId)
            && store.Packages.Any(p => p.Id == FakeOadmApi.TextRecognitionAddOnId);

        await store.LoadAsync(CancellationToken.None);

        Assert.True(loadedFirst); // the shell's page sync (on Changed) finds the add-on's page
    }

    [Fact]
    public async Task A_removed_add_on_keeps_its_files_until_the_next_start()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        await InstallAsync(api);
        api.AddOnClientPart = Part(File("Oadm.Plugins.Ocr.Client.dll", "client"));
        using var sync = new AddOnClientSync(api, Cache(), new RecordingLoader(), NullLogger<AddOnClientSync>.Instance);
        await sync.SyncAsync(CancellationToken.None);
        string folder = Path.Combine(_root, FakeOadmApi.TextRecognitionAddOnId);

        await api.RemoveAddOnAsync(FakeOadmApi.TextRecognitionAddOnId, CancellationToken.None);
        await sync.SyncAsync(CancellationToken.None);

        Assert.True(System.IO.File.Exists(Path.Combine(folder, FakeOadmApi.FakeServerVersion, "Oadm.Plugins.Ocr.Client.dll"))); // may be loaded now
        Assert.True(System.IO.File.Exists(Path.Combine(folder, AddOnClientCache.RemovedMark)));

        // Installed again before the restart: the mark goes, the files stay.
        Cache().Unmark(FakeOadmApi.TextRecognitionAddOnId);
        Cache().DeleteRemoved();
        Assert.True(Directory.Exists(folder));

        // Removed, next start: the files go before anything loads.
        Cache().MarkRemoved(FakeOadmApi.TextRecognitionAddOnId);
        Cache().DeleteRemoved();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_client_part_loads_into_the_registry_like_a_plugin_folder()
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

        Assert.Equal(1, loader.LoadAddOn("oadm.sample-addon", folder));

        Assert.Equal("oadm.sample.toolbar", Assert.Single(loader.ToolbarPlugins).Id);
        Assert.True(loader.HasPackage("oadm.sample-addon"));
        Assert.Equal(0, loader.LoadAddOn("oadm.sample-addon", folder)); // never twice
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
        Assert.Equal(("Text recognition", "V0.0.0-fake · 38 MB", true, true), (row.Name, row.MetaText, row.ShowInstall, row.CanInstall));
        Assert.False(row.IsInstalled);

        Task install = vm.InstallCommand.ExecuteAsync(row);
        Assert.True(row.IsInstalling);
        Assert.False(row.ShowInstall);
        gate.Release(); // Download 0 %
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => row.ProgressText == "Download 0 %", "the first step shows");
        gate.Release(); // Download 42 %
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => row.ProgressText == "Download 42 %", "the download shows its percent");
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
    public async Task Remove_asks_first_turns_the_add_on_off_and_says_when_the_files_go()
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

        await dialogs.Received().ConfirmAsync("Remove add-on", "Remove Text recognition? It turns off now. Its readers and settings are kept.", "Remove");
        Assert.True(row.IsRemoved);
        Assert.False(row.ShowRemove);
        Assert.False(row.ShowInstall);
        Assert.Equal("Removed. The files go at the next restart of the server.", AddOnTexts.Removed);
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
        Assert.Equal(AddOnState.AddOnNotInstalled, (await api.ListAddOnsAsync(CancellationToken.None)).AddOns[0].State);
    }

    [Fact]
    public async Task An_older_server_hides_the_card_and_the_plugins_page_reads_both_lists()
    {
        var api = Substitute.For<IOadmApi>();
        api.ListPluginPackagesAsync(Arg.Any<CancellationToken>()).Returns(new PluginPackageList());
        api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unimplemented, "")));
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var addOns = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance);
        var page = new PluginsViewModel(store, NullLogger<PluginsViewModel>.Instance, addOns: addOns);

        await page.RefreshAsync();

        Assert.Same(addOns, page.AddOns);
        Assert.False(addOns.HasAddOns);
        Assert.False(addOns.HasError);
        await api.Received(1).ListAddOnsAsync(Arg.Any<CancellationToken>());

        api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns<Task<AddOnList>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "The server is not reachable.")));
        await addOns.LoadAsync(CancellationToken.None);
        Assert.Equal("The add-ons could not be read: The server is not reachable.", addOns.ErrorText);
    }

    [Theory]
    [InlineData(38_000_000L, "38 MB")]
    [InlineData(512_000L, "512 KB")]
    [InlineData(1_400_000_000L, "1.4 GB")]
    public void Sizes_read_like_the_release_page(long bytes, string text)
    {
        using var culture = new CultureScope("en-US");
        Assert.Equal(text, AddOnTexts.Size(bytes));
        Assert.Equal("V4.11.0 · " + text, AddOnTexts.Meta("4.11.0", bytes));
        Assert.Equal("V4.11.0", AddOnTexts.Meta("4.11.0", 0));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _before = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _before;
    }
}
