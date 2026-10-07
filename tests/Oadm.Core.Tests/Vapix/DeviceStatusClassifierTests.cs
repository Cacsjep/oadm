using System.Net;
using System.Net.Sockets;

using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Vapix;

public class DeviceStatusClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, DeviceStatus.Ok)]
    [InlineData(HttpStatusCode.NoContent, DeviceStatus.Ok)]
    [InlineData(HttpStatusCode.Unauthorized, DeviceStatus.CredentialsRequired)]
    [InlineData(HttpStatusCode.Forbidden, DeviceStatus.CredentialsRequired)]
    [InlineData(HttpStatusCode.NotFound, DeviceStatus.Unknown)]
    [InlineData(HttpStatusCode.InternalServerError, DeviceStatus.Unknown)]
    public void FromStatusCode(HttpStatusCode status, DeviceStatus expected)
    {
        Assert.Equal(expected, DeviceStatusClassifier.FromStatusCode(status));
    }

    public static TheoryData<Exception, DeviceStatus> Exceptions => new()
    {
        { new CertificateChangedException("AA", "BB", null), DeviceStatus.CertificateChanged },
        { new VapixAuthenticationException("x", HttpStatusCode.Unauthorized), DeviceStatus.CredentialsRequired },
        { new PasswordNotSetException(), DeviceStatus.PasswordNotSet },
        { new VapixException("x", HttpStatusCode.Forbidden), DeviceStatus.CredentialsRequired },
        { new VapixException("parse"), DeviceStatus.Unknown },
        { new HttpRequestException(HttpRequestError.ConnectionError, "refused"), DeviceStatus.Unreachable },
        { new HttpRequestException(HttpRequestError.NameResolutionError, "dns"), DeviceStatus.Unreachable },
        { new HttpRequestException("wrapped", new SocketException((int)SocketError.ConnectionRefused)), DeviceStatus.Unreachable },
        { new HttpRequestException("wrapped", new SocketException((int)SocketError.HostUnreachable)), DeviceStatus.Unreachable },
        { new HttpRequestException("status", null, HttpStatusCode.Unauthorized), DeviceStatus.CredentialsRequired },
        { new TaskCanceledException("timeout", new TimeoutException()), DeviceStatus.Unreachable },
        { new TimeoutException(), DeviceStatus.Unreachable },
        { new InvalidOperationException(), DeviceStatus.Unknown },
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void FromException(Exception exception, DeviceStatus expected)
    {
        Assert.Equal(expected, DeviceStatusClassifier.FromException(exception));
    }
}
