using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// The Text recognition page. Layout only (fixed readers and settings columns, the video takes the rest): the camera picker,
/// the live video overlay's drawing, moving, resizing and nudging handed to the reader's editor, Esc stops drawing; the view
/// model watches while the view is in the visual tree.
/// </summary>
public partial class OcrView : UserControl
{
    /// <summary>The readers column's fixed width (the reader rows and the runtime stats fit).</summary>
    public const double ReadersWidth = 320;

    /// <summary>The settings column's fixed width (the format field and the areas fit).</summary>
    public const double SettingsWidth = 320;

    private OcrViewModel? _viewModel;
    private ReaderEditorViewModel? _editor;

    public OcrView()
    {
        InitializeComponent();
        Picture.AreaDrawn += (_, box) => _editor?.AddArea(box);
        Picture.AreaSelected += (_, area) => _editor?.SelectedArea = area;
        Picture.AreaChanged += (_, change) => _editor?.SetAreaBox(change.Area, change.Box);
        Picture.NudgeRequested += (_, by) => _editor?.NudgeSelected(by.X, by.Y);
        Picture.DrawingCancelled += (_, _) => _editor?.StopDrawing();
        Picture.AdjustingChanged += (_, adjusting) => _editor?.SetAdjusting(adjusting);
        // A press on an area's row or its field taking the focus: that area is the selected one on the live video.
        AreaList.AddHandler(GotFocusEvent, (_, e) => SelectAreaOf(e.Source));
        AreaList.AddHandler(PointerPressedEvent, (_, e) => SelectAreaOf(e.Source), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void SelectAreaOf(object? source)
    {
        if (_editor is { } editor && (source as Control)?.DataContext is AreaViewModel area)
        {
            editor.SelectedArea = area;
        }
    }

    /// <summary>The overlay over the live video of the shown reader (tests draw on it).</summary>
    internal ReadingPicture PictureControl => Picture;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as OcrViewModel;
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.PickCamera = PickCameraAsync;
        }

        WatchEditor(_viewModel?.Editor);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OcrViewModel.Editor))
        {
            WatchEditor(_viewModel?.Editor);
        }
    }

    private void WatchEditor(ReaderEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
            _editor.FormatSuggested -= OnFormatSuggested;
        }

        _editor = editor;
        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
            _editor.FormatSuggested += OnFormatSuggested;
        }
    }

    /// <summary>"Use as format": the area's format field shows with its text selected, ready to adjust (AAAA to AAAU).</summary>
    private void OnFormatSuggested(object? sender, AreaViewModel area) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusFormatOf(area), Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>Focuses the format field of <paramref name="area"/> and selects its text (tests call it directly).</summary>
    internal TextBox? FocusFormatOf(AreaViewModel area)
    {
        if (AreaList.ContainerFromItem(area) is not Control container)
        {
            return null;
        }

        container.BringIntoView();
        var box = container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "FormatBox");
        if (box is not null)
        {
            box.Focus();
            box.SelectAll();
        }

        return box;
    }

    /// <summary>Draw area on: the picture takes the keys, so Esc stops drawing.</summary>
    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderEditorViewModel.IsDrawing) && _editor is { IsDrawing: true })
        {
            Picture.Focus();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _editor is { IsDrawing: true } editor)
        {
            editor.StopDrawing();
            e.Handled = true;
        }
    }

    private async Task<Guid?> PickCameraAsync(Guid? selected) =>
        TopLevel.GetTopLevel(this) is Window owner && _viewModel is { } vm
            ? await DevicePickerWindow.ShowOneAsync(owner, vm.Cameras, selected, heading: "Choose a camera").ConfigureAwait(true)
            : null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewModel?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }
}
