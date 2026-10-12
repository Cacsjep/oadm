using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Oadm.Client.Settings;

/// <summary>
/// The dialog of the roles card for a new role, a copy or a rename (<see cref="RolesViewModel.IsEditorOpen"/>). View
/// only: it closes when the editor is done and tells the view model when the user closes it.
/// </summary>
public partial class RoleFormWindow : Window
{
    /// <summary>"Empty" in the Start from select (the null choice).</summary>
    private const string EmptyStart = "Empty";

    private RolesViewModel? _vm;

    public RoleFormWindow()
    {
        InitializeComponent();
        StartFromBox.ItemTemplate = new FuncDataTemplate<RoleColumnViewModel?>((role, _) => new TextBlock { Text = role?.Name ?? EmptyStart }, true);
    }

    public void Attach(RolesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is { } vm)
        {
            vm.PropertyChanged -= OnViewModelChanged;
            if (vm.IsEditorOpen)
            {
                vm.CancelEditorCommand.Execute(null);
            }
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RolesViewModel.IsEditorOpen) && _vm is { IsEditorOpen: false })
        {
            Close();
        }
    }
}
