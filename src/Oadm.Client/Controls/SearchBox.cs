using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Client.Controls;

/// <summary>
/// The one search field used on every page and dialog: search icon left, clear button right
/// while text is present, fixed width from the theme. Bind <see cref="Text"/> two-way.
/// </summary>
public sealed class SearchBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SearchBox, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<SearchBox, string?>(nameof(Placeholder), "Search");

    private readonly TextBox _box;
    private readonly Button _clear;

    public SearchBox()
    {
        var searchIcon = new OadmIcon { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        searchIcon.Classes.Add("secondary");

        var closeIcon = new OadmIcon { Width = 12, Height = 12 };
        _clear = new Button
        {
            Content = closeIcon,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _clear.Classes.Add("toolbar");
        _clear.Classes.Add("iconOnly");
        _clear.Click += (_, _) =>
        {
            Text = string.Empty;
            _box?.Focus();
        };

        _box = new TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            InnerLeftContent = searchIcon,
            InnerRightContent = _clear,
        };
        _box.Classes.Add("search");
        _box.TextChanged += (_, _) =>
        {
            if (Text != _box.Text)
            {
                Text = _box.Text;
            }
        };

        Content = _box;
        UpdateFromText();
        _box.PlaceholderText = Placeholder;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_box.InnerLeftContent is OadmIcon icon && this.TryFindResource("Icon.search", ActualThemeVariant, out object? search) && search is Geometry g)
        {
            icon.Data = g;
        }

        if (_box.InnerLeftContent is OadmIcon left && this.TryFindResource("Oadm.TextSecondaryBrush", ActualThemeVariant, out object? brush) && brush is IBrush b)
        {
            left.Foreground = b;
        }

        if (_clear.Content is OadmIcon close && this.TryFindResource("Icon.close", ActualThemeVariant, out object? x) && x is Geometry c)
        {
            close.Data = c;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            UpdateFromText();
        }
        else if (change.Property == PlaceholderProperty && _box is not null)
        {
            _box.PlaceholderText = Placeholder;
        }
    }

    private void UpdateFromText()
    {
        if (_box is null)
        {
            return;
        }

        if (_box.Text != Text)
        {
            _box.Text = Text;
        }

        _clear.IsVisible = !string.IsNullOrEmpty(Text);
    }
}
