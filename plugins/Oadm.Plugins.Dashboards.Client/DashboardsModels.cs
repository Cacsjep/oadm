using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Layout;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>A saved dashboard in the list on the left: a click opens its tab.</summary>
public sealed partial class DashboardEntry(Dashboard dashboard) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial Dashboard Dashboard { get; set; } = dashboard;

    public string Id => Dashboard.Id;

    public string Name => Dashboard.Name;

    /// <summary>The dashboard's tab is the active one.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>A row of the Events list: a group heading, or an event with its fields ("Temperature · Number").</summary>
public sealed class EventListItem
{
    private EventListItem(string? header, CatalogEvent? catalogEvent)
    {
        Header = header;
        Event = catalogEvent;
    }

    public string? Header { get; }

    public CatalogEvent? Event { get; }

    public bool IsHeader => Header is not null;

    public bool IsEvent => Event is not null;

    public string Name => Event?.Name ?? Header ?? string.Empty;

    /// <summary>"Active · Temperature (number)", or "Trigger the event once on the device." before its values were seen.</summary>
    public string FieldsText => Event is null ? string.Empty
        : Event.Fields.Count == 0 ? DashboardTexts.TriggerOnce
        : string.Join(" · ", Event.Fields.Select(f => f.Label + " (" + KindText(f.Kind) + ")"));

    public static EventListItem Group(string header) => new(header, null);

    public static EventListItem For(CatalogEvent catalogEvent) => new(null, catalogEvent);

    public static string KindText(EventValueKind kind) => kind switch
    {
        EventValueKind.YesNo => "yes / no",
        EventValueKind.Number => "number",
        _ => "text",
    };
}

/// <summary>
/// One widget of an open dashboard: its place in the grid, its settings and what it shows now. A value widget draws a
/// <see cref="WidgetView"/>; a video widget is the host's live video component (none without it or the "Live view"
/// right). A removed device keeps its place and says so.
/// </summary>
public sealed partial class WidgetViewModel : ObservableObject, ITileGridItem, IDisposable
{
    private readonly DashboardTab _owner;
    private readonly WidgetView? _view;

    internal WidgetViewModel(DashboardTab owner, Widget widget, IDeviceInfo? device)
    {
        _owner = owner;
        Widget = widget;
        Device = device;
        if (widget.IsValue)
        {
            _view = new WidgetView();
            _view.Show(widget, Display);
        }
    }

    /// <summary>The settings; every change of the panel replaces it.</summary>
    public Widget Widget { get; private set; }

    public string Id => Widget.Id;

    public TileRect Rect => Widget.Rect;

    /// <summary>The device of the widget (also of a video widget); null for a server event or a removed device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message), nameof(Content), nameof(Title))]
    public partial IDeviceInfo? Device { get; internal set; }

    /// <summary>The picture of a video widget; null without live video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Content), nameof(Message))]
    public partial ILiveVideo? Video { get; internal set; }

    /// <summary>The panel shows this widget (its frame is marked while editing).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    /// <summary>The latest value from the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    public partial ValueSnapshot? Value { get; private set; }

    /// <summary>What the widget shows now.</summary>
    public WidgetDisplay Display => WidgetDisplay.Of(Widget, Value);

    /// <summary>OADM no longer manages the widget's device.</summary>
    public bool IsMissing => Widget.DeviceId is { } id && id != Guid.Empty && Device is null;

    /// <summary>The value the widget watches; null for a video widget or one without an event yet.</summary>
    public ValueKey? Key => Widget.IsValue && !string.IsNullOrEmpty(Widget.TypeId) ? new ValueKey(Widget.TypeId, Widget.DeviceId) : null;

    /// <summary>The name in the edit frame: the widget's name, else the device's address or the kind.</summary>
    public string? Title => !string.IsNullOrWhiteSpace(Widget.Name) ? Widget.Name
        : Device?.Address ?? (Widget.Kind == WidgetKind.Video ? "Live video" : "Widget");

    /// <summary>The widget itself, or the live video.</summary>
    public Control? Content => IsMissing ? null
        : Widget.Kind == WidgetKind.Video ? Video?.View
        : _view;

    /// <summary>A removed device, or the reason a video widget shows no picture.</summary>
    public string? Message => IsMissing ? DeviceTexts.Removed((Widget.DeviceId ?? Guid.Empty).ToString("N"))
        : Widget.Kind == WidgetKind.Video && Video is null ? (_owner.PictureProblem ?? Device?.Address)
        : null;

    public DashboardTab Owner => _owner;

    ICommand? ITileGridItem.RemoveCommand => RemoveCommand;

    /// <summary>New settings (the panel, a move): the grid and the widget redraw.</summary>
    internal void Update(Widget widget)
    {
        var keyChanged = widget.TypeId != Widget.TypeId || widget.DeviceId != Widget.DeviceId;
        Widget = widget;
        OnPropertyChanged(nameof(Widget));
        OnPropertyChanged(nameof(Rect));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Key));
        if (keyChanged)
        {
            Value = null;
            _owner.KeysChanged();
        }

        Redraw();
    }

    internal void SetValue(ValueSnapshot? value)
    {
        Value = value;
        Redraw();
    }

    /// <summary>The X of the widget while editing.</summary>
    [RelayCommand]
    private void Remove() => _owner.RemoveWidget(this);

    internal void Attach(ILiveVideo video)
    {
        Video = video;
        video.CameraChosen += OnCameraChosen;
    }

    private void OnCameraChosen(object? sender, int camera)
    {
        if (_owner.IsEditing)
        {
            Widget = Widget with { Camera = camera }; // saved with the dashboard
        }
    }

    /// <summary>Draws the widget again; while editing without its name (the edit frame shows it).</summary>
    internal void Redraw() => _view?.Show(Widget, Display, showName: !_owner.IsEditing);

    public void Dispose()
    {
        if (Video is { } video)
        {
            video.CameraChosen -= OnCameraChosen;
            video.Dispose();
            Video = null;
        }
    }
}

/// <summary>
/// One open dashboard (a tab): its widgets in the grid, and while editing its name, the selected widget's panel, the
/// changes and Save / Cancel. A new dashboard is a tab in edit mode until it is saved.
/// </summary>
public sealed partial class DashboardTab : ValidatingViewModel, ITileGridSource, IDisposable
{
    private readonly DashboardsPageViewModel _owner;

    internal DashboardTab(DashboardsPageViewModel owner, Dashboard? saved, string name)
    {
        _owner = owner;
        Saved = saved;
        Name = name;
        Validation.Rule(nameof(Name), () => DashboardRules.NameProblem(Name) ?? _owner.NameTakenProblem(this, Name));
        Load(saved?.Widgets ?? []);
    }

    /// <summary>The stored dashboard; null until a new one is saved.</summary>
    public Dashboard? Saved { get; private set; }

    public string? Id => Saved?.Id;

    public ObservableCollection<WidgetViewModel> Widgets { get; } = [];

    IReadOnlyList<ITileGridItem> ITileGridSource.Items => Widgets;

    TileLayout ITileGridSource.Layout => DashboardsPluginInfo.Layout;

    string ITileGridSource.RemoveText => "Remove widget";

    bool ITileGridSource.TryMove(ITileGridItem item, TileRect rect) => item is WidgetViewModel widget && TryFit(widget, rect);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; set; }

    public string Title => string.IsNullOrWhiteSpace(Name) ? "New dashboard" : Name.Trim();

    public string ToolTip => DashboardRules.WidgetCount(Widgets.Count);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint), nameof(ShowPanel))]
    public partial bool IsEditing { get; internal set; }

    partial void OnIsEditingChanged(bool value)
    {
        foreach (var widget in Widgets)
        {
            widget.Redraw();
        }
    }

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    /// <summary>A failed save or a refused drop: below the toolbar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; internal set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    public partial bool IsSaving { get; internal set; }

    /// <summary>The widget whose settings the panel shows (editing only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial WidgetEditorViewModel? Editor { get; private set; }

    public bool HasEditor => Editor is not null;

    /// <summary>The panel right of the grid while editing.</summary>
    public bool ShowPanel => IsEditing;

    /// <summary>"Drag an event here" over an empty grid while editing.</summary>
    public bool ShowEmptyHint => IsEditing && Widgets.Count == 0;

    /// <summary>Why video widgets show no picture; null when they can.</summary>
    internal string? PictureProblem => _owner.PictureProblem.Length > 0 ? _owner.PictureProblem : null;

    /// <summary>The grid moved, added or removed a widget: the view redraws.</summary>
    public event EventHandler? LayoutChanged;

    [RelayCommand]
    private void Activate() => _owner.ActiveTab = this;

    [RelayCommand]
    private async Task Close() => await _owner.CloseTabAsync(this).ConfigureAwait(true);

    /// <summary>Shows every field error (Save was tried).</summary>
    internal void ShowAllErrors()
    {
        Validation.ShowAll();
        Editor?.ShowAllErrors();
    }

    /// <summary>The panel's errors block Save too.</summary>
    internal bool HasAnyErrors => HasErrors || (Editor?.HasAnyErrors ?? false);

    /// <summary>An event dropped on cell (x, y): a value widget there, selected for its panel; false and the reason when there is no room.</summary>
    public bool DropEvent(CatalogEvent catalogEvent, int cellX, int cellY) =>
        AddValue(catalogEvent, DashboardsPluginInfo.Layout.Place(Rects(), cellX, cellY));

    /// <summary>A click on an event while editing: the first free place.</summary>
    public bool AddEvent(CatalogEvent catalogEvent) => AddValue(catalogEvent, DashboardsPluginInfo.Layout.PlaceFirstFree(Rects()));

    /// <summary>A camera dropped on cell (x, y): a live video widget.</summary>
    public bool DropCamera(IDeviceInfo device, int cellX, int cellY) =>
        AddVideo(device, DashboardsPluginInfo.VideoLayout.Place(Rects(), cellX, cellY));

    /// <summary>A click on a camera while editing: the first free place.</summary>
    public bool AddCamera(IDeviceInfo device) => AddVideo(device, DashboardsPluginInfo.VideoLayout.PlaceFirstFree(Rects()));

    /// <summary>Where a dragged item would land (the frame while dragging); null without room.</summary>
    public TileRect? PreviewAt(bool camera, int cellX, int cellY) =>
        (camera ? DashboardsPluginInfo.VideoLayout : DashboardsPluginInfo.Layout).Place(Rects(), cellX, cellY);

    /// <summary>Moves or resizes a widget; false (nothing changes) when it would leave the grid's rules.</summary>
    public bool TryFit(WidgetViewModel widget, TileRect rect)
    {
        ArgumentNullException.ThrowIfNull(widget);
        var others = Widgets.Where(w => w != widget).Select(w => w.Rect).ToList();
        if (DashboardsPluginInfo.Layout.Fit(others, rect.X, rect.Y, rect.Width, rect.Height) is not { } fitted)
        {
            return false;
        }

        widget.Update(widget.Widget with { X = fitted.X, Y = fitted.Y, Width = fitted.Width, Height = fitted.Height });
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>A click on a widget while editing: its panel.</summary>
    public void Select(WidgetViewModel? widget)
    {
        if (!IsEditing || widget == Editor?.Target)
        {
            return;
        }

        Editor?.Detach();
        if (Editor is { } old)
        {
            old.Target.IsSelected = false;
        }

        Editor = widget is null ? null : _owner.CreateEditor(this, widget);
        if (widget is not null)
        {
            widget.IsSelected = true;
        }
    }

    internal void RemoveWidget(WidgetViewModel widget)
    {
        if (Widgets.Remove(widget))
        {
            if (Editor?.Target == widget)
            {
                Select(null);
            }

            widget.Dispose();
            Changed();
        }
    }

    public IReadOnlyList<Widget> WidgetList() => [.. Widgets.Select(w => w.Widget)];

    internal SaveDashboardRequest ToRequest() => new(Saved?.Id, Name.Trim(), Saved?.Version ?? 0, WidgetList());

    /// <summary>The stored dashboard after a save, or a change by another user while the tab is not edited.</summary>
    internal void ApplySaved(Dashboard saved)
    {
        Saved = saved;
        OnPropertyChanged(nameof(Id));
        if (!IsEditing)
        {
            Name = saved.Name;
            Load(saved.Widgets);
        }
    }

    /// <summary>Editing ends (saved or cancelled): the panel closes.</summary>
    internal void EndEditing()
    {
        Select(null);
        Editor = null;
        IsEditing = false;
    }

    /// <summary>Cancel: back to the stored dashboard (a new one closes).</summary>
    internal void Revert()
    {
        EndEditing();
        Error = null;
        if (Saved is { } saved)
        {
            Name = saved.Name;
            Load(saved.Widgets);
        }

        Validation.Reset();
    }

    /// <summary>The widgets of <paramref name="widgets"/>; video widgets of the same camera keep their picture.</summary>
    private void Load(IReadOnlyList<Widget> widgets)
    {
        var old = Widgets.ToList();
        Widgets.Clear();
        foreach (var widget in widgets)
        {
            var reuse = old.FirstOrDefault(w => w.Id == widget.Id && w.Widget.Kind == widget.Kind && w.Widget.DeviceId == widget.DeviceId);
            if (reuse is not null)
            {
                old.Remove(reuse);
                reuse.Update(widget);
                Widgets.Add(reuse);
            }
            else
            {
                Widgets.Add(_owner.CreateWidget(this, widget));
            }
        }

        foreach (var gone in old)
        {
            gone.Dispose();
        }

        Changed();
    }

    private bool AddValue(CatalogEvent catalogEvent, TileRect? rect)
    {
        ArgumentNullException.ThrowIfNull(catalogEvent);
        if (rect is not { } place)
        {
            Error = Widgets.Count >= DashboardsPluginInfo.MaxWidgets ? DashboardRules.TooManyWidgets : DashboardRules.NoRoom;
            return false;
        }

        // The first field and the widget that fits it; the panel chooses the device and the rest.
        var field = catalogEvent.Fields.Count > 0 ? catalogEvent.Fields[0] : null;
        var kind = field is null ? WidgetKind.Text : FieldValues.WidgetsFor(field.Kind)[0];
        var widget = new Widget
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            X = place.X,
            Y = place.Y,
            Width = place.Width,
            Height = place.Height,
            TypeId = catalogEvent.TypeId,
            Field = field?.Name,
            Name = field is null ? catalogEvent.Name : field.Label,
        };
        Error = null;
        var model = _owner.CreateWidget(this, widget);
        Widgets.Add(model);
        Changed();
        Select(model);
        return true;
    }

    private bool AddVideo(IDeviceInfo device, TileRect? rect)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (rect is not { } place || Widgets.Count(w => w.Widget.Kind == WidgetKind.Video) >= DashboardsPluginInfo.MaxVideos)
        {
            Error = Widgets.Count(w => w.Widget.Kind == WidgetKind.Video) >= DashboardsPluginInfo.MaxVideos ? DashboardRules.TooManyVideos
                : Widgets.Count >= DashboardsPluginInfo.MaxWidgets ? DashboardRules.TooManyWidgets
                : DashboardRules.NoRoom;
            return false;
        }

        var widget = new Widget
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = WidgetKind.Video,
            X = place.X,
            Y = place.Y,
            Width = place.Width,
            Height = place.Height,
            DeviceId = device.Id,
            Name = device.Address,
        };
        Error = null;
        var model = _owner.CreateWidget(this, widget, device);
        Widgets.Add(model);
        Changed();
        Select(model);
        return true;
    }

    private List<TileRect> Rects() => [.. Widgets.Select(w => w.Rect)];

    private void Changed()
    {
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(ShowEmptyHint));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        KeysChanged();
    }

    /// <summary>The values this tab needs changed: the page watches the new set.</summary>
    internal void KeysChanged() => _owner.WatchChanged();

    internal void RefreshDevices(IReadOnlyDictionary<Guid, IDeviceInfo> devices)
    {
        foreach (var widget in Widgets)
        {
            var device = widget.Widget.DeviceId is { } id ? devices.GetValueOrDefault(id) : null;
            var wasMissing = widget.IsMissing;
            widget.Device = device;
            if (widget.Widget.Kind != WidgetKind.Video)
            {
                continue;
            }

            if (device is null && widget.Video is not null)
            {
                widget.Video.Close();
            }
            else if (device is not null && wasMissing)
            {
                widget.Video?.Start(device.Id, widget.Widget.Camera);
            }
        }
    }

    public void Dispose()
    {
        Editor?.Detach();
        foreach (var widget in Widgets)
        {
            widget.Dispose();
        }

        Widgets.Clear();
    }

    /// <summary>"View 3" style default names: "Dashboard 3".</summary>
    internal static string DefaultName(int n) => string.Create(CultureInfo.InvariantCulture, $"Dashboard {n}");
}
