using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Samples.UpnpName.Client;

public sealed partial class UpnpNameWindow : Window
{
    public UpnpNameWindow() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is UpnpNameViewModel model)
        {
            model.CloseRequested += ok => Close(ok);
        }
    }
}
