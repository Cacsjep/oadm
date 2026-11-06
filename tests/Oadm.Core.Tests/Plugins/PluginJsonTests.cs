using System.Text.Json;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// The one JSON helper of the plugins: the common options, what an empty text means per method, and one error rule
/// (an <see cref="ArgumentException"/> with a plain message that quotes nothing).
/// </summary>
public sealed class PluginJsonTests
{
    private static readonly JsonSerializerOptions Options = PluginJson.CreateOptions();

    private enum Mode
    {
        Off = 0,
        NotRunning = 1,
    }

    private sealed record Request(string? Name, int Port, Mode Mode, Guid? DeviceId);

    private sealed class Settings
    {
        public string Host { get; set; } = "pool.ntp.org";

        public List<int> Ports { get; set; } = [123];
    }

    [Fact]
    public void TheCommonOptionsWriteCamelCaseAndLeaveNullsOut()
    {
        Assert.Equal("""{"name":"gate","port":80,"mode":1}""", PluginJson.Serialize(new Request("gate", 80, Mode.NotRunning, null), Options));
    }

    [Fact]
    public void TheCommonOptionsReadNamesInAnyCaseAndNumbersFromStrings()
    {
        var request = PluginJson.Parse<Request>("""{"NAME":"gate","Port":"80","mode":1}""", Options);

        Assert.Equal(new Request("gate", 80, Mode.NotRunning, null), request);
    }

    [Theory]
    [InlineData(PluginJsonEnums.Numbers, "1")]
    [InlineData(PluginJsonEnums.Names, "\"NotRunning\"")]
    [InlineData(PluginJsonEnums.CamelCase, "\"notRunning\"")]
    public void EnumsAreWrittenAndReadAsThePluginChose(PluginJsonEnums enums, string written)
    {
        var options = PluginJson.CreateOptions(enums);

        var json = PluginJson.Serialize(new Request(null, 0, Mode.NotRunning, null), options);

        Assert.Equal($$"""{"port":0,"mode":{{written}}}""", json);
        Assert.Equal(Mode.NotRunning, PluginJson.Parse<Request>(json, options).Mode);
    }

    [Fact]
    public void NullsAreWrittenWhenThePluginAsksForThem()
    {
        var options = PluginJson.CreateOptions(PluginJsonEnums.CamelCase, writeNulls: true);

        Assert.Equal("""{"name":null,"port":0,"mode":"off","deviceId":null}""", PluginJson.Serialize(new Request(null, 0, Mode.Off, null), options));
    }

    [Fact]
    public void EveryCallGetsOptionsOfItsOwn()
    {
        // A plugin keeps its options in its own static: the serializer's memory of the plugin's types goes with the plugin.
        Assert.NotSame(PluginJson.CreateOptions(), PluginJson.CreateOptions());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n")]
    [InlineData("null")]
    public void ParseRefusesAnEmptyText(string? json)
    {
        var ex = Assert.Throws<ArgumentException>(() => PluginJson.Parse<Request>(json, Options));

        Assert.Equal("The request is empty.", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ParseOrEmptyReadsAnEmptyTextAsAnEmptyObject(string? json)
    {
        Assert.Equal(new Request(null, 0, Mode.Off, null), PluginJson.ParseOrEmpty<Request>(json, Options));

        var settings = PluginJson.ParseOrEmpty<Settings>(json, Options);
        var fresh = new Settings(); // a class keeps the defaults of a new one
        Assert.Equal(fresh.Host, settings.Host);
        Assert.Equal(fresh.Ports, settings.Ports);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"port":"eighty"}""")]
    [InlineData("""{"deviceId":"hunter2-is-no-guid"}""")]
    public void BrokenJsonIsAnArgumentExceptionWithAPlainMessage(string json)
    {
        foreach (var parse in new Func<Request>[] { () => PluginJson.Parse<Request>(json, Options), () => PluginJson.ParseOrEmpty<Request>(json, Options) })
        {
            var ex = Assert.Throws<ArgumentException>(parse);

            // The exact text the server sends as the refusal: no parameter name, no reader text, nothing of the payload.
            Assert.Equal("The request cannot be read.", ex.Message);
            Assert.Null(ex.InnerException);
            Assert.Null(ex.ParamName);
        }
    }

    [Fact]
    public void ATextThatCannotBeAnObjectIsUnreadableAlsoWhenItIsLeftOut()
    {
        // "{}" is no list: the caller's mistake reads the same as any other broken request.
        var ex = Assert.Throws<ArgumentException>(() => PluginJson.ParseOrEmpty<List<int>>(null, Options));

        Assert.Equal(PluginJson.UnreadableRequest, ex.Message);
        Assert.Equal(new List<int> { 1, 2 }, PluginJson.ParseOrEmpty<List<int>>("[1,2]", Options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"ports":"none"}""")]
    public void TryParseIsNullForMissingOrDamagedStoredText(string? json) =>
        Assert.Null(PluginJson.TryParse<Settings>(json, Options));

    [Fact]
    public void TryParseReadsStoredText()
    {
        var settings = PluginJson.TryParse<Settings>("""{"host":"10.0.0.1","ports":[123,1123]}""", Options);

        Assert.NotNull(settings);
        Assert.Equal("10.0.0.1", settings.Host);
        Assert.Equal(new List<int> { 123, 1123 }, settings.Ports);
    }

    [Fact]
    public void TheTextsAreTheOnesEveryPluginUses()
    {
        Assert.Equal(("The request is empty.", "The request cannot be read."), (PluginJson.EmptyRequest, PluginJson.UnreadableRequest));
    }
}
