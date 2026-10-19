using System.Collections;

using Avalonia;
using Avalonia.Controls;

namespace Oadm.Client.EventRules;

/// <summary>The settings form of declared fields (<see cref="ActionFieldViewModel"/>): event rule actions and compliance checks.</summary>
public partial class SettingsFormView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> FieldsProperty =
        AvaloniaProperty.Register<SettingsFormView, IEnumerable?>(nameof(Fields));

    public SettingsFormView()
    {
        InitializeComponent();
    }

    /// <summary>The fields to draw (<see cref="ActionFieldViewModel"/>).</summary>
    public IEnumerable? Fields
    {
        get => GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }
}
