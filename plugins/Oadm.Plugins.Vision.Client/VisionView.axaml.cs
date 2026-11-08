using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// The Vision Center page. Layout and input routing only: the canvas and the video overlay hand their input to the view
/// models, the node list's choice goes to the canvas, the camera picker, the Stream settings and model dialogs, the file
/// picker of Upload model; the view model watches while the view is in the visual tree.
/// </summary>
/// <summary>The widths the canvas and the right column may take in a page of a given width (the resizer's stops).</summary>
public readonly record struct ColumnLimits(double CanvasMin, double RightMin, double RightMax);

/// <summary>The heights the video card and the node card may take in the right column, and the video card's first height.</summary>
public readonly record struct RowLimits(double VideoMin, double VideoMax, double NodeMin, double VideoFirst);

/// <summary>
/// The page's widths (docs/spec/vision-center.md "Page"): the fixed pipelines column, then the canvas card and the right column
/// with the shared splitter as their gap. Pure: the view applies it, the tests check it.
/// </summary>
public static class PageLayout
{
    /// <summary>The pipelines column's fixed width and the gap after it.</summary>
    public const double PipelinesWidth = 320 + 16;

    /// <summary>The right column's width at first and its least: the live video above the selected node's form.</summary>
    public const double RightMinWidth = 420;

    /// <summary>The canvas card's least width: its toolbar fits (Add node, Undo, Redo, Fit view, Tidy up, Delete).</summary>
    public const double CanvasMinWidth = 620;

    /// <summary>The video's height in the right column at its least width (16:9 of the picture's place), and its least.</summary>
    public const double VideoMinHeight = 226;

    /// <summary>The node card's least height: its heading and two fields (measured with the headless page).</summary>
    public const double NodeMinHeight = 210;

    /// <summary>
    /// The limits of the canvas card and the right column in a page of <paramref name="width"/> (the splitter of
    /// <paramref name="splitter"/> between them), in this order when they do not all fit: the canvas keeps its minimum, then the
    /// right column its own; a drag never takes the canvas below its minimum.
    /// </summary>
    /// <summary>
    /// The heights of the video card and the node card in a right column of <paramref name="height"/> (the splitter of
    /// <paramref name="splitter"/> between them; <paramref name="videoPadding"/> the video card's padding), in this order when
    /// they do not all fit: the cards never overflow the column, the video keeps its least picture, the node card its least
    /// height. At first the video card is as tall as a 16:9 picture of <paramref name="width"/> wants, within the limits.
    /// </summary>
    public static RowLimits RowsOf(double height, double width, double splitter, double videoPadding)
    {
        var cards = Math.Max(0, height - splitter);
        var videoMin = Math.Min(cards, VideoMinHeight + videoPadding);
        var nodeMin = Math.Min(NodeMinHeight, cards - videoMin);
        var videoMax = cards - nodeMin;
        var first = Math.Clamp(Math.Round(Math.Max(0, width - videoPadding) * 9 / 16) + videoPadding, videoMin, videoMax);
        return new RowLimits(videoMin, videoMax, nodeMin, first);
    }

    public static ColumnLimits LimitsOf(double width, double splitter)
    {
        var room = Math.Max(0, width - PipelinesWidth - splitter);
        var canvasMin = Math.Min(CanvasMinWidth, room);
        var rightMin = Math.Min(RightMinWidth, room - canvasMin);
        return new ColumnLimits(canvasMin, rightMin, Math.Max(rightMin, room - canvasMin));
    }
}

public partial class VisionView : UserControl, IVisionDialogs
{
    private VisionViewModel? _viewModel;
    private PipelineEditorViewModel? _editor;

    public VisionView()
    {
        InitializeComponent();
        Panes.SizeChanged += (_, _) => FitColumns();
        RightColumn.SizeChanged += (_, _) => FitRows();
        VideoSplitter.DragStarted += (_, _) => HoldRows();
        Picture.ShapeDrawn += (_, points) => _editor?.ShapeDrawn(points);
        Picture.ShapeChanged += (_, change) => _editor?.SetShape(change.Shape.Node, change.Points);
        Picture.ShapeSelected += (_, shape) => _editor?.Canvas.SelectedNode = shape.Node;
        Picture.NudgeRequested += (_, by) => _editor?.NudgeSelected(by.X, by.Y);
        Picture.DrawingCancelled += (_, _) => _editor?.StopDrawing();
        NodeList.AddHandler(TappedEvent, OnNodeChosen, RoutingStrategies.Bubble, handledEventsToo: true);
        NodeSearch.AddHandler(KeyDownEvent, OnNodeSearchKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Applies the limits; the right column keeps the width the splitter gave it (as long as this view lives).</summary>
    private void FitColumns()
    {
        if (Panes.Bounds.Width <= 0)
        {
            return;
        }

        var limits = PageLayout.LimitsOf(Panes.Bounds.Width, RightSplitter.Bounds.Width);
        var columns = Panes.ColumnDefinitions;
        columns[2].MinWidth = limits.CanvasMin;
        columns[4].MinWidth = limits.RightMin;
        columns[4].MaxWidth = limits.RightMax;
    }

    /// <summary>The splitter between the video and the node card was dragged: the video row keeps its own height from now on.</summary>
    private bool _rowsMoved;

    /// <summary>
    /// The limits of the two cards of the right column. Until the splitter is dragged the video card is as tall as its 16:9
    /// picture wants (it follows the column's width); afterwards the row keeps its height within the limits.
    /// </summary>
    private void FitRows()
    {
        if (RightColumn.Bounds.Height <= 0)
        {
            return;
        }

        var padding = VideoCard.Padding.Top + VideoCard.Padding.Bottom;
        var limits = PageLayout.RowsOf(RightColumn.Bounds.Height, RightColumn.Bounds.Width, VideoSplitter.Bounds.Height, padding);
        var rows = RightColumn.RowDefinitions;
        rows[0].MinHeight = limits.VideoMin;
        rows[0].MaxHeight = limits.VideoMax;
        rows[2].MinHeight = limits.NodeMin;
        if (!_rowsMoved)
        {
            rows[0].Height = new GridLength(limits.VideoFirst);
        }
    }

    private void HoldRows()
    {
        var row = RightColumn.RowDefinitions[0];
        _rowsMoved = true;
        row.Height = new GridLength(row.ActualHeight);
    }

    /// <summary>The video card's height now (tests).</summary>
    internal double VideoHeight => RightColumn.RowDefinitions[0].ActualHeight;

    /// <summary>Gives the video card <paramref name="height"/> within its limits, as a drag of the splitter does (tests).</summary>
    internal void SetVideoHeight(double height)
    {
        var row = RightColumn.RowDefinitions[0];
        _rowsMoved = true;
        row.Height = new GridLength(Math.Clamp(height, row.MinHeight, row.MaxHeight));
    }

    /// <summary>The right column's width now (tests).</summary>
    internal double RightWidth => Panes.ColumnDefinitions[4].ActualWidth;

    /// <summary>Gives the right column <paramref name="width"/> within its limits, as a drag of the splitter does (tests).</summary>
    internal void SetRightWidth(double width)
    {
        var column = Panes.ColumnDefinitions[4];
        column.Width = new GridLength(Math.Clamp(width, column.MinWidth, column.MaxWidth));
    }

    /// <summary>The overlay over the live video (tests draw on it).</summary>
    internal VisionPicture PictureControl => Picture;

    /// <summary>The node canvas (tests press on it).</summary>
    internal PipelineCanvas CanvasControl => Canvas;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var old = _viewModel;
        _viewModel = DataContext as VisionViewModel;
        if (ReferenceEquals(old, _viewModel))
        {
            return;
        }

        if (old is not null)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
            old.Dispose();
        }

        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.PickCamera = PickCameraAsync;
            vm.PickCameras = PickCamerasAsync;
            vm.Dialogs = this;
        }

        WatchEditor(_viewModel?.Editor);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VisionViewModel.Editor))
        {
            WatchEditor(_viewModel?.Editor);
        }
    }

    private void WatchEditor(PipelineEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
            _editor.Canvas.Picker.PropertyChanged -= OnPickerPropertyChanged;
        }

        _editor = editor;
        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
            _editor.Canvas.Picker.PropertyChanged += OnPickerPropertyChanged;
        }
    }

    /// <summary>Drawing starts: the video takes the keys, so Esc stops it.</summary>
    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelineEditorViewModel.DrawKind) && _editor is { DrawKind: not null })
        {
            Picture.Focus();
        }
    }

    /// <summary>The node list opens: its search takes the keys.</summary>
    private void OnPickerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodePickerViewModel.IsOpen) && sender is NodePickerViewModel { IsOpen: true })
        {
            NodeSearch.Focus();
        }
    }

    private void OnNodeChosen(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is PickerKind kind && _editor is { } editor)
        {
            editor.Canvas.Choose(kind.Kind);
            Canvas.Focus();
        }
    }

    /// <summary>Enter in the search adds the first kind shown; Esc closes the list.</summary>
    private void OnNodeSearchKey(object? sender, KeyEventArgs e)
    {
        if (_editor is not { } editor)
        {
            return;
        }

        if (e.Key == Key.Enter && editor.Canvas.Picker.ShownKinds is [var first, ..])
        {
            editor.Canvas.Choose(first);
            Canvas.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            editor.Canvas.Picker.Close();
            Canvas.Focus();
            e.Handled = true;
        }
    }

    private void OnPipelineActions(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PipelineListRow row } button && _viewModel is { } vm)
        {
            RowActions.Show(button, vm.ActionsOf(row));
        }
    }

    private void OnModelActions(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ModelRow row } button && _viewModel is { } vm)
        {
            RowActions.Show(button, vm.Models.ActionsOf(row));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _editor is { DrawKind: not null } editor)
        {
            editor.StopDrawing();
            e.Handled = true;
        }
    }

    /// <summary>Copy to cameras: the shared picker of several devices over the cameras a copy can go to.</summary>
    private async Task<IReadOnlyList<Guid>?> PickCamerasAsync(IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> cameras) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowAsync(owner, cameras, heading: VisionTexts.ChooseCameras).ConfigureAwait(true)
            : null;

    private async Task<Guid?> PickCameraAsync(Guid? selected) =>
        TopLevel.GetTopLevel(this) is Window owner && _viewModel is { } vm
            ? await DevicePickerWindow.ShowOneAsync(owner, vm.Cameras, selected, heading: VisionTexts.ChooseCamera).ConfigureAwait(true)
            : null;

    public async Task<bool> EditStreamAsync(StreamSettingsViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new StreamSettingsWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowDialogAsync(window).ConfigureAwait(true);
        return ok;
    }

    public async Task<bool> EditModelSettingsAsync(ModelSettingsViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new ModelSettingsWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowDialogAsync(window).ConfigureAwait(true);
        return ok;
    }

    public async Task<bool> ChooseEventTypeAsync(EventTypePickerViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new EventTypeWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowDialogAsync(window).ConfigureAwait(true);
        return ok;
    }

    public async Task<Guid?> ChooseDeviceAsync(Guid? selected) =>
        TopLevel.GetTopLevel(this) is Window owner && _viewModel is { } vm
            ? await DevicePickerWindow.ShowOneAsync(owner, vm.AllDevices, selected, heading: VisionTexts.ChooseDevice).ConfigureAwait(true)
            : null;

    public async Task<bool> EditClassListAsync(ClassListEditViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new ClassListWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowDialogAsync(window).ConfigureAwait(true);
        return ok;
    }

    private void OnClassListActions(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ClassListRow row } button && _viewModel is { } vm)
        {
            RowActions.Show(button, vm.Models.ActionsOf(row));
        }
    }

    public async Task<bool> EditModelAsync(ModelEditViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new ModelEditWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowDialogAsync(window).ConfigureAwait(true);
        return ok;
    }

    /// <summary>The model file, and a labels file with it if wanted (one name per line).</summary>
    public async Task<IReadOnlyList<IModelFile>?> PickModelFilesAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = VisionTexts.ChooseModelFile,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("ONNX model") { Patterns = ["*.onnx", "*.txt"] }],
        }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }

        var list = new List<IModelFile>();
        foreach (var file in files)
        {
            var props = await file.GetBasicPropertiesAsync().ConfigureAwait(true);
            list.Add(new PickedFile(file, (long)(props.Size ?? 0)));
        }

        return list;
    }

    private async Task ShowDialogAsync(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel?.Activate();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _viewModel?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class PickedFile(IStorageFile file, long length) : IModelFile
    {
        public string Name => file.Name;

        public long Length => length;

        public Task<Stream> OpenReadAsync() => file.OpenReadAsync();
    }
}
