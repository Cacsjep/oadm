using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Media;

using Oadm.Plugins.Pki.Policies;

namespace Oadm.Plugins.Pki.Client.Servers;

/// <summary>
/// The tooltip of a server tab's "N policies applied" (802.1X server, EST server; decision 2026-10-25): each policy in two
/// lines, its name bold (<c>TextBlock.tipTitle</c>) and its summary sentence below it (<c>TextBlock.secondary</c>). Built in
/// code from the lines (<see cref="Follow"/>), plain text blocks without templates or bindings: the tooltip's popup has no
/// way to the page's view model.
/// </summary>
public sealed class PolicyLinesTip : StackPanel
{
    public PolicyLinesTip(IReadOnlyList<PolicyLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Spacing = 8;
        MaxWidth = 380;
        foreach (var line in lines)
        {
            var name = new TextBlock { Text = line.Name, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Classes.Add("tipTitle");
            var summary = new TextBlock { Text = line.Summary, TextWrapping = TextWrapping.Wrap };
            summary.Classes.Add("secondary");
            Children.Add(new StackPanel { Spacing = 2, Children = { name, summary } });
        }
    }

    /// <summary>
    /// Keeps the tooltip of <paramref name="link"/> on the policies <paramref name="lines"/> reads, again after every change of
    /// the view model's <paramref name="property"/>; no tooltip without policies. Returns the unsubscribe.
    /// </summary>
    public static Action Follow(Control link, INotifyPropertyChanged viewModel, string property, Func<IReadOnlyList<PolicyLine>> lines)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(lines);
        void Update() => ToolTip.SetTip(link, lines() is { Count: > 0 } list ? new PolicyLinesTip(list) : null);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == property)
            {
                Update();
            }
        }

        viewModel.PropertyChanged += OnChanged;
        Update();
        return () => viewModel.PropertyChanged -= OnChanged;
    }
}
