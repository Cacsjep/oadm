using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Certificates;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The list of certificates (SDK 0.4.0), the Certificates card of the PKI page: the page's buttons (<see cref="Toolbar"/>)
/// left, Group by (when the source groups), the filters (state, use, issued by) and the search right, the progress row of a
/// later read or a write of the page (<see cref="IssuedCertificateListViewModel.WorkText"/>), the virtualized grid (name or
/// device with the key and issuer below it, the type as badges, valid until, used by, the state chip with the days left or
/// the revocation as tooltip and the page's statuses below it, the page's row buttons of <see cref="RowActions"/>; rows grow
/// to two lines, group headers with their counts over every page; several rows can be chosen, a double click is View
/// certificate), the empty line, the overlay of the first read, an error of the whole table directly below it and the
/// pages (Previous / Next, "1-500 of 12,345"). The DataContext is an <see cref="IssuedCertificateListViewModel"/>; the
/// grid's selection reaches it in one step.
/// </summary>
public sealed class IssuedCertificateList : UserControl
{
    /// <summary>The page's buttons left of the filters (View certificate, Revoke).</summary>
    public static readonly StyledProperty<Control?> ToolbarProperty =
        AvaloniaProperty.Register<IssuedCertificateList, Control?>(nameof(Toolbar));

    /// <summary>The row buttons of the page (the last column, e.g. View, Export, Revoke, Delete); null: no such column.</summary>
    public static readonly StyledProperty<IDataTemplate?> RowActionsProperty =
        AvaloniaProperty.Register<IssuedCertificateList, IDataTemplate?>(nameof(RowActions));

    /// <summary>The context menu of the grid's rows.</summary>
    public static readonly StyledProperty<ContextMenu?> RowContextMenuProperty =
        AvaloniaProperty.Register<IssuedCertificateList, ContextMenu?>(nameof(RowContextMenu));

    private readonly ContentControl _toolbar = new() { VerticalAlignment = VerticalAlignment.Center };
    private IssuedCertificateListViewModel? _vm;
    private bool _restoring;
    private DataGridTemplateColumn? _actions;

    public IssuedCertificateList()
    {
        Table = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = false,
            CanUserReorderColumns = false,
            SelectionMode = DataGridSelectionMode.Extended,
            MinHeight = 200,
        };
        Table.Classes.Add("wrapRows"); // a second line of the name, badges and statuses that wrap
        Table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Name",
            Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
            MinWidth = 160,
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateRow.Name)));
                name.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateRow.Name)));
                var detail = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                detail.Classes.Add("secondary");
                detail.Classes.Add("small");
                detail.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateRow.Detail)));
                detail.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateRow.Detail)));
                detail.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateRow.HasDetail)));
                return new StackPanel { Margin = new Thickness(10, 4), Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, detail } };
            }, supportsRecycling: true),
        });
        Table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Type",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 130,
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                var badges = new ItemsControl
                {
                    Margin = new Thickness(10, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 }),
                    ItemTemplate = new FuncDataTemplate<string>((_, _) =>
                    {
                        var text = new TextBlock();
                        text.Bind(TextBlock.TextProperty, new Binding("."));
                        var badge = new Border { Child = text };
                        badge.Classes.Add("badge");
                        badge.Classes.Add("neutral");
                        return badge;
                    }, supportsRecycling: true),
                };
                badges.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(IssuedCertificateRow.Badges)));
                return badges;
            }, supportsRecycling: true),
        });
        Table.Columns.Add(TextColumn("Valid until", nameof(IssuedCertificateRow.ValidUntil), new DataGridLength(100)));
        Table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Used by",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 110,
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                var text = new TextBlock { Margin = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateRow.UsedBy)));
                text.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateRow.UsedBy)));
                return text;
            }, supportsRecycling: true),
        });
        Table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "State",
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            MinWidth = 175,
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                var chip = new StatusChip();
                chip.Bind(StatusChip.TextProperty, new Binding(nameof(IssuedCertificateRow.StateText)));
                chip.Bind(StatusChip.IsOkProperty, new Binding(nameof(IssuedCertificateRow.IsOk)));
                chip.Bind(StatusChip.IsWarningProperty, new Binding(nameof(IssuedCertificateRow.IsWarning)));
                chip.Bind(StatusChip.IsErrorProperty, new Binding(nameof(IssuedCertificateRow.IsError)));
                chip.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateRow.StateTip)));
                var more = new ItemsControl
                {
                    ItemTemplate = new FuncDataTemplate<IssuedCertificateStatus>((_, _) =>
                    {
                        var status = new StatusChip();
                        status.Bind(StatusChip.TextProperty, new Binding(nameof(IssuedCertificateStatus.Text)));
                        status.Bind(StatusChip.IsOkProperty, new Binding(nameof(IssuedCertificateStatus.IsOk)));
                        status.Bind(StatusChip.IsWarningProperty, new Binding(nameof(IssuedCertificateStatus.IsWarning)));
                        status.Bind(StatusChip.IsErrorProperty, new Binding(nameof(IssuedCertificateStatus.IsError)));
                        status.Bind(StatusChip.IsAccentProperty, new Binding(nameof(IssuedCertificateStatus.IsAccent)));
                        status.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateStatus.Tip)));
                        return status;
                    }, supportsRecycling: true),
                };
                more.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(IssuedCertificateRow.MoreStates)));
                more.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateRow.HasMoreStates)));
                return new StackPanel { Margin = new Thickness(8, 4), Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { chip, more } };
            }, supportsRecycling: true),
        });
        Table.SelectionChanged += (_, _) =>
        {
            if (!_restoring)
            {
                _vm?.SetSelection(Table.SelectedItems.OfType<IssuedCertificateRow>());
            }
        };
        Table.DoubleTapped += (_, _) =>
        {
            if (_vm?.ViewCertificateCommand.CanExecute(null) == true)
            {
                _vm.ViewCertificateCommand.Execute(null);
            }
        };

        var filters = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var groupBy = new ComboBox { Width = 150 };
        groupBy.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(IssuedCertificateListViewModel.GroupChoices)));
        groupBy.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(IssuedCertificateListViewModel.GroupBy)) { Mode = BindingMode.TwoWay });
        groupBy.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateListViewModel.CanGroup)));
        filters.Children.Add(groupBy);
        filters.Children.Add(Filter(nameof(IssuedCertificateListViewModel.StateChoices), nameof(IssuedCertificateListViewModel.StateFilter)));
        filters.Children.Add(Filter(nameof(IssuedCertificateListViewModel.UseChoices), nameof(IssuedCertificateListViewModel.UseFilter)));
        filters.Children.Add(Filter(nameof(IssuedCertificateListViewModel.SourceChoices), nameof(IssuedCertificateListViewModel.SourceFilter)));
        var search = new SearchBox { Placeholder = "Search", Width = 260 };
        search.Bind(SearchBox.TextProperty, new Binding(nameof(IssuedCertificateListViewModel.Search)) { Mode = BindingMode.TwoWay });
        filters.Children.Add(search);
        // The filters take the rest of the row (not Auto): on a narrow card they wrap, the search box below, never cut off.
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        Grid.SetColumn(filters, 1);
        bar.Children.AddRange([_toolbar, filters]);

        var progress = new ProgressRow { Margin = new Thickness(0, 8, 0, 0) };
        progress.Bind(ProgressRow.TextProperty, new Binding(nameof(IssuedCertificateListViewModel.ProgressText)));
        progress.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateListViewModel.ShowsProgress)));

        var empty = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        empty.Classes.Add("secondary");
        empty.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateListViewModel.EmptyText)));
        empty.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateListViewModel.EmptyText)) { Converter = StringConverters.IsNotNullOrEmpty });
        var overlay = new LoadingOverlay { Text = IssuedCertificateTexts.Reading };
        overlay.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateListViewModel.IsLoading)));
        var table = new Panel { Margin = new Thickness(0, 8, 0, 0), Children = { Table, empty, overlay } };

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        error.Classes.Add("error");
        error.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateListViewModel.Error)));
        error.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateListViewModel.HasError)));
        var pageText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        pageText.Classes.Add("secondary");
        pageText.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateListViewModel.PageText)));
        var previous = PageButton("Previous", "Icon.chevronLeft", nameof(IssuedCertificateListViewModel.PreviousPageCommand), nameof(IssuedCertificateListViewModel.PreviousText));
        var next = PageButton("Next", "Icon.chevronRight", nameof(IssuedCertificateListViewModel.NextPageCommand), nameof(IssuedCertificateListViewModel.NextText));
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pageText, previous, next } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(pages, 1);
        footer.Children.AddRange([error, pages]);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Grid.SetRow(progress, 1);
        Grid.SetRow(table, 2);
        Grid.SetRow(footer, 3);
        root.Children.AddRange([bar, progress, table, footer]);
        Content = root;
    }

    /// <summary>The grid (tests and the page's own handling).</summary>
    public DataGrid Table { get; }

    /// <inheritdoc cref="ToolbarProperty"/>
    public Control? Toolbar
    {
        get => GetValue(ToolbarProperty);
        set => SetValue(ToolbarProperty, value);
    }

    /// <inheritdoc cref="RowActionsProperty"/>
    public IDataTemplate? RowActions
    {
        get => GetValue(RowActionsProperty);
        set => SetValue(RowActionsProperty, value);
    }

    /// <inheritdoc cref="RowContextMenuProperty"/>
    public ContextMenu? RowContextMenu
    {
        get => GetValue(RowContextMenuProperty);
        set => SetValue(RowContextMenuProperty, value);
    }

    /// <summary>
    /// The revoke confirmation with the reason (the shared <see cref="MessageWindow"/>, red Revoke); null for Cancel.
    /// </summary>
    public static async Task<RevocationReason?> ConfirmRevokeAsync(Window owner, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var reasons = IssuedCertificateTexts.Reasons;
        var chosen = await MessageWindow.ConfirmWithChoiceAsync(owner, title, message, "Revoke", "Reason",
            [.. reasons.Select(CertificateRevocation.ReasonText)], danger: true).ConfigureAwait(true);
        return chosen is { } index && index >= 0 && index < reasons.Count ? reasons[index] : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ToolbarProperty)
        {
            _toolbar.Content = Toolbar;
        }
        else if (change.Property == RowContextMenuProperty)
        {
            Table.ContextMenu = RowContextMenu;
        }
        else if (change.Property == RowActionsProperty)
        {
            if (_actions is not null)
            {
                Table.Columns.Remove(_actions);
                _actions = null;
            }

            if (RowActions is { } template)
            {
                // A fixed width: an Auto column takes the width of the first rows only and cuts off wider buttons further down.
                _actions = new DataGridTemplateColumn { Width = new DataGridLength(240), CellTemplate = template };
                Table.Columns.Add(_actions);
            }
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.SelectionRestored -= OnSelectionRestored;
            _vm.GroupsChanged -= OnGroupsChanged;
        }

        _vm = DataContext as IssuedCertificateListViewModel;
        if (_vm is not null)
        {
            _vm.SelectionRestored += OnSelectionRestored;
            _vm.GroupsChanged += OnGroupsChanged;
        }

        Regroup();
    }

    private void OnGroupsChanged(object? sender, EventArgs e) => Regroup();

    /// <summary>
    /// The rows, grouped by their group header while the list has groups (the shared <c>DataGrid.groups</c> look: chevron and
    /// "Device certificates · 60,000"); the server sends them in the groups' order.
    /// </summary>
    private void Regroup()
    {
        Table.Classes.Set("groups", _vm?.IsGrouped == true);
        if (_vm is null)
        {
            Table.ItemsSource = null;
            return;
        }

        if (!_vm.IsGrouped)
        {
            Table.ItemsSource = _vm.Rows;
            return;
        }

        var view = new DataGridCollectionView(_vm.Rows);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(IssuedCertificateRow.GroupTitle)));
        Table.ItemsSource = view;
    }

    /// <summary>Selects the kept rows again after the page was read (the grid lost them with the rows).</summary>
    private void OnSelectionRestored(object? sender, EventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        _restoring = true;
        try
        {
            Table.SelectedItems.Clear();
            foreach (var row in _vm.Selected)
            {
                Table.SelectedItems.Add(row);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private static DataGridTextColumn TextColumn(string header, string property, DataGridLength width) =>
        new() { Header = header, Width = width, Binding = new Binding(property) };

    private static ComboBox Filter(string choices, string selected)
    {
        var box = new ComboBox
        {
            Width = 150,
            ItemTemplate = new FuncDataTemplate<IssuedCertificateFilter>((_, _) =>
            {
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateFilter.Label)));
                return text;
            }, supportsRecycling: true),
        };
        box.Bind(ItemsControl.ItemsSourceProperty, new Binding(choices));
        box.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(selected) { Mode = BindingMode.TwoWay });
        return box;
    }

    private static Button PageButton(string text, string icon, string command, string tip)
    {
        var label = new IconLabel { Text = text, Spacing = 4 };
        label.Bind(IconLabel.IconProperty, label.GetResourceObservable(icon));
        var button = new Button { Content = label };
        button.Classes.Add("toolbar");
        button.Bind(Button.CommandProperty, new Binding(command));
        button.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateListViewModel.HasPages)));
        button.Bind(ToolTip.TipProperty, new Binding(tip));
        return button;
    }
}
