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

    /// <summary>The video's height in the right column at its least width (16:9 of the picture's place).</summary>
    public const double VideoMinHeight = 226;

    /// <summary>
    /// The limits of the canvas card and the right column in a page of <paramref name="width"/> (the splitter of
    /// <paramref name="splitter"/> between them), in this order when they do not all fit: the canvas keeps its minimum, then the
    /// right column its own; a drag never takes the canvas below its minimum.
    /// </summary>
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
        RightColumn.SizeChanged += (_, _) => FitVideo();
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

    /// <summary>The video follows the right column's width: the picture's 16:9 place, at least its first height.</summary>
    private void FitVideo()
    {
        var width = VideoPanel.Bounds.Width;
        if (width > 0)
        {
            VideoPanel.Height = Math.Max(PageLayout.VideoMinHeight, Math.Round(width * 9 / 16));
        }
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
