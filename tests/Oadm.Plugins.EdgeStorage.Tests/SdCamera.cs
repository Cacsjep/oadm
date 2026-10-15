using System.Globalization;
using System.Net;
using System.Text;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>
/// A camera with an SD card that behaves like the AXIS Q1615 Mk III (AXIS OS 12.11.118) on the recorded requests: format,
/// check and repair answer 403 while the card is mounted; every job finishes on the second status request; format leaves
/// the card unmounted; encryption on or off needs it unmounted and applies with the next format; retention 0..7000.
/// Every request is recorded ("POST axis-cgi/disks/format.cgi diskid=SD_DISK").
/// </summary>
internal sealed class SdCamera : IVapixClient
{
    private readonly Dictionary<string, (string Action, int Polls, bool Fail)> _jobs = [];
    private int _nextJob = 1;

    public Uri BaseAddress { get; } = new("http://10.0.0.48/");

    public bool HasCard { get; set; } = true;

    public bool Mounted { get; set; } = true;

    public bool EncryptionEnabled { get; set; }

    public bool Encrypted { get; set; }

    public int MaxAge { get; set; } = 7;

    public int Wear { get; set; } = 1;

    public string? FailJob { get; set; }

    /// <summary>The mount job fails (after another failure, to see which error the task keeps).</summary>
    public bool FailMount { get; set; }

    /// <summary>The unmount happens, but its answer is lost (HTTP 500).</summary>
    public bool UnmountAnswerLost { get; set; }

    /// <summary>Status requests of jobs that answer HTTP 500 before they answer again.</summary>
    public int FailingPolls { get; set; }

    public List<string> Requests { get; } = [];

    public IReadOnlyList<DeviceApi> Apis { get; set; } =
        [new("disk-management", "1.0", "Disk management", "official"), new("disk-properties", "1.1", "Disk properties", "official")];

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult(Apis);

    /// <summary>Holds every request until it completes (a slow camera; null = answer at once).</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        var path = request.RequestUri!.OriginalString;
        var query = path.Contains('?', StringComparison.Ordinal) ? path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty;
        path = path.Split('?')[0];
        var form = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        Requests.Add($"{request.Method} {path}" + (form.Length > 0 ? " " + form : string.Empty));
        var args = Parse(query + "&" + form);
        return path switch
        {
            "axis-cgi/disks/list.cgi" => Xml(ListXml()),
            "axis-cgi/disks/gethealth.cgi" => Xml($"<HealthStatusResponse><HealthStatusSuccess><HealthStatus diskid=\"SD_DISK\" wear=\"{(HasCard ? Wear : -3)}\"/><HealthStatus diskid=\"NetworkShare\" wear=\"-3\"/></HealthStatusSuccess></HealthStatusResponse>"),
            "axis-cgi/disks/properties/getdiskalertlevels.cgi" => Xml(Fixtures.Read("p3265-alert-levels.xml")),
            "axis-cgi/disks/mount.cgi" => StartJob(args["action"]),
            "axis-cgi/disks/format.cgi" or "axis-cgi/disks/checkdisk.cgi" or "axis-cgi/disks/repair.cgi" when Mounted => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "axis-cgi/disks/format.cgi" => StartJob("format"),
            "axis-cgi/disks/checkdisk.cgi" => StartJob("checkdisk"),
            "axis-cgi/disks/repair.cgi" => StartJob("repair"),
            "axis-cgi/disks/job.cgi" => JobStatus(args["jobid"]),
            "axis-cgi/disks/properties/setcleanupmaxage.cgi" => SetMaxAge(args["maxage"]),
            "axis-cgi/disks/properties/enablediskencryption.cgi" => Properties(() => EncryptionEnabled = true, needsUnmounted: true),
            "axis-cgi/disks/properties/disablediskencryption.cgi" => Properties(() => EncryptionEnabled = false, needsUnmounted: true),
            _ => throw new InvalidOperationException("Unexpected request " + path),
        };
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    private string ListXml()
    {
        var status = !HasCard ? "disconnected" : Mounted ? "OK" : "connected";
        var size = Mounted ? "totalsize=\"57922892\" freesize=\"57922656\"" : "totalsize=\"0\" freesize=\"0\"";
        return $"<root><disks numberofdisks=\"2\"><disk diskid=\"SD_DISK\" name=\"\" {size} cleanuplevel=\"98\" cleanupmaxage=\"{MaxAge}\" cleanuppolicy=\"fifo\" locked=\"no\" full=\"no\" readonly=\"no\" status=\"{status}\" filesystem=\"ext4\" group=\"S0\" requiredfilesystem=\"none\" encryptionenabled=\"{Lower(EncryptionEnabled)}\" diskencrypted=\"{Lower(Encrypted)}\"/>"
            + "<disk diskid=\"NetworkShare\" name=\"\" totalsize=\"0\" freesize=\"0\" cleanupmaxage=\"7\" status=\"disconnected\" filesystem=\"cifs\" group=\"S1\"/></disks></root>";
    }

    private HttpResponseMessage StartJob(string action)
    {
        if (action == "unmount" && UnmountAnswerLost && Mounted)
        {
            Mounted = false;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        if (action == "unmount" && !Mounted || action == "mount" && Mounted)
        {
            return Xml($"<root><job action=\"{action}\" diskid=\"SD_DISK\" result=\"ERROR\" description=\"already {action}ed\"/></root>");
        }

        var id = (_nextJob++).ToString(CultureInfo.InvariantCulture);
        _jobs[id] = (action, 0, action == FailJob || action == "mount" && FailMount);
        return Xml($"<root><job action=\"{action}\" diskid=\"SD_DISK\" result=\"OK\" jobid=\"{id}\"/></root>");
    }

    private HttpResponseMessage JobStatus(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return Xml(Fixtures.Read("job-unknown.xml"));
        }

        if (FailingPolls > 0)
        {
            FailingPolls--;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        _jobs[id] = job with { Polls = job.Polls + 1 };
        if (job.Polls == 0)
        {
            return Xml($"<root><job jobid=\"{id}\" diskid=\"SD_DISK\" action=\"{job.Action}\" result=\"OK\" progress=\"40\" description=\"OK\"/></root>");
        }

        if (job.Fail)
        {
            return Xml($"<root><job jobid=\"{id}\" diskid=\"SD_DISK\" action=\"{job.Action}\" result=\"ERROR\" progress=\"100\" description=\"Disk error.\"/></root>");
        }

        if (job.Polls == 1)
        {
            switch (job.Action)
            {
                case "unmount":
                    Mounted = false;
                    break;
                case "mount":
                    Mounted = true;
                    break;
                case "format":
                    Encrypted = EncryptionEnabled;
                    break;
            }
        }

        return Xml($"<root><job jobid=\"{id}\" diskid=\"SD_DISK\" action=\"{job.Action}\" result=\"OK\" progress=\"100\" description=\"OK\"/></root>");
    }

    private HttpResponseMessage SetMaxAge(string value)
    {
        var days = int.Parse(value, CultureInfo.InvariantCulture);
        if (days is < 0 or > 7000)
        {
            return Xml(Fixtures.Read("properties-invalid-max-age.xml"));
        }

        return Properties(() => MaxAge = days);
    }

    private HttpResponseMessage Properties(Action change, bool needsUnmounted = false)
    {
        if (Mounted && needsUnmounted)
        {
            return Xml("<DiskPropertiesResponse><Error><GeneralError><ErrorCode>160</ErrorCode><ErrorDescription>Disk is mounted.</ErrorDescription></GeneralError></Error></DiskPropertiesResponse>");
        }

        change();
        return Xml(Fixtures.Read("properties-success.xml"));
    }

    private static Dictionary<string, string> Parse(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

    private static string Lower(bool value) => value ? "true" : "false";

    private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
}

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
