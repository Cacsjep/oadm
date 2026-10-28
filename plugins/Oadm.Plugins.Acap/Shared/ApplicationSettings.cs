using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Acap;

/// <summary>The type of an application setting (param.cgi <c>listdefinitions</c>).</summary>
public enum AppSettingKind
{
    /// <summary>Free text (<c>string</c> and every other type the device names).</summary>
    Text = 0,

    /// <summary>A whole number (<c>int</c>), with a range when the definition gives one.</summary>
    Number = 1,

    /// <summary>One of a list of values (<c>enum</c>).</summary>
    List = 2,

    /// <summary>Yes or no (<c>bool</c>, with the device's texts for both).</summary>
    YesNo = 3,

    /// <summary>A password: never read back or shown.</summary>
    Password = 4,
}

/// <summary>One value of a list setting: the device value and its display text.</summary>
public sealed record AppSettingChoice(string Value, string? NiceValue = null)
{
    public string Display => string.IsNullOrEmpty(NiceValue) ? Value : NiceValue;
}

/// <summary>An allowed range of a number setting (<c>min</c>/<c>max</c> or a <c>rangeEntry</c> "1024-65534").</summary>
public sealed record AppSettingRange(long Min, long Max)
{
    public override string ToString() => Min == Max
        ? Min.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Min} to {Max}");
}

/// <summary>
/// A parameter an application registered (<c>root.&lt;app&gt;.&lt;Name&gt;</c>): its name below the application's group
/// ("ONVIFEvents", "Sub.Name" for a subgroup), the device's nice name, the type and the current value (null for passwords,
/// never read back).
/// </summary>
public sealed record AppSettingDefinition(string Name, AppSettingKind Kind)
{
    public string? NiceName { get; init; }

    /// <summary>The device's current value; null for a password.</summary>
    public string? Value { get; init; }

    /// <summary>The allowed ranges of a number; empty = any whole number.</summary>
    public IReadOnlyList<AppSettingRange> Ranges { get; init; } = [];

    /// <summary>The longest value the device takes (<c>maxlen</c>); null = not given.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The values of a list setting.</summary>
    public IReadOnlyList<AppSettingChoice> Choices { get; init; } = [];

    /// <summary>The device's texts for yes and no ("yes" / "no", "true" / "false").</summary>
    public string? TrueValue { get; init; }

    public string? FalseValue { get; init; }

    /// <summary>The definition marks it <c>hidden</c>: the device's own page does not show it, neither does OADM.</summary>
    public bool Hidden { get; init; }

    /// <summary>Read only, constant or internal: it cannot be changed.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Shown and changeable (not hidden, not read only).</summary>
    public bool IsChangeable => !Hidden && !ReadOnly;

    public string DisplayName => string.IsNullOrWhiteSpace(NiceName) ? Name : NiceName;
}

/// <summary>
/// The settings an application registers (<c>manifest.json</c> <c>paramConfig</c>): read with
/// <c>param.cgi?action=listdefinitions&amp;listformat=xmlschema&amp;group=root.&lt;Name&gt;</c> (seen on AXIS P3265-V, AXIS OS
/// 12.11), checked and written with one <c>param.cgi?action=update</c> (values in the POST body, never in the URL). Values
/// are treated as secret: no text of this class contains one.
/// </summary>
public static class AppSettings
{
    public const string ParamPath = "/axis-cgi/param.cgi";

    /// <summary>At most this many settings change in one task.</summary>
    public const int MaxValues = 200;

    /// <summary>The longest value OADM sends when the definition gives no <c>maxlen</c>.</summary>
    public const int MaxValueLength = 4096;

    /// <summary>The page's text for an application without settings of its own.</summary>
    public const string NoSettings = "This application has no settings here. Open its own page on the device.";

    private static readonly XNamespace Ns = "http://www.axis.com/ParameterDefinitionsSchema";

    private static readonly Regex NamePattern = new("^[A-Za-z0-9_][A-Za-z0-9_]{0,127}(\\.[A-Za-z0-9_]{1,128}){0,3}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>A setting name below the application's group: letters, digits, underscores, dots between parts.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>The application's parameter group ("root.AXISImageHealthAnalytics").</summary>
    public static string GroupOf(string application)
    {
        if (!EapReader.IsValidAppName(application))
        {
            throw new ArgumentException("Invalid application name.", nameof(application));
        }

        return "root." + application;
    }

    /// <summary><c>/axis-cgi/param.cgi?action=listdefinitions&amp;listformat=xmlschema&amp;group=root.&lt;Name&gt;</c>.</summary>
    public static string BuildDefinitionsUri(string application) =>
        $"{ParamPath}?action=listdefinitions&listformat=xmlschema&group={Uri.EscapeDataString(GroupOf(application))}";

    /// <summary>
    /// The settings of <paramref name="application"/> in a <c>listdefinitions</c> answer, hidden ones included (marked), in the
    /// device's order. An answer without the group ("# Error: ..." or an empty group) has none.
    /// </summary>
    public static IReadOnlyList<AppSettingDefinition> Parse(string xml, string application)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var text = xml.Trim();
        if (!text.StartsWith('<'))
        {
            // "# Error: Error -1 getting param in group 'root.X'": the application registered nothing.
            return [];
        }

        XDocument doc;
        try
        {
            doc = DeviceXml.Parse(text);
        }
        catch (XmlException ex)
        {
            throw new AcapSettingsException("The device sent unreadable settings.", ex);
        }

        var group = doc.Root?.Elements(Ns + "group").FirstOrDefault(g => (string?)g.Attribute("name") == "root")
            ?.Elements(Ns + "group").FirstOrDefault(g => string.Equals((string?)g.Attribute("name"), application, StringComparison.Ordinal));
        if (group is null)
        {
            return [];
        }

        var result = new List<AppSettingDefinition>();
        Walk(group, string.Empty, result);
        return result;
    }

    private static void Walk(XElement group, string path, List<AppSettingDefinition> result)
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
            else if (child.Name == Ns + "parameter")
            {
                result.Add(Definition(child, full));
            }
        }
    }

    private static AppSettingDefinition Definition(XElement parameter, string name)
    {
        var type = parameter.Element(Ns + "type");
        var kindElement = type?.Elements().FirstOrDefault();
        var kind = kindElement?.Name.LocalName switch
        {
            "int" => AppSettingKind.Number,
            "bool" => AppSettingKind.YesNo,
            "enum" => AppSettingKind.List,
            "password" => AppSettingKind.Password,
            _ => AppSettingKind.Text,
        };

        bool Flag(string flag) => (string?)type?.Attribute(flag) == "true";
        var ranges = new List<AppSettingRange>();
        if (kind == AppSettingKind.Number && kindElement is not null)
        {
            var min = Number((string?)kindElement.Attribute("min"));
            var max = Number((string?)kindElement.Attribute("max"));
            if (min is not null || max is not null)
            {
                ranges.Add(new AppSettingRange(min ?? long.MinValue, max ?? long.MaxValue));
            }

            foreach (var entry in kindElement.Elements(Ns + "rangeEntry"))
            {
                if (ParseRange((string?)entry.Attribute("value")) is { } range)
                {
                    ranges.Add(range);
                }
            }
        }

        return new AppSettingDefinition(name, kind)
        {
            NiceName = (string?)parameter.Attribute("niceName"),
            Value = kind == AppSettingKind.Password ? null : (string?)parameter.Attribute("value") ?? string.Empty,
            Ranges = ranges,
            MaxLength = (int?)Number((string?)kindElement?.Attribute("maxlen")) is { } len and > 0 ? len : null,
            Choices = kind == AppSettingKind.List && kindElement is not null
                ? [.. kindElement.Elements(Ns + "entry").Select(e => new AppSettingChoice((string?)e.Attribute("value") ?? string.Empty, (string?)e.Attribute("niceValue")))]
                : [],
            TrueValue = kind == AppSettingKind.YesNo ? (string?)kindElement!.Attribute("true") ?? "yes" : null,
            FalseValue = kind == AppSettingKind.YesNo ? (string?)kindElement!.Attribute("false") ?? "no" : null,
            Hidden = Flag("hidden"),
            ReadOnly = Flag("readonly") || Flag("const") || Flag("internal"),
        };
    }

    private static AppSettingRange? ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // "0" or "1024-65534" (a leading minus is a negative bound, not the separator).
        var dash = value.IndexOf('-', 1);
        if (dash < 0)
        {
            return Number(value) is { } single ? new AppSettingRange(single, single) : null;
        }

        return Number(value[..dash]) is { } lo && Number(value[(dash + 1)..]) is { } hi ? new AppSettingRange(lo, hi) : null;
    }

    private static long? Number(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The value as the device takes it (a yes/no in the device's own text, a number without leading zeros, a list value as
    /// the device spells it), or why it does not fit. The reason never contains the value.
    /// </summary>
    public static bool TryNormalize(AppSettingDefinition definition, string? value, out string normalized, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(definition);
        normalized = string.Empty;
        problem = null;
        if (value is null)
        {
            problem = "needs a value.";
            return false;
        }

        if (value.Any(c => char.IsControl(c)))
        {
            problem = "contains characters the device does not take.";
            return false;
        }

        var maxLength = definition.MaxLength ?? MaxValueLength;
        switch (definition.Kind)
        {
            case AppSettingKind.Number:
                if (!long.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    problem = "must be a whole number.";
                    return false;
                }

                if (definition.Ranges.Count > 0 && !definition.Ranges.Any(r => number >= r.Min && number <= r.Max))
                {
                    problem = "must be " + RangeText(definition.Ranges) + ".";
                    return false;
                }

                normalized = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case AppSettingKind.YesNo:
                if (string.Equals(value.Trim(), definition.TrueValue, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = definition.TrueValue!;
                    return true;
                }

                if (string.Equals(value.Trim(), definition.FalseValue, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = definition.FalseValue!;
                    return true;
                }

                problem = $"must be {definition.TrueValue} or {definition.FalseValue}.";
                return false;
            case AppSettingKind.List:
                var choice = definition.Choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.Ordinal))
                    ?? definition.Choices.FirstOrDefault(c => string.Equals(c.Value, value.Trim(), StringComparison.OrdinalIgnoreCase));
                if (choice is null)
                {
                    problem = "must be one of its listed values.";
                    return false;
                }

                normalized = choice.Value;
                return true;
            default:
                if (value.Length > maxLength)
                {
                    problem = string.Create(CultureInfo.InvariantCulture, $"must be at most {maxLength:N0} characters.");
                    return false;
                }

                normalized = value;
                return true;
        }
    }

    /// <summary>"0 to 63", "0 or 1024 to 65534".</summary>
    public static string RangeText(IReadOnlyList<AppSettingRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return ranges.Count switch
        {
            0 => "any whole number",
            1 => ranges[0].ToString(),
            _ => string.Join(", ", ranges.Take(ranges.Count - 1)) + " or " + ranges[^1],
        };
    }

    /// <summary>
    /// Why <paramref name="values"/> cannot be written to an application with <paramref name="definitions"/>, one text per
    /// problem with the setting's name (never its value); empty when every value fits. <paramref name="normalized"/> gets the
    /// values as the device takes them.
    /// </summary>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<AppSettingDefinition> definitions, IReadOnlyDictionary<string, string> values, string application, out IReadOnlyDictionary<string, string> normalized)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(values);
        var byName = new Dictionary<string, AppSettingDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            byName.TryAdd(definition.Name, definition);
        }

        var problems = new List<string>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!byName.TryGetValue(name, out var definition))
            {
                problems.Add($"{name} is not a setting of {application}.");
            }
            else if (!definition.IsChangeable)
            {
                problems.Add($"{definition.DisplayName} cannot be changed.");
            }
            else if (!TryNormalize(definition, value, out var fit, out var problem))
            {
                problems.Add($"{definition.DisplayName} {problem}");
            }
            else
            {
                result[name] = fit;
            }
        }

        normalized = result;
        return problems;
    }

    /// <summary>The <c>param.cgi</c> update form body: <c>action=update&amp;root.App.Name=value</c> (escaped, never in the URL).</summary>
    public static string BuildUpdateForm(string application, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var group = GroupOf(application);
        var form = new StringBuilder("action=update");
        foreach (var (name, value) in values)
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException("Invalid setting name.", nameof(values));
            }

            form.Append('&').Append(Uri.EscapeDataString(group + "." + name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return form.ToString();
    }

    /// <summary>
    /// Throws unless the update answer is "OK". The device's error line ("# Error: Error setting 'root.App.X' to '...'!")
    /// quotes the value, so only the setting's name is kept.
    /// </summary>
    public static void EnsureUpdated(string reply, string application)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var text = reply.Trim();
        if (text.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var group = "root." + application + ".";
        var named = Regex.Match(text, "'(root\\.[A-Za-z0-9_.]+)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var name = named.Success && named.Groups[1].Value.StartsWith(group, StringComparison.Ordinal) ? named.Groups[1].Value[group.Length..] : null;
        throw new AcapSettingsException(name is null
            ? "The device refused the new settings."
            : $"The device refused the new value of {name}.")
        {
            Setting = name,
        };
    }
}

/// <summary>A settings request failed. The message never contains a setting value.</summary>
public sealed class AcapSettingsException : Exception
{
    public AcapSettingsException()
    {
    }

    public AcapSettingsException(string message)
        : base(message)
    {
    }

    public AcapSettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The setting the device refused, when it named one.</summary>
    public string? Setting { get; init; }
}
