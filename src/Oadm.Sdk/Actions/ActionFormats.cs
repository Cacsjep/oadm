namespace Oadm.Sdk.Actions;

/// <summary>What a <see cref="ActionFieldType.Text"/> field must hold (SDK 0.11.0), checked while the user types and on the server.</summary>
public enum ActionFieldFormat
{
    /// <summary>Any text.</summary>
    None = 0,

    /// <summary>A fixed http:// or https:// URL: no placeholders, no spaces, no login in it.</summary>
    Url = 1,

    /// <summary>A host name or IP address, without scheme or port.</summary>
    Host = 2,

    /// <summary>One email address, with or without a name ("OADM &lt;oadm@example.com&gt;"). No placeholders.</summary>
    Email = 3,

    /// <summary>Email addresses separated by commas or semicolons. No placeholders.</summary>
    EmailList = 4,

    /// <summary>An opc.tcp:// address (SDK 0.12.0): no placeholders, no spaces, no login in it.</summary>
    OpcEndpoint = 5,
}

/// <summary>The checks of <see cref="ActionFieldFormat"/>, shared by the server and the forms of the client.</summary>
public static class ActionFormats
{
    /// <summary>The error of a URL that is not http:// or https://.</summary>
    public const string EnterUrl = "Enter a URL that starts with http:// or https://.";

    /// <summary>The error of an address that is not opc.tcp://.</summary>
    public const string EnterOpcEndpoint = "Enter an address that starts with opc.tcp://.";

    /// <summary>The error of a URL with a placeholder.</summary>
    public const string EnterFixedUrl = "Enter a fixed URL, without placeholders.";

    /// <summary>The error of a URL with a space.</summary>
    public const string EnterUrlWithoutSpaces = "Enter a URL without spaces.";

    /// <summary>The error of a URL with a user name or password in it.</summary>
    public const string EnterUrlWithoutLogin = "Enter a URL without a user name or password.";

    /// <summary>The error of an empty or wrong host.</summary>
    public const string EnterHost = "Enter a host name or IP address.";

    /// <summary>The error of an empty or wrong email address.</summary>
    public const string EnterEmail = "Enter an email address.";

    /// <summary>The error of a wrong list of email addresses.</summary>
    public const string EnterEmailList = "Enter email addresses separated by commas.";

    /// <summary>The error of a value in <paramref name="format"/>; null when it fits.</summary>
    public static string? Problem(ActionFieldFormat format, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return format switch
        {
            ActionFieldFormat.Url => UrlProblem(value, EnterUrl, Uri.UriSchemeHttp, Uri.UriSchemeHttps),
            ActionFieldFormat.OpcEndpoint => UrlProblem(value, EnterOpcEndpoint, "opc.tcp"),
            ActionFieldFormat.Host => IsHost(value) ? null : EnterHost,
            ActionFieldFormat.Email => IsMailbox(value) ? null : EnterEmail,
            ActionFieldFormat.EmailList => IsMailboxList(value) ? null : EnterEmailList,
            _ => null,
        };
    }

    /// <summary>A host name or IP address. <see cref="Uri.CheckHostName"/> takes 999.1.1.1 for a name, so a last part of digits is refused.</summary>
    public static bool IsHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var kind = Uri.CheckHostName(host);
        return kind != UriHostNameType.Unknown
            && (kind != UriHostNameType.Dns || !host.TrimEnd('.').Split('.')[^1].All(char.IsAsciiDigit));
    }

    private static string? UrlProblem(string url, string wrong, params string[] schemes)
    {
        if (url.AsSpan().IndexOfAny('{', '}') >= 0)
        {
            return EnterFixedUrl;
        }

        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return EnterUrlWithoutSpaces; // Uri would escape them and hide the typing error
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !schemes.Contains(uri.Scheme) || !IsHost(uri.Host))
        {
            return wrong;
        }

        return uri.UserInfo.Length > 0 ? EnterUrlWithoutLogin : null; // the target shows in the list and in the audit log
    }

    private static bool IsMailboxList(string text)
    {
        var parts = text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && parts.All(IsMailbox);
    }

    private static bool IsMailbox(string text)
    {
        if (text.AsSpan().IndexOfAny('{', '}') >= 0)
        {
            return false;
        }

        var address = text;
        if (text.EndsWith('>'))
        {
            var open = text.LastIndexOf('<');
            if (open < 0)
            {
                return false;
            }

            address = text[(open + 1)..^1];
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == address.LastIndexOf('@')
            && !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>')
            && IsHost(address[(at + 1)..]);
    }
}
