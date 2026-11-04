using Avalonia;
using Avalonia.Controls;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// Places its children on the picture itself, the same rectangle as <see cref="ReadingPicture.PictureRect"/>: on a wide screen
/// a corner of the picture, never of the black bars. Takes no room of its own and only its children catch the pointer, so
/// everything else of the picture stays <see cref="ReadingPicture"/>'s.
/// </summary>
public sealed class PicturePlace : Panel
{
    public static readonly StyledProperty<Resolution> PictureSizeProperty =
        AvaloniaProperty.Register<PicturePlace, Resolution>(nameof(PictureSize), Resolution.Default);

    static PicturePlace() => AffectsArrange<PicturePlace>(PictureSizeProperty);

    public Resolution PictureSize
    {
        get => GetValue(PictureSizeProperty);
        set => SetValue(PictureSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var picture = ReadingPicture.Fit(finalSize, ReadingPicture.AspectOf(PictureSize));
        foreach (var child in Children)
        {
            child.Arrange(picture);
        }

        return finalSize;
    }
}
