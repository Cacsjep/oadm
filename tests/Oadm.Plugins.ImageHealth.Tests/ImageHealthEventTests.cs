namespace Oadm.Plugins.ImageHealth.Tests;

/// <summary>"Monitor continuously" and the event rule events of the Image Health Dashboard.</summary>
public sealed class ImageHealthEventTests
{
    [Fact]
    public async Task Monitoring_is_off_by_default_and_then_every_event_is_reported_off()
    {
        await using var rig = await new Rig().StartAsync();

        Assert.False(rig.Plugin.IsMonitoring);
        Assert.Equal(ImageHealthEvents.Types.Count, rig.Publisher.Reports.Count);
        Assert.All(rig.Publisher.Reports, r => Assert.Empty(r.On));
        await Task.Delay(100);
        Assert.Equal(0, rig.Vapix.Requests); // nothing in the background
    }

    [Fact]
    public async Task Monitoring_checks_in_the_background_and_reports_detections_per_camera()
    {
        var blurred = new TestDevice("10.0.0.52", "Q3548-LVE");
        var stopped = new TestDevice("10.0.0.53", "Q3548-LVE");
        var fine = new TestDevice("10.0.0.54", "Q3548-LVE");
        await using var rig = await new Rig(new Monitoring.ImageHealthOptions { PublishInterval = TimeSpan.FromMilliseconds(20), MonitorInterval = TimeSpan.FromMilliseconds(100) }).StartAsync();
        rig.Devices.Items.AddRange([blurred, stopped, fine]);
        rig.Vapix.Answers[blurred.Id] = () => FakeVapix.Json(Recorded.Detected); // blur + block detected, redirect pending
        rig.Vapix.Answers[stopped.Id] = () => FakeVapix.Status(System.Net.HttpStatusCode.ServiceUnavailable);
        rig.Vapix.Answers[fine.Id] = () => FakeVapix.Json(Recorded.Normal);

        var state = ImageHealthJson.Deserialize<ImageHealthState>((await rig.Plugin.InvokeAsync(ImageHealthMethods.SetMonitoring, ImageHealthJson.Serialize(new MonitoringRequest(true)), CancellationToken.None))!);
        Assert.True(state.Monitoring);
        await Wait.UntilAsync(() => rig.Publisher.Last(ImageHealthEvents.Blur)?.Count == 1);

        Assert.Equal([blurred.Id], rig.Publisher.Last(ImageHealthEvents.Blur));
        Assert.Equal([blurred.Id], rig.Publisher.Last(ImageHealthEvents.Block));
        Assert.Empty(rig.Publisher.Last(ImageHealthEvents.Redirect)!); // pending is not on
        Assert.Equal([stopped.Id], rig.Publisher.Last(ImageHealthEvents.NotRunning));
        var requests = rig.Vapix.Requests;
        await Wait.UntilAsync(() => rig.Vapix.Requests >= requests + 3); // checks again on its own
        Assert.Contains("true", (await rig.Settings.GetAsync(ImageHealthPlugin.ConfigKey, CancellationToken.None))!, StringComparison.Ordinal);

        // Off: no more checks, every event off.
        await rig.Plugin.InvokeAsync(ImageHealthMethods.SetMonitoring, ImageHealthJson.Serialize(new MonitoringRequest(false)), CancellationToken.None);
        Assert.Empty(rig.Publisher.Last(ImageHealthEvents.Blur)!);
        var after = rig.Vapix.Requests;
        await Task.Delay(300);
        Assert.Equal(after, rig.Vapix.Requests);
    }

    [Fact]
    public async Task The_setting_survives_a_restart_and_only_administrators_change_it()
    {
        var rig = await new Rig().StartAsync();
        await rig.Plugin.InvokeAsync(ImageHealthMethods.SetMonitoring, ImageHealthJson.Serialize(new MonitoringRequest(true)), CancellationToken.None);
        await rig.Plugin.StopAsync(CancellationToken.None);

        await rig.Plugin.StartAsync(new FakeContext(rig.Devices, rig.Vapix, rig.Hub.For(ImageHealthPluginInfo.PluginId), rig.Settings, rig.Publisher), CancellationToken.None);
        Assert.True(rig.Plugin.IsMonitoring);
        Assert.Equal(Oadm.Sdk.Plugins.UserRole.Admin, rig.Plugin.RequiredRole(ImageHealthMethods.SetMonitoring));
        Assert.Equal(Oadm.Sdk.Plugins.UserRole.Operator, rig.Plugin.RequiredRole(ImageHealthMethods.Check));
        await rig.DisposeAsync();
    }
}
