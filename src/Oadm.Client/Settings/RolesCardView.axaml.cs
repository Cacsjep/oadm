using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Settings;

/// <summary>
/// The permission matrix. The first column (the permission, or a group header with its expander) is frozen; one column
/// per role follows, built here from <see cref="RolesViewModel.Columns"/> whenever they change (the number of roles is
/// not known in XAML). Every check box binds to its <see cref="PermissionCellViewModel"/>.
/// </summary>
public sealed partial class RolesCardView : UserControl
{
    /// <summary>Tooltip of the warning icon.</summary>
    public const string FullDeviceControlTip = "Full device control: uses the stored device login and can change anything on the devices.";

    private RolesViewModel? _vm;

    public RolesCardView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ColumnsChanged -= OnColumnsChanged;
        }

        _vm = DataContext as RolesViewModel;
        if (_vm is not null)
        {
            _vm.ColumnsChanged += OnColumnsChanged;
        }

        BuildColumns();
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildColumns();

    private void BuildColumns()
    {
        var grid = Matrix;
        grid.Columns.Clear();
        if (_vm is null)
        {
            return;
        }

        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Permission",
            Width = new DataGridLength(340),
            // Not recycled: the cell is built from its row (group or permission, name, warning), so a reused cell would show
            // another row after collapsing a group or searching.
            CellTemplate = new FuncDataTemplate<PermissionRowViewModel>((row, _) => row is null ? null : row.IsGroup ? GroupCell(row) : PermissionCell(row), supportsRecycling: false),
        });

        for (int i = 0; i < _vm.Columns.Count; i++)
        {
            var role = _vm.Columns[i];
            int index = i;
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = RoleHeader(role),
                Width = new DataGridLength(150),
                CellTemplate = new FuncDataTemplate<PermissionRowViewModel>((row, _) => row is null ? null : new CheckBox
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsThreeState = row.IsGroup,
                    [!ToggleButton.IsCheckedProperty] = new Binding($"{nameof(PermissionRowViewModel.Cells)}[{index}].{nameof(PermissionCellViewModel.IsChecked)}") { Mode = BindingMode.TwoWay },
                    [!IsEnabledProperty] = new Binding($"{nameof(PermissionRowViewModel.Cells)}[{index}].{nameof(PermissionCellViewModel.IsEnabled)}"),
                }, supportsRecycling: false),
            });
        }
    }

    /// <summary>The group header: expander, the group's icon and name.</summary>
    private StackPanel GroupCell(PermissionRowViewModel row)
    {
        var expander = new ToggleButton { Classes = { "groupExpander" }, Focusable = false, IsChecked = row.IsExpanded };
        expander.Click += (_, _) => _vm?.ToggleGroupCommand.Execute(row);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(expander);
        if (Icon(row.IconKey) is { } icon)
        {
            panel.Children.Add(new OadmIcon { Data = icon, VerticalAlignment = VerticalAlignment.Center });
        }

        panel.Children.Add(new TextBlock { Text = row.Title, Classes = { "groupHeader" }, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>
    /// A permission: its name (the explanation as its tooltip) and, right after it, a warning for full device control with
    /// a tooltip that says what it means.
    /// </summary>
    private static StackPanel PermissionCell(PermissionRowViewModel row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(34, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = row.Title, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(row.Description))
        {
            ToolTip.SetTip(name, row.Description);
        }

        panel.Children.Add(name);
        if (row.FullDeviceControl && Icon("status.warning") is { } warning)
        {
            // A transparent background makes the whole icon area show the tooltip, not only its strokes.
            var mark = new Border
            {
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new OadmIcon { Data = warning, Classes = { "warn" } },
            };
            ToolTip.SetTip(mark, FullDeviceControlTip);
            panel.Children.Add(mark);
        }

        if (row.IsUnavailable)
        {
            panel.Children.Add(new TextBlock { Text = "Plugin is off", Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center });
        }

        return panel;
    }

    /// <summary>The role's column header: name, "3 users" and its menu.</summary>
    private Grid RoleHeader(RoleColumnViewModel role)
    {
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(RoleColumnViewModel.Name)), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(RoleColumnViewModel.Subtitle)), Classes = { "small", "secondary" } });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), DataContext = role };
        ToolTip.SetTip(grid, role.DescriptionTip);
        grid.Children.Add(text);
        if (!role.IsAdministrator && _vm is { } vm)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuItem { Header = "Rename", Command = vm.RenameRoleCommand, CommandParameter = role });
            menu.Items.Add(new MenuItem { Header = "Copy", Command = vm.CopyRoleCommand, CommandParameter = role });
            if (role.CanReset)
            {
                menu.Items.Add(new MenuItem { Header = "Reset to default", Command = vm.ResetRoleCommand, CommandParameter = role });
            }

            if (role.CanDelete)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Delete", Command = vm.DeleteRoleCommand, CommandParameter = role });
            }

            var button = new Button { Classes = { "toolbar", "iconOnly" }, Flyout = menu, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(button, "Role actions");
            if (Icon("more") is { } more)
            {
                button.Content = new OadmIcon { Data = more };
            }

            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }
        else if (_vm is { } admin)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuItem { Header = "Copy", Command = admin.CopyRoleCommand, CommandParameter = role });
            var button = new Button { Classes = { "toolbar", "iconOnly" }, Flyout = menu, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(button, "Role actions");
            if (Icon("more") is { } more)
            {
                button.Content = new OadmIcon { Data = more };
            }

            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return grid;
    }

    /// <summary>An icon of the theme (the columns are built before the card is in a window, so the application is asked).</summary>
    private static Geometry? Icon(string? key) =>
        !string.IsNullOrEmpty(key) && Application.Current is { } app && app.TryFindResource("Icon." + key, app.ActualThemeVariant, out var value) ? value as Geometry : null;
}
