using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// The Text recognition page. Layout only: the camera picker, the picture's drawing handed to the reader's editor, Esc stops
/// drawing; the view model watches while the view is in the visual tree.
/// </summary>
public partial class OcrView : UserControl
{
    /// <summary>The smallest share of the page width each card keeps when the splitter moves.</summary>
    public const double MinPaneShare = 0.2;

    private OcrViewModel? _viewModel;
    private ReaderEditorViewModel? _editor;

    public OcrView()
    {
        InitializeComponent();
        Panes.SizeChanged += (_, e) =>
        {
            var min = e.NewSize.Width * MinPaneShare;
            Panes.ColumnDefinitions[0].MinWidth = min;
            Panes.ColumnDefinitions[2].MinWidth = min;
        };
        Picture.AreaDrawn += (_, box) => _editor?.AddArea(box);
        Picture.AreaClicked += (_, point) => _editor?.SelectAreaAt(point);
        Picture.DrawingCancelled += (_, _) => _editor?.StopDrawing();
        // An area's field takes the focus: that area is the selected one on the picture.
        AreaList.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (_editor is { } editor && (e.Source as Control)?.DataContext is AreaViewModel area)
            {
                editor.SelectedArea = area;
            }
        });
    }

    /// <summary>The picture of the shown reader (tests draw on it).</summary>
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
        }

        _editor = editor;
        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
        }
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
