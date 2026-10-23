using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.EdgeStorage.Disks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeRecordingExport.Recordings;

/// <summary>Timeouts of <see cref="EdgeRecordingService"/>; tests shorten them.</summary>
public sealed class EdgeRecordingOptions
{
    /// <summary>One list, capabilities or properties request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Until the export's headers arrive (the body is read with the caller's token).</summary>
    public TimeSpan ExportStartTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Size estimates asked at the same time on one camera.</summary>
    public int SizeParallelism { get; init; } = 4;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How often a running export of a group user asks whether the camera is still in the group.</summary>
    public TimeSpan AccessCheckInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The camera side of the page: <see cref="SearchAsync"/> lists the recordings of one camera in a range with their
/// estimated size, <see cref="OpenExportAsync"/> opens one recording's export as a stream that the host passes on to the
/// client (nothing is stored on the server). Read-only for the cameras; the stored login never leaves the server. Cameras
/// come from the user's call (<see cref="ICallContext"/>): for a user with a device group a camera outside the group answers
/// like an unknown one (<see cref="DevicesNotAvailableException"/>) and is never contacted.
/// </summary>
public sealed partial class EdgeRecordingService(ICorePluginContext ctx, EdgeRecordingOptions options)
{
    /// <summary>
    /// A group user's export ends when the camera leaves the group (checked every
    /// <see cref="EdgeRecordingOptions.AccessCheckInterval"/> through the group's devices now); users with all devices and
    /// hosts without groups get the camera's stream as it is.
    /// </summary>
    private Stream BoundToView(ICallContext caller, IDeviceInfo device, Stream stream)
    {
        if (caller.HasAllDevices || ctx.DeviceScopes is not { } scopes)
        {
            return stream;
        }

        var key = caller.DeviceScopeKey;
        var id = device.Id;
        return new ViewBoundStream(stream, async t => await scopes.Devices(key).FindAsync(id, t).ConfigureAwait(false) is not null, options.AccessCheckInterval, options.Time);
    }

    /// <summary>The recordings of one camera in the range. Never throws for camera problems: they are the result's Problem.</summary>
    public async Task<SearchResult> SearchAsync(ICallContext caller, SearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (request.StartUtc is { } s && request.StopUtc is { } e && s >= e)
        {
            throw new ArgumentException("The start must be before the stop.");
        }

        var device = await caller.Devices.FindAsync(request.DeviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            // A user with a device group: outside the group and unknown answer alike (users with all devices: as before).
            return caller.HasAllDevices ? Failed(request.DeviceId, DeviceMessages.Removed) : throw new DevicesNotAvailableException();
        }

        if (!device.HasVideo)
        {
            return Failed(device.Id, EdgeRecordingTexts.NotVideo);
        }

        if (RefusalFor(device) is { } refusal)
        {
            return Failed(device.Id, refusal);
        }

        try
        {
            var client = await caller.Vapix.CreateAsync(device.Id, ct).ConfigureAwait(false);
            var properties = await WithTimeout(t => client.ListParametersAsync([EdgeStorageRequests.PropertiesGroup], t), ct).ConfigureAwait(false);
            if (!IsYes(properties, "LocalStorage"))
            {
                return Failed(device.Id, EdgeRecordingTexts.NoStorage);
            }

            if (!IsYes(properties, "ExportRecording"))
            {
                return Failed(device.Id, EdgeRecordingTexts.NoExport);
            }

            var format = await FormatAsync(client, ct).ConfigureAwait(false);
            var (recordings, truncated) = await ListAsync(client, request, ct).ConfigureAwait(false);
            var disk = await DiskProblemAsync(client, ct).ConfigureAwait(false);
            if (recordings.Count == 0)
            {
                return new SearchResult(device.Id, [], 0, format, null, DiskProblem: disk);
            }

            var bytes = await EstimateAsync(client, recordings, format, ct).ConfigureAwait(false);
            LogSearched(ctx.Logger, device.Address, recordings.Count);
            return new SearchResult(device.Id, recordings, bytes, format, null, truncated, disk);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Failed(device.Id, ErrorText(ex, options.RequestTimeout));
        }
    }

    /// <summary>
    /// Opens the export of one recording, cut to the request's times; the caller disposes the download. Throws
    /// <see cref="InvalidOperationException"/> with the reason in plain words when the camera cannot send it.
    /// </summary>
    public async Task<PluginDownload> OpenExportAsync(ICallContext caller, ExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RecordingId) || string.IsNullOrWhiteSpace(request.DiskId))
        {
            throw new ArgumentException("The recording and its disk are required.");
        }

        var format = request.Format is EdgeStorageRequests.Mp4 ? EdgeStorageRequests.Mp4 : EdgeStorageRequests.Matroska;
        var device = await caller.Devices.FindAsync(request.DeviceId, ct).ConfigureAwait(false)
            ?? (caller.HasAllDevices ? throw new InvalidOperationException(DeviceMessages.Removed) : throw new DevicesNotAvailableException());
        if (!device.HasVideo)
        {
            throw new InvalidOperationException(EdgeRecordingTexts.NotVideo);
        }

        if (RefusalFor(device) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        HttpResponseMessage? response = null;
        try
        {
            var client = await caller.Vapix.CreateAsync(device.Id, ct).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(EdgeStorageRequests.ExportUri(request.RecordingId, request.DiskId, request.StartUtc, request.StopUtc, format), UriKind.Relative));
            message.Options.Set(VapixRequestOptions.Timeout, options.ExportStartTimeout);
            message.Options.Set(VapixRequestOptions.StreamResponse, true);
            response = await client.SendAsync(message, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(response.StatusCode == HttpStatusCode.BadRequest
                    ? EdgeRecordingTexts.NotExported
                    : DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
            }

            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (type.Contains("xml", StringComparison.OrdinalIgnoreCase) || type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(EdgeRecordingTexts.NotExported);
            }

            var name = EdgeStorageParsing.FileName(response.Content.Headers.ContentDisposition)
                ?? DefaultName(request, format);
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var download = new PluginDownload(BoundToView(caller, device, stream), name, response.Content.Headers.ContentLength, response)
            {
                AuditDetail = string.Create(CultureInfo.InvariantCulture, $"Exported recording: {device.Address}, {Range(request.StartUtc, request.StopUtc)}"),
            };
            response = null; // owned by the download now
            return download;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            throw new InvalidOperationException(ErrorText(ex, options.ExportStartTimeout), ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(DeviceMessages.Timeout(options.ExportStartTimeout));
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>The refusal text for a device status, or null when the camera may be contacted.</summary>
    internal static string? RefusalFor(DeviceStatus status) => status switch
    {
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.CredentialsRequired => DeviceMessages.CredentialsRequired,
        DeviceStatus.PasswordNotSet => DeviceMessages.PasswordNotSet,
        DeviceStatus.SiteOffline => Oadm.Sdk.Sites.SiteTexts.SiteOffline(null),
        _ => null,
    };

    /// <summary>The refusal text for <paramref name="device"/>: "Site offline: Building B" while its site is offline (nothing is sent).</summary>
    internal static string? RefusalFor(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.SiteOffline ? Oadm.Sdk.Sites.SiteTexts.SiteOffline(device.SiteName) : RefusalFor(device.Status);
    }


    private async Task<string> FormatAsync(IVapixClient client, CancellationToken ct)
    {
        try
        {
            var xml = await GetStringAsync(client, EdgeStorageRequests.CapabilitiesUri(), ct).ConfigureAwait(false);
            return EdgeStorageParsing.ChooseFormat(EdgeStorageParsing.ParseFormats(xml));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return EdgeStorageRequests.Matroska; // older cameras have no capabilities request; Matroska is the format of every version
        }
    }

    /// <summary>The problem of the SD card or network share; null when they are fine or the camera does not say.</summary>
    private async Task<string?> DiskProblemAsync(IVapixClient client, CancellationToken ct)
    {
        try
        {
            return EdgeDisks.FirstProblem(EdgeDisks.Parse(await GetStringAsync(client, EdgeDisks.ListUri, ct).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(IReadOnlyList<RecordingEntry> Recordings, bool Truncated)> ListAsync(IVapixClient client, SearchRequest request, CancellationToken ct)
    {
        var all = new List<RecordingEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matched = 0;
        while (all.Count < EdgeRecordingInfo.MaxRecordingsPerCamera)
        {
            var count = Math.Min(EdgeStorageRequests.PageSize, EdgeRecordingInfo.MaxRecordingsPerCamera - all.Count);
            var xml = await GetStringAsync(client, EdgeStorageRequests.ListUri(request.StartUtc, request.StopUtc, all.Count, count), ct).ConfigureAwait(false);
            var (pageMatched, page) = EdgeStorageParsing.ParseList(xml);
            matched = pageMatched;
            var added = 0;
            foreach (var recording in page)
            {
                if (seen.Add(recording.DiskId + "/" + recording.Id))
                {
                    all.Add(recording);
                    added++;
                }
            }

            if (added == 0 || all.Count >= matched)
            {
                break;
            }
        }

        all.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (all, matched > all.Count);
    }

    /// <summary>
    /// The estimated size of every recording in bytes: the camera's estimate of up to <see cref="EdgeRecordingInfo.SizeSamples"/>
    /// recordings spread over the list, the rest by their duration at the same bytes per second. Null when the camera
    /// estimates none of them.
    /// </summary>
    private async Task<long?> EstimateAsync(IVapixClient client, IReadOnlyList<RecordingEntry> recordings, string format, CancellationToken ct)
    {
        var now = options.Time.GetUtcNow();
        var samples = Samples(recordings.Count, EdgeRecordingInfo.SizeSamples).Select(i => recordings[i]).ToList();
        long sampledBytes = 0;
        double sampledSeconds = 0;
        var estimated = 0;
        var gate = new Lock();
        await Parallel.ForEachAsync(samples, new ParallelOptions { MaxDegreeOfParallelism = options.SizeParallelism, CancellationToken = ct }, async (r, token) =>
        {
            long? bytes;
            try
            {
                var xml = await GetStringAsync(client, EdgeStorageRequests.PropertiesUri(r.Id, r.DiskId, r.Start, r.Stop, format), token).ConfigureAwait(false);
                bytes = EdgeStorageParsing.ParseEstimatedBytes(xml);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                bytes = null; // one estimate that fails leaves the size to the others
            }

            if (bytes is { } b)
            {
                lock (gate)
                {
                    sampledBytes += b;
                    sampledSeconds += Seconds(r, now);
                    estimated++;
                }
            }
        }).ConfigureAwait(false);

        if (estimated == 0)
        {
            return null;
        }

        if (estimated == recordings.Count)
        {
            return sampledBytes;
        }

        var totalSeconds = recordings.Sum(r => Seconds(r, now));
        return sampledSeconds <= 0
            ? sampledBytes / estimated * recordings.Count
            : (long)(sampledBytes / sampledSeconds * totalSeconds);
    }

    /// <summary>Up to <paramref name="max"/> indexes spread evenly over <paramref name="count"/>, first and last included.</summary>
    internal static IEnumerable<int> Samples(int count, int max)
    {
        if (count <= max)
        {
            return Enumerable.Range(0, count);
        }

        return Enumerable.Range(0, max).Select(i => (int)Math.Round(i * (count - 1) / (double)(max - 1))).Distinct();
    }

    private static double Seconds(RecordingEntry r, DateTimeOffset now) => Math.Max(0, ((r.Stop ?? now) - r.Start).TotalSeconds);

    private async Task<string> GetStringAsync(IVapixClient client, string uri, CancellationToken ct) =>
        await WithTimeout(async t =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
            using var response = await client.SendAsync(request, t).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode), null, response.StatusCode);
            }

            return await response.Content.ReadAsStringAsync(t).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    private async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        return await call(timeout.Token).ConfigureAwait(false);
    }

    private static bool IsYes(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(EdgeStorageRequests.PropertiesGroup + "." + name, out var value) && string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static SearchResult Failed(Guid deviceId, string problem) => new(deviceId, [], null, null, problem);

    private static string DefaultName(ExportRequest request, string format) =>
        (request.StartUtc is { } s ? s.UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) : request.RecordingId)
        + EdgeStorageRequests.Extension(format);

    private static string Range(DateTimeOffset? start, DateTimeOffset? stop) =>
        (start is { } s ? s.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "start")
        + " to "
        + (stop is { } e ? e.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "now");

    /// <summary>A failure in plain words: timeout, unreachable, HTTP status, the camera's error text.</summary>
    internal static string ErrorText(Exception ex, TimeSpan timeout) => ex switch
    {
        OperationCanceledException or TimeoutException => DeviceMessages.Timeout(timeout),
        KeyNotFoundException => DeviceMessages.Removed,
        EdgeStorageException storage => storage.Message,
        HttpRequestException { StatusCode: { } code } => DeviceMessages.ForHttpStatus((int)code) ?? DeviceMessages.ServerError((int)code),
        HttpRequestException { InnerException: SocketException socket } => DeviceMessages.Unreachable(socket.Message),
        HttpRequestException http => DeviceMessages.Unreachable(http.InnerException?.Message ?? http.Message),
        FormatException or System.Xml.XmlException => EdgeRecordingTexts.Unreadable,
        _ => ex.Message,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Edge Recording Export: {Address} has {Count} recordings in the range")]
    private static partial void LogSearched(ILogger logger, string address, int count);
}

/// <summary>The plugin's user texts.</summary>
public static class EdgeRecordingTexts
{
    public const string NotVideo = "Not a video device";

    public const string NoStorage = "The camera cannot record to an SD card or network share";

    public const string NoExport = "The camera cannot export recordings";

    public const string NotExported = "The camera did not send the recording. It may have been deleted meanwhile";

    public const string Unreadable = "The camera sent an answer OADM cannot read";

    public const string NoDisk = EdgeDisks.NoDisk;
}
