using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;

using Oadm.Client.Controls;

namespace Oadm.Client.Shell;

/// <summary>
/// The start splash window (see <see cref="SplashViewModel"/>): the mark slices in, rests, slices out, then the window
/// fades before it closes.
/// </summary>
public partial class SplashWindow : Window, ISplashAnimation
{
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(280);

    public SplashWindow()
    {
        InitializeComponent();
    }

    public SplashMark SplashMark => Mark;

    public Task PlayIntroAsync() => Mark.PlayIntroAsync();

    /// <summary>The mark slices out, then the content fades; the caller closes the window afterwards.</summary>
    public async Task PlayExitAsync()
    {
        await Mark.PlayExitAsync().ConfigureAwait(true);
        var animation = new Animation
        {
            Duration = FadeDuration,
            FillMode = FillMode.Forward,
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0) } },
            },
        };
        await animation.RunAsync(Root).ConfigureAwait(true);
    }
}
