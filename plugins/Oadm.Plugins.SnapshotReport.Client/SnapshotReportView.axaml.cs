using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>Page view. View only: decoding pictures and opening the preview and export windows.</summary>
public partial class SnapshotReportView : UserControl, ISnapshotReportUi
{
    private bool _loadedOnce;

    public SnapshotReportView()
    {
        InitializeComponent();
    }

    /// <summary>Loads the snapshots the first time the page is shown (the rail creates the view at connect).</summary>
    public bool LoadOnFirstShow { get; set; } = true;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (!_loadedOnce && LoadOnFirstShow && DataContext is SnapshotReportViewModel vm)
        {
            _loadedOnce = true;
            _ = vm.RefreshAllAsync();
        }
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
