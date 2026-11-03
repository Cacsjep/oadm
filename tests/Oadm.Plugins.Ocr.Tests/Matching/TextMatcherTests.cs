using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Tests.Matching;

/// <summary>Line order, joining and the format of an area's expected text (docs/spec/ocr.md "Several lines", "Expected text").</summary>
public sealed class TextMatcherTests
{
    private readonly TextMatcher _matcher = new();

    private static TextLine Line(string text, double x, double y, double confidence = 90, double height = 0.05, double width = 0.2) =>
        new(text, new RelativeBox(x, y, width, height), confidence);

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
        Assert.Equal("DECAF-FEINATO 250 g", match.TextRead);
        Assert.Equal("DECAF-\nFEINATO\n250 g", match.RawText);
        Assert.True(match.MatchesFormat);
        Assert.Equal(70, match.Confidence);
        Assert.Empty(match.AllMatches);
        Assert.Null(match.Number);
    }

    [Fact]
    public void TheEventTextIsCutAt500Characters()
    {
        var lines = Enumerable.Range(0, 20).Select(i => Line(new string('x', 40), 0.1, i * 0.04, height: 0.03)).ToList();

        var match = TextMatcher.MatchAll(lines, ExpectedText.AnyText);

        Assert.Equal(LineOrder.MaxEventChars, match.TextRead.Length);
        Assert.Equal(LineOrder.MaxEventChars, match.Text.Length);
    }

    [Fact]
    public void AnEmptyAreaHasNoValueAndDoesNotMatch()
    {
        var match = TextMatcher.MatchAll([Line("  ", 0.1, 0.1)], ExpectedText.WithFormat("#####"));

        Assert.Equal(string.Empty, match.Text);
        Assert.False(match.MatchesFormat);
        Assert.Equal(0, match.Confidence);
        Assert.False(_matcher.Match([], ExpectedText.AnyText).MatchesFormat);
    }

    [Theory]
    [InlineData("#####", "Counter 01234 kWh", "01234")]
    [InlineData("AAAU ###### #", "MSCU 123456 6", "MSCU 123456 6")]
    [InlineData("AAAU ###### #", "MSCU1234566", "MSCU 123456 6")]
    [InlineData("## ## #### ###-#", "31 80 4955 123 4", "31 80 4955 123-4")]
    [InlineData("## ## #### ###-#", "318049551234", "31 80 4955 123-4")]
    [InlineData("## ## #### ###-#", "31 80 4955 123 - 4", "31 80 4955 123-4")]
    [InlineData("*-###", "Bay x123", "x-123")]
    [InlineData("Order ####", "order 4711 paid", "Order 4711")]
    public void TheFormatFindsItsTextAndWritesItInItsForm(string format, string text, string value)
    {
        var match = TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format));

        Assert.True(match.MatchesFormat);
        Assert.Equal(value, match.Text);
        Assert.Equal(text, match.TextRead);
    }

    [Theory]
    [InlineData("#####", "1234")]
    [InlineData("#####", "123456")]
    [InlineData("#####", "x12345")]
    [InlineData("AAA", "A1-")] // a dash is no letter
    [InlineData("##.#", "12,5")]
    [InlineData("A#A", "a 1 b")] // a space where the format has none
    public void TextThatDoesNotFitIsNoMatch(string format, string text)
    {
        var match = TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.WithFormat(format));

        Assert.False(match.MatchesFormat);
        Assert.Equal(string.Empty, match.Text);
        Assert.Equal(text, match.TextRead);
        Assert.Null(match.Number);
    }

    [Theory]
    [InlineData("#####", "O1ISB", "01158")]
    [InlineData("####", "LZG8", "1268")]
    [InlineData("AAA", "05I", "OSI")]
    [InlineData("AAAA", "1826", "IBZG")]
    [InlineData("A#", "80", "B0")] // by position: a letter, then a digit
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

        var format = ExpectedText.WithFormat("AAAU ###### #");
        Assert.Equal("CSQU 305438 3", TextMatcher.MatchAll([Line("CSQU 305438 3", 0.1, 0.1)], format).Text);
        Assert.False(TextMatcher.MatchAll([Line("CSQU 305438 4", 0.1, 0.1)], format).MatchesFormat);

        // A look-alike fixed first: "CSQU 3O5438 3" is the valid number.
        Assert.Equal("CSQU 305438 3", TextMatcher.MatchAll([Line("CSQU 3O5438 3", 0.1, 0.1)], format).Text);
    }

    [Fact]
    public void ANumberSplitOverTwoLinesIsFound()
    {
        var lines = new[] { Line("CSQU 3054", 0.1, 0.1, confidence: 95), Line("38 3", 0.1, 0.2, confidence: 80) };

        var match = TextMatcher.MatchAll(lines, ExpectedText.WithFormat("AAAU ###### #"));

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
    [InlineData("01234", 1234.0)]
    [InlineData("12.5", 12.5)]
    [InlineData("12,5", 12.5)]
    [InlineData(" -3 ", -3.0)]
    public void TheValueIsANumberWhenItIsOne(string text, double number)
    {
        Assert.Equal(number, TextMatcher.ParseNumber(text));
        Assert.Equal(number, TextMatcher.MatchAll([Line(text, 0.1, 0.1)], ExpectedText.AnyText).Number);
    }

    [Theory]
    [InlineData("12.345,6")]
    [InlineData("12 kWh")]
    [InlineData("")]
    [InlineData("١٢")]
    public void OtherTextsAreNoNumber(string text) => Assert.Null(TextMatcher.ParseNumber(text));

    [Fact]
    public void TheModelFormKeepsValueMatchAndRawText()
    {
        var area = _matcher.Match([Line("Meter", 0.1, 0.1), Line("00815", 0.1, 0.2)], ExpectedText.WithFormat("#####"));

        Assert.Equal(new AreaMatch("00815", true, "Meter\n00815"), area);
    }

    [Fact]
    public void AFormatWithoutCharactersIsRefused() =>
        Assert.Throws<ArgumentException>(() => TextFormat.Parse(" - "));
}
