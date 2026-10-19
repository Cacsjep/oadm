using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The icon of a device everywhere a device is listed (the device grid, the Web View tree): the icon of its category
/// (<c>Icon.device.*</c>), neutral while the device is OK, amber when it needs attention (Credentials required, Password
/// not set, Certificate changed), red when it does not answer. <see cref="ShowStatusToolTip"/> puts the reason on hover.
/// </summary>
public sealed class DeviceIcon : Decorator
{
    public static readonly StyledProperty<DeviceCategory> CategoryProperty =
        AvaloniaProperty.Register<DeviceIcon, DeviceCategory>(nameof(Category), DeviceCategory.Unknown);

    public static readonly StyledProperty<DeviceStatus> StatusProperty =
        AvaloniaProperty.Register<DeviceIcon, DeviceStatus>(nameof(Status), DeviceStatus.Ok);

    public static readonly StyledProperty<bool> ShowStatusToolTipProperty =
        AvaloniaProperty.Register<DeviceIcon, bool>(nameof(ShowStatusToolTip));

    private readonly OadmIcon _icon = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    public DeviceIcon()
    {
        Child = _icon;
        Width = 18;
        Height = 18;
    }

    /// <summary>Which device icon to draw.</summary>
    public DeviceCategory Category
    {
        get => GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    /// <summary>Colors the icon: neutral when OK, amber or red for a problem.</summary>
    public DeviceStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>True: the tooltip is the status message for a device that needs attention, else the category.</summary>
    public bool ShowStatusToolTip
    {
        get => GetValue(ShowStatusToolTipProperty);
        set => SetValue(ShowStatusToolTipProperty, value);
    }

    /// <summary>The theme geometry key of a category, without "Icon.": "device.camera".</summary>
    public static string IconKey(DeviceCategory category) => category switch
    {
        DeviceCategory.Camera => "device.camera",
        DeviceCategory.Encoder => "device.encoder",
        DeviceCategory.Speaker => "device.speaker",
        DeviceCategory.Audio => "device.audio",
        DeviceCategory.Intercom => "device.intercom",
        DeviceCategory.Radar => "device.radar",
        DeviceCategory.IoModule => "device.io",
        DeviceCategory.DoorController => "device.door",
        _ => "device.generic",
    };

    /// <summary>The category in words: "Camera", "I/O module".</summary>
    public static string CategoryText(DeviceCategory category) => category switch
    {
        DeviceCategory.Camera => "Camera",
        DeviceCategory.Encoder => "Video encoder",
        DeviceCategory.Speaker => "Speaker",
        DeviceCategory.Audio => "Audio device",
        DeviceCategory.Intercom => "Intercom",
        DeviceCategory.Radar => "Radar",
        DeviceCategory.IoModule => "I/O module",
        DeviceCategory.DoorController => "Door controller",
        DeviceCategory.Other => "Other device",
        _ => "Unknown device type",
    };

    /// <summary>The hover text: the reason for a device that needs attention, else null.</summary>
    public static string? StatusMessage(DeviceStatus status) => status switch
    {
        DeviceStatus.Unreachable => DeviceMessages.StatusText(status),
        DeviceStatus.CredentialsRequired => DeviceMessages.CredentialsRequired,
        DeviceStatus.PasswordNotSet => DeviceMessages.PasswordNotSet,
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.SiteOffline => DeviceMessages.StatusText(status),
        _ => null,
    };

    /// <summary>Amber for a device that needs attention (also a device whose site is offline).</summary>
    public static bool IsWarning(DeviceStatus status) =>
        status is DeviceStatus.CredentialsRequired or DeviceStatus.PasswordNotSet or DeviceStatus.CertificateChanged or DeviceStatus.SiteOffline;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CategoryProperty || change.Property == StatusProperty || change.Property == ShowStatusToolTipProperty
            || change.Property == WidthProperty || change.Property == HeightProperty)
        {
            Apply();
        }
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Apply();
    }

    private void Apply()
    {
        _icon.Width = double.IsNaN(Width) ? 18 : Width;
        _icon.Height = double.IsNaN(Height) ? 18 : Height;
        if (this.TryFindResource("Icon." + IconKey(Category), ActualThemeVariant, out var found) && found is Geometry geometry)
        {
            _icon.Data = geometry;
        }

        _icon.Classes.Set("warn", IsWarning(Status));
        _icon.Classes.Set("error", Status == DeviceStatus.Unreachable);
        if (ShowStatusToolTip)
        {
            ToolTip.SetTip(this, StatusMessage(Status) ?? CategoryText(Category));
        }
    }
}
