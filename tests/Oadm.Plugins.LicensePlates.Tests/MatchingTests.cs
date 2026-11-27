using Oadm.Plugins.LicensePlates.Matching;
using Oadm.Plugins.LicensePlates.Plates;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>docs/spec/license-plate-center.md "Matching": exact, deviation 1 and 2, short plates, two equal near matches, tags and dates.</summary>
public sealed class MatchingTests
{
    private static readonly Guid Garage = Guid.NewGuid();
    private static readonly Guid Staff = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0);
    private static readonly HashSet<Guid> AtGarage = [Garage];

    private sealed class Source(params Plate[] plates) : IPlateSource
    {
        public Dictionary<string, Plate> Plates { get; } = plates.ToDictionary(p => p.Text, StringComparer.Ordinal);

        public Plate? Find(string text) => Plates.GetValueOrDefault(text);

        public IReadOnlyCollection<string> TextsWithAnyTag(IReadOnlySet<Guid> tags) => [.. Plates.Values.Where(p => p.Tags.Any(tags.Contains)).Select(p => p.Text)];
    }

    private static Plate P(string text, params Guid[] tags) => new() { Text = text, Name = "Car " + text, Tags = tags };

    private static PlateMatcher Matcher(int deviation, params Plate[] plates) => new(new Source(plates)) { Deviation = deviation, RebuildInline = true };

    [Fact]
    public void An_exact_match_is_allowed_and_decides_at_once()
    {
        var result = Matcher(1, P("W12345A", Garage)).Match("W12345A", AtGarage, Now);

        Assert.Equal(ReadResult.Allowed, result.Result);
        Assert.Equal(ReadReason.Exact, result.Reason);
        Assert.True(result.Exact);
        Assert.Equal("W12345A", result.Plate!.Text);
    }

    [Theory]
    [InlineData("W12345B")] // a wrong character
    [InlineData("W1234A")] // a missing character
    [InlineData("W123455A")] // an extra character
    public void Deviation_1_allows_one_wrong_missing_or_extra_character(string read)
    {
        var result = Matcher(1, P("W12345A", Garage)).Match(read, AtGarage, Now);

        Assert.Equal(ReadResult.Allowed, result.Result);
        Assert.Equal(ReadReason.Near, result.Reason);
        Assert.False(result.Exact);
        Assert.Equal("W12345A", result.Plate!.Text);
    }

    [Fact]
    public void Two_characters_off_need_deviation_2()
    {
        Assert.Equal(ReadResult.Unknown, Matcher(1, P("W12345A", Garage)).Match("W12399A", AtGarage, Now).Result);
        Assert.Equal(ReadResult.Allowed, Matcher(2, P("W12345A", Garage)).Match("W12399A", AtGarage, Now).Result);
        Assert.Equal(ReadResult.Allowed, Matcher(2, P("W12345A", Garage)).Match("X12345", AtGarage, Now).Result);
    }

    [Fact]
    public void Exact_never_near_matches()
    {
        var matcher = Matcher(0, P("W12345A", Garage));

        Assert.Equal(ReadResult.Unknown, matcher.Match("W12345B", AtGarage, Now).Result);
        Assert.Equal(ReadResult.Allowed, matcher.Match("W12345A", AtGarage, Now).Result);
    }

    [Fact]
    public void With_2_plates_shorter_than_6_characters_deviate_at_most_1()
    {
        var matcher = Matcher(2, P("AB123", Garage));

        Assert.Equal(ReadResult.Allowed, matcher.Match("AB124", AtGarage, Now).Result);
        Assert.Equal(ReadResult.Unknown, matcher.Match("AB199", AtGarage, Now).Result);
    }

    [Fact]
    public void Equally_close_to_two_allowed_plates_is_not_allowed()
    {
        var result = Matcher(1, P("W12345A", Garage), P("W12345C", Garage)).Match("W12345B", AtGarage, Now);

        Assert.Equal(ReadResult.NotAllowed, result.Result);
        Assert.Equal(ReadReason.SeveralPlates, result.Reason);
        Assert.Null(result.Plate);
        Assert.Equal("Not allowed, matches more than one plate", LicensePlateTexts.Reason(result.Result, result.Reason, null));
    }

    [Fact]
    public void The_closer_of_two_plates_wins()
    {
        var result = Matcher(2, P("W12345A", Garage), P("W12399A", Garage)).Match("W12398A", AtGarage, Now);

        Assert.Equal(ReadResult.Allowed, result.Result);
        Assert.Equal("W12399A", result.Plate!.Text);
    }

    [Fact]
    public void A_plate_without_a_tag_allowed_here_is_not_allowed_and_its_near_matches_are_unknown()
    {
        var matcher = Matcher(1, P("W12345A", Staff));

        var exact = matcher.Match("W12345A", AtGarage, Now);
        Assert.Equal(ReadResult.NotAllowed, exact.Result);
        Assert.Equal(ReadReason.NoTagHere, exact.Reason);
        Assert.True(exact.Exact);

        // A car of another garage never opens this one because its plate is close to an allowed one.
        Assert.Equal(ReadResult.Unknown, matcher.Match("W12345B", AtGarage, Now).Result);
    }

    [Fact]
    public void A_plate_without_tags_is_allowed_nowhere()
    {
        var result = Matcher(1, P("W12345A")).Match("W12345A", AtGarage, Now);

        Assert.Equal(ReadResult.NotAllowed, result.Result);
        Assert.Equal(ReadReason.NoTagHere, result.Reason);
    }

    [Fact]
    public void A_camera_without_allowed_tags_allows_no_plate()
    {
        var matcher = Matcher(1, P("W12345A", Garage));

        Assert.Equal(ReadResult.NotAllowed, matcher.Match("W12345A", new HashSet<Guid>(), Now).Result);
        Assert.Equal(ReadResult.Unknown, matcher.Match("W12345B", new HashSet<Guid>(), Now).Result);
    }

    [Fact]
    public void Before_allowed_from_and_after_allowed_to_are_not_allowed_with_the_time()
    {
        var from = Now.AddDays(1);
        var to = Now.AddDays(-1);
        var matcher = Matcher(1,
            P("W12345A", Garage) with { AllowedFrom = from },
            P("W99999X", Garage) with { AllowedTo = to },
            P("G55555K", Garage) with { AllowedFrom = Now.AddHours(-1), AllowedTo = Now.AddHours(1) });

        var notYet = matcher.Match("W12345A", AtGarage, Now);
        Assert.Equal((ReadResult.NotAllowed, ReadReason.NotYet, (DateTime?)from), (notYet.Result, notYet.Reason, notYet.ReasonTime));
        var ended = matcher.Match("W99999X", AtGarage, Now);
        Assert.Equal((ReadResult.NotAllowed, ReadReason.Ended, (DateTime?)to), (ended.Result, ended.Reason, ended.ReasonTime));
        Assert.Equal("Not allowed, ended 26 Nov 2026", LicensePlateTexts.Reason(ended.Result, ended.Reason, ended.ReasonTime));
        Assert.Equal(ReadResult.Allowed, matcher.Match("G55555K", AtGarage, Now).Result);

        // Near matches only count plates allowed now.
        Assert.Equal(ReadResult.Unknown, matcher.Match("W12345B", AtGarage, Now).Result);
        Assert.Equal(ReadResult.Allowed, matcher.Match("G55555X", AtGarage, Now).Result);
    }

    [Fact]
    public void Allowed_to_is_the_end_itself()
    {
        var plate = P("W12345A", Garage) with { AllowedTo = Now };

        Assert.False(PlateMatcher.IsAllowed(plate, AtGarage, Now));
        Assert.True(PlateMatcher.IsAllowed(plate, AtGarage, Now.AddMinutes(-1)));
    }

    [Fact]
    public void A_changed_plate_list_reaches_the_near_match_index_of_its_tags()
    {
        var source = new Source(P("W12345A", Garage));
        var matcher = new PlateMatcher(source) { Deviation = 1, RebuildInline = true };
        Assert.Equal(ReadResult.Unknown, matcher.Match("K77777B", AtGarage, Now).Result);

        source.Plates["K77777A"] = P("K77777A", Garage);
        matcher.Changed([Garage]);

        Assert.Equal("K77777A", matcher.Match("K77777B", AtGarage, Now).Plate!.Text);

        // Deleted: the old index entry is checked against the list and dropped.
        source.Plates.Remove("K77777A");
        Assert.Equal(ReadResult.Unknown, matcher.Match("K77777B", AtGarage, Now).Result);
    }

    [Fact]
    public void Each_camera_tag_set_has_its_own_index()
    {
        var matcher = Matcher(1, P("W12345A", Garage), P("S12345A", Staff));

        Assert.Equal("W12345A", matcher.Match("W12345B", AtGarage, Now).Plate!.Text);
        Assert.Equal("S12345A", matcher.Match("S12345B", new HashSet<Guid> { Staff }, Now).Plate!.Text);
        Assert.Equal(ReadResult.Unknown, matcher.Match("S12345B", AtGarage, Now).Result);
        Assert.Equal(2, matcher.SetCount);
    }

    [Theory]
    [InlineData("w-12 345.a", "W12345A")]
    [InlineData(" ab 12 ", "AB12")]
    public void Plates_are_kept_upper_case_without_spaces_dashes_and_dots(string typed, string kept)
    {
        Assert.Equal(kept, PlateText.Normalize(typed));
        Assert.Null(PlateText.Problem(kept));
    }

    [Theory]
    [InlineData("A", "A plate has 2 to 12 characters.")]
    [InlineData("ABCDEFGHIJKLM", "A plate has 2 to 12 characters.")]
    [InlineData("AB/12", "Use only letters and digits.")]
    [InlineData("", "Enter the plate.")]
    public void A_plate_has_2_to_12_letters_and_digits(string text, string error) =>
        Assert.Equal(error, PlateText.Problem(PlateText.Normalize(text)));

    [Theory]
    [InlineData("KITTEN", "SITTING", 2, 3)]
    [InlineData("ABC", "ABC", 1, 0)]
    [InlineData("ABC", "ABD", 1, 1)]
    [InlineData("ABCDEF", "XYZ", 2, 3)]
    public void The_deviation_is_the_edit_distance_up_to_the_bound(string a, string b, int max, int expected) =>
        Assert.Equal(expected, EditDistance.Bounded(a, b, max));
}
