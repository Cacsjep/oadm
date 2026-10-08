namespace Oadm.Plugins.Users;

/// <summary>
/// User name and password rules checked before anything is sent to a device. They follow VAPIX
/// pwdgrp.cgi (user: 1-14 characters a-z, A-Z, 0-9; password: 1-64 ASCII characters 0x20-0x7E,
/// the <c>strict_pwd</c> standard) plus the device passphrase policy from systemready.
/// Messages never contain the password.
/// </summary>
public static class CredentialRules
{
    public const int MaxUserNameLength = 14;
    public const int MaxPasswordLength = 64;
    public const int LengthPolicyMinimum = 15;
    public const int ComplexPolicyMinimum = 12;

    /// <returns>null when valid, otherwise a user-facing reason.</returns>
    public static string? ValidateUserName(string? userName)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return "Enter a user name.";
        }

        if (userName.Length > MaxUserNameLength)
        {
            return $"The user name can have at most {MaxUserNameLength} characters.";
        }

        foreach (var c in userName)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return "The user name may only contain the letters a-z, A-Z and the digits 0-9.";
            }
        }

        return null;
    }

    /// <returns>null when valid, otherwise a user-facing reason (never containing the password).</returns>
    public static string? ValidatePassword(string? password, PassphrasePolicy policy)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Enter a password.";
        }

        if (password.Length > MaxPasswordLength)
        {
            return $"The password can have at most {MaxPasswordLength} characters.";
        }

        foreach (var c in password)
        {
            if (c < (char)0x20 || c > (char)0x7E)
            {
                return "Use only letters, digits, spaces and standard symbols.";
            }
        }

        switch (policy)
        {
            case PassphrasePolicy.Length when password.Length < LengthPolicyMinimum:
                return $"This device needs at least {LengthPolicyMinimum} characters.";
            case PassphrasePolicy.Complex when password.Length < ComplexPolicyMinimum:
                return $"This device needs at least {ComplexPolicyMinimum} characters.";
            case PassphrasePolicy.Complex when !(password.Any(char.IsAsciiLetterUpper) && password.Any(char.IsAsciiLetterLower)
                && password.Any(char.IsAsciiDigit) && password.Any(c => !char.IsAsciiLetterOrDigit(c))):
                return "This device needs upper and lower case, a digit and a symbol.";
            default:
                return null;
        }
    }

    /// <summary>Text shown next to the password fields.</summary>
    public static string Hint(PassphrasePolicy policy) => policy switch
    {
        PassphrasePolicy.Length => $"1-64 characters, no accented letters. At least {LengthPolicyMinimum} characters (device rule).",
        PassphrasePolicy.Complex => $"1-64 characters, no accented letters. At least {ComplexPolicyMinimum} characters with upper and lower case, a digit and a symbol (device rule).",
        _ => "1-64 characters, no accented letters.",
    };

    /// <summary>Maps the systemready <c>passphrasepolicy</c> value. Unknown values map to None: the device still enforces its own policy.</summary>
    public static PassphrasePolicy ParsePolicy(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "LENGTH" => PassphrasePolicy.Length,
        "COMPLEX" => PassphrasePolicy.Complex,
        _ => PassphrasePolicy.None,
    };
}
