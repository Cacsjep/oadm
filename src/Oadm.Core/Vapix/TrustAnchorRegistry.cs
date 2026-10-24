using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Vapix;

/// <summary>The trust anchors at one point in time: the union of every owner's set, and its version.</summary>
/// <param name="Version">Increases with every change of any set (0 = never set).</param>
/// <param name="Certificates">Distinct anchors (by thumbprint). Never modify or dispose.</param>
public sealed record TrustAnchorSnapshot(long Version, X509Certificate2Collection Certificates)
{
    public static TrustAnchorSnapshot Empty { get; } = new(0, []);

    /// <summary>The OADM CAs whose device certificates are pinned again on renewal (the PKI plugin's issuers only).</summary>
    public X509Certificate2Collection RenewalIssuers { get; init; } = [];

    public bool IsEmpty => Certificates.Count == 0;
}

/// <summary>
/// Server-wide extra trust anchors for rating device certificates (<see cref="CertificateTrustEvaluator"/>): one set per
/// owner (core plugin id), the union is trusted. Singleton in the server; core plugins reach their set through
/// <see cref="ICorePluginContext.TrustAnchors"/> (<see cref="For"/>). Changes are lazy: a device shows the new rating
/// with its next full refresh, nothing is polled at once. Thread-safe; readers get an immutable snapshot without locks.
/// </summary>
public sealed class TrustAnchorRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, X509Certificate2[]> _sets = new(StringComparer.OrdinalIgnoreCase);
    private X509Certificate2[] _issuers = [];
    private volatile TrustAnchorSnapshot _current = TrustAnchorSnapshot.Empty;
    private volatile ExpiryWarning? _expiry;

    /// <summary>The days of "expires soon" without PKI's Expiry warning policies.</summary>
    public const int DefaultExpiryWarningDays = 30;

    /// <summary>Raised after any set changed (outside the lock).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after PKI handed new expiry warning days (<see cref="ITrustAnchors.SetExpiryWarning"/>).</summary>
    public event EventHandler? ExpiryWarningChanged;

    /// <summary>
    /// The days before its end that <paramref name="device"/>'s certificate counts as "expires soon" (the device grid's chip):
    /// PKI's Expiry warning policies, else <see cref="DefaultExpiryWarningDays"/>. A failing lookup counts as the default.
    /// </summary>
    public int ExpiryWarningDays(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_expiry is not { } expiry)
        {
            return DefaultExpiryWarningDays;
        }

        try
        {
            return expiry.DaysFor(device) is var days and > 0 ? days : DefaultExpiryWarningDays;
        }
#pragma warning disable CA1031 // A plugin's lookup never breaks the device list.
        catch (Exception)
#pragma warning restore CA1031
        {
            return DefaultExpiryWarningDays;
        }
    }

    /// <summary>The days of a new Compliance certificate check (PKI's, else <see cref="DefaultExpiryWarningDays"/>).</summary>
    public int ExpiryDefaultDays => _expiry is { DefaultDays: > 0 } expiry ? expiry.DefaultDays : DefaultExpiryWarningDays;

    /// <summary>
    /// Replaces the expiry warning days of <paramref name="ownerId"/> (<see cref="ITrustAnchors.SetExpiryWarning"/>); only
    /// <see cref="RenewalOwner"/>'s (PKI) are used.
    /// </summary>
    public void SetExpiryWarning(string ownerId, Func<IDeviceInfo, int>? daysFor, int defaultDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (!string.Equals(ownerId, RenewalOwner, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _expiry = daysFor is null ? null : new ExpiryWarning(daysFor, defaultDays);
        ExpiryWarningChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The current union of all sets.</summary>
    public TrustAnchorSnapshot Current => _current;

    /// <summary>The <see cref="ITrustAnchors"/> of one owner.</summary>
    public ITrustAnchors For(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return new OwnerAnchors(this, ownerId);
    }

    /// <summary>The only owner whose renewal issuers count: the PKI plugin (the OADM CA).</summary>
    public const string RenewalOwner = "oadm.pki";

    /// <summary>
    /// Replaces the renewal issuers of <paramref name="ownerId"/> (<see cref="ITrustAnchors.SetRenewalIssuers"/>); only
    /// <see cref="RenewalOwner"/>'s are used. Throws for undecodable certificates.
    /// </summary>
    public void SetRenewalIssuers(string ownerId, IReadOnlyList<byte[]> derCertificates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var parsed = Parse(derCertificates);
        if (!string.Equals(ownerId, RenewalOwner, StringComparison.OrdinalIgnoreCase))
        {
            return; // no other plugin may make certificates replace a pin
        }

        lock (_gate)
        {
            _issuers = [.. parsed];
            _current = _current with { Version = _current.Version + 1, RenewalIssuers = [.. _issuers] };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<X509Certificate2> Parse(IReadOnlyList<byte[]> derCertificates)
    {
        ArgumentNullException.ThrowIfNull(derCertificates);
        var parsed = new List<X509Certificate2>(derCertificates.Count);
        foreach (var der in derCertificates)
        {
            ArgumentNullException.ThrowIfNull(der);
            try
            {
                parsed.Add(X509CertificateLoader.LoadCertificate(der));
            }
            catch (CryptographicException ex)
            {
                throw new ArgumentException("A trust anchor is not a DER encoded certificate.", nameof(derCertificates), ex);
            }
        }

        return parsed;
    }

    /// <summary>Replaces the set of <paramref name="ownerId"/>; an empty list removes it. Throws for undecodable certificates.</summary>
    public void Set(string ownerId, IReadOnlyList<byte[]> derCertificates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var parsed = Parse(derCertificates);
        lock (_gate)
        {
            if (parsed.Count == 0)
            {
                if (!_sets.Remove(ownerId))
                {
                    return;
                }
            }
            else
            {
                _sets[ownerId] = [.. parsed];
            }

            var union = new X509Certificate2Collection();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var certificate in _sets.Values.SelectMany(s => s))
            {
                if (seen.Add(certificate.Thumbprint))
                {
                    union.Add(certificate);
                }
            }

            _current = new TrustAnchorSnapshot(_current.Version + 1, union) { RenewalIssuers = [.. _issuers] };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the sets of <paramref name="ownerId"/> (plugin stopped): its anchors, its renewal issuers and its expiry warning days.</summary>
    public void Remove(string ownerId)
    {
        Set(ownerId, []);
        SetRenewalIssuers(ownerId, []);
        if (string.Equals(ownerId, RenewalOwner, StringComparison.OrdinalIgnoreCase) && _expiry is not null)
        {
            SetExpiryWarning(ownerId, null, DefaultExpiryWarningDays);
        }
    }

    private sealed record ExpiryWarning(Func<IDeviceInfo, int> DaysFor, int DefaultDays);

    private sealed class OwnerAnchors(TrustAnchorRegistry registry, string ownerId) : ITrustAnchors
    {
        public void Set(IReadOnlyList<byte[]> derCertificates) => registry.Set(ownerId, derCertificates);

        public void SetRenewalIssuers(IReadOnlyList<byte[]> derCertificates) => registry.SetRenewalIssuers(ownerId, derCertificates);

        public void SetExpiryWarning(Func<IDeviceInfo, int>? daysFor, int defaultDays) => registry.SetExpiryWarning(ownerId, daysFor, defaultDays);
    }
}
