using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>What the event Text read declares for Dashboards (docs/spec/ocr.md "Events and the rule action").</summary>
public sealed class TextReadEventTypeTests
{
    [Fact]
    public void TextReadBelongsToTheReadersCameraAndIsSplitByArea()
    {
        var camera = Guid.NewGuid();
        var type = TextReadEvents.TypeOf(new Reader
        {
            Id = "r1",
            Name = "Gate 2",
            DeviceId = camera,
            Areas = [new Area { Name = "Container number" }, new Area { Name = "All" }],
        });

        Assert.Equal(camera, type.FixedDeviceId);
        Assert.Equal([camera], type.DeviceIds);
        Assert.Equal(OcrInfo.AreaField, type.SplitField);
        var area = Assert.Single(type.DataFields, f => f.Name == type.SplitField);
        Assert.Equal(EventFieldType.Choice, area.Type);
        Assert.Equal(["Container number", "All"], area.Values);
    }
}
