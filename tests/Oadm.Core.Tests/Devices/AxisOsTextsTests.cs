using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Devices;

/// <summary>The one name of the device firmware, and "firmware" (the ADM word) as a search alias for it.</summary>
public sealed class AxisOsTextsTests
{
    [Theory]
    [InlineData("Upgrade AXIS OS", "firmware")]
    [InlineData("Upgrade AXIS OS", "Firmware")]
    [InlineData("Upgrade AXIS OS", "upgrade firmware")]
    [InlineData("AXIS OS", "firm")]
    [InlineData("Upgrade AXIS OS", "axis os")]
    [InlineData("Upgrade AXIS OS", "upgrade")]
    [InlineData("Firmware management API", "firmware")]
    public void ANameIsFoundByItsTextAndByFirmware(string text, string term) =>
        Assert.True(AxisOsTexts.Contains(text, term));

    [Theory]
    [InlineData("Upgrade AXIS OS", "downgrade firmware")]
    [InlineData("Remove users", "firmware")]
    [InlineData("Upgrade AXIS OS", "fir")]
    [InlineData(null, "firmware")]
    [InlineData("", "firmware")]
    public void OtherNamesAreNotFound(string? text, string term) =>
        Assert.False(AxisOsTexts.Contains(text, term));

    [Fact]
    public void TheVersionTextNamesAxisOs() => Assert.Equal("AXIS OS 12.11.77", AxisOsTexts.Version("12.11.77"));
}
