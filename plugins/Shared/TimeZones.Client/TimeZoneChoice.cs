using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Plugins.DateAndTime.Model;

namespace Oadm.Plugins.TimeZones.Client;

/// <summary>
/// The time zone select of every plugin that sets one (the Date and time task, Provisioning's Date and time node): the IANA
/// zones of AXIS OS (<see cref="TimeZoneCatalog.All"/>) sorted by UTC offset like ADM, "(UTC+01:00) Vienna"; typing a city
/// jumps to it. Bind <see cref="SelectingItemsControl.SelectedItem"/> to a <see cref="TimeZoneEntry"/>. Linked as source
/// into the plugins that use it, with the catalogue (plugins/Oadm.Plugins.DateTime/Model).
/// </summary>
public sealed class TimeZoneChoice : ComboBox
{
    /// <summary>The placeholder while no zone is chosen.</summary>
    public const string Placeholder = "Choose a time zone";

    public TimeZoneChoice()
    {
        ItemsSource = TimeZoneCatalog.All;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        PlaceholderText = Placeholder;
        IsTextSearchEnabled = true;
        TextSearch.SetTextBinding(this, new Binding(nameof(TimeZoneEntry.City)));
        ItemTemplate = new FuncDataTemplate<TimeZoneEntry>((zone, _) => new TextBlock { Text = zone?.ShortLabel, TextTrimming = TextTrimming.CharacterEllipsis });
    }

    protected override Type StyleKeyOverride => typeof(ComboBox);
}
