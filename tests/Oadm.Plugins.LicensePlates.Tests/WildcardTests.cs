using Oadm.Plugins.LicensePlates.Matching;
using Oadm.Plugins.LicensePlates.Plates;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>
/// Wildcard plates (docs/spec/license-plate-center.md "Plates", "Matching", decisions of 2026-11-27): * is exactly one
/// letter or digit, never a deviation, the exact plate first, then the wildcard plate with more fixed characters, then a
/// near match; the rules of a wildcard plate.
/// </summary>
public sealed class WildcardTests
{
    private static readonly Guid Garage = Guid.NewGuid();
    private static readonly Guid Staff = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0);
    private static readonly HashSet<Guid> AtGarage = [Garage];

    /// <summary>The plates with the real wildcard index, like the plate store.</summary>
    internal sealed class Source : IPlateSource
    {
        private readonly WildcardIndex _index = new();

        public Source(params Plate[] plates)
        {
            foreach (var plate in plates)
            {
                Plates[plate.Text] = plate;
                if (PlateText.IsWildcard(plate.Text))
                {
                    _index.Add(plate.Text);
                }
            }
        }

        public Dictionary<string, Plate> Plates { get; } = new(StringComparer.Ordinal);

        public Plate? Find(string text) => Plates.GetValueOrDefault(text);

        public IReadOnlyCollection<string> TextsWithAnyTag(IReadOnlySet<Guid> tags) =>
            [.. Plates.Values.Where(p => !PlateText.IsWildcard(p.Text) && p.Tags.Any(tags.Contains)).Select(p => p.Text)];

        public IReadOnlyList<Plate> WildcardsFitting(string read) => [.. _index.Fitting(read).Select(p => Plates[p])];
    }

    private static Plate P(string text, params Guid[] tags) => new() { Text = text, Name = "Car " + text, Tags = tags };

    private static PlateMatcher Matcher(int deviation, params Plate[] plates) => new(new Source(plates)) { Deviation = deviation, RebuildInline = true };

    [Theory]
    [InlineData("AXIS1234", ReadResult.Allowed)]
    [InlineData("AXIS12AB", ReadResult.Allowed)]
    [InlineData("AXIS123", ReadResult.Unknown)] // one too short
    [InlineData("AXIS12345", ReadResult.Unknown)] // one too long
    [InlineData("AXIS1334", ReadResult.Unknown)] // a fixed character differs: no deviation for a wildcard plate
    public void A_star_is_exactly_one_letter_or_digit(string read, ReadResult expected)
    {
        var result = Matcher(2, P("AXIS12**", Garage)).Match(read, AtGarage, Now);

        Assert.Equal(expected, result.Result);
        if (expected == ReadResult.Allowed)
        {
            Assert.Equal(("AXIS12**", true), (result.Plate!.Text, result.Exact));
        }
    }

    [Fact]
    public void The_plate_on_its_own_wins_also_when_it_does_not_allow()
    {
        var ended = P("AXIS1234", Garage) with { AllowedTo = Now.AddDays(-1) };
        var matcher = Matcher(1, ended, P("AXIS12**", Garage));

        var result = matcher.Match("AXIS1234", AtGarage, Now);
        Assert.Equal((ReadResult.NotAllowed, ReadReason.Ended, "AXIS1234"), (result.Result, result.Reason, result.Plate!.Text));
        Assert.Equal(ReadResult.Allowed, matcher.Match("AXIS1299", AtGarage, Now).Result); // the wildcard plate for the others
    }

    [Fact]
    public void A_wildcard_plate_beats_a_near_match()
    {
        // AXIS1235 is one character from AXIS1234 (a near match with deviation 1) and fits AXIS12** exactly.
        var matcher = Matcher(1, P("AXIS1234", Staff), P("AXIS12**", Garage));

        var result = matcher.Match("AXIS1235", new HashSet<Guid> { Garage, Staff }, Now);
        Assert.Equal((ReadResult.Allowed, ReadReason.Exact, "AXIS12**"), (result.Result, result.Reason, result.Plate!.Text));
    }

    [Fact]
    public void Of_two_wildcard_plates_the_one_with_more_fixed_characters_decides()
    {
        var narrow = P("AXIS123*", Garage) with { AllowedTo = Now.AddDays(-1) };
        var matcher = Matcher(1, P("AXIS1***", Garage), narrow);

        var result = matcher.Match("AXIS1234", AtGarage, Now);
        Assert.Equal((ReadResult.NotAllowed, ReadReason.Ended, "AXIS123*"), (result.Result, result.Reason, result.Plate!.Text));
        Assert.Equal("AXIS1***", matcher.Match("AXIS1934", AtGarage, Now).Plate!.Text);
    }

    [Fact]
    public void On_a_tie_the_read_is_allowed_when_any_of_the_tied_plates_allows_it()
    {
        var matcher = Matcher(1, P("AXIS**34", Staff), P("AXIS12**", Garage));

        var garage = matcher.Match("AXIS1234", AtGarage, Now);
        Assert.Equal((ReadResult.Allowed, "AXIS12**"), (garage.Result, garage.Plate!.Text));

        var nowhere = matcher.Match("AXIS1234", new HashSet<Guid> { Guid.NewGuid() }, Now);
        Assert.Equal((ReadResult.NotAllowed, ReadReason.NoTagHere), (nowhere.Result, nowhere.Reason));
    }

    [Fact]
    public void A_wildcard_plate_is_never_a_near_match_candidate()
    {
        var source = new Source(P("AXIS12**", Garage), P("W12345A", Garage));
        Assert.Equal(["W12345A"], source.TextsWithAnyTag(AtGarage));
        Assert.Equal(ReadResult.Unknown, new PlateMatcher(source) { Deviation = 2, RebuildInline = true }.Match("AXIS12", AtGarage, Now).Result);
    }

    [Theory]
    [InlineData("AXIS12**", null)]
    [InlineData("ax-is 12**", null)]
    [InlineData("AB****", LicensePlateTexts.TooFewFixed)]
    [InlineData("A*B*", LicensePlateTexts.TooFewFixed)]
    [InlineData("ABC*", null)]
    [InlineData("AB?12", "Use only letters, digits and *.")]
    [InlineData("ABCDEFGHIJK**", "A plate has 2 to 12 characters.")]
    public void A_wildcard_plate_has_at_least_3_letters_or_digits_besides_the_stars(string typed, string? error) =>
        Assert.Equal(error, PlateText.Problem(PlateText.Normalize(typed)));

    [Theory]
    [InlineData("AXIS12**", "AXIS", true)]
    [InlineData("AXIS12**", "AXIS1234", true)]
    [InlineData("AXIS12**", "AXIS13", false)]
    [InlineData("W12345A", "W123", true)]
    [InlineData("AXIS12**", "AXIS12345", false)]
    public void The_search_finds_a_wildcard_plate_by_its_fixed_part(string plate, string search, bool found) =>
        Assert.Equal(found, PlateText.Starts(plate, search));

    [Fact]
    public void The_index_looks_up_the_shapes_of_a_length_never_every_plate()
    {
        var index = new WildcardIndex();
        for (var i = 0; i < LicensePlateLimits.MaxWildcardPlates; i++)
        {
            index.Add("F" + (char)('A' + (i % 26)) + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "**");
        }

        Assert.Equal(LicensePlateLimits.MaxWildcardPlates, index.Count);
        Assert.Equal(1, index.GroupsOf(8)); // one shape: the first three fixed at 0, 1, 2
        Assert.Equal(["FB0001**"], index.Fitting("FB0001XY"));
        index.Remove("FB0001**");
        Assert.Empty(index.Fitting("FB0001XY"));
        Assert.Equal(LicensePlateLimits.MaxWildcardPlates - 1, index.Count);
    }
}
