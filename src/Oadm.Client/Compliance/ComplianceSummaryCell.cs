using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Compliance;

/// <summary>
/// The worst compliance result of a device (Compliance page "Result", Devices grid "Compliance"): problems and warnings as
/// <see cref="StatusChip"/> ("2 problems, 1 warning"), all passed as plain text "OK" (colour marks exceptions), empty
/// without results; the failed checks as tooltip.
/// </summary>
public sealed class ComplianceSummaryCell : ContentControl
{
    public static readonly StyledProperty<DeviceCompliance?> ComplianceProperty =
        AvaloniaProperty.Register<ComplianceSummaryCell, DeviceCompliance?>(nameof(Compliance));

    private readonly StatusChip _chip = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private DeviceCompliance? _watched;

    public ComplianceSummaryCell()
    {
        var panel = new Panel();
        panel.Children.Add(_text);
        panel.Children.Add(_chip);
        Content = panel;
        Update();
    }

    public DeviceCompliance? Compliance
    {
        get => GetValue(ComplianceProperty);
        set => SetValue(ComplianceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ComplianceProperty)
        {
            Watch(TopLevel.GetTopLevel(this) is null ? null : Compliance);
            Update();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Compliance);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    private void Watch(DeviceCompliance? compliance)
    {
        if (_watched == compliance)
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnChanged;
        }

        _watched = compliance;
        if (compliance is not null)
        {
            compliance.PropertyChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var level = Compliance?.Level ?? ComplianceLevel.None;
        _text.IsVisible = level == ComplianceLevel.Passed;
        _text.Text = Compliance?.Text;
        _chip.IsVisible = level is ComplianceLevel.Problem or ComplianceLevel.Warning;
        _chip.Text = Compliance?.Text;
        _chip.IsError = level == ComplianceLevel.Problem;
        _chip.IsWarning = level == ComplianceLevel.Warning;
        ToolTip.SetTip(this, Compliance?.ToolTip);
    }
}
