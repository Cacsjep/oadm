using System.Collections.ObjectModel;
using System.Windows.Input;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Layout;

namespace Oadm.Plugins.LiveView.Client;

/// <summary>A saved view in the list on the left: a click opens its tab.</summary>
public sealed partial class ViewEntry(SavedView view) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial SavedView View { get; set; } = view;

    public string Id => View.Id;

    public string Name => View.Name;

    /// <summary>The view's tab is the active one.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>
/// One camera of an open view: its place in the grid and the shared live video (none without the host's live video or
/// the "Live view" right). A removed device keeps its place and says so.
/// </summary>
public sealed partial class TileViewModel : ObservableObject, ITileGridItem, IDisposable
{
    private readonly ViewTab _owner;

    internal TileViewModel(ViewTab owner, ViewTile tile, IDeviceInfo? device)
    {
        _owner = owner;
        DeviceId = tile.DeviceId;
        Camera = tile.Camera;
        X = tile.X;
        Y = tile.Y;
        Width = tile.Width;
        Height = tile.Height;
        Device = device;
    }

    public Guid DeviceId { get; }

    /// <summary>The channel; the channel buttons change it, saved while the view is edited.</summary>
    public int Camera { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rect))]
    public partial int X { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rect))]
    public partial int Y { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rect))]
    public partial int Width { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rect))]
    public partial int Height { get; private set; }

    /// <summary>The tile's cells in the grid.</summary>
    public TileRect Rect => new(X, Y, Width, Height);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsMissing), nameof(Content), nameof(Message))]
    public partial IDeviceInfo? Device { get; internal set; }

    /// <summary>OADM no longer manages the device: the tile stays and says so.</summary>
    public bool IsMissing => Device is null;

    /// <summary>The address (as in the device list), or "Removed device 1a2b3c4d".</summary>
    public string Title => Device?.Address ?? DeviceTexts.Removed(DeviceId.ToString("N"));

    /// <summary>The picture; null without live video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Content), nameof(Message))]
    public partial ILiveVideo? Video { get; internal set; }

    /// <summary>The live video while the device is managed.</summary>
    public Control? Content => IsMissing ? null : Video?.View;

    /// <summary>The removed device, or the address while there is no video.</summary>
    public string? Message => Content is null ? Title : null;

    ICommand? ITileGridItem.RemoveCommand => RemoveCommand;

    public ViewTab Owner => _owner;

    public ViewTile ToTile() => new(DeviceId, Camera, X, Y, Width, Height);

    internal void Apply(ViewTile tile)
    {
        X = tile.X;
        Y = tile.Y;
        Width = tile.Width;
        Height = tile.Height;
    }

    /// <summary>The X of the tile while editing.</summary>
    [RelayCommand]
    private void Remove() => _owner.RemoveTile(this);

    public void Dispose()
    {
        if (Video is { } video)
        {
            video.CameraChosen -= OnCameraChosen;
            video.Dispose();
            Video = null;
        }
    }

    internal void Attach(ILiveVideo video)
    {
        Video = video;
        video.CameraChosen += OnCameraChosen;
    }

    private void OnCameraChosen(object? sender, int camera)
    {
        if (_owner.IsEditing)
        {
            Camera = camera; // saved with the view
        }
    }
}

/// <summary>
/// One open view (a tab): its cameras in the grid, and while editing its name, the changes and Save / Cancel. A new view is
/// a tab in edit mode until it is saved.
/// </summary>
public sealed partial class ViewTab : ValidatingViewModel, ITileGridSource, IDisposable
{
    private readonly LiveViewPageViewModel _owner;

    internal ViewTab(LiveViewPageViewModel owner, SavedView? saved, string name)
    {
        _owner = owner;
        Saved = saved;
        Name = name;
        Validation.Rule(nameof(Name), () => ViewRules.NameProblem(Name) ?? _owner.NameTakenProblem(this, Name));
        Load(saved?.Tiles ?? []);
    }

    /// <summary>One camera alone in its tab, filling it: a click on a camera outside editing. Never edited or saved.</summary>
    internal ViewTab(LiveViewPageViewModel owner, IDeviceInfo device)
        : this(owner, null, device.Address)
    {
        SingleCamera = device.Id;
        Load([new ViewTile(device.Id, 0, 0, 0, LiveViewPluginInfo.GridSize, LiveViewPluginInfo.GridSize)]);
    }

    /// <summary>The camera of a single-camera tab; null for a view.</summary>
    public Guid? SingleCamera { get; }

    public bool IsSingleCamera => SingleCamera is not null;

    /// <summary>The stored view; null until a new view is saved.</summary>
    public SavedView? Saved { get; private set; }

    public string? Id => Saved?.Id;

    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    IReadOnlyList<ITileGridItem> ITileGridSource.Items => Tiles;

    TileLayout ITileGridSource.Layout => LiveViewPluginInfo.Layout;

    string ITileGridSource.RemoveText => "Remove camera";

    bool ITileGridSource.TryMove(ITileGridItem item, TileRect rect) =>
        item is TileViewModel tile && TryFit(tile, rect.X, rect.Y, rect.Width, rect.Height);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; set; }

    public string Title => string.IsNullOrWhiteSpace(Name) ? "New view" : Name.Trim();

    public string ToolTip => ViewRules.CameraCount(Tiles.Count);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    public partial bool IsEditing { get; internal set; }

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    /// <summary>A failed save or a refused drop: below the view's toolbar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; internal set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    public partial bool IsSaving { get; internal set; }

    /// <summary>"Drag cameras here" over an empty grid while editing.</summary>
    public bool ShowEmptyHint => IsEditing && Tiles.Count == 0;

    /// <summary>The grid moved a camera, added or removed one: the view redraws.</summary>
    public event EventHandler? LayoutChanged;

    [RelayCommand]
    private void Activate() => _owner.ActiveTab = this;

    [RelayCommand]
    private async Task Close() => await _owner.CloseTabAsync(this).ConfigureAwait(true);

    /// <summary>Shows every field error (Save was tried).</summary>
    internal void ShowAllErrors() => Validation.ShowAll();

    /// <summary>A camera dropped on cell (x, y); false and the reason in <see cref="Error"/> when there is no room.</summary>
    public bool Drop(IDeviceInfo device, int cellX, int cellY)
    {
        ArgumentNullException.ThrowIfNull(device);
        var tile = ViewLayout.Place(TileList(), device.Id, 0, cellX, cellY);
        return Add(tile, device);
    }

    /// <summary>A click on a camera while editing: the first free place.</summary>
    public bool AddToFirstFree(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Add(ViewLayout.PlaceFirstFree(TileList(), device.Id, 0), device);
    }

    /// <summary>Moves or resizes a camera; false (nothing changes) when it would leave the grid's rules.</summary>
    public bool TryFit(TileViewModel tile, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var others = Tiles.Where(t => t != tile).Select(t => t.ToTile()).ToList();
        if (ViewLayout.Fit(others, tile.ToTile(), x, y, width, height) is not { } fitted)
        {
            return false;
        }

        tile.Apply(fitted);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void RemoveTile(TileViewModel tile)
    {
        if (Tiles.Remove(tile))
        {
            tile.Dispose();
            Changed();
        }
    }

    public IReadOnlyList<ViewTile> TileList() => [.. Tiles.Select(t => t.ToTile())];

    internal SaveViewRequest ToRequest() => new(Saved?.Id, Name.Trim(), Saved?.Version ?? 0, TileList());

    /// <summary>The stored view after a save, or a change by another user while the tab is not edited.</summary>
    internal void ApplySaved(SavedView saved)
    {
        Saved = saved;
        OnPropertyChanged(nameof(Id));
        if (!IsEditing)
        {
            Name = saved.Name;
            Load(saved.Tiles);
        }
    }

    /// <summary>Cancel: back to the stored view (a new one closes).</summary>
    internal void Revert()
    {
        IsEditing = false;
        Error = null;
        if (Saved is { } saved)
        {
            Name = saved.Name;
            Load(saved.Tiles);
        }

        Validation.Reset();
    }

    /// <summary>The tiles of <paramref name="tiles"/>; tiles of the same camera keep their picture.</summary>
    private void Load(IReadOnlyList<ViewTile> tiles)
    {
        var old = Tiles.ToList();
        Tiles.Clear();
        foreach (var tile in tiles)
        {
            var reuse = old.FirstOrDefault(t => t.DeviceId == tile.DeviceId && t.Camera == tile.Camera);
            if (reuse is not null)
            {
                old.Remove(reuse);
                reuse.Apply(tile);
                Tiles.Add(reuse);
            }
            else
            {
                Tiles.Add(_owner.CreateTile(this, tile));
            }
        }

        foreach (var gone in old)
        {
            gone.Dispose();
        }

        Changed();
    }

    private bool Add(ViewTile? tile, IDeviceInfo device)
    {
        if (tile is null)
        {
            Error = Tiles.Count >= LiveViewPluginInfo.MaxTiles ? ViewRules.TooManyTiles : ViewRules.NoRoom;
            return false;
        }

        Error = null;
        Tiles.Add(_owner.CreateTile(this, tile, device));
        Changed();
        return true;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(ShowEmptyHint));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RefreshDevices(IReadOnlyDictionary<Guid, IDeviceInfo> devices)
    {
        foreach (var tile in Tiles)
        {
            var device = devices.GetValueOrDefault(tile.DeviceId);
            var wasMissing = tile.IsMissing;
            tile.Device = device;
            if (device is null && tile.Video is not null)
            {
                tile.Video.Close();
            }
            else if (device is not null && wasMissing)
            {
                tile.Video?.Start(device.Id, tile.Camera);
            }
        }
    }

    public void Dispose()
    {
        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }

        Tiles.Clear();
    }
}

/// <summary>The page's texts (one phrase per concept).</summary>
public static class LiveViewTexts
{
    public const string NoViews = "No views yet. Click New view.";

    public const string NoViewsReadOnly = "No views yet.";

    public const string NoCameras = "No cameras yet. Add devices on the Devices page.";

    public const string NoTabs = "Click a view on the left to open it.";

    public const string EmptyView = "Drag cameras from the left into the view, or click a camera to add it.";

    /// <summary>The role lacks "Live view".</summary>
    public const string NotAllowed = "Your role does not allow live view.";

    public const string NotAvailable = "This OADM client cannot show live video. Update the client.";

    public static string LoadFailed(string reason) => "The views could not be read: " + reason;

    public static string DeleteFailed(string reason) => "The view could not be deleted: " + reason;
}
