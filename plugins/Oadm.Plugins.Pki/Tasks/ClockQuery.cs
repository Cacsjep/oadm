using Oadm.Plugins.DeviceClocks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>The read-only clock query of the Enable HTTPS and Enable IEEE 802.1X dialogs (<see cref="CameraClock.QueryMethod"/>).</summary>
internal static class ClockQuery
{
    public static async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!string.Equals(method, CameraClock.QueryMethod, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown query '{method}'.", nameof(method));
        }

        return await CameraClock.QueryAsync(ctx.Vapix, device, time, ct).ConfigureAwait(false);
    }
}
