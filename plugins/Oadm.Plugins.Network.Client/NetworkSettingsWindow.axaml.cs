using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.Network.Client;

/// <summary>View of <see cref="NetworkSettingsViewModel"/>. Code-behind only wires the close request.</summary>
public sealed partial class NetworkSettingsWindow : Window
{
    private NetworkSettingsViewModel? _model;

    public NetworkSettingsWindow()
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

        _model = DataContext as NetworkSettingsViewModel;
        if (_model is not null)
        {
            _model.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool apply) => Close(apply);
}
