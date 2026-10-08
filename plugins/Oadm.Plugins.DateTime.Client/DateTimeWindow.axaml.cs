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

    /// <summary>The dialog asks for 2200 px (user decision): on a smaller screen it takes 92 % of it, centered.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var maxWidth = area.Width / screen.Scaling * 0.92;
        if (Width > maxWidth)
        {
            Width = Math.Max(MinWidth, maxWidth);
            var width = (int)(Width * screen.Scaling);
            Position = new Avalonia.PixelPoint(area.X + ((area.Width - width) / 2), Position.Y);
        }
    }

    private void OnCloseRequested(object? sender, bool apply) => Close(apply);
}
