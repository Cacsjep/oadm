using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Plugins.DeviceEvents;

// The filter rows of an event choice, shared as source by the client parts of Device triggers and the Action Rule Editor.

/// <summary>A value of a source item, or "Any".</summary>
public sealed record FilterOption(string? Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One source item of the chosen event: Any or one of the values the devices list.</summary>
public sealed partial class FilterRowViewModel : ObservableObject
{
    public FilterRowViewModel(EventItem item, string? selected)
    {
        Item = item;
        Options = [new FilterOption(null, "Any"), .. item.Values.Select(v => new FilterOption(v.Value, string.IsNullOrWhiteSpace(v.DisplayName) ? v.Value : $"{v.DisplayName} ({v.Value})"))];
        Selected = Options.FirstOrDefault(o => o.Value == selected) ?? Options[0];
    }

    public EventItem Item { get; }

    public string Label => Item.Label;

    public IReadOnlyList<FilterOption> Options { get; }

    [ObservableProperty] public partial FilterOption Selected { get; set; }
}
