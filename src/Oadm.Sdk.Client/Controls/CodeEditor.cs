using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one editable code field (request bodies; user decision 2026-10-11): a monospace <see cref="TextBox"/> for typing,
/// selecting, the caret and undo, with the same text drawn colored on top of it (JSON, XML, param.cgi key=value; the
/// tokenizer and colors of <see cref="CodeView"/>). The colored layer follows the text box's scrolling and never takes
/// input. The text is shown exactly as typed (never re-indented); texts above <see cref="CodeText.DefaultHighlightLimit"/>
/// characters are plain. Look from the theme: <c>ui|CodeEditor</c>, <c>TextBox.codeEditorInput</c>,
/// <c>TextBlock.codeEditorText</c>.
/// </summary>
public sealed class CodeEditor : Panel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<CodeLanguage> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, CodeLanguage>(nameof(Language), CodeLanguage.Plain);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(PlaceholderText));

    private readonly TextBox _box = new();
    private readonly TextBlock _colored = new()
    {
        TextWrapping = TextWrapping.NoWrap,
        IsHitTestVisible = false,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
    };
    private readonly Panel _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private TextPresenter? _presenter;
    private ScrollViewer? _scroll;
    private bool _syncing;

    public CodeEditor()
    {
        _box.Classes.Add("code");
        _box.Classes.Add("codeEditorInput");
        _colored.Classes.Add("codeEditorText");
        _colored.Bind(TextBlock.FontFamilyProperty, _box.GetObservable(TextBox.FontFamilyProperty));
        _colored.Bind(TextBlock.FontSizeProperty, _box.GetObservable(TextBox.FontSizeProperty));
        _box.Bind(TextBox.PlaceholderTextProperty, this.GetObservable(PlaceholderTextProperty));
        _viewport.Children.Add(_colored);
        Children.Add(_box);
        Children.Add(_viewport); // on top: the colored text over the transparent text of the box
        _box.TemplateApplied += (_, e) =>
        {
            _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
            _scroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_scroll is not null)
            {
                _scroll.ScrollChanged += (_, _) => Align();
            }
        };
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_syncing)
            {
                _syncing = true;
                SetCurrentValue(TextProperty, _box.Text);
                _syncing = false;
                Render();
            }
        };
        _box.LayoutUpdated += (_, _) => Align();
    }

    /// <summary>The text (two-way).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>How the text is colored; Plain = no colors.</summary>
    public CodeLanguage Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>The inner text box (tests, focus).</summary>
    public TextBox Input => _box;

    /// <summary>What the colored layer shows right now (tests).</summary>
    public CodeDocument Document { get; private set; } = CodeDocument.Empty;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Render(); // brushes come from the theme once the editor is in the tree
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing)
        {
            _syncing = true;
            _box.Text = Text;
            _syncing = false;
            Render();
        }
        else if (change.Property == LanguageProperty)
        {
            Render();
        }
    }

    private void Render()
    {
        var language = Language == CodeLanguage.Auto ? CodeLanguage.Plain : Language;
        Document = CodeText.Prepare(_box.Text, language, null, format: false, colorUnparsed: true);
        var inlines = VisualRoot is null ? null : CodeHighlight.Build(this, Document);
        if (inlines is null)
        {
            _colored.Inlines = null;
            _colored.Text = Document.Text;
        }
        else
        {
            _colored.Text = null;
            _colored.Inlines = inlines;
        }

        Align();
    }

    /// <summary>
    /// Lays the colored text exactly over the box's own (transparent) text: the viewport is the box's scroll area, the
    /// text moves with the presenter (scrolling included).
    /// </summary>
    private void Align()
    {
        if (_presenter is null || _scroll is null)
        {
            return;
        }

        if (_scroll.TranslatePoint(default, this) is { } viewport)
        {
            var margin = new Thickness(viewport.X, viewport.Y, Math.Max(0, Bounds.Width - viewport.X - _scroll.Bounds.Width), Math.Max(0, Bounds.Height - viewport.Y - _scroll.Bounds.Height));
            if (_viewport.Margin != margin)
            {
                _viewport.Margin = margin;
            }
        }

        if (_presenter.TranslatePoint(default, _scroll) is { } origin)
        {
            if (_colored.RenderTransform is not TranslateTransform shift || shift.X != origin.X || shift.Y != origin.Y)
            {
                _colored.RenderTransform = new TranslateTransform(origin.X, origin.Y);
            }
        }
    }
}
