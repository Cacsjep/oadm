using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>One line of a wizard's overview: a short label ("Next run") and its text.</summary>
/// <param name="Label">The short label in the left column ("Next run").</param>
/// <param name="Text">The fact beside it ("Tue 24 Nov, 23:00").</param>
public sealed record OverviewLine(string Label, string Text);

/// <summary>
/// The overview step of every wizard (the Event rule editor, Schedules): one line per fact, the label in a narrow column
/// left, the text beside it, wrapping. Bind <see cref="ItemsControl.ItemsSource"/> to items with <c>Label</c> and <c>Text</c>
/// (<see cref="OverviewLine"/>). Look from the theme (<c>TextBlock.fieldLabel</c>).
/// </summary>
public sealed class OverviewList : ItemsControl
{
    /// <summary>The width of the label column.</summary>
    public const double LabelWidth = 80;

    public OverviewList()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 10 });
        ItemTemplate = new FuncDataTemplate<object>((_, _) => Line(), supportsRecycling: true);
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    private static Grid Line()
    {
        var label = new TextBlock();
        label.Classes.Add("fieldLabel");
        label.Bind(TextBlock.TextProperty, new Binding(nameof(OverviewLine.Label)));
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(OverviewLine.Text)));
        Grid.SetColumn(text, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{LabelWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},*"),
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { label, text },
        };
    }
}
