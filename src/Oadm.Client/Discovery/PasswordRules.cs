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

    /// <summary>Error of the password field alone (empty, length, characters, policy); null when valid.</summary>
    public static string? PasswordError(string password, string? policy)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            return "Enter a password.";
        }

        string? error = Validate(password, password, policy);
        return string.IsNullOrEmpty(error) ? null : error;
    }

    /// <summary>Error of the confirmation field; null when it repeats the password.</summary>
    public static string? ConfirmError(string password, string confirm)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(confirm);
        return confirm.Length == 0 ? "Enter the password again."
            : confirm != password ? "The passwords do not match." : null;
    }

    /// <summary>
    /// The hint for several devices with possibly different policies: the strictest rule wins, "length" and "complex"
    /// together need both (at least 15 characters with every character class).
    /// </summary>
    public static string Hint(IEnumerable<string?> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var set = policies.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        if (set.Contains("length") && set.Contains("complex"))
        {
            return $"1-64 printable ASCII characters; the device policies \"length\" and \"complex\": at least {LengthPolicyMinimum} characters with upper- and lower-case letters, a digit and a special character.";
        }

        return Hint(set.Contains("complex") ? "complex" : set.Contains("length") ? "length" : null);
    }

    /// <summary>Error of the password field against every policy (first failing one); null when valid.</summary>
    public static string? PasswordError(string password, IEnumerable<string?> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var distinct = policies.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            distinct.Add("");
        }

        return distinct.Select(p => PasswordError(password, p)).FirstOrDefault(e => e is not null);
    }

    private static string Normalize(string? policy) => (policy ?? "").Trim().ToLowerInvariant();
}
