using System.Net.Sockets;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>User texts for failed device requests of the import (the shared <see cref="DeviceMessages"/> where they fit).</summary>
internal static class DeviceErrors
{
    /// <summary>The task itself was cancelled: not a device problem.</summary>
    public static bool IsCancelled(Exception ex, CancellationToken ct) => ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>
    /// The device presents another certificate than the pinned one (the host's <c>CertificateChangedException</c>, known to
    /// plugins only by name): nothing else can work.
    /// </summary>
    public static bool IsCertificateChanged(Exception ex) => Find(ex, e => e.GetType().Name == "CertificateChangedException") is not null;

    /// <summary>
    /// "Timeout after 60 s", "Unreachable - Connection refused", the device's own text of a
    /// <see cref="ConfigDeviceException"/>, else the message. <paramref name="timeout"/> null = the length is not known.
    /// </summary>
    public static string Describe(Exception ex, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is ConfigDeviceException or ConfigFileException)
        {
            return ex.Message;
        }

        if (ex is OperationCanceledException || Find(ex, e => e is TimeoutException) is not null)
        {
            return timeout is { } length ? DeviceMessages.Timeout(length) : DeviceMessages.TimedOut;
        }

        if (Find(ex, e => e is SocketException) is SocketException socket)
        {
            return socket.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable
                ? DeviceMessages.HostUnreachable
                : DeviceMessages.Unreachable(socket.Message);
        }

        if (Find(ex, e => e is HttpRequestException) is HttpRequestException request)
        {
            return DeviceMessages.Unreachable(request.InnerException?.Message ?? request.Message);
        }

        return ex.Message;
    }

    private static Exception? Find(Exception? ex, Func<Exception, bool> match)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (match(e))
            {
                return e;
            }
        }

        return null;
    }
}
