using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Api;

/// <summary>Ids, names, permissions, events and the action shared by the server part, the page and the tests (docs/spec/ocr.md).</summary>
public static class OcrInfo
{
    public const string PluginId = "oadm.ocr";
    public const string DisplayName = "Text recognition";
    public const string IconKey = "text";

    /// <summary>The page's subtitle and the plugin's one line.</summary>
    public const string Description = "Reads text in camera pictures for the event rules.";

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

    /// <summary>The folders of a reader's event in the rule dialog: Text recognition / reader name.</summary>
    public static IReadOnlyList<string> TextReadPath(string readerName) => [EventGroup, readerName];

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

    /// <summary>The page format field's hint.</summary>
    public const string FormatHint = "# digit, A letter, * letter or digit";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class OcrMethods
{
    /// <summary>{} -> <see cref="ReaderList"/>: the caller's readers with their last text. View.</summary>
    public const string ListReaders = "listReaders";

    /// <summary><see cref="ReaderRequest"/> -> <see cref="Reader"/>. View.</summary>
    public const string GetReader = "getReader";

    /// <summary><see cref="SaveReaderRequest"/> -> <see cref="Reader"/>: creates (empty id) or changes a reader. Change.</summary>
    public const string SaveReader = "saveReader";

    /// <summary><see cref="ReaderRequest"/> -> {}: deletes a reader. Change.</summary>
    public const string DeleteReader = "deleteReader";

    /// <summary><see cref="ReaderRequest"/> -> <see cref="ReadingReply"/>: reads one picture now and publishes every area's event. Change.</summary>
    public const string ReadNow = "readNow";

    /// <summary><see cref="ReaderRequest"/> -> <see cref="ReadingReply"/>: the reader's last reading with its picture (empty without one). View.</summary>
    public const string GetReading = "getReading";

    /// <summary>
    /// Event: <see cref="ReadingEvent"/> after every reading (without the picture; a page that shows the reader fetches it with
    /// <see cref="GetReading"/>, at most at the reader's rate).
    /// </summary>
    public const string ReadingTopic = "reading";

    /// <summary>Event: <see cref="ReaderList"/> after a reader was created, changed or deleted.</summary>
    public const string ReadersTopic = "readers";
}

/// <summary>A reader in the list.</summary>
public sealed record ReaderSummary
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Guid DeviceId { get; init; }

    public ReaderMode Mode { get; init; }

    /// <summary>The value of the first area of the last reading; empty without one.</summary>
    public string LastText { get; init; } = string.Empty;

    /// <summary>When the last reading was taken; null without one.</summary>
    public DateTimeOffset? LastTime { get; init; }

    /// <summary>Why the reader does not read now (its camera left its device group); null while it can read.</summary>
    public string? Problem { get; init; }
}

/// <summary>The answer of <see cref="OcrMethods.ListReaders"/> and the live event <see cref="OcrMethods.ReadersTopic"/>.</summary>
public sealed record ReaderList
{
    public IReadOnlyList<ReaderSummary> Readers { get; init; } = [];

    /// <summary>The frame rates a watching reader may use on this server (1, 2, 4 on the CPU; 1, 2, 4, 8, 15 on a GPU).</summary>
    public IReadOnlyList<int> FpsChoices { get; init; } = [];

    /// <summary>Watching readers per server at most (20 on the CPU, 50 on a GPU).</summary>
    public int MaxWatching { get; init; }
}

/// <summary>A request for one reader.</summary>
public sealed record ReaderRequest
{
    public string ReaderId { get; init; } = string.Empty;
}

/// <summary>The request of <see cref="OcrMethods.SaveReader"/>: the whole reader (empty id: a new one).</summary>
public sealed record SaveReaderRequest
{
    public Reader Reader { get; init; } = new();
}

/// <summary>A reading with its picture.</summary>
public sealed record ReadingReply
{
    /// <summary>The reading; null when the reader has none yet.</summary>
    public Reading? Reading { get; init; }

    /// <summary>The picture of the reading as JPEG (base64 in JSON); null without one.</summary>
    public byte[]? Jpeg { get; init; }

    /// <summary>The further values of each area (with the cut-outs), in the reader's area order; empty without a reading.</summary>
    public IReadOnlyList<AreaDetails> Details { get; init; } = [];
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

    /// <summary>The value's cut-out picture as JPEG (base64 in JSON); only in <see cref="ReadingReply"/>, null without one.</summary>
    public byte[]? Cutout { get; init; }
}

/// <summary>The live event <see cref="OcrMethods.ReadingTopic"/>.</summary>
public sealed record ReadingEvent
{
    public Reading Reading { get; init; } = new();

    /// <summary>The further values of each area (<see cref="AreaDetails"/> without the cut-outs).</summary>
    public IReadOnlyList<AreaDetails> Details { get; init; } = [];
}

/// <summary>The JSON of the page's calls and events (web names, enums as names).</summary>
public static class OcrJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("Missing payload.", nameof(json));
}
