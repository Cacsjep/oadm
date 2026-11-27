using System.Collections.ObjectModel;

using Oadm.Client.Infrastructure;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Devices;

/// <summary>Device grid columns (spec order), visibility for the column chooser, persisted order and widths.</summary>
public sealed class ColumnLayoutViewModel
{
    public const string IconKey = "icon";

    /// <summary>The Compliance column (the worst result); hidden while each check has its own column.</summary>
    public const string ComplianceKey = "compliance";

    /// <summary>The Architecture column ("aarch64"), off until turned on in the column chooser.</summary>
    public const string ArchitectureKey = "architecture";

    /// <summary>The Vulnerabilities column: the Axis CVEs a device may be affected by ("2 · High", docs/spec/vulnerabilities.md).</summary>
    public const string VulnerabilitiesKey = "vulnerabilities";

    /// <summary>The SoC column ("A8"), off until turned on in the column chooser.</summary>
    public const string SocKey = "soc";

    /// <summary>The HTTPS column (Enabled / Disabled), off until turned on in the column chooser.</summary>
    public const string HttpsKey = "https";

    /// <summary>The IEEE 802.1X column (Enabled / Disabled), off until turned on in the column chooser.</summary>
    public const string Dot1xKey = "dot1x";

    /// <summary>The AXIS OS column ("12.11.77"); the key stays "firmware" (saved layouts).</summary>
    public const string FirmwareKey = "firmware";

    /// <summary>The DHCP column (Yes / No), off until turned on in the column chooser.</summary>
    public const string DhcpKey = "dhcp";

    /// <summary>Default columns in the order of the spec table.</summary>
    public static IReadOnlyList<(string Key, string Header)> DefaultColumns { get; } =
    [
        (IconKey, ""),
        ("address", "Address"),
        ("model", "Model"),
        ("status", "Status"),
        (ComplianceKey, "Compliance"),
        ("mac", "MAC address"),
        (FirmwareKey, AxisOsTexts.Name),
        (VulnerabilitiesKey, "Vulnerabilities"),
        (SocKey, "SoC"),
        (ArchitectureKey, "Architecture"),
        ("clock", "Clock"),
        ("tags", "Tags"),
        ("site", "Site"),
        (DhcpKey, "DHCP"),
        (HttpsKey, "HTTPS"),
        ("certExpires", "Certificate expires"),
        ("certTrust", "Certificate"),
        (Dot1xKey, "IEEE 802.1X"),
    ];

    private readonly IClientSettingsStore _settings;
    private readonly GridColumnLayout _layout;

    public ColumnLayoutViewModel(IClientSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _layout = new GridColumnLayout(
            [.. DefaultColumns.Select(c => new GridColumnDefinition(c.Key, c.Header, c.Key != IconKey, DefaultVisible(c.Key)))],
            [.. settings.Current.DeviceColumns.Where(c => !string.IsNullOrEmpty(c.Key)).Select(c => new GridColumnState(c.Key, c.IsVisible, c.Width, c.DisplayIndex))],
            state =>
            {
                _settings.Current.DeviceColumns = [.. state.Select(c => new ColumnLayoutEntry { Key = c.Key, IsVisible = c.IsVisible, Width = c.Width, DisplayIndex = c.DisplayIndex })];
                _settings.Save();
            });
    }

    /// <summary>The shared layout (the binder and the chooser take it).</summary>
    public GridColumnLayout Layout => _layout;

    /// <summary>Group mode of the device grid (a group per tag), persisted with the layout.</summary>
    public bool GroupByTag
    {
        get => _settings.Current.GroupDevicesByTag;
        set
        {
            if (_settings.Current.GroupDevicesByTag != value)
            {
                _settings.Current.GroupDevicesByTag = value;
                _settings.Save();
            }
        }
    }

    /// <summary>
    /// Every column shows by default except Compliance, SoC, Architecture, DHCP, HTTPS and IEEE 802.1X (turned on in the
    /// column chooser). A layout a client saved keeps its own choice.
    /// </summary>
    public static bool DefaultVisible(string key) => key is not (ComplianceKey or SocKey or ArchitectureKey or DhcpKey or HttpsKey or Dot1xKey);

    /// <summary>The device grid shows one column per compliance check, persisted per client.</summary>
    public bool ExpandCompliance
    {
        get => _settings.Current.ExpandCompliance;
        set
        {
            if (_settings.Current.ExpandCompliance != value)
            {
                _settings.Current.ExpandCompliance = value;
                _settings.Save();
            }
        }
    }

    /// <summary>All columns in spec order; <see cref="GridColumnOption.DisplayIndex"/> holds the user order.</summary>
    public ObservableCollection<GridColumnOption> Columns => _layout.Columns;

    /// <summary>Columns the user may hide, for the column chooser.</summary>
    public IEnumerable<GridColumnOption> Choosable => _layout.Choosable;

    public GridColumnOption? Find(string key) => _layout.Find(key);

    /// <summary>Called by the view after the user reordered or resized columns.</summary>
    public void Capture(IEnumerable<(string Key, int DisplayIndex, double Width)> state) => _layout.Capture(state);

    public void ResetToDefault() => _layout.ResetToDefault();

    public void Save() => _layout.Save();
}
