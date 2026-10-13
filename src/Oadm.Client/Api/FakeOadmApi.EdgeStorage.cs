using System.Globalization;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake Edge storage so <c>--fake</c> and the tests show SD cards: every video device has a card (64 or 128 GB, filled by its
/// position in the list), the M3106-L Mk II has none (like the fake Edge Recording Export), the Q1656-LE's card is read-only,
/// the M4317-PLVE's card is worn past the camera's alert level, the P3727-PLE's card is encrypted with 30 days of retention;
/// devices with a refused status are not read. A run creates one fake task per camera with the plugin's step names. JSON of
/// the plugin (camelCase).
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string EdgeStoragePluginId = "oadm.edge-storage";

    private const string EdgeStorageTaskId = "oadm.edge-storage.task";

    private async Task<string?> InvokeEdgeStorageAsync(string method, string? payloadJson, CancellationToken ct)
    {
        await Task.Delay(100, ct).ConfigureAwait(false);
        return method switch
        {
            "read" or "getState" => FakeStorageState(),
            "change" or "format" => FakeStorageRun(method, JsonNode.Parse(payloadJson ?? "{}")!.AsObject()),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'.")),
        };
    }

    private string FakeStorageState()
    {
        List<Device> devices;
        lock (_gate)
        {
            ThrowIfOffline();
            devices = [.. _devices.Where(d => d.HasVideo).Select(d => d.Clone())];
        }

        var rows = new JsonArray();
        int ok = 0, warnings = 0, problems = 0, noCard = 0, failed = 0;
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var row = new JsonObject { ["deviceId"] = d.Id, ["address"] = d.Address, ["model"] = d.Model, ["readUtc"] = DateTimeOffset.UtcNow };
            var refusal = d.Status switch
            {
                DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
                DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
                DeviceStatus.CertificateChanged => "Certificate changed. Remove the device and add it again to trust the new certificate.",
                DeviceStatus.Unreachable => "Unreachable - the device does not answer",
                _ => null,
            };
            if (refusal is not null)
            {
                row["state"] = "Error";
                row["text"] = refusal;
                failed++;
            }
            else if (d.Model.Contains("M3106-L", StringComparison.Ordinal))
            {
                row["state"] = "No SD card";
                noCard++;
            }
            else
            {
                var total = i % 2 == 0 ? 57922892L : 116109324L;
                row["totalKb"] = total;
                row["freeKb"] = total * (90 - (i * 17 % 80)) / 100;
                row["wearPercent"] = d.Model.Contains("M4317", StringComparison.Ordinal) ? 93 : i * 3 % 40;
                row["wearAlertPercent"] = 90;
                row["encrypted"] = d.Model.Contains("P3727", StringComparison.Ordinal);
                row["retentionDays"] = d.Model.Contains("P3727", StringComparison.Ordinal) ? 30 : 7;
                row["mounted"] = true;
                if (d.Model.Contains("Q1656", StringComparison.Ordinal))
                {
                    row["state"] = "Problem";
                    row["text"] = "The SD card is read-only";
                    problems++;
                }
                else if (d.Model.Contains("M4317", StringComparison.Ordinal))
                {
                    row["state"] = "Warning";
                    row["text"] = "Worn 93 % (the camera warns at 90 %). Replace the SD card soon";
                    warnings++;
                }
                else
                {
                    row["state"] = "OK";
                    ok++;
                }
            }

            rows.Add(row);
        }

        return new JsonObject
        {
            ["reading"] = false,
            ["done"] = devices.Count,
            ["total"] = devices.Count,
            ["ok"] = ok,
            ["warnings"] = warnings,
            ["problems"] = problems,
            ["noCard"] = noCard,
            ["failed"] = failed,
            ["readUtc"] = DateTimeOffset.UtcNow,
            ["rows"] = rows,
        }.ToJsonString();
    }

    private string FakeStorageRun(string method, JsonObject request)
    {
        var action = request["action"]?.GetValue<string>() ?? string.Empty;
        var days = request["retentionDays"]?.GetValue<int>();
        var change = action is "check" or "retention";
        if (change != (method == "change"))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "This action needs the other right."));
        }

        var name = action switch
        {
            "check" => "Check SD card",
            "repair" => "Repair SD card",
            "format" => "Format SD card",
            "encryptionOn" => "Encrypt SD card",
            "encryptionOff" => "Turn off SD card encryption",
            "retention" when days is 0 => "Remove SD card retention limit",
            "retention" when days is 1 => "Set SD card retention to 1 day",
            "retention" => string.Create(CultureInfo.InvariantCulture, $"Set SD card retention to {days} days"),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown action. Nothing was changed.")),
        };
        string[] steps = action switch
        {
            "retention" => ["Check compatibility", "Read the SD card", "Set the retention"],
            "encryptionOn" => ["Check compatibility", "Read the SD card", "Unmount the SD card", "Turn on encryption", "Format the SD card", "Mount the SD card"],
            "encryptionOff" => ["Check compatibility", "Read the SD card", "Unmount the SD card", "Turn off encryption", "Format the SD card", "Mount the SD card"],
            _ => ["Check compatibility", "Read the SD card", "Unmount the SD card", $"{char.ToUpperInvariant(action[0])}{action[1..]} the SD card", "Mount the SD card"],
        };
        var ids = request["deviceIds"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        var owner = request["owner"]?.GetValue<string>() ?? "admin";
        lock (_gate)
        {
            ThrowIfOffline();
            var taskIds = new JsonArray();
            foreach (var task in AddBatch(EdgeStorageTaskId, name, owner, TaskState.Queued, 0, ids.Distinct()))
            {
                _jobs[task.Id] = new FakeJob(task, 20, null, steps);
                taskIds.Add(task.Id);
            }

            return taskIds.ToJsonString();
        }
    }
}
