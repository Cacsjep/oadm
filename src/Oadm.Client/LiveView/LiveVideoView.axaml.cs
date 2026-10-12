using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oadm.Client.LiveView;

/// <summary>
/// The view of <see cref="LiveVideoViewModel"/>. Tells the view model the picture area's size in device pixels and
/// whether it can be seen (attached, shown, inside the visible part of a scrolling list, window not minimized), and opens
/// or closes the fullscreen window when the view model asks (button, double-click).
/// </summary>
public sealed partial class LiveVideoView : UserControl
{
    /// <summary>How often visibility is checked (hidden pages, minimized windows, tiles scrolled away).</summary>
    private static readonly TimeSpan VisibilityCheck = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _visibilityTimer;
    private LiveVideoViewModel? _video;
    private Rect? _effectiveViewport;
    private FullscreenVideoWindow? _fullscreen;

    public LiveVideoView()
    {
        AvaloniaXamlLoader.Load(this);
        _visibilityTimer = new DispatcherTimer(VisibilityCheck, DispatcherPriority.Background, (_, _) => ReportVisibility());
        var picture = this.FindControl<Panel>("Picture")!;
        picture.SizeChanged += (_, _) => ReportSize();
        picture.DoubleTapped += OnDoubleTapped;
        EffectiveViewportChanged += (_, e) =>
        {
            _effectiveViewport = e.EffectiveViewport;
            ReportVisibility();
        };
        ToolTip.SetTip(this.FindControl<Button>("ControlsButton")!, "Camera controls");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_video is not null)
        {
            _video.FullscreenRequested -= OnFullscreenRequested;
            _video.PropertyChanged -= OnVideoPropertyChanged;
            _fullscreen?.Close();
        }

        _video = DataContext as LiveVideoViewModel;
        if (_video is not null)
        {
            _video.FullscreenRequested += OnFullscreenRequested;
            _video.PropertyChanged += OnVideoPropertyChanged;
            var fullscreen = _video.IsFullscreen;
            ToolTip.SetTip(this.FindControl<Button>("FullscreenButton")!, fullscreen ? "Exit fullscreen (Esc)" : "Fullscreen (double-click)");
            if (this.TryFindResource(fullscreen ? "Icon.minimize" : "Icon.maximize", ActualThemeVariant, out var icon) && icon is Geometry geometry)
            {
                this.FindControl<Sdk.Client.Controls.OadmIcon>("FullscreenIcon")!.Data = geometry;
            }

            ReportSize();
            ReportVisibility();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _visibilityTimer.Start();
        ReportSize();
        ReportVisibility();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _visibilityTimer.Stop();
        _video?.SetVisible(false);
    }

    private void ReportSize()
    {
        var picture = this.FindControl<Panel>("Picture");
        if (_video is null || picture is null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var scale = top.RenderScaling;
        _video.SetViewport((int)Math.Round(picture.Bounds.Width * scale), (int)Math.Round(picture.Bounds.Height * scale));
    }

    private void ReportVisibility()
    {
        if (_video is null)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        var visible = top is not null
            && IsEffectivelyVisible
            && (top is not Window window || window.WindowState != WindowState.Minimized)
            && (_effectiveViewport is not { } viewport || viewport.Intersects(new Rect(Bounds.Size)) || Bounds.Size == default);
        _video.SetVisible(visible);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_video is { AllowFullscreen: true } video && OnPicture(e.Source as Visual))
        {
            video.ToggleFullscreenCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>A double-click on the picture itself, not on a button, slider, list, bar or the PTZ and focus area overlays.</summary>
    private static bool OnPicture(Visual? source)
    {
        for (var visual = source; visual is not null and not LiveVideoView; visual = visual.GetVisualParent())
        {
            if (visual is Button or Slider or ComboBox or OpticsBar or PtzBar or PtzOverlay or FocusAreaOverlay)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The video below stopped (closed, another device): its fullscreen window closes too.</summary>
    private void OnVideoPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveVideoViewModel.State) && _video?.State == LiveViewState.Closed)
        {
            _fullscreen?.Close();
        }
    }

    private void OnFullscreenRequested(object? sender, EventArgs e)
    {
        if (_video is not { } video)
        {
            return;
        }

        if (video.IsFullscreen)
        {
            if (TopLevel.GetTopLevel(this) is Window own)
            {
                own.Close();
            }

            return;
        }

        if (_fullscreen is not null)
        {
            _fullscreen.Activate();
            return;
        }

        var window = new FullscreenVideoWindow(video.CreateFullscreen());
        _fullscreen = window;
        window.Closed += (_, _) => _fullscreen = null;
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}
