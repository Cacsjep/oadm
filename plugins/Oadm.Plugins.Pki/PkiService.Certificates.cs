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
    /// Reads a device's certificate list (alias and PEM, read only) for Revoke all of this device; null = no device access
    /// (tests, a host without it). Set by the plugin.
    /// </summary>
    public Func<IDeviceInfo, CancellationToken, Task<IReadOnlyList<(string Alias, string Pem)>>>? DeviceCertificateReader { get; set; }

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
    internal async Task<CertificateDetailsReply> GetCertificateAsync(CertificateDetailsRequest request, ViewedSet? view, CancellationToken ct)
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

    /// <summary>
    /// Revokes every certificate of a device that is still valid (Revoke all of this device). A device that answers has its
    /// certificate list read first (at most <see cref="PkiOptions.DeviceReadTimeout"/>), so OADM certificates on it that the
    /// list did not hold yet (from before the list) are revoked too.
    /// </summary>
    internal async Task<RevokeReply> RevokeDeviceAsync(RevokeDeviceRequest request, string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Reason))
        {
            return new RevokeReply { Error = PkiCertificateTexts.UnknownReason };
        }

        var device = await ReadDeviceCertificatesAsync(request.DeviceId, ct).ConfigureAwait(false);
        var serials = device is null ? _issued.RevocableOf(request.DeviceId) : _issued.RevocableOf(device); // also a camera added again
        return serials.Count == 0
            ? new RevokeReply()
            : Reply(await _issued.RevokeAsync(serials, request.Reason, userName, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Adds the OADM certificates on the device to the list (best effort: an offline or older device adds nothing). Returns
    /// the device when OADM manages it.
    /// </summary>
    private async Task<IDeviceInfo?> ReadDeviceCertificatesAsync(Guid deviceId, CancellationToken ct)
    {
        var device = (await SystemDevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Id == deviceId);
        if (DeviceCertificateReader is not { } read || device is not { Status: DeviceStatus.Ok } || !Tasks.PkiCompatibility.CanRunCertificateTask(device))
        {
            return device;
        }

        using var limit = new CancellationTokenSource(_options.DeviceReadTimeout, _options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            var found = await read(device, linked.Token).ConfigureAwait(false);
            await ImportFoundAsync(device, found, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The device's list is extra: revoking what the list holds goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDeviceReadFailed(device.Address, ex.Message);
        }

        return device;
    }

    /// <summary>The CA service's revoke.</summary>
    internal Task<RevokeResult> RevokeSerialsAsync(IReadOnlyCollection<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct) =>
        _issued.RevokeAsync(serialNumbers, reason, userName, ct);

    private static RevokeReply Reply(RevokeResult result) =>
        new() { Revoked = result.Revoked, AlreadyRevoked = result.AlreadyRevoked, NotFound = result.NotFound };

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "PKI: the certificates of {Device} could not be read before revoking: {Error}")]
    private partial void LogDeviceReadFailed(string device, string error);
}
