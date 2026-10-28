using Oadm.Core.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Vapix;

/// <summary>basicdeviceinfo with the answer's HTTP Date header and the round trip around it (server clock).</summary>
internal sealed record TimedBasicDeviceInfo(BasicDeviceInfo Info, DateTimeOffset? DeviceDate, DateTimeOffset Sent, DateTimeOffset Received)
{
    /// <summary>The device clock minus the server clock in ms, null without a readable Date header.</summary>
    public int? OffsetMs => ClockHealth.Offset(DeviceDate, Sent, Received);
}

public sealed partial class VapixClient
{
    /// <summary>
    /// <c>ntp.cgi getNTPInfo</c> (read only) with the device's version of the NTP API (<paramref name="apiVersion"/>, 1.x).
    /// Throws like every VAPIX read when the device does not answer or refuses.
    /// </summary>
    internal async Task<NtpState> GetNtpStateAsync(string apiVersion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        var json = await PostJsonAsync(ClockHealth.NtpPath, ClockHealth.NtpInfoRequest(apiVersion), ct).ConfigureAwait(false);
        return ClockHealth.ParseNtpInfo(json);
    }
}
