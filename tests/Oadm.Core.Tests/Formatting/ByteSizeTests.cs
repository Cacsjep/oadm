using System.Globalization;
using Oadm.Sdk.Formatting;

namespace Oadm.Core.Tests.Formatting;

/// <summary>The one text of a number of bytes: steps of 1,024, whole bytes and KB, one decimal from MB on, never "1024 KB".</summary>
public sealed class ByteSizeTests
{
    private const long Kb = 1024;
    private const long Mb = 1024 * Kb;
    private const long Gb = 1024 * Mb;
    private const long Tb = 1024 * Gb;

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(2, "2 bytes")]
    [InlineData(512, "512 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(Kb, "1 KB")]
    [InlineData(1535, "1 KB")]
    [InlineData(1536, "2 KB")]
    [InlineData(412 * Kb, "412 KB")]
    [InlineData((1023 * Kb) + 511, "1023 KB")]
    [InlineData(Mb, "1.0 MB")]
    [InlineData((long)(1.44 * Mb), "1.4 MB")]
    [InlineData(87 * Mb, "87.0 MB")]
    [InlineData(Gb, "1.0 GB")]
    [InlineData((long)(2.06 * Gb), "2.1 GB")]
    [InlineData(48 * Gb, "48.0 GB")]
    [InlineData(Tb, "1.0 TB")]
    [InlineData(3 * Tb / 2, "1.5 TB")]
    public void ASizeReadsInItsUnit(long bytes, string expected) => Assert.Equal(expected, ByteSize.Text(bytes));

    [Theory]
    [InlineData((1023 * Kb) + 512, "1.0 MB")] // 1023.5 KB would round to "1024 KB"
    [InlineData(Mb - 1, "1.0 MB")]
    [InlineData((1023 * Mb) + 996_147, "1023.9 MB")] // just below 1023.95 MB
    [InlineData((1023 * Mb) + 996_148, "1.0 GB")] // just above: would round to "1024.0 MB"
    [InlineData(Gb - 1, "1.0 GB")]
    [InlineData(Tb - 1, "1.0 TB")]
    public void ANumberThatRoundsUpTo1024MovesToTheNextUnit(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Text(bytes));

    [Fact]
    public void TheLargestUnitTakesTheRest()
    {
        Assert.Equal("2048.0 TB", ByteSize.Text(2048 * Tb));
        Assert.Equal("8388608.0 TB", ByteSize.Text(long.MaxValue));
    }

    [Theory]
    [InlineData(-1, "-1 byte")]
    [InlineData(-512, "-512 bytes")]
    [InlineData(-Kb, "-1 KB")]
    [InlineData(-3 * Mb / 2, "-1.5 MB")]
    [InlineData(long.MinValue, "-8388608.0 TB")]
    public void ANegativeNumberKeepsItsSign(long bytes, string expected) => Assert.Equal(expected, ByteSize.Text(bytes));

    [Fact]
    public void TheTextIsTheSameInEveryCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(("1.5 MB", "0.5 of 1.5 MB"), (ByteSize.Text(3 * Mb / 2), ByteSize.Progress(Mb / 2, 3 * Mb / 2)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(0, 82 * Mb, "0.0 of 82.0 MB")]
    [InlineData(37 * Mb, (82 * Mb) + (4 * Mb / 10), "37.0 of 82.4 MB")]
    [InlineData(512 * Kb, 3 * Mb / 2, "0.5 of 1.5 MB")]
    [InlineData(12 * Kb, 412 * Kb, "12 of 412 KB")]
    [InlineData(100, 512, "100 of 512 bytes")]
    [InlineData(0, 1, "0 of 1 byte")]
    [InlineData(Gb / 2, 2 * Gb, "0.5 of 2.0 GB")]
    [InlineData(500_000, Mb - 1, "0.5 of 1.0 MB")] // the total reads "1.0 MB", so the part is in MB too
    public void ProgressReadsBothNumbersInTheUnitOfTheTotal(long done, long total, string expected) =>
        Assert.Equal(expected, ByteSize.Progress(done, total));

    [Theory]
    [InlineData(3 * Mb / 2, 0, "1.5 MB")]
    [InlineData(412 * Kb, -1, "412 KB")]
    public void ProgressWithoutATotalIsTheSizeAlone(long done, long total, string expected) =>
        Assert.Equal(expected, ByteSize.Progress(done, total));
}
