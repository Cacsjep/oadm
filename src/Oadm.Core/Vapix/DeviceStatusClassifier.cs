using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

using Oadm.Sdk.Devices;

namespace Oadm.Core.Vapix;

/// <summary>Maps HTTP results and exceptions of VAPIX calls to <see cref="DeviceStatus"/>.</summary>
public static class DeviceStatusClassifier
{
    /// <summary>2xx = Ok, 401/403 = CredentialsRequired, anything else Unknown.</summary>
    public static DeviceStatus FromStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code switch
        {
            >= 200 and < 300 => DeviceStatus.Ok,
            401 or 403 => DeviceStatus.CredentialsRequired,
            _ => DeviceStatus.Unknown,
        };
    }

    /// <summary>
    /// Classifies a failed call. Timeouts, refused connections, unreachable hosts and DNS
    /// failures are <see cref="DeviceStatus.Unreachable"/>. A caller-requested cancellation is
    /// not a device state and is rethrown by callers, never classified; pass it here and you
    /// get <see cref="DeviceStatus.Unknown"/>.
    /// </summary>
    public static DeviceStatus FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case CertificateChangedException:
                return DeviceStatus.CertificateChanged;
            case VapixAuthenticationException:
                return DeviceStatus.CredentialsRequired;
            case PasswordNotSetException:
                return DeviceStatus.PasswordNotSet;
            case VapixException { StatusCode: { } status }:
                return FromStatusCode(status);
            case TimeoutException:
                return DeviceStatus.Unreachable;
            case TaskCanceledException { InnerException: TimeoutException }:
                return DeviceStatus.Unreachable;
            case SocketException socket:
                return FromSocketError(socket.SocketErrorCode);
            case HttpRequestException http:
                return FromHttpRequestException(http);
            case AuthenticationException:
                // TLS handshake failure that is not a pin mismatch: we reached the device.
                return DeviceStatus.Unknown;
            default:
                break;
        }

        return exception.InnerException is { } inner ? FromException(inner) : DeviceStatus.Unknown;
    }

    private static DeviceStatus FromHttpRequestException(HttpRequestException http)
    {
        if (http.StatusCode is { } status)
        {
            return FromStatusCode(status);
        }

        switch (http.HttpRequestError)
        {
            case HttpRequestError.ConnectionError:
            case HttpRequestError.NameResolutionError:
                return DeviceStatus.Unreachable;
            default:
                break;
        }

        return http.InnerException is { } inner ? FromException(inner) : DeviceStatus.Unknown;
    }

    private static DeviceStatus FromSocketError(SocketError error)
    {
        return error switch
        {
            SocketError.ConnectionRefused
                or SocketError.HostUnreachable
                or SocketError.HostDown
                or SocketError.NetworkUnreachable
                or SocketError.NetworkDown
                or SocketError.TimedOut
                or SocketError.HostNotFound
                or SocketError.TryAgain
                or SocketError.NoData
                or SocketError.ConnectionReset
                or SocketError.ConnectionAborted => DeviceStatus.Unreachable,
            _ => DeviceStatus.Unknown,
        };
    }
}

/// <summary>The device is factory default: no admin password exists yet (systemready needsetup=yes).</summary>
public sealed class PasswordNotSetException : VapixException
{
    public PasswordNotSetException()
        : base("The device has no admin password yet (factory default).")
    {
    }

    public PasswordNotSetException(string message)
        : base(message)
    {
    }

    public PasswordNotSetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
