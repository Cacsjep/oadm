using System.Reflection;
using System.Runtime.Loader;

using Oadm.Contracts.Plugins;
using Oadm.Server.Hosting;

namespace Oadm.Server.Tests;

/// <summary><c>--self-check</c>: a plugin without one of its assemblies fails the release, as Send email should have in 4.11.0.</summary>
public sealed class SelfCheckTests
{
    private static string BuiltPlugin() =>
        Path.GetDirectoryName(typeof(Oadm.Plugins.Notifications.SendEmailAction).Assembly.Location)!;

    private static string AppWithNotifications()
    {
        var app = Directory.CreateTempSubdirectory("oadm-self-check-").FullName;
        var plugin = Directory.CreateDirectory(Path.Combine(app, "plugins", "oadm.notifications")).FullName;
        foreach (var file in Directory.GetFiles(BuiltPlugin(), "*.dll"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("Oadm.Plugins.Notifications", StringComparison.Ordinal) || name is "MailKit.dll" or "MimeKit.dll" or "BouncyCastle.Cryptography.dll")
            {
                File.Copy(file, Path.Combine(plugin, name));
            }
        }

        return app;
    }

    /// <summary>A loaded library stays locked on Windows until the context is collected: what remains is in the temp folder.</summary>
    private static void Remove(string app)
    {
        try
        {
            Directory.Delete(app, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    [Fact]
    public void APluginWithEverythingPasses()
    {
        var app = AppWithNotifications();
        try
        {
            using var output = new StringWriter();
            Assert.True(SelfCheck.Run(output, app) == 0, output.ToString());
            Assert.Contains("Plugins checked: 1", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Remove(app);
        }
    }

    [Fact]
    public void AMissingLibraryIsNamed()
    {
        // The test's own folder holds MailKit, a published server's does not: "--self-check" on a published server proves the real load.
        static Assembly Load(AssemblyName name) => name.Name == "MailKit" ? throw new FileNotFoundException("gone") : AssemblyLoadContext.Default.LoadFromAssemblyName(name);

        var problems = PluginAssemblyCheck.Problems(typeof(Oadm.Plugins.Notifications.SendEmailAction).Assembly, Load, _ => false);

        Assert.Equal(["MailKit is missing, Oadm.Plugins.Notifications.Server needs it"], problems);
    }

    [Fact]
    public void AnAppWithoutPluginsFails()
    {
        var app = Directory.CreateTempSubdirectory("oadm-self-check-").FullName;
        try
        {
            using var output = new StringWriter();
            Assert.Equal(1, SelfCheck.Run(output, app));
            Assert.Contains("No plugins next to the exe.", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Remove(app);
        }
    }
}
