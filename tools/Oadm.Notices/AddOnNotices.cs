using System.Text;
using System.Text.Json;

namespace Oadm.Notices;

/// <summary>
/// THIRD-PARTY-NOTICES.txt of one add-on (docs/spec/add-ons.md "Release files", "manage package addons"): the add-on's
/// published folder ships alone, so it lists its own NuGet packages (every <c>*.deps.json</c> in the folder: the server and
/// the client part) and, as bundled components, the license files of its models (<c>Models/*LICENSE*</c>, e.g.
/// <c>Models/PaddleOCR-LICENSE.txt</c>). OADM's own components are in OADM's notices, not here.
/// </summary>
public static class AddOnNotices
{
    /// <summary>The folder of an add-on's models; its license files are the bundled components.</summary>
    public const string ModelsFolder = "Models";

    /// <summary>
    /// The NuGet packages the add-on ships outside its deps.json (a native library copied by hand, e.g. DirectML.dll):
    /// "manage package addons" writes the <c>OadmNoticePackage</c> items of the add-on's projects into this file (a JSON
    /// array of the answers of <c>dotnet msbuild -getItem:OadmNoticePackage</c>, one per project). Never shipped: the
    /// notices run deletes it.
    /// </summary>
    public const string ExtraFileName = "notices-extra.json";

    /// <summary>The notices of the add-on in <paramref name="folder"/> (its plugin.json gives id and name).</summary>
    public static NoticesResult Build(string folder, string version, string? rid, Func<string, string, PackageInfo> readPackage, LicenseTemplates templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var (id, name) = ReadManifest(folder);
        var uses = Directory.GetFiles(folder, "*.deps.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .SelectMany(deps => DepsFile.Read(deps, "plugin " + id))
            .Concat(ReadExtra(Path.Combine(folder, ExtraFileName), "plugin " + id))
            .ToList();
        var options = new NoticesOptions(
            version,
            rid,
            Bundled(folder),
            ["Apache-2.0"],
            Contents: $"the optional plugin {name} ({id})",
            BundledSummary: "models")
        {
            OwnLicensePlugins = File.Exists(Path.Combine(folder, "LICENSE")) ? [id] : [],
        };
        return NoticesWriter.Build(options, uses, readPackage, templates);
    }

    /// <summary>
    /// The packages of <see cref="ExtraFileName"/> (<c>Identity</c> and <c>Version</c> of each <c>OadmNoticePackage</c> item);
    /// none when the file is missing. An item without a version: <see cref="InvalidDataException"/> (a notice must name it).
    /// </summary>
    public static IReadOnlyList<PackageUse> ReadExtra(string path, string usedBy)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var answers = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var result = new List<PackageUse>();
        foreach (var answer in answers)
        {
            if (!answer.TryGetProperty("Items", out var items) || !items.TryGetProperty("OadmNoticePackage", out var packages))
            {
                continue;
            }

            foreach (var package in packages.EnumerateArray())
            {
                var id = package.TryGetProperty("Identity", out var i) ? i.GetString() : null;
                var version = package.TryGetProperty("Version", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidDataException($"{path}: OadmNoticePackage '{id}' needs Include and Version");
                }

                result.Add(new PackageUse(id.Trim(), version.Trim(), false, usedBy));
            }
        }

        return result;
    }

    /// <summary>The license files of the models, each with its path in the add-on folder; one line when there are none.</summary>
    public static string Bundled(string folder)
    {
        var models = Directory.Exists(folder)
            ? Directory.GetDirectories(folder).FirstOrDefault(d => string.Equals(Path.GetFileName(d), ModelsFolder, StringComparison.OrdinalIgnoreCase))
            : null;
        var files = models is null
            ? []
            : Directory.GetFiles(models, "*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).Contains("LICENSE", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToList();
        if (files.Count == 0)
        {
            return "No bundled components besides the NuGet packages.\n";
        }

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            sb.Append("=== ").Append(relative).Append(" ===\n\n").Append(PackageReader.Normalize(File.ReadAllText(file))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Id and display name from plugin.json; the folder name when it has none.</summary>
    private static (string Id, string Name) ReadManifest(string folder)
    {
        var fallback = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        var path = Path.Combine(folder, "plugin.json");
        if (!File.Exists(path))
        {
            return (fallback, fallback);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var id = doc.RootElement.TryGetProperty("id", out var i) && i.GetString() is { Length: > 0 } s ? s : fallback;
        var name = doc.RootElement.TryGetProperty("displayName", out var n) && n.GetString() is { Length: > 0 } d ? d : id;
        return (id, name);
    }
}
