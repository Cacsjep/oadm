using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// The plugin test kit of SDK 0.4.0 (<see cref="FakeCoreContext"/> with the host's call rules, <see cref="PluginCanary"/>):
/// a converted plugin passes the canary, a plugin that uses the context's full access, leaks a job of another user or
/// answers an outside device differently from an unknown one fails it with the method named.
/// </summary>
public sealed class PluginCanaryTests
{
    [Fact]
    public async Task AConvertedPluginPassesTheCanary()
    {
        var (ctx, plugin, inside, outside) = await StartAsync(new Converted());
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method("read", """{"deviceIds":["{inside}","{device}"]}""")
            .Download("export", """{"deviceId":"{device}"}""")
            .Job("report.status", """{"id":"{job}"}""", async owner => (await ctx.InvokeAsync(plugin, owner, "report.start", null))!)
            .WithoutDevices("settings", "server settings, no device data")
            .AllDevicesOnly("save")
            .RunAsync();
    }

    [Fact]
    public async Task APluginThatUsesTheContextInAGroupUsersCallFailsTheCanary()
    {
        var (ctx, plugin, inside, outside) = await StartAsync(new Unconverted());
        var failed = await Assert.ThrowsAsync<PluginCanaryException>(() => new PluginCanary(ctx, plugin, inside, outside)
            .Method("read", """{"deviceId":"{device}"}""")
            .WithoutDevices("count", "a count")
            .AllDevicesOnly("save")
            .Job("report.status", """{"id":"{job}"}""", _ => Task.FromResult("leaked"))
            .RunAsync());

        Assert.Contains(failed.Failures, f => f.StartsWith("read: ", StringComparison.Ordinal) && f.Contains("Vapix", StringComparison.Ordinal));
        Assert.Contains(failed.Failures, f => f.StartsWith("count: ", StringComparison.Ordinal) && f.Contains("Devices", StringComparison.Ordinal));
        Assert.Contains(failed.Failures, f => f.StartsWith("save: RequiresAllDevices", StringComparison.Ordinal));
        Assert.Contains(failed.Failures, f => f.StartsWith("report.status: ", StringComparison.Ordinal));
        Assert.Equal(2, (await plugin.Context!.Devices.ListAsync(CancellationToken.None)).Count); // outside a call the context works
    }

    [Fact]
    public async Task TheFakeContextAppliesTheHostsRules()
    {
        var (ctx, plugin, inside, outside) = await StartAsync(new Converted());
        var group = ctx.CallAsGroup([inside.Id]);
        var all = ctx.CallAsAllDevices();

        // System access only in the method that declares a reason; ctx.Devices never in a group call.
        Assert.Equal("2", await ctx.InvokeAsync(plugin, group, "system.count", null));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ctx.InvokeAsync(plugin, group, "ctx.count", null));
        Assert.Equal("2", await ctx.InvokeAsync(plugin, all, "ctx.count", null));
        Assert.Contains("Devices", ctx.RefusedAccesses);

        // Events as each watcher receives them.
        await ctx.InvokeAsync(plugin, all, "publish", null);
        Assert.Equal(["old", "rows", "mine"], ctx.EventsSeenBy(all).Select(e => e.Topic));
        var seen = ctx.EventsSeenBy(group);
        Assert.Equal(["rows"], seen.Select(e => e.Topic));
        Assert.Equal($"[\"{inside.Id}\"]", seen[0].PayloadJson);
        _ = outside;
    }

    private static async Task<(FakeCoreContext Ctx, ProbeBase Plugin, IDeviceInfo Inside, IDeviceInfo Outside)> StartAsync(ProbeBase plugin)
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.2" });
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (ctx, plugin, inside, outside);
    }

    private abstract class ProbeBase : ICorePlugin
    {
        public string Id => "test.canary";

        public string DisplayName => "Canary";

        public string IconKey => "plugin";

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public ICorePluginContext? Context { get; private set; }

        public Task StartAsync(ICorePluginContext context, CancellationToken ct)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        protected static IReadOnlyList<Guid> Ids(string? payload)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload!);
            var root = doc.RootElement;
            return root.TryGetProperty("deviceIds", out var many)
                ? [.. many.EnumerateArray().Select(e => Guid.Parse(e.GetString()!))]
                : [Guid.Parse(root.GetProperty("deviceId").GetString()!)];
        }
    }

    /// <summary>Uses the call for every device, finds jobs through the registry, declares its all-devices method.</summary>
    private sealed class Converted : ProbeBase, ICorePlugin, ICorePluginDownloads
    {
        public bool RequiresAllDevices(string method) => method == "save";

        public string? SystemAccessReason(string method) => method == "system.count" ? "a count, no device data" : null;

        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            var ctx = Context!;
            switch (method)
            {
                case "read":
                    foreach (var id in Ids(payloadJson))
                    {
                        _ = await caller.Devices.FindAsync(id, ct) ?? throw new DevicesNotAvailableException();
                    }

                    foreach (var id in Ids(payloadJson))
                    {
                        await (await caller.Vapix.CreateAsync(id, ct)).GetBasicDeviceInfoAsync(ct);
                    }

                    return "ok";
                case "report.start":
                    return ctx.Jobs!.Start(caller, "report", (_, _) => Task.CompletedTask).Id;
                case "report.status":
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(payloadJson!);
                        return ctx.Jobs!.Find(caller, doc.RootElement.GetProperty("id").GetString()!) is null
                            ? throw new KeyNotFoundException("The report is no longer available.")
                            : "done";
                    }

                case "settings":
                    return await ctx.Settings.GetAsync("x", ct);
                case "save":
                    await ctx.Settings.SetAsync("x", "1", ct);
                    return null;
                case "system.count":
                    return (await ctx.System.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "ctx.count":
                    return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "publish":
                    var devices = await caller.Devices.ListAsync(ct);
                    ctx.Events!.Publish("old", null);
                    ctx.Events.Publish("rows", "[" + string.Join(",", devices.Select(d => $"\"{d.Id}\"")) + "]", PluginEventAudience.DeviceRows([.. devices.Select(d => d.Id)]));
                    ctx.Events.Publish("mine", null, PluginEventAudience.Of(caller));
                    return null;
                default:
                    throw new ArgumentException("Unknown method.");
            }
        }

        public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        public async Task<PluginDownload> OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            var id = Ids(payloadJson)[0];
            await (await caller.Vapix.CreateAsync(id, ct)).GetBasicDeviceInfoAsync(ct);
            return new PluginDownload(new MemoryStream([1, 2, 3]), "file.bin", 3);
        }
    }

    /// <summary>A method nobody converted: the context's full accesses, a job id of anyone, no declaration.</summary>
    private sealed class Unconverted : ProbeBase, ICorePlugin
    {
        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            var ctx = Context!;
            switch (method)
            {
                case "read":
                    await (await ctx.Vapix.CreateAsync(Ids(payloadJson)[0], ct)).GetBasicDeviceInfoAsync(ct);
                    return "ok";
                case "count":
                    return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "save":
                    await ctx.Settings.SetAsync("x", "1", ct);
                    return null;
                case "report.status":
                    return "done"; // answers every id
                default:
                    throw new ArgumentException("Unknown method.");
            }
        }
    }
}
