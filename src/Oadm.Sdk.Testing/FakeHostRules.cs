using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The server's rules for a user's call of a plugin method, for <see cref="FakeCoreContext"/>: the methods only users
/// with all devices may call, the refusal of the context's full accesses in a group user's call (kept by work the call
/// starts, like on the server), and the audiences of published events.
/// </summary>
internal static class FakeHostRules
{
    private static readonly AsyncLocal<Mark?> Current = new();

    /// <summary>A call in progress: the user has all devices; the method may use <c>ctx.System</c>.</summary>
    internal sealed record Mark(bool HasAllDevices, bool SystemAllowed);

    public static IDisposable Use(Mark mark)
    {
        var previous = Current.Value;
        Current.Value = mark;
        return new Scope(previous);
    }

    /// <summary>True when the current call is a group user's (and, for <paramref name="system"/>, its method declared no reason).</summary>
    public static bool Refuses(bool system) => Current.Value is { HasAllDevices: false } mark && !(system && mark.SystemAllowed);

    public static async Task<T> CallAsync<T>(ICorePlugin plugin, ICallContext call, string method, Func<Task<T>> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var systemAllowed = false;
        if (!call.HasAllDevices)
        {
            if (plugin.RequiresAllDevices(method))
            {
                throw new DeviceAccessRefusedException();
            }

            systemAllowed = !string.IsNullOrWhiteSpace(plugin.SystemAccessReason(method));
        }

        using (Use(new Mark(call.HasAllDevices, systemAllowed)))
        {
            return await run().ConfigureAwait(false);
        }
    }

    /// <summary>The event as <paramref name="watcher"/> receives it, or null (see <see cref="FakeCoreContext.EventsSeenBy"/>).</summary>
    public static PluginEvent? Deliver(FakeCoreContext core, FakePublishedEvent published, ICallContext watcher)
    {
        var audience = published.Audience;
        var item = new PluginEvent(published.Topic, published.PayloadJson);
        bool Sees(Guid id) => core.DeviceList.Find(d => d.Id == id) is { } device ? watcher.Contains(device) : watcher.HasAllDevices;
        switch (audience.Kind)
        {
            case PluginEventAudienceKind.AllDevices:
                return watcher.HasAllDevices ? item : null;
            case PluginEventAudienceKind.Device:
                return watcher.HasAllDevices || Sees(audience.DeviceIds[0]) ? item : null;
            case PluginEventAudienceKind.User:
                return string.Equals(audience.UserName, watcher.UserName, StringComparison.OrdinalIgnoreCase) ? item : null;
            case PluginEventAudienceKind.Session:
                return string.Equals(audience.SessionId, watcher.SessionId, StringComparison.Ordinal) ? item : null;
            case PluginEventAudienceKind.DeviceScope:
                return string.Equals(audience.ScopeKey, watcher.DeviceScopeKey, StringComparison.OrdinalIgnoreCase) ? item : null;
            case PluginEventAudienceKind.DeviceRows:
                if (watcher.HasAllDevices)
                {
                    return item;
                }

                JsonArray? rows = null;
                try
                {
                    rows = published.PayloadJson is null ? null : JsonNode.Parse(published.PayloadJson) as JsonArray;
                }
                catch (JsonException)
                {
                }

                if (rows is null || rows.Count != audience.DeviceIds.Count)
                {
                    return null; // the server drops it for group users (and logs it)
                }

                var kept = new JsonArray();
                for (var i = 0; i < rows.Count; i++)
                {
                    if (Sees(audience.DeviceIds[i]))
                    {
                        kept.Add(rows[i]?.DeepClone());
                    }
                }

                return kept.Count == 0 ? null : item with { PayloadJson = kept.ToJsonString() };
            default:
                return null;
        }
    }

    private sealed class Scope(Mark? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
