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
    public const string NotListed = "This certificate is not in the list.";

    /// <summary>A certificate listed before OADM kept a copy of each one (4.1 and older).</summary>
    public const string NoCopy = "OADM keeps no copy of this certificate.";

    public const string ChooseOne = "Choose at least one certificate.";

    public const string UnknownReason = "Choose a reason.";
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
    /// The certificates of the active and the previous CAs (public parts), for finding OADM certificates on devices. Built
    /// again when the CAs change; never disposed (a reader may still use the old list).
    /// </summary>
    public IReadOnlyList<X509Certificate2> KnownCaCertificates()
    {
        var ca = _ca;
        var previous = _previous;
        var key = string.Join(',', (ca is null ? [] : new[] { ca.Id }).Concat(previous.Select(p => p.Id)));
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

    /// <summary>
    /// One page for the page: <paramref name="devices"/> are the caller's devices; <paramref name="onlyListed"/> for a user
    /// limited to a group (certificates of other and of removed devices are left out).
    /// </summary>
    internal CertificatesReply GetCertificates(CertificatesRequest request, IReadOnlyList<IDeviceInfo> devices, bool onlyListed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(devices);
        var byId = new Dictionary<Guid, IDeviceInfo>(devices.Count);
        foreach (var device in devices)
        {
            byId[device.Id] = device;
        }

        var limit = Math.Clamp(request.Limit, 1, PkiPluginInfo.CertificatesPageSize);
        var query = new IssuedCertificateQuery
        {
            State = request.State is { } state && Enum.IsDefined(state) ? state : null,
            Purpose = DeviceCertificatePurposes.All.Contains(request.Purpose ?? string.Empty, StringComparer.Ordinal) ? request.Purpose : null,
            Source = IssuedCertificateSources.All.Contains(request.Source ?? string.Empty, StringComparer.Ordinal) ? request.Source : null,
            Search = request.Search is { Length: > 100 } text ? text[..100] : request.Search,
            Offset = Math.Max(0, request.Offset),
            Limit = limit,
        };
        var warningDays = _config.ExpiryWarningDays;
        var page = _issued.Query(query, byId, onlyListed, warningDays);
        if (page.Rows.Count == 0 && page.Total > 0 && query.Offset > 0)
        {
            // Fewer rows than before (a filter, revoked rows): the last page.
            query = query with { Offset = (page.Total - 1) / limit * limit };
            page = _issued.Query(query, byId, onlyListed, warningDays);
        }

        return new CertificatesReply { Rows = page.Rows, Total = page.Total, Offset = query.Offset, WarningDays = warningDays };
    }

    /// <summary>In-process callers: one page over every device.</summary>
    internal async Task<CertificatesReply> GetCertificatesForEveryDeviceAsync(CertificatesRequest request, CancellationToken ct) =>
        GetCertificates(request, await SystemDevicesAsync(ct).ConfigureAwait(false), onlyListed: false);

    /// <summary>The CA service's query (every device; <see cref="IssuedCertificateQuery.DeviceIds"/> narrows it).</summary>
    internal async Task<IssuedCertificatePage> QueryIssuedAsync(IssuedCertificateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var devices = await SystemDevicesAsync(ct).ConfigureAwait(false);
        return _issued.Query(query, devices.ToDictionary(d => d.Id), onlyListedDevices: false, _config.ExpiryWarningDays);
    }

    /// <summary>
    /// One listed certificate in plain names. A certificate of a device outside <paramref name="view"/> answers like one
    /// that is not listed.
    /// </summary>
    internal async Task<CertificateDetailsReply> GetCertificateAsync(CertificateDetailsRequest request, ViewedSet? view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SerialNumber) || _issued.Find(request.SerialNumber) is not { } entry || (view is not null && !view.Contains(entry.DeviceId)))
        {
            return new CertificateDetailsReply { Error = PkiCertificateTexts.NotListed };
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

    /// <summary>Revokes the chosen certificates (users with all devices; the plugin checks the call).</summary>
    internal async Task<RevokeReply> RevokeAsync(RevokeRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Reason))
        {
            return new RevokeReply { Error = PkiCertificateTexts.UnknownReason };
        }

        var serials = request.SerialNumbers?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
        if (serials.Count == 0)
        {
            return new RevokeReply { Error = PkiCertificateTexts.ChooseOne };
        }

        if (serials.Count > PkiPluginInfo.MaxRevokeAtOnce)
        {
            return new RevokeReply { Error = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Choose at most {PkiPluginInfo.MaxRevokeAtOnce:N0} certificates.") };
        }

        return Reply(await _issued.RevokeAsync(serials, request.Reason, userName, ct).ConfigureAwait(false));
    }

    /// <summary>Revokes every certificate of a device that is still valid (Revoke all of this device).</summary>
    internal async Task<RevokeReply> RevokeDeviceAsync(RevokeDeviceRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Reason))
        {
            return new RevokeReply { Error = PkiCertificateTexts.UnknownReason };
        }

        var serials = _issued.RevocableOf(request.DeviceId);
        return serials.Count == 0
            ? new RevokeReply()
            : Reply(await _issued.RevokeAsync(serials, request.Reason, userName, ct).ConfigureAwait(false));
    }

    /// <summary>The CA service's revoke.</summary>
    internal Task<RevokeResult> RevokeSerialsAsync(IReadOnlyCollection<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct) =>
        _issued.RevokeAsync(serialNumbers, reason, userName, ct);

    private static RevokeReply Reply(RevokeResult result) =>
        new() { Revoked = result.Revoked, AlreadyRevoked = result.AlreadyRevoked, NotFound = result.NotFound };

    private async Task<IReadOnlyList<IDeviceInfo>> SystemDevicesAsync(CancellationToken ct)
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
