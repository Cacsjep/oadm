using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Oadm.Client.EventRules;

/// <summary>
/// The Event rules page. View only: streams run while it is shown; a double click on a rule or a recipient edits it. Rules
/// and Recipients sit side by side, on a narrow page Recipients moves below Rules and the event log takes less height.
/// </summary>
public partial class EventRulesView : UserControl
{
    /// <summary>Below this page width Recipients moves below Rules: both columns no longer fit at their minimum.</summary>
    public const double StackedBelowWidth = 900;

    /// <summary>The event log's height while the panes are stacked, so both lists keep their height on a small screen.</summary>
    private const double StackedLogHeight = 200;

    private bool _stacked;
    private GridLength _logHeight;

    public EventRulesView()
    {
        InitializeComponent();
        RuleGrid.DoubleTapped += OnRuleDoubleTapped;
        RecipientGrid.DoubleTapped += OnRecipientDoubleTapped;
        SizeChanged += (_, e) => Stack(e.NewSize.Width < StackedBelowWidth);
    }

    /// <summary>True while Recipients sits below Rules.</summary>
    public bool IsStacked => _stacked;

    private void Stack(bool stacked)
    {
        if (stacked == _stacked)
        {
            return;
        }

        _stacked = stacked;
        var log = PageGrid.RowDefinitions[LogRow];
        if (stacked)
        {
            _logHeight = log.Height;
            log.Height = new GridLength(Math.Min(StackedLogHeight, log.ActualHeight > 0 ? log.ActualHeight : StackedLogHeight));
        }
        else
        {
            log.Height = _logHeight;
        }

        Panes.ColumnDefinitions = stacked
            ? [new ColumnDefinition(GridLength.Star)]
            : [
                new ColumnDefinition(new GridLength(7, GridUnitType.Star)) { MinWidth = 560 },
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(3, GridUnitType.Star)) { MinWidth = 280 },
            ];
        Panes.RowDefinitions = stacked
            ? [new RowDefinition(new GridLength(3, GridUnitType.Star)) { MinHeight = 160 }, new RowDefinition(GridLength.Auto), new RowDefinition(new GridLength(2, GridUnitType.Star)) { MinHeight = 160 }]
            : [new RowDefinition(GridLength.Star)];
        Grid.SetColumn(PaneSplitter, stacked ? 0 : 1);
        Grid.SetRow(PaneSplitter, stacked ? 1 : 0);
        Grid.SetColumn(RecipientsCard, stacked ? 0 : 2);
        Grid.SetRow(RecipientsCard, stacked ? 2 : 0);
        PaneSplitter.Classes.Set("vertical", !stacked);
    }

    private const int LogRow = 3;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as EventRulesViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as EventRulesViewModel)?.Deactivate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null)
        {
            (DataContext as EventRulesViewModel)?.Activate();
        }
    }

    private void OnRecipientDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is EventRulesViewModel { SelectedRecipient: { } recipient } vm && vm.EditRecipientCommand.CanExecute(recipient))
        {
            vm.EditRecipientCommand.Execute(recipient);
        }
    }

    private void OnRuleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is EventRulesViewModel { SelectedRule: { } rule } vm && vm.EditCommand.CanExecute(rule))
        {
            vm.EditCommand.Execute(rule);
        }
    }
}
