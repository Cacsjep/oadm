using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "Hardening scan" core plugin (plugins/Oadm.Plugins.HardeningScan) so <c>--fake</c> shows the page:
/// deterministic results per fake device (most pass, some warn on SSH, discovery and DHCP, some fail on ciphers or SNMP v2c,
/// devices with a bad status cannot be scanned), Basic results from the day before at start, and a scan simulated over about
/// 3 s that pushes progress and result events like the server. The JSON matches the plugin's contract (camelCase); the plugin
/// tests check that, including the column order.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string HardeningScanPluginId = "oadm.hardening-scan";

    /// <summary>The check columns in the plugin's catalog order (HardeningCatalog.ColumnIds).</summary>
    public static readonly IReadOnlyList<string> FakeHardeningColumns =
        ["B2", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B12", "B13", "B14", "B15", "B20", "B21", "E3", "E4", "E5", "E6", "E7", "X1", "X2", "X3", "X4", "X5", "X6"];

    private const int FakeBasicColumns = 15;

    private readonly Dictionary<(string DeviceId, string Level), JsonObject> _fakeHardening = [];
    private JsonObject? _fakeHardeningJob;
    private CancellationTokenSource? _fakeHardeningScan;

    /// <summary>Duration of a simulated scan (tests shorten it).</summary>
    public TimeSpan HardeningScanDuration { get; set; } = TimeSpan.FromSeconds(3);

    private Task<string?> InvokeHardeningScanAsync(string method, string? payloadJson)
    {
        JsonNode? payload = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson);
        lock (_gate)
        {
            ThrowIfOffline();
            if (_fakeHardening.Count == 0)
            {
                // A Basic scan "yesterday", so the page is not empty.
                var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
                for (var i = 0; i < _devices.Count; i++)
                {
                    _fakeHardening[(_devices[i].Id, "Basic")] = FakeHardeningResult(_devices[i], i, "Basic", yesterday);
                }
            }

            switch (method)
            {
                case "getState":
                    var known = _devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                    var results = new JsonArray([.. _fakeHardening.Where(r => known.Contains(r.Key.DeviceId)).Select(r => (JsonNode)Compact(r.Value))]);
                    var state = new JsonObject
                    {
                        ["columns"] = new JsonArray([.. FakeHardeningColumns.Select(c => (JsonNode)JsonValue.Create(c))]),
                        ["results"] = results,
                    };
                    if (_fakeHardeningJob is not null)
                    {
                        state["job"] = _fakeHardeningJob.DeepClone();
                    }

                    return Task.FromResult<string?>(state.ToJsonString(FakeJson));
                case "startScan":
                    return Task.FromResult<string?>(FakeStartHardeningScan(payload).ToJsonString(FakeJson));
                case "cancelScan":
                    _fakeHardeningScan?.Cancel();
                    return Task.FromResult<string?>(_fakeHardeningJob?.ToJsonString(FakeJson));
                case "getDetail":
                    var ids = payload?["deviceIds"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                    var devices = new JsonArray([.. _fakeHardening.Where(r => ids.Contains(r.Key.DeviceId)).Select(r => (JsonNode)r.Value.DeepClone())]);
                    return Task.FromResult<string?>(new JsonObject { ["devices"] = devices }.ToJsonString(FakeJson));
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
            }
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private JsonObject FakeStartHardeningScan(JsonNode? payload)
    {
        if (_fakeHardeningJob?["state"]?.GetValue<string>() == "running")
        {
            return (JsonObject)_fakeHardeningJob.DeepClone();
        }

        var level = payload?["level"]?.GetValue<string>() == "Extended" ? "Extended" : "Basic";
        var wanted = payload?["deviceIds"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var targets = _devices.Select((d, i) => (Device: d.Clone(), Index: i)).Where(x => wanted.Count == 0 || wanted.Contains(x.Device.Id)).ToList();
        var jobId = Guid.NewGuid().ToString("N");
        _fakeHardeningJob = FakeHardeningJob(jobId, level, "running", 0, targets.Count, 0);
        _fakeHardeningScan?.Dispose();
        _fakeHardeningScan = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _fakeHardeningScan.Token;
        _ = Task.Run(() => FakeHardeningScanAsync(jobId, level, targets, token), CancellationToken.None);
        return (JsonObject)_fakeHardeningJob.DeepClone();
    }

    private async Task FakeHardeningScanAsync(string jobId, string level, List<(Device Device, int Index)> targets, CancellationToken ct)
    {
        const int batches = 6;
        var done = 0;
        var failed = 0;
        var cancelled = false;
        var perBatch = Math.Max(1, (targets.Count + batches - 1) / batches);
        foreach (var chunk in targets.Chunk(perBatch))
        {
            try
            {
                await Task.Delay(HardeningScanDuration / batches, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }

            var rows = new JsonArray();
            lock (_gate)
            {
                foreach (var (device, index) in chunk)
                {
                    var result = FakeHardeningResult(device, index, level, DateTimeOffset.UtcNow);
                    _fakeHardening[(device.Id, level)] = result;
                    rows.Add(Compact(result));
                    done++;
                    failed += result["status"] is null ? 0 : 1;
                }

                _fakeHardeningJob = FakeHardeningJob(jobId, level, "running", done, targets.Count, failed);
            }

            PublishHardening("results", new JsonObject { ["results"] = rows });
            PublishHardening("progress", _fakeHardeningJob.DeepClone().AsObject());
        }

        lock (_gate)
        {
            _fakeHardeningJob = FakeHardeningJob(jobId, level, cancelled ? "cancelled" : "done", done, targets.Count, failed);
        }

        PublishHardening("progress", _fakeHardeningJob.DeepClone().AsObject());
    }

    private void PublishHardening(string topic, JsonObject payload) =>
        _pluginEvents.Publish(new PluginEvent { PluginId = HardeningScanPluginId, Topic = topic, PayloadJson = payload.ToJsonString(FakeJson) });

    private static JsonObject FakeHardeningJob(string jobId, string level, string state, int done, int total, int failed) => new()
    {
        ["jobId"] = jobId,
        ["level"] = level,
        ["state"] = state,
        ["done"] = done,
        ["total"] = total,
        ["failed"] = failed,
        ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>The stored (detail) form of one device's result.</summary>
    private static JsonObject FakeHardeningResult(Device device, int index, string level, DateTimeOffset scanned)
    {
        string? status = device.Status switch
        {
            DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
            DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
            DeviceStatus.CertificateChanged => "Certificate changed - accept the new certificate first",
            DeviceStatus.Unreachable => "Unreachable - the device did not answer the last status check",
            _ => null,
        };
        var checks = new JsonArray();
        var count = level == "Extended" ? FakeHardeningColumns.Count : FakeBasicColumns;
        for (var c = 0; c < count; c++)
        {
            var id = FakeHardeningColumns[c];
            var (state, value) = status is not null && id is not ("B2" or "B15" or "E3") ? ("Error", status) : FakeCheck(id, device, index);
            checks.Add(new JsonObject { ["id"] = id, ["state"] = state, ["value"] = value });
        }

        var result = new JsonObject
        {
            ["deviceId"] = device.Id,
            ["level"] = level,
            ["scannedUtc"] = scanned.ToString("O", CultureInfo.InvariantCulture),
            ["checks"] = checks,
        };
        if (status is not null)
        {
            result["status"] = status;
        }

        return result;
    }

    private static (string State, string Value) FakeCheck(string id, Device device, int i)
    {
        var speaker = device.Category is DeviceCategory.Speaker or DeviceCategory.Audio or DeviceCategory.Intercom;
        var video = device.HasVideo;
        return id switch
        {
            "B2" => ("Info", "AXIS OS " + device.FirmwareVersion),
            "B3" => i % 7 == 3 ? ("Warn", "1 administrator") : ("Pass", "2 administrators, 1 operator"),
            "B4" => i % 3 == 0 ? ("Warn", "Password policy: none") : ("Pass", "Password policy: complex passwords"),
            "B5" => device.HasDhcpEnabled && device.DhcpEnabled ? ("Warn", "DHCP") : ("Pass", "Static IP address"),
            "B6" => i % 11 == 5 ? ("Warn", "NTP on, 1 server, synchronized") : ("Pass", "NTP on, 2 servers, synchronized"),
            "B7" => (i % 4) switch { 1 => ("Fail", "SD card not encrypted"), 2 => ("Pass", "SD card encrypted"), _ => ("NotApplicable", "No storage connected") },
            "B8" => i % 5 == 0 ? ("Warn", "Unsigned applications allowed, 1 of 3 not signed") : ("Pass", "2 signed applications running"),
            "B9" => i % 2 == 0 ? ("Warn", "Web interface on") : ("Pass", "Web interface off"),
            "B10" => i % 3 == 1 ? ("Warn", "Bonjour, ZeroConf on") : ("Pass", "Discovery protocols off"),
            "B12" => speaker ? ("NotApplicable", "Audio is the purpose of this device") : ("Pass", "Audio off"),
            "B13" => video ? ("Pass", "Card in use") : ("NotApplicable", "No SD card slot"),
            "B14" => i % 6 == 2 ? ("Fail", "SSH on") : ("Pass", "SSH off"),
            "B15" => ("Pass", "Debug port off by default (AXIS OS " + device.FirmwareVersion + ")"),
            "B20" => (i % 4) switch { 0 => ("Fail", "Firewall off"), 1 => ("Warn", "Firewall on, default accept, 1 rule (limit rules only)"), _ => ("Pass", "Firewall on, default drop, 3 rules") },
            "B21" => i % 9 == 4 ? ("Warn", "2 other ciphers: AES128-SHA, AES256-SHA") : ("Pass", "Recommended ciphers only"),
            "E3" => device.CertTrust switch
            {
                CertificateTrust.Trusted => ("Pass", "Trusted certificate"),
                CertificateTrust.SelfSigned => ("Warn", "Self-signed certificate"),
                CertificateTrust.Untrusted => ("Fail", "Untrusted certificate"),
                CertificateTrust.Expired => ("Fail", "Certificate expired"),
                _ => ("NotApplicable", "Certificate not checked yet"),
            },
            "E4" => i % 3 == 0 ? ("Pass", "Remote syslog over TLS") : ("Fail", "Remote syslog off"),
            "E5" => i % 8 == 6 ? ("Fail", "SNMP v1, v2c on") : ("Pass", "SNMP off"),
            "E6" => video ? ("Warn", "RTSPS off") : ("NotApplicable", "No video"),
            "E7" => ("Warn", "Not configured"),
            "X1" => i % 5 == 2 ? ("Pass", "HTTPS only") : ("Warn", "HTTP and HTTPS"),
            "X2" => device.HasDot1XEnabled && device.Dot1XEnabled ? ("Pass", "IEEE 802.1X on") : ("Warn", "IEEE 802.1X off"),
            "X3" => ("Pass", "Password throttling on"),
            "X4" => ("Warn", "Access log off"),
            "X5" => video ? ("Warn", "Signed video off") : ("NotApplicable", "No video"),
            _ => ("Warn", "NTS off"),
        };
    }

    /// <summary>The compact form (getState, results events): one state code per column.</summary>
    private static JsonObject Compact(JsonObject detail)
    {
        var states = new char[FakeHardeningColumns.Count];
        var values = new JsonArray();
        Array.Fill(states, '-');
        var byId = (detail["checks"]?.AsArray() ?? []).OfType<JsonObject>().ToDictionary(c => c["id"]!.GetValue<string>(), StringComparer.Ordinal);
        for (var c = 0; c < FakeHardeningColumns.Count; c++)
        {
            if (byId.TryGetValue(FakeHardeningColumns[c], out var check))
            {
                states[c] = check["state"]!.GetValue<string>() switch
                {
                    "Pass" => 'p',
                    "Warn" => 'w',
                    "Fail" => 'f',
                    "NotApplicable" => 'n',
                    "Error" => 'e',
                    "Info" => 'i',
                    _ => '-',
                };
                values.Add(check["value"]?.GetValue<string>());
            }
            else
            {
                values.Add(null);
            }
        }

        var result = new JsonObject
        {
            ["deviceId"] = detail["deviceId"]!.GetValue<string>(),
            ["level"] = detail["level"]!.GetValue<string>(),
            ["scannedUtc"] = detail["scannedUtc"]!.GetValue<string>(),
            ["states"] = new string(states),
            ["values"] = values,
        };
        if (detail["status"] is { } status)
        {
            result["status"] = status.GetValue<string>();
        }

        return result;
    }
}
