using System.Globalization;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.Pki;

/// <summary>
/// Field rules shared by the server (authoritative) and the page (errors while typing). Every rule returns the message
/// shown under the field, or null.
/// </summary>
public static partial class PkiValidation
{
    public const int MinDeviceValidityDays = 1;
    public const int MaxDeviceValidityDays = 3650;
    public const int MinWarningDays = 1;
    public const int MaxWarningDays = 365;
    public const int MaxCustomIdentityLength = 64;

    /// <summary>Placeholders of the custom EAP identity.</summary>
    public static readonly IReadOnlyList<string> IdentityPlaceholders = ["{serial}", "{hostName}"];

    public static string? CommonName(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "Enter a name.";
        }

        return text.Length > PkiPluginInfo.MaxNameLength ? $"At most {PkiPluginInfo.MaxNameLength} characters." : null;
    }

    public static string? Organization(string? value) =>
        (value?.Trim().Length ?? 0) > PkiPluginInfo.MaxNameLength ? $"At most {PkiPluginInfo.MaxNameLength} characters." : null;

    public static string? ValidityYears(int years) =>
        years is < PkiPluginInfo.MinValidityYears or > PkiPluginInfo.MaxValidityYears
            ? $"Enter a whole number from {PkiPluginInfo.MinValidityYears} to {PkiPluginInfo.MaxValidityYears}."
            : null;

    /// <summary>A whole number from the text of a field; null when it is not one.</summary>
    public static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static string? ValidityYears(string? text) =>
        ParseInt(text) is { } years ? ValidityYears(years) : ValidityYears(0);

    public static string? DeviceCertValidityDays(int days) =>
        days is < MinDeviceValidityDays or > MaxDeviceValidityDays
            ? $"Enter a whole number from {MinDeviceValidityDays} to {MaxDeviceValidityDays}."
            : null;

    public static string? ExpiryWarningDays(int days) =>
        days is < MinWarningDays or > MaxWarningDays
            ? $"Enter a whole number from {MinWarningDays} to {MaxWarningDays}."
            : null;

    public static string? EapolVersion(int version) => version is 1 or 2 ? null : "Choose EAPOL version 1 or 2.";

    public static string? Identity(string? identity) =>
        identity is Dot1xIdentity.Mac or Dot1xIdentity.HostName or Dot1xIdentity.Custom ? null : "Choose an EAP identity.";

    /// <summary>Required for the custom identity: 1..64 printable characters, only the known placeholders.</summary>
    public static string? CustomIdentity(string? identity, string? custom)
    {
        if (identity != Dot1xIdentity.Custom)
        {
            return null;
        }

        var text = custom?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "Enter the identity.";
        }

        if (text.Length > MaxCustomIdentityLength)
        {
            return $"At most {MaxCustomIdentityLength} characters.";
        }

        if (text.Any(c => c is < (char)0x20 or > (char)0x7E))
        {
            return "Use printable characters only (letters, digits, punctuation).";
        }

        foreach (Match match in PlaceholderPattern().Matches(text))
        {
            if (!IdentityPlaceholders.Contains(match.Value, StringComparer.Ordinal))
            {
                return $"Unknown placeholder {match.Value}. Use {{serial}} or {{hostName}}.";
            }
        }

        return null;
    }

    public static string? RadiusCa(string? source, string? pem) => source switch
    {
        RadiusCaSource.Oadm => null,
        RadiusCaSource.Imported when string.IsNullOrWhiteSpace(pem) => "Import the CA certificate of the RADIUS server.",
        RadiusCaSource.Imported => null,
        _ => "Choose the CA of the RADIUS server.",
    };

    /// <summary>All rules of the settings (device certificates and 802.1X).</summary>
    public static Dictionary<string, string> Config(PkiConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dot1x = config.Dot1x ?? new Dot1xConfig();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(errors, PkiFields.DeviceCertValidityDays, DeviceCertValidityDays(config.DeviceCertValidityDays));
        Add(errors, PkiFields.ExpiryWarningDays, ExpiryWarningDays(config.ExpiryWarningDays));
        Add(errors, PkiFields.EapolVersion, EapolVersion(dot1x.EapolVersion));
        Add(errors, PkiFields.Identity, Identity(dot1x.Identity));
        Add(errors, PkiFields.CustomIdentity, CustomIdentity(dot1x.Identity, dot1x.CustomIdentity));
        Add(errors, PkiFields.RadiusCa, RadiusCa(dot1x.RadiusCa, dot1x.RadiusCaPem));
        return errors;
    }

    /// <summary>Backup password: at least 8 characters; the confirmation must match.</summary>
    public static string? BackupPassword(string? password) =>
        (password?.Length ?? 0) < PkiPluginInfo.MinBackupPasswordLength
            ? $"Use at least {PkiPluginInfo.MinBackupPasswordLength} characters."
            : null;

    public static string? ConfirmPassword(string? password, string? confirm) =>
        string.Equals(password ?? string.Empty, confirm ?? string.Empty, StringComparison.Ordinal) ? null : "The passwords do not match.";

    private static void Add(Dictionary<string, string> errors, string field, string? message)
    {
        if (message is not null)
        {
            errors[field] = message;
        }
    }

    [GeneratedRegex(@"\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
