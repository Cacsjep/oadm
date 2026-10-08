using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Oadm.Client.Shell;

/// <summary>The start splash window (see <see cref="SplashViewModel"/>); fades out before it closes.</summary>
public partial class SplashWindow : Window
{
    public static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(0.35);

    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>Fades the content out (with a slight zoom like the mockup); the caller closes the window afterwards.</summary>
    public Task FadeOutAsync()
    {
        var animation = new Animation
        {
            Duration = FadeDuration,
            FillMode = FillMode.Forward,
            Easing = new CubicEaseIn(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0) } },
            },
        };
        return animation.RunAsync(Root);
    }
}
