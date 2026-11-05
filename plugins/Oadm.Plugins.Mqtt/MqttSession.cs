using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Mqtt;

/// <summary>One message of an action.</summary>
/// <param name="Topic">The checked topic.</param>
/// <param name="Payload">The text, sent as UTF-8.</param>
/// <param name="QualityOfService">0, 1 or 2.</param>
/// <param name="Retain">The broker keeps the message for later subscribers.</param>
public sealed record MqttMessage(string Topic, string Payload, int QualityOfService, bool Retain);

/// <summary>What an action sends through a session; a session is never asked to receive.</summary>
public interface IMqttPublisher
{
    /// <summary>Sends one message; QoS 1 and 2 return after the broker's acknowledge. Throws with a plain text when it failed.</summary>
    Task PublishAsync(MqttMessage message, CancellationToken ct);
}

/// <summary>Makes the sessions of an MQTT recipient.</summary>
public sealed class MqttSessionFactory : IRecipientSessionFactory
{
    public IRecipientSession Create(RecipientSessionRequest request) => new MqttSession(request);
}

/// <summary>
/// One connection to a broker, output only: it never subscribes and handles nothing it receives. MQTTnet sends for several actions at
/// the same time. The plugin never reconnects: the pool opens a new session after <see cref="Dropped"/>. The certificates belong to the
/// pool, which disposes them after the session.
/// </summary>
public sealed class MqttSession : IRecipientSession, IMqttPublisher
{
    /// <summary>One publish. A broker answers in milliseconds; an action must not stand behind a slow one for long.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Below the pool's own limit of a close.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly MqttClientOptions _options;
    private readonly string _target;
    private readonly bool _allowUntrusted;
    private readonly IReadOnlyList<X509Certificate2> _trusted;
    private readonly TimeProvider _time;
    private readonly X509Certificate2? _keyStoreCopy;
    private string? _certificateProblem;
    private string? _droppedReason;
    private int _ended;

    public MqttSession(RecipientSessionRequest request, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Get("host") ?? string.Empty;
        var port = request.GetInt("port", MqttRecipient.DefaultPort);
        _target = string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
        _allowUntrusted = request.GetBool("allowUntrusted");
        _trusted = request.CertificatesOf("trustedCa");
        _time = time ?? TimeProvider.System;

        var clientId = request.Get("clientId")?.Trim();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithProtocolVersion(request.Get("protocolVersion") == MqttRecipient.Version311 ? MqttProtocolVersion.V311 : MqttProtocolVersion.V500)
            .WithClientId(string.IsNullOrEmpty(clientId) ? MqttRecipient.DefaultClientId : clientId)
            .WithTimeout(AnswerTimeout);
        var user = request.Get("user") ?? string.Empty;
        var password = request.Get("password");
        if (user.Length > 0 || !string.IsNullOrEmpty(password))
        {
            builder = builder.WithCredentials(user, string.IsNullOrEmpty(password) ? null : password);
        }

        if (request.GetBool("tls"))
        {
            var chosen = request.CertificatesOf("clientCertificate");
            _keyStoreCopy = chosen.Count > 0 && OperatingSystem.IsWindows() ? KeyStoreCopy(chosen[0]) : null;
            var clientCertificate = _keyStoreCopy ?? (chosen.Count > 0 ? chosen[0] : null);
            builder = builder.WithTlsOptions(tls =>
            {
                tls.UseTls()
                    .WithTargetHost(host)
                    .WithRevocationMode(X509RevocationMode.NoCheck)
                    .WithCertificateValidationHandler(CheckBroker);
                if (clientCertificate is not null)
                {
                    tls.WithClientCertificates([clientCertificate]);
                }
            });
        }

        _options = builder.Build();
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public event EventHandler<RecipientDroppedEventArgs>? Dropped;

    public async Task ConnectAsync(CancellationToken ct)
    {
        MqttClientConnectResult result;
        try
        {
            result = await _client.ConnectAsync(_options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MqttCommunicationException or MqttProtocolViolationException or AuthenticationException or IOException or SocketException)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(ConnectProblem(ex), ex);
        }

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(Refused(result.ResultCode));
        }
    }

    public async Task PublishAsync(MqttMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var packet = new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)message.QualityOfService)
            .WithRetainFlag(message.Retain)
            .Build();
        using var timeout = new CancellationTokenSource(AnswerTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        MqttClientPublishResult result;
        try
        {
            result = await _client.PublishAsync(packet, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MqttClientNotConnectedException or ObjectDisposedException)
        {
            throw new InvalidOperationException(RecipientSessionTexts.NotConnected(Volatile.Read(ref _droppedReason)), ex);
        }
        catch (Exception ex) when (ex is OperationCanceledException or MqttCommunicationTimedOutException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{_target} did not answer. The message may have been sent.", ex);
        }
        catch (MqttCommunicationException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"The connection to {_target} ended. The message may have been sent.", ex);
        }

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(Refused(result.ReasonCode));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _ended, 1);
        if (_client.IsConnected)
        {
            using var timeout = new CancellationTokenSource(CloseTimeout, _time);
            try
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MqttCommunicationException or OperationCanceledException)
            {
                // The broker is gone already.
            }
        }

        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _client.Dispose();
        _keyStoreCopy?.Dispose(); // takes the key out of the key store
    }

    /// <summary>
    /// Windows TLS refuses a key that lives only in memory for a client certificate, as for a server's (SiteIdentityStore), so on
    /// Windows the session holds a copy with its key in a file of the key store until it closes.
    /// </summary>
    private static X509Certificate2 KeyStoreCopy(X509Certificate2 certificate)
    {
        byte[]? pkcs12 = null;
        try
        {
            pkcs12 = certificate.Export(X509ContentType.Pkcs12);
            return X509CertificateLoader.LoadPkcs12(pkcs12, null);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The key of the client certificate cannot be used.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private bool CheckBroker(MqttClientCertificateValidationEventArgs check)
    {
        var problem = MqttCertificateCheck.Problem(check.Certificate, check.Chain, check.SslPolicyErrors, _allowUntrusted, _trusted);
        Volatile.Write(ref _certificateProblem, problem);
        return problem is null;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs disconnected)
    {
        if (disconnected.ClientWasConnected)
        {
            var reason = DroppedReason(disconnected.Reason);
            Volatile.Write(ref _droppedReason, reason);
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Dropped?.Invoke(this, new RecipientDroppedEventArgs(reason));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>What a failed connect means to the user: the texts of the library are not plain words.</summary>
    private string ConnectProblem(Exception failure)
    {
        if (Volatile.Read(ref _certificateProblem) is { } certificate)
        {
            return certificate;
        }

        for (var ex = failure; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case AuthenticationException:
                    return $"The TLS connection to {_target} failed. Check the port and the certificates.";
                case MqttProtocolViolationException:
                    return "The other side does not speak MQTT.";
                case MqttCommunicationTimedOutException:
                    return $"{_target} did not answer.";
                case SocketException socket when socket.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown):
                    return $"{_target} cannot be reached: {socket.Message}";
            }
        }

        return $"{_target} closed the connection. Check the port, TLS and the protocol version.";
    }

    private static string Refused(MqttClientConnectResultCode code) => code switch
    {
        MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized or MqttClientConnectResultCode.BadAuthenticationMethod =>
            "The broker refused the login. Check the user name and password.",
        MqttClientConnectResultCode.ClientIdentifierNotValid => "The broker refused the client ID.",
        MqttClientConnectResultCode.UnsupportedProtocolVersion => "The broker does not support this protocol version.",
        MqttClientConnectResultCode.ServerUnavailable or MqttClientConnectResultCode.ServerBusy => "The broker is not available.",
        MqttClientConnectResultCode.Banned => "The broker banned this client.",
        _ => string.Create(CultureInfo.InvariantCulture, $"The broker refused the connection (code {(int)code})."),
    };

    private static string Refused(MqttClientPublishReasonCode code) => code switch
    {
        MqttClientPublishReasonCode.NotAuthorized => "The broker does not allow this topic.",
        MqttClientPublishReasonCode.TopicNameInvalid => "The broker refused the topic.",
        MqttClientPublishReasonCode.QuotaExceeded => "The broker is over its quota.",
        MqttClientPublishReasonCode.PayloadFormatInvalid => "The broker refused the payload.",
        _ => string.Create(CultureInfo.InvariantCulture, $"The broker refused the message (code {(int)code})."),
    };

    private static string DroppedReason(MqttClientDisconnectReason reason) => reason switch
    {
        MqttClientDisconnectReason.ServerShuttingDown => "The broker is shutting down",
        MqttClientDisconnectReason.SessionTakenOver => "Another connection uses this client ID",
        MqttClientDisconnectReason.NotAuthorized => "The broker refused the login",
        MqttClientDisconnectReason.KeepAliveTimeout => "The broker stopped answering",
        _ => "The connection ended",
    };
}
