namespace Oadm.Core.Plugins.AddOns;

/// <summary>
/// An add-on plugin this OADM version offers (docs/spec/add-ons.md): not in the installers, installed on demand on the
/// Plugins page from the release of the same version.
/// </summary>
/// <param name="Id">The plugin.json id; also the folder under <c>&lt;datafolder&gt;/addons/</c>.</param>
/// <param name="DisplayName">The name on the Plugins page and in the audit log ("Text recognition").</param>
/// <param name="Description">One line what it adds.</param>
public sealed record AddOnEntry(string Id, string DisplayName, string Description)
{
    /// <summary>
    /// The size of the download per server platform (runtime id "win-x64", "linux-x64", "linux-arm64", "osx-x64") in bytes;
    /// a platform not listed shows no size.
    /// </summary>
    public IReadOnlyDictionary<string, long> SizeByRuntime { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>The download size for <paramref name="runtimeId"/>, or null when unknown.</summary>
    public long? SizeFor(string runtimeId) => SizeByRuntime.TryGetValue(runtimeId, out var size) ? size : null;
}

/// <summary>
/// The add-ons of this OADM version, kept in the server so the Plugins page lists them without internet
/// (docs/spec/add-ons.md "Release files").
/// </summary>
public static class AddOnCatalog
{
    /// <summary>The id of Text recognition (docs/spec/ocr.md).</summary>
    public const string TextRecognitionId = "oadm.ocr";

    /// <summary>The runtime ids an add-on ZIP is built for, one per server platform.</summary>
    public static readonly IReadOnlyList<string> RuntimeIds = ["win-x64", "linux-x64", "linux-arm64", "osx-x64"];

    /// <summary>Every add-on of this version, in the order of the Plugins page.</summary>
    public static readonly IReadOnlyList<AddOnEntry> Entries =
    [
        new(TextRecognitionId, "Text recognition", "Reads text in camera pictures for the event rules."),
    ];

    /// <summary>The entry with <paramref name="id"/> (ordinal, ignoring case), or null.</summary>
    public static AddOnEntry? Find(string id) =>
        Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="id"/> is an add-on of this version.</summary>
    public static bool Contains(string id) => Find(id) is not null;

    /// <summary>
    /// The release file of an add-on for one platform: <c>oadm-addon-&lt;id&gt;-&lt;version&gt;-&lt;rid&gt;.zip</c>
    /// (docs/spec/add-ons.md "Release files"), as listed in the release's <c>SHA256SUMS.txt</c>.
    /// </summary>
    public static string ReleaseFileName(string id, string version, string runtimeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        return $"oadm-addon-{id}-{version}-{runtimeId}.zip";
    }
}
