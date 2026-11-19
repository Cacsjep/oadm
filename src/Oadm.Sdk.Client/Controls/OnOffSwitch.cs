using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one On / Off switch (SDK 0.15.0): "On" or "Off" written left of a switch in the accent, as in the Windows 11
/// settings. The host's Settings page and the switch of a whole feature in a dialog (Provisioning management) use it.
/// Bind <see cref="IsChecked"/> two-way; or, when the view model decides (it may refuse), bind it one-way and set
/// <see cref="Command"/>: a click runs the command and the switch shows <see cref="IsChecked"/> again once the view model
/// raises it.
/// </summary>
public sealed class OnOffSwitch : StackPanel
{
    /// <summary>The text while the switch is on.</summary>
    public const string OnText = "On";

    /// <summary>The text while the switch is off.</summary>
    public const string OffText = "Off";

    /// <summary>Whether the switch is on.</summary>
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<OnOffSwitch, bool>(nameof(IsChecked), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Runs on a click; the switch is disabled while it cannot run.</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<OnOffSwitch, ICommand?>(nameof(Command));

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _switch = new() { OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A switch that is off.</summary>
    public OnOffSwitch()
    {
        Orientation = Orientation.Horizontal;
        _text.Classes.Add("onOffText");
        _switch.IsCheckedChanged += (_, _) => SetCurrentValue(IsCheckedProperty, _switch.IsChecked == true);
        Children.Add(_text);
        Children.Add(_switch);
        Update();
    }

    /// <summary>Whether the switch is on.</summary>
    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>Runs on a click, instead of a two-way <see cref="IsChecked"/>.</summary>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>The switch itself (tests click it).</summary>
    public ToggleSwitch Switch => _switch;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty)
        {
            Update();
        }
        else if (change.Property == CommandProperty)
        {
            _switch.Command = Command;
        }
    }

    private void Update()
    {
        _switch.IsChecked = IsChecked;
        _text.Text = IsChecked ? OnText : OffText;
    }
}
