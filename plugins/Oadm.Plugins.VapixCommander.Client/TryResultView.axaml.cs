using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>The answer of Send (view only); <see cref="CloseCommand"/> is the close icon.</summary>
public partial class TryResultView : UserControl
{
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<TryResultView, ICommand?>(nameof(CloseCommand));

    public TryResultView()
    {
        InitializeComponent();
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }
}
