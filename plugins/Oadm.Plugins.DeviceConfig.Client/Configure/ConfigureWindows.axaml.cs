using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.DeviceConfig.Client.Configure;

/// <summary>The settings editor (tabs, parameter list, rules, profiles). No code-behind logic.</summary>
public partial class SettingsEditorView : UserControl
{
    public SettingsEditorView() => AvaloniaXamlLoader.Load(this);
}

/// <summary>The Configure devices window; closes when the view model asks.</summary>
public partial class ConfigureDevicesWindow : Window
{
    public ConfigureDevicesWindow() => AvaloniaXamlLoader.Load(this);

    public void Attach(ConfigureDevicesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close(viewModel.Result);
        Closed += (_, _) => viewModel.Dispose();
    }
}

/// <summary>The Create configuration file window; closes when the view model asks.</summary>
public partial class CreateConfigFileWindow : Window
{
    public CreateConfigFileWindow() => AvaloniaXamlLoader.Load(this);

    public void Attach(CreateConfigFileViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.Dispose();
    }
}
