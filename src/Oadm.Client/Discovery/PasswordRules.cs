namespace Oadm.Client.Discovery;

/// <summary>
/// Rules for the first root password of a factory-default device: 1-64 printable ASCII characters
/// (pwdgrp.cgi) plus the device passphrase policy from systemready ("length": at least 15
/// characters, "complex": at least 12 with upper- and lower-case letters, a digit and a special
/// character). The device checks again when the password is set.
/// </summary>
public static class PasswordRules
{
    public const int MaxLength = 64;
    public const int LengthPolicyMinimum = 15;
    public const int ComplexPolicyMinimum = 12;

    /// <summary>The hint shown next to the password fields.</summary>
    public static string Hint(string? policy) => Normalize(policy) switch
    {
        "length" => $"1-64 printable ASCII characters; device policy \"length\": at least {LengthPolicyMinimum} characters.",
        "complex" => $"1-64 printable ASCII characters; device policy \"complex\": at least {ComplexPolicyMinimum} characters with upper- and lower-case letters, a digit and a special character.",
        _ => "1-64 printable ASCII characters (letters, digits, space and !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~).",
    };

    /// <summary>Null when valid; "" when incomplete (nothing to report yet); else the reason (never the password).</summary>
    public static string? Validate(string password, string confirm, string? policy)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(confirm);
        if (password.Length == 0)
        {
            return "";
        }

        if (password.Length > MaxLength)
        {
            return $"The password can have at most {MaxLength} characters.";
        }

        if (password.Any(c => c < (char)0x20 || c > (char)0x7E))
        {
            return "The password may only contain printable ASCII characters.";
        }

        switch (Normalize(policy))
        {
            case "length" when password.Length < LengthPolicyMinimum:
                return $"The device passphrase policy requires at least {LengthPolicyMinimum} characters.";
            case "complex" when password.Length < ComplexPolicyMinimum:
                return $"The device passphrase policy requires at least {ComplexPolicyMinimum} characters.";
            case "complex" when !(password.Any(char.IsAsciiLetterUpper) && password.Any(char.IsAsciiLetterLower)
                && password.Any(char.IsAsciiDigit) && password.Any(c => !char.IsAsciiLetterOrDigit(c))):
                return "The device passphrase policy requires an upper-case letter, a lower-case letter, a digit and a special character.";
        }

        if (confirm.Length == 0)
        {
            return "";
        }

        return password == confirm ? null : "The passwords do not match.";
    }

    private static string Normalize(string? policy) => (policy ?? "").Trim().ToLowerInvariant();
}
