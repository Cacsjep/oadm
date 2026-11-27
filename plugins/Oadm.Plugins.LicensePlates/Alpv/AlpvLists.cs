using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.LicensePlates;

/// <summary>A region of ALPV (<c>lprUseCase</c>): its code and name ("Europe" 5).</summary>
public sealed record AlpvRegion(int Code, string Name);

/// <summary>A country of ALPV (<c>CountryPriority</c>, the <c>country</c> of the events): its code ("AUT", or "US" for the
/// countries ALPV names with two letters), its name and the country groups of the regions it belongs to.</summary>
public sealed record AlpvCountry(string Code, string Name, IReadOnlyList<string> Groups);

/// <summary>A state of ALPV (<c>StatePriority</c>): its code, name and the code of its country ("MX").</summary>
public sealed record AlpvState(string Code, string Name, string Country);

/// <summary>A choice of a vehicle filter: the value the events carry (compared without case) and the name shown.</summary>
public sealed record AlpvChoice(string Value, string Name);

/// <summary>
/// ALPV's own lists (docs/spec/license-plate-center.md "ALPV on the camera"): the regions, the countries of each region, the
/// states of the regions that have them, the vehicle types and colours. ALPV's page has them built in; OADM ships them as
/// data of the server part (<c>Alpv/alpv-lists-3.1.7.json</c>, read from ALPV 3.1.7's page) and hands them to the client part
/// (<see cref="LicensePlatesMethods.AlpvLists"/>).
/// </summary>
public sealed record AlpvLists
{
    /// <summary>"Select" of ALPV's page: no camera location.</summary>
    public const string NoCountry = "0";

    /// <summary>The ALPV version the lists were read from.</summary>
    public string Version { get; init; } = string.Empty;

    public IReadOnlyList<AlpvRegion> Regions { get; init; } = [];

    public IReadOnlyList<AlpvCountry> Countries { get; init; } = [];

    /// <summary>The country groups of each region: a country is offered for a region when one of its groups is here.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> RegionGroups { get; init; } = new Dictionary<int, IReadOnlyList<string>>();

    /// <summary>The regions with a State choice.</summary>
    public IReadOnlyList<int> RegionsWithStates { get; init; } = [];

    public IReadOnlyList<AlpvState> States { get; init; } = [];

    public IReadOnlyList<AlpvChoice> VehicleTypes { get; init; } = [];

    public IReadOnlyList<AlpvChoice> VehicleColors { get; init; } = [];

    /// <summary>The countries offered as Camera location for <paramref name="region"/>, by name.</summary>
    public IReadOnlyList<AlpvCountry> CountriesOf(int region)
    {
        var groups = RegionGroups.TryGetValue(region, out var g) ? g.ToHashSet(StringComparer.Ordinal) : [];
        return [.. Countries.Where(c => c.Groups.Any(groups.Contains)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The states offered for <paramref name="country"/> in <paramref name="region"/>; none for a region without states.</summary>
    public IReadOnlyList<AlpvState> StatesOf(int region, string? country) =>
        !RegionsWithStates.Contains(region) || string.IsNullOrEmpty(country)
            ? []
            : [.. States.Where(s => string.Equals(s.Country, country, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The country of a code; null when ALPV's list does not have it.</summary>
    public AlpvCountry? Country(string? code) => Countries.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the lists shipped with the server part (the embedded <c>alpv-lists-3.1.7.json</c>).</summary>
internal static class AlpvListData
{
    public const string Version = "3.1.7";

    private static readonly Lazy<AlpvLists> Data = new(Load);

    public static AlpvLists Lists => Data.Value;

    /// <summary>ALPV's upper case vehicle names in OADM's wording: "Car", "Minivan"; abbreviations stay ("SUV").</summary>
    public static string NameOf(string alpvName) => alpvName switch
    {
        "SUV" => "SUV",
        "UNDEFINED" => "Unknown",
        _ => alpvName.Length == 0 ? alpvName : char.ToUpperInvariant(alpvName[0]) + alpvName[1..].ToLowerInvariant(),
    };

    /// <summary>Parses the file of ALPV's lists (the shape of ALPV's page data).</summary>
    public static AlpvLists Parse(Stream json)
    {
        var file = JsonSerializer.Deserialize<ListFile>(json, Options) ?? throw new InvalidDataException("The ALPV lists are empty.");
        return new AlpvLists
        {
            Version = Version,
            Regions = [.. file.Regions.Select(r => new AlpvRegion(r.Code, r.Name))],
            Countries = [.. file.Countries.Select(c => new AlpvCountry(c.Code, c.Name, c.Groups ?? []))],
            RegionGroups = file.RegionCountryGroups.ToDictionary(
                g => int.Parse(g.Key, NumberStyles.Integer, CultureInfo.InvariantCulture), g => (IReadOnlyList<string>)g.Value),
            RegionsWithStates = file.RegionsWithStates,
            States = [.. file.States.Select(s => new AlpvState(s.Code, s.Name, s.Country is { Count: > 1 } c ? c[1] : string.Empty))],
            VehicleTypes = [.. file.VehicleTypes.Select(t => new AlpvChoice(t.Name, NameOf(t.Name)))],
            VehicleColors = [.. file.VehicleColors.Select(c => new AlpvChoice(c.Name, c.Name))],
        };
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static AlpvLists Load()
    {
        using var stream = typeof(AlpvListData).Assembly.GetManifestResourceStream("Oadm.Plugins.LicensePlates.alpv-lists-" + Version + ".json")
            ?? throw new InvalidOperationException("The ALPV lists are missing from the plugin.");
        return Parse(stream);
    }

    private sealed record ListFile(
        List<Item<int>> Regions,
        Dictionary<string, List<string>> RegionCountryGroups,
        List<CountryItem> Countries,
        List<int> RegionsWithStates,
        List<StateItem> States,
        List<Item<int>> VehicleColors,
        List<Item<int>> VehicleTypes);

    private sealed record Item<T>(string Name, T Code);

    private sealed record CountryItem(string Name, string Code, List<string>? Groups);

    private sealed record StateItem(string Name, string Code, [property: JsonPropertyName("country")] List<string>? Country);
}
