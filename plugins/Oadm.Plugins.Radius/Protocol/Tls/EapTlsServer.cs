using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Oadm.Plugins.Radius.Protocol.Tls;

/// <summary>
/// The BouncyCastle TLS server of one EAP-TLS session (docs/spec/radius-server.md "TLS library"): versions and cipher suites
/// from <see cref="TlsSettings"/>, a client certificate required and handed to the validator, the alerts of both directions
/// recorded with their numbers, no resumption and no tickets (RFC 9190 2.1.2, 2.1.3 are not offered: no session cache, BC
/// sends no NewSessionTicket), no early data and no post-handshake authentication (RFC 9190 2.1).
/// </summary>
internal sealed class EapTlsServer : DefaultTlsServer
{
    private readonly TlsSettings _settings;
    private readonly TlsServerIdentity _identity;
    private readonly Func<IReadOnlyList<byte[]>, TlsVersion, ClientCertificateVerdict> _check;

    public EapTlsServer(BcTlsCrypto crypto, TlsSettings settings, TlsServerIdentity identity, Func<IReadOnlyList<byte[]>, TlsVersion, ClientCertificateVerdict> check)
        : base(crypto)
    {
        _settings = settings;
        _identity = identity;
        _check = check;
    }

    /// <summary>The first alert of the session (raised by OADM or received from the device), or null.</summary>
    public TlsAlert? Alert { get; private set; }

    /// <summary>The validator's verdict once the client certificate arrived.</summary>
    public ClientCertificateVerdict? Verdict { get; private set; }

    /// <summary>The certificates the device sent (leaf first), once they arrived.</summary>
    public IReadOnlyList<byte[]>? ClientChain { get; private set; }

    /// <summary>True from the moment the client certificate arrived until the validator answered.</summary>
    public bool CheckingCertificate { get; private set; }

    /// <summary>True once the client certificate message was processed (passed or not).</summary>
    public bool ClientCertificateSeen { get; private set; }

    public bool HandshakeComplete { get; private set; }

    /// <summary>Called right before the validator runs (the session shows "Checking client certificate").</summary>
    public Action? CheckStarting { get; init; }

    public TlsServerContext Context => m_context;

    /// <summary>The negotiated version (valid after the server hello).</summary>
    public TlsVersion? NegotiatedVersion => m_context?.ServerVersion is { } v ? ToVersion(v) : null;

    /// <summary>Only the versions with a checked cipher suite the server key can use: no version is offered that could only fail.</summary>
    protected override ProtocolVersion[] GetSupportedVersions() =>
        [.. _settings.VersionsFor(_identity.Kind).Select(ToProtocolVersion)];

    protected override int[] GetSupportedCipherSuites() =>
        TlsUtilities.GetSupportedCipherSuites(Crypto, [.. _settings.Offered(_identity.Kind)]);

    /// <summary>The minimum version is TLS 1.0 or 1.1: SHA-1 signatures are allowed for those old devices only.</summary>
    private bool Legacy => _settings.MinimumVersion < TlsVersion.Tls12;

    /// <summary>
    /// The signature schemes OADM accepts from the device (CertificateVerify and its certificates): ECDSA, RSA-PSS, EdDSA
    /// and RSA PKCS#1 with SHA-256 or stronger; never SHA-224, and SHA-1 only when the minimum is TLS 1.0 or 1.1
    /// (RFC 9155 2, RFC 8446 4.2.3).
    /// </summary>
    internal static IList<SignatureAndHashAlgorithm> AcceptedSignatures(bool legacy)
    {
        List<SignatureAndHashAlgorithm> list =
        [
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha384, SignatureAlgorithm.ecdsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha512, SignatureAlgorithm.ecdsa),
            SignatureAndHashAlgorithm.ed25519,
            SignatureAndHashAlgorithm.ed448,
            SignatureAndHashAlgorithm.rsa_pss_rsae_sha256,
            SignatureAndHashAlgorithm.rsa_pss_rsae_sha384,
            SignatureAndHashAlgorithm.rsa_pss_rsae_sha512,
            SignatureAndHashAlgorithm.rsa_pss_pss_sha256,
            SignatureAndHashAlgorithm.rsa_pss_pss_sha384,
            SignatureAndHashAlgorithm.rsa_pss_pss_sha512,
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.rsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha384, SignatureAlgorithm.rsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha512, SignatureAlgorithm.rsa),
        ];
        if (legacy)
        {
            list.Add(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.ecdsa));
            list.Add(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.rsa));
        }

        return list;
    }

    public override CertificateRequest GetCertificateRequest()
    {
        var version = m_context.ServerVersion;
        var algorithms = TlsUtilities.IsSignatureAlgorithmsExtensionAllowed(version) ? AcceptedSignatures(Legacy) : null;
        if (TlsUtilities.IsTlsV13(version))
        {
            return new CertificateRequest(TlsUtilities.EmptyBytes, algorithms, null, null);
        }

        return new CertificateRequest([ClientCertificateType.ecdsa_sign, ClientCertificateType.rsa_sign], algorithms, null);
    }

    /// <summary>
    /// The device's certificate (RFC 5216 2.1.1, RFC 9190 2.1.1): none refuses it (TLS 1.3: alert 116 certificate required,
    /// RFC 8446 4.4.2.4; earlier: 40 handshake failure, RFC 5246 7.4.6), the reason names the alert sent; else the validator
    /// decides and a refusal raises its alert.
    /// </summary>
    public override void NotifyClientCertificate(Certificate clientCertificate)
    {
        ClientCertificateSeen = true;
        var version = NegotiatedVersion ?? TlsVersion.Tls12;
        if (clientCertificate is null || clientCertificate.IsEmpty)
        {
            var alert = (byte)(version == TlsVersion.Tls13 ? AlertDescription.certificate_required : AlertDescription.handshake_failure);
            Verdict = ClientCertificateVerdict.Refuse(TlsAlertTexts.NoClientCertificate(alert), alert);
            throw new TlsFatalAlert(Verdict.Alert);
        }

        var chain = new List<byte[]>(clientCertificate.Length);
        for (var i = 0; i < clientCertificate.Length; i++)
        {
            chain.Add(clientCertificate.GetCertificateAt(i).GetEncoded());
        }

        ClientChain = chain;
        CheckingCertificate = true;
        CheckStarting?.Invoke();
        try
        {
            Verdict = _check(chain, version);
        }
#pragma warning disable CA1031 // fail closed: a broken validator refuses the device
        catch (Exception)
#pragma warning restore CA1031
        {
            Verdict = ClientCertificateVerdict.Refuse(TlsAlertTexts.Text((byte)AlertDescription.internal_error, TlsAlertDirection.SentByOadm), (byte)AlertDescription.internal_error);
        }
        finally
        {
            CheckingCertificate = false;
        }

        if (!Verdict.Accepted)
        {
            throw new TlsFatalAlert(Verdict.Alert);
        }
    }

    public override TlsCredentials GetCredentials()
    {
        var version = m_context.ServerVersion;
        var tls13 = TlsUtilities.IsTlsV13(version);
        SignatureAndHashAlgorithm? algorithm = null;
        if (TlsUtilities.IsSignatureAlgorithmsExtensionAllowed(version))
        {
            algorithm = ChooseSignature(m_context.SecurityParameters.ClientSigAlgs, tls13)
                ?? throw new TlsFatalAlert(AlertDescription.handshake_failure);
        }

        var crypto = (BcTlsCrypto)Crypto;
        var certificates = _identity.Chain.Select(der => (TlsCertificate)crypto.CreateCertificate(der)).ToArray();
        var certificate = tls13
            ? new Certificate(TlsUtilities.EmptyBytes, [.. certificates.Select(c => new CertificateEntry(c, null))])
            : new Certificate(certificates);
        return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), crypto, _identity.PrivateKey, certificate, algorithm);
    }

    public override void NotifyAlertRaised(short alertLevel, short alertDescription, string message, Exception cause)
    {
        Alert ??= new TlsAlert((byte)alertDescription, TlsAlertDirection.SentByOadm);
    }

    public override void NotifyAlertReceived(short alertLevel, short alertDescription)
    {
        // The first alert counts; a close_notify only until a real one arrives (user_canceled is followed by close_notify).
        if (Alert is null || (Alert.Value.Description == AlertDescription.close_notify && alertDescription != AlertDescription.close_notify))
        {
            Alert = new TlsAlert((byte)alertDescription, TlsAlertDirection.SentByDevice);
        }
    }

    /// <summary>
    /// The handshake is done. BouncyCastle exports keying material only here, so the MSK is derived now (RFC 5216 2.3,
    /// RFC 9190 2.3): TLS 1.3 TLS-Exporter("EXPORTER_EAP_TLS_Key_Material", 0x0D, 128); TLS 1.2 and earlier
    /// TLS-PRF-128(master_secret, "client EAP encryption", client.random || server.random), which is the RFC 5705 exporter
    /// without context. MSK = Key_Material(0, 63).
    /// </summary>
    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        var material = NegotiatedVersion == TlsVersion.Tls13
            ? m_context.ExportKeyingMaterial(Tls13KeyLabel, [EapTlsType], 128)
            : m_context.ExportKeyingMaterial(Tls12KeyLabel, null, 128);
        Msk = material[..64];
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(material);
        HandshakeComplete = true;
    }

    /// <summary>The MSK (64 bytes) once the handshake completed; the session takes and clears it.</summary>
    public byte[]? Msk { get; set; }

    private const string Tls12KeyLabel = "client EAP encryption";
    private const string Tls13KeyLabel = "EXPORTER_EAP_TLS_Key_Material";
    private const byte EapTlsType = 13;

    internal static ProtocolVersion ToProtocolVersion(TlsVersion version) => version switch
    {
        TlsVersion.Tls10 => ProtocolVersion.TLSv10,
        TlsVersion.Tls11 => ProtocolVersion.TLSv11,
        TlsVersion.Tls12 => ProtocolVersion.TLSv12,
        _ => ProtocolVersion.TLSv13,
    };

    internal static TlsVersion? ToVersion(ProtocolVersion version) =>
        version.Equals(ProtocolVersion.TLSv13) ? TlsVersion.Tls13
        : version.Equals(ProtocolVersion.TLSv12) ? TlsVersion.Tls12
        : version.Equals(ProtocolVersion.TLSv11) ? TlsVersion.Tls11
        : version.Equals(ProtocolVersion.TLSv10) ? TlsVersion.Tls10
        : null;

    /// <summary>
    /// The signature scheme for the server key from the client's list (RFC 8446 4.2.3, RFC 5246 7.4.1.4.1): ECDSA with the
    /// hash of the curve first (TLS 1.3 binds it), RSA-PSS before PKCS#1 (TLS 1.3 allows only PSS). SHA-1 only when the
    /// minimum is TLS 1.0 or 1.1 (RFC 9155 2: TLS 1.2 servers MUST NOT sign with SHA-1); then a TLS 1.2 client without a
    /// list gets the RFC 5246 7.4.1.4.1 default SHA-1, else it is refused.
    /// </summary>
    private SignatureAndHashAlgorithm? ChooseSignature(IList<SignatureAndHashAlgorithm>? offered, bool tls13)
    {
        List<SignatureAndHashAlgorithm> preferred = [];
        short[] hashes = Legacy
            ? [HashAlgorithm.sha256, HashAlgorithm.sha384, HashAlgorithm.sha512, HashAlgorithm.sha1]
            : [HashAlgorithm.sha256, HashAlgorithm.sha384, HashAlgorithm.sha512];
        if (_identity.Kind == ServerKeyKind.Ecdsa)
        {
            var curveHash = _identity.KeyBits switch { <= 256 => HashAlgorithm.sha256, <= 384 => HashAlgorithm.sha384, _ => HashAlgorithm.sha512 };
            preferred.Add(SignatureAndHashAlgorithm.GetInstance(curveHash, SignatureAlgorithm.ecdsa));
            if (!tls13)
            {
                preferred.AddRange([.. hashes.Where(h => h != curveHash).Select(h => SignatureAndHashAlgorithm.GetInstance(h, SignatureAlgorithm.ecdsa))]);
            }
        }
        else
        {
            preferred.AddRange([SignatureAndHashAlgorithm.rsa_pss_rsae_sha256, SignatureAndHashAlgorithm.rsa_pss_rsae_sha384, SignatureAndHashAlgorithm.rsa_pss_rsae_sha512]);
            if (!tls13)
            {
                preferred.AddRange([.. hashes.Select(h => SignatureAndHashAlgorithm.GetInstance(h, SignatureAlgorithm.rsa))]);
            }
        }

        if (offered is null || offered.Count == 0)
        {
            return tls13 || !Legacy ? null : SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, _identity.Kind == ServerKeyKind.Ecdsa ? SignatureAlgorithm.ecdsa : SignatureAlgorithm.rsa);
        }

        return preferred.FirstOrDefault(p => offered.Any(o => o.Equals(p)));
    }
}
