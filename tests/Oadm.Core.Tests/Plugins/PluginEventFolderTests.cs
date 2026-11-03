using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// A plugin's event folders reach every rule dialog: invalid ones fall back to the group. The audit text of a plugin call
/// falls back to the method when the plugin gives none or fails.
/// </summary>
public sealed class PluginEventFolderTests
{
    [Fact]
    public void InvalidFoldersFallBackToTheGroup()
    {
        var registry = new PluginRegistry();
        var source = new Source(
        [
            new("ok", "Fine", "Group", EventKind.Stateless) { Path = ["Devices", "Device triggers"] },
            new("blank", "Blank folder", "Group", EventKind.Stateless) { Path = ["Devices", " "] },
            new("null", "Null folder", "Group", EventKind.Stateless) { Path = ["Devices", null!] },
            new("deep", "Too deep", "Group", EventKind.Stateless) { Path = ["a", "b", "c", "d", "e"] },
            new("nogroup", "No group", "", EventKind.Stateless),
        ]);

        Assert.True(registry.RegisterCorePlugin(source, new PluginOrigin("test.events", "1.0.0", null)));

        var folders = registry.AllEventTypes.ToDictionary(e => e.Type.Id, e => e.Type.Folders);
        Assert.Equal(["Devices", "Device triggers"], folders["ok"]);
        Assert.Equal(["Group"], folders["blank"]);
        Assert.Equal(["Group"], folders["null"]);
        Assert.Equal(["Group"], folders["deep"]);
        Assert.Equal([PluginRegistry.OtherEventsFolder], folders["nogroup"]);
        Assert.Equal(3, registry.Errors.Count(e => e.Message.Contains("invalid folder path", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheAuditTextFallsBackToTheMethod()
    {
        var registry = new PluginRegistry();
        Assert.True(registry.RegisterCorePlugin(new Source([]) { Detail = (m, _) => m == "press" ? "Pressed Open gate on Gate house" : null }, new PluginOrigin("test.events", "1.0.0", null)));
        var throwing = new Source([], "test.throws") { Detail = (_, _) => throw new InvalidOperationException("broken") };
        Assert.True(registry.RegisterCorePlugin(throwing, new PluginOrigin("test.throws", "1.0.0", null)));
        var devices = new FakeDeviceRepository();
        var vapix = new FakeVapixClientFactory();
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix);
        await using var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider());

        Assert.Equal("Pressed Open gate on Gate house", host.AuditDetailOf("test.events", "press", "{}"));
        Assert.Equal("save", host.AuditDetailOf("test.events", "save", "{}")); // no detail: the method
        Assert.Equal("press", host.AuditDetailOf("test.throws", "press", "{}")); // the plugin failed: the method
        Assert.Equal("press", host.AuditDetailOf("not.registered", "press", null));
    }

    private sealed class Source(IReadOnlyList<EventTypeInfo> types, string id = "test.events") : ICorePlugin, IEventSource
    {
        public Func<string, string?, string?>? Detail { get; init; }

        public string Id => id;

        public string DisplayName => "Test events";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public IReadOnlyList<EventTypeInfo> EventTypes => types;

        public string? AuditDetail(string method, string? payloadJson) => Detail?.Invoke(method, payloadJson);

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
