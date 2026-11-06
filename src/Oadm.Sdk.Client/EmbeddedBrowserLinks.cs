namespace Oadm.Sdk.Client;

/// <summary>
/// Where an embedded browser opens an address: a page stays on the origin it was opened for (the Help window's docs
/// server, a Web View tab's relay); anything else is cancelled and handed to the system browser.
/// </summary>
public static class EmbeddedBrowserLinks
{
    /// <summary>
    /// True when the browser should not open <paramref name="address"/> itself: web addresses of another scheme, host or
    /// port than <paramref name="origin"/> (every web address without an origin), and mail links. Other schemes (about:,
    /// data:, blob:) stay with the browser.
    /// </summary>
    public static bool Leaves(Uri? address, Uri? origin)
    {
        if (address is null || !address.IsAbsoluteUri)
        {
            return false;
        }

        if (address.Scheme == Uri.UriSchemeMailto)
        {
            return true;
        }

        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return origin is null
            || !origin.IsAbsoluteUri
            || address.Scheme != origin.Scheme
            || !string.Equals(address.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase);
    }
}
