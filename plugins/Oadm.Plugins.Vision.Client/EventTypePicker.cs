using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A row of the event list: a folder heading or an event type.</summary>
public abstract record EventPickerRow;

public sealed record EventPickerFolder(string Name) : EventPickerRow;

public sealed record EventPickerType(EventTypeChoice Type) : EventPickerRow
{
    /// <summary>"Door open", or "Door open (turned off)" while the plugin that sends it is off (shown dimmed).</summary>
    public string Name => Type.Available ? Type.Name : $"{Type.Name} ({VisionTexts.TurnedOff})";

    public bool IsAvailable => Type.Available;
}

/// <summary>
/// The Choose event dialog of the Device event node: the event types (<see cref="VisionMethods.ListEventTypes"/>) in their
/// folders (the event rules' picker), a search; OK takes the chosen one. A type whose plugin is off shows dimmed with
/// "(turned off)". A per-device event then asks for the device.
/// </summary>
public sealed partial class EventTypePickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<EventTypeChoice> _types;

    public EventTypePickerViewModel(IReadOnlyList<EventTypeChoice> types, string? selectedId)
    {
        ArgumentNullException.ThrowIfNull(types);

        // Vision Center's own events never go into one of its nodes (the server leaves them out too).
        _types = [.. types.Where(t => !IsOwn(t))];
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
    public EventTypeChoice? Chosen => (Selected as EventPickerType)?.Type;

    /// <summary>The folder line of a type: its path ("Devices / Any device").</summary>
    public static string FolderOf(EventTypeChoice type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return string.Join(" / ", type.Path);
    }

    private static bool IsOwn(EventTypeChoice type) =>
        type.Id.StartsWith(VisionInfo.PluginId + ".", StringComparison.Ordinal) || (type.Path.Count > 0 && type.Path[0] == VisionInfo.EventGroup);

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
            .Where(t => query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || FolderOf(t).Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(FolderOf)
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
