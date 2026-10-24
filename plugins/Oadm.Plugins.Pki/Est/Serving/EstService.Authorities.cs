using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// "Sign in with" (docs/spec/pki.md "Services select CAs"): which of PKI's CAs a camera may sign in with. EST tells PKI what
/// it uses (<see cref="IDeviceCertificateAuthority.SetUsedBy"/>): the chosen CAs that PKI still holds and the OADM CA it
/// signs with.
/// </summary>
public sealed partial class EstService
{
    private readonly Lock _usedByGate = new();
    private IDeviceCertificateAuthority? _watched;

    /// <summary>
    /// The CAs a camera may sign in with: the Axis device ID, the OADM CA and the previous OADM CAs PKI holds (a PKI without
    /// the list: the Axis device ID and the OADM CA).
    /// </summary>
    internal static IReadOnlyList<SignInChoice> SignInChoices(IDeviceCertificateAuthority? ca)
    {
        var authorities = ca?.Authorities ?? [];
        if (authorities.Count == 0)
        {
            return ca is null
                ? [new(CertificateAuthorityInfo.AxisDeviceIdId, SignInNames.AxisDeviceId), new(CertificateAuthorityInfo.OadmId, SignInNames.Oadm + SignInNames.ForRenewals)]
                : [new(CertificateAuthorityInfo.AxisDeviceIdId, SignInNames.AxisDeviceId), new(CertificateAuthorityInfo.OadmId, ChainName(ca) + SignInNames.ForRenewals)];
        }

        return
        [
            .. authorities.Where(a => a.Kind is CertificateAuthorityKind.AxisDeviceId or CertificateAuthorityKind.Oadm or CertificateAuthorityKind.PreviousOadm)
                .OrderBy(a => a.Kind switch { CertificateAuthorityKind.AxisDeviceId => 0, CertificateAuthorityKind.Oadm => 1, _ => 2 })
                .Select(a => new SignInChoice(a.Id, a.Kind switch
                {
                    CertificateAuthorityKind.AxisDeviceId => SignInNames.AxisDeviceId,
                    _ => (string.IsNullOrWhiteSpace(a.Name) ? SignInNames.Oadm : a.Name) + SignInNames.ForRenewals,
                })),
        ];
    }

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

        var available = SignInChoices(ca).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = _store.Config.SignInWith.Where(available.Contains).Append(CertificateAuthorityInfo.OadmId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
        PublishState(); // the page's choices follow PKI's list
    }
}
