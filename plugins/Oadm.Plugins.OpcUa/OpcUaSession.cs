using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Logging;

using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Security.Certificates;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.OpcUa;

/// <summary>What an action writes through a session; a session is never asked to read.</summary>
public interface IOpcUaWriter
{
    /// <summary>Writes the Value attribute of one node and checks the server's status.</summary>
    Task WriteAsync(NodeId node, Variant value, CancellationToken ct);
}

/// <summary>Makes the sessions of an OPC UA recipient.</summary>
public sealed class OpcUaSessionFactory : IRecipientSessionFactory
{
    public IRecipientSession Create(RecipientSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OpcUaSession(OpcUaOptions.From(request), request.Logger);
    }
}

/// <summary>A recipient's settings. A class, so that no generated text ever holds the password.</summary>
public sealed class OpcUaOptions
{
    public required string Endpoint { get; init; }

    public string Security { get; init; } = OpcUaRecipient.SecurityNone;

    public string Policy { get; init; } = OpcUaRecipient.PolicyBest;

    public string Login { get; init; } = OpcUaRecipient.LoginAnonymous;

    public string User { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>The chosen certificate with its key, then its issuers.</summary>
    public IReadOnlyList<X509Certificate2> ClientCertificate { get; init; } = [];

    /// <summary>The chosen certificate, then its issuers.</summary>
    public IReadOnlyList<X509Certificate2> TrustedCa { get; init; } = [];

    public bool AllowUntrusted { get; init; }

    internal MessageSecurityMode Mode => Security switch
    {
        OpcUaRecipient.SecuritySign => MessageSecurityMode.Sign,
        OpcUaRecipient.SecuritySignAndEncrypt => MessageSecurityMode.SignAndEncrypt,
        _ => MessageSecurityMode.None,
    };

    /// <summary>The chosen policy; null for the best the server offers.</summary>
    internal string? PolicyUri => Policy switch
    {
        OpcUaRecipient.PolicyBasic256Sha256 => SecurityPolicies.Basic256Sha256,
        OpcUaRecipient.PolicyAes128 => SecurityPolicies.Aes128_Sha256_RsaOaep,
        OpcUaRecipient.PolicyAes256 => SecurityPolicies.Aes256_Sha256_RsaPss,
        _ => null,
    };

    public static OpcUaOptions From(RecipientSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OpcUaOptions
        {
            Endpoint = request.Get(OpcUaRecipient.Endpoint) ?? string.Empty,
            Security = request.Get(OpcUaRecipient.Security) ?? OpcUaRecipient.SecurityNone,
            Policy = request.Get(OpcUaRecipient.Policy) ?? OpcUaRecipient.PolicyBest,
            Login = request.Get(OpcUaRecipient.Login) ?? OpcUaRecipient.LoginAnonymous,
            User = request.Get(OpcUaRecipient.User) ?? string.Empty,
            Password = request.Get(OpcUaRecipient.Password) ?? string.Empty,
            ClientCertificate = request.CertificatesOf(OpcUaRecipient.ClientCertificate),
            TrustedCa = request.CertificatesOf(OpcUaRecipient.TrustedCa),
            AllowUntrusted = request.GetBool(OpcUaRecipient.AllowUntrusted),
        };
    }
}

/// <summary>
/// One OPC UA session, built in memory: no configuration file, no certificate store, nothing on disk. The stack's channel and session
/// get the chosen certificate directly, and <see cref="OpcUaCertificates"/> decides about the server's. It only writes; its one
/// read is the stack's keep alive, and when that fails the pool opens a new session.
/// </summary>
public sealed class OpcUaSession : IRecipientSession, IOpcUaWriter
{
    /// <summary>One write. A machine must not stand behind a slow server for long.</summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private const uint SessionTimeoutMs = 60_000;
    private const int OperationTimeoutMs = 10_000;

    /// <summary>Used only where no chosen certificate names the client: security None.</summary>
    private const string PlainApplicationUri = "urn:oadm:client";

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly OpcUaOptions _options;
    private readonly ILogger _logger;
    private readonly int _keepAliveMs;
    private OpcUaCertificates? _certificates;
    private ISession? _session;
    private int _ended;

    public OpcUaSession(OpcUaOptions options, ILogger logger)
        : this(options, logger, 5000)
    {
    }

    internal OpcUaSession(OpcUaOptions options, ILogger logger, int keepAliveMs)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keepAliveMs = keepAliveMs;
    }

    public event EventHandler<RecipientDroppedEventArgs>? Dropped;

    public async Task ConnectAsync(CancellationToken ct)
    {
        CheckCertificates();
        try
        {
            _session = await OpenAsync(ct).ConfigureAwait(false);
        }
        catch (ServiceResultException ex)
        {
            ct.ThrowIfCancellationRequested(); // the pool's timeout, which the stack reports as a status
            throw new InvalidOperationException(OpcUaTexts.ConnectFailed(ex.StatusCode, ex.Message, _options.Endpoint, _options.Login), ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new InvalidOperationException($"{_options.Endpoint} cannot be reached.", ex);
        }
    }

    public async Task WriteAsync(NodeId node, Variant value, CancellationToken ct)
    {
        var session = _session ?? throw new InvalidOperationException(RecipientSessionTexts.NotConnected(null));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);
        var write = new WriteValue { NodeId = node, AttributeId = Attributes.Value, Value = new DataValue(value) };
        WriteResponse response;
        try
        {
            response = await session.WriteAsync(null, new[] { write }.ToArrayOf(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(OpcUaTexts.NoAnswer);
        }
        catch (ServiceResultException ex)
        {
            throw new InvalidOperationException(OpcUaTexts.WriteFailed(ex.StatusCode), ex);
        }

        var results = response.Results;
        if (results.Count != 1)
        {
            throw new InvalidOperationException(OpcUaTexts.NoAnswer);
        }

        if (StatusCode.IsBad(results[0]))
        {
            throw new InvalidOperationException(OpcUaTexts.WriteFailed(results[0]));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _ended, 1);
        if (_session is { } session)
        {
            session.KeepAlive -= OnKeepAlive;
            session.SessionClosing -= OnClosing;
            using var timeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await session.CloseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServiceResultException or OperationCanceledException or IOException)
            {
                // The server went first, or a close that hangs is given up on.
            }

            session.Dispose();
        }

        _certificates?.Dispose();
    }

    /// <summary>Connects to the server, picks its endpoint and opens the session. Throws what the stack throws.</summary>
    private async Task<ISession> OpenAsync(CancellationToken ct)
    {
        var secured = _options.Mode != MessageSecurityMode.None;

        // The pool disposes its certificates after the session; the stack disposes its own, so this one is a second handle.
        using var own = _options.ClientCertificate.Count > 0 ? Certificate.From(new X509Certificate2(_options.ClientCertificate[0].Handle)) : null;
        var telemetry = new OpcUaTelemetry(_logger);
        var url = new Uri(_options.Endpoint);
        _certificates = new OpcUaCertificates(url, _options.AllowUntrusted || !secured, _options.TrustedCa, own);
        var configuration = Configuration(telemetry, _certificates, own);
        var endpointConfiguration = EndpointConfiguration.Create(configuration);

        ArrayOf<EndpointDescription> offered;
        using (var discovery = await DiscoveryClient.CreateAsync(configuration, url, endpointConfiguration, ct: ct).ConfigureAwait(false))
        {
            offered = await discovery.GetEndpointsAsync(default, ct).ConfigureAwait(false);
        }

        var selected = OpcUaEndpoints.Select(offered, url, _options.Mode, _options.PolicyUri, own);
        var channel = await UaChannelBase.CreateUaBinaryChannelAsync(
            configuration,
            selected,
            endpointConfiguration,
            secured ? own?.AddRef() : null,
            secured ? Chain(own) : null,
            configuration.CreateMessageContext(),
            ct).ConfigureAwait(false);

        var session = new DefaultSessionFactory(telemetry).Create(
            channel,
            configuration,
            new ConfiguredEndpoint(null, selected, endpointConfiguration),
            secured ? own?.AddRef() : null,
            secured ? Chain(own) : null,
            offered);
        try
        {
            session.KeepAliveInterval = _keepAliveMs;
            session.KeepAlive += OnKeepAlive;
            session.SessionClosing += OnClosing;
            var identity = await IdentityAsync(own, ct).ConfigureAwait(false);
            await session.OpenAsync("OADM", SessionTimeoutMs, identity, default, false, true, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.KeepAlive -= OnKeepAlive;
            session.SessionClosing -= OnClosing;
            session.Dispose();
            throw;
        }
    }

    /// <summary>The application without a file. Nothing here names a store, so nothing reads or writes one.</summary>
    private ApplicationConfiguration Configuration(ITelemetryContext telemetry, OpcUaCertificates certificates, Certificate? own)
    {
        var uris = own is not null ? X509Utils.GetApplicationUrisFromCertificate(own) : [];
        return new ApplicationConfiguration(telemetry)
        {
            ApplicationName = "OADM",
            ApplicationUri = uris.Count > 0 ? uris[0] : PlainApplicationUri,
            ProductUri = "urn:oadm",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration { SendCertificateChain = _options.ClientCertificate.Count > 1 },
            TransportQuotas = new TransportQuotas { OperationTimeout = OperationTimeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = (int)SessionTimeoutMs },
            CertificateManager = certificates,
        };
    }

    /// <summary>The certificate and its issuers for the transport; null without issuers. The receiver owns it.</summary>
    private CertificateCollection? Chain(Certificate? own)
    {
        if (own is null || _options.ClientCertificate.Count < 2)
        {
            return null;
        }

        var chain = new CertificateCollection { own };
        foreach (var issuer in _options.ClientCertificate.Skip(1))
        {
            using var parsed = Certificate.FromRawData(issuer.RawData);
            chain.Add(parsed);
        }

        return chain;
    }

    private async Task<IUserIdentity> IdentityAsync(Certificate? own, CancellationToken ct) => _options.Login switch
    {
        OpcUaRecipient.LoginUser => new UserIdentity(_options.User, Encoding.UTF8.GetBytes(_options.Password)),
        OpcUaRecipient.LoginCertificate => await UserIdentity.CreateAsync(
            new CertificateIdentifier { RawData = own!.RawData },
            new CertificatePasswordProvider(),
            _certificates!,
            ct).ConfigureAwait(false),
        _ => new UserIdentity(),
    };

    /// <summary>The reasons a recipient cannot work, before anything goes to the network.</summary>
    private void CheckCertificates()
    {
        var secured = _options.Mode != MessageSecurityMode.None;
        if (_options.ClientCertificate.Count == 0)
        {
            if (secured)
            {
                throw new InvalidOperationException(OpcUaTexts.NeedsClientCertificate);
            }

            if (_options.Login == OpcUaRecipient.LoginCertificate)
            {
                throw new InvalidOperationException(OpcUaTexts.NeedsLoginCertificate);
            }

            return;
        }

        var chosen = _options.ClientCertificate[0];
        if ((secured || _options.Login == OpcUaRecipient.LoginCertificate) && !chosen.HasPrivateKey)
        {
            throw new InvalidOperationException(OpcUaTexts.NoPrivateKey);
        }

        if (secured)
        {
            using var parsed = Certificate.FromRawData(chosen.RawData);
            if (X509Utils.GetApplicationUrisFromCertificate(parsed).Count == 0)
            {
                throw new InvalidOperationException(OpcUaTexts.NoUriName);
            }
        }
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (e.Status is { } status && ServiceResult.IsBad(status))
        {
            e.CancelKeepAlive = true;
            End(OpcUaTexts.ConnectionEnded);
        }
        else if (e.CurrentState is ServerState.Shutdown or ServerState.Failed or ServerState.CommunicationFault)
        {
            e.CancelKeepAlive = true;
            End(OpcUaTexts.ServerStopped);
        }
    }

    private void OnClosing(object? sender, EventArgs e) => End(OpcUaTexts.SessionClosed);

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            Dropped?.Invoke(this, new RecipientDroppedEventArgs(reason));
        }
    }
}

/// <summary>The endpoint of a recipient among those the server offers.</summary>
internal static class OpcUaEndpoints
{
    /// <summary>
    /// The endpoint with the chosen security, or the one the server ranks highest for the chosen certificate's key. It keeps the address
    /// the user entered: servers often name themselves by a host OADM cannot reach.
    /// </summary>
    public static EndpointDescription Select(ArrayOf<EndpointDescription> offered, Uri url, MessageSecurityMode mode, string? policyUri, Certificate? own)
    {
        var modes = offered.ToArray()!
            .Where(e => e.SecurityMode == mode && e.EndpointUrl?.StartsWith(Utils.UriSchemeOpcTcp, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (modes.Count == 0)
        {
            throw new InvalidOperationException(OpcUaTexts.ModeNotOffered);
        }

        var candidates = mode == MessageSecurityMode.None ? modes
            : policyUri is not null ? modes.FindAll(e => e.SecurityPolicyUri == policyUri)
            : modes.FindAll(e => Fits(e, own));
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(OpcUaTexts.PolicyNotOffered);
        }

        var best = (EndpointDescription)candidates.MaxBy(e => e.SecurityLevel)!.Clone();
        best.EndpointUrl = new UriBuilder(best.EndpointUrl!) { Host = url.IdnHost, Port = url.Port }.ToString();
        return best;
    }

    private static bool Fits(EndpointDescription endpoint, Certificate? own) =>
        own is null
        || CertificateIdentifier.MapSecurityPolicyToCertificateTypes(endpoint.SecurityPolicyUri!).Contains(CertificateIdentifier.GetCertificateType(own));
}

/// <summary>The stack's log in the server log: warnings and worse, nothing else.</summary>
internal sealed class OpcUaTelemetry(ILogger logger) : TelemetryContextBase(new StackLogs(logger))
{
    private sealed class StackLogs(ILogger logger) : ILoggerFactory, ILogger
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
