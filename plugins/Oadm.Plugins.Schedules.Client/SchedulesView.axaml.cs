using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.Schedules.Client;

/// <summary>The Schedules page. View only: follows the server while shown, a double click edits.</summary>
public partial class SchedulesView : UserControl
{
    public SchedulesView()
    {
        InitializeComponent();
        ScheduleGrid.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: ScheduleRow } && DataContext is SchedulesViewModel vm && vm.EditCommand.CanExecute(null))
            {
                vm.EditCommand.Execute(null);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as SchedulesViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as SchedulesViewModel)?.Deactivate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null)
        {
            (DataContext as SchedulesViewModel)?.Activate();
        }
    }
}
