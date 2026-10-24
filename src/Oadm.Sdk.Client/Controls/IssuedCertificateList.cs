using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Oadm.Sdk.Certificates;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The list of certificates (SDK 0.4.0), the Certificates card of the PKI page: the page's buttons (<see cref="Toolbar"/>)
/// left, Group by (when the source groups), the filters (state, use, issued by) and the search right, the progress row of a
/// later read or a write of the page (<see cref="IssuedCertificateListViewModel.WorkText"/>), the virtualized grid (name or
/// device with the key and issuer below it, the type as badges with the page's statuses below them, valid until with a
/// state badge only when it needs attention, used by as badges or the device, one ⋯ button with the row's actions menu, the same as its right-click menu; rows grow
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

    /// <summary>A line of the page below the toolbar row, above the list (e.g. the state of the PKI's CA); null for none.</summary>
    public static readonly StyledProperty<Control?> BannerProperty =
        AvaloniaProperty.Register<IssuedCertificateList, Control?>(nameof(Banner));

    private readonly ContentControl _toolbar = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _banner = new();
    private IssuedCertificateListViewModel? _vm;
    private bool _restoring;

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
                // The badges, then the page's statuses of the row (the OADM CA: where it is installed).
                var more = new ItemsControl
                {
                    ItemTemplate = new FuncDataTemplate<IssuedCertificateStatus>((_, _) =>
                    {
                        var status = new StatusChip { MaxLines = 1 };
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
                return new StackPanel { Margin = new Thickness(10, 4), Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { Badges(nameof(IssuedCertificateRow.Badges), wrap: true), more } };
            }, supportsRecycling: true),
        });
        Table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Valid until",
            Width = new DataGridLength(0.8, DataGridLengthUnitType.Star),
            MinWidth = 150,
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                // The date; below it a badge only when the state needs attention (expires soon, expired, revoked, replaced).
                var date = new TextBlock();
                date.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateRow.ValidUntil)));
                var chip = new StatusChip { MaxLines = 1 };
                chip.Bind(StatusChip.TextProperty, new Binding(nameof(IssuedCertificateRow.StateText)));
                chip.Bind(StatusChip.IsWarningProperty, new Binding(nameof(IssuedCertificateRow.IsWarning)));
                chip.Bind(StatusChip.IsErrorProperty, new Binding(nameof(IssuedCertificateRow.IsError)));
                chip.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateRow.StateTip)));
                chip.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateRow.HasStateBadge)));
                return new StackPanel { Margin = new Thickness(10, 4), Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { date, chip } };
            }, supportsRecycling: true),
        });
        Table.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Used by",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 110,
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                // One line: badges (a CA's groups, a server) or the device; every use in the tooltip.
                var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(IssuedCertificateRow.UsedByText)));
                text.Bind(IsVisibleProperty, new Binding(nameof(IssuedCertificateRow.UsedByText)) { Converter = StringConverters.IsNotNullOrEmpty });
                var badges = Badges(nameof(IssuedCertificateRow.UsedByBadges), wrap: false);
                var cell = new Panel { Margin = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Background = Brushes.Transparent, Children = { text, badges } };
                cell.Bind(ToolTip.TipProperty, new Binding(nameof(IssuedCertificateRow.UsedByTip)));
                return cell;
            }, supportsRecycling: true),
        });
        Table.Columns.Add(new DataGridTemplateColumn
        {
            // One fixed narrow column: the ⋯ button opens the row's actions (IssuedCertificateListViewModel.MenuOf).
            Width = new DataGridLength(52),
            CanUserResize = false,
            CellTemplate = new FuncDataTemplate<IssuedCertificateRow>((_, _) =>
            {
                var icon = new OadmIcon();
                icon.Bind(OadmIcon.DataProperty, icon.GetResourceObservable("Icon.more"));
                var button = new Button { Content = icon, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                button.Classes.Add("toolbar");
                button.Classes.Add("iconOnly");
                ToolTip.SetTip(button, IssuedCertificateTexts.ActionsTip);
                button.Click += (_, _) =>
                {
                    if (button.DataContext is IssuedCertificateRow row && _vm is not null)
                    {
                        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
                        foreach (var item in MenuItems(_vm.MenuOf(row)))
                        {
                            flyout.Items.Add(item);
                        }

                        flyout.ShowAt(button);
                    }
                };
                return button;
            }, supportsRecycling: true),
        });
        Table.ContextRequested += OnRowContextRequested;
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

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
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
        // One row: the page's buttons left, the filters and the search right; on a narrow card the filters go below as a whole.
        var bar = new ToolbarRow { Children = { _toolbar, filters } };

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

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Grid.SetRow(_banner, 1);
        Grid.SetRow(progress, 2);
        Grid.SetRow(table, 3);
        Grid.SetRow(footer, 4);
        _banner.IsVisible = false;
        root.Children.AddRange([bar, _banner, progress, table, footer]);
        Content = root;
    }

    /// <summary>The grid (tests and the page's own handling).</summary>
    public DataGrid Table { get; }

    /// <inheritdoc cref="BannerProperty"/>
    public Control? Banner
    {
        get => GetValue(BannerProperty);
        set => SetValue(BannerProperty, value);
    }

    /// <inheritdoc cref="ToolbarProperty"/>
    public Control? Toolbar
    {
        get => GetValue(ToolbarProperty);
        set => SetValue(ToolbarProperty, value);
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
        if (change.Property == BannerProperty)
        {
            _banner.Content = Banner;
            _banner.IsVisible = Banner is not null;
        }
        else if (change.Property == ToolbarProperty)
        {
            _toolbar.Content = Toolbar;
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

    /// <summary>Neutral badges (<c>Border.badge.neutral</c>) of a row's list of texts; on one line or wrapping.</summary>
    private static ItemsControl Badges(string property, bool wrap)
    {
        var badges = new ItemsControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            ItemsPanel = wrap
                ? new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 })
                : new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }),
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
        badges.Bind(ItemsControl.ItemsSourceProperty, new Binding(property));
        return badges;
    }

    /// <summary>A right click on a row: its actions menu (the row is chosen first unless it is one of the chosen rows).</summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not { DataContext: IssuedCertificateRow row } gridRow)
        {
            return;
        }

        if (!Table.SelectedItems.Contains(row))
        {
            Table.SelectedItem = row;
        }

        var menu = new ContextMenu();
        foreach (var item in MenuItems(_vm.MenuOf(row)))
        {
            menu.Items.Add(item);
        }

        menu.Open(gridRow);
        e.Handled = true;
    }

    /// <summary>The menu entries of <paramref name="actions"/>: red ones with the class <c>danger</c>, submenus, separators.</summary>
    public static IEnumerable<Control> MenuItems(IReadOnlyList<IssuedCertificateAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        foreach (var action in actions)
        {
            if (action.IsSeparator)
            {
                yield return new Separator();
                continue;
            }

            var item = new MenuItem { Header = action.Text, Command = action.Command, CommandParameter = action.Parameter };
            if (action.IsDanger)
            {
                item.Classes.Add("danger");
            }

            foreach (var child in MenuItems(action.Items ?? []))
            {
                item.Items.Add(child);
            }

            yield return item;
        }
    }

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

/// <summary>
/// The toolbar row of <see cref="IssuedCertificateList"/>: the first child left, the second right-aligned in the same row
/// while both fit, else the second on a line of its own below, right-aligned (it never wraps inside or gets cut).
/// </summary>
internal sealed class ToolbarRow : Panel
{
    /// <summary>The gap between the two, and between the lines.</summary>
    public const double Gap = 16;

    private bool _twoLines;

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        Size left = default, right = default;
        if (Children.Count > 0)
        {
            Children[0].Measure(infinite);
            left = Children[0].DesiredSize;
        }

        if (Children.Count > 1)
        {
            Children[1].Measure(infinite);
            right = Children[1].DesiredSize;
        }

        _twoLines = left.Width > 0 && right.Width > 0 && left.Width + Gap + right.Width > availableSize.Width;
        return _twoLines
            ? new Size(Math.Max(left.Width, right.Width), left.Height + Gap / 2 + right.Height)
            : new Size(left.Width + (left.Width > 0 && right.Width > 0 ? Gap : 0) + right.Width, Math.Max(left.Height, right.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count > 0)
        {
            var left = Children[0].DesiredSize;
            var top = _twoLines ? 0 : (finalSize.Height - left.Height) / 2;
            Children[0].Arrange(new Rect(0, Math.Max(0, top), left.Width, left.Height));
        }

        if (Children.Count > 1)
        {
            var right = Children[1].DesiredSize;
            var y = _twoLines ? Children[0].DesiredSize.Height + Gap / 2 : (finalSize.Height - right.Height) / 2;
            Children[1].Arrange(new Rect(Math.Max(0, finalSize.Width - right.Width), Math.Max(0, y), right.Width, right.Height));
        }

        return finalSize;
    }
}
