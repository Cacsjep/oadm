using Avalonia;
using Avalonia.Controls;

namespace Oadm.Client.Shell;

/// <summary>
/// Keeps every window (main window, dialogs, plugin dialogs) inside the screen it opens on: a dialog designed for a large
/// screen is made smaller on a small or scaled one (Full HD at 125 % leaves about 1536 x 816), its minimum and maximum
/// sizes follow, and it is moved back onto the screen. Applied once a window is shown (<see cref="Attach"/>), on the screen
/// it was placed on (next to its owner, which may be another monitor); the size logic is <see cref="Fit"/>, without UI.
/// </summary>
public static class WindowFit
{
    /// <summary>Space kept free around a window that had to shrink.</summary>
    public const double Margin = 16;

    /// <summary>Fits every window that opens from now on.</summary>
    /// <remarks>
    /// <see cref="Window.WindowOpenedEvent"/> comes before Avalonia places the window next to its owner (a new window still
    /// sits on the main monitor then), so the fit waits for <see cref="Window.Opened"/>, which comes after.
    /// </remarks>
    public static void Attach() =>
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            void OnOpened(object? sender, EventArgs e)
            {
                window.Opened -= OnOpened;
                Apply(window);
            }

            window.Opened += OnOpened;
        });

    /// <summary>
    /// Opens <paramref name="window"/> (not owned, e.g. a plugin window) centered on the screen <paramref name="opener"/> is
    /// on, instead of the main monitor.
    /// </summary>
    public static void CenterOn(Window window, Window opener)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(opener);
        var screen = opener.Screens.ScreenFromWindow(opener);
        if (screen is null || screen.Scaling <= 0 || double.IsNaN(window.Width) || double.IsNaN(window.Height))
        {
            return;
        }

        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(new Size(window.Width, window.Height), screen.Scaling);
        var center = new PixelPoint(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = Inside(center, size, area);
    }

    /// <summary>
    /// The sizes of a window inside <paramref name="available"/> (the work area in logical pixels): width, height,
    /// minimum and maximum never exceed it less <see cref="Margin"/> on each side, and nothing grows.
    /// </summary>
    public static FittedSize Fit(FittedSize requested, Size available)
    {
        var maxWidth = Math.Max(200, available.Width - 2 * Margin);
        var maxHeight = Math.Max(200, available.Height - 2 * Margin);
        return new FittedSize(
            Limit(requested.Width, maxWidth),
            Limit(requested.Height, maxHeight),
            Math.Min(requested.MinWidth, maxWidth),
            Math.Min(requested.MinHeight, maxHeight),
            Math.Min(requested.MaxWidth, maxWidth),
            Math.Min(requested.MaxHeight, maxHeight));
    }

    /// <summary>The position that keeps a window of <paramref name="size"/> (device pixels) inside <paramref name="area"/>.</summary>
    public static PixelPoint Inside(PixelPoint position, PixelSize size, PixelRect area)
    {
        var x = Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - size.Width));
        var y = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        return new PixelPoint(x, y);
    }

    private static double Limit(double value, double max) => double.IsNaN(value) ? value : Math.Min(value, max);

    private static void Apply(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null || screen.Scaling <= 0)
        {
            return;
        }

        var area = screen.WorkingArea;
        var available = new Size(area.Width / screen.Scaling, area.Height / screen.Scaling);
        var requested = new FittedSize(window.Width, window.Height, window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight);
        var fitted = Fit(requested, available);
        if (fitted == requested)
        {
            return;
        }

        window.MinWidth = fitted.MinWidth;
        window.MinHeight = fitted.MinHeight;
        window.MaxWidth = fitted.MaxWidth;
        window.MaxHeight = fitted.MaxHeight;
        if (!double.IsNaN(fitted.Width))
        {
            window.Width = fitted.Width;
        }

        if (!double.IsNaN(fitted.Height))
        {
            window.Height = fitted.Height;
        }

        // After the new size took effect (a window sized to its content measures again): centered when it shrank,
        // else only moved back onto the screen.
        var shrank = fitted.Width < requested.Width || fitted.Height < requested.Height || fitted.MaxHeight < requested.MaxHeight;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (window.WindowState != WindowState.Normal || !window.IsVisible)
            {
                return;
            }

            var size = PixelSize.FromSize(window.ClientSize, screen.Scaling);
            var center = new PixelPoint(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
            window.Position = Inside(shrank ? center : window.Position, size, area);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}

/// <summary>Width, height, minimum and maximum of a window (NaN width or height = sized to content).</summary>
public readonly record struct FittedSize(double Width, double Height, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);
