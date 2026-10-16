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

    /// <summary>"Active (yes / no) · Temperature (number)"; empty for an event without values.</summary>
    public string FieldsText => Event is null ? string.Empty
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

    ICommand? ITileGridItem.CopyCommand => CopyForDevicesCommand;

    /// <summary>A widget with a device can be copied for other devices (the copy icon of its frame, and the panel).</summary>
    public bool CanCopy => Widget.DeviceId is { } id && id != Guid.Empty;

    /// <summary>The copy icon: the device picker, then one copy per chosen device.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyForDevicesAsync()
    {
        var devices = await _owner.PickCopyDevicesAsync(this).ConfigureAwait(true);
        if (devices.Count > 0)
        {
            _owner.CopyFor(this, devices);
        }
    }

    /// <summary>Value widgets are transparent on the card; video keeps the dark video background.</summary>
    string? ITileGridItem.FrameClass => Widget.IsValue ? "valueTile" : null;

    /// <summary>New settings (the panel, a move): the grid and the widget redraw.</summary>
    internal void Update(Widget widget)
    {
        var keyChanged = widget.TypeId != Widget.TypeId || widget.DeviceId != Widget.DeviceId;
        Widget = widget;
        OnPropertyChanged(nameof(Widget));
        OnPropertyChanged(nameof(Rect));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(CanCopy));
        CopyForDevicesCommand.NotifyCanExecuteChanged();
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

    string ITileGridSource.CopyText => "Copy for other devices";

    internal Task<IReadOnlyList<IDeviceInfo>> PickCopyDevicesAsync(WidgetViewModel widget) => _owner.PickCopyDevicesAsync([widget]);

    internal Task<IReadOnlyList<IDeviceInfo>> PickCopyDevicesAsync(IReadOnlyList<WidgetViewModel> widgets) => _owner.PickCopyDevicesAsync(widgets);

    /// <summary>
    /// A selected widget dragged with others selected moves them all (inside) or resizes them all (an edge or a corner);
    /// anything else moves or resizes the one.
    /// </summary>
    bool ITileGridSource.TryMove(ITileGridItem item, TileRect rect)
    {
        if (item is not WidgetViewModel widget)
        {
            return false;
        }

        if (_selected.Count > 1 && _selected.Contains(widget))
        {
            var current = widget.Rect;
            return rect.Width == current.Width && rect.Height == current.Height
                ? TryMoveSelection(rect.X - current.X, rect.Y - current.Y)
                : TryResizeSelection(rect.X - current.X, rect.Width - current.Width, rect.Y - current.Y, rect.Height - current.Height);
        }

        return TryFit(widget, rect);
    }

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

    private List<WidgetViewModel> _selected = [];

    /// <summary>The selected widgets (editing only): one shows its panel, several the group panel.</summary>
    public IReadOnlyList<WidgetViewModel> Selection => _selected;

    /// <summary>The panel of the one selected widget.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor), nameof(Panel), nameof(HasPanel))]
    public partial WidgetEditorViewModel? Editor { get; private set; }

    /// <summary>The panel of several selected widgets: what they share.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Panel), nameof(HasPanel))]
    public partial GroupEditorViewModel? GroupEditor { get; private set; }

    public bool HasEditor => Editor is not null;

    /// <summary>The panel right of the grid: one widget's or the group's.</summary>
    public object? Panel => (object?)Editor ?? GroupEditor;

    public bool HasPanel => Panel is not null;

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
        GroupEditor?.ShowAllErrors();
    }

    /// <summary>The panel's errors block Save too.</summary>
    internal bool HasAnyErrors => HasErrors || (Editor?.HasAnyErrors ?? false) || (GroupEditor?.HasErrors ?? false);

    /// <summary>An event dropped on cell (x, y): a value widget there, selected for its panel; false and the reason when there is no room.</summary>
    public bool DropEvent(CatalogEvent catalogEvent, int cellX, int cellY) =>
        AddValue(catalogEvent, DashboardsPluginInfo.Layout.Place(Rects(), cellX, cellY));

    /// <summary>A click on an event while editing: the first free place.</summary>
    public bool AddEvent(CatalogEvent catalogEvent) => AddValue(catalogEvent, DashboardsPluginInfo.Layout.PlaceFirstFree(Rects()));

    /// <summary>
    /// Add cameras: a live video widget per camera, each in the first free place, at most 16 live videos. Adds as many as
    /// fit and says why the rest did not; one camera is selected for its panel. Returns how many were added.
    /// </summary>
    public int AddCameras(IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var added = new List<WidgetViewModel>();
        string? reason = null;
        var wanted = devices.DistinctBy(d => d.Id).ToList();

        // Several cameras share the room: 16 take 5 x 5 each, one up to 8 x 8; smaller where the grid is full.
        var room = Math.Max(1, Math.Min(wanted.Count, DashboardsPluginInfo.MaxVideos - Widgets.Count(w => w.Widget.Kind == WidgetKind.Video)));
        var size = Math.Clamp(DashboardsPluginInfo.Layout.Size / (int)Math.Ceiling(Math.Sqrt(room)), DashboardsPluginInfo.Layout.MinTileSize, DashboardsPluginInfo.VideoLayout.DropSize);
        foreach (var device in wanted)
        {
            if (Widgets.Count(w => w.Widget.Kind == WidgetKind.Video) >= DashboardsPluginInfo.MaxVideos)
            {
                reason = DashboardRules.TooManyVideos;
                break;
            }

            if (Widgets.Count >= DashboardsPluginInfo.MaxWidgets)
            {
                reason = DashboardRules.TooManyWidgets;
                break;
            }

            if (PlaceCamera(size) is not { } place)
            {
                reason = DashboardRules.NoRoom;
                break;
            }

            var model = _owner.CreateWidget(this, new Widget
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = WidgetKind.Video,
                X = place.X,
                Y = place.Y,
                Width = place.Width,
                Height = place.Height,
                DeviceId = device.Id,
                Name = device.Address,
            }, device);
            Widgets.Add(model);
            added.Add(model);
        }

        Error = reason is null ? null : DashboardRules.CamerasAdded(added.Count, wanted.Count, reason);
        if (added.Count > 0)
        {
            Changed();
            SetSelection(added);
        }

        return added.Count;
    }

    /// <summary>The first free square of <paramref name="size"/> cells, else a smaller one; null when nothing is free.</summary>
    private TileRect? PlaceCamera(int size)
    {
        for (var side = size; side >= DashboardsPluginInfo.Layout.MinTileSize; side--)
        {
            if (DashboardsPluginInfo.Layout.PlaceSized(Rects(), side, side) is { } place)
            {
                return place;
            }
        }

        return null;
    }

    /// <summary>Where a dragged event would land (the frame while dragging); null without room.</summary>
    public TileRect? PreviewAt(int cellX, int cellY) => DashboardsPluginInfo.Layout.Place(Rects(), cellX, cellY);

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

    /// <summary>A click on a widget while editing: it alone is selected and its panel shows (null: nothing selected).</summary>
    public void Select(WidgetViewModel? widget) => SetSelection(widget is null ? [] : [widget]);

    /// <summary>Ctrl+click: the widget joins the selection or leaves it.</summary>
    public void Toggle(WidgetViewModel widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        SetSelection(_selected.Contains(widget) ? _selected.Where(w => w != widget) : _selected.Append(widget));
    }

    /// <summary>The selection rectangle: every widget it touches; with Ctrl they join the selection.</summary>
    public void SelectArea(TileRect area, bool add)
    {
        var inside = Widgets.Where(w => w.Rect.Overlaps(area));
        SetSelection(add ? _selected.Concat(inside) : inside);
    }

    /// <summary>Ctrl+A: every widget.</summary>
    public void SelectAll() => SetSelection(Widgets);

    private void SetSelection(IEnumerable<WidgetViewModel> widgets)
    {
        var next = widgets.Where(Widgets.Contains).Distinct().ToList();
        if (!IsEditing || (next.Count == _selected.Count && next.All(_selected.Contains)))
        {
            return;
        }

        Editor?.Detach();
        GroupEditor?.Detach();
        foreach (var widget in _selected)
        {
            widget.IsSelected = false;
        }

        _selected = next;
        foreach (var widget in next)
        {
            widget.IsSelected = true;
        }

        Editor = next.Count == 1 ? _owner.CreateEditor(this, next[0]) : null;
        GroupEditor = next.Count > 1 ? new GroupEditorViewModel(this, next) : null;
        OnPropertyChanged(nameof(Selection));
    }

    /// <summary>Moves every selected widget by whole cells, stopping at the grid's edge; false when one would hit another widget.</summary>
    public bool TryMoveSelection(int dx, int dy)
    {
        if (_selected.Count == 0)
        {
            return false;
        }

        var size = DashboardsPluginInfo.Layout.Size;
        dx = Math.Clamp(dx, -_selected.Min(w => w.Rect.X), size - _selected.Max(w => w.Rect.X + w.Rect.Width));
        dy = Math.Clamp(dy, -_selected.Min(w => w.Rect.Y), size - _selected.Max(w => w.Rect.Y + w.Rect.Height));
        if (dx == 0 && dy == 0)
        {
            return false;
        }

        var others = Widgets.Except(_selected).Select(w => w.Rect).ToList();
        var moved = _selected.Select(w => (Widget: w, Rect: w.Rect with { X = w.Rect.X + dx, Y = w.Rect.Y + dy })).ToList();
        if (moved.Any(m => others.Any(o => o.Overlaps(m.Rect))))
        {
            return false;
        }

        foreach (var (widget, rect) in moved)
        {
            widget.Update(widget.Widget with { X = rect.X, Y = rect.Y });
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// An edge or corner of one selected widget dragged: every selected widget grows or shrinks by the same cells
    /// (<paramref name="dw"/>, <paramref name="dh"/>; <paramref name="dx"/> / <paramref name="dy"/> not zero = the left / top
    /// edge moved). Selected widgets side by side make room for each other: the right edge pushes the ones to the right
    /// further, the left edge the ones to the left (the same for rows). All or nothing: false when a widget would leave the
    /// grid, get smaller than 2 x 2 or hit another widget.
    /// </summary>
    public bool TryResizeSelection(int dx, int dw, int dy, int dh)
    {
        if (_selected.Count == 0 || (dw == 0 && dh == 0))
        {
            return false;
        }

        static bool RowsOverlap(TileRect a, TileRect b) => a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
        static bool ColumnsOverlap(TileRect a, TileRect b) => a.X < b.X + b.Width && b.X < a.X + a.Width;
        var rects = _selected.Select(w => w.Rect).ToList();
        var next = new List<TileRect>(rects.Count);
        foreach (var r in rects)
        {
            var left = rects.Count(o => o != r && o.X + o.Width <= r.X && RowsOverlap(o, r));
            var right = rects.Count(o => o != r && o.X >= r.X + r.Width && RowsOverlap(o, r));
            var above = rects.Count(o => o != r && o.Y + o.Height <= r.Y && ColumnsOverlap(o, r));
            var below = rects.Count(o => o != r && o.Y >= r.Y + r.Height && ColumnsOverlap(o, r));
            var x = dx != 0 ? r.X - (dw * (1 + right)) : r.X + (dw * left);
            var y = dy != 0 ? r.Y - (dh * (1 + below)) : r.Y + (dh * above);
            next.Add(new TileRect(x, y, r.Width + dw, r.Height + dh));
        }

        var others = Widgets.Except(_selected).Select(w => w.Rect).ToList();
        if (next.Any(r => !DashboardsPluginInfo.Layout.Fits(r) || others.Any(o => o.Overlaps(r))) || TileLayout.FirstOverlap(next) >= 0)
        {
            return false;
        }

        for (var i = 0; i < _selected.Count; i++)
        {
            var r = next[i];
            _selected[i].Update(_selected[i].Widget with { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height });
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// The group panel's Width / Height: every selected widget that has room takes the size (kept where it is, moved left or
    /// up only at the grid's edge); the others keep theirs and the reason shows below the toolbar.
    /// </summary>
    public int ResizeSelection(int? width, int? height)
    {
        var changed = 0;
        var refused = 0;
        foreach (var widget in _selected)
        {
            var others = Widgets.Where(o => o != widget).Select(o => o.Rect).ToList();
            if (DashboardsPluginInfo.Layout.Fit(others, widget.Rect.X, widget.Rect.Y, width ?? widget.Rect.Width, height ?? widget.Rect.Height) is not { } rect)
            {
                refused++;
                continue;
            }

            if (rect != widget.Rect)
            {
                widget.Update(widget.Widget with { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height });
                changed++;
            }
        }

        Error = refused > 0 ? DashboardRules.Resized(_selected.Count - refused, _selected.Count) : null;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>The group panel's remove: every selected widget.</summary>
    public void RemoveSelection()
    {
        foreach (var widget in _selected.ToList())
        {
            RemoveWidget(widget);
        }
    }

    internal void RemoveWidget(WidgetViewModel widget)
    {
        if (Widgets.Remove(widget))
        {
            if (_selected.Contains(widget))
            {
                SetSelection(_selected.Where(w => w != widget));
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
        GroupEditor = null;
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

    /// <summary>Copies of one widget for other devices (see the overload for several).</summary>
    public int CopyFor(WidgetViewModel source, IReadOnlyList<IDeviceInfo> devices) => CopyFor([source], devices);

    /// <summary>
    /// Copies of <paramref name="sources"/> for other devices: per device a copy of each widget (not for its own device), the
    /// same settings and size, each in the first free place, its name with the device's address instead of the source's
    /// (else " · address" after it). Adds as many as fit and says why the rest did not; returns how many were added.
    /// </summary>
    public int CopyFor(IReadOnlyList<WidgetViewModel> sources, IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(devices);
        var pairs = devices.DistinctBy(d => d.Id)
            .SelectMany(d => sources.Where(s => s.CanCopy && s.Widget.DeviceId != d.Id).Select(s => (Device: d, Source: s)))
            .ToList();
        var added = 0;
        string? reason = null;
        foreach (var (device, source) in pairs)
        {
            if (source.Widget.Kind == WidgetKind.Video && Widgets.Count(w => w.Widget.Kind == WidgetKind.Video) >= DashboardsPluginInfo.MaxVideos)
            {
                reason = DashboardRules.TooManyVideos;
                break;
            }

            if (Widgets.Count >= DashboardsPluginInfo.MaxWidgets)
            {
                reason = DashboardRules.TooManyWidgets;
                break;
            }

            if (DashboardsPluginInfo.Layout.PlaceSized(Rects(), source.Rect.Width, source.Rect.Height) is not { } place)
            {
                reason = DashboardRules.NoRoom;
                break;
            }

            var copy = source.Widget with
            {
                Id = Guid.NewGuid().ToString("N"),
                X = place.X,
                Y = place.Y,
                Width = place.Width,
                Height = place.Height,
                DeviceId = device.Id,
                Camera = source.Widget.Kind == WidgetKind.Video ? 0 : source.Widget.Camera,
                Name = CopyName(source, device),
            };
            Widgets.Add(_owner.CreateWidget(this, copy, device));
            added++;
        }

        Error = reason is null ? null : DashboardRules.CopiesAdded(added, pairs.Count, reason);
        if (added > 0)
        {
            Changed();
        }

        return added;
    }

    private static string CopyName(WidgetViewModel source, IDeviceInfo device)
    {
        var name = source.Widget.Name;
        if (source.Device?.Address is { Length: > 0 } address && name.Contains(address, StringComparison.Ordinal))
        {
            name = name.Replace(address, device.Address, StringComparison.Ordinal);
        }
        else
        {
            name = string.IsNullOrWhiteSpace(name) ? device.Address : name + " · " + device.Address;
        }

        return name.Length <= DashboardsPluginInfo.MaxNameLength ? name : name[..DashboardsPluginInfo.MaxNameLength];
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
        GroupEditor?.Detach();
        foreach (var widget in Widgets)
        {
            widget.Dispose();
        }

        Widgets.Clear();
    }

    /// <summary>"View 3" style default names: "Dashboard 3".</summary>
    internal static string DefaultName(int n) => string.Create(CultureInfo.InvariantCulture, $"Dashboard {n}");
}
