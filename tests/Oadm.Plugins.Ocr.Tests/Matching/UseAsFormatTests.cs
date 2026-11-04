using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Readers;

namespace Oadm.Plugins.Ocr.Tests.Matching;

/// <summary>"Use read text as format" (docs/spec/ocr.md "Expected text"): a read text turned into a format by <see cref="TextFormat.FromText"/>.</summary>
public sealed class UseAsFormatTests
{
    [Theory]
    [InlineData("MSKU 123456 7", "@@@@ ###### #")]
    [InlineData("4/12", "#/##")]
    [InlineData("DECAF-FEINATO", "@@@@@-@@@@@@@")]
    [InlineData("Müller Straße 5", "@@@@@@ @@@@@@ #")]
    [InlineData("1234,5", "####,#")]
    [InlineData("  AB\n12  ", "@@ ##")]
    [InlineData("A  -  1", "@ - #")]
    [InlineData("", "")]
    public void Digits_become_hashes_letters_become_A_and_the_rest_stays(string text, string format) =>
        Assert.Equal(format, TextFormat.FromText(text));

    [Theory]
    [InlineData("ABCA12", "ABCA12")]
    [InlineData("XBCQ12", "")]
    public void Every_letter_in_a_format_means_itself(string text, string value) =>
        Assert.Equal(value, new TextMatcher().Match([new(text, new(0.1, 0.1, 0.5, 0.1), 90)], Model.ExpectedText.WithFormat("ABCA##")).Match.Value);

    [Fact]
    public void The_format_made_passes_the_usual_check_and_matches_its_text()
    {
        var format = TextFormat.FromText("MSKU 123456 7");

        Assert.Null(ReaderRules.FormatProblem(format));
        Assert.Equal("MSKU 123456 7", new TextMatcher().Match([new("MSKU 123456 7", new(0.1, 0.1, 0.5, 0.1), 90)], Model.ExpectedText.WithFormat(format)).Match.Value);
        Assert.Equal(ReaderRules.FormatWithoutPlaceholder, ReaderRules.FormatProblem(TextFormat.FromText("-/-")));
    }
}
