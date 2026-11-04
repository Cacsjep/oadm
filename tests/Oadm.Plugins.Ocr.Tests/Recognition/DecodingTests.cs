using Oadm.Plugins.Ocr.Matching;
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

    [Theory]
    [InlineData("ORDER", true)]
    [InlineData("CAFE", true)]
    [InlineData("1689", false)]
    [InlineData("SOS", false)]
    [InlineData("AE", false)]
    public void Only_lines_read_surely_upright_skip_the_angle_model(string text, bool upright)
    {
        Assert.Equal(upright, OnnxTextRecognizer.SurelyUpright(Scores(Frames(text)), Chars));
    }

    [Theory]
    [InlineData("CSQU3054383", "CSQU 305438 3")]
    [InlineData("TRLZ1234560", "TRLZ 123456 0")]
    [InlineData("CSQJ3054386", "CSQJ 305438 6")]
    public void A_question_mark_finds_every_category_and_keeps_the_letter_read(string text, string value) =>
        Assert.Equal(value, LineReader.Read(Scores(Frames(text)), Chars, FormatPattern.Parse("@@@? ###### #"), RelativeBox.Whole)!.FormatMatch);

    [Fact]
    public void A_lower_case_category_finds_the_upper_case_letter_the_model_reads() =>
        Assert.Equal("CSQU 305438 3", LineReader.Read(Scores(Frames("CSQU3054383")), Chars, FormatPattern.Parse("@@@u ###### #"), RelativeBox.Whole)!.FormatMatch);

    [Fact]
    public void A_question_mark_is_a_category_only_in_a_container_number()
    {
        Assert.True(FormatPattern.Parse("@@@? ###### #")!.IsContainerNumber);
        Assert.Equal(FormatTokenKind.Category, FormatPattern.Parse("@@@? ###### #")!.Tokens[3].Kind);
        Assert.Equal(FormatTokenKind.Literal, FormatPattern.Parse("@@@? ######")!.Tokens[3].Kind);
    }

    [Fact]
    public void A_wrong_check_digit_is_no_match_with_a_question_mark_too() =>
        Assert.Null(LineReader.Read(Scores(Frames("TRLZ1234561")), Chars, FormatPattern.Parse("@@@? ###### #"), RelativeBox.Whole)?.FormatMatch);

    [Fact]
    public void The_pooled_tables_of_the_format_search_leave_no_trace_of_an_earlier_line()
    {
        var pattern = FormatPattern.Parse("##-###")!;
        var fresh = FormatGuidedDecoder.Decode(Scores(Frames("12-345")), Chars, pattern)!;

        // What another line leaves in the shared pool.
        var doubles = System.Buffers.ArrayPool<double>.Shared.Rent(64);
        var ints = System.Buffers.ArrayPool<int>.Shared.Rent(1024);
        Array.Fill(doubles, 1e9);
        Array.Fill(ints, 3);
        System.Buffers.ArrayPool<double>.Shared.Return(doubles);
        System.Buffers.ArrayPool<int>.Shared.Return(ints);

        Assert.Equal(fresh.Text, FormatGuidedDecoder.Decode(Scores(Frames("12-345")), Chars, pattern)!.Text);
    }

    [Fact]
    public void An_unsure_character_asks_the_angle_model()
    {
        Assert.False(OnnxTextRecognizer.SurelyUpright(Scores("O", "_", "R", "_", "D:0.6", "_", "E", "_", "R"), Chars));
    }

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

        var match = FormatGuidedDecoder.Decode(scores, Chars, FormatPattern.Parse("##@#@")!);

        Assert.NotNull(match);
        Assert.Equal("36F1A", match.Text);
        Assert.Equal(0.3f, match.MinProbability, 3);
        Assert.True(match.LogScore < 0);

        var line = LineReader.Read(scores, Chars, FormatPattern.Parse("##@#@"), RelativeBox.Whole);
        Assert.NotNull(line);
        Assert.Equal("ORDER 36F1A", line.Line.Text);
        Assert.Equal("36F1A", line.FormatMatch);

        // The confidence of a line with a match: the lowest character of the match, 0 to 100.
        Assert.Equal(30, line.Line.Confidence, 1);
        Assert.Equal(30, line.MinConfidenceOf(6, 5), 1);
    }

    [Fact]
    public void The_search_may_pass_a_dash_by_but_only_a_dash_it_used_counts()
    {
        var withDash = FormatGuidedDecoder.Decode(Scores(Frames("12-345")), Chars, FormatPattern.Parse("##-###")!);
        var without = FormatGuidedDecoder.Decode(Scores(Frames("12345")), Chars, FormatPattern.Parse("## - ###")!);

        Assert.Equal("12-345", withDash!.Text);
        Assert.True(withDash.AllDashesRead);
        Assert.Equal("12345", without!.Text);
        Assert.False(without.AllDashesRead);
    }

    [Theory]
    [InlineData("CX 1152 7244 0 AT")]
    [InlineData("CX 1152 72 44 0 AT")]
    [InlineData("CP037400 78 3 AT")]
    public void A_line_without_the_dashes_of_its_format_keeps_its_free_reading(string text)
    {
        var line = LineReader.Read(Scores(Frames(text)), Chars, FormatPattern.Parse("@@-####-##"), RelativeBox.Whole);

        Assert.NotNull(line);
        Assert.Null(line.FormatMatch);
        Assert.Equal(text, line.Line.Text); // no dash made up, no gap dropped
        Assert.DoesNotContain('-', line.Line.Text);
    }

    [Fact]
    public void A_dash_the_reader_only_suspected_makes_no_match()
    {
        // A faint mark where a dash would be: the free reading has none, so the format has none either.
        var frames = Frames("DE").Concat(["-:0.3", "_"]).Concat(Frames("2009")).Concat(["-:0.3", "_"]).Concat(Frames("39")).ToArray();

        var line = LineReader.Read(Scores(frames), Chars, FormatPattern.Parse("@@-####-##"), RelativeBox.Whole);

        Assert.NotNull(line);
        Assert.Null(line.FormatMatch);
        Assert.Equal("DE200939", line.Line.Text);
    }

    [Fact]
    public void A_dash_the_format_found_under_another_character_makes_no_match()
    {
        // The reader saw an X where the format wants a dash, the dash its second choice: the search takes it, it was never read.
        var scores = Scores("1", "_", "2", "_", "X|-:0.35", "_", "3", "_", "4");
        var pattern = FormatPattern.Parse("##-##")!;
        Assert.Equal("12-34", FormatGuidedDecoder.Decode(scores, Chars, pattern)!.Text);

        var line = LineReader.Read(scores, Chars, pattern, RelativeBox.Whole);

        Assert.Null(line!.FormatMatch);
        Assert.Equal("12X34", line.Line.Text);
    }

    [Fact]
    public void A_dash_the_reader_read_is_never_left_out_of_a_match()
    {
        var line = LineReader.Read(Scores(Frames("2026-11-04")), Chars, FormatPattern.Parse("####-####"), RelativeBox.Whole);

        Assert.Null(line!.FormatMatch);
        Assert.Equal("2026-11-04", line.Line.Text);
    }

    [Fact]
    public void A_format_without_a_dash_passes_a_read_dash_by_as_before() =>
        Assert.Equal("12345", LineReader.Read(Scores(Frames("12-345")), Chars, FormatPattern.Parse("#####"), RelativeBox.Whole)!.FormatMatch);

    [Theory]
    [InlineData("DE-2009-39", "DE-2009-39")]
    [InlineData("STD DE-2009-39", "STD DE-2009-39")]
    [InlineData("DE - 2009 - 39", "DE-2009-39")] // the format's layout: gaps inside the match go
    public void A_line_with_the_dashes_of_its_format_matches(string text, string lineText)
    {
        var line = LineReader.Read(Scores(Frames(text)), Chars, FormatPattern.Parse("@@-####-##"), RelativeBox.Whole);

        Assert.NotNull(line);
        Assert.Equal("DE-2009-39", line.FormatMatch);
        Assert.Equal(lineText, line.Line.Text);
    }

    [Fact]
    public void A_format_with_a_dash_still_fixes_characters_by_position()
    {
        var frames = Frames("DE-").Concat(["O|0:0.3", "O|0:0.3", "_"]).Concat(Frames("009-39")).ToArray();

        Assert.Equal("DE-0009-39", LineReader.Read(Scores(frames), Chars, FormatPattern.Parse("@@-####-##"), RelativeBox.Whole)!.FormatMatch);
    }

    [Fact]
    public void Spaces_of_a_format_stay_optional_in_the_decoding()
    {
        var line = LineReader.Read(Scores(Frames("MSKU1234565")), Chars, FormatPattern.Parse("@@@U ###### #"), RelativeBox.Whole);

        Assert.Equal("MSKU 123456 5", line!.FormatMatch);
    }

    [Fact]
    public void Dashes_at_the_ends_of_a_format_mean_nothing_as_in_the_matcher() =>
        Assert.Equal("123", LineReader.Read(Scores(Frames("123")), Chars, FormatPattern.Parse("-###-"), RelativeBox.Whole)!.FormatMatch);

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
    public void A_container_number_whose_check_digit_was_misread_is_no_match()
    {
        // CSQU 305438 3 is valid; the reader saw an 8 for the 3 of the check digit, with the 3 second. No character is
        // changed to make the check digit fit: a misread never becomes a value reported as sure.
        var frames = Frames("CSQU 305438 ").Concat(["8|3:0.25", "8|3:0.25", "_"]).ToArray();
        var scores = Scores(frames);
        var pattern = FormatPattern.Parse("@@@U ###### #")!;
        Assert.True(pattern.IsContainerNumber);

        var match = FormatGuidedDecoder.Decode(scores, Chars, pattern);

        Assert.NotNull(match);
        Assert.False(match.CheckDigitValid);
        Assert.Equal("CSQU3054388", match.Text.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Null(LineReader.Read(scores, Chars, pattern, RelativeBox.Whole)!.FormatMatch);
    }

    [Fact]
    public void A_container_number_with_its_check_digit_matches()
    {
        var scores = Scores(Frames("CSQU 305438 3"));
        var line = LineReader.Read(scores, Chars, FormatPattern.Parse("@@@U ###### #"), RelativeBox.Whole);

        Assert.NotNull(line);
        Assert.Equal("CSQU 305438 3", line.FormatMatch);
    }

    [Fact]
    public void A_container_number_with_more_after_it_is_checked_on_its_first_eleven()
    {
        var pattern = FormatPattern.Parse("@@@U ###### # 22G1")!;
        Assert.True(pattern.IsContainerNumber);
        Assert.False(FormatPattern.Parse("@@@U ######")!.IsContainerNumber);

        Assert.True(FormatGuidedDecoder.Decode(Scores(Frames("CSQU 305438 3 22G1")), Chars, pattern)!.CheckDigitValid);
        Assert.False(FormatGuidedDecoder.Decode(Scores(Frames("CSQU 305438 4 22G1")), Chars, pattern)!.CheckDigitValid);
    }

    [Theory]
    [InlineData("CSQU3054383", true)]
    [InlineData("CSQU 305438 3", true)]
    [InlineData("CSQU 305438 3 22G1", true)]
    [InlineData("CSQU3054384", false)]
    [InlineData("MSCU 123456 7", false)]
    [InlineData("CSQU305438", false)]
    [InlineData("csqu3054383", true)] // the same rule as the matcher's: letters in either case
    public void Iso6346_check_digit(string number, bool valid) => Assert.Equal(valid, ContainerCheck.IsValid(number));

    [Fact]
    public void Iso6346_letter_values_skip_the_multiples_of_eleven()
    {
        // A container number with an L (23, after skipping 22) and a V (34, after skipping 33): HLXU 571426 0 from the set.
        Assert.Equal(0, ContainerCheck.CheckDigit("HLXU571426"));
        Assert.Equal(3, ContainerCheck.CheckDigit("CSQU305438"));
        Assert.Equal(-1, ContainerCheck.CheckDigit("CSQ1305438"));
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
