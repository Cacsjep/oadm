using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "System report" core plugin (plugins/Oadm.Plugins.SystemReport) so <c>--fake</c> runs the toolbar
/// button: every device "downloads" for <see cref="SystemReportDelay"/>, four at a time; devices with a bad status fail with
/// the server's texts; the bundle holds small generated ZIPs and a summary. The JSON matches the plugin's models (camelCase);
/// the plugin tests check that.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string SystemReportPluginId = "oadm.system-report";

    private readonly Dictionary<string, FakeSystemReportJob> _systemReportJobs = [];

    /// <summary>Time one fake device takes for its report (tests pass a few milliseconds).</summary>
    public TimeSpan SystemReportDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    private Task<string?> InvokeSystemReportAsync(string method, string? payloadJson)
    {
        JsonNode? payload = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson);
        string jobId = payload?["jobId"]?.GetValue<string>() ?? string.Empty;
        switch (method)
        {
            case "start":
                return Task.FromResult<string?>(FakeStartSystemReport(payload?["deviceIds"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? []));
            case "status":
                return Task.FromResult<string?>(FakeSystemReportStatus(FindSystemReportJob(jobId)));
            case "read":
            {
                FakeSystemReportJob job = FindSystemReportJob(jobId);
                byte[] bundle = job.Bundle ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "The system reports are not ready yet."));
                long offset = payload?["offset"]?.GetValue<long>() ?? 0;
                int length = (int)Math.Min(2 * 1024 * 1024, bundle.Length - offset);
                return Task.FromResult<string?>(JsonSerializer.Serialize(new { dataBase64 = Convert.ToBase64String(bundle, (int)offset, length), offset, total = bundle.Length, eof = offset + length >= bundle.Length }, FakeJson));
            }

            case "delete":
                lock (_gate)
                {
                    _systemReportJobs.Remove(jobId);
                }

                return Task.FromResult<string?>(null);
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
        }
    }

    private string FakeStartSystemReport(List<string> deviceIds)
    {
        if (deviceIds.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Select at least one device."));
        }

        var job = new FakeSystemReportJob(Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        lock (_gate)
        {
            ThrowIfOffline();
            foreach (string id in deviceIds.Distinct())
            {
                Device? device = _devices.Find(d => d.Id == id)?.Clone();
                string? error = device?.Status switch
                {
                    null => Oadm.Sdk.Devices.DeviceMessages.Removed,
                    DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
                    DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
                    DeviceStatus.CertificateChanged => Oadm.Sdk.Devices.DeviceMessages.CertificateChanged,
                    DeviceStatus.Unreachable => "Unreachable - No route to host",
                    _ => null,
                };
                job.Devices.Add((id, device?.Address ?? string.Empty, device?.Serial ?? string.Empty, device?.Model ?? string.Empty, error));
            }

            _systemReportJobs[job.Id] = job;
        }

        return FakeSystemReportStatus(job);
    }

    private FakeSystemReportJob FindSystemReportJob(string jobId)
    {
        lock (_gate)
        {
            return _systemReportJobs.GetValueOrDefault(jobId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "The system report job no longer exists; start it again."));
        }
    }

    /// <summary>States from the elapsed time: device i runs in slot i / 4; refused devices fail at once.</summary>
    private string FakeSystemReportStatus(FakeSystemReportJob job)
    {
        double elapsed = (DateTime.UtcNow - job.Started).TotalMilliseconds;
        double step = Math.Max(1, SystemReportDelay.TotalMilliseconds);
        var devices = new List<object>();
        int finished = 0, failed = 0;
        for (int i = 0; i < job.Devices.Count; i++)
        {
            (string id, string address, string serial, string model, string? error) = job.Devices[i];
            double start = i / 4 * step;
            string state;
            long size = 0;
            if (error is not null)
            {
                state = "failed";
            }
            else if (elapsed >= start + step)
            {
                state = "done";
                size = 300_000 + (i * 1_234);
            }
            else
            {
                state = elapsed >= start ? "downloading" : "waiting";
            }

            finished += state is "done" or "failed" ? 1 : 0;
            failed += state == "failed" ? 1 : 0;
            devices.Add(new { deviceId = id, address, serial, model, state, error = state == "failed" ? error : null, size });
        }

        bool done = finished == job.Devices.Count;
        if (done)
        {
            lock (_gate)
            {
                job.Bundle ??= FakeSystemReportBundle(job);
            }
        }

        return JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            state = done ? "done" : "running",
            total = job.Devices.Count,
            finished,
            failed,
            size = done ? job.Bundle!.Length : 0,
            version = 0,
            devices,
        }, FakeJson);
    }

    private static byte[] FakeSystemReportBundle(FakeSystemReportJob job)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var summary = new StringBuilder("OADM system reports (fake mode)\r\n");
            foreach ((_, string address, string serial, string model, string? error) in job.Devices)
            {
                if (error is not null)
                {
                    summary.Append(CultureInfo.InvariantCulture, $"  {address}  {serial}  {model}  {error}\r\n");
                    continue;
                }

                ZipArchiveEntry entry = zip.CreateEntry($"{address}_{serial}_{model.Replace("AXIS ", "", StringComparison.Ordinal).Replace(' ', '-')}_{job.Started:yyyyMMdd-HHmmss}.zip", CompressionLevel.NoCompression);
                using Stream target = entry.Open();
                using var inner = new ZipArchive(target, ZipArchiveMode.Create);
                using var writer = new StreamWriter(inner.CreateEntry("serverreport_cgi.txt").Open());
                writer.Write($"Fake server report of {model} {serial} at {address} (fake mode)\n");
            }

            using var summaryWriter = new StreamWriter(zip.CreateEntry("summary.txt").Open());
            summaryWriter.Write(summary.ToString());
        }

        return memory.ToArray();
    }

    private sealed class FakeSystemReportJob(string id, DateTime started)
    {
        public string Id { get; } = id;
        public DateTime Started { get; } = started;
        public List<(string Id, string Address, string Serial, string Model, string? Error)> Devices { get; } = [];
        public byte[]? Bundle { get; set; }
    }
}
