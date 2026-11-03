using Oadm.Plugins.Ocr.Api;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Plugins): readers belong to the device scope that made them; no method reaches
/// a camera outside the caller's group.
/// </summary>
public sealed class DeviceGroupTests
{
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.2" });
        await using var plugin = new OcrPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);

        // Skeleton (wave 1): the readers come with package O4, which turns SaveReader (it takes the camera's id) into a
        // .Method case and the reader-id methods into checks of the caller's own scope.
        await new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices(OcrMethods.ListReaders, "the readers of the caller's own device scope")
            .WithoutDevices(OcrMethods.GetReader, "a reader of the caller's own device scope", """{"readerId":"r1"}""")
            .WithoutDevices(OcrMethods.SaveReader, "not built yet", """{"reader":{"name":"Gate","deviceId":"{device}"}}""")
            .WithoutDevices(OcrMethods.DeleteReader, "a reader of the caller's own device scope", """{"readerId":"r1"}""")
            .WithoutDevices(OcrMethods.ReadNow, "a reader of the caller's own device scope", """{"readerId":"r1"}""")
            .WithoutDevices(OcrMethods.GetReading, "a reader of the caller's own device scope", """{"readerId":"r1"}""")
            .RunAsync();
    }
}
