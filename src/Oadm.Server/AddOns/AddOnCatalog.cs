using System.Text.RegularExpressions;

using Oadm.Contracts.Updates;

namespace Oadm.Server.AddOns;

/// <summary>
/// An add-on plugin this OADM version offers (docs/spec/add-ons.md): not in the installers, installed on demand on the
/// Plugins page from the ZIP of the same version.
/// </summary>
/// <param name="Id">The plugin.json id; also the folder under <c>&lt;datafolder&gt;/addons/</c>.</param>
/// <param name="DisplayName">The name on the Plugins page and in the audit log ("Text recognition").</param>
/// <param name="Description">One line what it adds.</param>
public sealed record AddOnEntry(string Id, string DisplayName, string Description);

/// <summary>What the name of a release file says.</summary>
/// <param name="Id">The add-on's id.</param>
/// <param name="Version">The OADM version.</param>
/// <param name="RuntimeId">The server platform.</param>
public sealed record AddOnFileName(string Id, string Version, string RuntimeId);

/// <summary>
/// The add-ons of this OADM version, kept in the server so the Plugins page lists them without internet
/// (docs/spec/add-ons.md "Release files").
/// </summary>
public static partial class AddOnCatalog
{
    /// <summary>
    /// The oldest OADM version whose add-ons still run on this server (docs/spec/add-ons.md rule 3). Raise it in the release that
    /// breaks them, for example a binary change of the SDK or a theme key they use: older ones then ask to be installed again.
    /// </summary>
    public const string OldestRunningVersion = "4.10.0";

    /// <summary>The id of Text recognition (docs/spec/ocr.md).</summary>
    public const string TextRecognitionId = "oadm.ocr";

    /// <summary>The id of Vision Center (docs/spec/vision-center.md).</summary>
    public const string VisionCenterId = "oadm.vision";

    /// <summary>The runtime ids an add-on ZIP is built for, one per server platform.</summary>
    public static readonly IReadOnlyList<string> RuntimeIds = ["win-x64", "linux-x64", "linux-arm64", "osx-x64"];

    /// <summary>Every add-on of this version, in the order of the Plugins page.</summary>
    public static readonly IReadOnlyList<AddOnEntry> Entries =
    [
        new(TextRecognitionId, "Text recognition", "Reads text in camera pictures."),
        new(VisionCenterId, "Vision Center", "Finds, tracks and counts objects in camera pictures."),
    ];

    /// <summary>The entry with <paramref name="id"/> (ordinal, ignoring case), or null.</summary>
    public static AddOnEntry? Find(string id) =>
        Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="id"/> is an add-on of this version.</summary>
    public static bool Contains(string id) => Find(id) is not null;

    /// <summary>
    /// The release file of an add-on for one platform: <c>oadm-addon-&lt;id&gt;-&lt;version&gt;-&lt;rid&gt;.zip</c>
    /// (docs/spec/add-ons.md "Release files").
    /// </summary>
    public static string ReleaseFileName(string id, string version, string runtimeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        return $"oadm-addon-{id}-{version}-{runtimeId}.zip";
    }

    /// <summary>Where the OADM release of <paramref name="version"/> offers the file of <see cref="ReleaseFileName"/>.</summary>
    public static string DownloadUrl(string id, string version, string runtimeId) =>
        $"{GitHubReleaseFeed.DownloadPrefix}v{version}/{ReleaseFileName(id, version, runtimeId)}";

    /// <summary>What a file's name says when it reads like <see cref="ReleaseFileName"/>; null otherwise (a renamed file).</summary>
    public static AddOnFileName? ParseReleaseFileName(string? fileName)
    {
        if (fileName is null || NameShape().Match(fileName) is not { Success: true } match)
        {
            return null;
        }

        var rest = match.Groups["rest"].Value;
        var runtimeId = RuntimeIds.FirstOrDefault(r => rest.EndsWith("-" + r, StringComparison.Ordinal));
        if (runtimeId is null || IdAndVersion().Match(rest[..^(runtimeId.Length + 1)]) is not { Success: true } parts)
        {
            return null;
        }

        return new AddOnFileName(parts.Groups["id"].Value, parts.Groups["version"].Value, runtimeId);
    }

    [GeneratedRegex(@"^oadm-addon-(?<rest>.+)\.zip$")]
    private static partial Regex NameShape();

    [GeneratedRegex(@"^(?<id>.+?)-(?<version>\d+\.\d+\.\d+.*)$")]
    private static partial Regex IdAndVersion();
}
