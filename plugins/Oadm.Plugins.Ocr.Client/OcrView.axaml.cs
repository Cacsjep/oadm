using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// The Text recognition page. Layout only (fixed readers and settings columns, the video takes the rest): the camera picker,
/// the Stream settings and Rename dialogs,
/// the live video overlay's drawing, moving, resizing and nudging handed to the reader's editor, Esc stops drawing; the view
/// model watches while the view is in the visual tree.
/// </summary>
public partial class OcrView : UserControl, IReaderDialogs
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
        var old = _viewModel;
        _viewModel = DataContext as OcrViewModel;
        if (ReferenceEquals(old, _viewModel))
        {
            return;
        }

        if (old is not null)
        {
            // The view owns its page's view model (OcrPage makes both): one that leaves the view is done for good.
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

    /// <summary>Drawing on (New in the Areas header): the picture takes the keys, so Esc stops drawing.</summary>
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
            ? await DevicePickerWindow.ShowOneAsync(owner, vm.Cameras, selected, heading: OcrTexts.ChooseCamera).ConfigureAwait(true)
            : null;

    /// <summary>Stream settings: the dialog over the page, OK or Cancel.</summary>
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

    /// <summary>Rename of an area: the dialog over the page, Rename or Cancel.</summary>
    public async Task<bool> RenameAreaAsync(RenameAreaViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new RenameAreaWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowDialogAsync(window).ConfigureAwait(true);
        return ok;
    }

    private async Task ShowDialogAsync(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
    }

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
