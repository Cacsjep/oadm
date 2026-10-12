using System.Globalization;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeRecordingExport.Recordings;

/// <summary>
/// The Edge storage API requests the plugin sends (all read-only; the recording endpoints allow Viewer and above):
/// <c>record/list.cgi</c>, <c>record/export/capabilities.cgi</c>, <c>properties.cgi</c> and <c>exportrecording.cgi</c>.
/// Verified on an AXIS P3265-V (AXIS OS 12.11, export schema 1.4): the list cuts the start and stop of every recording to
/// the requested range (a recording that still records has an empty stop), <c>startatresultnumber</c> counts from 0,
/// <c>numberofrecordings</c> is the number that matched (the page may hold fewer); properties and export accept the
/// listed times with microseconds and answer a wider range with the recording's own times; the export answers
/// <c>video/mp4</c> or <c>video/x-matroska</c> chunked, with <c>Content-Disposition</c> naming the file
/// "<c>yyyyMMdd_HHmmss_yyyyMMdd_HHmmss.mp4</c>" in the camera's local time; errors of the export API are HTTP 200
/// <c>GeneralError</c> (properties) or HTTP 400 (export).
/// </summary>
public static class EdgeStorageRequests
{
    public const string ListPath = "axis-cgi/record/list.cgi";

    public const string CapabilitiesPath = "axis-cgi/record/export/capabilities.cgi";

    public const string PropertiesPath = "axis-cgi/record/export/properties.cgi";

    public const string ExportPath = "axis-cgi/record/export/exportrecording.cgi";

    /// <summary>The disks (SD card, network share) with their state; asked only when a search found nothing.</summary>
    public const string DisksUri = "axis-cgi/disks/list.cgi?diskid=all";

    /// <summary>Recordings per list request.</summary>
    public const int PageSize = 1000;

    /// <summary>The parameter group that says whether the camera has edge storage and can export.</summary>
    public const string PropertiesGroup = "Properties.LocalStorage";

    public const string Mp4 = "mp4";

    public const string Matroska = "matroska";

    /// <summary>"2026-10-12T18:09:25.743158Z", the camera's own format.</summary>
    public static string Time(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>One page of the recordings that overlap the range, oldest first.</summary>
    public static string ListUri(DateTimeOffset? start, DateTimeOffset? stop, int startAt, int count)
    {
        var uri = string.Create(CultureInfo.InvariantCulture, $"{ListPath}?recordingid=all&sortorder=ascending&startatresultnumber={startAt}&maxnumberofresults={count}");
        if (start is { } s)
        {
            uri += "&starttime=" + Uri.EscapeDataString(Time(s));
        }

        if (stop is { } e)
        {
            uri += "&stoptime=" + Uri.EscapeDataString(Time(e));
        }

        return uri;
    }

    public static string CapabilitiesUri() => CapabilitiesPath + "?schemaversion=1";

    public static string PropertiesUri(string recordingId, string diskId, DateTimeOffset? start, DateTimeOffset? stop, string format) =>
        PropertiesPath + RecordingQuery(recordingId, diskId, start, stop, format);

    public static string ExportUri(string recordingId, string diskId, DateTimeOffset? start, DateTimeOffset? stop, string format) =>
        ExportPath + RecordingQuery(recordingId, diskId, start, stop, format);

    private static string RecordingQuery(string recordingId, string diskId, DateTimeOffset? start, DateTimeOffset? stop, string format)
    {
        var query = "?schemaversion=1&recordingid=" + Uri.EscapeDataString(recordingId) + "&diskid=" + Uri.EscapeDataString(diskId)
            + "&exportformat=" + Uri.EscapeDataString(format);
        if (start is { } s)
        {
            query += "&starttime=" + Uri.EscapeDataString(Time(s));
        }

        if (stop is { } e)
        {
            query += "&stoptime=" + Uri.EscapeDataString(Time(e));
        }

        return query;
    }

    /// <summary>The file extension of an export format.</summary>
    public static string Extension(string format) => format == Mp4 ? ".mp4" : ".mkv";
}

/// <summary>An error the export API answered (<c>GeneralError</c>): the code and the camera's text.</summary>
public sealed class EdgeStorageException(int code, string description)
    : Exception(string.IsNullOrWhiteSpace(description) ? string.Create(CultureInfo.InvariantCulture, $"The camera refused (error {code})") : description.Trim().TrimEnd('.'))
{
    public int Code { get; } = code;
}

/// <summary>Reads the answers of <see cref="EdgeStorageRequests"/> (XML through <see cref="DeviceXml"/>, names without namespace).</summary>
public static class EdgeStorageParsing
{
    /// <summary>One page of <c>record/list.cgi</c>: the number of recordings that matched and the recordings of the page.</summary>
    public static (int Matched, IReadOnlyList<RecordingEntry> Recordings) ParseList(string xml)
    {
        var doc = DeviceXml.Parse(xml);
        var recordings = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "recordings")
            ?? throw new FormatException("The camera sent no list of recordings.");
        var list = new List<RecordingEntry>();
        foreach (var r in recordings.Elements().Where(e => e.Name.LocalName == "recording"))
        {
            var id = Attr(r, "recordingid");
            var disk = Attr(r, "diskid");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(disk) || ParseTime(Attr(r, "starttime")) is not { } start)
            {
                continue;
            }

            list.Add(new RecordingEntry(id, disk, start, ParseTime(Attr(r, "stoptime")), NullIfEmpty(Attr(r, "source")), NullIfEmpty(Attr(r, "recordingtype"))));
        }

        var matched = int.TryParse(Attr(recordings, "numberofrecordings"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : list.Count;
        return (Math.Max(matched, list.Count), list);
    }

    /// <summary>
    /// Whether <c>disks/list.cgi</c> lists a disk in use (status "OK"). Verified: a camera without an SD card lists
    /// <c>SD_DISK</c> and <c>NetworkShare</c> with status "disconnected".
    /// </summary>
    public static bool HasDiskInUse(string xml) =>
        DeviceXml.Parse(xml).Descendants().Where(e => e.Name.LocalName == "disk").Any(d => string.Equals(Attr(d, "status"), "OK", StringComparison.OrdinalIgnoreCase));

    /// <summary>The export formats the camera offers (<c>capabilities.cgi</c>), e.g. "matroska", "mp4".</summary>
    public static IReadOnlyList<string> ParseFormats(string xml)
    {
        var doc = DeviceXml.Parse(xml);
        ThrowOnError(doc);
        return [.. doc.Descendants().Where(e => e.Name.LocalName == "ExportCapabilities").Select(e => Attr(e, "ExportFormat")).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The estimated file size of <c>properties.cgi</c> in bytes; null when the camera cannot estimate it (-1).</summary>
    public static long? ParseEstimatedBytes(string xml)
    {
        var doc = DeviceXml.Parse(xml);
        ThrowOnError(doc);
        var properties = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ExportProperties")
            ?? throw new FormatException("The camera sent no export properties.");
        return long.TryParse(Attr(properties, "EstimatedFileSize"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var kb) && kb >= 0
            ? kb * 1024
            : null;
    }

    /// <summary>The format to export in: MP4 when offered (plays everywhere), else Matroska (every camera with the API).</summary>
    public static string ChooseFormat(IReadOnlyList<string> offered) =>
        offered.Contains(EdgeStorageRequests.Mp4, StringComparer.OrdinalIgnoreCase) ? EdgeStorageRequests.Mp4 : EdgeStorageRequests.Matroska;

    /// <summary>The file name of a <c>Content-Disposition</c> header without folders, or null.</summary>
    public static string? FileName(System.Net.Http.Headers.ContentDispositionHeaderValue? disposition)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = Path.GetFileName(name.Trim().Trim('"').Replace('\\', '/'));
        return name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." ? null : name;
    }

    private static void ThrowOnError(XDocument doc)
    {
        if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "GeneralError") is { } error)
        {
            var code = int.TryParse(Child(error, "ErrorCode"), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0;
            throw new EdgeStorageException(code, Child(error, "ErrorDescription"));
        }
    }

    private static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
