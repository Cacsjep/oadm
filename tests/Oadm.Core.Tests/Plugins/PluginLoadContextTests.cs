using System.Reflection;
using System.Text.Json.Nodes;

using Oadm.Core.Plugins;

namespace Oadm.Core.Tests.Plugins;

public sealed class PluginLoadContextTests
{
    /// <summary>
    /// The installed 1.2.0 Snapshot report failed with "Could not load MigraDoc.DocumentObjectModel": its published
    /// .deps.json listed the PDF package without its DLLs, so the resolver found nothing although the DLL was in the
    /// folder. The context now falls back to "&lt;name&gt;.dll" in the plugin folder.
    /// </summary>
    [Fact]
    public void ADependencyMissingFromTheDepsFileLoadsFromThePluginFolder()
    {
        var repo = PluginPaths.FindRepositoryRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Repository root not found.");
        var deployed = Path.Combine(repo, "artifacts", "plugins", "oadm.snapshot-report");
        Assert.True(File.Exists(Path.Combine(deployed, "MigraDoc.DocumentObjectModel.dll")), "build the solution first: the plugin is not deployed");

        var dir = Path.Combine(Path.GetTempPath(), "oadm-loadcontext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in Directory.GetFiles(deployed))
            {
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            }

            // Like the broken publish: the package entry keeps its dependencies but lists no runtime DLLs.
            var depsPath = Path.Combine(dir, "Oadm.Plugins.SnapshotReport.Server.deps.json");
            var deps = JsonNode.Parse(File.ReadAllText(depsPath))!;
            foreach (var target in deps["targets"]!.AsObject())
            {
                foreach (var library in target.Value!.AsObject().Where(l => l.Key.StartsWith("PDFsharp", StringComparison.Ordinal)))
                {
                    library.Value!.AsObject().Remove("runtime");
                }
            }

            File.WriteAllText(depsPath, deps.ToJsonString());

            var context = new PluginLoadContext(Path.Combine(dir, "Oadm.Plugins.SnapshotReport.Server.dll"), "test");
            try
            {
                var migraDoc = context.LoadFromAssemblyName(new AssemblyName("MigraDoc.DocumentObjectModel"));
                Assert.Equal(dir, Path.GetDirectoryName(migraDoc.Location), ignoreCase: true);
            }
            finally
            {
                context.Unload();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // The unloaded context may still hold a file for a moment; the temp folder is cleaned up by the OS.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
