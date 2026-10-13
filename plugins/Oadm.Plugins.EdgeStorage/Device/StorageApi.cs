using System.Globalization;
using System.Net;
using System.Xml.Linq;

using Oadm.Plugins.EdgeStorage.Disks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeStorage.Device;

/// <summary>
/// The requests of the VAPIX Edge storage API the plugin sends (disk management 1.0, disk properties 1.x). Verified on an
/// AXIS Q1615 Mk III (AXIS OS 12.11.118, 64 GB card) and an AXIS P3265-V (12.11.77), see the plugin README:
/// <list type="bullet">
/// <item>format, check and repair answer HTTP 403 while the card is mounted: the card is unmounted first, mounted after.</item>
/// <item>every one of them, mount and unmount start a job (<c>jobid</c>) that <c>job.cgi</c> follows (3 to 21 s on 64 GB);
/// format leaves the card unmounted.</item>
/// <item>encryption on or off needs an unmounted card and takes effect with the next format; the disk properties CGIs
/// accept their arguments as a POST form, so a passphrase never stands in a URL.</item>
/// <item>retention (<c>cleanupmaxage</c>) is 0..7000 days, 0 = no age limit; 7001 is error 170 "Invalid max age."</item>
/// </list>
/// </summary>
public static class StorageRequests
{
    public const string SdCard = EdgeDisk.SdCardId;

    public const string HealthPath = "axis-cgi/disks/gethealth.cgi";

    public const string MountPath = "axis-cgi/disks/mount.cgi";

    public const string FormatPath = "axis-cgi/disks/format.cgi";

    public const string CheckPath = "axis-cgi/disks/checkdisk.cgi";

    public const string RepairPath = "axis-cgi/disks/repair.cgi";

    public const string JobPath = "axis-cgi/disks/job.cgi";

    public const string AlertLevelsPath = "axis-cgi/disks/properties/getdiskalertlevels.cgi";

    public const string MaxAgePath = "axis-cgi/disks/properties/setcleanupmaxage.cgi";

    public const string EnableEncryptionPath = "axis-cgi/disks/properties/enablediskencryption.cgi";

    public const string DisableEncryptionPath = "axis-cgi/disks/properties/disablediskencryption.cgi";

    /// <summary>The API ids of the API list (apidiscovery).</summary>
    public const string DiskManagementApi = "disk-management";

    public const string DiskPropertiesApi = "disk-properties";

    /// <summary>Retention the camera accepts (days; 0 = no age limit).</summary>
    public const int MaxRetentionDays = 7000;

    /// <summary>Passphrase length the camera accepts (bytes, UTF-8).</summary>
    public const int MaxPassphraseBytes = 512;

    public static string AlertLevelsUri(string diskId) => AlertLevelsPath + "?schemaversion=1&diskid=" + Uri.EscapeDataString(diskId);

    public static string JobUri(string jobId, string diskId) =>
        JobPath + "?jobid=" + Uri.EscapeDataString(jobId) + "&diskid=" + Uri.EscapeDataString(diskId);

    /// <summary>A POST with a form body (the arguments never in the URL).</summary>
    public static HttpRequestMessage Post(string path, params (string Name, string Value)[] form) =>
        new(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))) };
}

/// <summary>The health of a disk (<c>gethealth.cgi</c>): wear in percent of the expected life, can pass 100.</summary>
public sealed record DiskHealth(string DiskId, int? WearPercent, int? TemperatureKelvin, bool? Passed)
{
    /// <summary>"-1" not supported, "-2" error, "-3" not present: no value.</summary>
    public static int? Value(string? text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
}

/// <summary>A job of the disk management API (<c>startjob1</c> and <c>checkjob1</c> answers).</summary>
public sealed record DiskJob(string? JobId, string? Action, bool Ok, int Progress, string? Description)
{
    public bool Finished => Progress >= 100 || !Ok;
}

/// <summary>The device refused an Edge storage request; the message is for the user.</summary>
public sealed class StorageException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Parsers of the Edge storage answers (XML through <see cref="DeviceXml"/>, names without namespace).</summary>
public static class StorageParsing
{
    public static IReadOnlyList<DiskHealth> ParseHealth(string xml) =>
        [.. Xml(xml).Descendants().Where(e => e.Name.LocalName == "HealthStatus").Select(e => new DiskHealth(
            Attr(e, "diskid") ?? string.Empty,
            DiskHealth.Value(Attr(e, "wear")),
            DiskHealth.Value(Attr(e, "temperature")),
            DiskHealth.Value(Attr(e, "overallhealth")) is { } h ? h == 1 : null))];

    /// <summary>The wear alert level of <c>getdiskalertlevels.cgi</c> (percent), null when not given.</summary>
    public static int? ParseWearAlert(string xml)
    {
        var doc = Xml(xml);
        ThrowOnPropertiesError(doc);
        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AlertLevels") is { } levels ? DiskHealth.Value(Attr(levels, "Wear")) : null;
    }

    /// <summary>The job of a start or status answer; a missing job is <see cref="StorageException"/>.</summary>
    public static DiskJob ParseJob(string xml)
    {
        var job = Xml(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "job")
            ?? throw new StorageException("The camera did not start the job.");
        var ok = string.Equals(Attr(job, "result"), "OK", StringComparison.OrdinalIgnoreCase);
        var progress = int.TryParse(Attr(job, "progress"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
        return new DiskJob(Attr(job, "jobid"), Attr(job, "action"), ok, progress, Attr(job, "description"));
    }

    /// <summary>A disk properties answer: <c>GeneralSuccess</c>, or <see cref="StorageException"/> with the plain reason.</summary>
    public static void EnsurePropertiesSuccess(string xml) => ThrowOnPropertiesError(Xml(xml));

    /// <summary>The answer as XML; an answer that is not XML (an HTML error page) is <see cref="StorageException"/>.</summary>
    private static XDocument Xml(string xml)
    {
        try
        {
            return DeviceXml.Parse(xml.Trim());
        }
        catch (System.Xml.XmlException)
        {
            throw new StorageException(StorageTexts.Unreadable);
        }
    }

    private static void ThrowOnPropertiesError(XDocument doc)
    {
        if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "GeneralError") is { } error)
        {
            var code = int.TryParse(Child(error, "ErrorCode"), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0;
            throw new StorageException(StorageTexts.PropertiesError(code, Child(error, "ErrorDescription")), code);
        }

        if (!doc.Descendants().Any(e => e.Name.LocalName is "Success" or "GeneralSuccess"))
        {
            throw new StorageException("The camera sent an answer OADM cannot read.");
        }
    }

    private static string? Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value.Trim();

    private static string Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
}

/// <summary>Sends the Edge storage requests with the stored credentials of one device.</summary>
public sealed class StorageClient(IVapixClient client, TimeProvider time, TimeSpan? pollInterval = null)
{
    /// <summary>How often a running job is asked for its progress.</summary>
    public static readonly TimeSpan JobPollInterval = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _pollInterval = pollInterval ?? JobPollInterval;

    /// <summary>Status requests of a running job that may fail in a row before the task gives up.</summary>
    public const int MaxFailedPolls = 5;

    /// <summary>The longest a job may take (format of a large card).</summary>
    public static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<EdgeDisk>> ListDisksAsync(CancellationToken ct) =>
        EdgeDisks.Parse(await GetAsync(EdgeDisks.ListUri, ct).ConfigureAwait(false));

    /// <summary>The SD card, or <see cref="StorageException"/> "No SD card" when the slot is empty.</summary>
    public async Task<EdgeDisk> GetSdCardAsync(CancellationToken ct) =>
        (await ListDisksAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.IsSdCard && d.IsPresent)
            ?? throw new StorageException(StorageTexts.NoCard + ". Nothing was changed.");

    public async Task<IReadOnlyList<DiskHealth>> GetHealthAsync(CancellationToken ct) =>
        StorageParsing.ParseHealth(await GetAsync(StorageRequests.HealthPath, ct).ConfigureAwait(false));

    public async Task<int?> GetWearAlertAsync(string diskId, CancellationToken ct) =>
        StorageParsing.ParseWearAlert(await GetAsync(StorageRequests.AlertLevelsUri(diskId), ct).ConfigureAwait(false));

    public Task MountAsync(string diskId, bool mount, IProgress<int>? progress, CancellationToken ct) =>
        RunJobAsync(StorageRequests.MountPath, [("action", mount ? "mount" : "unmount"), ("diskid", diskId)], diskId, progress, ct);

    public Task FormatAsync(string diskId, IProgress<int>? progress, CancellationToken ct) =>
        RunJobAsync(StorageRequests.FormatPath, [("diskid", diskId)], diskId, progress, ct);

    public Task CheckAsync(string diskId, IProgress<int>? progress, CancellationToken ct) =>
        RunJobAsync(StorageRequests.CheckPath, [("diskid", diskId)], diskId, progress, ct);

    public Task RepairAsync(string diskId, IProgress<int>? progress, CancellationToken ct) =>
        RunJobAsync(StorageRequests.RepairPath, [("diskid", diskId)], diskId, progress, ct);

    public Task SetRetentionAsync(string diskId, int days, CancellationToken ct) =>
        PropertiesAsync(StorageRequests.MaxAgePath, ct, ("schemaversion", "1"), ("diskid", diskId), ("maxage", days.ToString(CultureInfo.InvariantCulture)));

    public Task EnableEncryptionAsync(string diskId, string passphrase, CancellationToken ct) =>
        PropertiesAsync(StorageRequests.EnableEncryptionPath, ct, ("schemaversion", "1"), ("diskid", diskId), ("passphrase", passphrase));

    public Task DisableEncryptionAsync(string diskId, CancellationToken ct) =>
        PropertiesAsync(StorageRequests.DisableEncryptionPath, ct, ("schemaversion", "1"), ("diskid", diskId));

    /// <summary>Starts a job and follows it to the end: <see cref="StorageException"/> with the camera's reason when it fails.</summary>
    private async Task RunJobAsync(string path, (string, string)[] form, string diskId, IProgress<int>? progress, CancellationToken ct)
    {
        using var start = StorageRequests.Post(path, form);
        var job = StorageParsing.ParseJob(await SendAsync(start, ct).ConfigureAwait(false));
        if (!job.Ok || job.JobId is not { Length: > 0 } jobId)
        {
            throw new StorageException(StorageTexts.JobFailed(job.Description));
        }

        var deadline = time.GetUtcNow() + JobTimeout;
        var failedPolls = 0;
        while (true)
        {
            await Task.Delay(_pollInterval, time, ct).ConfigureAwait(false);
            try
            {
                job = StorageParsing.ParseJob(await GetAsync(StorageRequests.JobUri(jobId, diskId), ct).ConfigureAwait(false));
                failedPolls = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or StorageException or TimeoutException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // The camera keeps working on its job: a lost answer is asked again, up to the deadline.
                if (++failedPolls >= MaxFailedPolls || time.GetUtcNow() > deadline)
                {
                    throw;
                }

                continue;
            }

            if (!job.Ok)
            {
                throw new StorageException(StorageTexts.JobFailed(job.Description));
            }

            progress?.Report(Math.Clamp(job.Progress, 0, 100));
            if (job.Finished)
            {
                return;
            }

            if (time.GetUtcNow() > deadline)
            {
                throw new StorageException(StorageTexts.JobTimedOut(JobTimeout));
            }
        }
    }

    private async Task PropertiesAsync(string path, CancellationToken ct, params (string, string)[] form)
    {
        using var request = StorageRequests.Post(path, form);
        StorageParsing.EnsurePropertiesSuccess(await SendAsync(request, ct).ConfigureAwait(false));
    }

    private async Task<string> GetAsync(string uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new StorageException(response.StatusCode == HttpStatusCode.Forbidden
                ? StorageTexts.Forbidden
                : DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode), (int)response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Every text of the plugin about the card, in one place (wording rules: plain, short).</summary>
public static class StorageTexts
{
    public const string NoCard = "No SD card";

    public const string NoApi = "The camera has no SD card management";

    public const string Unreadable = "The camera sent an answer OADM cannot read";

    /// <summary>"Worn 92 % (the camera warns at 90 %)".</summary>
    public static string Worn(int wear, int alert) =>
        string.Create(CultureInfo.CurrentCulture, $"Worn {wear} % (the camera warns at {alert} %). Replace the SD card soon");

    /// <summary>HTTP 403: format, check and repair while the card is mounted, or a login without administrator rights.</summary>
    public static readonly string Forbidden = DeviceMessages.Forbidden.TrimEnd(')') + ", or the SD card is in use)";

    public static string JobFailed(string? description) =>
        string.IsNullOrWhiteSpace(description) || description.Equals("OK", StringComparison.OrdinalIgnoreCase)
            ? "The camera could not finish it"
            : "The camera could not finish it: " + description.Trim().TrimEnd('.');

    public static string JobTimedOut(TimeSpan timeout) =>
        string.Create(CultureInfo.InvariantCulture, $"The camera did not finish within {timeout.TotalMinutes:0} minutes");

    /// <summary>The disk properties error codes (VAPIX disk properties API) in plain words; others with the camera's text.</summary>
    public static string PropertiesError(int code, string description) => code switch
    {
        100 => "The camera cannot encrypt this SD card",
        110 => "The camera does not accept this passphrase",
        140 => "The SD card is encrypted already",
        150 => "The SD card must be in use for this",
        160 => "The SD card is in use",
        170 => string.Create(CultureInfo.InvariantCulture, $"The camera does not accept this retention (0 to {StorageRequests.MaxRetentionDays:N0} days)"),
        _ => string.IsNullOrWhiteSpace(description)
            ? string.Create(CultureInfo.InvariantCulture, $"The camera refused (error {code})")
            : string.Create(CultureInfo.InvariantCulture, $"The camera refused: {description.Trim().TrimEnd('.')} (error {code})"),
    };
}
