using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.DateAndTime.Client;

/// <summary>View of <see cref="DateTimeDialogViewModel"/>. Code-behind only wires the close request.</summary>
public sealed partial class DateTimeWindow : Window
{
    private DateTimeDialogViewModel? _model;

    public DateTimeWindow()
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

        _model = DataContext as DateTimeDialogViewModel;
        if (_model is not null)
        {
            _model.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool apply) => Close(apply);
}
