using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one read-only code display (response bodies, headers): monospace, selectable and copyable, no line
/// wrapping (horizontal scrolling), JSON and XML pretty-printed with 2 spaces and highlighted, param.cgi
/// <c>key=value</c> text highlighted (keys, values, "# Error" lines). <see cref="Language"/> Auto detects the
/// language from <see cref="ContentType"/> and the text. Text that does not parse is shown exactly as it is;
/// texts above <see cref="HighlightLimit"/> characters are shown without colors. Colors are the theme brushes
/// <c>Oadm.Code.KeyBrush</c>, <c>StringBrush</c>, <c>NumberBrush</c>, <c>LiteralBrush</c>, <c>PunctuationBrush</c>,
/// <c>TagBrush</c>, <c>AttributeBrush</c>, <c>CommentBrush</c>, <c>ErrorBrush</c>; the look comes from the theme
/// style <c>ui|CodeView</c>. Editable bodies stay a <c>TextBox.code</c>.
/// </summary>
public sealed class CodeView : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeView, string?>(nameof(Text));

    public static readonly StyledProperty<CodeLanguage> LanguageProperty =
        AvaloniaProperty.Register<CodeView, CodeLanguage>(nameof(Language), CodeLanguage.Auto);

    public static readonly StyledProperty<string?> ContentTypeProperty =
        AvaloniaProperty.Register<CodeView, string?>(nameof(ContentType));

    public static readonly StyledProperty<bool> IsFormattedProperty =
        AvaloniaProperty.Register<CodeView, bool>(nameof(IsFormatted), true);

    public static readonly StyledProperty<int> HighlightLimitProperty =
        AvaloniaProperty.Register<CodeView, int>(nameof(HighlightLimit), CodeText.DefaultHighlightLimit);

    private readonly SelectableTextBlock _text = new() { TextWrapping = TextWrapping.NoWrap };
    private bool _attached;

    public CodeView()
    {
        Classes.Add("codeView");
        _text.Classes.Add("code");
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _text,
        };
        Child = scroll;
    }

    /// <summary>The text to show (e.g. the response body).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public CodeLanguage Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    /// <summary>HTTP content type of <see cref="Text"/>, used by <see cref="CodeLanguage.Auto"/>.</summary>
    public string? ContentType
    {
        get => GetValue(ContentTypeProperty);
        set => SetValue(ContentTypeProperty, value);
    }

    /// <summary>True (default): JSON and XML are re-indented. False: the text is shown exactly as given (still highlighted).</summary>
    public bool IsFormatted
    {
        get => GetValue(IsFormattedProperty);
        set => SetValue(IsFormattedProperty, value);
    }

    /// <summary>Texts longer than this many characters are shown without colors.</summary>
    public int HighlightLimit
    {
        get => GetValue(HighlightLimitProperty);
        set => SetValue(HighlightLimitProperty, value);
    }

    /// <summary>What is shown right now (text, language used, tokens).</summary>
    public CodeDocument Document { get; private set; } = CodeDocument.Empty;

    /// <summary>The shown text; select all and copy goes through the inner <see cref="SelectableTextBlock"/> (Ctrl+C, context menu).</summary>
    public string DisplayedText => Document.Text;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == LanguageProperty || change.Property == ContentTypeProperty
            || change.Property == IsFormattedProperty || change.Property == HighlightLimitProperty)
        {
            Document = CodeText.Prepare(Text, Language, ContentType, IsFormatted, HighlightLimit);
            Render();
        }
    }

    private void Render()
    {
        var document = Document;
        if (!document.IsHighlighted || !_attached)
        {
            // Brushes come from the theme, so colors are applied once the view is in the tree.
            _text.Inlines = null;
            _text.Text = document.Text;
            return;
        }

        var brushes = new IBrush?[Enum.GetValues<CodeTokenKind>().Length];
        foreach (var kind in Enum.GetValues<CodeTokenKind>())
        {
            if (kind != CodeTokenKind.Text && this.TryFindResource($"Oadm.Code.{kind}Brush", ActualThemeVariant, out var found) && found is IBrush brush)
            {
                brushes[(int)kind] = brush;
            }
        }

        var text = document.Text;
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var token in document.Tokens)
        {
            if (token.Start > position)
            {
                inlines.Add(new Run(text[position..token.Start]));
            }

            var run = new Run(text.Substring(token.Start, token.Length));
            if (brushes[(int)token.Kind] is { } foreground)
            {
                run.Foreground = foreground;
            }

            inlines.Add(run);
            position = token.Start + token.Length;
        }

        if (position < text.Length)
        {
            inlines.Add(new Run(text[position..]));
        }

        _text.Text = null;
        _text.Inlines = inlines;
    }
}
