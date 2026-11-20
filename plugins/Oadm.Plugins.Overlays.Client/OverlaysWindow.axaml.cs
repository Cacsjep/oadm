using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.Overlays.Client;

/// <summary>The "Text overlays" dialog. Closes with the payload the view model hands over (null = cancelled). No logic here.</summary>
public partial class OverlaysWindow : Window
{
    private OverlaysDialogViewModel? _vm;

    public OverlaysWindow() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
        }

        _vm = DataContext as OverlaysDialogViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += OnCloseRequested;
        }

        base.OnDataContextChanged(e);
    }

    private void OnCloseRequested(object? sender, string? payload) => Close(payload);
}
