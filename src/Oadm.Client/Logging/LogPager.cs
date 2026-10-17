using Avalonia.Controls;
using Avalonia.Layout;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Logging;

/// <summary>
/// The paging row of a log tab (DataContext an <see cref="ILogPages"/>): "Entries 201 to 400" and the buttons Newest,
/// Newer and Older. Shared by the Server and the Client tab of the Logs page.
/// </summary>
public sealed class LogPager : StackPanel
{
    public LogPager()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        text.Classes.Add("secondary");
        text.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ILogPages.PageText)));
        Children.Add(text);
        Children.Add(PageButton("Newest", "chevronUp", nameof(ILogPages.NewestCommand)));
        Children.Add(PageButton("Newer", "chevronLeft", nameof(ILogPages.NewerCommand)));
        Children.Add(PageButton("Older", "chevronRight", nameof(ILogPages.OlderCommand)));
    }

    private static ToolbarButton PageButton(string text, string icon, string command)
    {
        var button = new ToolbarButton { Text = text, IconKey = icon };
        button.Bind(Avalonia.Controls.Button.CommandProperty, new Avalonia.Data.Binding(command));
        return button;
    }
}
