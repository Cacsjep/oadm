using Avalonia.Controls;
using Avalonia.Input;

namespace Oadm.Client.LiveView;

/// <summary>
/// A live video over the whole screen: its own stream at the size of the screen, the same overlays as everywhere.
/// Esc, the button top left or a double-click close it; closing stops its stream and lets the video below stream again.
/// </summary>
public sealed class FullscreenVideoWindow : Window
{
    private readonly LiveVideoViewModel _video;

    public FullscreenVideoWindow(LiveVideoViewModel video)
    {
        ArgumentNullException.ThrowIfNull(video);
        _video = video;
        Title = string.IsNullOrEmpty(video.Title) ? "OADM" : $"{video.Title} · OADM";
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; // the screen of the main window
        WindowState = WindowState.FullScreen;
        Classes.Add("fullscreenVideo");
        Content = new LiveVideoView { DataContext = video };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _video.Dispose();
    }
}
