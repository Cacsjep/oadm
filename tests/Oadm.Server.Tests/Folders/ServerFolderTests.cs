using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Folders;
using Oadm.Server.Hosting;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Folders;

/// <summary>
/// The server folder picker's calls (ServerFolderService, <see cref="ServerFolderBrowser"/>): subfolder names only, sorted and
/// capped, the roots, a new folder, refused paths and names, the folder checks of plugins and the Administrator-only access.
/// </summary>
public sealed class ServerFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oadm-folder-tests", Guid.NewGuid().ToString("N"));

    public ServerFolderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test folder left behind in the temp folder harms nothing.
        }
    }

    private string Data => Path.Combine(_root, "data");

    private string Program => Path.Combine(_root, "program");

    private ServerFolderBrowser Browser(IFolderPermissions? permissions = null, int maxEntries = ServerFolderTexts.MaxEntries) =>
        new(Data, Program, permissions, NullLogger<ServerFolderBrowser>.Instance, maxEntries);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task AListingHasTheSubfolderNamesSortedWithoutFilesAndHiddenFolders()
    {
        var parent = Folder("work");
        Folder("work", "beta");
        Folder("work", "Alpha");
        Folder("work", "gamma");
        var hidden = Folder("work", ".hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        await File.WriteAllTextAsync(Path.Combine(parent, "notes.txt"), "not a folder");

        var listing = await Browser().ListAsync(parent + Path.DirectorySeparatorChar, CancellationToken.None);

        Assert.Equal(parent, listing.Path); // normalized: no trailing separator
        Assert.Equal(_root, listing.Parent);
        Assert.Equal(["Alpha", "beta", "gamma"], listing.Folders.Select(f => f.Name));
        Assert.Equal(Path.Combine(parent, "Alpha"), listing.Folders[0].Path);
        Assert.False(listing.More);
        Assert.Null(listing.Problem); // writable, not a system or OADM folder
        Assert.Empty(Directory.GetFiles(parent, ".oadm-write-check-*")); // the write probe is gone
    }

    [Fact]
    public async Task AListingStopsAtItsLimitAndSaysThereAreMore()
    {
        var parent = Folder("many");
        for (var i = 0; i < 7; i++)
        {
            Folder("many", $"cam-{i}");
        }

        var listing = await Browser(maxEntries: 5).ListAsync(parent, CancellationToken.None);

        Assert.Equal(["cam-0", "cam-1", "cam-2", "cam-3", "cam-4"], listing.Folders.Select(f => f.Name));
        Assert.True(listing.More);
        Assert.Equal(2000, ServerFolderTexts.MaxEntries);
    }

    [Fact]
    public async Task NoPathListsTheRootsAndARootCannotBeChosen()
    {
        var browser = Browser();
        var roots = await browser.ListAsync(null, CancellationToken.None);

        Assert.Equal(string.Empty, roots.Path);
        Assert.Null(roots.Parent);
        Assert.Equal(ServerFolderTexts.ChooseFolder, roots.Problem);
        Assert.All(roots.Folders, r => Assert.True(r.IsRoot));
        var tempRoot = Path.GetPathRoot(Path.GetFullPath(_root))!;
        Assert.Contains(roots.Folders, r => string.Equals(r.Path, tempRoot, StringComparison.OrdinalIgnoreCase));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("/", roots.Folders[0].Path);
        }

        Assert.Equal(ServerFolderTexts.WholeDrive, await browser.CheckAsync(tempRoot, CancellationToken.None));
        var top = await browser.ListAsync(tempRoot, CancellationToken.None);
        Assert.Null(top.Parent);
        Assert.Equal(ServerFolderTexts.WholeDrive, top.Problem);
    }

    [Theory]
    [InlineData("relative/folder")]
    [InlineData("..")]
    [InlineData("")]
    public async Task PathsThatAreNotFullAreRefused(string path)
    {
        Assert.Null(ServerFolderBrowser.Normalize(path));
        var problem = await Browser().CheckAsync(path, CancellationToken.None);
        Assert.Equal(path.Length == 0 ? ServerFolderTexts.ChooseFolder : ServerFolderTexts.NotFullPath, problem);
    }

    [Fact]
    public async Task DotDotTricksDevicePathsAndControlCharactersAreRefused()
    {
        var parent = Folder("tricks", "inner");
        string[] refused =
        [
            parent + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "inner",
            parent + "/./x",
            parent + "\nother",
            new string('a', ServerFolderTexts.MaxPathLength + 1),
            @"\\?\C:\Windows",
            @"\\.\PhysicalDrive0",
        ];
        foreach (var path in refused)
        {
            Assert.Null(ServerFolderBrowser.Normalize(path));
            var error = await Assert.ThrowsAsync<ServerFolderException>(() => Browser().ListAsync(path, CancellationToken.None));
            Assert.Equal((ServerFolderRefusal.Invalid, ServerFolderTexts.NotFullPath), (error.Refusal, error.Message));
        }

        var missing = await Assert.ThrowsAsync<ServerFolderException>(() => Browser().ListAsync(Path.Combine(_root, "gone"), CancellationToken.None));
        Assert.Equal((ServerFolderRefusal.NotFound, ServerFolderTexts.NotFound), (missing.Refusal, missing.Message));
        Assert.Equal(ServerFolderTexts.NotFound, await Browser().CheckAsync(Path.Combine(_root, "gone"), CancellationToken.None));
    }

    [Fact]
    public async Task OADMAndSystemFoldersCannotBeChosenAndGetNoNewFolder()
    {
        var data = Folder("data");
        var plugins = Folder("data", "plugins", "oadm.example");
        var addOns = Folder("data", "addons");
        var program = Folder("program", "runtimes");
        var pluginData = Folder("data", "plugin-data", "oadm.plates");
        var browser = Browser();

        Assert.Equal(ServerFolderTexts.OadmFolder, await browser.CheckAsync(data, CancellationToken.None));
        Assert.Equal(ServerFolderTexts.OadmFolder, await browser.CheckAsync(plugins, CancellationToken.None));
        Assert.Equal(ServerFolderTexts.OadmFolder, await browser.CheckAsync(addOns, CancellationToken.None));
        Assert.Equal(ServerFolderTexts.OadmFolder, await browser.CheckAsync(program, CancellationToken.None));
        Assert.Null(await browser.CheckAsync(pluginData, CancellationToken.None)); // a folder below the data folder may hold a plugin's data

        var system = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : "/etc";
        Assert.Equal(ServerFolderTexts.SystemFolder, await browser.CheckAsync(system, CancellationToken.None));
        var systemError = await Assert.ThrowsAsync<ServerFolderException>(() => browser.CreateAsync(system, "Plates", CancellationToken.None));
        Assert.Equal((ServerFolderRefusal.Failed, ServerFolderTexts.SystemFolder), (systemError.Refusal, systemError.Message));
        var dataError = await Assert.ThrowsAsync<ServerFolderException>(() => browser.CreateAsync(data, "Plates", CancellationToken.None));
        Assert.Equal(ServerFolderTexts.OadmFolder, dataError.Message);
        Assert.False(Directory.Exists(Path.Combine(data, "Plates")));
    }

    [Fact]
    public async Task ANewFolderIsMadeInTheListedFolder()
    {
        var parent = Folder("logs");
        var browser = Browser();

        var created = await browser.CreateAsync(parent, "  Plates 2026", CancellationToken.None);

        Assert.Equal(("Plates 2026", Path.Combine(parent, "Plates 2026"), false), (created.Name, created.Path, created.IsRoot));
        Assert.True(Directory.Exists(created.Path));
        Assert.Equal(["Plates 2026"], (await browser.ListAsync(parent, CancellationToken.None)).Folders.Select(f => f.Name));

        var exists = await Assert.ThrowsAsync<ServerFolderException>(() => browser.CreateAsync(parent, "Plates 2026", CancellationToken.None));
        Assert.Equal((ServerFolderRefusal.Exists, ServerFolderTexts.NameExists), (exists.Refusal, exists.Message));
        var noParent = await Assert.ThrowsAsync<ServerFolderException>(() => browser.CreateAsync(Path.Combine(parent, "gone"), "x", CancellationToken.None));
        Assert.Equal(ServerFolderRefusal.NotFound, noParent.Refusal);
    }

    [Theory]
    [InlineData("", ServerFolderTexts.NameEmpty)]
    [InlineData("   ", ServerFolderTexts.NameEmpty)]
    [InlineData("a/b", ServerFolderTexts.NameInvalid)]
    [InlineData("a\\b", ServerFolderTexts.NameInvalid)]
    [InlineData("what?", ServerFolderTexts.NameInvalid)]
    [InlineData("tab\there", ServerFolderTexts.NameInvalid)]
    [InlineData("..", ServerFolderTexts.NameDots)]
    [InlineData(".hidden", ServerFolderTexts.NameDots)]
    [InlineData("ends.", ServerFolderTexts.NameDots)]
    [InlineData("ends ", ServerFolderTexts.NameDots)]
    [InlineData("CON", ServerFolderTexts.NameReserved)]
    [InlineData("com1.txt", ServerFolderTexts.NameReserved)]
    [InlineData("Plates", null)]
    [InlineData("Plates v2.1", null)]
    [InlineData("Console", null)]
    public async Task FolderNamesFollowOneRuleOnEverySystem(string name, string? expected)
    {
        Assert.Equal(expected, ServerFolderTexts.CheckName(name));
        Assert.Equal(ServerFolderTexts.NameTooLong, ServerFolderTexts.CheckName(new string('a', ServerFolderTexts.MaxNameLength + 1)));
        if (expected is not null)
        {
            var error = await Assert.ThrowsAsync<ServerFolderException>(() => Browser().CreateAsync(Folder("names"), name, CancellationToken.None));
            Assert.Equal((ServerFolderRefusal.Invalid, expected), (error.Refusal, error.Message));
        }
    }

    [Fact]
    public async Task AsTheServiceAFolderOthersCanChangeIsRefusedAndANewOneIsMadeAdminOnly()
    {
        var loose = Folder("loose");
        var permissions = new RecordingPermissions { Loose = loose };
        var browser = Browser(permissions);

        Assert.Equal(ServerFolderTexts.OthersCanChange, await browser.CheckAsync(loose, CancellationToken.None));
        var created = await browser.CreateAsync(loose, "Plates", CancellationToken.None);

        Assert.Equal([created.Path], permissions.Fixed);
        Assert.Null(await browser.CheckAsync(created.Path, CancellationToken.None));
    }

    [Fact]
    public async Task OnlyAdministratorsCallItAndANewFolderIsAudited()
    {
        await using var host = await TestServerHost.StartAsync();
        var parent = Folder("grpc");
        var admin = new Proto.ServerFolderService.ServerFolderServiceClient(host.Invoker);

        var created = await admin.CreateAsync(new Proto.CreateServerFolderRequest { Path = parent, Name = "Plates" });
        var listing = await admin.ListAsync(new Proto.ServerFolderRequest { Path = parent });
        var check = await admin.CheckAsync(new Proto.ServerFolderRequest { Path = created.Path });

        Assert.Equal(["Plates"], listing.Folders.Select(f => f.Name));
        Assert.Equal((parent, _root, string.Empty), (listing.Path, listing.Parent, listing.Problem));
        Assert.Equal((created.Path, string.Empty), (check.Path, check.Problem));
        var roots = await admin.ListAsync(new Proto.ServerFolderRequest());
        Assert.NotEmpty(roots.Folders);
        Assert.Equal(ServerFolderTexts.ChooseFolder, roots.Problem);
        var data = await admin.CheckAsync(new Proto.ServerFolderRequest { Path = host.DataDirectory });
        Assert.Equal(ServerFolderTexts.OadmFolder, data.Problem);

        var entries = await host.Audit.ListAsync(new Proto.ListAuditRequest());
        Assert.Single(entries.Entries, e => e.Action == AuditActions.ServerFolderCreated && e.Target == created.Path);
        Assert.DoesNotContain(entries.Entries, e => e.Action == AuditActions.ServerFolderCreated && e.Target != created.Path); // listing is not audited

        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => admin.ListAsync(new Proto.ServerFolderRequest { Path = "relative" }).ResponseAsync)).StatusCode);
        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(() => admin.ListAsync(new Proto.ServerFolderRequest { Path = Path.Combine(parent, "gone") }).ResponseAsync)).StatusCode);
        var exists = await Assert.ThrowsAsync<RpcException>(() => admin.CreateAsync(new Proto.CreateServerFolderRequest { Path = parent, Name = "Plates" }).ResponseAsync);
        Assert.Equal((StatusCode.AlreadyExists, ServerFolderTexts.NameExists), (exists.StatusCode, exists.Status.Detail));
        var name = await Assert.ThrowsAsync<RpcException>(() => admin.CreateAsync(new Proto.CreateServerFolderRequest { Path = parent, Name = "a/b" }).ResponseAsync);
        Assert.Equal((StatusCode.InvalidArgument, ServerFolderTexts.NameInvalid), (name.StatusCode, name.Status.Detail));

        var op = new Proto.ServerFolderService.ServerFolderServiceClient(await host.InvokerForUserAsync("op", UserRole.Operator));
        foreach (var call in new Func<Task>[]
        {
            async () => await op.ListAsync(new Proto.ServerFolderRequest { Path = parent }),
            async () => await op.CreateAsync(new Proto.CreateServerFolderRequest { Path = parent, Name = "Other" }),
            async () => await op.CheckAsync(new Proto.ServerFolderRequest { Path = parent }),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(call)).StatusCode);
        }

        Assert.False(Directory.Exists(Path.Combine(parent, "Other")));
        // Core plugins check a chosen folder with the same rules.
        Assert.Same(host.Get<ServerFolderBrowser>(), host.Get<CorePluginHost>().FolderCheck);
    }

    /// <summary>The installed service's folder permissions: <see cref="Loose"/> can be changed by others until fixed.</summary>
    private sealed class RecordingPermissions : IFolderPermissions
    {
        public string? Loose { get; init; }

        public List<string> Fixed { get; } = [];

        public Task<string?> FindProblemAsync(string path, CancellationToken ct) => FindOwnProblemAsync(path, ct);

        public Task<string?> FindOwnProblemAsync(string path, CancellationToken ct) =>
            Task.FromResult<string?>(string.Equals(path, Loose, StringComparison.Ordinal) ? "Users can write " + path : null);

        public Task FixAsync(string path, bool readableByUsers, CancellationToken ct)
        {
            Assert.False(readableByUsers);
            Fixed.Add(path);
            return Task.CompletedTask;
        }
    }
}
