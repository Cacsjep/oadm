using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki;

/// <summary>Plain texts of the Certificates tab, the same on the server and the page.</summary>
public static class PkiCertificateTexts
{
    /// <summary>A serial that is not listed, or a certificate of a device outside the user's group (the same answer).</summary>
    public const string NotListed = IssuedCertificateMessages.NotListed;

    /// <summary>A certificate listed before OADM kept a copy of each one (4.1 and older).</summary>
    public const string NoCopy = IssuedCertificateMessages.NoCopy;

    public const string ChooseOne = "Choose at least one certificate.";
}

/// <summary>
/// The list of issued certificates and the revocation list of the page (tab Certificates) and of the CA service
/// (<see cref="IDeviceCertificateAuthority"/>): one page per request, newest first, the rows of a user limited to a device
/// group only of the group's devices; revoking for users with all devices.
/// </summary>
public sealed partial class PkiService
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>>? _systemDevices;
    private readonly Lock _knownGate = new();
    private (string Key, IReadOnlyList<X509Certificate2> Certificates) _known = (string.Empty, []);
    private int _certificatesEventPending;

    /// <summary>
    /// Reads a device's certificate list (alias and PEM, read only) for Revoke all of this device; null = no device access
    /// (tests, a host without it). Set by the plugin.
    /// </summary>
    public Func<IDeviceInfo, CancellationToken, Task<IReadOnlyList<(string Alias, string Pem)>>>? DeviceCertificateReader { get; set; }

    /// <summary>OADM PRO is active (the policy list shows the kinds of the PRO servers); true without a host check (tests, in process).</summary>
    public Func<bool> IsPro { get; set; } = () => true;

    /// <summary>The latest end of the active and the previous CAs: no certificate they signed lasts longer (null without a CA).</summary>
    public DateTime? LatestCaEnd()
    {
        DateTime? latest = null;
        foreach (var ca in KnownCaCertificates())
        {
            var end = ca.NotAfter.ToUniversalTime();
            if (latest is null || end > latest)
            {
                latest = end;
            }
        }

        return latest;
    }

    /// <summary>
    /// The certificates of the active and the previous CAs (public parts), for finding OADM certificates on devices. Built
    /// again when the CAs change; never disposed (a reader may still use the old list).
    /// </summary>
    public IReadOnlyList<X509Certificate2> KnownCaCertificates()
    {
        var ca = _ca;
        var previous = _previous;
        // CAs made here with Create CA too (their key never left OADM); imported CAs stay out (their key may issue elsewhere).
        var created = _imported.Where(i => i.Created).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issuing = _importedMaterial.Values.Where(m => created.Contains(m.Id)).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        var key = string.Join(',', (ca is null ? [] : new[] { ca.Id }).Concat(previous.Select(p => p.Id)).Concat(issuing.Select(m => m.Id)));
        lock (_knownGate)
        {
            if (key == _known.Key)
            {
                return _known.Certificates;
            }

            var list = new List<X509Certificate2>();
            if (ca is not null)
            {
                list.Add(X509CertificateLoader.LoadCertificate(ca.Certificate.RawData));
            }

            foreach (var entry in previous)
            {
                try
                {
                    list.AddRange(CaCertificates.ReadPemCertificates(entry.CertificatePem).Take(1));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    // an unreadable previous CA finds nothing
                }
            }

            list.AddRange(issuing.Select(m => X509CertificateLoader.LoadCertificate(m.Certificate.RawData)));

            _known = (key, list);
            return list;
        }
    }

    /// <summary>
    /// Adds the OADM certificates in a device's certificate list (read by a PKI task, the View certificates window or the
    /// one pass over every device) that the list does not hold yet. Returns how many were added.
    /// </summary>
    public Task<int> ImportFoundAsync(IDeviceInfo device, IEnumerable<(string Alias, string Pem)> certificates, CancellationToken ct) =>
        _issued.ImportFoundAsync(device, certificates, KnownCaCertificates(), ct);

    /// <summary>In-process callers: one page over every device.</summary>
    internal async Task<CertificatesReply> GetCertificatesForEveryDeviceAsync(CertificatesRequest request, CancellationToken ct) =>
        GetCertificates(request, await SystemDevicesAsync(ct).ConfigureAwait(false), onlyListed: false);

    /// <summary>The CA service's query (every device; <see cref="IssuedCertificateQuery.DeviceIds"/> narrows it).</summary>
    internal async Task<IssuedCertificatePage> QueryIssuedAsync(IssuedCertificateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var devices = await SystemDevicesAsync(ct).ConfigureAwait(false);
        // Every stored certificate (also pending ones and those of cameras OADM no longer manages), not only the page's list.
        return _issued.Query(query, devices.ToDictionary(d => d.Id), onlyListedDevices: false, WarningDaysFor(devices), currentOnly: false);
    }

    /// <summary>
    /// One listed certificate in plain names, by its serial number (a device certificate) or its row id (a CA, a server, a
    /// certificate created, signed or imported here). A certificate of a device outside <paramref name="view"/>, and every row
    /// that is no device certificate for a user limited to a group, answers like one that is not listed.
    /// </summary>
    /// <param name="viewDevices">The devices of <paramref name="view"/> (a camera found by its serial); none: only by device id.</param>
    internal async Task<CertificateDetailsReply> GetCertificateAsync(CertificateDetailsRequest request, ViewedSet? view, CancellationToken ct, IReadOnlyList<IDeviceInfo>? viewDevices = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CertificateRowDer(request.SerialNumber) is { } rowDer)
        {
            if (view is not null || rowDer.Der is null)
            {
                return new CertificateDetailsReply { Error = PkiCertificateTexts.NotListed };
            }

            try
            {
                return new CertificateDetailsReply { Certificate = CertificateDescriber.Describe(rowDer.Der), Revocation = rowDer.Serial is { } serial ? _issued.FindRevocation(serial) : null };
            }
            catch (CryptographicException)
            {
                return new CertificateDetailsReply { Error = PkiCertificateTexts.NoCopy };
            }
        }

        if (string.IsNullOrWhiteSpace(request.SerialNumber) || _issued.Find(request.SerialNumber) is not { } entry)
        {
            return new CertificateDetailsReply { Error = PkiCertificateTexts.NotListed };
        }

        // The camera as the list finds it: by its serial (also a camera added again under a new id), else by the device id.
        if (view is not null)
        {
            var deviceId = entry.DeviceSerial is { Length: > 0 } camera
                ? (viewDevices ?? []).FirstOrDefault(d => string.Equals(d.Serial, camera, StringComparison.OrdinalIgnoreCase))?.Id
                : entry.DeviceId;
            if (!view.Contains(deviceId))
            {
                return new CertificateDetailsReply { Error = PkiCertificateTexts.NotListed };
            }
        }

        var der = await _issued.GetDerAsync(entry.SerialNumber, ct).ConfigureAwait(false);
        if (der is null)
        {
            return new CertificateDetailsReply { Error = PkiCertificateTexts.NoCopy };
        }

        try
        {
            return new CertificateDetailsReply { Certificate = CertificateDescriber.Describe(der), Revocation = _issued.FindRevocation(entry.SerialNumber) };
        }
        catch (CryptographicException)
        {
            return new CertificateDetailsReply { Error = PkiCertificateTexts.NoCopy };
        }
    }

    /// <summary>The CA service's revoke.</summary>
    internal Task<RevokeResult> RevokeSerialsAsync(IReadOnlyCollection<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct) =>
        _issued.RevokeAsync(serialNumbers, reason, userName, ct);

    /// <summary>Every managed device; none for a host (or test) without a device list.</summary>
    internal async Task<IReadOnlyList<IDeviceInfo>> SystemDevicesAsync(CancellationToken ct)
    {
        if (_systemDevices is null)
        {
            return [];
        }

        try
        {
            return await _systemDevices(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return []; // a host (or test) without a device list
        }
    }

    /// <summary>Every page reads its page again: at most one event per <see cref="PkiOptions.CertificatesEventDelay"/>.</summary>
    private void OnIssuedChanged(object? sender, EventArgs e)
    {
        if (_events is null || Interlocked.Exchange(ref _certificatesEventPending, 1) == 1)
        {
            return;
        }

        var delay = _options.CertificatesEventDelay;
        if (delay <= TimeSpan.Zero)
        {
            Volatile.Write(ref _certificatesEventPending, 0);
            PublishCertificatesChanged();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _options.Time, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Volatile.Write(ref _certificatesEventPending, 0);
            PublishCertificatesChanged();
        }, CancellationToken.None);
    }

    private void PublishCertificatesChanged()
    {
        try
        {
            _events?.Publish(PkiMethods.CertificatesTopic, "{}", PluginEventAudience.AllDevices);
            foreach (var key in Viewers.Keys())
            {
                _events?.Publish(PkiMethods.CertificatesTopic, "{}", PluginEventAudience.DeviceScope(key));
            }
        }
#pragma warning disable CA1031 // Events are best effort; pages read again when they open.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPublishFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: the server offers no plugin records; the issued certificates and the revocation list are kept in memory only")]
    private partial void LogNoRecords();
}
