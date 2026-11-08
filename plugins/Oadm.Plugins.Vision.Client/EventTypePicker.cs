using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// An event type of the event rules as the page method <see cref="PickerEventTypes.Method"/> lists it: its id, its folder
/// ("Device status", "Vision Center / Gate 1"), its name and whether it comes from a device (then the node asks which one).
/// </summary>
public sealed record PickerEventType
{
    public string Id { get; init; } = string.Empty;

    public string Group { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Raised per device: the Device event node names the device.</summary>
    public bool DeviceScope { get; init; }
}

/// <summary>The reply of <see cref="PickerEventTypes.Method"/>.</summary>
public sealed record PickerEventTypeList
{
    public IReadOnlyList<PickerEventType> Types { get; init; } = [];
}

/// <summary>The page method that lists the event types for the Device event node.</summary>
public static class PickerEventTypes
{
    public const string Method = "listEventTypes";
}

/// <summary>A row of the event list: a folder heading or an event type.</summary>
public abstract record EventPickerRow;

public sealed record EventPickerFolder(string Name) : EventPickerRow;

public sealed record EventPickerType(PickerEventType Type) : EventPickerRow
{
    public string Name => Type.Name;
}

/// <summary>
/// The Choose event dialog of the Device event node: the event types in their folders (the event rules' picker), a search;
/// OK takes the chosen one. A per-device event then asks for the device.
/// </summary>
public sealed partial class EventTypePickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<PickerEventType> _types;

    public EventTypePickerViewModel(IReadOnlyList<PickerEventType> types, string? selectedId)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        Fill();
        Selected = Rows.OfType<EventPickerType>().FirstOrDefault(r => r.Type.Id == selectedId);
    }

    /// <summary>OK (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public static string Title => VisionTexts.ChooseEvent;

    public static string Placeholder => VisionTexts.SearchEvents;

    public static string EmptyText => VisionTexts.NoEventMatches;

    public RangeObservableCollection<EventPickerRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial EventPickerRow? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The chosen event type after OK.</summary>
    public PickerEventType? Chosen => (Selected as EventPickerType)?.Type;

    partial void OnSearchTextChanged(string value) => Fill();

    partial void OnSelectedChanged(EventPickerRow? value)
    {
        if (value is EventPickerFolder)
        {
            Selected = null; // a folder is no choice
        }
    }

    private void Fill()
    {
        var query = SearchText.Trim();
        var rows = new List<EventPickerRow>();
        foreach (var group in _types
            .Where(t => query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.Group)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            rows.Add(new EventPickerFolder(group.Key));
            rows.AddRange(group.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => new EventPickerType(t)));
        }

        Rows.ReplaceAll(rows);
        IsEmpty = rows.Count == 0;
    }

    private bool CanOk() => Selected is EventPickerType;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}

/// <summary>The value of an <c>EventType</c> field: {"type": "&lt;event type id&gt;", "device": "&lt;device id or empty&gt;"}.</summary>
public sealed record EventTypeValue(string Type, Guid? Device)
{
    public static EventTypeValue? Of(JsonNode? value) =>
        value is JsonObject o && NodeValues.Text(o["type"]) is { Length: > 0 } type
            ? new EventTypeValue(type, Guid.TryParse(NodeValues.Text(o["device"]), out var device) ? device : null)
            : null;

    public JsonObject ToJson() => new() { ["type"] = Type, ["device"] = Device?.ToString() ?? string.Empty };
}
