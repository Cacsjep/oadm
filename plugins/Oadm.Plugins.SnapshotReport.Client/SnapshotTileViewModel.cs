using System.Globalization;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Plugins.SnapshotReport.Client;

public enum TileState
{
    /// <summary>Waiting for its turn (lazy loading).</summary>
    Waiting,
    Loading,
    Ok,
    Error,
}

/// <summary>One picture of the grid: a video source of a device with its latest snapshot.</summary>
public sealed partial class SnapshotTileViewModel : ObservableObject, IDisposable
{
    private readonly Func<SnapshotTileViewModel, Task> _refresh;
    private readonly Action<SnapshotTileViewModel> _preview;

    public SnapshotTileViewModel(SnapshotTile tile, Func<SnapshotTileViewModel, Task> refresh, Action<SnapshotTileViewModel> preview)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(preview);
        Tile = tile;
        _refresh = refresh;
        _preview = preview;
        if (tile.Error is not null)
        {
            SetError(tile.Error);
        }
    }

    public SnapshotTile Tile { get; }

    public Guid DeviceId => Tile.Device.DeviceId;

    public int Camera => Tile.Camera;

    /// <summary>"10.0.0.48" or "10.0.0.48 - Sensor 2".</summary>
    public string Title => Tile.Title;

    /// <summary>"AXIS P3265-V · 12.11.77 · B8A44F631339".</summary>
    public string Facts => FactsText.Line(Tile.Device);

    /// <summary>Text the search box matches against.</summary>
    public string SearchText => string.Join(' ', Title, Facts, Tile.Device.HostName, FactsText.Status(Tile.Device.Status));

    /// <summary>The device itself cannot deliver sources (offline, credentials); refreshing the tile re-reads the sources.</summary>
    public bool IsDeviceError => Tile.Error is not null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial IImage? Image { get; private set; }

    /// <summary>The JPEG of <see cref="Image"/> (the preview decodes it at full size).</summary>
    public byte[]? Jpeg { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsError), nameof(StatusText), nameof(ShowStatus))]
    public partial TileState State { get; private set; } = TileState.Waiting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureText))]
    public partial DateTimeOffset? CapturedUtc { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureText))]
    public partial string? Resolution { get; private set; }

    public bool HasImage => Image is not null;

    public bool IsLoading => State is TileState.Loading or TileState.Waiting;

    public bool IsError => State == TileState.Error;

    /// <summary>Status line under the picture: shown while loading and for errors.</summary>
    public bool ShowStatus => State != TileState.Ok;

    public string StatusText => State switch
    {
        TileState.Waiting => "Waiting...",
        TileState.Loading => "Loading...",
        TileState.Error => Error ?? "Failed",
        _ => string.Empty,
    };

    /// <summary>"14:03:12 · 1280x720".</summary>
    public string CaptureText => CapturedUtc is { } t
        ? string.Join(" · ", new[] { t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Resolution }.Where(s => !string.IsNullOrEmpty(s)))
        : string.Empty;

    [RelayCommand]
    private Task RefreshAsync() => _refresh(this);

    [RelayCommand]
    private void Preview() => _preview(this);

    [RelayCommand]
    private void ToggleSelected() => IsSelected = !IsSelected;

    public void SetWaiting() => State = TileState.Waiting;

    public void SetLoading()
    {
        Error = null;
        State = TileState.Loading;
    }

    /// <summary>New picture: the previous bitmap is disposed.</summary>
    public void SetImage(byte[] jpeg, IImage? image, int width, int height, DateTimeOffset? captured)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        var old = Image;
        Jpeg = jpeg;
        Image = image;
        (old as IDisposable)?.Dispose();
        Resolution = width > 0 && height > 0 ? string.Create(CultureInfo.InvariantCulture, $"{width}x{height}") : null;
        CapturedUtc = captured;
        Error = null;
        State = TileState.Ok;
    }

    /// <summary>The snapshot failed; an older picture stays visible.</summary>
    public void SetError(string error)
    {
        Error = error;
        State = TileState.Error;
    }

    public void Dispose()
    {
        (Image as IDisposable)?.Dispose();
        Image = null;
    }
}
