using Oadm.Client.Plugins;

namespace Oadm.Client.Settings;

/// <summary>
/// The packaged desktop extension <c>OADM.mcpb</c> (docs/spec/mcp.md): next to the client exe, or in
/// <c>OADM.app/Contents/Resources</c> of the macOS package (codesign refuses a file that is no code in <c>Contents/MacOS</c>).
/// </summary>
public static class DesktopExtensionFile
{
    public const string Name = "OADM.mcpb";

    /// <summary>The extension's path, or null when this client has none.</summary>
    public static string? Find(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        var next = Path.Combine(baseDirectory, Name);
        if (File.Exists(next))
        {
            return next;
        }

        return ClientPluginLoader.AppBundleResources(baseDirectory) is { } resources && File.Exists(Path.Combine(resources, Name))
            ? Path.Combine(resources, Name)
            : null;
    }
}
