using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>Page view. View only: decoding pictures and opening the preview and export windows.</summary>
public partial class SnapshotReportView : UserControl, ISnapshotReportUi
{
    private readonly Dictionary<Control, SnapshotTileRow> _realized = [];

    public SnapshotReportView()
    {
        InitializeComponent();

        // Lazy loading: the view model loads snapshots only for the rows the virtualizing panel realized.
        var rows = this.FindControl<ItemsControl>("TileRows")!;
        rows.ContainerPrepared += (_, e) =>
        {
            if (rows.ItemFromContainer(e.Container) is SnapshotTileRow row && DataContext is SnapshotReportViewModel vm)
            {
                _realized[e.Container] = row;
                vm.SetRowRealized(row, true);
            }
        };
        rows.ContainerClearing += (_, e) =>
        {
            if (_realized.Remove(e.Container, out var row) && DataContext is SnapshotReportViewModel vm)
            {
                vm.SetRowRealized(row, false);
            }
        };
        rows.SizeChanged += (_, e) =>
        {
            if (DataContext is SnapshotReportViewModel vm)
            {
                vm.AvailableWidth = e.NewSize.Width;
            }
        };
    }

    public IImage? Decode(byte[] jpeg, int decodeWidth)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        using var stream = new MemoryStream(jpeg, writable: false);
        return decodeWidth > 0 ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality) : new Bitmap(stream);
    }

    public void ShowPreview(SnapshotTileViewModel tile)
    {
        var window = new SnapshotPreviewWindow();
        window.Attach(new SnapshotPreviewViewModel(tile, this));
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    public async Task ShowExportAsync(ExportReportViewModel dialog)
    {
        var window = new ExportReportWindow();
        window.Attach(dialog);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog<string?>(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }
}
