using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oadm.Client.Controls;

/// <summary>
/// A text whose parts that match the search words are shown in the theme's match colour (<c>Oadm.AccentSoftBrush</c>),
/// semibold, like a search result: the tiles of the Plugins page. Without words it is the plain <see cref="Source"/>.
/// Styled as a TextBlock (<c>:is(TextBlock).tileName</c>).
/// </summary>
public sealed class HighlightText : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<HighlightText, string?>(nameof(Source));

    public static readonly StyledProperty<IReadOnlyList<string>?> WordsProperty =
        AvaloniaProperty.Register<HighlightText, IReadOnlyList<string>?>(nameof(Words));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>The whole text.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The search words; every place a word occurs is marked, case does not matter.</summary>
    public IReadOnlyList<string>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    /// <summary>The text cut into parts, each marked when it is part of a word's match; adjacent parts never share a mark.</summary>
    public static IReadOnlyList<(string Text, bool IsMatch)> Split(string? text, IReadOnlyList<string>? words)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (words is not { Count: > 0 })
        {
            return [(text, false)];
        }

        var marked = new bool[text.Length];
        foreach (var word in words)
        {
            if (word.Length == 0)
            {
                continue;
            }

            var at = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
            while (at >= 0)
            {
                for (var i = at; i < Math.Min(text.Length, at + word.Length); i++)
                {
                    marked[i] = true;
                }

                at = at + 1 < text.Length ? text.IndexOf(word, at + 1, StringComparison.CurrentCultureIgnoreCase) : -1;
            }
        }

        var parts = new List<(string, bool)>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || marked[i] != marked[start])
            {
                parts.Add((text[start..i], marked[start]));
                start = i;
            }
        }

        return parts;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == WordsProperty)
        {
            Build();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Build();
    }

    private void Build()
    {
        var parts = Split(Source, Words);
        if (!parts.Any(p => p.IsMatch))
        {
            Inlines = null;
            Text = Source;
            return;
        }

        IBrush? match = this.TryFindResource("Oadm.AccentSoftBrush", ActualThemeVariant, out var value) ? value as IBrush : null;
        var inlines = new InlineCollection();
        foreach (var (text, isMatch) in parts)
        {
            var run = new Run(text);
            if (isMatch)
            {
                run.FontWeight = FontWeight.SemiBold;
                if (match is not null)
                {
                    run.Foreground = match;
                }
            }

            inlines.Add(run);
        }

        Text = null;
        Inlines = inlines;
    }
}
