using System.Text.Json;

using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Api;

/// <summary>Ids, names, permissions, events and the action shared by the server part, the page and the tests (docs/spec/ocr.md).</summary>
public static class OcrInfo
{
    public const string PluginId = "oadm.ocr";
    public const string DisplayName = "Text recognition";
    public const string IconKey = "text";

    /// <summary>The page's subtitle and the plugin's one line.</summary>
    public const string Description = "Reads text in camera pictures.";

    /// <summary>The empty page.</summary>
    public const string EmptyText = "Create a reader to read text from a camera.";

    /// <summary>
    /// "View text recognition": the page, the readers and their results. The host's automatic "show" permission of the page
    /// (<c>PermissionKeys.Show</c>, "oadm.ocr:show"), declared with these texts.
    /// </summary>
    public const string ViewPermissionName = "View text recognition";

    /// <summary>"Change text readers": New, Delete, Save, Read now ("oadm.ocr:change").</summary>
    public const string ChangePermission = "change";

    public const string ChangePermissionName = "Change text readers";

    /// <summary>The first folder of the readers' events in the rule dialog; the second is the reader's name.</summary>
    public const string EventGroup = DisplayName;

    /// <summary>The start of a reader's event type id: <c>oadm.ocr.text-read.&lt;reader id&gt;</c>.</summary>
    public const string TextReadTypePrefix = PluginId + ".text-read.";

    /// <summary>The name of a reader's event.</summary>
    public const string TextReadName = "Text read";

    /// <summary>The event type of a reader "Text read" (stable while the reader lives).</summary>
    public static string TextReadTypeId(string readerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerId);
        return TextReadTypePrefix + readerId;
    }

    /// <summary>The start of a reader's event type id for no text: <c>oadm.ocr.no-text.&lt;reader id&gt;</c>.</summary>
    public const string NoTextTypePrefix = PluginId + ".no-text.";

    /// <summary>The name of a reader's event when an area holds no text.</summary>
    public const string NoTextName = "No text read";

    /// <summary>The event type of a reader "No text read" (stable while the reader lives).</summary>
    public static string NoTextTypeId(string readerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerId);
        return NoTextTypePrefix + readerId;
    }

    /// <summary>The start of a reader's event type id for its problems: <c>oadm.ocr.reader-problem.&lt;reader id&gt;</c>.</summary>
    public const string ReaderProblemTypePrefix = PluginId + ".reader-problem.";

    /// <summary>The name of the event that is on while a reader has a problem.</summary>
    public const string ReaderProblemName = "Reader problem";

    /// <summary>The event type that is on while at least one reader has a problem.</summary>
    public const string AnyReaderProblemTypeId = PluginId + ".any-reader-problem";

    /// <summary>The name of <see cref="AnyReaderProblemTypeId"/>.</summary>
    public const string AnyReaderProblemName = "Any reader problem";

    /// <summary>The event type of a reader "Reader problem" (stable while the reader lives).</summary>
    public static string ReaderProblemTypeId(string readerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerId);
        return ReaderProblemTypePrefix + readerId;
    }

    /// <summary>The folders of a reader's event in the rule dialog: Text recognition / reader name.</summary>
    public static IReadOnlyList<string> TextReadPath(string readerName) => [EventGroup, readerName];

    /// <summary>The values of the problem events: the reader's name (the names of "Any reader problem").</summary>
    public const string ReaderField = "reader";

    /// <summary>The server's full text, as the tooltip of the reader's red chip.</summary>
    public const string ProblemField = "problem";

    /// <summary>"Any reader problem": how many readers have a problem.</summary>
    public const string CountField = "count";

    /// <summary>The event's values (fields), docs/spec/ocr.md "Events and the rule action".</summary>
    public const string AreaField = "area";

    public const string TextField = "text";

    public const string TextReadField = "textRead";

    public const string ConfidenceField = "confidence";

    /// <summary>Every different match of the format, separated by a space; empty without a format.</summary>
    public const string AllMatchesField = "allMatches";

    /// <summary>The value as a number ("." as the decimal sign) when it is one; empty otherwise.</summary>
    public const string NumberField = "number";

    public const string MatchesFormatField = "matchesFormat";

    /// <summary>The values of <see cref="MatchesFormatField"/>.</summary>
    public const string Yes = "Yes";

    public const string No = "No";

    /// <summary>The rule action "Read text": reads one picture of the chosen reader now.</summary>
    public const string ReadTextActionId = PluginId + ".read-text";

    public const string ReadTextActionName = "Read text";

    /// <summary>The action's setting: the reader's id.</summary>
    public const string ReaderActionField = "reader";

    /// <summary>The action's answer while a reading of that reader runs; nothing is queued.</summary>
    public const string AlreadyReading = "Already reading";

}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class OcrMethods
{
    /// <summary>{} -> <see cref="ReaderList"/>: the caller's readers with their last text. View.</summary>
    public const string ListReaders = "listReaders";

    /// <summary><see cref="ReaderRequest"/> -> <see cref="Reader"/>. View.</summary>
    public const string GetReader = "getReader";

    /// <summary>
    /// <see cref="SaveReaderRequest"/> -> <see cref="SaveReaderReply"/>: creates (empty id) or changes a reader. A check that
    /// belongs to a field answers with <see cref="SaveReaderReply.Errors"/> (nothing stored); other refusals throw. Change.
    /// </summary>
    public const string SaveReader = "saveReader";

    /// <summary><see cref="ReaderRequest"/> -> {}: deletes a reader. Change.</summary>
    public const string DeleteReader = "deleteReader";

    /// <summary>
    /// <see cref="ReaderRequest"/> -> {}: an On action reader reads one picture now and fires its real Text read and No text read events, so
    /// the rules on it run (the same as the action Read text; refused for a watching reader). Change, audited.
    /// </summary>
    public const string ReadNow = "readNow";

    /// <summary>
    /// <see cref="ReaderRequest"/> -> <see cref="ReadingReply"/>: the reader's last reading and last reads with the cut-outs
    /// (only for a user with Live view), never the picture (empty without one). View.
    /// </summary>
    public const string GetReading = "getReading";

    /// <summary>
    /// <see cref="PreviewRequest"/> -> <see cref="PreviewReply"/>: starts the page's live preview of a reader (no
    /// <see cref="PreviewRequest.PreviewId"/>, or one that ended) or keeps it alive and takes the new draft. The preview reads
    /// at the reader's rate (On action: 1 per second) and publishes no rule events; it ends
    /// <see cref="Model.OcrLimits.PreviewLease"/> after the last call. View.
    /// </summary>
    public const string StartPreview = "startPreview";

    /// <summary><see cref="PreviewRequest"/> (the id only) -> {}: ends the preview. View.</summary>
    public const string StopPreview = "stopPreview";

    /// <summary>
    /// <see cref="PreviewRequest"/> (the id only) -> <see cref="ReadingReply"/>: the preview's last reading and last reads, no
    /// picture; the reading's own cut-outs only while paused, the rows' cut-outs for a user with Live view. View.
    /// </summary>
    public const string GetPreview = "getPreview";

    /// <summary>
    /// <see cref="PreviewRequest"/> (the id only) -> <see cref="PauseReply"/>: holds the frame of the preview's latest reading
    /// in server memory and answers it, so the page tunes on the very picture that was read; the draft then re-reads that
    /// frame (docs/spec/ocr.md "Pause"). View and the host's Live view.
    /// </summary>
    public const string PausePreview = "pausePreview";

    /// <summary><see cref="PreviewRequest"/> (the id only) -> <see cref="PreviewReply"/>: the held frame goes, the preview reads live again. View.</summary>
    public const string ResumePreview = "resumePreview";

    /// <summary>Event: <see cref="PreviewEvent"/> after every reading of a preview, only to the user who started it (the page fetches the cut-outs with <see cref="GetPreview"/>).</summary>
    public const string PreviewTopic = "preview";

    /// <summary>{} -> <see cref="EngineInfo"/>: what reads the pictures and the frame rates it offers. View.</summary>
    public const string GetEngine = "getEngine";

    /// <summary><see cref="UseGpuRequest"/> -> <see cref="EngineInfo"/>: "Use GPU" on or off (a plugin setting). Change.</summary>
    public const string SetUseGpu = "setUseGpu";

    /// <summary><see cref="CameraOptionsRequest"/> -> <see cref="CameraOptions"/>: the video sources and picture sizes of one camera (read only). View.</summary>
    public const string GetCameraOptions = "getCameraOptions";

    /// <summary>
    /// Event: <see cref="ReadingEvent"/> when a reading's values change, else at most once a second per reader (without the
    /// cut-outs; a page that shows the reader fetches them with <see cref="GetReading"/>).
    /// </summary>
    public const string ReadingTopic = "reading";

    /// <summary>Event: <see cref="ReaderList"/> after a reader was created, changed or deleted.</summary>
    public const string ReadersTopic = "readers";

    /// <summary>
    /// {} -> <see cref="OcrStats"/>: how the recognition keeps up (docs/spec/ocr.md "Runtime stats"), for the caller's
    /// readers plus the server-wide load and CPU. Each call keeps <see cref="StatsTopic"/> on for the caller's scope for
    /// <see cref="Model.OcrLimits.PreviewLease"/> (the page calls it every <see cref="Model.OcrLimits.PreviewKeepAlive"/>). View.
    /// </summary>
    public const string GetStats = "getStats";

    /// <summary>Event: <see cref="OcrStats"/> every 2 s while a page of the scope keeps it on with <see cref="GetStats"/>.</summary>
    public const string StatsTopic = "stats";
}

/// <summary>The traffic light of the runtime stats.</summary>
public enum LoadState
{
    /// <summary>Green: every watching reader keeps its frame rate and the load is below 75 %.</summary>
    Ok = 0,

    /// <summary>Orange: a reader reads slower than set, or the load is 75 % or more.</summary>
    Busy = 1,

    /// <summary>Red: the load stayed at 95 % or more for 30 s, or a reader reads less than half of its frame rate.</summary>
    Overloaded = 2,

    /// <summary>The readers wait for the graphics card's test (<see cref="Readers.ReaderRules.Starting"/>).</summary>
    Starting = 3,
}

/// <summary>
/// The answer of <see cref="OcrMethods.GetStats"/> and the live event <see cref="OcrMethods.StatsTopic"/> (docs/spec/ocr.md
/// "Runtime stats"). Load, server CPU, pictures per second and reading time are server-wide; the counts and the readers are
/// the caller's (a user with a device group sees only their group's readers).
/// </summary>
public sealed record OcrStats
{
    /// <summary>The traffic light.</summary>
    public LoadState State { get; init; }

    /// <summary>"All readers keep up", "Busy: 2 readers read slower than set", ... (<see cref="Readers.ReaderRules"/>).</summary>
    public string StateText { get; init; } = string.Empty;

    /// <summary>0 to 100: the share of the last 10 s the recognition workers were busy (graphics card: its one at a time).</summary>
    public double Load { get; init; }

    /// <summary>Pictures read per second over the last 10 s, previews included.</summary>
    public double PicturesPerSecond { get; init; }

    /// <summary>
    /// Pictures per second over the last 10 s whose areas had not changed: their last reading was given again without the
    /// models (watching readers and previews). Not in <see cref="PicturesPerSecond"/>.
    /// </summary>
    public double UnchangedPerSecond { get; init; }

    /// <summary>Milliseconds per picture read over the last 10 s; 0 without one.</summary>
    public double AverageReadMs { get; init; }

    /// <summary>The caller's watching readers that read now.</summary>
    public int Watching { get; init; }

    /// <summary>Watching readers per server at most (by the server's cores: 4 per recognition worker, 8 to 50).</summary>
    public int MaxWatching { get; init; }

    /// <summary>The caller's page previews now.</summary>
    public int Previews { get; init; }

    /// <summary>Previews per server at most.</summary>
    public int MaxPreviews { get; init; }

    /// <summary>The caller's camera streams open now (watching readers and previews with a stream of their own).</summary>
    public int Streams { get; init; }

    /// <summary>The caller's streams per site ("Harbour 2 of 4"); empty without sites.</summary>
    public IReadOnlyList<SiteStreams> Sites { get; init; } = [];

    /// <summary>0 to 100: the OADM server process's share of all cores over the last 10 s.</summary>
    public double ServerCpu { get; init; }

    /// <summary>Each of the caller's watching readers: its frame rate and what it reads.</summary>
    public IReadOnlyList<ReaderRate> Readers { get; init; } = [];
}

/// <summary>Streams at one site.</summary>
public sealed record SiteStreams
{
    public string Name { get; init; } = string.Empty;

    public int Streams { get; init; }

    /// <summary>Watching readers per site at most.</summary>
    public int Max { get; init; }
}

/// <summary>A watching reader's frame rate and what it reached over the last 10 s.</summary>
public sealed record ReaderRate
{
    public string ReaderId { get; init; } = string.Empty;

    /// <summary>The frame rate set.</summary>
    public int Fps { get; init; }

    /// <summary>Pictures read per second over the last 10 s.</summary>
    public double Reads { get; init; }

    /// <summary>Watching for the whole window and below 90 % of <see cref="Fps"/>: its list chip turns orange.</summary>
    public bool Slow { get; init; }
}

/// <summary>A reader in the list.</summary>
public sealed record ReaderSummary
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Guid DeviceId { get; init; }

    public ReaderMode Mode { get; init; }

    /// <summary>The frame rate while Watching ("Watching · 2 fps").</summary>
    public int Fps { get; init; } = OcrLimits.DefaultFps;

    /// <summary>The value of the first area of the last reading; empty without one.</summary>
    public string LastText { get; init; } = string.Empty;

    /// <summary>When the last reading was taken; null without one.</summary>
    public DateTimeOffset? LastTime { get; init; }

    /// <summary>Why the reader does not read now (the watching limits, the camera refuses or gives no pictures, its camera left its device group); null while it can read.</summary>
    public string? Problem { get; init; }
}

/// <summary>The answer of <see cref="OcrMethods.ListReaders"/> and the live event <see cref="OcrMethods.ReadersTopic"/>.</summary>
public sealed record ReaderList
{
    public IReadOnlyList<ReaderSummary> Readers { get; init; } = [];

    /// <summary>The frame rates a watching reader may use: 1, 2, 4, 8, 12 or 15 on every server.</summary>
    public IReadOnlyList<int> FpsChoices { get; init; } = [];

    /// <summary>Watching readers per server at most (by the server's cores: 4 per recognition worker, 8 to 50).</summary>
    public int MaxWatching { get; init; }
}

/// <summary>A request for one reader.</summary>
public sealed record ReaderRequest
{
    public string ReaderId { get; init; } = string.Empty;

    /// <summary><see cref="OcrMethods.GetReading"/>: the reads whose cut-out the page has (see <see cref="ReadingReply.Recent"/>); null: none.</summary>
    public IReadOnlyList<long>? KnownCutouts { get; init; }
}

/// <summary>The request of <see cref="OcrMethods.SaveReader"/>: the whole reader (empty id: a new one).</summary>
public sealed record SaveReaderRequest
{
    public Reader Reader { get; init; } = new();
}

/// <summary>The answer of <see cref="OcrMethods.SaveReader"/>: the stored reader, or the field errors of a refused save.</summary>
public sealed record SaveReaderReply
{
    /// <summary>The reader as stored (with its id); null when the save was refused.</summary>
    public Reader? Reader { get; init; }

    /// <summary>Why the save was refused, per field (<see cref="OcrFields"/>); empty after a save.</summary>
    public IReadOnlyList<FieldError> Errors { get; init; } = [];
}

/// <summary>A refusal of the server that belongs to one field of the page.</summary>
/// <param name="Field">The field's key (<see cref="OcrFields"/>): <c>name</c>, <c>mode</c>, <c>areas[2].format</c>.</param>
/// <param name="Message">The text shown below the field.</param>
public sealed record FieldError(string Field, string Message);

/// <summary>The stable keys of <see cref="FieldError.Field"/>: the page shows each error below its field.</summary>
public static class OcrFields
{
    public const string Name = "name";

    public const string Camera = "camera";

    public const string VideoSource = "videoSource";

    public const string Resolution = "resolution";

    /// <summary>The mode and the watching limits.</summary>
    public const string Mode = "mode";

    public const string Fps = "fps";

    /// <summary>The area list as a whole (too many areas).</summary>
    public const string Areas = "areas";

    /// <summary><c>areas[i].name</c>.</summary>
    public static string AreaName(int index) => Area(index, "name");

    /// <summary><c>areas[i].box</c>.</summary>
    public static string AreaBox(int index) => Area(index, "box");

    /// <summary><c>areas[i].format</c>.</summary>
    public static string AreaFormat(int index) => Area(index, "format");

    /// <summary>The area index and its part of <c>areas[i].part</c>; null for another key.</summary>
    public static (int Index, string Part)? ParseArea(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        const string Start = "areas[";
        var close = field.IndexOf("].", StringComparison.Ordinal);
        return field.StartsWith(Start, StringComparison.Ordinal) && close > Start.Length
            && int.TryParse(field.AsSpan(Start.Length, close - Start.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            ? (index, field[(close + 2)..])
            : null;
    }

    private static string Area(int index, string part) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"areas[{index}].{part}");
}

/// <summary>The answer of <see cref="OcrMethods.GetEngine"/> and <see cref="OcrMethods.SetUseGpu"/> (docs/spec/ocr.md "GPU").</summary>
public sealed record EngineInfo
{
    /// <summary>"NVIDIA GeForce RTX 5070 Ti" or "CPU (8 cores)": shown as "Runs on: ...".</summary>
    public string RunsOn { get; init; } = string.Empty;

    /// <summary>A graphics card was found and passed the test reading.</summary>
    public bool GpuAvailable { get; init; }

    /// <summary>The setting "Use GPU" (on by default).</summary>
    public bool UseGpu { get; init; } = true;

    /// <summary>The plugin started and the graphics card's test runs: the readers wait for it.</summary>
    public bool GpuStarting { get; init; }

    /// <summary>The frame rates a watching reader may use: 1, 2, 4, 8, 12 or 15 on every server.</summary>
    public IReadOnlyList<int> FpsChoices { get; init; } = OcrLimits.FpsChoices;
}

/// <summary>The request of <see cref="OcrMethods.SetUseGpu"/>.</summary>
public sealed record UseGpuRequest
{
    public bool UseGpu { get; init; }
}

/// <summary>The request of <see cref="OcrMethods.GetCameraOptions"/>.</summary>
public sealed record CameraOptionsRequest
{
    public Guid DeviceId { get; init; }
}

/// <summary>A video source of a camera: its number (1 for the first) and its name ("View area 1").</summary>
public sealed record CameraSource
{
    public int Number { get; init; } = 1;

    public string Name { get; init; } = string.Empty;
}

/// <summary>The answer of <see cref="OcrMethods.GetCameraOptions"/>.</summary>
public sealed record CameraOptions
{
    public IReadOnlyList<CameraSource> VideoSources { get; init; } = [];

    /// <summary>The camera's picture sizes, largest first.</summary>
    public IReadOnlyList<Resolution> Resolutions { get; init; } = [];
}

/// <summary>A reading with the cut-outs of its values (never the whole picture: the page shows the camera's live video).</summary>
public sealed record ReadingReply
{
    /// <summary>The reading; null when the reader has none yet.</summary>
    public Reading? Reading { get; init; }

    /// <summary>
    /// The further values of each area, in the reader's area order; empty without a reading. With the cut-outs only for a
    /// user with the host's Live view permission.
    /// </summary>
    public IReadOnlyList<AreaDetails> Details { get; init; } = [];

    /// <summary>
    /// The last reads, newest first, at most <see cref="OcrLimits.RecentReads"/>; empty while paused. A cut-out comes once per
    /// row: a row named in the request's known cut-outs comes without it.
    /// </summary>
    public IReadOnlyList<RecentRead> Recent { get; init; } = [];
}

/// <summary>
/// One row of the last reads: a new value in an area (docs/spec/ocr.md "The last 20 reads"). Server memory only.
/// </summary>
public sealed record RecentRead
{
    /// <summary>Unique on the server, growing: the page keeps a row's decoded cut-out by it.</summary>
    public long Id { get; init; }

    /// <summary>When the picture was taken.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>The area's name at that moment.</summary>
    public string Area { get; init; } = string.Empty;

    /// <summary>The value, at most <see cref="Events.TextReadEvents.MaxTextLength"/> characters.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>0 to 100.</summary>
    public double Confidence { get; init; }

    /// <summary>The area had a format.</summary>
    public bool HasFormat { get; init; }

    public bool MatchesFormat { get; init; }

    /// <summary>The value's cut-out as JPEG (base64 in JSON), at most <see cref="OcrLimits.MaxCutoutBytes"/>; only for a user with Live view.</summary>
    public byte[]? Cutout { get; init; }
}

/// <summary>
/// The values of an area beyond <see cref="AreaResult"/> (docs/spec/ocr.md "Expected text", "Events and the rule action"):
/// every different match, the number, where the value was found and its cut-out picture (in memory only).
/// </summary>
public sealed record AreaDetails
{
    /// <summary>The area's name (<see cref="AreaResult.Area"/>).</summary>
    public string Area { get; init; } = string.Empty;

    /// <summary>Every different match of the format, at most 10, separated by a space; empty without a format.</summary>
    public string AllMatches { get; init; } = string.Empty;

    /// <summary>The value as a number in invariant culture ("1234.5"); empty when it is none.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>Where the value was found, relative to the picture; null without a value.</summary>
    public RelativeBox? MatchBox { get; init; }

    /// <summary>How the value's line lies in the picture, for its upright cut-out; server only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LineTurn? MatchTurn { get; init; }

    /// <summary>The value's cut-out picture as JPEG (base64 in JSON); only in <see cref="ReadingReply"/>, null without one.</summary>
    public byte[]? Cutout { get; init; }

    /// <summary>Lines lower than 10 px were left out and nothing else was read: a held frame shows <see cref="Readers.ReaderRules.TooSmall"/>.</summary>
    public bool TooSmall { get; init; }

    /// <summary>Lines lower than 10 px were left out, whatever else was read: live, the page's alert after <see cref="OcrLimits.SmallTextWait"/> without a value.</summary>
    public bool SmallText { get; init; }
}

/// <summary>The live event <see cref="OcrMethods.ReadingTopic"/>.</summary>
public sealed record ReadingEvent
{
    public Reading Reading { get; init; } = new();

    /// <summary>The further values of each area (<see cref="AreaDetails"/> without the cut-outs).</summary>
    public IReadOnlyList<AreaDetails> Details { get; init; } = [];
}

/// <summary>
/// The request of <see cref="OcrMethods.StartPreview"/>, <see cref="OcrMethods.StopPreview"/> and
/// <see cref="OcrMethods.GetPreview"/>: the preview's id (empty: a new one) and, to start or keep it, the reader as the page
/// shows it now (saved or not).
/// </summary>
public sealed record PreviewRequest
{
    public string PreviewId { get; init; } = string.Empty;

    /// <summary>The page's draft; only <see cref="OcrMethods.StartPreview"/> reads it.</summary>
    public Reader? Reader { get; init; }

    /// <summary><see cref="OcrMethods.GetPreview"/>: the reads whose cut-out the page has (see <see cref="ReadingReply.Recent"/>); null: none.</summary>
    public IReadOnlyList<long>? KnownCutouts { get; init; }
}

/// <summary>The answer of <see cref="OcrMethods.StartPreview"/>.</summary>
public sealed record PreviewReply
{
    /// <summary>The preview's id: the page keeps it alive with it and finds its readings by it.</summary>
    public string PreviewId { get; init; } = string.Empty;

    /// <summary>Why the preview reads nothing now (the camera, the limits); null while it reads.</summary>
    public string? Problem { get; init; }

    /// <summary>The preview holds a frame (<see cref="OcrMethods.PausePreview"/>); false after the frame went, whatever the reason.</summary>
    public bool Paused { get; init; }

    /// <summary>Paused, and the held frame is being read with the newest draft: a reading follows as the event <see cref="OcrMethods.PreviewTopic"/>.</summary>
    public bool Rereading { get; init; }

    /// <summary>The held frame's readings so far (<see cref="PreviewEvent.HeldRead"/>): an event with a higher number already brought what this answer announces.</summary>
    public int HeldRead { get; init; }
}

/// <summary>The answer of <see cref="OcrMethods.PausePreview"/>.</summary>
public sealed record PauseReply
{
    /// <summary>The frame is held; false: <see cref="Problem"/> says why not (the preview ended: no text).</summary>
    public bool Paused { get; init; }

    public string? Problem { get; init; }

    /// <summary>The held frame as the camera sent it (base64 in JSON): the exact picture of <see cref="Reading"/>.</summary>
    public byte[]? Picture { get; init; }

    /// <summary>The reading of that frame.</summary>
    public Reading? Reading { get; init; }

    /// <summary>The further values of each area with the cut-outs.</summary>
    public IReadOnlyList<AreaDetails> Details { get; init; } = [];

    /// <summary>The frame is held for a draft its reading does not fit: a reading follows as the event <see cref="OcrMethods.PreviewTopic"/>.</summary>
    public bool Rereading { get; init; }

    /// <summary>See <see cref="PreviewReply.HeldRead"/>.</summary>
    public int HeldRead { get; init; }
}

/// <summary>The live event <see cref="OcrMethods.PreviewTopic"/>.</summary>
public sealed record PreviewEvent
{
    public string PreviewId { get; init; } = string.Empty;

    /// <summary>The how many reading of the held frame this is; 0 for a live reading.</summary>
    public int HeldRead { get; init; }

    public Reading Reading { get; init; } = new();

    /// <summary>The further values of each area (<see cref="AreaDetails"/> without the cut-outs).</summary>
    public IReadOnlyList<AreaDetails> Details { get; init; } = [];
}

/// <summary>The JSON of the page's calls and events (web names, enums as names).</summary>
public static class OcrJson
{
    public static readonly JsonSerializerOptions Options = PluginJson.CreateOptions(PluginJsonEnums.Names);

    public static string Serialize<T>(T value) => PluginJson.Serialize(value, Options);

    /// <summary>A call without a payload reads as an empty one; broken JSON is an <see cref="ArgumentException"/> (<see cref="PluginJson.ParseOrEmpty{T}"/>).</summary>
    public static T Deserialize<T>(string? json) => PluginJson.ParseOrEmpty<T>(json, Options);
}
