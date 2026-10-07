using Microsoft.Extensions.Logging;

using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Devices;

public sealed class DeviceCategoryMapperTests
{
    [Theory]
    [InlineData("Dome Camera", DeviceCategory.Camera)] // AXIS P3265-V, AXIS OS 12.11 (verified)
    [InlineData("Network Camera", DeviceCategory.Camera)]
    [InlineData("PTZ Network Camera", DeviceCategory.Camera)]
    [InlineData("Bullet Camera", DeviceCategory.Camera)]
    [InlineData("Thermal Network Camera", DeviceCategory.Camera)]
    [InlineData("Radar-Video Fusion Camera", DeviceCategory.Camera)]
    [InlineData("Network Video Encoder", DeviceCategory.Encoder)]
    [InlineData("Video Encoder", DeviceCategory.Encoder)]
    [InlineData("Network Speaker", DeviceCategory.Speaker)]
    [InlineData("Network Horn Speaker", DeviceCategory.Speaker)]
    [InlineData("Network Audio Bridge", DeviceCategory.Audio)]
    [InlineData("Network Microphone", DeviceCategory.Audio)]
    [InlineData("Network Intercom", DeviceCategory.Intercom)]
    [InlineData("Network Video Door Station", DeviceCategory.Intercom)]
    [InlineData("Security Radar", DeviceCategory.Radar)]
    [InlineData("Radar", DeviceCategory.Radar)]
    [InlineData("Network I/O Module", DeviceCategory.IoModule)]
    [InlineData("Network I/O Relay Module", DeviceCategory.IoModule)]
    [InlineData("Network Door Controller", DeviceCategory.DoorController)]
    [InlineData("network door controller", DeviceCategory.DoorController)]
    [InlineData("Network Strobe Siren", DeviceCategory.Other)]
    [InlineData("Something New", DeviceCategory.Other)]
    [InlineData("", DeviceCategory.Unknown)]
    [InlineData("   ", DeviceCategory.Unknown)]
    [InlineData(null, DeviceCategory.Unknown)]
    public void MapsProductTypes(string? productType, DeviceCategory expected)
    {
        Assert.Equal(expected, DeviceCategoryMapper.Map(productType));
    }

    [Fact]
    public void UnknownProductTypeIsLoggedOnce()
    {
        var logger = new CountingLogger();
        var unique = "Gizmo " + Guid.NewGuid().ToString("N");

        Assert.Equal(DeviceCategory.Other, DeviceCategoryMapper.Map(unique, logger));
        Assert.Equal(DeviceCategory.Other, DeviceCategoryMapper.Map(unique, logger));
        Assert.Equal(DeviceCategory.Camera, DeviceCategoryMapper.Map("Dome Camera", logger));

        Assert.Equal(1, logger.Count);
    }

    [Theory]
    [InlineData(DeviceCategory.Camera, true)]
    [InlineData(DeviceCategory.Encoder, true)]
    [InlineData(DeviceCategory.Intercom, true)]
    [InlineData(DeviceCategory.Speaker, false)]
    [InlineData(DeviceCategory.Radar, false)]
    [InlineData(DeviceCategory.IoModule, false)]
    [InlineData(DeviceCategory.Unknown, false)]
    public void HasVideoFollowsTheCategory(DeviceCategory category, bool expected)
    {
        Assert.Equal(expected, DeviceCategories.HasVideo(category));
        Assert.Equal(expected, new Device { Category = category }.HasVideo);
    }

    private sealed class CountingLogger : ILogger
    {
        public int Count { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Count++;
    }
}
