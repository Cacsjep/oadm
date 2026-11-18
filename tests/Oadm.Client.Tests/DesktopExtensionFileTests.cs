using Oadm.Client.Settings;

namespace Oadm.Client.Tests;

/// <summary>Where the client finds OADM.mcpb: next to its exe, or in Contents/Resources of the macOS app.</summary>
public sealed class DesktopExtensionFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oadm-mcpb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheExtensionNextToTheClientIsFound()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, DesktopExtensionFile.Name), "zip");

        Assert.Equal(Path.Combine(_root, DesktopExtensionFile.Name), DesktopExtensionFile.Find(_root));
    }

    [Fact]
    public void TheMacOsAppKeepsItInItsResources()
    {
        var macOs = Path.Combine(_root, "OADM.app", "Contents", "MacOS");
        var resources = Path.Combine(_root, "OADM.app", "Contents", "Resources");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, DesktopExtensionFile.Name), "zip");

        Assert.Equal(Path.Combine(resources, DesktopExtensionFile.Name), DesktopExtensionFile.Find(macOs));
    }

    [Fact]
    public void AClientWithoutTheExtensionHasNone()
    {
        Directory.CreateDirectory(_root);

        Assert.Null(DesktopExtensionFile.Find(_root));
    }
}
