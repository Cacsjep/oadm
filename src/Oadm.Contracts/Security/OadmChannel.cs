using System.Net.Security;

using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace Oadm.Contracts.Security;

/// <summary>gRPC metadata keys of the OADM API (auth travels in metadata, never in messages).</summary>
public static class OadmMetadata
{
    /// <summary>"authorization: Bearer &lt;token&gt;".</summary>
    public const string Authorization = "authorization";

    public const string BearerPrefix = "Bearer ";

    /// <summary>Machine name of the client; the task owner is "&lt;user&gt;@&lt;machine&gt;".</summary>
    public const string ClientMachine = "oadm-client-machine";
}

/// <summary>Adds the bearer token and the client machine name to every call.</summary>
public sealed class AuthHeaderInterceptor(Func<string?> token, string? clientMachine = null) : Interceptor
{
    private readonly string _machine = clientMachine ?? Environment.MachineName;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(request, WithHeaders(context));
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(request, WithHeaders(context));
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(WithHeaders(context));
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(WithHeaders(context));
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(request, WithHeaders(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> WithHeaders<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = context.Options.Headers ?? [];
        string? value = token();
        if (!string.IsNullOrEmpty(value) && headers.Get(OadmMetadata.Authorization) is null)
        {
            headers.Add(OadmMetadata.Authorization, OadmMetadata.BearerPrefix + value);
        }

        if (headers.Get(OadmMetadata.ClientMachine) is null)
        {
            headers.Add(OadmMetadata.ClientMachine, _machine);
        }

        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, context.Options.WithHeaders(headers));
    }
}

/// <summary>
/// Creates the client channel to an OADM server: HTTP/2 over TLS with the pinned server certificate
/// (<see cref="ServerCertificatePinning"/>) for https addresses, plain h2c for http (tests, development), and the
/// <see cref="AuthHeaderInterceptor"/> on every call.
/// </summary>
public static class OadmChannel
{
    /// <summary>Keyframes of high resolution video can exceed the 4 MB default message limit.</summary>
    public const int MaxReceiveMessageSize = 32 * 1024 * 1024;

    /// <summary>"localhost:5080" -> "https://localhost:5080"; an explicit http:// or https:// is kept.</summary>
    public static Uri NormalizeAddress(string? address, string defaultAddress = "https://localhost:5080")
    {
        string trimmed = (address ?? "").Trim();
        if (trimmed.Length == 0)
        {
            trimmed = defaultAddress;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed.TrimEnd('/'), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new FormatException($"'{address}' is not a server address. Enter a host name or IP address, optionally with a port, e.g. localhost:5080.");
        }

        return uri;
    }

    /// <param name="address">Normalized server address.</param>
    /// <param name="pinning">Required for https addresses.</param>
    /// <param name="token">Current bearer token (read on every call).</param>
    /// <param name="handler">Test hook: replaces the HTTP handler (in-process test server); no TLS check then.</param>
    public static (GrpcChannel Channel, CallInvoker Invoker) Create(Uri address, ServerCertificatePinning? pinning, Func<string?> token, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            if (address.Scheme == Uri.UriSchemeHttps)
            {
                ArgumentNullException.ThrowIfNull(pinning);
                sockets.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = pinning.CallbackFor(address) };
            }

            handler = sockets;
        }

        var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxReceiveMessageSize = MaxReceiveMessageSize,
        });
        return (channel, channel.Intercept(new AuthHeaderInterceptor(token)));
    }
}
