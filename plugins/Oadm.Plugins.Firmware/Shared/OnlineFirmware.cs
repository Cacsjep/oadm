using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.Firmware;

/// <summary>The kind of an AXIS OS track of a model on axis.com.</summary>
public enum OnlineTrackKind
{
    /// <summary>The model's newest major version, not ending on an LTS minor ("AXIS OS 13").</summary>
    Active = 0,

    /// <summary>An LTS or product-specific support track ("LTS 2024 · 11.11", "PSS 9.80").</summary>
    Lts = 1,

    /// <summary>An older major version whose last minor is no LTS ("AXIS OS 10").</summary>
    Old = 2,
}

/// <summary>One version of a track. Size and SHA-256 are known for the newest version of each track only.</summary>
public sealed record OnlineVersion
{
    public required string Version { get; init; }

    /// <summary>Bytes as listed (rounded by the listing), null when not read.</summary>
    public long? Size { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>A track of one model: the versions newest first.</summary>
public sealed record OnlineTrack
{
    public required int Major { get; init; }

    /// <summary>The minor of an LTS or old track; null for the active track (every minor of the major).</summary>
    public int? Minor { get; init; }

    public required OnlineTrackKind Kind { get; init; }

    /// <summary>As on axis.com: "AXIS OS 13", "LTS 2026 · 12.11", "PSS 9.80".</summary>
    public required string Name { get; init; }

    public required IReadOnlyList<OnlineVersion> Versions { get; init; }

    public string Newest => Versions[0].Version;
}

/// <summary>The answer of the lookup for one model of the selection.</summary>
public sealed record OnlineModel
{
    /// <summary>The model as the dialog asked for it (the device's model).</summary>
    public required string Model { get; init; }

    /// <summary>The product folder on axis.com ("P3265-V"); null when the model is not listed.</summary>
    public string? Product { get; init; }

    /// <summary>"MPQT" or "PACS".</summary>
    public string? Source { get; init; }

    /// <summary>A page of this model could not be read.</summary>
    public bool Failed { get; init; }

    /// <summary>The model's newest version (<c>latest/ver.txt</c>, else the version of the <c>latest/</c> file, else the highest).</summary>
    public string? Latest { get; init; }

    /// <summary>Newest track first; empty when not listed.</summary>
    public IReadOnlyList<OnlineTrack> Tracks { get; init; } = [];

    public bool IsListed => Product is not null && Tracks.Count > 0;
}

/// <summary>Query <c>online</c>: the models of the selection.</summary>
public sealed record OnlineLookupRequest
{
    public IReadOnlyList<string> Models { get; init; } = [];
}

/// <summary>Answer of the query <c>online</c>.</summary>
public sealed record OnlineLookupResult
{
    public IReadOnlyList<OnlineModel> Models { get; init; } = [];

    /// <summary>axis.com did not answer (the product lists could not be read).</summary>
    public bool Unreachable { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, FirmwarePayload.JsonOptions);

    public static OnlineLookupResult? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<OnlineLookupResult>(json, FirmwarePayload.JsonOptions);
}

/// <summary>Query <c>onlineVersion</c>: one version of one product folder.</summary>
public sealed record OnlineVersionRequest
{
    public string? Source { get; init; }

    public string? Product { get; init; }

    public string? Version { get; init; }
}

/// <summary>The firmware file of one version on axis.com, as the server read it from the listing.</summary>
public sealed record OnlineFileInfo
{
    public required string Source { get; init; }

    public required string Product { get; init; }

    public required string Version { get; init; }

    /// <summary>The .bin name ("P3265-V_12_11_147.bin", or "P3265-V.bin" of <c>latest/</c>).</summary>
    public required string FileName { get; init; }

    public required string Url { get; init; }

    public required long Size { get; init; }

    public required string Sha256 { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, FirmwarePayload.JsonOptions);

    public static OnlineFileInfo? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) || json.Trim() == "null" ? null : JsonSerializer.Deserialize<OnlineFileInfo>(json, FirmwarePayload.JsonOptions);
}

/// <summary>
/// The rules of Firmware online shared by the dialog and the server (README "Firmware online"): model match, tracks, the
/// three selection rules and the texts.
/// </summary>
public static partial class OnlineFirmware
{
    /// <summary>The site and the two listings.</summary>
    public const string Site = "https://www.axis.com";
    public const string Mpqt = "MPQT";
    public const string Pacs = "PACS";

    /// <summary>The listing of a source ("/ftp/pub_soft/MPQT/").</summary>
    public static string SourcePath(string source) => "/ftp/pub_soft/" + source + "/";

    /// <summary>
    /// The lowest major version counted as firmware OADM installs: Axis firmware 5.x and AXIS OS 6 and later. Older folders
    /// (C1410's "1_97_1", the first firmware of an audio product) are no tracks.
    /// </summary>
    public const int MinMajor = 5;

    public const string SignInText = "Sign in to MyAxis to download from axis.com.";
    public const string UnreachableText = "axis.com could not be reached.";
    public const string FileChosenText = "A file was chosen.";

    /// <summary>
    /// The model as a key: lower case, only a-z and 0-9, without "AXIS ". "AXIS P3265-V" and "p3265v" are one model;
    /// "P1455-LE" never equals "P1455-LE-3" or "P1455-LE_29_mm".
    /// </summary>
    public static string Normalize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var text = model.Trim();
        if (text.StartsWith("AXIS ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..];
        }

        return string.Concat(text.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9'));
    }

    /// <summary>The device model matches the product folder (exact after <see cref="Normalize"/>).</summary>
    public static bool Matches(string? product, string? model)
    {
        var a = Normalize(product);
        return a.Length > 0 && string.Equals(a, Normalize(model), StringComparison.Ordinal);
    }

    /// <summary>A product folder name the server accepts from a payload (letters, digits, "-" and "_").</summary>
    public static bool IsValidProduct(string? product) => product is not null && ProductName().IsMatch(product);

    public static bool IsValidSource(string? source) => source is Mpqt or Pacs;

    /// <summary>
    /// The tracks of a model from its versions: the newest version of each major. The newest major is the active track
    /// (all its versions) unless its last minor is an LTS track; an older major counts only with its last minor, LTS when
    /// that minor is one. Newest track first, versions newest first.
    /// </summary>
    public static IReadOnlyList<OnlineTrack> BuildTracks(IEnumerable<AxisOsVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var byMajor = versions.Where(v => v.Major >= MinMajor).Distinct().GroupBy(v => v.Major).OrderByDescending(g => g.Key).ToList();
        var tracks = new List<OnlineTrack>(byMajor.Count);
        for (var i = 0; i < byMajor.Count; i++)
        {
            var major = byMajor[i].Key;
            var lastMinor = byMajor[i].Max(v => v.Minor);
            var lts = AxisOsUpgradePath.IsLtsTrack(major, lastMinor);
            var active = i == 0 && !lts;
            var members = byMajor[i].Where(v => active || v.Minor == lastMinor).OrderDescending().ToList();
            tracks.Add(new OnlineTrack
            {
                Major = major,
                Minor = active ? null : lastMinor,
                Kind = active ? OnlineTrackKind.Active : lts ? OnlineTrackKind.Lts : OnlineTrackKind.Old,
                Name = active || !lts
                    ? string.Create(CultureInfo.InvariantCulture, $"AXIS OS {major}")
                    : AxisOsUpgradePath.LtsName(major, lastMinor)!,
                Versions = [.. members.Select(v => new OnlineVersion { Version = v.Text })],
            });
        }

        return tracks;
    }

    /// <summary>The track of the model a device on <paramref name="deviceVersion"/> is on: the track of its major; null when none.</summary>
    public static OnlineTrack? TrackOf(OnlineModel model, int? deviceMajor)
    {
        ArgumentNullException.ThrowIfNull(model);
        return deviceMajor is { } major ? model.Tracks.FirstOrDefault(t => t.Major == major) : null;
    }

    /// <summary>
    /// Recommended: the newest version on the devices' own track (11.11 stays 11.11, the active track stays active); a
    /// track axis.com no longer lists for the model: its newest LTS; no LTS either: its newest version.
    /// </summary>
    public static string? Recommended(OnlineModel model, int? deviceMajor)
    {
        ArgumentNullException.ThrowIfNull(model);
        return TrackOf(model, deviceMajor)?.Newest ?? LatestLts(model);
    }

    /// <summary>Latest LTS: the newest LTS track of the model; a model without one: its newest version.</summary>
    public static string? LatestLts(OnlineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Tracks.FirstOrDefault(t => t.Kind == OnlineTrackKind.Lts)?.Newest ?? Newest(model);
    }

    /// <summary>Latest active: the model's active track; a model without one (it ends on an LTS): its newest version.</summary>
    public static string? LatestActive(OnlineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Tracks.FirstOrDefault(t => t.Kind == OnlineTrackKind.Active)?.Newest ?? Newest(model);
    }

    /// <summary>The model's newest version: <see cref="OnlineModel.Latest"/> when listed in a track, else the highest.</summary>
    public static string? Newest(OnlineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Latest is { } latest && model.Tracks.Any(t => t.Versions.Any(v => v.Version == latest)))
        {
            return latest;
        }

        return model.Tracks.SelectMany(t => t.Versions).Select(v => AxisOsVersion.TryParse(v.Version)).OfType<AxisOsVersion>().Max()?.Text;
    }

    /// <summary>The download name of an online version for the decision table ("P3265-V_12_11_147.bin").</summary>
    public static string FileNameOf(string product, string version) => product + "_" + version.Replace('.', '_') + ".bin";

    /// <summary>
    /// The image an online version stands for in the decision table: a file with a known version; the product is matched by
    /// the selection (<see cref="FirmwareCompatibility.ChooseWithOnline"/>), so it is left open here.
    /// </summary>
    public static FirmwareImageInfo ImageOf(FirmwareOnline entry, long size)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new FirmwareImageInfo(FileNameOf(entry.Product, entry.Version), size, null, AxisOsVersion.TryParse(entry.Version), null);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductName();
}
