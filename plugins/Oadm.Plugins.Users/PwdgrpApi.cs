using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.Users;

/// <summary>
/// Requests and response parsing for the VAPIX user management CGIs. All requests are POSTs with
/// the arguments in an <c>application/x-www-form-urlencoded</c> body, so user names and passwords
/// never appear in a URL (and therefore never in device or proxy access logs).
/// </summary>
public static partial class PwdgrpApi
{
    /// <summary>API discovery id of pwdgrp.cgi ("User Management").</summary>
    public const string ApiId = "user-management";

    /// <summary>Lowest supported version; a different major version is a different API and is refused.</summary>
    public const string MinVersion = "1.0";

    public const string SystemReadyApiId = "systemready";
    public const string SystemReadyMinVersion = "1.0";

    public const string PwdgrpPath = "axis-cgi/pwdgrp.cgi";
    public const string UserGroupPath = "axis-cgi/usergroup.cgi";
    public const string SystemReadyPath = "axis-cgi/systemready.cgi";

    /// <summary>Primary group for every account created by OADM, as recommended by VAPIX.</summary>
    public const string PrimaryGroup = "users";

    /// <summary><c>action=get</c>: users per access group (read-only).</summary>
    public static HttpRequestMessage BuildGet() => BuildForm([new("action", "get")]);

    /// <summary><c>action=add</c> with grp=users, the role's sgrp and an empty comment.</summary>
    public static HttpRequestMessage BuildAdd(string userName, string password, UserRole role, bool ptz) => BuildForm(
    [
        new("action", "add"),
        new("user", userName),
        new("pwd", password),
        new("grp", PrimaryGroup),
        new("sgrp", UserRoles.ToSecondaryGroups(role, ptz)),
        new("comment", string.Empty),
    ]);

    /// <summary><c>action=update</c>: new password and/or new access groups. At least one must be given.</summary>
    public static HttpRequestMessage BuildUpdate(string userName, string? password, UserRole? role, bool ptz)
    {
        if (password is null && role is null)
        {
            throw new ArgumentException("Nothing to update.");
        }

        var fields = new List<KeyValuePair<string, string>> { new("action", "update"), new("user", userName) };
        if (password is not null)
        {
            fields.Add(new("pwd", password));
        }

        if (role is { } r)
        {
            fields.Add(new("sgrp", UserRoles.ToSecondaryGroups(r, ptz)));
        }

        return BuildForm(fields);
    }

    /// <summary><c>action=remove</c>.</summary>
    public static HttpRequestMessage BuildRemove(string userName) => BuildForm([new("action", "remove"), new("user", userName)]);

    /// <summary>usergroup.cgi: name and groups of the account the request is authenticated as (read-only).</summary>
    public static HttpRequestMessage BuildCurrentAccount() => new(HttpMethod.Get, new Uri(UserGroupPath, UriKind.Relative));

    /// <summary>systemready.cgi: passphrase policy (read-only, works without credentials).</summary>
    public static HttpRequestMessage BuildSystemReady() => new(HttpMethod.Post, new Uri(SystemReadyPath, UriKind.Relative))
    {
        Content = new StringContent("""{"apiVersion":"1.0","method":"systemready","params":{"timeout":10}}""", System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpRequestMessage BuildForm(IEnumerable<KeyValuePair<string, string>> fields) =>
        new(HttpMethod.Post, new Uri(PwdgrpPath, UriKind.Relative)) { Content = new FormUrlEncodedContent(fields) };

    /// <summary>
    /// Parses the <c>action=get</c> body (lines <c>group="a,b"</c>). Users come from <c>digusers</c> (all
    /// accounts) plus anyone listed in an access group; the role is the highest group.
    /// </summary>
    public static IReadOnlyList<DeviceUser> ParseUsers(string body, string? currentAccount = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in GroupLine().Matches(body))
        {
            groups[m.Groups["group"].Value] = m.Groups["users"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        if (groups.Count == 0)
        {
            throw new UserManagementException("The device returned an unexpected user list. Nothing was changed.");
        }

        List<string> Group(string name) => groups.TryGetValue(name, out var list) ? list : [];
        var names = new List<string>();
        foreach (var name in Group("digusers").Concat(Group("admin")).Concat(Group("operator")).Concat(Group("viewer")).Concat(Group("ptz")))
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        bool In(string group, string name) => Group(group).Contains(name, StringComparer.Ordinal);
        return names
            .Select(n => new DeviceUser(
                n,
                In("admin", n) ? UserRole.Administrator : In("operator", n) ? UserRole.Operator : In("viewer", n) ? UserRole.Viewer : UserRole.None,
                In("ptz", n),
                currentAccount is not null && string.Equals(n, currentAccount, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Checks a write response. pwdgrp.cgi answers 200 with "Created account x.", "Modified account x."
    /// or "Removed account x." on success and 200 with "Error: ..." on failure.
    /// </summary>
    public static void EnsureWriteSucceeded(int statusCode, string body, string expectedVerb)
    {
        var text = HtmlTag().Replace(body ?? string.Empty, " ").Trim();
        var shortText = text.Length > 200 ? text[..200] : text;
        if (statusCode is 401 or 403)
        {
            throw new UserManagementException($"The device refused the request (HTTP {statusCode}): the stored account is not an administrator.");
        }

        if (statusCode is < 200 or > 299)
        {
            throw new UserManagementException($"The device answered HTTP {statusCode}: {shortText}");
        }

        if (text.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserManagementException($"The device refused the change: {shortText}");
        }

        if (!text.Contains(expectedVerb, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserManagementException($"The device answered unexpectedly: {shortText}");
        }
    }

    /// <summary>usergroup.cgi body: first line the account name, second line its groups.</summary>
    public static string? ParseCurrentAccount(string body)
    {
        var first = (body ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) || first.Contains('<', StringComparison.Ordinal) || first.Contains(' ', StringComparison.Ordinal) ? null : first;
    }

    /// <summary>systemready <c>data.passphrasepolicy</c>; None when absent (older AXIS OS has no policy).</summary>
    public static PassphrasePolicy ParsePassphrasePolicy(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("passphrasepolicy", out var policy)
                && policy.ValueKind == JsonValueKind.String
                ? CredentialRules.ParsePolicy(policy.GetString())
                : PassphrasePolicy.None;
        }
        catch (JsonException)
        {
            return PassphrasePolicy.None;
        }
    }

    [GeneratedRegex("""^\s*(?<group>[A-Za-z0-9_]+)\s*=\s*"(?<users>[^"]*)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex GroupLine();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTag();
}

/// <summary>A user management step failed or was refused. Messages are user-facing and never contain passwords.</summary>
public sealed class UserManagementException : Exception
{
    public UserManagementException()
    {
    }

    public UserManagementException(string message)
        : base(message)
    {
    }

    public UserManagementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
