using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Shared;

/// <summary>
/// The call of the old <c>InvokeAsync(method, payload, ct)</c> overload (SDK 0.4.0): the host uses it only for in-process work
/// without a user (and tests), never for a user's call (that one always comes with its <see cref="ICallContext"/>). It
/// reaches devices through the context's own members and owns objects for all devices, like a plugin before device groups.
/// </summary>
internal sealed class ContextCall(ICorePluginContext ctx) : ICallContext
{
    public string UserName => "OADM";

    public string SessionId => "oadm";

    public string DeviceScopeKey => ICallContext.AllDevicesScopeKey;

    public bool HasAllDevices => true;

    public IDeviceRepository Devices => ctx.Devices;

    public IVapixClientFactory Vapix => ctx.Vapix;

    public ITaskRunner Tasks => ctx.Tasks;

    public IDeviceEventStreams? EventStreams => ctx.EventStreams;

    public bool Has(string permission) => true;

    public bool Contains(IDeviceInfo device) => true;
}

/// <summary>Device id checks of the converted page methods (SDK 0.4.0).</summary>
internal static class CallDevices
{
    /// <summary>
    /// The devices of <paramref name="ids"/> in the caller's view, in order and without duplicates; any id outside the view
    /// or unknown refuses the whole request (<see cref="DevicesNotAvailableException"/>, the same answer for both). One list
    /// read for many ids (30,000 ids: one pass), one lookup for a few.
    /// </summary>
    public static async Task<IReadOnlyList<IDeviceInfo>> RequireAsync(ICallContext caller, IEnumerable<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var found = new List<IDeviceInfo>(wanted.Count);
        var missing = 0;
        if (wanted.Count <= 8)
        {
            foreach (var id in wanted)
            {
                if (await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) is { } device)
                {
                    found.Add(device);
                }
                else
                {
                    missing++;
                }
            }
        }
        else
        {
            var byId = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
            foreach (var id in wanted)
            {
                if (byId.TryGetValue(id, out var device))
                {
                    found.Add(device);
                }
                else
                {
                    missing++;
                }
            }
        }

        return missing > 0 ? throw new DevicesNotAvailableException(missing) : found;
    }
}
