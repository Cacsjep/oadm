namespace Oadm.Sdk.Vapix;

/// <summary>
/// The one check that a path stays on the device's own address (SDK 0.7.0): a page of its web interface, an application's
/// own page, a custom path. Used by the server and client parts alike (no Avalonia).
/// </summary>
public static class DevicePaths
{
    /// <summary>
    /// Whether <paramref name="path"/> is a path on the device only: starts with one "/", no "//" anywhere (no scheme such as
    /// "http://", no host), no "\", no ".." segment (also percent-encoded), no spaces or control characters. Query and
    /// fragment may follow ("/local/app/index.html#/a?b=c").
    /// </summary>
    public static bool IsOnDevice(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal) || path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return false;
        }

        var end = path.IndexOfAny(['?', '#']);
        var pathPart = end < 0 ? path : path[..end];
        foreach (var segment in pathPart.Split('/'))
        {
            var plain = segment.Contains('%', StringComparison.Ordinal) ? Unescape(segment) : segment;
            if (plain is null || plain == ".." || plain.Contains('/', StringComparison.Ordinal) || plain.Contains('\\', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="page"/> as a path on the device: trimmed, with a leading "/" added when it has none
    /// ("local/app/index.html" becomes "/local/app/index.html"); null when it is empty or would leave the device
    /// (<see cref="IsOnDevice"/>).
    /// </summary>
    public static string? ToDevicePath(string? page)
    {
        var text = page?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var path = text[0] == '/' ? text : "/" + text;
        return IsOnDevice(path) ? path : null;
    }

    private static string? Unescape(string segment)
    {
        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
