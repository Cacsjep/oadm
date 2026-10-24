using System.Buffers.Binary;
using Oadm.Plugins.Radius.Protocol.Eap;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Oadm.Plugins.Radius.Tests.Support;

/// <summary>How the test camera behaves.</summary>
public sealed record SupplicantOptions
{
    public string Identity { get; init; } = "axis-accc8e123456";

    /// <summary>The camera's certificate; null sends none.</summary>
    public TestCertificate? Certificate { get; init; } = TestPki.CameraCertificate;

    /// <summary>The TLS versions the camera offers, newest first.</summary>
    public ProtocolVersion[] Versions { get; init; } = [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12];

    /// <summary>The cipher suites the camera offers; null for BouncyCastle's defaults.</summary>
    public int[]? CipherSuites { get; init; }

    /// <summary>The CA the camera trusts for the server certificate; a different one makes it send alert 48.</summary>
    public byte[] TrustedCa { get; init; } = TestPki.CaCertificate;

    /// <summary>TLS data per EAP-TLS fragment the camera sends.</summary>
    public int FragmentSize { get; init; } = 1000;

    /// <summary>Set L on unfragmented messages too (allowed, RFC 9190 2.1.9).</summary>
    public bool LengthOnUnfragmented { get; init; }

    /// <summary>Answer an EAP-TLS Start with a NAK asking for these methods instead.</summary>
    public byte[]? NakWith { get; init; }

    /// <summary>The signature schemes the camera offers (signature_algorithms); null for BouncyCastle's defaults.</summary>
    public IList<SignatureAndHashAlgorithm>? SignatureAlgorithms { get; init; }

    /// <summary>Send the second flight in two unfragmented messages: its first TLS record, then the rest when asked.</summary>
    public bool SplitFlightAfterFirstRecord { get; init; }
}

/// <summary>
/// The camera of the tests: an EAP peer with a BouncyCastle TLS client in non-blocking mode (RFC 3748, RFC 5216, RFC 9190),
/// fragmentation both ways, and the MSK it derives itself, to compare with the keys of the Access-Accept.
/// </summary>
public sealed class TestSupplicant
{
    private readonly SupplicantOptions _options;
    private TlsClientProtocol? _tls;
    private PeerClient? _client;
    private byte[]? _incoming;
    private int _incomingLength;
    private byte[]? _outgoing;
    private int _outgoingOffset;
    private byte[]? _rest;
    private bool _split;

    public TestSupplicant(SupplicantOptions? options = null)
    {
        _options = options ?? new SupplicantOptions();
    }

    /// <summary>The MSK the camera derived (RFC 5216 2.3 / RFC 9190 2.3) once its handshake completed.</summary>
    public byte[]? Msk { get; private set; }

    /// <summary>EAP-Success or EAP-Failure received.</summary>
    public EapCode? Result { get; private set; }

    /// <summary>The TLS 1.3 commitment message (application data 0x00) arrived.</summary>
    public bool CommitmentReceived { get; private set; }

    public ProtocolVersion? NegotiatedVersion => _client?.Version;

    public int? NegotiatedCipherSuite => _client?.CipherSuite;

    /// <summary>The alert the camera received from OADM.</summary>
    public short? AlertReceived => _client?.AlertReceived;

    /// <summary>The alert the camera raised.</summary>
    public short? AlertRaised => _client?.AlertRaised;

    /// <summary>EAP-TLS messages the camera received with M set, and acknowledgements it sent.</summary>
    public int FragmentsReceived { get; private set; }

    public int FragmentsSent { get; private set; }

    /// <summary>The flags and lengths of every EAP-TLS message the server sent (for the fragmentation tests).</summary>
    public List<EapTlsMessage> ServerMessages { get; } = [];

    /// <summary>The camera's answer to one EAP packet of OADM; null for Success/Failure (no answer).</summary>
    public EapPacket? Respond(EapPacket request)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Code)
        {
            case EapCode.Success or EapCode.Failure:
                Result = request.Code;
                return null;
            case EapCode.Request when request.Type == EapType.Identity:
                return EapPacket.Response(request.Identifier, EapType.Identity, System.Text.Encoding.UTF8.GetBytes(_options.Identity));
            case EapCode.Request when request.Type == EapType.Tls:
                return Tls(request.Identifier, EapTlsMessage.Parse(request.Data.Span) ?? throw new InvalidOperationException("Bad EAP-TLS request"));
            default:
                return EapPacket.Response(request.Identifier, EapType.Nak, new byte[] { EapType.Tls });
        }
    }

    private EapPacket Tls(byte identifier, EapTlsMessage message)
    {
        ServerMessages.Add(message);
        if (message.IsStart)
        {
            if (_options.NakWith is { } methods)
            {
                return EapPacket.Response(identifier, EapType.Nak, methods);
            }

            _client = new PeerClient(_options);
            _tls = new TlsClientProtocol();
            _tls.Connect(_client);
            return SendFlight(identifier, Drain());
        }

        if (_outgoing is not null)
        {
            // OADM acknowledged a fragment: the next one.
            if (!message.IsAcknowledgement)
            {
                throw new InvalidOperationException("Expected an acknowledgement");
            }

            return NextFragment(identifier);
        }

        if (message.HasMore)
        {
            if (_incoming is null)
            {
                _incoming = new byte[message.TotalLength ?? throw new InvalidOperationException("First fragment without L")];
                _incomingLength = 0;
            }

            message.Data.Span.CopyTo(_incoming.AsSpan(_incomingLength));
            _incomingLength += message.Data.Length;
            FragmentsReceived++;
            return Ack(identifier);
        }

        byte[] data;
        if (_incoming is not null)
        {
            message.Data.Span.CopyTo(_incoming.AsSpan(_incomingLength));
            _incomingLength += message.Data.Length;
            if (_incomingLength != _incoming.Length)
            {
                throw new InvalidOperationException("Fragments do not add up");
            }

            data = _incoming;
            _incoming = null;
        }
        else
        {
            data = message.Data.ToArray();
        }

        if (data.Length == 0)
        {
            if (_rest is { } rest)
            {
                _rest = null;
                return SendFlight(identifier, rest);
            }

            return Ack(identifier);
        }

        try
        {
            _tls!.OfferInput(data);
        }
        catch (TlsFatalAlertReceived)
        {
            // OADM refused us: answer the alert with an empty response, then OADM sends EAP-Failure (RFC 5216 2.1.3).
            return Ack(identifier);
        }
        catch (TlsFatalAlert)
        {
            // We refuse OADM: send our alert (RFC 5216 2.1.3).
            return SendFlight(identifier, Drain());
        }

        if (_tls.GetAvailableInputBytes() > 0)
        {
            var app = new byte[_tls.GetAvailableInputBytes()];
            _tls.ReadInput(app, 0, app.Length);
            CommitmentReceived = app is [0];
        }

        if (_client!.HandshakeComplete && Msk is null)
        {
            Msk = _client.Msk;
        }

        var output = Drain();
        if (output.Length > 0 && _options.SplitFlightAfterFirstRecord && !_split && output.Length > 5)
        {
            // The first TLS record now, the rest when OADM asks for more with an empty request.
            _split = true;
            var first = 5 + ((output[3] << 8) | output[4]);
            if (first < output.Length)
            {
                _rest = output[first..];
                output = output[..first];
            }
        }

        return output.Length == 0 ? Ack(identifier) : SendFlight(identifier, output);
    }

    private EapPacket SendFlight(byte identifier, byte[] flight)
    {
        if (flight.Length <= _options.FragmentSize)
        {
            var whole = _options.LengthOnUnfragmented
                ? new EapTlsMessage(EapTlsBits.Length, (uint)flight.Length, flight)
                : new EapTlsMessage(EapTlsBits.None, null, flight);
            return EapPacket.Response(identifier, EapType.Tls, whole.ToBytes());
        }

        _outgoing = flight;
        _outgoingOffset = 0;
        return NextFragment(identifier);
    }

    private EapPacket NextFragment(byte identifier)
    {
        var flight = _outgoing!;
        var size = Math.Min(_options.FragmentSize, flight.Length - _outgoingOffset);
        var first = _outgoingOffset == 0;
        var more = _outgoingOffset + size < flight.Length;
        var flags = (first ? EapTlsBits.Length : EapTlsBits.None) | (more ? EapTlsBits.More : EapTlsBits.None);
        var message = new EapTlsMessage(flags, first ? (uint)flight.Length : null, flight.AsMemory(_outgoingOffset, size));
        _outgoingOffset += size;
        if (!more)
        {
            _outgoing = null;
        }

        FragmentsSent++;
        return EapPacket.Response(identifier, EapType.Tls, message.ToBytes());
    }

    private static EapPacket Ack(byte identifier) => EapPacket.Response(identifier, EapType.Tls, EapTlsMessage.Acknowledgement.ToBytes());

    private byte[] Drain()
    {
        var available = _tls!.GetAvailableOutputBytes();
        var output = new byte[available];
        if (available > 0)
        {
            _tls.ReadOutput(output, 0, available);
        }

        return output;
    }

    /// <summary>The BouncyCastle client of the camera.</summary>
    private sealed class PeerClient(SupplicantOptions options) : DefaultTlsClient(new BcTlsCrypto(new SecureRandom()))
    {
        public bool HandshakeComplete { get; private set; }

        public short? AlertReceived { get; private set; }

        public short? AlertRaised { get; private set; }

        public TlsClientContext Context => m_context;

        public ProtocolVersion? Version => m_context?.ServerVersion;

        public int? CipherSuite => HandshakeComplete ? m_context.SecurityParameters.CipherSuite : null;

        public SupplicantOptions Options => options;

        protected override ProtocolVersion[] GetSupportedVersions() => options.Versions;

        protected override IList<SignatureAndHashAlgorithm> GetSupportedSignatureAlgorithms() =>
            options.SignatureAlgorithms ?? base.GetSupportedSignatureAlgorithms();

        protected override int[] GetSupportedCipherSuites() =>
            options.CipherSuites is { } suites ? TlsUtilities.GetSupportedCipherSuites(Crypto, suites) : base.GetSupportedCipherSuites();

        public override TlsAuthentication GetAuthentication() => new Authentication(this);

        public byte[]? Msk { get; private set; }

        public override void NotifyHandshakeComplete()
        {
            base.NotifyHandshakeComplete();
            var tls13 = m_context.ServerVersion.Equals(ProtocolVersion.TLSv13);
            var material = tls13
                ? m_context.ExportKeyingMaterial("EXPORTER_EAP_TLS_Key_Material", [EapType.Tls], 128)
                : m_context.ExportKeyingMaterial("client EAP encryption", null, 128);
            Msk = material[..64];
            HandshakeComplete = true;
        }

        public override void NotifyAlertRaised(short alertLevel, short alertDescription, string message, Exception cause) => AlertRaised ??= alertDescription;

        public override void NotifyAlertReceived(short alertLevel, short alertDescription) => AlertReceived ??= alertDescription;
    }

    private sealed class Authentication(PeerClient client) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain.IsEmpty)
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            // The server's leaf must be signed by the trusted CA (the camera trusts the OADM CA through the PKI task).
            var crypto = (BcTlsCrypto)client.Crypto;
            var leaf = new Org.BouncyCastle.X509.X509Certificate(chain.GetCertificateAt(0).GetEncoded());
            var ca = new Org.BouncyCastle.X509.X509Certificate(client.Options.TrustedCa);
            try
            {
                leaf.Verify(ca.GetPublicKey());
            }
            catch (Exception)
            {
                throw new TlsFatalAlert(AlertDescription.unknown_ca);
            }

            _ = crypto;
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest)
        {
            var certificate = client.Options.Certificate;
            if (certificate is null)
            {
                return null;
            }

            var context = client.Context;
            var crypto = (BcTlsCrypto)client.Crypto;
            var key = PrivateKeyFactory.CreateKey(certificate.Pkcs8);
            var tls13 = TlsUtilities.IsTlsV13(context.ServerVersion);
            var entries = certificate.Chain.Select(der => (TlsCertificate)crypto.CreateCertificate(der)).ToArray();
            var bcCertificate = tls13
                ? new Certificate(certificateRequest.GetCertificateRequestContext(), [.. entries.Select(e => new CertificateEntry(e, null))])
                : new Certificate(entries);
            SignatureAndHashAlgorithm? algorithm = null;
            if (TlsUtilities.IsSignatureAlgorithmsExtensionAllowed(context.ServerVersion))
            {
                var signature = certificate.Kind == Protocol.Tls.ServerKeyKind.Ecdsa ? SignatureAlgorithm.ecdsa : SignatureAlgorithm.rsa;
                algorithm = certificateRequest.SupportedSignatureAlgorithms?.FirstOrDefault(a =>
                    tls13
                        ? (certificate.Kind == Protocol.Tls.ServerKeyKind.Ecdsa ? a.Equals(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)) : a.Equals(SignatureAndHashAlgorithm.rsa_pss_rsae_sha256))
                        : a.Signature == signature && a.Hash == HashAlgorithm.sha256)
                    ?? throw new TlsFatalAlert(AlertDescription.handshake_failure);
            }

            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), crypto, key, bcCertificate, algorithm);
        }
    }
}

/// <summary>Little helpers for EAP bytes.</summary>
public static class EapBytes
{
    public static ushort Length(ReadOnlySpan<byte> eap) => BinaryPrimitives.ReadUInt16BigEndian(eap[2..]);
}
