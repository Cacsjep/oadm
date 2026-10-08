using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.DateAndTime.Tests;
using Oadm.Plugins.NtpServer.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>The contributed task against the stateful fake camera (recorded 10.0.0.48 answers), with the server on loopback.</summary>
public sealed class UseOadmNtpServerTaskTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 16, 0, 0, TimeSpan.Zero));
    private NtpServerPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new NtpServerPlugin(Options.Test());
        await _plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(NtpServerPluginInfo.PluginId), null), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    private UseOadmNtpServerTask TaskPlugin => (UseOadmNtpServerTask)Assert.Single(_plugin.TaskPlugins);

    private async Task EnableAsync() =>
        await _plugin.InvokeAsync(NtpServerMethods.Save, NtpJson.Serialize(new SaveRequest(true, "lo-id", null)), CancellationToken.None);

    private async Task<(RecordingContext Ctx, Exception? Error)> RunAsync(FakeTimeVapix vapix, string address = "127.0.0.1")
    {
        var ctx = new RecordingContext(vapix);
        Exception? error = null;
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => TaskPlugin.ExecuteAsync(ctx, new FakeDevice(Guid.NewGuid(), address) { Apis = vapix.ApiList }, null, CancellationToken.None));
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return (ctx, error);
    }

    [Fact]
    public void Task_is_a_maintenance_entry_for_devices_with_an_NTP_or_parameter_API()
    {
        Assert.Equal("oadm.ntp-server.use", TaskPlugin.Id);
        Assert.Equal("Use OADM as NTP server", TaskPlugin.DisplayName);
        Assert.Equal(TaskGroups.Maintenance, TaskPlugin.Group);
        Assert.False(TaskPlugin.RequiresDialog);
        Assert.True(TaskPlugin.CanRun(new FakeDevice(Guid.NewGuid())));
        Assert.True(TaskPlugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = Fixture.LegacyOnly }));
        Assert.False(TaskPlugin.CanRun(new FakeDevice(Guid.NewGuid()) { Apis = [] }));
        Assert.Equal("OADM has not read what this device supports yet: refresh the device", TaskPlugin.NotSupportedReason(new FakeDevice(Guid.NewGuid()) { Apis = [] }));
        Assert.Equal("Needs the NTP API (this device has 12.11.77)", TaskPlugin.NotSupportedReason(new FakeDevice(Guid.NewGuid()) { Apis = [new("basic-device-info", "1.3")] }));
        Assert.Equal("Use OADM NTP server", TaskPlugin.GetTaskName(null)); // not running
    }

    [Fact]
    public async Task Points_the_device_at_the_OADM_address_with_named_steps()
    {
        await EnableAsync();
        Assert.Equal("Use OADM NTP server 127.0.0.1", TaskPlugin.GetTaskName(null));
        var vapix = new FakeTimeVapix(_time);

        var (ctx, error) = await RunAsync(vapix);

        Assert.Null(error);
        Assert.Equal(
            ["Check compatibility: Done", "Read NTP settings: Done", "Set NTP server 127.0.0.1: Done", "Verify NTP settings: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("NTP servers 10.0.0.17", StepRun.Detail(ctx.Steps, "Read NTP settings"));
        var write = Assert.Single(vapix.Writes);
        Assert.Equal("setNTPClientConfiguration", write.Method);
        Assert.Equal("[\"127.0.0.1\"]", write.Params!["staticServers"]!.ToJsonString());
        Assert.Equal("static", write.Params["serversSource"]!.GetValue<string>());
        Assert.Equal("NTP servers 127.0.0.1", StepRun.Detail(ctx.Steps, "Verify NTP settings"));
    }

    [Fact]
    public async Task Already_using_the_OADM_server_changes_nothing()
    {
        await EnableAsync();
        var vapix = new FakeTimeVapix(_time);
        vapix.NtpClient["staticServers"] = new System.Text.Json.Nodes.JsonArray("127.0.0.1");

        var (ctx, error) = await RunAsync(vapix);

        Assert.Null(error);
        Assert.Empty(vapix.Writes);
        Assert.Equal("Already uses 127.0.0.1", StepRun.Detail(ctx.Steps, "Set NTP server 127.0.0.1"));
        Assert.Contains("Verify NTP settings: Skipped", StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task Legacy_devices_use_the_time_parameters()
    {
        await EnableAsync();
        var vapix = new FakeTimeVapix(_time) { ApiList = Fixture.LegacyOnly };

        var (ctx, error) = await RunAsync(vapix);

        Assert.Null(error);
        var write = Assert.Single(vapix.Writes);
        Assert.Contains("Time.NTP.Server=127.0.0.1", write.Body, StringComparison.Ordinal);
        Assert.Equal("Verify NTP settings: Done", StepRun.Lines(ctx.Steps)[3]);
    }

    [Fact]
    public async Task Fails_before_any_request_when_the_server_is_not_running()
    {
        var vapix = new FakeTimeVapix(_time);
        var (ctx, error) = await RunAsync(vapix);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("Check compatibility: Failed", StepRun.Lines(ctx.Steps)[0]);
        Assert.Contains("not running", StepRun.Detail(ctx.Steps, "Check compatibility"), StringComparison.Ordinal);
        Assert.Empty(vapix.Sent);
    }

    [Fact]
    public async Task A_device_that_ignores_the_write_ends_with_a_warning()
    {
        await EnableAsync();
        var vapix = new FakeTimeVapix(_time) { ApplyWrites = false };
        var (ctx, error) = await RunAsync(vapix);

        Assert.Null(error);
        Assert.Contains("Verify NTP settings: Warning", StepRun.Lines(ctx.Steps));
        Assert.Single(ctx.Warnings);
    }

    [Fact]
    public async Task Device_addresses_with_port_or_brackets_resolve()
    {
        Assert.Equal(IPAddress.Parse("10.0.0.48"), await UseOadmNtpServerTask.ResolveDeviceAddressAsync("10.0.0.48", CancellationToken.None));
        Assert.Equal(IPAddress.Parse("10.0.0.48"), await UseOadmNtpServerTask.ResolveDeviceAddressAsync("10.0.0.48:8443", CancellationToken.None));
        Assert.Equal(IPAddress.Parse("fd00::48"), await UseOadmNtpServerTask.ResolveDeviceAddressAsync("[fd00::48]:443", CancellationToken.None));
        Assert.Equal(IPAddress.Parse("fd00::48"), await UseOadmNtpServerTask.ResolveDeviceAddressAsync("fd00::48", CancellationToken.None));
    }

    [Fact]
    public async Task All_interfaces_uses_the_address_the_server_routes_to_the_device_with()
    {
        await _plugin.InvokeAsync(NtpServerMethods.Save, NtpJson.Serialize(new SaveRequest(true, "all", null)), CancellationToken.None);
        var (address, error) = _plugin.Service!.ServerAddressFor(IPAddress.Loopback);
        Assert.Null(error);
        Assert.Equal(IPAddress.Loopback, address);
        Assert.Equal("Use OADM NTP server", TaskPlugin.GetTaskName(null));
    }
}
