using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Vapix;

/// <summary>The trust anchors at one point in time: the union of every owner's set, and its version.</summary>
/// <param name="Version">Increases with every change of any set (0 = never set).</param>
/// <param name="Certificates">Distinct anchors (by thumbprint). Never modify or dispose.</param>
public sealed record TrustAnchorSnapshot(long Version, X509Certificate2Collection Certificates)
{
    public static TrustAnchorSnapshot Empty { get; } = new(0, []);

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
    private volatile TrustAnchorSnapshot _current = TrustAnchorSnapshot.Empty;

    /// <summary>Raised after any set changed (outside the lock).</summary>
    public event EventHandler? Changed;

    /// <summary>The current union of all sets.</summary>
    public TrustAnchorSnapshot Current => _current;

    /// <summary>The <see cref="ITrustAnchors"/> of one owner.</summary>
    public ITrustAnchors For(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return new OwnerAnchors(this, ownerId);
    }

    /// <summary>Replaces the set of <paramref name="ownerId"/>; an empty list removes it. Throws for undecodable certificates.</summary>
    public void Set(string ownerId, IReadOnlyList<byte[]> derCertificates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
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

            _current = new TrustAnchorSnapshot(_current.Version + 1, union);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the set of <paramref name="ownerId"/> (plugin stopped).</summary>
    public void Remove(string ownerId) => Set(ownerId, []);

    private sealed class OwnerAnchors(TrustAnchorRegistry registry, string ownerId) : ITrustAnchors
    {
        public void Set(IReadOnlyList<byte[]> derCertificates) => registry.Set(ownerId, derCertificates);
    }
}
