using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The colored runs of a <see cref="CodeDocument"/> with the theme brushes <c>Oadm.Code.&lt;Kind&gt;Brush</c>; shared by
/// <see cref="CodeView"/> (read-only) and <see cref="CodeEditor"/> (editable).
/// </summary>
internal static class CodeHighlight
{
    /// <summary>The runs, or null when the document has no colors (plain text, too long) or the theme is not reachable.</summary>
    public static InlineCollection? Build(StyledElement host, CodeDocument document)
    {
        if (!document.IsHighlighted)
        {
            return null;
        }

        var brushes = new IBrush?[Enum.GetValues<CodeTokenKind>().Length];
        foreach (var kind in Enum.GetValues<CodeTokenKind>())
        {
            if (kind != CodeTokenKind.Text && host.TryFindResource($"Oadm.Code.{kind}Brush", host is Control c ? c.ActualThemeVariant : null, out var found) && found is IBrush brush)
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

        return inlines;
    }
}
