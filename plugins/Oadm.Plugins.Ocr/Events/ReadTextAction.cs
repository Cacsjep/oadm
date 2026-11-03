using System.Text.Json;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Ocr.Events;

/// <summary>
/// Rule action "Read text" (docs/spec/ocr.md "Events and the rule action"): reads one picture of the chosen reader (On
/// action, or the fresh reading of a Watching one) now and publishes every area's event "Text read", changed or not. While a reading of that reader
/// runs the action ends with "Already reading" in the event log and nothing is queued; loops (a Text read event that runs
/// Read text of the same reader) are held by the rule limits. Contributed by Text recognition, available while it is on.
/// Event rules are for users with all devices (docs/spec/device-scope.md), so every reader is a choice.
/// </summary>
public sealed class ReadTextAction(OcrPlugin owner) : IActionPlugin
{
    public string Id => OcrInfo.ReadTextActionId;

    public string DisplayName => OcrInfo.ReadTextActionName;

    public string? IconKey => OcrInfo.IconKey;

    public string Group => OcrInfo.DisplayName;

    public string? Description => "Reads one picture of a text reader now.";

    public bool NeedsDevice => false;

    public IReadOnlyList<ActionField> Fields { get; } = [ActionField.DynamicChoice(OcrInfo.ReaderActionField, "Reader")];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var id = ReaderOf(settings);
        if (string.IsNullOrEmpty(id))
        {
            return new Dictionary<string, string> { [OcrInfo.ReaderActionField] = ReaderRules.ReaderMissing };
        }

        return owner.FindReader(id) is not null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [OcrInfo.ReaderActionField] = ReaderRules.Gone };
    }

    public string Describe(JsonElement settings) =>
        owner.FindReader(ReaderOf(settings)) is { } reader ? $"Read text with {reader.Name}" : DisplayName;

    public Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IActionQueryContext ctx, string fieldKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActionChoice>>(fieldKey == OcrInfo.ReaderActionField
            ? [.. owner.AllReaders.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => new ActionChoice(r.Id, r.Name))]
            : []);

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var reader = owner.FindReader(invocation.Get(OcrInfo.ReaderActionField)) ?? throw new InvalidOperationException(ReaderRules.Gone);
        try
        {
            var reading = await owner.ReadOnceAsync(reader, ct).ConfigureAwait(false);
            ctx.Log(string.Join("; ", reading.Reading.Areas.Select(a => $"{a.Area}: {(a.Text.Length == 0 ? "no text" : TextReadEvents.Join(a.Text))}")));
        }
        catch (InvalidOperationException ex) when (ex.Message == OcrInfo.AlreadyReading)
        {
            ctx.Log(OcrInfo.AlreadyReading);
        }
    }

    private static string? ReaderOf(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(OcrInfo.ReaderActionField, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
