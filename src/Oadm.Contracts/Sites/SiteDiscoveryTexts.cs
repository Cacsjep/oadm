namespace Oadm.Contracts.Sites;

/// <summary>
/// The texts of discovery at sites (docs/sites.md, Client: add page and import), shared by the server (waiting list) and the
/// client (add page), so one concept has one phrase.
/// </summary>
public static class SiteDiscoveryTexts
{
    /// <summary>The subtitle of Discovery while the server has no sites.</summary>
    public const string FoundLocally = "Axis devices found on the local network.";

    /// <summary>The subtitle of Discovery while the server has sites: it looks through every online site too.</summary>
    public const string FoundHereAndAtSites = "Axis devices found on this server's network and at the sites.";

    /// <summary>The server's own network where a list of routes names it.</summary>
    public const string ThisServer = "this server";

    /// <summary>
    /// A line without a site that reaches different devices through several routes: "Answers through Building A and
    /// Building B." (three or more: "Building A, Building B and this server").
    /// </summary>
    public static string AnswersThrough(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return "Answers through " + Join(names) + ".";
    }

    /// <summary>The tooltip of a device that answered through other routes too: "Also answers through this server."</summary>
    public static string AlsoAnswersThrough(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return "Also answers through " + Join(names) + ".";
    }

    /// <summary>A site name, or <see cref="ThisServer"/> for the server's own network (empty name).</summary>
    public static string RouteName(string? siteName) => string.IsNullOrWhiteSpace(siteName) ? ThisServer : siteName;

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string Join(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }
}
