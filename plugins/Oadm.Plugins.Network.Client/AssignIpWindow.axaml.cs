using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.Network.Client;

/// <summary>View of <see cref="AssignIpViewModel"/>. Code-behind only wires the close request.</summary>
public sealed partial class AssignIpWindow : Window
{
    private AssignIpViewModel? _model;

    public AssignIpWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.CloseRequested -= OnCloseRequested;
        }

        _model = DataContext as AssignIpViewModel;
        if (_model is not null)
        {
            _model.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool finish) => Close(finish);
}
