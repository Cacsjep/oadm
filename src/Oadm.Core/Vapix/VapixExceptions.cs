using System.Net;

namespace Oadm.Core.Vapix;

/// <summary>A VAPIX call failed: unexpected HTTP status or a response that could not be parsed.</summary>
public class VapixException : Exception
{
    public VapixException()
    {
    }

    public VapixException(string message)
        : base(message)
    {
    }

    public VapixException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VapixException(string message, HttpStatusCode? statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status returned by the device, when there was one.</summary>
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>The device rejected the credentials (HTTP 401 or 403).</summary>
public sealed class VapixAuthenticationException : VapixException
{
    public VapixAuthenticationException()
    {
    }

    public VapixAuthenticationException(string message)
        : base(message)
    {
    }

    public VapixAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VapixAuthenticationException(string message, HttpStatusCode statusCode)
        : base(message, statusCode)
    {
    }
}

/// <summary>
/// The device presented a TLS certificate whose SHA-256 fingerprint differs from the pinned one.
/// Maps to <see cref="Oadm.Sdk.Devices.DeviceStatus.CertificateChanged"/>.
/// </summary>
public sealed class CertificateChangedException : VapixException
{
    public CertificateChangedException()
    {
    }

    public CertificateChangedException(string message)
        : base(message)
    {
    }

    public CertificateChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CertificateChangedException(string expectedFingerprint, string? actualFingerprint, Exception? innerException)
        : base(
            $"Device certificate changed. Pinned SHA-256 {expectedFingerprint}, presented {actualFingerprint ?? "<none>"}.",
            innerException ?? new InvalidOperationException("Certificate mismatch"))
    {
        ExpectedFingerprint = expectedFingerprint;
        ActualFingerprint = actualFingerprint;
    }

    public string? ExpectedFingerprint { get; }

    public string? ActualFingerprint { get; }
}

/// <summary>The device answered with more than <see cref="VapixClient.MaxResponseBytes"/> (16 MB); the answer was dropped.</summary>
public sealed class VapixResponseTooLargeException : VapixException
{
    public VapixResponseTooLargeException()
        : base(DefaultMessage)
    {
    }

    public VapixResponseTooLargeException(string message)
        : base(message)
    {
    }

    public VapixResponseTooLargeException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }

    public VapixResponseTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private const string DefaultMessage = "The device answer is larger than 16 MB and was not read.";
}
