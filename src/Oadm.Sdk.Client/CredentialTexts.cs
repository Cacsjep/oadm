using System.Globalization;

namespace Oadm.Sdk.Client;

/// <summary>
/// Texts of a credential list entry shared by the Credentials page and plugin pages (SDK 0.15.0, one phrase per concept):
/// the masked password and when the entry was added.
/// </summary>
public static class CredentialTexts
{
    /// <summary>Shown while a stored password is hidden; always the same length so it says nothing about the password.</summary>
    public const string Masked = "••••••••";

    /// <summary>The start of the error when the eye could not load a stored password; the server's reason follows.</summary>
    public const string ShowFailed = "Showing the password failed: ";

    /// <summary>"Added 18.11.2026 14:02": when an entry was added, in local time and the user's format.</summary>
    /// <param name="added">When the entry was added.</param>
    public static string Added(DateTimeOffset added) => "Added " + added.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
