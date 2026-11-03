using System.Globalization;
using System.Text;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Ocr.Events;

/// <summary>
/// The event "Text read" of each reader (docs/spec/ocr.md "Events and the rule action"): one stateless event type per
/// reader in the folder Text recognition / reader name, one event per area and reading, with the fields Area, Text, Text
/// read, All matches, Number, Confidence and Matches format.
/// </summary>
public static class TextReadEvents
{
    /// <summary>The longest text of an event value (docs/spec/ocr.md "Several lines").</summary>
    public const int MaxTextLength = 500;

    /// <summary>The event type of <paramref name="reader"/>: on its camera, owned by the reader's owner.</summary>
    public static EventTypeInfo TypeOf(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var areas = reader.Areas.Select(a => a.Name).ToList();
        return new EventTypeInfo(
            OcrInfo.TextReadTypeId(reader.Id),
            OcrInfo.TextReadName,
            OcrInfo.EventGroup,
            EventKind.Stateless,
            EventScope.Device,
            [
                new EventField(OcrInfo.AreaField, "Area", EventFieldType.Choice, areas),
                new EventField(OcrInfo.TextField, "Text"),
                new EventField(OcrInfo.TextReadField, "Text read"),
                new EventField(OcrInfo.AllMatchesField, "All matches"),
                new EventField(OcrInfo.NumberField, "Number", EventFieldType.Number),
                new EventField(OcrInfo.ConfidenceField, "Confidence", EventFieldType.Number),
                new EventField(OcrInfo.MatchesFormatField, "Matches format", EventFieldType.Choice, [OcrInfo.Yes, OcrInfo.No]) { Kind = EventValueKind.YesNo },
            ])
        {
            Description = $"The reader {reader.Name} read the text of an area.",
            DeviceIds = [reader.DeviceId],
            OwnerScopeKey = OwnerScope.Of(reader.OwnerScopeKey),
            Path = OcrInfo.TextReadPath(reader.Name),
        };
    }

    /// <summary>
    /// The event's values of one area: Text and Text read with the lines joined by a space (a line ending in "-" without
    /// one), at most <see cref="MaxTextLength"/> characters; Confidence rounded to one decimal; Matches format Yes or No.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Data(AreaResult area, AreaDetails details)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(details);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OcrInfo.AreaField] = area.Area,
            [OcrInfo.TextField] = Join(area.Text),
            [OcrInfo.TextReadField] = Join(area.TextRead),
            [OcrInfo.AllMatchesField] = Cut(details.AllMatches),
            [OcrInfo.NumberField] = details.Number,
            [OcrInfo.ConfidenceField] = Math.Round(Math.Clamp(area.Confidence, 0, 100), 1).ToString(CultureInfo.InvariantCulture),
            [OcrInfo.MatchesFormatField] = area.MatchesFormat ? OcrInfo.Yes : OcrInfo.No,
        };
    }

    /// <summary>
    /// Lines joined for the events: with a space, a line that ends in "-" without one ("DECAF-" and "FEINATO" give
    /// "DECAF-FEINATO"); at most <see cref="MaxTextLength"/> characters.
    /// </summary>
    public static string Join(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var joined = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (joined.Length > 0 && joined[^1] != '-')
            {
                joined.Append(' ');
            }

            joined.Append(line);
        }

        return Cut(joined.ToString());
    }

    private static string Cut(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength];
}
