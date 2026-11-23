using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Contracts.Plugins;
using Oadm.Core.Auth;
using Oadm.Core.Backup;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Server.Backup;
using Oadm.Server.Hosting;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Plugins;

/// <summary>
/// docs/spec/plugin-removal.md: the shipped manifest written by the publish step and checked by <c>--self-check</c>, bundled
/// folders it does not list skipped and deleted (never in the data folder or an app bundle), the state of a formerly shipped
/// id deleted at the start after a restore, and the installers' part.
/// </summary>
public sealed class PluginRemovalTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("oadm-removal-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SampleDirectory => TestRepository.DeployedPlugin(TestRepository.TestPlugins, "oadm.sample", "Oadm.TestPlugins.Sample.Server.dll");

    private static string Read(params string[] parts) => File.ReadAllText(TestRepository.Path(parts)).ReplaceLineEndings("\n");

    private static string Function(string script, string start)
    {
        var at = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{start} not found");
        var end = script.IndexOf("\n}\n", at, StringComparison.Ordinal);
        return script[at..(end < 0 ? script.Length : end)];
    }

    /// <summary>A plugin folder with only a plugin.json of <paramref name="id"/>.</summary>
    private static string Folder(string root, string name, string id)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""{ "id": "{{id}}", "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(folder, "x.dll"), "x");
        return folder;
    }

    private static void Manifest(string root, params string[] ids) =>
        File.WriteAllText(Path.Combine(root, BundledPlugins.FileName), "{ \"plugins\": [" + string.Join(", ", ids.Select(i => '"' + i + '"')) + "] }");

    private static void CopySample(string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(SampleDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    [Fact]
    public void AFolderTheManifestDoesNotListIsSkippedAndDeletedAndALeftoverOfACrashGoes()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "plugins")).FullName;
        Folder(root, "oadm.kept", "oadm.kept");
        Folder(root, "oadm.gone", "oadm.gone");
        Folder(root, "oadm.old" + BundledPlugins.RemovingSuffix, "oadm.old");
        Manifest(root, "oadm.kept");

        var skipped = BundledPlugins.Clean(root, allowDelete: true);

        Assert.Equal(["oadm.gone"], skipped.Select(s => s.Id));
        Assert.All(skipped, s => Assert.Null(s.Folder));
        Assert.Equal(["oadm.kept"], Directory.GetDirectories(root).Select(Path.GetFileName));
        Assert.Empty(BundledPlugins.Clean(root, allowDelete: true)); // a second start finds nothing
    }

    [Fact]
    public void ARootWithoutAManifestLoadsAsBefore()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "plugins")).FullName;
        Folder(root, "oadm.any", "oadm.any");

        Assert.Empty(BundledPlugins.Clean(root, allowDelete: true));
        Assert.True(Directory.Exists(Path.Combine(root, "oadm.any")));
    }

    [Fact]
    public void InsideAMacAppBundleAFolderIsOnlySkipped()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "OADM.app", "Contents", "Resources", "plugins")).FullName;
        var gone = Folder(root, "oadm.gone", "oadm.gone");
        Manifest(root);
        Assert.True(BundledPlugins.IsInsideAppBundle(root));
        Assert.False(BundledPlugins.IsInsideAppBundle(_temp));

        var skipped = BundledPlugins.Clean(root, allowDelete: !BundledPlugins.IsInsideAppBundle(root));

        Assert.Equal(gone, Assert.Single(skipped).Folder);
        Assert.True(Directory.Exists(gone));
    }

    [Fact]
    public void AFolderThatCannotBeRenamedIsOnlySkipped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // an open file blocks the rename only on Windows; elsewhere the app bundle case covers the skip
        }

        var root = Directory.CreateDirectory(Path.Combine(_temp, "plugins")).FullName;
        var gone = Folder(root, "oadm.gone", "oadm.gone");
        Manifest(root);
        using (new FileStream(Path.Combine(gone, "x.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var skipped = BundledPlugins.Clean(root, allowDelete: true);

            Assert.Equal(gone, Assert.Single(skipped).Folder);
            Assert.True(Directory.Exists(gone));
        }
    }

    [Fact]
    public void TheServerSkipsANotShippedFolderAndNeverTouchesTheDataFolder()
    {
        var bundled = Directory.CreateDirectory(Path.Combine(_temp, "app", "plugins")).FullName;
        CopySample(Path.Combine(bundled, "oadm.sample"));
        Manifest(bundled, "oadm.other");
        var installed = Directory.CreateDirectory(Path.Combine(_temp, "data", "plugins")).FullName;
        CopySample(Path.Combine(installed, "oadm.sample"));
        Manifest(installed); // even a manifest there changes nothing

        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);
        try
        {
            var roots = BundledPluginsAtStart.PrepareRoots(loader, [bundled], installed, NullLogger.Instance);
            Assert.Equal([bundled], roots);
            Assert.False(Directory.Exists(Path.Combine(bundled, "oadm.sample")));
            Assert.Empty(BundledPluginsAtStart.PrepareRoots(loader, [installed], installed, NullLogger.Instance));
            Assert.True(Directory.Exists(Path.Combine(installed, "oadm.sample")));

            loader.LoadFromRoots([bundled, installed]);

            Assert.Empty(registry.Errors);
            var package = Assert.Single(loader.Packages);
            Assert.StartsWith(installed, package.Directory, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(BundledPluginsAtStart.BundledIds(registry, [bundled])!); // loaded from the data folder, not shipped
            var known = BundledPluginsAtStart.KnownIds(registry, installed, Path.Combine(_temp, "data", "addons"));
            Assert.Contains("oadm.sample", known);
            Assert.Contains("oadm.est", known);
            Assert.Contains(Oadm.Server.AddOns.AddOnCatalog.TextRecognitionId, known);
        }
        finally
        {
            foreach (var package in loader.Packages)
            {
                package.LoadContext.Unload();
            }
        }
    }

    [Fact]
    public void OnlyAPackageOfTheShippedRootIsBundledNeverOneOfTheDataFolderOrAnAddOn()
    {
        var bundled = Directory.CreateDirectory(Path.Combine(_temp, "app", "plugins")).FullName;
        CopySample(Path.Combine(bundled, "oadm.sample"));
        Manifest(bundled, "oadm.sample");
        var installed = Directory.CreateDirectory(Path.Combine(_temp, "data", "plugins")).FullName;
        var development = Directory.CreateDirectory(Path.Combine(_temp, "repo", "artifacts", "plugins")).FullName;

        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);
        try
        {
            var roots = BundledPluginsAtStart.PrepareRoots(loader, [bundled, installed], installed, NullLogger.Instance);
            loader.IsBundled = BundledPluginsAtStart.BundledRule(roots, development);
            loader.LoadFromRoots([bundled, installed]);

            Assert.True(Assert.Single(registry.CorePlugins).Origin.Bundled);
            var rule = BundledPluginsAtStart.BundledRule(roots, development);
            Assert.True(rule(Path.Combine(bundled, "oadm.sample"), "oadm.sample"));
            Assert.True(rule(Path.Combine(development, "oadm.schedules"), "oadm.schedules")); // a development run
            Assert.False(rule(Path.Combine(installed, "oadm.sample"), "oadm.sample")); // hand-installed
            Assert.False(rule(Path.Combine(_temp, "data", "addons", "oadm.ocr"), "oadm.ocr")); // an optional plugin
            Assert.False(rule(Path.Combine(development, "oadm.ocr"), Oadm.Server.AddOns.AddOnCatalog.TextRecognitionId)); // never an add-on
            Assert.False(rule(Path.Combine(bundled, "oadm.sample", "nested"), "oadm.sample")); // only folders right in the root
            Assert.False(BundledPluginsAtStart.BundledRule([], null)(Path.Combine(bundled, "oadm.sample"), "oadm.sample"));
        }
        finally
        {
            foreach (var package in loader.Packages)
            {
                package.LoadContext.Unload();
            }
        }
    }

    [Fact]
    public void TheManifestMustListExactlyTheFoldersNextToIt()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "plugins")).FullName;
        Folder(root, "oadm.a", "oadm.a");
        Assert.Equal(["bundled.json is missing in " + root + "."], BundledPlugins.ManifestProblems(root));

        Manifest(root, "oadm.a");
        Assert.Empty(BundledPlugins.ManifestProblems(root));

        Manifest(root, "oadm.b");
        Assert.Equal(["bundled.json does not list the plugin folder oadm.a.", "bundled.json lists oadm.b, which has no plugin folder."], BundledPlugins.ManifestProblems(root));
    }

    [Fact]
    public void TheSelfCheckFailsWithoutTheManifest()
    {
        var app = Directory.CreateDirectory(Path.Combine(_temp, "app")).FullName;
        CopySample(Path.Combine(app, "plugins", "oadm.sample"));
        using var output = new StringWriter();
        Assert.Equal(1, SelfCheck.Run(output, app));
        Assert.Contains("bundled.json is missing", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BothScriptsWriteTheManifestIntoTheStageAndBothAppsGetIt()
    {
        var sh = Read("manage.sh");
        var ps1 = Read("manage.ps1");

        Assert.Contains("write_bundled_manifest", Function(sh, "publish_plugins() {"), StringComparison.Ordinal);
        Assert.Contains("\"$PLUGIN_STAGE/bundled.json\"", Function(sh, "write_bundled_manifest() {"), StringComparison.Ordinal);
        Assert.Contains("cp -R \"$PLUGIN_STAGE\"/. \"$1/plugins/\"", Function(sh, "copy_plugins() {"), StringComparison.Ordinal); // the file too
        Assert.Contains("Write-BundledManifest", Function(ps1, "function Publish-Plugins {"), StringComparison.Ordinal);
        Assert.Contains("'bundled.json'", Function(ps1, "function Write-BundledManifest {"), StringComparison.Ordinal);
        Assert.Contains("Join-Path $PluginStage 'bundled.json'", Function(ps1, "function Copy-Plugins("), StringComparison.Ordinal);
        Assert.Contains("--self-check", Read(".github", "workflows", "release.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMacPreinstallScriptsRemoveTheWholeBundledPluginsFolder()
    {
        Assert.Contains("rm -rf \"/Library/Application Support/OADM/server/plugins\"\n", Read("packaging", "macos", "scripts", "preinstall"), StringComparison.Ordinal);
        Assert.Contains("rm -rf /Applications/OADM.app/Contents/Resources/plugins\n", Read("packaging", "macos", "client-scripts", "preinstall"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoredBackupWhoseVersionShippedAGonePluginIsCleanedAtTheNextStartAndTheReportSaysSo()
    {
        const string password = "correct horse";
        var shipped = Directory.CreateDirectory(Path.Combine(_temp, "v1", "plugins")).FullName;
        CopySample(Path.Combine(shipped, "oadm.sample"));
        Manifest(shipped, "oadm.sample");
        var next = Directory.CreateDirectory(Path.Combine(_temp, "v2", "plugins")).FullName;
        Manifest(next);
        void Services(IServiceCollection services)
        {
            services.RemoveAll<BackupArchive>();
            services.AddSingleton(sp => new BackupArchive(sp.GetRequiredService<OadmPaths>()) { Iterations = 1000 });
            services.RemoveAll<IServerRestart>();
            services.AddSingleton<IServerRestart>(new NoRestart());
        }

        var host = await TestServerHost.StartAsync(configureServices: Services, pluginRoots: [shipped]);
        host.KeepDataDirectory = true;
        var dataDirectory = host.DataDirectory;
        try
        {
            Assert.Contains("oadm.sample", await host.Get<ServerSettingsStore>().GetAsync<string[]>(RemovedPlugins.SettingKey, CancellationToken.None) ?? []);
            await host.Get<ServerSettingsStore>().SetJsonAsync(SettingKeys.PluginKey("oadm.sample", "kept"), "true", CancellationToken.None);
            var backup = new Proto.BackupService.BackupServiceClient(host.Invoker);
            byte[] content;
            string name;
            using (var call = backup.Create(new Proto.CreateBackupRequest { Password = password }))
            {
                Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
                name = call.ResponseStream.Current.FileName;
                using var data = new MemoryStream();
                while (await call.ResponseStream.MoveNext(CancellationToken.None))
                {
                    call.ResponseStream.Current.Data.WriteTo(data);
                }

                content = data.ToArray();
            }

            var files = new Proto.FileService.FileServiceClient(host.Invoker);
            string fileId;
            using (var upload = files.Upload())
            {
                await upload.RequestStream.WriteAsync(new Proto.UploadChunk { Header = new Proto.UploadHeader { Name = name, Size = content.Length } });
                await upload.RequestStream.WriteAsync(new Proto.UploadChunk { Data = ByteString.CopyFrom(content) });
                await upload.RequestStream.CompleteAsync();
                fileId = (await upload.ResponseAsync).Id;
            }

            await backup.RestoreAsync(new Proto.RestoreRequest { FileId = fileId, Password = password });
        }
        finally
        {
            await host.DisposeAsync();
        }

        // The next version does not ship oadm.sample: the restored database names it, so its state goes now.
        var restarted = await TestServerHost.StartAsync(configureServices: Services, pluginRoots: [next], dataDirectory: dataDirectory);
        try
        {
            Assert.Null(await restarted.Get<ServerSettingsStore>().GetJsonAsync(SettingKeys.PluginKey("oadm.sample", "kept"), CancellationToken.None));
            var state = await new Proto.BackupService.BackupServiceClient(restarted.Invoker).GetStateAsync(new Proto.Empty());
            Assert.Contains(RemovedPlugins.DeletedText("oadm.sample"), state.LastRestore.Lines);
            Assert.DoesNotContain(state.LastRestore.Lines, l => l.StartsWith("Plugin oadm.sample: not installed", StringComparison.Ordinal));
            var audit = await restarted.Audit.ListAsync(new Proto.ListAuditRequest { Limit = 50 });
            Assert.Contains(audit.Entries, e => e.Action == AuditActions.PluginDataDeleted && e.Target == "oadm.sample");
            Assert.Empty(await restarted.Get<ServerSettingsStore>().GetAsync<string[]>(RemovedPlugins.SettingKey, CancellationToken.None) ?? ["?"]);
        }
        finally
        {
            restarted.KeepDataDirectory = false;
            await restarted.DisposeAsync();
        }
    }

    private sealed class NoRestart : IServerRestart
    {
        public void Request()
        {
        }
    }
}
