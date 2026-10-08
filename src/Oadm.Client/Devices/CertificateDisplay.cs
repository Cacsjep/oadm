using System.Globalization;

using Oadm.Contracts.V1;

namespace Oadm.Client.Devices;

/// <summary>Text and kind of a grid chip. Immutable; rows swap the whole value so bindings refresh together.</summary>
public sealed record ChipInfo(string Text, PillKind Kind)
{
    public static ChipInfo Empty { get; } = new(string.Empty, PillKind.Neutral);

    public bool HasText => Text.Length > 0;
    public bool IsOk => Kind == PillKind.Ok;
    public bool IsWarning => Kind == PillKind.Warning;
    public bool IsError => Kind == PillKind.Error;
}

/// <summary>Formatting of the certificate columns ("Certificate expires", "Certificate").</summary>
public static class CertificateDisplay
{
    /// <summary>Certificates expiring within this many days are shown as a warning.</summary>
    public const int WarningDays = 30;

    /// <summary>
    /// "245 days", "1 day", "Today" (less than a day left), "Expired today", "Expired 1 day ago",
    /// "Expired 3 days ago", "Never" (RFC 5280 "no well-defined expiration", 9999-12-31, e.g. the Axis device ID
    /// certificate); empty when there is no certificate (HTTP only or not checked).
    /// </summary>
    public static string ExpiryText(DateTime? notAfterUtc, DateTime nowUtc)
    {
        if (notAfterUtc is not { } notAfter)
        {
            return string.Empty;
        }

        if (NeverExpires(notAfter))
        {
            return "Never";
        }

        TimeSpan left = notAfter - nowUtc;
        if (left < TimeSpan.Zero)
        {
            int ago = (int)Math.Floor(-left.TotalDays);
            return ago switch
            {
                0 => "Expired today",
                1 => "Expired 1 day ago",
                _ => string.Create(CultureInfo.InvariantCulture, $"Expired {ago} days ago"),
            };
        }

        int days = (int)Math.Floor(left.TotalDays);
        return days switch
        {
            0 => "Today",
            1 => "1 day",
            _ => string.Create(CultureInfo.InvariantCulture, $"{days} days"),
        };
    }

    /// <summary>RFC 5280 4.1.2.5: NotAfter 99991231235959Z means the certificate has no well-defined expiration date.</summary>
    public static bool NeverExpires(DateTime notAfterUtc) => notAfterUtc.Year >= 9999;

    /// <summary>Ok when more than <see cref="WarningDays"/> days are left, warning up to that, error when expired.</summary>
    public static PillKind ExpiryKind(DateTime? notAfterUtc, DateTime nowUtc)
    {
        if (notAfterUtc is not { } notAfter)
        {
            return PillKind.Neutral;
        }

        TimeSpan left = notAfter - nowUtc;
        return left < TimeSpan.Zero ? PillKind.Error
            : left.TotalDays <= WarningDays ? PillKind.Warning
            : PillKind.Ok;
    }

    public static ChipInfo Expiry(DateTime? notAfterUtc, DateTime nowUtc) =>
        notAfterUtc is null ? ChipInfo.Empty : new ChipInfo(ExpiryText(notAfterUtc, nowUtc), ExpiryKind(notAfterUtc, nowUtc));

    public static string TrustText(CertificateTrust trust) => trust switch
    {
        CertificateTrust.Trusted => "Trusted",
        CertificateTrust.SelfSigned => "Self-signed",
        CertificateTrust.Untrusted => "Untrusted",
        CertificateTrust.Expired => "Expired",
        _ => string.Empty,
    };

    public static PillKind TrustKind(CertificateTrust trust) => trust switch
    {
        CertificateTrust.Trusted => PillKind.Ok,
        CertificateTrust.SelfSigned => PillKind.Warning,
        CertificateTrust.Untrusted or CertificateTrust.Expired => PillKind.Error,
        _ => PillKind.Neutral,
    };

    /// <summary>
    /// The trust chip. An expiry date that already passed wins over a stale chain result, so the
    /// column never says Trusted next to "Expired 3 days ago".
    /// </summary>
    public static ChipInfo Trust(CertificateTrust trust, DateTime? notAfterUtc, DateTime nowUtc)
    {
        if (trust != CertificateTrust.Unknown && notAfterUtc is { } notAfter && notAfter < nowUtc)
        {
            trust = CertificateTrust.Expired;
        }

        return trust == CertificateTrust.Unknown ? ChipInfo.Empty : new ChipInfo(TrustText(trust), TrustKind(trust));
    }

    /// <summary>Tooltip of both certificate cells: subject, issuer, valid until. Null without certificate info.</summary>
    public static string? Tooltip(string? subject, string? issuer, DateTime? notAfterUtc)
    {
        if (string.IsNullOrEmpty(subject) && string.IsNullOrEmpty(issuer) && notAfterUtc is null)
        {
            return null;
        }

        var lines = new List<string>(3);
        if (!string.IsNullOrEmpty(subject))
        {
            lines.Add("Subject: " + subject);
        }

        if (!string.IsNullOrEmpty(issuer))
        {
            lines.Add("Issuer: " + issuer);
        }

        if (notAfterUtc is { } notAfter)
        {
            lines.Add("Valid until: " + notAfter.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
