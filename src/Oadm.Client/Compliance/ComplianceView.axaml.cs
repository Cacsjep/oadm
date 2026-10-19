using System.Collections;
using System.ComponentModel;

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;

using Oadm.Client.Devices;
using Oadm.Client.Tags;
using Oadm.Contracts.V1;

namespace Oadm.Client.Compliance;

/// <summary>
/// The Compliance page. View only: one column per check (wide with the result text), the columns the chooser turned off
/// hidden, and in group mode the (device x tag) rows grouped by tag like the Devices page.
/// </summary>
public partial class ComplianceView : UserControl
{
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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TagGrouping.GroupsChanged -= OnGroupsChanged;
        }

        _viewModel = DataContext as ComplianceViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ColumnsChanged += OnColumnsChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.TagGrouping.GroupsChanged += OnGroupsChanged;
        }

        ApplyItemsSource();
        BuildCheckColumns();
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildCheckColumns();

    private void OnGroupsChanged(object? sender, EventArgs e)
    {
        if (_viewModel?.GroupByTag == true)
        {
            ApplyItemsSource();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComplianceViewModel.GroupByTag))
        {
            ApplyItemsSource();
        }
    }

    /// <summary>The device rows, or in group mode the (device x tag) rows grouped by tag in tag name order, "No tag" last.</summary>
    private void ApplyItemsSource()
    {
        if (_viewModel is not { } vm)
        {
            ResultGrid.ItemsSource = null;
            return;
        }

        if (!vm.GroupByTag)
        {
            ResultGrid.Classes.Remove("tagGroups");
            ResultGrid.ItemsSource = vm.Rows;
            return;
        }

        var view = new DataGridCollectionView(vm.TagGrouping.Rows);
        var byTag = new DataGridPathGroupDescription(nameof(DeviceTagRow.Group));
        foreach (TagGroupKey key in vm.TagGrouping.Groups)
        {
            byTag.GroupKeys.Add(key);
        }

        view.GroupDescriptions.Add(byTag);
        ResultGrid.Classes.Add("tagGroups");
        ResultGrid.ItemsSource = view;
    }

    private void BuildCheckColumns()
    {
        foreach (var column in ResultGrid.Columns.Where(c => c.Tag is string tag && tag.StartsWith(ComplianceViewModel.CheckColumnPrefix, StringComparison.Ordinal)).ToList())
        {
            ResultGrid.Columns.Remove(column);
        }

        if (_viewModel is not { } vm)
        {
            return;
        }

        foreach (var column in ResultGrid.Columns)
        {
            if (column.Tag is ComplianceViewModel.ModelColumn or ComplianceViewModel.TagsColumn or ComplianceViewModel.ResultColumn)
            {
                column.IsVisible = vm.IsColumnVisible((string)column.Tag);
            }
        }

        foreach (var check in vm.ColumnChecks)
        {
            var column = CheckColumn(vm.Store, check, vm.ShowText);
            column.IsVisible = vm.IsColumnVisible(ComplianceViewModel.CheckColumnPrefix + check.Id);
            ResultGrid.Columns.Add(column);
        }
    }

    /// <summary>
    /// One check: an icon per device (with <paramref name="showText"/> the result text next to it, a wider column), sorted
    /// failed first; the header names the check. Shared with the Devices grid.
    /// </summary>
    internal static DataGridTemplateColumn CheckColumn(ComplianceStore store, ComplianceCheckSummary check, bool showText = false)
    {
        var id = check.Id;
        return new DataGridTemplateColumn
        {
            Tag = ComplianceViewModel.CheckColumnPrefix + id,
            Header = new TextBlock { Text = check.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, [ToolTip.TipProperty] = check.Summary },
            Width = new DataGridLength(showText ? 220 : 120),
            MinWidth = 100,
            CanUserSort = true,
            CustomSortComparer = new OutcomeComparer(id),
            CellTemplate = new FuncDataTemplate<IDeviceGridItem>((_, _) => new ComplianceResultCell(store, id, showText)
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
