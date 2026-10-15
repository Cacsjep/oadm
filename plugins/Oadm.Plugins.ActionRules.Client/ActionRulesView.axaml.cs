using Avalonia;
using Avalonia.Controls;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>Shows the page; the view model connects while the view is in the visual tree. Opens the page's dialogs.</summary>
public partial class ActionRulesView : UserControl, IRuleDialogs
{
    private bool _restoring;
    private ActionRulesViewModel? _model;

    public ActionRulesView()
    {
        InitializeComponent();
        GroupGrid.SelectionChanged += (_, _) =>
        {
            if (!_restoring)
            {
                (DataContext as ActionRulesViewModel)?.SetGroupSelection(GroupGrid.SelectedItems.OfType<RuleGroupRow>());
            }
        };
        MemberGrid.SelectionChanged += (_, _) => (DataContext as ActionRulesViewModel)?.SetMemberSelection(MemberGrid.SelectedItems.OfType<RuleMemberRow>());
        HookPresets(RecipientGroupGrid, RecipientMemberGrid, vm => vm.Recipients);
        HookPresets(ScheduleGroupGrid, ScheduleMemberGrid, vm => vm.Schedules);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is { } old)
        {
            old.SelectionRestored -= OnRulesRestored;
            old.Recipients.SelectionRestored -= OnRecipientsRestored;
            old.Schedules.SelectionRestored -= OnSchedulesRestored;
        }

        _model = DataContext as ActionRulesViewModel;
        if (_model is { } vm)
        {
            vm.Dialogs = this;
            vm.SelectionRestored += OnRulesRestored;
            vm.Recipients.SelectionRestored += OnRecipientsRestored;
            vm.Schedules.SelectionRestored += OnSchedulesRestored;
        }
    }

    private void OnRulesRestored(object? sender, IReadOnlyList<RuleGroupRow> rows) => Restore(GroupGrid, rows);

    private void OnRecipientsRestored(object? sender, IReadOnlyList<PresetGroupRow> rows) => Restore(RecipientGroupGrid, rows);

    private void OnSchedulesRestored(object? sender, IReadOnlyList<PresetGroupRow> rows) => Restore(ScheduleGroupGrid, rows);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ActionRulesViewModel)?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as ActionRulesViewModel)?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private void HookPresets(DataGrid groups, DataGrid members, Func<ActionRulesViewModel, PresetTabViewModel> tab)
    {
        groups.SelectionChanged += (_, _) =>
        {
            if (!_restoring && DataContext is ActionRulesViewModel vm)
            {
                tab(vm).SetGroupSelection(groups.SelectedItems.OfType<PresetGroupRow>());
            }
        };
        members.SelectionChanged += (_, _) =>
        {
            if (DataContext is ActionRulesViewModel vm)
            {
                tab(vm).SetMemberSelection(members.SelectedItems.OfType<PresetMemberRow>());
            }
        };
    }

    /// <summary>The groups were built again: select the ones that were selected (same key) without a selection round trip.</summary>
    private void Restore<T>(DataGrid grid, IReadOnlyList<T> rows)
    {
        _restoring = true;
        try
        {
            grid.SelectedItems.Clear();
            foreach (var row in rows)
            {
                grid.SelectedItems.Add(row);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    public async Task<RulePatch?> EditAsync(EditRulesViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new EditRulesWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowAsync(window).ConfigureAwait(true);
        return ok ? form.Patch() : null;
    }

    public async Task<Guid?> ChooseUndoAsync(UndoViewModel list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var ok = false;
        var window = new UndoWindow { DataContext = list };
        list.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowAsync(window).ConfigureAwait(true);
        return ok ? list.Selected?.Run.RunId : null;
    }

    public async Task<IReadOnlyList<Guid>?> ChooseDevicesAsync(string heading, Func<Guid, string?> unavailableReason)
    {
        ArgumentNullException.ThrowIfNull(unavailableReason);
        return DataContext is ActionRulesViewModel vm && TopLevel.GetTopLevel(this) is Window owner
            ? await DevicePickerWindow.ShowAsync(owner, vm.AllDevices, null, d => unavailableReason(d.Id), heading).ConfigureAwait(true)
            : null;
    }

    public async Task<RecipientPatch?> EditRecipientsAsync(RecipientFormViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new RecipientFormWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowAsync(window).ConfigureAwait(true);
        return ok ? form.Patch() : null;
    }

    public async Task<NewRule?> NewRuleAsync(NewRuleViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        NewRule? rule = null;
        var window = new NewRuleWindow { DataContext = form };
        form.CloseRequested += (_, ok) =>
        {
            rule = ok ? form.Rule() : null;
            window.Close();
        };
        await ShowAsync(window).ConfigureAwait(true);
        return rule;
    }

    public async Task<bool> EditScheduleAsync(ScheduleFormViewModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var ok = false;
        var window = new ScheduleFormWindow { DataContext = form };
        form.CloseRequested += (_, result) =>
        {
            ok = result;
            window.Close();
        };
        await ShowAsync(window).ConfigureAwait(true);
        return ok;
    }

    private async Task ShowAsync(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
    }
}
