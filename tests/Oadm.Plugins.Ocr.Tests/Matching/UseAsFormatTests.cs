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

    [Theory]
    [InlineData("MSKU 123456 5", "@@@? ###### #")]
    [InlineData("msku 123456 5", "@@@? ###### #")]
    [InlineData("CSQU3054383", "@@@?#######")]
    [InlineData("CSQ U 305438 3", "@@@ ? ###### #")]
    [InlineData("\u0001CSQU 305438 3", "@@@? ###### #")]
    [InlineData("  CSQU\n305438 3", "@@@? ###### #")]
    [InlineData("TRLZ 123456 0", "@@@? ###### #")]
    [InlineData("CSQJ 305438 6", "@@@? ###### #")]
    [InlineData("CSQU 305438 3 22G1", "@@@? ###### # ##@#")]
    [InlineData("MSKU 123456 7", "@@@@ ###### #")] // the check digit does not fit: a misread stays open
    [InlineData("MSKX 123456 5", "@@@@ ###### #")] // no category of the standard
    [InlineData("ABC? 123456 7", "@@@@ ###### #")] // a question mark read there is no category
    [InlineData("MSK", "@@@")]
    [InlineData("MS-U", "@@-@")]
    public void A_container_number_with_a_fitting_check_digit_is_checked_for_every_category(string text, string format) =>
        Assert.Equal(format, TextFormat.FromText(text));

    [Fact]
    public void The_format_of_a_container_number_checks_its_digit_and_reads_a_chassis_too()
    {
        var format = TextFormat.Parse(TextFormat.FromText("CSQU 305438 3"));

        Assert.True(format.HasContainerCheckDigit);
        Assert.Equal("TRLZ 123456 0", Assert.Single(format.FindAll("TRLZ 123456 0")).Value);
    }

    [Fact]
    public void The_format_made_passes_the_usual_check_and_matches_its_text()
    {
        var format = TextFormat.FromText("MSKU 123456 7");

        Assert.Null(ReaderRules.FormatProblem(format));
        Assert.Equal("MSKU 123456 7", new TextMatcher().Match([new("MSKU 123456 7", new(0.1, 0.1, 0.5, 0.1), 90)], Model.ExpectedText.WithFormat(format)).Match.Value);
        Assert.Equal(ReaderRules.FormatWithoutPlaceholder, ReaderRules.FormatProblem(TextFormat.FromText("-/-")));
    }
}
