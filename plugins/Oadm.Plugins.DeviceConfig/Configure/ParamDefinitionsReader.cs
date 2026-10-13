using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// The writable parameters of a device with their definitions and values: <c>param.cgi?action=listdefinitions&amp;
/// listformat=xmlschema</c> (verified on an AXIS P3265-V, AXIS OS 12.11: name, value, niceName and a type with range,
/// choices or the bool texts). One request when the answer stays below the 1 MB XML limit (<see cref="DeviceXml"/>), else
/// one top-level group at a time (a group that is still too large by its subgroups); a task reads only the groups it writes. Internal, hidden, read-only,
/// constant and write-only parameters are left out, and so are those nobody may write: the last digit of
/// <c>securityLevel</c> is the write level, 7 = no user (Brand, firmware version, serial number: "7707"; settings have 4 for
/// operators or 6 for administrators).
/// </summary>
public static class ParamDefinitionsReader
{
    public const string ParamPath = "/axis-cgi/param.cgi";

    private static readonly XNamespace Ns = "http://www.axis.com/ParameterDefinitionsSchema";

    /// <summary>Type attributes that make a parameter not configurable.</summary>
    private static readonly string[] Excluded = ["internal", "hidden", "readonly", "const", "writeonly"];

    /// <summary>
    /// Every configurable parameter, or with <paramref name="only"/> those of the top-level groups the given names are in
    /// (a task reads just what it writes).
    /// </summary>
    public static async Task<IReadOnlyList<ParamDefinition>> ReadAsync(IVapixClient vapix, CancellationToken ct, IReadOnlyCollection<string>? only = null)
    {
        ArgumentNullException.ThrowIfNull(vapix);

        // One request for everything when it fits the XML limit (AXIS P3265-V: 395 KB in 2.4 s; 29 group requests took about
        // 10 s, each pays the login again). Larger answers (multi-sensor cameras) are read group by group.
        if (only is null)
        {
            var all = await GetAsync(vapix, null, ct).ConfigureAwait(false);
            if (all.Length <= DeviceXml.MaxCharacters)
            {
                return [.. Parse(all).OrderBy(p => p.Name, StringComparer.Ordinal)];
            }
        }

        // The names first (66 KB of plain text for 1,518 parameters): they give the groups to read.
        var names = await vapix.ListParametersAsync(["root"], ct).ConfigureAwait(false);
        var wanted = only?.Select(n => n.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var tops = names.Keys
            .Select(n => n.Split('.')[0])
            .Where(g => wanted is null || wanted.Contains(g))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var result = new List<ParamDefinition>();
        var gate = new object();
        await Parallel.ForEachAsync(tops, new ParallelOptions { MaxDegreeOfParallelism = ConfigureIds.GroupParallelism, CancellationToken = ct }, async (group, token) =>
        {
            var found = await ReadGroupAsync(vapix, group, names.Keys, token).ConfigureAwait(false);
            lock (gate)
            {
                result.AddRange(found);
            }
        }).ConfigureAwait(false);

        return [.. result.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>One group; when its answer is over the XML limit, its subgroups one by one.</summary>
    private static async Task<IReadOnlyList<ParamDefinition>> ReadGroupAsync(IVapixClient vapix, string group, IEnumerable<string> names, CancellationToken ct)
    {
        var xml = await GetAsync(vapix, group, ct).ConfigureAwait(false);
        if (xml.Length <= DeviceXml.MaxCharacters)
        {
            return Parse(xml);
        }

        var prefix = group + ".";
        var subgroups = names
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.IndexOf('.', prefix.Length) > 0)
            .Select(n => n[..n.IndexOf('.', prefix.Length)])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (subgroups.Count == 0)
        {
            throw new ConfigDeviceException($"The parameters of {group} are too large to read.");
        }

        var result = new List<ParamDefinition>();
        foreach (var subgroup in subgroups)
        {
            result.AddRange(await ReadGroupAsync(vapix, subgroup, names, ct).ConfigureAwait(false));
        }

        // Parameters directly in the group (not in a subgroup) are read with the group's own answer only; a group that large
        // has none in practice. They are skipped rather than read without limits.
        return result;
    }

    private static async Task<string> GetAsync(IVapixClient vapix, string? group, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ParamPath}?action=listdefinitions&listformat=xmlschema" + (group is null ? string.Empty : "&group=" + Uri.EscapeDataString(group)));
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ConfigDeviceException(Oadm.Sdk.Devices.DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? $"HTTP {(int)response.StatusCode}");
        }

        return body;
    }

    /// <summary>The configurable parameters of a <c>listdefinitions</c> answer, with full names ("Image.I0.Appearance.Compression").</summary>
    public static IReadOnlyList<ParamDefinition> Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = DeviceXml.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new ConfigDeviceException("The device sent unreadable parameter definitions.", ex);
        }

        var root = doc.Root?.Elements(Ns + "group").FirstOrDefault(g => (string?)g.Attribute("name") == "root");
        if (root is null)
        {
            // "# Error: Error -1 getting param in group 'X'" or an empty group: nothing configurable.
            return [];
        }

        var result = new List<ParamDefinition>();
        Walk(root, string.Empty, result);
        return result;
    }

    private static void Walk(XElement group, string path, List<ParamDefinition> result)
    {
        foreach (var child in group.Elements())
        {
            var name = (string?)child.Attribute("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var full = path.Length == 0 ? name : path + "." + name;
            if (child.Name == Ns + "group")
            {
                Walk(child, full, result);
            }
            else if (child.Name == Ns + "parameter" && Definition(child, full) is { } definition)
            {
                result.Add(definition);
            }
        }
    }

    private static ParamDefinition? Definition(XElement parameter, string name)
    {
        var type = parameter.Element(Ns + "type");
        var kindElement = type?.Elements().FirstOrDefault();
        if (type is null || kindElement is null || Excluded.Any(flag => (string?)type.Attribute(flag) == "true")
            || (string?)parameter.Attribute("securityLevel") is { Length: 4 } level && level[3] == '7')
        {
            return null;
        }

        var kind = kindElement.Name.LocalName switch
        {
            "int" => ParamKind.Number,
            "bool" => ParamKind.Bool,
            "enum" => ParamKind.Enum,
            "hostname" => ParamKind.Hostname,
            "ip" => ParamKind.Ip,
            "ipList" => ParamKind.IpList,
            "password" => ParamKind.Password,
            _ => ParamKind.Text,
        };

        return new ParamDefinition(name, kind)
        {
            NiceName = (string?)parameter.Attribute("niceName"),
            Value = kind == ParamKind.Password ? string.Empty : (string?)parameter.Attribute("value") ?? string.Empty,
            Min = Number(kindElement.Attribute("min")),
            Max = Number(kindElement.Attribute("max")),
            MaxLength = (int?)Number(kindElement.Attribute("maxlen")),
            TrueValue = kind == ParamKind.Bool ? (string?)kindElement.Attribute("true") ?? "yes" : null,
            FalseValue = kind == ParamKind.Bool ? (string?)kindElement.Attribute("false") ?? "no" : null,
            Choices = kind == ParamKind.Enum
                ? [.. kindElement.Elements(Ns + "entry")
                    .Select(e => new ParamChoice((string?)e.Attribute("value") ?? string.Empty, (string?)e.Attribute("niceValue")))]
                : [],
        };
    }

    private static long? Number(XAttribute? attribute) =>
        attribute is not null && long.TryParse(attribute.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
}
