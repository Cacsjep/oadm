using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// The device's installed applications for <see cref="InstalledAppRules"/>: <c>POST /axis-cgi/applications/list.cgi</c>
/// (read-only, the call of the Applications plugin; recorded on AXIS P3265-V, AXIS OS 12.11.77).
/// </summary>
public sealed class InstalledAppsReader(IVapixClient vapix)
{
    public const string ListPath = "axis-cgi/applications/list.cgi";

    private readonly IVapixClient _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));

    /// <summary>The applications, or null when the device cannot list them (no application API, refused, unreadable).</summary>
    public async Task<IReadOnlyList<InstalledApp>?> TryListAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ListPath);
            using var response = await _vapix.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or InvalidOperationException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Parses the list.cgi reply; null when it is not <c>result="ok"</c>.</summary>
    public static IReadOnlyList<InstalledApp>? Parse(string xml)
    {
        var root = XElement.Parse(xml);
        if (!string.Equals((string?)root.Attribute("result"), "ok", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return root.Elements("application")
            .Select(a => new InstalledApp
            {
                Name = (string?)a.Attribute("Name") ?? string.Empty,
                NiceName = (string?)a.Attribute("NiceName"),
                Bundled = string.Equals((string?)a.Attribute("Bundled"), "Yes", StringComparison.OrdinalIgnoreCase),
                SignatureStatus = (string?)a.Attribute("SignatureStatus"),
                CompatibleOsVersions = a.Element("CompatibleOsVersions")?.Elements("VersionRange")
                    .Select(r => new AppOsRange((string?)r.Element("Min"), (string?)r.Element("Max")))
                    .ToList() ?? [],
            })
            .Where(a => a.Name.Length > 0)
            .ToList();
    }
}
