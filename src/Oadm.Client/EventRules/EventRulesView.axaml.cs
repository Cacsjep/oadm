using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Oadm.Client.EventRules;

/// <summary>The Event rules page. View only: streams run while it is shown; a double click on a rule edits it.</summary>
public partial class EventRulesView : UserControl
{
    public EventRulesView()
    {
        InitializeComponent();
        RuleGrid.DoubleTapped += OnRuleDoubleTapped;
    }

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

    private void OnRuleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is EventRulesViewModel { SelectedRule: { } rule } vm && vm.EditCommand.CanExecute(rule))
        {
            vm.EditCommand.Execute(rule);
        }
    }
}
