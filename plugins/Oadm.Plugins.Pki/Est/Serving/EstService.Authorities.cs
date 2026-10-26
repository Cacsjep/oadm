using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// The CAs EST uses (docs/spec/pki.md "Policies"): the CAs of PKI's Sign-in CA policies and the OADM CA it signs with. EST
/// tells PKI what it uses (<see cref="IDeviceCertificateAuthority.SetUsedBy"/>): those PKI still holds.
/// </summary>
public sealed partial class EstService
{
    private readonly Lock _usedByGate = new();
    private IDeviceCertificateAuthority? _watched;

    /// <summary>The name of the CA that issues (the OADM CA, as the PKI page names it); null without one.</summary>
    internal static string? IssuingName(IDeviceCertificateAuthority? ca) =>
        ca?.Authorities.FirstOrDefault(a => a.Kind == CertificateAuthorityKind.Oadm)?.Name is { Length: > 0 } name ? name
        : ca is { IsReady: true } ? ChainName(ca)
        : null;

    /// <summary>The OADM CA's name from its certificate (the first of the chain), else "OADM CA".</summary>
    private static string ChainName(IDeviceCertificateAuthority ca)
    {
        if (ca.Chain is not { Count: > 0 } chain)
        {
            return SignInNames.Oadm;
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(chain[0]);
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? SignInNames.Oadm : name.Trim();
        }
        catch (CryptographicException)
        {
            return SignInNames.Oadm;
        }
    }

    /// <summary>
    /// Tells PKI the CAs EST uses now (the chosen ones PKI still holds, and the OADM CA it signs with) and follows PKI's
    /// changes; a new PKI instance (PKI restarted) is watched instead of the old one.
    /// </summary>
    internal void ReportUsedBy()
    {
        var ca = _host.Authority();
        lock (_usedByGate)
        {
            if (!ReferenceEquals(ca, _watched))
            {
                if (_watched is { } old)
                {
                    old.Changed -= OnAuthorityChanged;
                }

                _watched = ca;
                if (ca is not null && !_stopping)
                {
                    ca.Changed += OnAuthorityChanged;
                }
            }
        }

        if (ca is null)
        {
            return;
        }

        // The CAs its profiles issue with are named by their issuing policies (decision 2026-10-26: no fixed OADM CA).
        var used = EstRules.Held(Rules.AllSignInCas, ca.Authorities).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ca.SetUsedBy(EstPluginInfo.PluginId, EstPluginInfo.DisplayName, _stopping ? [] : used);
    }

    private void StopReportingUsedBy()
    {
        IDeviceCertificateAuthority? watched;
        lock (_usedByGate)
        {
            watched = _watched;
            _watched = null;
        }

        if (watched is not null)
        {
            watched.Changed -= OnAuthorityChanged;
            watched.SetUsedBy(EstPluginInfo.PluginId, EstPluginInfo.DisplayName, []);
        }
    }

    private void OnAuthorityChanged(object? sender, EventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        ReportUsedBy();
        PublishState(); // the issuing CA's name follows PKI's list
    }
}
