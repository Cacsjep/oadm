using System.ComponentModel;
using System.Globalization;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>One label / value row of the preview.</summary>
public sealed record FactRow(string Label, string Value);

/// <summary>Large preview of one tile: the snapshot at full size and the device facts; follows tile refreshes.</summary>
public sealed partial class SnapshotPreviewViewModel : ObservableObject, IDisposable
{
    private readonly ISnapshotReportUi _ui;
    private byte[]? _decoded;

    public SnapshotPreviewViewModel(SnapshotTileViewModel tile, ISnapshotReportUi ui)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(ui);
        Tile = tile;
        _ui = ui;
        tile.PropertyChanged += OnTileChanged;
        UpdateImage();
    }

    public SnapshotTileViewModel Tile { get; }

    public string Title => Tile.Title;

    /// <summary>Label / value rows below the picture.</summary>
    public IReadOnlyList<FactRow> Facts
    {
        get
        {
            var device = Tile.Tile.Device;
            var now = DateTime.UtcNow;
            return
            [
                new("Model", device.Model ?? "Unknown"),
                new("MAC address", FactsText.Mac(device.Serial)),
                new("Address", FactsText.Address(device)),
                new("Firmware", device.Firmware ?? "Unknown"),
                new("Status", FactsText.Status(device.Status)),
                new("Certificate", FactsText.Certificate(device, now)),
                new("Source", Tile.Tile.SourceLabel ?? "Single source"),
                new("Snapshot", Tile.CapturedUtc is null ? "Not taken" : string.Create(CultureInfo.InvariantCulture, $"{FactsText.Time(Tile.CapturedUtc)} · {Tile.Resolution}")),
            ];
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial IImage? Image { get; private set; }

    public bool HasImage => Image is not null;

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private Task RefreshAsync() => Tile.RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        Tile.PropertyChanged -= OnTileChanged;
        (Image as IDisposable)?.Dispose();
        Image = null;
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SnapshotTileViewModel.Image) or nameof(SnapshotTileViewModel.State) or nameof(SnapshotTileViewModel.CapturedUtc))
        {
            UpdateImage();
            OnPropertyChanged(nameof(Facts));
        }
    }

    private void UpdateImage()
    {
        if (Tile.Jpeg is not { } jpeg || ReferenceEquals(jpeg, _decoded))
        {
            return;
        }

        _decoded = jpeg;
        var old = Image;
        Image = _ui.Decode(jpeg, 0);
        (old as IDisposable)?.Dispose();
    }
}
