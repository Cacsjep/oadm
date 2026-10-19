using System.Collections;

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Compliance;

/// <summary>The Compliance page. View only: one icon column per check, rebuilt when the checks change.</summary>
public partial class ComplianceView : UserControl
{
    private const string CheckColumnPrefix = "check:";
    private ComplianceViewModel? _viewModel;

    public ComplianceView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.ColumnsChanged -= OnColumnsChanged;
        }

        _viewModel = DataContext as ComplianceViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ColumnsChanged += OnColumnsChanged;
        }

        BuildCheckColumns();
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildCheckColumns();

    private void BuildCheckColumns()
    {
        foreach (var column in ResultGrid.Columns.Where(c => c.Tag is string tag && tag.StartsWith(CheckColumnPrefix, StringComparison.Ordinal)).ToList())
        {
            ResultGrid.Columns.Remove(column);
        }

        if (_viewModel is null)
        {
            return;
        }

        foreach (var check in _viewModel.ColumnChecks)
        {
            ResultGrid.Columns.Add(CheckColumn(_viewModel.Store, check));
        }
    }

    /// <summary>One check: an icon per device, sorted failed first; the header names the check.</summary>
    internal static DataGridTemplateColumn CheckColumn(ComplianceStore store, ComplianceCheckSummary check)
    {
        var id = check.Id;
        return new DataGridTemplateColumn
        {
            Tag = CheckColumnPrefix + id,
            Header = new TextBlock { Text = check.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, [ToolTip.TipProperty] = check.Summary },
            Width = new DataGridLength(120),
            MinWidth = 100,
            CanUserSort = true,
            CustomSortComparer = new OutcomeComparer(id),
            CellTemplate = new FuncDataTemplate<IDeviceGridItem>((_, _) => new ComplianceResultCell(store, id)
            {
                [!ComplianceResultCell.ComplianceProperty] = new Binding("Row.Compliance"),
            }),
        };
    }

    /// <summary>Failed first, then could not check, not checked, not applicable, passed, none.</summary>
    private sealed class OutcomeComparer(string checkId) : IComparer
    {
        public int Compare(object? x, object? y) => Rank(x).CompareTo(Rank(y));

        private int Rank(object? item) => (item as IDeviceGridItem)?.Row.Compliance.Get(checkId)?.Outcome switch
        {
            ComplianceOutcome.ComplianceFailed => 0,
            ComplianceOutcome.ComplianceCouldNotCheck => 1,
            ComplianceOutcome.ComplianceNotChecked => 2,
            ComplianceOutcome.ComplianceNotApplicable => 3,
            ComplianceOutcome.CompliancePassed => 4,
            _ => 5,
        };
    }
}
