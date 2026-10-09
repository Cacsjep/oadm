using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Oadm.Plugins.DateAndTime.Client;

/// <summary>
/// View of <see cref="DateTimeDialogViewModel"/>. Code-behind only wires the close request and the one-second clock
/// that lets the device and server time tick (<see cref="DateTimeDialogViewModel.Tick"/>).
/// </summary>
public sealed partial class DateTimeWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeDialogViewModel? _model;

    public DateTimeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _clock.Tick += (_, _) => _model?.Tick();
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _clock.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, bool apply) => Close(apply);
}
