using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Events;

public sealed class ActionPlaceholderTests
{
    [Fact]
    public void FillsInOnePassAndKeepsUnknownNames()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "{b}", ["b"] = "B" };

        Assert.Equal("{b} B {unknown} {", ActionPlaceholders.Fill("{a} {B} {unknown} {", values.GetValueOrDefault));
    }

    [Theory]
    [InlineData(PlaceholderEscape.None, "x \"q\" <a&b> é")]
    [InlineData(PlaceholderEscape.Json, "x \\\"q\\\" <a&b> é")]
    [InlineData(PlaceholderEscape.Xml, "x &quot;q&quot; &lt;a&amp;b&gt; é")]
    [InlineData(PlaceholderEscape.Url, "x%20%22q%22%20%3Ca%26b%3E%20%C3%A9")]
    public void EscapesOnlyTheValues(PlaceholderEscape escape, string expected)
    {
        Assert.Equal("[" + expected + "]", ActionPlaceholders.Fill("[{v}]", _ => "x \"q\" <a&b> é", escape));
    }

    [Fact]
    public void EveryFixedPlaceholderHasAValueAndEventValuesComeAfterThem()
    {
        var device = new Device();
        var trigger = new ActionTrigger("t", "Person", device.Id, device.Address, device.Model, false,
            new DateTimeOffset(2026, 10, 11, 12, 5, 9, TimeSpan.Zero), new Dictionary<string, string> { ["objectId"] = "7", ["serial"] = "event value" });

        var values = Placeholders.Values("Gate", trigger, device);

        Assert.All(ActionPlaceholders.Fixed, p => Assert.True(values.ContainsKey(p.Name), p.Name));
        Assert.Equal("B8:A4:4F:63:13:39", values["mac"]);
        Assert.Equal("B8A44F631339", values["serial"]); // the fixed name wins
        Assert.Equal("7", values["objectId"]);
        Assert.Equal("https://10.0.0.48/", values["webUrl"]);
        Assert.Equal("Building A; Gate", values["tags"]);
        Assert.Equal("I/O module", values["category"]);
        Assert.Equal("off", values["state"]);
        Assert.Equal("2026-10-11T12:05:09Z", values["timeUtc"]);
        Assert.Equal("OK", values["status"]);
    }

    private sealed class Device : IDeviceInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Serial => "B8A44F631339";
        public string Address => "10.0.0.48";
        public string? HostName => "io-gate";
        public string? Model => "AXIS A9210";
        public string? FirmwareVersion => "12.10.73";
        public DeviceStatus Status => DeviceStatus.Ok;
        public DeviceCategory Category => DeviceCategory.IoModule;
        public bool HasVideo => false;
        public IReadOnlyList<DeviceApi> Apis => [];
        public IReadOnlyList<string> Tags => ["Building A", "Gate"];
    }
}
