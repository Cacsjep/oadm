using Microsoft.Extensions.Logging;

using Oadm.Core.Plugins;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Plugins;

/// <summary>Display name rules (no trailing ellipsis, at most 32 characters) and context menu groups.</summary>
public sealed class TaskPluginNamesTests
{
    [Theory]
    [InlineData("Restart", "Restart")]
    [InlineData("Users...", "Users")]
    [InlineData("Users......", "Users")]
    [InlineData("Network settings…", "Network settings")]
    [InlineData("  Upgrade firmware ... ", "Upgrade firmware")]
    [InlineData("Wait... for it", "Wait... for it")]
    [InlineData(null, "")]
    public void TrailingEllipsesAreStripped(string? name, string expected) =>
        Assert.Equal(expected, TaskPluginNames.StripEllipsis(name));

    [Fact]
    public void LongNamesAreShortenedWithAnEllipsis()
    {
        var exact = new string('a', TaskPluginNames.MaxDisplayNameLength);
        Assert.Equal(exact, TaskPluginNames.Normalize(exact));

        var shown = TaskPluginNames.Normalize("Deploy certificates from the internal CA to all selected devices...");
        Assert.Equal(TaskPluginNames.MaxDisplayNameLength, shown.Length);
        Assert.Equal("Deploy certificates from the in…", shown);
    }

    [Theory]
    [InlineData(null, "General")]
    [InlineData("", "General")]
    [InlineData("  ", "General")]
    [InlineData(" Network ", "Network")]
    [InlineData("Custom tools", "Custom tools")]
    public void EmptyGroupsAreGeneral(string? group, string expected) =>
        Assert.Equal(expected, TaskPluginNames.NormalizeGroup(group));

    [Fact]
    public void RegistryNormalizesNamesAndGroupsAndWarns()
    {
        var logger = new ListLogger<PluginRegistry>();
        var registry = new PluginRegistry(logger);
        var origin = new PluginOrigin("test", "1.0.0", null);

        Assert.True(registry.RegisterTaskPlugin(new NamedPlugin("x.long", "Deploy certificates from the internal CA to all selected devices", null), origin));
        Assert.True(registry.RegisterTaskPlugin(new NamedPlugin("x.dots", "Users...", "Users"), origin));
        Assert.True(registry.RegisterTaskPlugin(new DefaultGroupPlugin(), origin));

        Assert.True(registry.TryGetTaskPlugin("x.long", out var longOne));
        Assert.Equal("Deploy certificates from the in…", longOne.DisplayName);
        Assert.Equal(TaskGroups.General, longOne.Group);
        Assert.True(registry.TryGetTaskPlugin("x.dots", out var dots));
        Assert.Equal("Users", dots.DisplayName);
        Assert.Equal(TaskGroups.Users, dots.Group);
        Assert.True(registry.TryGetTaskPlugin("x.default", out var plain));
        Assert.Equal(TaskGroups.General, plain.Group); // the interface default

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("x.long", StringComparison.Ordinal) && e.Message.Contains("longer than 32", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("x.dots", StringComparison.Ordinal) && e.Message.Contains("ellipsis", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("x.default", StringComparison.Ordinal));
    }

    /// <summary>Every task plugin deployed for development (artifacts/plugins) complies, also ones added later.</summary>
    [Fact]
    public void DeployedTaskPluginsFollowTheNameRulesAndUseTheirGroups()
    {
        var root = PluginPaths.Development(AppContext.BaseDirectory);
        Assert.NotNull(root);
        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);
        try
        {
            loader.LoadFromRoots([root]);
            Assert.NotEmpty(registry.TaskPlugins);
            foreach (var registered in registry.TaskPlugins)
            {
                var raw = registered.Plugin.DisplayName;
                Assert.True(raw.Length <= TaskPluginNames.MaxDisplayNameLength, $"{registered.Id}: '{raw}' is too long");
                Assert.Equal(raw, TaskPluginNames.Normalize(raw)); // no trailing "..."
                Assert.False(string.IsNullOrWhiteSpace(registered.Plugin.Group), registered.Id);
            }

            var expected = new Dictionary<string, string>
            {
                ["oadm.restart"] = TaskGroups.Maintenance,
                ["oadm.firmware"] = TaskGroups.Maintenance,
                ["oadm.acap"] = TaskGroups.Applications,
                ["oadm.users"] = TaskGroups.Users,
                ["oadm.network"] = TaskGroups.Network,
                ["oadm.network.assign-ip"] = TaskGroups.Network,
            };
            foreach (var (id, group) in expected)
            {
                if (registry.TryGetTaskPlugin(id, out var plugin))
                {
                    Assert.Equal(group, plugin.Group);
                }
            }

            Assert.True(registry.TryGetTaskPlugin("oadm.restart", out var restart));
            Assert.Equal(TaskGroups.Maintenance, restart.Group);
        }
        finally
        {
            foreach (var package in loader.Packages)
            {
                package.LoadContext.Unload();
            }
        }
    }

    private sealed class NamedPlugin(string id, string name, string? group) : ITaskPlugin
    {
        public string Id => id;
        public string DisplayName => name;
        public string? IconKey => null;
        public string Group => group!;
        public bool ShowInToolbar => false;
        public bool RequiresDialog => false;
        public bool CanRun(IDeviceInfo device) => true;
        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DefaultGroupPlugin : ITaskPlugin
    {
        public string Id => "x.default";
        public string DisplayName => "Identify";
        public string? IconKey => null;
        public bool ShowInToolbar => false;
        public bool RequiresDialog => false;
        public bool CanRun(IDeviceInfo device) => true;
        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
