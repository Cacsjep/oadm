using System.Security.Cryptography.X509Certificates;

using Opc.Ua;
using Opc.Ua.Security.Certificates;

using CertificateValidationOptions = Opc.Ua.Security.Certificates.CertificateValidationOptions;
using CertificateValidationResult = Opc.Ua.CertificateValidationResult;

namespace Oadm.Plugins.OpcUa;

/// <summary>
/// The stack's certificate manager without stores, so that nothing touches the disk: it holds the recipient's certificate in memory
/// and decides about the server's. A real <c>CertificateManager</c> cannot do this: its trust lists live in stores, and it never
/// accepts a certificate whose CA it does not know, which Allow untrusted must. The stack needs only the two checks and the
/// certificate of a Certificate login; everything else belongs to a stored trust list or a server and is empty here.
/// </summary>
internal sealed class OpcUaCertificates(Uri endpoint, bool allowUntrusted, IReadOnlyList<X509Certificate2> trusted, Certificate? own)
    : ICertificateManager, ICertificateProvider, IDisposable
{
    private readonly Certificate? _own = own?.AddRef();

    public ICertificateProvider CertificateProvider => this;

    public bool SendCertificateChain => false;

    public IReadOnlyCollection<TrustListIdentifier> TrustLists => [];

    public IObservable<CertificateChangeEvent> CertificateChanges { get; } = new NoChanges();

    public Func<Certificate, ServiceResult, bool>? AcceptError { get; set; }

    public Task<CertificateValidationResult> ValidateAsync(
        CertificateCollection chain, TrustListIdentifier? trustList = null, CertificateValidationOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return Task.FromResult(Check(chain[0], chain.Skip(1)));
    }

    public Task<CertificateValidationResult> ValidateAsync(Certificate certificate, TrustListIdentifier? trustList = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Task.FromResult(Check(certificate, []));
    }

    public Certificate? TryGetPrivateKeyCertificate(string thumbprint) =>
        string.Equals(_own?.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase) ? _own?.AddRef() : null;

    public ValueTask<Certificate?> GetPrivateKeyCertificateAsync(
        CertificateIdentifier identifier, ICertificatePasswordProvider? passwordProvider = null, string? applicationUri = null, CancellationToken ct = default) =>
        new(_own?.AddRef());

    public CertificateEntryCollection SnapshotApplicationCertificates() => new([]);

    public CertificateEntry? AcquireApplicationCertificateByType(NodeId certificateType) => null;

    public CertificateEntry? AcquireApplicationCertificateBySecurityPolicy(string securityPolicyUri) => null;

    public Task<bool> GetIssuersAsync(Certificate certificate, IList<CertificateIssuerReference> issuers, CancellationToken ct = default) => Task.FromResult(false);

    public void RegisterTrustList(TrustListIdentifier trustList, string trustedStorePath, string? issuerStorePath = null) => throw new NotSupportedException();

    public ICertificateStore OpenTrustedStore(TrustListIdentifier trustList) => throw new NotSupportedException();

    public ICertificateStore? OpenIssuerStore(TrustListIdentifier trustList) => null;

    public Task<ITrustListTransaction> BeginUpdateAsync(TrustListIdentifier trustList, CancellationToken ct = default) => throw new NotSupportedException();

    public void NotifyTrustListChanged(TrustListIdentifier trustList, bool trustChanged, bool crlChanged)
    {
    }

    public Task UpdateApplicationCertificateAsync(NodeId certificateType, Certificate newCertificate, CertificateCollection? issuerChain = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RejectCertificateAsync(CertificateCollection chain, CancellationToken ct = default) => Task.CompletedTask;

    public Task ReloadApplicationCertificatesAsync(SecurityConfiguration securityConfiguration, string? applicationUri = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateAsync(SecurityConfiguration securityConfiguration, string? applicationUri = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task FlushRejectedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<TrustListData> ReadTrustListAsync(TrustListIdentifier trustList, TrustListMasks masks = TrustListMasks.All, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task WriteTrustListAsync(TrustListIdentifier trustList, TrustListData data, TrustListMasks masks = TrustListMasks.All, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public void Dispose() => _own?.Dispose();

    /// <summary>
    /// Allow untrusted accepts. The imported certificate itself is trusted while it is valid. Otherwise a chain up to the chosen CA or, without
    /// one, the roots of the operating system, and the certificate must name the host: a CA vouches for many servers.
    /// </summary>
    private CertificateValidationResult Check(Certificate leaf, IEnumerable<Certificate> rest)
    {
        if (allowUntrusted)
        {
            return CertificateValidationResult.Success;
        }

        if (trusted.Any(t => string.Equals(t.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase)))
        {
            var now = DateTime.Now;
            return now >= leaf.NotBefore && now <= leaf.NotAfter ? CertificateValidationResult.Success : Refused(StatusCodes.BadCertificateTimeInvalid);
        }

        using var server = leaf.AsX509Certificate2();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (trusted.Count > 0)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(trusted.ToArray());
        }

        var sent = rest.Select(c => c.AsX509Certificate2()).ToArray();
        try
        {
            chain.ChainPolicy.ExtraStore.AddRange(sent);
            var built = chain.Build(server);
            var expired = chain.ChainStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.NotTimeValid));
            foreach (var element in chain.ChainElements)
            {
                element.Certificate.Dispose();
            }

            return !built ? Refused(expired ? StatusCodes.BadCertificateTimeInvalid : StatusCodes.BadCertificateUntrusted)
                : X509Utils.DoesUrlMatchCertificate(leaf, endpoint) ? CertificateValidationResult.Success
                : Refused(StatusCodes.BadCertificateHostNameInvalid);
        }
        finally
        {
            foreach (var certificate in sent)
            {
                certificate.Dispose();
            }
        }
    }

    private static CertificateValidationResult Refused(StatusCode status) =>
        new(false, status, [new ServiceResult(status)], false);

    private sealed class NoChanges : IObservable<CertificateChangeEvent>, IDisposable
    {
        public IDisposable Subscribe(IObserver<CertificateChangeEvent> observer) => this;

        public void Dispose()
        {
        }
    }
}
