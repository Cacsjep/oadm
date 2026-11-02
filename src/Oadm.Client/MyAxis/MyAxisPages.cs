using System.Net;

namespace Oadm.Client.MyAxis;

/// <summary>
/// The pure rules of the MyAxis sign-in window (docs/spec/myaxis.md): where it starts, which finished pages read the
/// cookies, and which cookie goes to the server. No UI, no browser.
/// </summary>
public static class MyAxisPages
{
    /// <summary>The cookie of www.axis.com that holds the MyAxis session; the only one sent to the server.</summary>
    public const string SessionCookie = "axis_connect_session_sid";

    /// <summary>The server takes at most this many characters ("name=value").</summary>
    public const int MaxSessionLength = 4096;

    private const string Host = "www.axis.com";

    /// <summary>Where the login returns to: the plain download index, a light page.</summary>
    public static Uri DoneAddress { get; } = new("https://www.axis.com/ftp/pub_soft/");

    /// <summary>Axis's login page, back to <see cref="DoneAddress"/> once signed in.</summary>
    public static Uri StartAddress { get; } =
        new("https://www.axis.com/my-axis/login?axis_destination=" + Uri.EscapeDataString(DoneAddress.AbsoluteUri));

    /// <summary>The address whose cookies the window reads.</summary>
    public static Uri CookieAddress { get; } = new("https://www.axis.com/");

    /// <summary>
    /// Whether a page that finished loading reads the cookies: HTTPS on www.axis.com, outside every login path
    /// (<c>/my-axis/login</c>, also with a language prefix such as <c>/en-us/my-axis/login</c>, or any <c>/login</c>
    /// segment). Login hosts (eu.login.connect.axis.com) and other sites never do.
    /// </summary>
    public static bool ReadsCookies(Uri? page)
    {
        if (page is null || !page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps
            || !string.Equals(page.IdnHost, Host, StringComparison.OrdinalIgnoreCase) || !page.IsDefaultPort)
        {
            return false;
        }

        var path = page.AbsolutePath;
        if (path.Contains("/my-axis/login", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, "login", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The session for <c>MyAxisService.SignIn</c>: <c>axis_connect_session_sid=&lt;value&gt;</c> of www.axis.com
    /// (domain www.axis.com, .axis.com or axis.com), null when there is none or its value is not one the server takes
    /// (visible ASCII without <c>; , " \</c> or spaces, at most <see cref="MaxSessionLength"/> characters in all).
    /// The value is a secret: never logged, never shown.
    /// </summary>
    public static string? SessionOf(IEnumerable<Cookie>? cookies)
    {
        if (cookies is null)
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            if (cookie is null || !string.Equals(cookie.Name, SessionCookie, StringComparison.Ordinal) || !IsAxisDomain(cookie.Domain)
                || cookie.Expired || !IsValidValue(cookie.Value))
            {
                continue;
            }

            var session = SessionCookie + "=" + cookie.Value;
            if (session.Length <= MaxSessionLength)
            {
                return session;
            }
        }

        return null;
    }

    private static bool IsAxisDomain(string? domain)
    {
        // A cookie read without a domain (some browsers) was read for www.axis.com itself.
        var d = (domain ?? "").Trim().TrimStart('.');
        return d.Length == 0
            || string.Equals(d, Host, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d, "axis.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c <= ' ' || c > '~' || c is ';' or ',' or '"' or '\\')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The cookies of the sign-in window's browser.</summary>
public interface ICookieSource
{
    /// <summary>
    /// The cookies the browser holds for <paramref name="address"/>; null when this browser gives no way to its cookies.
    /// Never throws for a browser problem (that is null).
    /// </summary>
    Task<IReadOnlyList<Cookie>?> ReadAsync(Uri address, CancellationToken ct);
}
