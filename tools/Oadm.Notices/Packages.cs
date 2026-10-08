using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Oadm.Notices;

/// <summary>A NuGet package (or .NET runtime pack) that a published part uses.</summary>
/// <param name="Id">Package id as written in the deps.json ("Avalonia").</param>
/// <param name="Version">Package version.</param>
/// <param name="IsRuntimePack">True for the .NET runtime packs of a self-contained publish.</param>
/// <param name="UsedBy">"server", "client" or "plugin oadm.ntp-server".</param>
public sealed record PackageUse(string Id, string Version, bool IsRuntimePack, string UsedBy);

/// <summary>Reads the packages of a <c>*.deps.json</c> (the dependency list the SDK writes for a publish).</summary>
public static class DepsFile
{
    private const string RuntimePackPrefix = "runtimepack.";

    /// <summary>
    /// Every library of type <c>package</c> and <c>runtimepack</c>; projects and plain references are OADM's own or come
    /// from a package listed elsewhere. Packages without runtime files stay in the list (a meta package costs one line,
    /// a missed license costs more).
    /// </summary>
    public static IReadOnlyList<PackageUse> Read(string path, string usedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var result = new List<PackageUse>();
        if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return result;
        }

        foreach (var library in libraries.EnumerateObject())
        {
            var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
            var slash = library.Name.LastIndexOf('/');
            if (slash <= 0)
            {
                continue;
            }

            var id = library.Name[..slash];
            var version = library.Name[(slash + 1)..];
            if (type == "package")
            {
                result.Add(new PackageUse(id, version, false, usedBy));
            }
            else if (type == "runtimepack")
            {
                result.Add(new PackageUse(id.StartsWith(RuntimePackPrefix, StringComparison.Ordinal) ? id[RuntimePackPrefix.Length..] : id, version, true, usedBy));
            }
        }

        return result;
    }
}

/// <summary>A license or notice file shipped inside a package.</summary>
/// <param name="Path">Path inside the package ("LICENSE.txt", "legal/licenses/dav1d/COPYING").</param>
/// <param name="Text">Its text, line ends normalized to \n.</param>
public sealed record PackageFile(string Path, string Text);

/// <summary>License facts of one package from its .nuspec and the license files it ships.</summary>
public sealed record PackageInfo(
    string Id,
    string Version,
    bool Found,
    string? LicenseExpression,
    string? LicenseFile,
    string? LicenseUrl,
    string? Copyright,
    string? ProjectUrl,
    string? Authors,
    IReadOnlyList<PackageFile> Files);

/// <summary>Reads packages from the NuGet global packages folder (<c>&lt;root&gt;/&lt;id lower&gt;/&lt;version&gt;/</c>).</summary>
public static class PackageReader
{
    /// <summary>Largest license file read; bigger files are listed by name only.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private static readonly string[] LicenseNamePrefixes =
        ["LICENSE", "LICENCE", "COPYING", "NOTICE", "THIRD-PARTY-NOTICES", "THIRDPARTYNOTICES", "THIRD_PARTY_NOTICES"];

    private static readonly string[] TextExtensions = ["", ".txt", ".md", ".rtf", ".html", ".htm"];

    public static PackageInfo Read(string packagesRoot, string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var dir = Path.Combine(packagesRoot, id.ToLowerInvariant(), version.ToLowerInvariant());
        if (!Directory.Exists(dir))
        {
            return new PackageInfo(id, version, false, null, null, null, null, null, null, []);
        }

        string? expression = null, licenseFile = null, licenseUrl = null, copyright = null, projectUrl = null, authors = null;
        var nuspec = Directory.GetFiles(dir, "*.nuspec").FirstOrDefault();
        if (nuspec is not null)
        {
            var metadata = XDocument.Load(nuspec).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? Value(string name) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var license = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            var licenseType = license?.Attribute("type")?.Value;
            if (license is not null && string.Equals(licenseType, "expression", StringComparison.OrdinalIgnoreCase))
            {
                expression = license.Value.Trim();
            }
            else if (license is not null && string.Equals(licenseType, "file", StringComparison.OrdinalIgnoreCase))
            {
                licenseFile = license.Value.Trim().Replace('\\', '/');
            }

            licenseUrl = Value("licenseUrl");
            copyright = Value("copyright");
            projectUrl = Value("projectUrl");
            authors = Value("authors");
        }

        return new PackageInfo(id, version, true, expression, licenseFile, licenseUrl, copyright, projectUrl, authors, ReadFiles(dir, licenseFile));
    }

    /// <summary>License-like files in the package root, the nuspec's license file and everything below <c>legal/</c>.</summary>
    public static IReadOnlyList<PackageFile> ReadFiles(string packageDir, string? licenseFile = null)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(packageDir))
        {
            var name = Path.GetFileName(file);
            var upper = name.ToUpperInvariant();
            if (LicenseNamePrefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal))
                && TextExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            {
                paths.Add(name);
            }
        }

        if (!string.IsNullOrEmpty(licenseFile) && File.Exists(Path.Combine(packageDir, licenseFile)))
        {
            paths.Add(licenseFile);
        }

        var legal = Path.Combine(packageDir, "legal");
        if (Directory.Exists(legal))
        {
            foreach (var file in Directory.GetFiles(legal, "*", SearchOption.AllDirectories))
            {
                paths.Add(Path.GetRelativePath(packageDir, file).Replace('\\', '/'));
            }
        }

        var result = new List<PackageFile>();
        foreach (var relative in paths)
        {
            var full = Path.Combine(packageDir, relative);
            if (new FileInfo(full).Length > MaxFileBytes)
            {
                continue;
            }

            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue; // binary
            }

            result.Add(new PackageFile(relative, Normalize(Encoding.UTF8.GetString(bytes))));
        }

        return result;
    }

    /// <summary>Text with \n line ends, no BOM, no trailing blank lines.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd() + "\n";
    }
}
