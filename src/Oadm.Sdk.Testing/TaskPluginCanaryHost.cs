using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The core plugin of the canary (<see cref="PluginCanary"/>) for plugins without a page: task plugins and event rule
/// actions (SDK 0.4.0). It starts their work the way the host does for a user: <see cref="Run"/> starts a task through the
/// call (<c>{"pluginId":"..","deviceIds":[..],"payload":".."}</c>), <see cref="Action"/> runs an action on one device of
/// the call (<c>{"actionId":"..","deviceId":"..","settings":{..}}</c>) with the call's client. Queries of the task plugins
/// are canary cases of their own (<see cref="PluginCanary.Query"/>).
/// </summary>
/// <param name="taskPlugins">The task plugins <see cref="Run"/> may start.</param>
/// <param name="actions">The actions <see cref="Action"/> may run.</param>
public sealed class TaskPluginCanaryHost(IReadOnlyList<ITaskPlugin> taskPlugins, IReadOnlyList<IActionPlugin>? actions = null) : ICorePlugin
{
    /// <summary>The method that starts a task through the call.</summary>
    public const string Run = "run";

    /// <summary>The method that runs an action on one device of the call.</summary>
    public const string Action = "action";

    /// <inheritdoc />
    public string Id => "canary.host";

    /// <inheritdoc />
    public string DisplayName => "Canary host";

    /// <inheritdoc />
    public string? IconKey => null;

    /// <inheritdoc />
    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = taskPlugins ?? throw new ArgumentNullException(nameof(taskPlugins));

    /// <inheritdoc />
    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("The canary calls with a user.");

    /// <inheritdoc />
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        using var doc = JsonDocument.Parse(payloadJson ?? "{}");
        var root = doc.RootElement;
        switch (method)
        {
            case Run:
            {
                var pluginId = root.GetProperty("pluginId").GetString()!;
                if (!TaskPlugins.Any(p => p.Id == pluginId))
                {
                    throw new ArgumentException($"Unknown task plugin '{pluginId}'.");
                }

                var ids = root.GetProperty("deviceIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
                var payload = root.TryGetProperty("payload", out var p) ? p.GetString() : null;
                var tasks = await caller.Tasks.RunAsync(pluginId, ids, payload, caller.UserName, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(tasks);
            }

            case Action:
            {
                var actionId = root.GetProperty("actionId").GetString()!;
                var action = actions?.FirstOrDefault(a => a.Id == actionId) ?? throw new ArgumentException($"Unknown action '{actionId}'.");
                var device = await caller.Devices.FindAsync(Guid.Parse(root.GetProperty("deviceId").GetString()!), ct).ConfigureAwait(false)
                    ?? throw new DevicesNotAvailableException();
                var vapix = await caller.Vapix.CreateAsync(device.Id, ct).ConfigureAwait(false);
                var context = new FakeActionContext(vapix);
                var trigger = new ActionTrigger("canary", "Canary", device.Id, device.Address, device.Model, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>());
                using var empty = JsonDocument.Parse("{}");
                var settings = root.TryGetProperty("settings", out var s) ? s.Clone() : empty.RootElement.Clone();
                await action.ExecuteAsync(context, new ActionInvocation(Guid.NewGuid(), "Canary", ActionEdge.Fired, settings, device, trigger), ct).ConfigureAwait(false);
                return null;
            }

            default:
                throw new ArgumentException($"Unknown method '{method}'.");
        }
    }
}
