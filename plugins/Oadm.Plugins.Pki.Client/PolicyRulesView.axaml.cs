using Avalonia.Controls;
using Avalonia.Interactivity;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// One rule list of the Policies tab. View only: the Devices column only for kinds with devices, Add (a menu of the kinds
/// when the list has several), the row's ⋯ menu and a double click run the view model's commands.
/// </summary>
public partial class PolicyRulesView : UserControl
{
    public PolicyRulesView()
    {
        InitializeComponent();
        RulesGrid.DoubleTapped += (_, _) =>
        {
            if (DataContext is PolicySectionViewModel section && RulesGrid.SelectedItem is PolicyRowViewModel row)
            {
                section.EditRowCommand.Execute(row);
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RulesGrid.Columns[1].IsVisible = DataContext is PolicySectionViewModel { HasDevices: true };
    }

    /// <summary>Add: the editor of the list's kind at once, or a menu of its kinds ("HTTPS certificate", "IEEE 802.1X certificate").</summary>
    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PolicySectionViewModel section)
        {
            return;
        }

        if (section.AddChoices.Count == 1)
        {
            section.AddCommand.Execute(section.AddChoices[0].Value);
            return;
        }

        RowActions.Show(AddButton, [.. section.AddChoices.Select(c => new IssuedCertificateAction(c.Label, section.AddCommand, c.Value))]);
    }

    /// <summary>A row's ⋯ button: Edit; Delete in red apart from it, not for a system policy.</summary>
    private void OnRowActions(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PolicyRowViewModel row } button || DataContext is not PolicySectionViewModel section)
        {
            return;
        }

        List<IssuedCertificateAction> actions = [new("Edit", section.EditRowCommand, row)];
        if (row.CanDelete)
        {
            actions.Add(IssuedCertificateAction.Separator);
            actions.Add(new("Delete", section.DeleteRowCommand, row, IsDanger: true));
        }

        RowActions.Show(button, actions);
    }
}
