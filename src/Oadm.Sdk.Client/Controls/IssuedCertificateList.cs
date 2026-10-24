using Avalonia;
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
/// left, the filters (state, use, issued by) and the search right, the progress row of a later read or a write of the page
/// (<see cref="IssuedCertificateListViewModel.WorkText"/>), the virtualized grid (name or device, use, issued by, valid
/// until, used by, state chip with the revocation as tooltip, the page's row buttons of <see cref="RowActions"/>; several
/// rows can be chosen, a double click is View certificate), the empty line, the overlay of the first read, an error of the
/// whole table directly below it and the pages (Previous / Next, "1-500 of 12,345"). The DataContext is an
/// <see cref="IssuedCertificateListViewModel"/>; the grid's selection reaches it in one step.
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
        Table.Columns.Add(TextColumn("Name", nameof(IssuedCertificateRow.Name), new DataGridLength(1.4, DataGridLengthUnitType.Star)));
        Table.Columns.Add(TextColumn("Use", nameof(IssuedCertificateRow.Use), new DataGridLength(110)));
        Table.Columns.Add(TextColumn("Issued by", nameof(IssuedCertificateRow.IssuedBy), new DataGridLength(1, DataGridLengthUnitType.Star)));
        Table.Columns.Add(TextColumn("Valid until", nameof(IssuedCertificateRow.ValidUntil), new DataGridLength(110)));
        Table.Columns.Add(TextColumn("Used by", nameof(IssuedCertificateRow.UsedBy), new DataGridLength(1, DataGridLengthUnitType.Star)));
        Table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "State",
            Width = new DataGridLength(140),
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                var chip = new StatusChip { Margin = new Thickness(8, 0) };
                chip.Bind(StatusChip.TextProperty, new Binding(nameof(IssuedCertificateRow.State)));
                chip.Bind(StatusChip.IsOkProperty, new Binding(nameof(IssuedCertificateRow.IsOk)));
                chip.Bind(StatusChip.IsWarningProperty, new Binding(nameof(IssuedCertificateRow.IsWarning)));
                chip.Bind(StatusChip.IsErrorProperty, new Binding(nameof(IssuedCertificateRow.IsError)));
                chip.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateRow.StateTip)));
                return chip;
            }, supportsRecycling: true),
        });
        Table.Bind(DataGrid.ItemsSourceProperty, new Binding(nameof(IssuedCertificateListViewModel.Rows)));
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
                _actions = new DataGridTemplateColumn { Width = new DataGridLength(250), CellTemplate = template };
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
        }

        _vm = DataContext as IssuedCertificateListViewModel;
        if (_vm is not null)
        {
            _vm.SelectionRestored += OnSelectionRestored;
        }
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
