using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Tests.Matching;

/// <summary>
/// Line order, joining and the format of an area's expected text (docs/spec/ocr.md "Several lines", "Expected text"); the rows
/// themselves: <see cref="LineOrderTests"/>.
/// </summary>
public sealed class TextMatcherTests
{
    private readonly TextMatcher _matcher = new();

    private static TextLine Line(string text, double x, double y, double confidence = 90, double height = 0.05, double width = 0.2) =>
        new(text, new RelativeBox(x, y, width, height), confidence);

    [Fact]
    public void A_label_line_without_dashes_never_beats_the_value_with_them()
    {
        var format = ExpectedText.WithFormat("@@-####-##");
        var label = new TextLine("CP037400 78 3 AT", new(0.1, 0.7, 0.4, 0.1), 100);
        var routing = new TextLine("STD DE-2009-39", new(0.1, 0.4, 0.4, 0.1), 85);

        var match = TextMatcher.MatchAll([label, routing], format);

        Assert.Equal("DE-2009-39", match.Text);
        Assert.Equal(["DE-2009-39"], match.AllMatches);
    }

    [Theory]
    [InlineData("CP037400 78 3 AT")]
    [InlineData("CX 1152 72 44 0 AT")]
    [InlineData("CX 1152 7244 0 AT")]
    [InlineData("AB123456")]
    [InlineData("AB 1234 56")]
    [InlineData("AB-123456")] // one dash of two
    [InlineData("AB1234-56")]
    public void A_dash_of_the_format_must_be_a_dash_in_the_text(string text)
    {
        var match = TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat("@@-####-##"));

        Assert.False(match.MatchesFormat);
        Assert.Empty(match.Text);
        Assert.Empty(match.AllMatches);
    }

    [Theory]
    [InlineData("DE-2009-39", "DE-2009-39")]
    [InlineData("STD DE-2009-39", "DE-2009-39")]
    [InlineData("DE - 2009 - 39", "DE-2009-39")]
    [InlineData("DE -2009- 39", "DE-2009-39")]
    public void Spaces_around_a_dash_are_allowed(string text, string value) =>
        Assert.Equal(value, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat("@@-####-##")).Text);

    [Theory]
    [InlineData("@@@U ###### #", "MSKU1234565", "MSKU 123456 5")]
    [InlineData("@@@U ###### #", "MSKU 123456 5", "MSKU 123456 5")]
    [InlineData("### ###", "123456", "123 456")]
    [InlineData("#####", "12345", "12345")]
    public void Spaces_of_a_format_stay_optional_and_a_format_without_a_dash_behaves_as_before(string format, string text, string value) =>
        Assert.Equal(value, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format)).Text);

    [Fact]
    public void Of_several_matches_the_surest_wins_whatever_its_spaces()
    {
        var match = TextMatcher.MatchAll(
            [new TextLine("1234 5678", new(0.1, 0.1, 0.4, 0.1), 80), new TextLine("87654321", new(0.1, 0.4, 0.4, 0.1), 95)],
            ExpectedText.WithFormat("#### ####"));

        Assert.Equal("8765 4321", match.Text);
    }

    [Fact]
    public void The_value_keeps_how_its_line_lies_and_a_value_over_two_lines_has_no_turn()
    {
        var turn = new LineTurn(100, 200, 300, 40, MathF.PI);
        var line = Line("Order 1001", 0.1, 0.1) with { Turn = turn };
        Assert.Equal(turn, _matcher.Match([line], ExpectedText.WithFormat("Order ####")).MatchTurn);
        Assert.Equal(turn, _matcher.Match([line], ExpectedText.AnyText).MatchTurn);

        var split = new[] { Line("CSQU 3054", 0.1, 0.1) with { Turn = turn }, Line("38 3", 0.1, 0.2) with { Turn = turn with { CenterY = 150 } } };
        var value = _matcher.Match(split, ExpectedText.WithFormat("@@@U ###### #"));
        Assert.Equal("CSQU 305438 3", value.Match.Value);
        Assert.Null(value.MatchTurn);
        Assert.Null(_matcher.Match(split, ExpectedText.AnyText).MatchTurn);
    }

    [Fact]
    public void LinesAreReadTopToBottomThenLeftToRight()
    {
        // Out of order; "B" and "A" share a row though "B" sits a little lower.
        var lines = new[]
        {
            Line("third", 0.1, 0.50),
            Line("B", 0.5, 0.12),
            Line("A", 0.1, 0.10),
            Line("fourth", 0.5, 0.505),
        };

        var ordered = LineOrder.Order(lines);

        Assert.Equal(["A", "B", "third", "fourth"], ordered.Select(l => l.Text));
    }

    [Fact]
    public void LinesThatBarelyOverlapAreRowsOfTheirOwn()
    {
        var lines = new[] { Line("right top", 0.6, 0.10), Line("left lower", 0.1, 0.13) };

        Assert.Equal(["right top", "left lower"], LineOrder.Order(lines).Select(l => l.Text));
    }

    [Fact]
    public void AnyTextJoinsTheLinesWithASpaceAndAfterADashWithout()
    {
        var match = TextMatcher.MatchAll([Line("DECAF-", 0.1, 0.1), Line("FEINATO", 0.1, 0.2), Line("250 g", 0.1, 0.3, confidence: 70)], ExpectedText.AnyText);

        Assert.Equal("DECAF-FEINATO 250 g", match.Text);
        Assert.Equal("DECAF-\nFEINATO\n250 g", match.RawText);
        Assert.False(match.MatchesFormat); // Any text has no format to match: Matches format is No
        Assert.Equal(70, match.Confidence);
        Assert.Empty(match.AllMatches);
        Assert.Empty(match.Number);
    }

    [Fact]
    public void TheEventTextIsCutAt500Characters()
    {
        var lines = Enumerable.Range(0, 20).Select(i => Line(new string('x', 40), 0.1, i * 0.04, height: 0.03)).ToList();

        var match = TextMatcher.MatchAll(lines, ExpectedText.AnyText);

        Assert.Equal(LineOrder.MaxEventChars, match.RawText.Length);
        Assert.Equal(LineOrder.MaxEventChars, match.Text.Length);
    }

    [Fact]
    public void TheValueIsOneLineAndTheRawTextHoldsTheRows()
    {
        // A receipt's rows: the article left, its price right, a little lower (the paper is never quite level).
        var lines = new[]
        {
            Line("10.60", 0.6, 0.305, width: 0.1), Line("1x Water", 0.1, 0.2), Line("TOTAL EUR", 0.1, 0.3),
            Line("1.90", 0.6, 0.205, width: 0.1), Line("ORDER 23F7H", 0.1, 0.4, confidence: 70),
        };

        var match = TextMatcher.MatchAll(lines, ExpectedText.AnyText);

        Assert.Equal("1x Water 1.90 TOTAL EUR 10.60 ORDER 23F7H", match.Text);
        Assert.Equal("1x Water 1.90\nTOTAL EUR 10.60\nORDER 23F7H", match.RawText);
        Assert.DoesNotContain('\n', match.Text);
        Assert.Empty(match.Number);
        Assert.Equal(70, match.Confidence);

        // With a format the value is the match; the raw text is still the whole area, in rows.
        var order = TextMatcher.MatchAll(lines, ExpectedText.WithFormat("##@#@"));
        Assert.Equal("23F7H", order.Text);
        Assert.Equal(match.RawText, order.RawText);
        var price = TextMatcher.MatchAll(lines, ExpectedText.WithFormat("##.##"));
        Assert.Equal("10.60", price.Text);
        Assert.Equal("10.6", price.Number);
        Assert.Equal(["10.60"], price.AllMatches);
    }

    [Fact]
    public void ANumberOnItsOwnStaysANumberWhateverItsRows()
    {
        Assert.Equal("815", TextMatcher.MatchAll([Line("00815", 0.1, 0.1)], ExpectedText.AnyText).Number);

        // Two rows are no number, as before: the value is "00815 kWh".
        var two = TextMatcher.MatchAll([Line("00815", 0.1, 0.1), Line("kWh", 0.1, 0.2)], ExpectedText.AnyText);
        Assert.Equal("00815 kWh", two.Text);
        Assert.Equal("00815\nkWh", two.RawText);
        Assert.Empty(two.Number);
    }

    [Fact]
    public void ATextFoundTwiceIsReadOnceButAFormatStillLooksAtBothLines()
    {
        // The tile overlap: the price found whole and once more cut ("5.80" and ".80"); a wide piece "ORDER 23F" cut by a
        // tile's edge over the whole code "23F7H".
        var lines = new[]
        {
            Line("2x Espresso", 0.1, 0.1), Line("5.80", 0.60, 0.1, width: 0.10), Line(".80", 0.63, 0.101, width: 0.07),
            Line("ORDER 23F", 0.1, 0.3, width: 0.25), Line("23F7H", 0.25, 0.301, width: 0.15),
        };

        var any = TextMatcher.MatchAll(lines, ExpectedText.AnyText);
        Assert.Equal("2x Espresso 5.80\nORDER 23F", any.RawText);
        Assert.Equal("2x Espresso 5.80 ORDER 23F", any.Text);

        var order = TextMatcher.MatchAll(lines, ExpectedText.WithFormat("##@#@"));
        Assert.Equal("23F7H", order.Text);
        Assert.Equal(any.RawText, order.RawText);
    }

    [Fact]
    public void AnEmptyAreaHasNoValueAndDoesNotMatch()
    {
        var match = TextMatcher.MatchAll([Line("  ", 0.1, 0.1)], ExpectedText.WithFormat("#####"));

        Assert.Equal(string.Empty, match.Text);
        Assert.False(match.MatchesFormat);
        Assert.Equal(0, match.Confidence);
        Assert.False(_matcher.Match([], ExpectedText.AnyText).Match.MatchesFormat);
    }

    [Theory]
    [InlineData("#####", "Counter 01234 kWh", "01234")]
    [InlineData("@@@U ###### #", "MSCU 123456 6", "MSCU 123456 6")]
    [InlineData("@@@U ###### #", "MSCU1234566", "MSCU 123456 6")]
    [InlineData("## ## #### ###-#", "31 80 4955 123-4", "31 80 4955 123-4")]
    [InlineData("## ## #### ###-#", "31804955123-4", "31 80 4955 123-4")]
    [InlineData("## ## #### ###-#", "31 80 4955 123 - 4", "31 80 4955 123-4")]
    [InlineData("*-###", "Bay x-123", "x-123")]
    [InlineData("Order ####", "order 4711 paid", "Order 4711")]
    public void TheFormatFindsItsTextAndWritesItInItsForm(string format, string text, string value)
    {
        var match = TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format));

        Assert.True(match.MatchesFormat);
        Assert.Equal(value, match.Text);
        Assert.Equal(text, match.RawText);
    }

    [Theory]
    [InlineData("#####", "1234")]
    [InlineData("#####", "123456")]
    [InlineData("#####", "x12345")]
    [InlineData("@@@", "A1-")] // a dash is no letter
    [InlineData("##.#", "12,5")]
    [InlineData("@#@", "a 1 b")] // a space where the format has none
    [InlineData("## ## #### ###-#", "31 80 4955 123 4")] // a space is no dash
    [InlineData("*-###", "Bay x123")]
    public void TextThatDoesNotFitIsNoMatch(string format, string text)
    {
        var match = TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format));

        Assert.False(match.MatchesFormat);
        Assert.Equal(string.Empty, match.Text);
        Assert.Equal(text, match.RawText);
        Assert.Empty(match.Number);
    }

    [Theory]
    [InlineData("#####", "O1ISB", "01158")]
    [InlineData("####", "LZG8", "1268")]
    [InlineData("@@@", "05I", "OSI")]
    [InlineData("@@@@", "1826", "IBZG")]
    [InlineData("@#", "80", "B0")] // by position: a letter, then a digit
    public void LookAlikesAreFixedByPosition(string format, string text, string value)
    {
        var match = TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format));

        Assert.Equal(value, match.Text);
    }

    [Fact]
    public void AContainerNumberNeedsItsCheckDigit()
    {
        Assert.Equal(3, ContainerCheck.CheckDigit("CSQU305438"));
        Assert.True(ContainerCheck.IsValid("CSQU 305438 3"));
        Assert.False(ContainerCheck.IsValid("CSQU 305438 4"));

        var format = ExpectedText.WithFormat("@@@U ###### #");
        Assert.Equal("CSQU 305438 3", TextMatcher.MatchAll([Line("CSQU 305438 3", 0.1, 0.1)], format).Text);
        Assert.False(TextMatcher.MatchAll([Line("CSQU 305438 4", 0.1, 0.1)], format).MatchesFormat);

        // A look-alike fixed first: "CSQU 3O5438 3" is the valid number.
        Assert.Equal("CSQU 305438 3", TextMatcher.MatchAll([Line("CSQU 3O5438 3", 0.1, 0.1)], format).Text);
    }

    [Fact]
    public void ANumberSplitOverTwoLinesIsFound()
    {
        var lines = new[] { Line("CSQU 3054", 0.1, 0.1, confidence: 95), Line("38 3", 0.1, 0.2, confidence: 80) };

        var match = TextMatcher.MatchAll(lines, ExpectedText.WithFormat("@@@U ###### #"));

        Assert.True(match.MatchesFormat);
        Assert.Equal("CSQU 305438 3", match.Text);
        Assert.Equal(80, match.Confidence);
        // The cut-out covers both lines.
        var box = match.TextBox!.Value;
        Assert.Equal(0.1, box.Y, 9);
        Assert.Equal(0.25, box.Y + box.Height, 9);
    }

    [Fact]
    public void ANumberSplitAfterASeparatorIsFound()
    {
        var match = TextMatcher.MatchAll([Line("Order", 0.1, 0.1), Line("4711", 0.1, 0.2)], ExpectedText.WithFormat("Order ####"));

        Assert.Equal("Order 4711", match.Text);
    }

    [Fact]
    public void SeveralMatchesTakeTheMostConfidentAndListEveryDifferentOne()
    {
        var lines = new[]
        {
            Line("Order 1001", 0.1, 0.1, confidence: 80),
            Line("Order 1002", 0.1, 0.3, confidence: 97),
            Line("Order 1001", 0.6, 0.5, confidence: 85),
        };

        var match = TextMatcher.MatchAll(lines, ExpectedText.WithFormat("Order ####"));

        Assert.Equal("Order 1002", match.Text);
        Assert.Equal(97, match.Confidence);
        Assert.Equal(["Order 1001", "Order 1002"], match.AllMatches);
        Assert.Equal("Order 1001 Order 1002", match.AllMatchesText);
        Assert.Equal(lines[1].Box, match.TextBox);
    }

    [Fact]
    public void OnATieTheLargerTextWins()
    {
        var lines = new[] { Line("11111", 0.1, 0.1, height: 0.04), Line("22222", 0.1, 0.3, height: 0.09) };

        Assert.Equal("22222", TextMatcher.MatchAll(lines, ExpectedText.WithFormat("#####")).Text);
    }

    [Fact]
    public void AllMatchesHoldsAtMostTen()
    {
        var lines = Enumerable.Range(0, 15).Select(i => Line($"{i:00000}", 0.1, i * 0.06, height: 0.05)).ToList();

        var match = TextMatcher.MatchAll(lines, ExpectedText.WithFormat("#####"));

        Assert.Equal(TextMatcher.MaxMatches, match.AllMatches.Count);
        Assert.Equal("00000", match.AllMatches[0]);
    }

    [Theory]
    [InlineData("01234", "1234")]
    [InlineData("12.5", "12.5")]
    [InlineData("12,5", "12.5")]
    [InlineData(" -3 ", "-3")]
    public void TheValueIsANumberWhenItIsOne(string text, string number)
    {
        Assert.Equal(number, NumberText.Of(text));
        Assert.Equal(number, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.AnyText).Number);
    }

    [Theory]
    [InlineData("12.345,6")]
    [InlineData("12 kWh")]
    [InlineData("")]
    [InlineData("١٢")]
    public void OtherTextsAreNoNumber(string text) => Assert.Empty(NumberText.Of(text));

    [Fact]
    public void TheModelFormKeepsValueMatchAndRawText()
    {
        var area = _matcher.Match([Line("Meter", 0.1, 0.1), Line("00815", 0.1, 0.2)], ExpectedText.WithFormat("#####"));

        Assert.Equal(new AreaMatch("00815", true, "Meter\n00815"), area.Match);
        Assert.Equal(["00815"], area.AllMatches);
    }

    [Theory]
    [InlineData("@@@U ######", "MSCU 123456", "MSCU 123456")] // no check digit in the format: nothing to check
    [InlineData("@@@U ###### # 22G1", "CSQU 305438 3 22G1", "CSQU 305438 3 22G1")] // the size and type code after it
    [InlineData("@@@U ###### # 22G1", "CSQU3054383 22G1", "CSQU 305438 3 22G1")]
    [InlineData("@@@U-######-#", "CSQU-305438-3", "CSQU-305438-3")]
    [InlineData("@@@Z ###### #", "TRLZ1234560", "TRLZ 123456 0")] // a chassis number
    [InlineData("@@@J ###### #", "CSQJ 305438 6", "CSQJ 305438 6")]
    public void ContainerFormatsCheckTheFirstElevenLettersAndDigits(string format, string text, string value)
    {
        Assert.Equal(value, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format)).Text);
    }

    [Theory]
    [InlineData("@@@U ###### #", true)]
    [InlineData(" @@@U-######-# 22G1", true)]
    [InlineData("@@@U ######", false)]
    [InlineData("@@@U ###### @", false)]
    [InlineData("@@@@ ###### #", false)]
    [InlineData("@@@J ###### #", true)]
    [InlineData("@@@Z ###### #", true)]
    [InlineData("@@@u ###### #", true)]
    [InlineData("@@@? ###### #", true)]
    [InlineData("@@@?-######-#", true)]
    [InlineData("@@@? ######", false)]
    [InlineData("@@@X ###### #", false)]
    [InlineData("#####", false)]
    public void AFormatHasACheckDigitWhenItStartsWithAContainerNumber(string format, bool check)
    {
        Assert.Equal(check, ContainerCheck.FormatHasCheckDigit(format));
        Assert.Equal(check, TextFormat.Parse(format).HasContainerCheckDigit);
    }

    [Fact]
    public void AWrongCheckDigitBeforeATypeCodeIsNoMatch() =>
        Assert.False(TextMatcher.MatchAll([Line("CSQU 305438 4 22G1", 0.1, 0.1)], ExpectedText.WithFormat("@@@U ###### # 22G1")).MatchesFormat);

    [Theory]
    [InlineData("@@@Z ###### #", "TRLZ 123456 1")] // a chassis number
    [InlineData("@@@J ###### #", "CSQJ 305438 5")]
    [InlineData("@@@u ###### #", "CSQU 305438 4")]
    public void AWrongCheckDigitIsNoMatchForEveryEquipmentCategory(string format, string text) =>
        Assert.False(TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format)).MatchesFormat);

    [Fact]
    public void ALowerCaseCategoryGivesTheValueInUpperCase() =>
        Assert.Equal("CSQU 305438 3", TextMatcher.MatchAll([Line("CSQU 305438 3", 0.1, 0.1)], ExpectedText.WithFormat("@@@u ###### #")).Text);

    [Theory]
    [InlineData("CSQU 305438 3")]
    [InlineData("CSQJ 305438 6")]
    [InlineData("TRLZ 123456 0")]
    public void AQuestionMarkReadsEveryEquipmentCategoryAndKeepsTheLetterRead(string text) =>
        Assert.Equal(text, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat("@@@? ###### #")).Text);

    [Theory]
    [InlineData("CSQU 305438 4")] // a wrong check digit
    [InlineData("TRLZ 123456 1")]
    [InlineData("MSKX 123456 5")] // no category of the standard
    public void AQuestionMarkStillChecksTheNumber(string text) =>
        Assert.False(TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat("@@@? ###### #")).MatchesFormat);

    [Fact]
    public void AQuestionMarkTakesTheLookAlikeOfACategory() =>
        Assert.Equal("TRLZ 123456 0", TextMatcher.MatchAll([Line("TRL2 123456 0", 0.1, 0.1)], ExpectedText.WithFormat("@@@? ###### #")).Text);

    [Theory]
    [InlineData("##?", "12?", "12?")] // anywhere else it is a plain character
    [InlineData("@@@? ######", "ABC? 123456", "ABC? 123456")] // no check digit: no container number
    [InlineData("@@@U ###### # ?", "CSQU 305438 3 ?", "CSQU 305438 3 ?")]
    public void AQuestionMarkOutsideAContainerNumberIsItself(string format, string text, string value) =>
        Assert.Equal(value, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format)).Text);

    [Theory]
    [InlineData("(###)", "Lane(123)", "(123)")] // a format that starts with a sign may touch letters
    [InlineData("##)", "12)ab", "12)")]
    public void AFormatThatStartsOrEndsWithASignMayTouchText(string format, string text, string value) =>
        Assert.Equal(value, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format)).Text);

    [Fact]
    public void AFormatWithoutCharactersIsRefused() =>
        Assert.Throws<ArgumentException>(() => TextFormat.Parse(" - "));
}
