using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// CTC decoding, the format-guided decoding and the ISO 6346 check digit (docs/spec/ocr.md "Reading" step 4 and "Accuracy"),
/// on made-up reader outputs: each frame names its likely characters.
/// </summary>
public sealed class DecodingTests
{
    internal static readonly CharacterList Chars = new(
        [.. "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyzäöüß-/.".Select(c => c.ToString())]);

    /// <summary>
    /// A reader output: one frame per entry; "_" is a blank frame, " " a space, "X" the character X at 0.97, "X:0.4" at 0.4,
    /// "X|Y:0.3" X with Y at 0.3 as the runner-up. The rest of each frame's probability goes to the blank.
    /// </summary>
    internal static LineScores Scores(params string[] frames)
    {
        var classes = Chars.ClassCount;
        var data = new float[frames.Length * classes];
        for (var t = 0; t < frames.Length; t++)
        {
            var row = data.AsSpan(t * classes, classes);
            var spec = frames[t];
            if (spec == "_")
            {
                row[CharacterList.BlankClass] = 0.98f;
                row[Chars.SpaceClass] = 0.01f;
                continue;
            }

            var parts = spec.Split('|');
            var first = parts[0].Split(':');
            var firstClass = first[0] == " " ? Chars.SpaceClass : Chars.ClassOf(first[0]);
            var firstP = first.Length > 1 ? Probability(first[1]) : 0.97f;
            if (parts.Length > 1)
            {
                var second = parts[1].Split(':');
                var secondP = Probability(second[1]);
                row[Chars.ClassOf(second[0])] = secondP;
                firstP = first.Length > 1 ? firstP : 0.97f - secondP;
            }

            row[firstClass] = firstP;
            row[CharacterList.BlankClass] = 1 - row.ToArray().Sum();
        }

        return new LineScores(data, frames.Length, classes);
    }

    private static float Probability(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The frames of a text: each character for two frames, a blank after each.</summary>
    internal static string[] Frames(string text) => [.. text.SelectMany(c => new[] { c.ToString(), c.ToString(), "_" })];

    [Fact]
    public void Best_path_merges_repeats_and_keeps_doubles_split_by_a_blank()
    {
        var chars = CtcDecoder.Greedy(Scores("_", "1", "1", "_", "1", "A", "A", "_", " ", "B"), Chars);

        Assert.Equal("11A B", CtcDecoder.Text(chars));
        Assert.Equal(1, chars[0].FirstFrame);
        Assert.Equal(2, chars[0].LastFrame);
        Assert.All(chars, c => Assert.Equal(0.97f, c.Probability, 3));
    }

    [Fact]
    public void A_format_limits_each_position_while_decoding()
    {
        // "ORDER 36F1A" where the reader saw an I for the 1 (the 1 second) and an O for the 0... of a letter position.
        var frames = Frames("ORDER").Concat([" ", "_"]).Concat(Frames("36F")).Concat(["I|1:0.3", "I|1:0.3", "_"]).Concat(Frames("A")).ToArray();
        var scores = Scores(frames);
        Assert.Equal("ORDER 36FIA", CtcDecoder.Text(CtcDecoder.Greedy(scores, Chars)));

        var match = FormatGuidedDecoder.Decode(scores, Chars, FormatPattern.Parse("##A#A")!);

        Assert.NotNull(match);
        Assert.Equal("36F1A", match.Text);
        Assert.Equal(0.3f, match.MinProbability, 3);
        Assert.True(match.LogScore < 0);

        var line = LineReader.Read(scores, Chars, FormatPattern.Parse("##A#A"), RelativeBox.Whole);
        Assert.NotNull(line);
        Assert.Equal("ORDER 36F1A", line.Line.Text);
        Assert.Equal("36F1A", line.FormatMatch);

        // The confidence of a line with a match: the lowest character of the match, 0 to 100.
        Assert.Equal(30, line.Line.Confidence, 1);
        Assert.Equal(30, line.MinConfidenceOf(6, 5), 1);
    }

    [Fact]
    public void Dashes_and_spaces_of_a_format_may_be_missing()
    {
        var withDash = FormatGuidedDecoder.Decode(Scores(Frames("12-345")), Chars, FormatPattern.Parse("##-###")!);
        var without = FormatGuidedDecoder.Decode(Scores(Frames("12345")), Chars, FormatPattern.Parse("## - ###")!);

        Assert.Equal("12-345", withDash!.Text);
        Assert.Equal("12345", without!.Text);
    }

    [Fact]
    public void A_line_too_short_for_the_format_gives_no_match()
    {
        // Fewer frames than positions: no match at all; enough frames but other text: a forced fit the line does not take.
        Assert.Null(FormatGuidedDecoder.Decode(Scores("1", "_", "2"), Chars, FormatPattern.Parse("#####")!));
        Assert.Null(LineReader.Read(Scores(Frames("12")), Chars, FormatPattern.Parse("#####"), RelativeBox.Whole)!.FormatMatch);
        Assert.Null(FormatPattern.Parse("  "));
        Assert.Null(FormatPattern.Parse("--"));
    }

    [Fact]
    public void A_format_forced_onto_other_text_is_not_taken()
    {
        // "Thank you" holds no five digits: the decoder finds its best fit, the line keeps the free reading.
        var scores = Scores(Frames("Thank you"));
        var line = LineReader.Read(scores, Chars, FormatPattern.Parse("#####"), RelativeBox.Whole);

        Assert.NotNull(line);
        Assert.Equal("Thank you", line.Line.Text);
        Assert.Null(line.FormatMatch);
    }

    [Fact]
    public void A_container_number_gets_the_one_change_its_check_digit_needs()
    {
        // CSQU 305438 3 is valid; the reader saw an 8 for the 3 of the check digit, with the 3 second.
        var frames = Frames("CSQU 305438 ").Concat(["8|3:0.25", "8|3:0.25", "_"]).ToArray();
        var scores = Scores(frames);
        var pattern = FormatPattern.Parse("AAAU ###### #")!;
        Assert.True(pattern.IsContainerNumber);

        var match = FormatGuidedDecoder.Decode(scores, Chars, pattern);

        Assert.NotNull(match);
        Assert.True(match.CheckDigitValid);
        Assert.Equal("CSQU3054383", match.Text.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal(0.25f, match.MinProbability, 3);
    }

    [Fact]
    public void A_container_number_no_single_change_can_fix_is_no_match()
    {
        var scores = Scores(Frames("CSQU 305438 8"));
        var line = LineReader.Read(scores, Chars, FormatPattern.Parse("AAAU ###### #"), RelativeBox.Whole);

        Assert.NotNull(line);
        Assert.Null(line.FormatMatch);
        Assert.Equal("CSQU 305438 8", line.Line.Text);
    }

    [Theory]
    [InlineData("CSQU3054383", true)]
    [InlineData("CSQU 305438 3", true)]
    [InlineData("CSQU3054384", false)]
    [InlineData("MSCU 123456 7", false)]
    [InlineData("CSQU305438", false)]
    [InlineData("csqu3054383", false)]
    public void Iso6346_check_digit(string number, bool valid) => Assert.Equal(valid, Iso6346.IsValid(number));

    [Fact]
    public void Iso6346_letter_values_skip_the_multiples_of_eleven()
    {
        // A container number with an L (23, after skipping 22) and a V (34, after skipping 33): HLXU 571426 0 from the set.
        Assert.Equal(0, Iso6346.CheckDigit("HLXU571426"));
        Assert.Equal(3, Iso6346.CheckDigit("CSQU305438"));
        Assert.Equal(-1, Iso6346.CheckDigit("CSQ1305438"));
    }

    [Fact]
    public void Lines_below_half_confidence_are_dropped()
    {
        var unsure = Scores("A:0.4", "_", "C:0.45");

        Assert.Null(LineReader.Read(unsure, Chars, null, RelativeBox.Whole));
        Assert.Null(LineReader.Read(Scores("_", "_", " "), Chars, null, RelativeBox.Whole));
    }

    [Fact]
    public void The_confidence_without_a_format_is_the_weakest_character()
    {
        var line = LineReader.Read(Scores("A", "_", "B|8:0.4", "_", "C"), Chars, null, RelativeBox.Whole);

        Assert.NotNull(line);
        Assert.Equal("ABC", line.Line.Text);
        Assert.Equal(57, line.Line.Confidence, 0);
        Assert.Equal(57, line.MinConfidence, 0);
        Assert.Equal(new[] { "A", "B", "C" }, line.Characters.Select(c => c.Text));
    }

    [Fact]
    public void The_character_list_has_blank_first_and_space_last()
    {
        var latin = CharacterList.Load(Path.Combine(AppContext.BaseDirectory, "Models", ModelFiles.RecognitionLatinCharacters));

        Assert.Equal(string.Empty, latin[CharacterList.BlankClass]);
        Assert.Equal(" ", latin[latin.SpaceClass]);
        Assert.Equal(latin.ClassCount - 1, latin.SpaceClass);
        Assert.Equal(10, latin.Digits.Length);
        Assert.Equal(26, latin.UpperLatinLetters.Length);
        Assert.Contains(latin.ClassOf("ä"), latin.Letters);
        Assert.Contains(latin.ClassOf("ß"), latin.Letters);
        Assert.Equal(-1, latin.ClassOf("中"));
    }
}
