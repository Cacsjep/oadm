using Avalonia.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>The Set up dialog. Layout only: the picture's selections, drags and nudges go to the view model.</summary>
public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        Picture.AreaSelected += (_, id) => (DataContext as SetupViewModel)?.Select(id);
        Picture.AreaChanged += (_, change) => (DataContext as SetupViewModel)?.Apply(change);
        Picture.NudgeRequested += (_, by) => (DataContext as SetupViewModel)?.Nudge(by);
    }

    /// <summary>The overlay over the live picture (tests drag on it).</summary>
    internal AreaPicture PictureControl => Picture;
}
