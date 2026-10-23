using Oadm.Plugins.Radius.Protocol.Tls;

namespace Oadm.Plugins.Radius.Tests.Support;

/// <summary>
/// The validator of the tests: accepts a leaf signed by the test OADM CA (or by an intermediate it sent that the CA
/// signed), refuses others with alert 48; <see cref="Decide"/> replaces that rule.
/// </summary>
public sealed class TestValidator : IClientCertificateValidator
{
    private readonly Lock _lock = new();

    public Func<ClientCertificateCheck, ClientCertificateVerdict>? Decide { get; set; }

    public int? VlanId { get; set; }

    public List<ClientCertificateCheck> Checks { get; } = [];

    public ClientCertificateVerdict Validate(ClientCertificateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        lock (_lock)
        {
            Checks.Add(check);
        }

        if (Decide is { } decide)
        {
            return decide(check);
        }

        return SignedByCa(check.Chain)
            ? ClientCertificateVerdict.Accept(VlanId, tag: "device")
            : ClientCertificateVerdict.Refuse("Client certificate not issued by a trusted CA", 48);
    }

    public static bool SignedByCa(IReadOnlyList<byte[]> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var ca = new Org.BouncyCastle.X509.X509Certificate(TestPki.CaCertificate);
        var certificates = chain.Select(c => new Org.BouncyCastle.X509.X509Certificate(c)).ToList();
        try
        {
            for (var i = 0; i < certificates.Count; i++)
            {
                var issuer = i + 1 < certificates.Count && !certificates[i + 1].Equals(ca) ? certificates[i + 1] : ca;
                certificates[i].Verify(issuer.GetPublicKey());
                if (ReferenceEquals(issuer, ca))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
