using System.Globalization;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Sdk.Client.Controls;

/// <summary>A weekday of <see cref="DayChips"/>: its short name and whether it is on.</summary>
public interface IDayChoice
{
    /// <summary>The short name on the chip ("Mon").</summary>
    string Text { get; }

    /// <summary>The day is chosen (the chip is on).</summary>
    bool IsChecked { get; set; }
}

/// <summary>A plain <see cref="IDayChoice"/> for a weekday, Monday first in <see cref="Week"/>.</summary>
public sealed partial class DayChoice(DayOfWeek day) : ObservableObject, IDayChoice
{
    /// <summary>The weekday.</summary>
    public DayOfWeek Day { get; } = day;

    /// <summary>The short name on the chip ("Mon").</summary>
    public string Text { get; } = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    /// <summary>The day is chosen (the chip is on).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Monday to Sunday.</summary>
    public static IReadOnlyList<DayChoice> Week() =>
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new DayChoice(d))];
}

/// <summary>
/// The weekday chips (the event rules' schedule, Vision Center's Time window): one toggle chip per day in a row, the theme's
/// <c>ToggleButton.day</c>. Bind <see cref="ItemsControl.ItemsSource"/> to the days (<see cref="IDayChoice"/>).
/// </summary>
public sealed class DayChips : ItemsControl
{
    public DayChips()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 });
        ItemTemplate = new FuncDataTemplate<IDayChoice>((_, _) =>
        {
            var chip = new ToggleButton();
            chip.Classes.Add("day");
            chip.Bind(ContentControl.ContentProperty, new Binding(nameof(IDayChoice.Text)));
            chip.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(IDayChoice.IsChecked)) { Mode = BindingMode.TwoWay });
            return chip;
        });
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);
}
