using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>A validation problem of one dialog field (shown under the input) or of the whole payload.</summary>
/// <param name="Field">One of the <see cref="PayloadValidator"/> field names.</param>
/// <param name="Message">User text.</param>
public sealed record FieldError(string Field, string Message);

/// <summary>
/// Validates the payload before anything is written (HARD RULE device safety). The dialog runs the same rules per
/// field while the user types; the server runs them again per device before the first write.
/// </summary>
public static class PayloadValidator
{
    public const string FieldTimeZone = "timeZone";
    public const string FieldMode = "mode";
    public const string FieldNtpServers = "ntpServers";
    public const string FieldManualDateTime = "manualDateTime";

    /// <summary>Most static NTP servers an AXIS device takes (ntp 1.5 <c>maxSupportedStaticServers</c> = 5, REST maxItems 5).</summary>
    public const int MaxNtpServers = 5;

    /// <summary>Earliest date the Time API accepts (Unix epoch).</summary>
    public const int MinYear = 1970;

    /// <summary>Latest year when the device does not say (<c>maxYearSupported</c> on AXIS OS 12.11 = 2069).</summary>
    public const int DefaultMaxYear = 2037;

    public static IReadOnlyList<FieldError> Validate(DateTimePayload payload, int maxYear = DefaultMaxYear)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new List<FieldError>();
        if (!Enum.IsDefined(payload.Mode))
        {
            errors.Add(new(FieldMode, "Choose a time mode."));
        }

        // Like ADM the time zone is always written; server time mode uses the OADM server's zone instead.
        if (payload.Mode != TimeMode.ServerTime && ValidateTimeZone(payload.TimeZone) is { } tz)
        {
            errors.Add(new(FieldTimeZone, tz));
        }

        switch (payload.Mode)
        {
            case TimeMode.Ntp when payload.Ntp is null:
                errors.Add(new(FieldNtpServers, "NTP settings are missing."));
                break;
            case TimeMode.Ntp when payload.Ntp.Source == NtpSource.Static || payload.Ntp.Nts:
                if (ValidateNtpServers(payload.Ntp.Servers, payload.Ntp.Nts) is { } ntp)
                {
                    errors.Add(new(FieldNtpServers, ntp));
                }

                break;
            case TimeMode.Manual:
                if (ValidateManualDateTime(payload.ManualDateTime, maxYear) is { } manual)
                {
                    errors.Add(new(FieldManualDateTime, manual));
                }

                break;
        }

        return errors;
    }

    /// <summary>Throws <see cref="ArgumentException"/> with every problem (task fails before any write).</summary>
    public static void ThrowIfInvalid(DateTimePayload payload, int maxYear = DefaultMaxYear)
    {
        var errors = Validate(payload, maxYear);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors.Select(e => e.Message)) + " Nothing was changed.");
        }
    }

    /// <summary>Null when <paramref name="id"/> is an IANA time zone the devices offer.</summary>
    public static string? ValidateTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Select a time zone.";
        }

        return TimeZoneCatalog.IsKnown(id) ? null : $"\"{id}\" is not a time zone AXIS devices know.";
    }

    /// <summary>Null when the list has 1 to 5 distinct valid host names or IP addresses.</summary>
    public static string? ValidateNtpServers(IReadOnlyList<string>? servers, bool nts = false)
    {
        var kind = nts ? "NTS KE server" : "NTP server";
        if (servers is null || servers.Count == 0 || servers.All(string.IsNullOrWhiteSpace))
        {
            return $"Enter at least one {kind}.";
        }

        if (servers.Count > MaxNtpServers)
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {MaxNtpServers} {kind}s are supported.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in servers)
        {
            var server = raw?.Trim() ?? string.Empty;
            if (server.Length == 0)
            {
                return $"Remove the empty {kind} line.";
            }

            if (!IsHostOrAddress(server))
            {
                return $"\"{server}\" is not a valid host name or IP address.";
            }

            if (!seen.Add(server))
            {
                return $"\"{server}\" is listed twice.";
            }
        }

        return null;
    }

    /// <summary>Null when <paramref name="value"/> is "yyyy-MM-ddTHH:mm:ss" between 1970 and <paramref name="maxYear"/>.</summary>
    public static string? ValidateManualDateTime(string? value, int maxYear = DefaultMaxYear)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Enter the date and time.";
        }

        if (!System.DateTime.TryParseExact(value, DateTimePayload.ManualFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return "Enter the date as yyyy-mm-dd and the time as hh:mm or hh:mm:ss.";
        }

        if (parsed.Year < MinYear || parsed.Year > maxYear)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The year must be between {MinYear} and {maxYear}.");
        }

        return null;
    }

    /// <summary>IPv4, IPv6 (optionally in brackets) or an RFC 1123 host name (letters, digits, hyphen; dot separated; max 253).</summary>
    public static bool IsHostOrAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        if (text.Length is 0 or > 253)
        {
            return false;
        }

        var unbracketed = text.StartsWith('[') && text.EndsWith(']') ? text[1..^1] : text;
        if (IPAddress.TryParse(unbracketed, out var address))
        {
            // IPAddress.TryParse also accepts "1" or "1.2"; require the dotted quad for IPv4.
            return address.AddressFamily == AddressFamily.InterNetworkV6 || unbracketed.Count(c => c == '.') == 3;
        }

        if (text.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return false; // looks like a broken IPv4 address
        }

        var labels = text.TrimEnd('.').Split('.');
        return labels.All(label => label.Length is > 0 and <= 63
            && char.IsAsciiLetterOrDigit(label[0])
            && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
