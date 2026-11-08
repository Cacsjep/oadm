using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// The Choose event dialog of the Device event node: the event types (<see cref="VisionMethods.ListEventTypes"/>) in the
/// shared event select of the event rules (<see cref="EventPicker"/>: folders, search, description); OK takes the chosen
/// one. The dialog shows at once and reads the list while it shows (a ring; a failure below the field, never a silent empty
/// list). A type whose plugin is off shows with "(turned off)". A per-device event then asks for the device.
/// </summary>
public sealed partial class EventTypePickerViewModel : ObservableObject
{
    private readonly string? _selectedId;
    private IReadOnlyList<EventTypeChoice> _types = [];

    /// <summary>The dialog before the list is read (<see cref="LoadAsync"/>).</summary>
    public EventTypePickerViewModel(string? selectedId)
    {
        _selectedId = selectedId;
        IsLoading = true;
    }

    /// <summary>The dialog with the list read already.</summary>
    public EventTypePickerViewModel(IReadOnlyList<EventTypeChoice> types, string? selectedId)
        : this(selectedId) => Fill(types);

    /// <summary>OK (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public static string Title => VisionTexts.ChooseEvent;

    public static string LoadingText => VisionTexts.ReadingEvents;

    /// <summary>The events in their folders (null while reading).</summary>
    [ObservableProperty]
    public partial EventTree? Tree { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial EventChoice? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why there is no event to choose: the list could not be read, or it is empty. Below the field.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>The chosen event type after OK.</summary>
    public EventTypeChoice? Chosen => Selected is { } chosen ? _types.FirstOrDefault(t => t.Id == chosen.Id) : null;

    /// <summary>Reads the list while the dialog shows; a failure says why below the field.</summary>
    public async Task LoadAsync(Func<CancellationToken, Task<IReadOnlyList<EventTypeChoice>>> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        IsLoading = true;
        try
        {
            Fill(await read(ct).ConfigureAwait(true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The dialog closed meanwhile.
        }
#pragma warning disable CA1031 // The server's reason below the field.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Problem = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static bool IsOwn(EventTypeChoice type) =>
        type.Id.StartsWith(VisionInfo.PluginId + ".", StringComparison.Ordinal) || (type.Path.Count > 0 && type.Path[0] == VisionInfo.EventGroup);

    private void Fill(IReadOnlyList<EventTypeChoice> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        // Vision Center's own events never go into one of its nodes (the server leaves them out too).
        _types = [.. types.Where(t => !IsOwn(t))];
        var choices = _types.Select(t => new EventChoice(t.Id, t.Name, t.Path, t.Available)).ToList();
        Tree = new EventTree(choices);
        Selected = choices.FirstOrDefault(c => c.Id == _selectedId);
        Problem = _types.Count == 0 ? VisionTexts.NoEvents : null;
        IsLoading = false;
    }

    private bool CanOk() => !IsLoading && Selected is not null;

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
