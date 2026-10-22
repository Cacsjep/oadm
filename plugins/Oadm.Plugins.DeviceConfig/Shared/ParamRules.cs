using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// Checks a value against a parameter's definition (type, range, choices, length), the same in the dialog and on the device
/// side of the task. The device checks again when it is written.
/// </summary>
public static class ParamRules
{
    /// <summary>Longest text OADM writes to one parameter (the device's own maxlen applies when it is smaller).</summary>
    public const int MaxTextLength = 4096;

    /// <summary>Why <paramref name="value"/> does not fit <paramref name="definition"/>, or null.</summary>
    public static string? Problem(ParamDefinition definition, string? value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var text = value ?? string.Empty;
        if (definition.Kind == ParamKind.Password && text.Length == 0)
        {
            // The device never sends a password back: an empty one would clear it on every device.
            return "Enter the password.";
        }

        if (definition.Kind == ParamKind.Number)
        {
            if (!long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                return "Enter a whole number.";
            }

            return definition.Min is { } min && number < min || definition.Max is { } top && number > top ? "Enter " + Range(definition) + "." : null;
        }

        if (text.Length > MaxTextLength || (definition.MaxLength is { } max && max > 0 && text.Length > max))
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {Math.Min(definition.MaxLength ?? MaxTextLength, MaxTextLength)} characters.");
        }

        if (text.Any(c => char.IsControl(c) && c != '\t'))
        {
            return "Line breaks and control characters are not allowed.";
        }

        switch (definition.Kind)
        {
            case ParamKind.Bool:
                return text == (definition.TrueValue ?? "yes") || text == (definition.FalseValue ?? "no") ? null : "Choose a value.";

            case ParamKind.Enum:
                return definition.Choices.Count == 0 || definition.Choices.Any(c => c.Value == text) ? null : "Choose a value from the list.";

            case ParamKind.Ip:
                return text.Length == 0 || IsIp(text) ? null : "Enter an IP address.";

            case ParamKind.IpList:
                return text.Length == 0 || text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).All(IsIp)
                    ? null
                    : "Enter IP addresses separated by commas.";

            case ParamKind.Hostname:
                // Some host name parameters hold several, separated by spaces (Time.NTP.Server "10.0.0.17 ts1.univie.ac.at").
                // Only plain spaces separate them: no tabs, and text of spaces only is not a name.
                if (text.Length == 0)
                {
                    return null;
                }

                return text.Trim().Length > 0 && !text.Contains('\t', StringComparison.Ordinal)
                    && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(name => IsIp(name) || IsHostName(name))
                    ? null
                    : "Enter a host name or an IP address.";

            default:
                return null;
        }
    }

    /// <summary>The hint below an input: "0 to 100", "At least 1", "Whole number", "IP address"; null when none.</summary>
    public static string? Hint(ParamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Kind switch
        {
            ParamKind.Number => definition.Min is null && definition.Max is null ? "Whole number" : Capitalize(Range(definition)),
            ParamKind.Ip => "IP address",
            ParamKind.IpList => "IP addresses separated by commas",
            ParamKind.Hostname => "Host name or IP address, several separated by spaces",
            _ => null,
        };
    }

    /// <summary>"0 to 100", "at least 1", "at most 10".</summary>
    public static string Range(ParamDefinition definition) => (definition.Min, definition.Max) switch
    {
        ({ } min, { } max) => string.Create(CultureInfo.InvariantCulture, $"{min} to {max}"),
        ({ } min, null) => string.Create(CultureInfo.InvariantCulture, $"at least {min}"),
        (null, { } max) => string.Create(CultureInfo.InvariantCulture, $"at most {max}"),
        _ => "a whole number",
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static bool IsIp(string text) =>
        IPAddress.TryParse(text.Trim(), out var address)
        && (address.AddressFamily == AddressFamily.InterNetworkV6 || text.Trim().Count(c => c == '.') == 3);

    private static bool IsHostName(string text)
    {
        var name = text.Trim().TrimEnd('.');
        return name.Length is > 0 and <= 253
            && name.Split('.').All(label => label.Length is > 0 and <= 63
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                && label[0] != '-' && label[^1] != '-');
    }
}

/// <summary>
/// The parameters Configure devices warns about (decision Q3): they can make the devices unreachable for OADM, or give every
/// selected device the same value of something that must differ per device. One table for the dialog, the permission and
/// the task (written last).
/// </summary>
public static class DangerousParameters
{
    public const string IdentityReason = "Every selected device gets this value; the devices may become unreachable.";

    public const string ConnectionReason = "OADM may lose the connection to the devices.";

    /// <summary>Names (exact) or prefixes (ending in ".") of the device's network identity.</summary>
    private static readonly string[] Identity =
    [
        "Network.IPAddress", "Network.SubnetMask", "Network.DefaultRouter", "Network.Broadcast", "Network.BootProto",
        "Network.HostName", "Network.DomainName", "Network.DNSServer1", "Network.DNSServer2", "Network.Enabled", "Network.Media",
        "Network.Resolver.", "Network.VolatileHostName.", "Network.IPv6.", "Network.eth", "Network.Bonjour.FriendlyName",
        "Network.UPnP.FriendlyName", "Network.DNSUpdate.DNSName", "Network.Interface.I0.Link.",
    ];

    /// <summary>
    /// Names or prefixes of how OADM connects: HTTPS, the web server's ports and HTTP / HTTPS policy, HTTP/2, the web
    /// interface, login method, IEEE 802.1X, RTSP, the IP address filter.
    /// </summary>
    private static readonly string[] Connection =
    [
        "HTTPS.", "Network.HTTP.", "Network.Interface.I0.dot1x.", "Network.RTSP.Port", "Network.RTSP.Enabled",
        "Network.RTSP.ProtViewer", "Network.RTSPS.Port", "Network.RTSPS.Enabled", "Network.Filter.", "System.BoaPort",
        "System.AlternateBoaPort", "System.BoaGroupPolicy.", "System.HTTP2.", "System.WebInterfaceDisabled",
    ];

    /// <summary>
    /// The device's own addresses: never written by Configure devices (Network settings and Assign IP address check that a
    /// new address is free and follow the device to it).
    /// </summary>
    private static readonly string[] Addresses = ["Network.IPAddress", "Network.IPv6.IPAddress"];

    public const string AddressNote = "IP addresses are set with Network settings or Assign IP address.";

    /// <summary>
    /// Values that must differ per device (addresses, host name, device names): not offered when several devices are
    /// selected, every device would get the same one (decision 2026-10-13).
    /// </summary>
    private static readonly string[] PerDevice =
    [
        "Network.IPAddress", "Network.IPv6.IPAddress", "Network.HostName", "Network.Bonjour.FriendlyName",
        "Network.UPnP.FriendlyName", "Network.DNSUpdate.DNSName",
    ];

    public const string PerDeviceNote = "Addresses and names differ per device: use Network settings or Assign IP address.";

    public static bool IsDangerous(string name) => Reason(name) is not null;

    /// <summary>The device's own address: never offered, never written (<see cref="AddressNote"/>).</summary>
    public static bool IsAddress(string name) => Addresses.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The parameter must have another value on each device (not offered for several devices).</summary>
    public static bool IsPerDevice(string name) => PerDevice.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the parameter is dangerous (the tooltip of its warning icon), or null.</summary>
    public static string? Reason(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Matches(Identity, name))
        {
            return IdentityReason;
        }

        return Matches(Connection, name) ? ConnectionReason : null;
    }

    private static bool Matches(string[] table, string name) =>
        table.Any(entry => entry.EndsWith('.')
            ? name.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, entry, StringComparison.OrdinalIgnoreCase)
              || (entry == "Network.eth" && name.StartsWith(entry, StringComparison.OrdinalIgnoreCase)));
}
