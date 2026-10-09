using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

using SkiaSharp;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "Snapshot report" core plugin (plugins/Oadm.Plugins.SnapshotReport) so <c>--fake</c>
/// shows the page: tiles from the fake live view sources, generated test pictures, a small placeholder PDF.
/// The JSON matches the plugin's payload models (camelCase); the plugin tests check that.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string SnapshotReportPluginId = "oadm.snapshot-report";

    private static readonly JsonSerializerOptions FakeJson = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, (int Total, DateTime Started, byte[] Pdf)> _fakeReports = [];

    /// <summary>Delay of one fake snapshot (tests pass zero).</summary>
    public TimeSpan SnapshotDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    private async Task<string?> InvokeSnapshotReportAsync(string method, string? payloadJson, CancellationToken ct)
    {
        JsonNode? payload = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson);
        switch (method)
        {
            case "listSources":
                return await FakeListSourcesAsync(ct).ConfigureAwait(false);
            case "snapshot":
            {
                string deviceId = payload?["deviceId"]?.GetValue<string>() ?? string.Empty;
                int camera = payload?["camera"]?.GetValue<int>() ?? 1;
                int maxWidth = payload?["maxWidth"]?.GetValue<int>() ?? 1280;
                int maxHeight = payload?["maxHeight"]?.GetValue<int>() ?? 720;
                await Task.Delay(SnapshotDelay, ct).ConfigureAwait(false);
                return FakeSnapshot(deviceId, camera, maxWidth, maxHeight);
            }

            case "generateReport":
            {
                int total = payload?["items"]?.AsArray().Count ?? 0;
                if (total == 0)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Select at least one snapshot for the report."));
                }

                string id = Guid.NewGuid().ToString("N");
                string site = payload?["site"]?.GetValue<string>() ?? string.Empty;
                lock (_gate)
                {
                    _fakeReports[id] = (total, DateTime.UtcNow, PlaceholderPdf($"Maintenance report - {site} - {total} snapshots (fake mode)"));
                }

                return FakeReportStatus(id);
            }

            case "reportStatus":
                return FakeReportStatus(payload?["jobId"]?.GetValue<string>() ?? string.Empty);
            case "readReport":
            {
                byte[] pdf = FindFakeReport(payload?["jobId"]?.GetValue<string>() ?? string.Empty).Pdf;
                return JsonSerializer.Serialize(new { dataBase64 = Convert.ToBase64String(pdf), offset = 0, total = pdf.Length, eof = true }, FakeJson);
            }

            case "deleteReport":
                lock (_gate)
                {
                    _fakeReports.Remove(payload?["jobId"]?.GetValue<string>() ?? string.Empty);
                }

                return null;
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
        }
    }

    private async Task<string> FakeListSourcesAsync(CancellationToken ct)
    {
        List<Device> devices;
        lock (_gate)
        {
            ThrowIfOffline();
            devices = [.. _devices.Where(d => d.HasVideo).OrderBy(d => System.Net.IPAddress.TryParse(d.Address, out var ip) ? ip.GetAddressBytes()[^1] : 255).Select(d => d.Clone())];
        }

        var tiles = new JsonArray();
        foreach (Device device in devices)
        {
            string? error = device.Status switch
            {
                DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
                DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
                DeviceStatus.CertificateChanged => Oadm.Sdk.Devices.DeviceMessages.CertificateChanged,
                DeviceStatus.Unreachable => "Timeout after 3 s",
                _ => null,
            };
            IReadOnlyList<LiveViewSource> sources = error is null ? await ListLiveViewSourcesAsync(device.Id, ct).ConfigureAwait(false) : [];
            if (sources.Count <= 1)
            {
                tiles.Add(Tile(device, 1, null, 1, device.Address, error));
                continue;
            }

            foreach (LiveViewSource source in sources)
            {
                string label = source.Name.StartsWith("Camera", StringComparison.Ordinal)
                    ? string.Create(CultureInfo.InvariantCulture, $"Sensor {source.Camera}")
                    : source.Name;
                tiles.Add(Tile(device, source.Camera, label, sources.Count, $"{device.Address} - {label}", null));
            }
        }

        return new JsonObject { ["tiles"] = tiles }.ToJsonString(FakeJson);

        static JsonObject Tile(Device device, int camera, string? label, int count, string title, string? error) => new()
        {
            ["device"] = new JsonObject
            {
                ["deviceId"] = device.Id,
                ["address"] = device.Address,
                ["hostName"] = string.IsNullOrEmpty(device.HostName) ? null : device.HostName,
                ["model"] = device.Model,
                ["serial"] = device.Serial,
                ["firmware"] = device.FirmwareVersion,
                ["status"] = device.Status switch
                {
                    DeviceStatus.Ok => "Ok",
                    DeviceStatus.Unreachable => "Unreachable",
                    DeviceStatus.CredentialsRequired => "CredentialsRequired",
                    DeviceStatus.PasswordNotSet => "PasswordNotSet",
                    DeviceStatus.CertificateChanged => "CertificateChanged",
                    _ => "Unknown",
                },
                ["certNotAfterUtc"] = device.CertNotAfter?.ToDateTime(),
                ["certTrust"] = device.CertTrust switch
                {
                    CertificateTrust.Trusted => "Trusted",
                    CertificateTrust.SelfSigned => "SelfSigned",
                    CertificateTrust.Untrusted => "Untrusted",
                    CertificateTrust.Expired => "Expired",
                    _ => null,
                },
            },
            ["camera"] = camera,
            ["sourceLabel"] = label,
            ["sourceCount"] = count,
            ["title"] = title,
            ["error"] = error,
        };
    }

    private string FakeSnapshot(string deviceId, int camera, int maxWidth, int maxHeight)
    {
        Device? device;
        lock (_gate)
        {
            ThrowIfOffline();
            device = _devices.Find(d => d.Id == deviceId)?.Clone();
        }

        if (device is null)
        {
            return JsonSerializer.Serialize(new { error = Oadm.Sdk.Devices.DeviceMessages.Removed }, FakeJson);
        }

        // Multisensor sensors are 4:3 (2592x1944), everything else 16:9.
        bool fourThree = device.Model.Contains("P3727", StringComparison.Ordinal);
        int height = Math.Max(90, maxHeight);
        int width = fourThree ? height * 4 / 3 : height * 16 / 9;
        if (width > maxWidth)
        {
            width = Math.Max(160, maxWidth);
            height = fourThree ? width * 3 / 4 : width * 9 / 16;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] jpeg = TestPicture(width, height, $"{device.Address}  camera {camera}", device.Model, now, HashCode.Combine(deviceId, camera));
        return JsonSerializer.Serialize(new { jpegBase64 = Convert.ToBase64String(jpeg), width, height, capturedUtc = now }, FakeJson);
    }

    /// <summary>A generated JPEG: sky, ground, a few buildings, a caption with the source and the time.</summary>
    public static byte[] TestPicture(int width, int height, string caption, string model, DateTimeOffset time, int seed)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            float hue = random.Next(180, 260);
            using (var sky = new SKPaint())
            {
                sky.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height * 0.6f),
                    [SKColor.FromHsl(hue, 45, 62), SKColor.FromHsl(hue, 35, 82)], SKShaderTileMode.Clamp);
                canvas.DrawRect(0, 0, width, height, sky);
            }

            using (var ground = new SKPaint { Color = SKColor.FromHsl(random.Next(80, 130), 25, 32) })
            {
                canvas.DrawRect(0, height * 0.62f, width, height * 0.38f, ground);
            }

            using (var road = new SKPaint { Color = new SKColor(70, 70, 74) })
            {
                using var path = new SKPath();
                path.MoveTo(width * 0.42f, height * 0.62f);
                path.LineTo(width * 0.58f, height * 0.62f);
                path.LineTo(width * 0.9f, height);
                path.LineTo(width * 0.1f, height);
                path.Close();
                canvas.DrawPath(path, road);
            }

            for (int i = 0; i < 6; i++)
            {
                using var building = new SKPaint { Color = SKColor.FromHsl(random.Next(0, 360), 12, random.Next(35, 70)) };
                float bw = width * (0.06f + (float)random.NextDouble() * 0.08f);
                float bh = height * (0.12f + (float)random.NextDouble() * 0.3f);
                float x = width * (float)random.NextDouble() * 0.92f;
                canvas.DrawRect(x, (height * 0.62f) - bh, bw, bh, building);
            }

            float textSize = Math.Max(12, height / 22f);
            using var font = new SKFont(SKTypeface.Default, textSize);
            using var band = new SKPaint { Color = new SKColor(0, 0, 0, 140) };
            canvas.DrawRect(0, 0, width, textSize * 1.8f, band);
            using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawText($"{caption}  ·  {model}", textSize * 0.6f, textSize * 1.25f, font, text);
            string stamp = time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            canvas.DrawText(stamp, width - (font.MeasureText(stamp) + (textSize * 0.6f)), textSize * 1.25f, font, text);
        }

        using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private string FakeReportStatus(string jobId)
    {
        var (total, started, pdf) = FindFakeReport(jobId);
        int done = Math.Min(total, (int)((DateTime.UtcNow - started).TotalMilliseconds / 150));
        bool finished = done >= total;
        return JsonSerializer.Serialize(new
        {
            jobId,
            state = finished ? "done" : "running",
            done,
            total,
            message = finished ? "Report ready" : string.Create(CultureInfo.InvariantCulture, $"Take snapshot {done + 1} of {total}"),
            size = finished ? pdf.Length : 0,
            pages = 1,
            failed = 0,
        }, FakeJson);
    }

    private (int Total, DateTime Started, byte[] Pdf) FindFakeReport(string jobId)
    {
        lock (_gate)
        {
            return _fakeReports.TryGetValue(jobId, out var report)
                ? report
                : throw new RpcException(new Status(StatusCode.NotFound, "The report no longer exists; create it again."));
        }
    }

    /// <summary>A one-page PDF with one line of text (fake mode has no PDF library).</summary>
    internal static byte[] PlaceholderPdf(string line)
    {
        string text = new(line.Where(c => c is >= ' ' and <= '~' and not '(' and not ')' and not '\\').ToArray());
        string content = $"BT /F1 14 Tf 72 760 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
