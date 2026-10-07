using System.Globalization;

namespace Oadm.Plugins.SnapshotReport;

/// <summary>Display texts of device facts, worded like the device grid. Used by the page and the PDF.</summary>
public static class FactsText
{
    /// <summary>Certificates expiring within this many days count as "expiring" (warning), like the device grid.</summary>
    public const int CertificateWarningDays = 30;

    public static string Status(string? status) => status switch
    {
        "Ok" => "OK",
        "Unreachable" => "Unreachable",
        "CredentialsRequired" => "Credentials required",
        "PasswordNotSet" => "Password not set",
        "CertificateChanged" => "Certificate changed",
        _ => "Unknown",
    };

    public static bool IsOnline(DeviceFacts device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == "Ok";
    }

    public static string Trust(string? trust) => trust switch
    {
        "Trusted" => "Trusted",
        "SelfSigned" => "Self-signed",
        "Untrusted" => "Untrusted",
        "Expired" => "Expired",
        _ => string.Empty,
    };

    /// <summary>"245 days", "1 day", "Today", "Expired today", "Expired 3 days ago"; empty without a certificate.</summary>
    public static string Expiry(DateTime? notAfterUtc, DateTime nowUtc)
    {
        if (notAfterUtc is not { } notAfter)
        {
            return string.Empty;
        }

        var left = notAfter - nowUtc;
        if (left < TimeSpan.Zero)
        {
            var ago = (int)Math.Floor(-left.TotalDays);
            return ago switch
            {
                0 => "Expired today",
                1 => "Expired 1 day ago",
                _ => string.Create(CultureInfo.InvariantCulture, $"Expired {ago} days ago"),
            };
        }

        var days = (int)Math.Floor(left.TotalDays);
        return days switch
        {
            0 => "Today",
            1 => "1 day",
            _ => string.Create(CultureInfo.InvariantCulture, $"{days} days"),
        };
    }

    /// <summary>The certificate is expired or expires within <see cref="CertificateWarningDays"/> days.</summary>
    public static bool IsCertificateDue(DateTime? notAfterUtc, DateTime nowUtc) =>
        notAfterUtc is { } notAfter && (notAfter - nowUtc).TotalDays <= CertificateWarningDays;

    /// <summary>"Self-signed · valid until 2027-03-01 (245 days)", "Expired on 2026-10-04 (3 days ago)", "Not available" without certificate data.</summary>
    public static string Certificate(DeviceFacts device, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.CertNotAfterUtc is not { } notAfter)
        {
            var trust = Trust(device.CertTrust);
            return trust.Length == 0 ? "Not available" : trust;
        }

        var expired = notAfter < nowUtc;
        var date = notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var trustText = Trust(device.CertTrust);
        if (expired)
        {
            // "Expired on 2026-10-04 (3 days ago)"; the trust "Expired" would only repeat it.
            var ago = (int)Math.Floor((nowUtc - notAfter).TotalDays);
            var when = ago switch { 0 => "today", 1 => "1 day ago", _ => string.Create(CultureInfo.InvariantCulture, $"{ago} days ago") };
            return $"Expired on {date} ({when})";
        }

        var validity = $"valid until {date} ({Expiry(notAfter, nowUtc)})";
        return trustText.Length == 0 ? char.ToUpperInvariant(validity[0]) + validity[1..] : $"{trustText} · {validity}";
    }

    /// <summary>MAC address with colons: "B8:A4:4F:63:13:39".</summary>
    public static string Mac(string? serial)
    {
        if (string.IsNullOrEmpty(serial) || serial.Length != 12)
        {
            return serial ?? string.Empty;
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(i => serial.Substring(i * 2, 2)));
    }

    /// <summary>"10.0.0.48" or "camera1.example.com (10.0.0.48)" when a different host name is known.</summary>
    public static string Address(DeviceFacts device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.HostName) || string.Equals(device.HostName, device.Address, StringComparison.OrdinalIgnoreCase)
            ? device.Address
            : $"{device.Address} ({device.HostName})";
    }

    /// <summary>Small facts line of a tile: "AXIS P3265-V · 12.11.77 · B8A44F631339".</summary>
    public static string Line(DeviceFacts device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.Join(" · ", new[] { device.Model, device.Firmware, device.Serial }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>Capture time in local time: "2026-10-07 14:03:12".</summary>
    public static string Time(DateTimeOffset? time) =>
        time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
}
