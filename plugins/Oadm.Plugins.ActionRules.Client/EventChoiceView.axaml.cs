using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.ActionRules.Client;

public partial class EventChoiceView : UserControl
{
    /// <summary>False where the form shows the filters as rows of its own (the rule's event).</summary>
    public static readonly StyledProperty<bool> ShowFiltersProperty =
        AvaloniaProperty.Register<EventChoiceView, bool>(nameof(ShowFilters), true);

    public EventChoiceView()
    {
        InitializeComponent();
    }

    public bool ShowFilters
    {
        get => GetValue(ShowFiltersProperty);
        set => SetValue(ShowFiltersProperty, value);
    }
}
