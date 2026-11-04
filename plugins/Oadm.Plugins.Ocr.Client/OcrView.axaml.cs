using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>The heights of the video and results cards; <see cref="VideoFirst"/> caps the video card until the splitter is dragged.</summary>
internal readonly record struct SplitLimits(double VideoMin, double VideoMax, double ResultsMin, double VideoFirst);

/// <summary>
/// The Text recognition page. Layout only (fixed readers and settings columns, the video and the results in two cards with a
/// splitter between them, <see cref="LimitsOf"/>): the camera picker,
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

    /// <summary>The height kept for the results card until the splitter is dragged: on a wide screen the video shrinks, the results stay.</summary>
    public const double ResultsMinHeight = 280;

    /// <summary>The results card's height the splitter stops at: the table's header and one row of a read.</summary>
    public const double ResultsDragMinHeight = 140;

    /// <summary>The video card's least share of the column's height.</summary>
    public const double VideoMinShare = 0.30;

    /// <summary>The theme's least height of the video surface.</summary>
    private const double PictureMinHeight = 120;

    private OcrViewModel? _viewModel;
    private ReaderEditorViewModel? _editor;

    /// <summary>The splitter was dragged: the first row keeps its height (for as long as the view lives, one connection).</summary>
    private bool _splitMoved;

    public OcrView()
    {
        InitializeComponent();
        VideoColumn.SizeChanged += (_, _) => FitSplit();
        ResultsSplitter.DragStarted += (_, _) => HoldSplit();
        Picture.AreaDrawn += (_, box) => _editor?.AddArea(box);
        Picture.AreaSelected += (_, area) => _editor?.SelectedArea = area;
        Picture.SelectionCleared += (_, _) => _editor?.SelectedArea = null;
        Picture.AreaChanged += (_, change) => _editor?.SetAreaBox(change.Area, change.Box);
        Picture.NudgeRequested += (_, by) => _editor?.NudgeSelected(by.X, by.Y);
        Picture.DrawingCancelled += (_, _) => _editor?.StopDrawing();
        Picture.AdjustingChanged += (_, adjusting) => _editor?.SetAdjusting(adjusting);
        // A press on an area's row or its field taking the focus: that area is the selected one on the live video.
        AreaList.AddHandler(GotFocusEvent, (_, e) => SelectAreaOf(e.Source));
        AreaList.AddHandler(PointerPressedEvent, (_, e) => SelectAreaOf(e.Source), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// The limits of the two cards in a column of <paramref name="height"/>, in this order when they do not all fit: the cards
    /// never overflow the column, the video keeps its 30 %, the results their minimum, the video what its card holds
    /// (<paramref name="videoContent"/>: padding and the least picture). The window's own minimum height leaves room for
    /// all of them; only below it the card cuts its content.
    /// </summary>
    internal static SplitLimits LimitsOf(double height, double splitter, double videoContent)
    {
        var cards = Math.Max(0, height - splitter);
        var videoMin = Math.Min(cards, Math.Max(Math.Ceiling(VideoMinShare * height), Math.Min(videoContent, cards - ResultsDragMinHeight))); // whole pixels: rounded rows never overflow
        var resultsMin = Math.Min(ResultsDragMinHeight, cards - videoMin);
        return new SplitLimits(videoMin, cards - resultsMin, resultsMin, Math.Max(videoMin, cards - ResultsMinHeight));
    }

    /// <summary>Applies the limits. The first row sizes to the picture, which follows the width alone, until the splitter holds it.</summary>
    private void FitSplit()
    {
        if (VideoColumn.Bounds.Height <= 0)
        {
            return;
        }

        var padding = VideoCard.Padding.Top + VideoCard.Padding.Bottom;
        var limits = LimitsOf(VideoColumn.Bounds.Height, ResultsSplitter.Bounds.Height, padding + PictureMinHeight);
        var rows = VideoColumn.RowDefinitions;
        rows[0].MinHeight = limits.VideoMin;
        rows[0].MaxHeight = limits.VideoMax;
        rows[2].MinHeight = limits.ResultsMin;
        if (!_splitMoved)
        {
            VideoCard.MaxHeight = limits.VideoFirst;
        }
    }

    /// <summary>The first drag: from now on the row has a height of its own, within its limits.</summary>
    private void HoldSplit()
    {
        var row = VideoColumn.RowDefinitions[0];
        _splitMoved = true;
        VideoCard.ClearValue(MaxHeightProperty);
        row.Height = new GridLength(row.ActualHeight);
    }

    private void OnAreaActions(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AreaViewModel area } button && _editor is { } editor)
        {
            RowActions.Show(button, editor.ActionsOf(area));
        }
    }

    private void OnCutOut(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ResultRow row })
        {
            _editor?.OpenCutOutCommand.Execute(row);
        }
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

    /// <summary>A row's cut-out, larger: the dialog over the page.</summary>
    public async Task ShowCutOutAsync(CutOutViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var window = new CutOutWindow { DataContext = form };
        form.CloseRequested += (_, _) => window.Close();
        await ShowDialogAsync(window).ConfigureAwait(true);
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
