using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake Edge Recording Export so <c>--fake</c> and the tests show recordings: every video device with status OK has a
/// 10 s recording every 40 minutes (the Q6135-LE records continuously in hour pieces, the M3106-L Mk II has no SD card, the Q1656-LE's card is read-only), sizes
/// at about 4 Mbit/s, and a download sends that many bytes (an MP4 header and zeros) at about 40 MB/s, named like the
/// camera names its files. JSON of the plugin (camelCase).
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string EdgeRecordingPluginId = "oadm.edge-recording-export";

    private const long FakeBytesPerSecond = 500_000;

    private async Task<string?> InvokeEdgeRecordingAsync(string method, string? payloadJson, CancellationToken ct)
    {
        if (method != "search")
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
        }

        var request = JsonNode.Parse(payloadJson ?? "{}")!.AsObject();
        var deviceId = request["deviceId"]?.GetValue<string>() ?? string.Empty;
        var start = request["startUtc"]?.GetValue<DateTimeOffset>();
        var stop = request["stopUtc"]?.GetValue<DateTimeOffset>();
        await Task.Delay(150, ct).ConfigureAwait(false);
        Device? device;
        lock (_gate)
        {
            ThrowIfOffline();
            device = _devices.Find(d => d.Id == deviceId)?.Clone();
        }

        var result = new JsonObject { ["deviceId"] = deviceId, ["recordings"] = new JsonArray() };
        if (device is null || !device.HasVideo)
        {
            result["problem"] = device is null ? "The device was removed from OADM." : "Not a video device";
            return result.ToJsonString();
        }

        if (device.Status is DeviceStatus.CredentialsRequired or DeviceStatus.PasswordNotSet or DeviceStatus.CertificateChanged or DeviceStatus.Unreachable)
        {
            result["problem"] = device.Status switch
            {
                DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
                DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
                DeviceStatus.CertificateChanged => "Certificate changed. Remove the device and add it again to trust the new certificate.",
                _ => "Unreachable - the device does not answer",
            };
            return result.ToJsonString();
        }

        if (device.Model.Contains("M3106", StringComparison.Ordinal))
        {
            result["problem"] = "The camera cannot record to an SD card or network share";
            return result.ToJsonString();
        }

        var now = DateTimeOffset.UtcNow;
        var from = start ?? now.AddDays(-3);
        var to = stop is { } s && s < now ? s : now;
        var continuous = device.Model.Contains("Q6135", StringComparison.Ordinal);
        var recordings = (JsonArray)result["recordings"]!;
        long bytes = 0;
        var step = continuous ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(40);
        var length = continuous ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(10);
        var serial = device.Serial;
        for (var t = new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero); t < to && recordings.Count < 5000; t += step)
        {
            var end = t + length;
            if (end <= from)
            {
                continue;
            }

            var cutStart = t < from ? from : t;
            var ongoing = end > now;
            var cutEnd = ongoing ? (DateTimeOffset?)null : end > to ? to : end;
            recordings.Add(new JsonObject
            {
                ["id"] = t.UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_0001_" + serial,
                ["diskId"] = "SD_DISK",
                ["start"] = cutStart,
                ["stop"] = cutEnd,
                ["source"] = "1",
                ["type"] = continuous ? "continuous" : "triggered",
            });
            bytes += (long)(((cutEnd ?? now) - cutStart).TotalSeconds * FakeBytesPerSecond);
        }

        if (device.Model.Contains("Q1656", StringComparison.Ordinal))
        {
            result["diskProblem"] = "The SD card is read-only";
        }

        result["estimatedBytes"] = bytes;
        result["format"] = "mp4";
        return result.ToJsonString();
    }

    public async Task<Oadm.Sdk.Plugins.PluginDownload> DownloadCorePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        if (pluginId != EdgeRecordingPluginId || method != "export")
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, $"The plugin '{pluginId}' has no downloads."));
        }

        await Task.Delay(50, ct).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfOffline();
        }

        var request = JsonNode.Parse(payloadJson ?? "{}")!.AsObject();
        var start = request["startUtc"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow;
        var stop = request["stopUtc"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow;
        var length = Math.Max(64, (long)((stop - start).TotalSeconds * FakeBytesPerSecond));
        var name = start.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_"
            + stop.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".mp4";
        return new Oadm.Sdk.Plugins.PluginDownload(new FakeRecordingStream(length), name, length);
    }

    /// <summary>An MP4 file type box followed by zeros, read at about 40 MB/s.</summary>
    private sealed class FakeRecordingStream(long length) : Stream
    {
        private static readonly byte[] Header = [0, 0, 0, 0x1c, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'5'];

        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = (int)Math.Min(Math.Min(buffer.Length, 256 * 1024), length - _position);
            if (count <= 0)
            {
                return 0;
            }

            await Task.Delay(6, cancellationToken).ConfigureAwait(false);
            buffer.Span[..count].Clear();
            if (_position < Header.Length)
            {
                var head = (int)Math.Min(Header.Length - _position, count);
                Header.AsSpan((int)_position, head).CopyTo(buffer.Span);
            }

            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
