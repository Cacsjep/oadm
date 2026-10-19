using Avalonia;

using Oadm.Theme.Shell;

namespace Oadm.Client.Tests;

public sealed class WindowFitTests
{
    // Full HD at 125 % scaling with the taskbar: 1536 x 816 logical pixels.
    private static readonly Size SmallScreen = new(1536, 816);

    [Fact]
    public void A_dialog_larger_than_the_screen_shrinks_to_it()
    {
        var fitted = WindowFit.Fit(new FittedSize(1160, 860, 900, 820, double.PositiveInfinity, double.PositiveInfinity), SmallScreen);

        Assert.Equal(1160, fitted.Width);
        Assert.Equal(816 - 2 * WindowFit.Margin, fitted.Height);
        Assert.Equal(816 - 2 * WindowFit.Margin, fitted.MinHeight);
        Assert.Equal(816 - 2 * WindowFit.Margin, fitted.MaxHeight);
        Assert.Equal(1536 - 2 * WindowFit.Margin, fitted.MaxWidth);
    }

    [Fact]
    public void A_window_sized_to_its_content_keeps_that_and_gets_a_maximum()
    {
        var fitted = WindowFit.Fit(new FittedSize(460, double.NaN, 0, 0, double.PositiveInfinity, double.PositiveInfinity), SmallScreen);

        Assert.True(double.IsNaN(fitted.Height));
        Assert.Equal(460, fitted.Width);
        Assert.Equal(816 - 2 * WindowFit.Margin, fitted.MaxHeight);
    }

    [Fact]
    public void A_window_that_fits_is_unchanged_but_for_its_maximum()
    {
        var requested = new FittedSize(800, 600, 400, 300, 1000, 700);

        Assert.Equal(requested, WindowFit.Fit(requested, new Size(1920, 1040)));
    }

    [Fact]
    public void A_window_is_moved_back_onto_the_screen()
    {
        var area = new PixelRect(0, 0, 1920, 1040);

        Assert.Equal(new PixelPoint(420, 0), WindowFit.Inside(new PixelPoint(900, -50), new PixelSize(1500, 900), area));
        Assert.Equal(new PixelPoint(0, 0), WindowFit.Inside(new PixelPoint(-30, 10), new PixelSize(2500, 1200), area));
    }
}
