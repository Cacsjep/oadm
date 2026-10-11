using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// "Import configuration" (Maintenance, dialog): imports a configuration file of the same model into the device, one settings
/// area at a time, always as a merge and without the device's own network identity (<see cref="ConfigIdentity"/>). The device
/// validates each area itself; OADM shows its answer. Steps: Read configuration file, Check compatibility, Import &lt;area&gt;
/// (one per area; a failing area does not stop the others), Import connection settings (only when chosen, always last: it can
/// cut OADM off from the device), Verify configuration.
/// </summary>
public sealed class ImportConfigTask : ITaskPlugin
{
    public static class Steps
    {
        public const string ReadFile = "Read configuration file";
        public const string CheckCompatibility = "Check compatibility";
        public const string Verify = "Verify configuration";

        /// <summary>"Import time zone".</summary>
        public static string Import(string key) => key switch
        {
            ConfigSections.Param => "Import network and system settings",
            ConfigSections.ActionRules => "Import event rules",
            "event-schedules.v2" => "Import schedules",
            "event-mqtt-bridge.v2" => "Import MQTT events",
            "time.v2" => "Import time zone",
            ConfigSections.PowerSettings => "Import power settings",
            "user-management.v2" => "Import password policy",
            ConfigSections.Connection => "Import connection settings",
            _ => "Import " + key,
        };
    }

    /// <summary>Most differing settings the verify step names; the task log lists all.</summary>
    public const int MaxNamedDifferences = 5;

    public string Id => DeviceConfigIds.Import;

    public string DisplayName => DeviceConfigIds.ImportName;

    public string? IconKey => "download";

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    public bool CanRun(IDeviceInfo device) => DeviceConfigSupport.CanRun(device);

    public string? NotSupportedReason(IDeviceInfo device) => DeviceConfigSupport.NotSupportedReason(device);

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            return ImportPayload.Parse(payloadJson).TaskName();
        }
        catch (ArgumentException)
        {
            return DisplayName;
        }
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = ImportPayload.Parse(payloadJson);
        if (!payload.Files.TryGetValue(device.Id, out var entry))
        {
            using var leftOut = ctx.BeginStep(Steps.CheckCompatibility);
            leftOut.Warn((payload.LeftOut.GetValueOrDefault(device.Id) ?? DeviceConfigIds.NoFile) + ". Nothing was changed.");
            return;
        }

        // The connection settings can cut OADM off from the device: their own step, always last.
        var sections = payload.Sections.Distinct(StringComparer.Ordinal).OrderBy(key => key == ConfigSections.Connection).ToList();
        ctx.PlanSteps([Steps.ReadFile, Steps.CheckCompatibility, .. sections.Select(Steps.Import), Steps.Verify]);

        ConfigFile file;
        using (var step = ctx.BeginStep(Steps.ReadFile))
        {
            file = await ReadFileAsync(ctx, payload, entry, device, ct).ConfigureAwait(false);
            step.Complete(file.SourceText);
        }

        IReadOnlySet<string> available;
        using (var step = ctx.BeginStep(Steps.CheckCompatibility))
        {
            if (TaskSupportReasons.ForStatus(device.Status) is { } refused)
            {
                throw new ConfigDeviceException(refused + ". Nothing was changed.");
            }

            var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            if (!FirmwareVersions.SupportsConfigFiles(info.Version))
            {
                throw new ConfigDeviceException($"Needs AXIS OS {DeviceConfigIds.MinFirmwareText} or later (this device has {info.Version}). Nothing was changed.");
            }

            if (!ModelNames.Same(file.Model, info.ProdNbr))
            {
                throw new ConfigDeviceException($"The file is for {ModelNames.Display(file.Model)}; this device is {ModelNames.Display(info.ProdNbr)}. Nothing was changed.");
            }

            available = await ConfigApi.DiscoverAsync(ctx.Vapix, ct).ConfigureAwait(false);
            if (!sections.Any(key => file.Data[ConfigSections.SourceKey(key)] is JsonObject && available.Contains(ConfigSections.SourceKey(key))))
            {
                throw new ConfigDeviceException("None of the settings in the file exist on this device. Nothing was changed.");
            }

            step.Complete($"{ModelNames.Display(info.ProdNbr)}, AXIS OS {info.Version}");
        }

        var imported = new List<(string Key, JsonNode Data, HashSet<string> Removed)>();
        var failures = new List<string>();
        foreach (var key in sections)
        {
            var name = Steps.Import(key);
            var target = ConfigSections.SourceKey(key);
            if (file.Data[target] is not JsonObject section)
            {
                ctx.SkipStep(name, "Not in the file");
                continue;
            }

            if (!available.Contains(target))
            {
                ctx.SkipStep(name, "This device does not have these settings");
                continue;
            }

            var data = ConfigIdentity.Clean(key, section);
            if (data is JsonObject { Count: 0 })
            {
                ctx.SkipStep(name, "Nothing to import");
                continue;
            }

            using var step = ctx.BeginStep(name);
            try
            {
                var answer = await ConfigApi.ImportAsync(ctx.Vapix, target, data, ct).ConfigureAwait(false);
                if (answer.Refused is { } refused)
                {
                    throw new ConfigDeviceException("The device refused these settings: " + refused + ". Nothing of them was changed.");
                }

                imported.Add((target, data, answer.RemovedFields.ToHashSet(StringComparer.Ordinal)));
                foreach (var text in answer.Information)
                {
                    ctx.Log(TaskLogLevel.Info, $"{ConfigSections.Name(key)}: {text}");
                }

                foreach (var text in answer.Warnings)
                {
                    ctx.Log(TaskLogLevel.Warning, $"{ConfigSections.Name(key)}: {text}");
                }

                var values = DeviceConfigIds.Count(Math.Max(0, ConfigIdentity.CountImported(key, section) - answer.RemovedFields.Count), "value")
                    + (answer.RemovedFields.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $", {answer.RemovedFields.Count:N0} not on this device") : string.Empty);
                if (answer.Warnings.Count > 0)
                {
                    step.Warn(Summary(answer.Warnings, 3));
                }
                else
                {
                    step.Complete(values);
                }
            }
            catch (Exception ex) when (!DeviceErrors.IsCancelled(ex, ct))
            {
                // A changed certificate stops the import: every later request fails the same way.
                if (DeviceErrors.IsCertificateChanged(ex))
                {
                    throw new ConfigDeviceException(DeviceMessages.CertificateChanged, ex);
                }

                // Any other failure (refused, timeout, connection lost) fails this area only; the others still run.
                var message = DeviceErrors.Describe(ex, ConfigApi.ImportTimeout);
                step.Fail(message);
                ctx.Log(TaskLogLevel.Error, $"{ConfigSections.Name(key)}: {message}");
                failures.Add($"{ConfigSections.Name(key)}: {message}");
            }
        }

        if (await VerifyAsync(ctx, imported, ct).ConfigureAwait(false) is { } verifyFailure)
        {
            failures.Add(verifyFailure);
        }

        if (failures.Count > 0)
        {
            throw new ConfigDeviceException(failures.Count == 1
                ? failures[0]
                : string.Create(CultureInfo.InvariantCulture, $"{failures[0]} (+{failures.Count - 1} more failed)"));
        }
    }

    private static async Task<ConfigFile> ReadFileAsync(ITaskExecutionContext ctx, ImportPayload payload, string entry, IDeviceInfo device, CancellationToken ct)
    {
        const string Gone = "The configuration file is no longer on the server. Import it again. Nothing was changed.";

        // The dialog matched the ZIP entry by the MAC address in its name; the server checks it again.
        if (payload.IsZip && !ConfigFileNames.SerialsIn(entry).Contains(ConfigFileNames.NormalizeSerial(device.Serial), StringComparer.Ordinal))
        {
            throw new ConfigFileException($"The file {entry} is not for this device ({ConfigFileNames.NormalizeSerial(device.Serial)}). Nothing was changed.");
        }

        if (await ctx.Files.FindAsync(payload.FileId, ct).ConfigureAwait(false) is null)
        {
            throw new ConfigFileException(Gone);
        }

        Stream stream;
        try
        {
            stream = await ctx.Files.OpenReadAsync(payload.FileId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed between the two calls (retention, quota or a delete).
            throw new ConfigFileException(Gone, ex);
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                if (!payload.IsZip)
                {
                    return await ConfigFile.ReadAsync(stream, ct).ConfigureAwait(false);
                }

                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                var zipEntry = zip.GetEntry(entry) ?? throw new ConfigFileException($"The file has no entry {entry}. Nothing was changed.");
                var content = zipEntry.Open();
                await using (content.ConfigureAwait(false))
                {
                    return await ConfigFile.ReadAsync(content, ct).ConfigureAwait(false);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new ConfigFileException(DeviceConfigIds.NotAConfigFile + ". Nothing was changed.", ex);
            }
        }
    }

    /// <summary>
    /// Exports again and compares every imported value; differences are a warning. Returns the failure text when the device
    /// could not be read (e.g. the connection settings cut OADM off), else null.
    /// </summary>
    private static async Task<string?> VerifyAsync(ITaskExecutionContext ctx, List<(string Key, JsonNode Data, HashSet<string> Removed)> imported, CancellationToken ct)
    {
        if (imported.Count == 0)
        {
            ctx.SkipStep(Steps.Verify, "Nothing was imported");
            return null;
        }

        using var step = ctx.BeginStep(Steps.Verify);
        JsonObject? exported;
        try
        {
            exported = (JsonNode.Parse(await ConfigApi.ExportAsync(ctx.Vapix, ct).ConfigureAwait(false)) as JsonObject)?["data"] as JsonObject;
        }
        catch (JsonException)
        {
            exported = null;
        }
        catch (Exception ex) when (!DeviceErrors.IsCancelled(ex, ct))
        {
            var message = DeviceErrors.IsCertificateChanged(ex) ? DeviceMessages.CertificateChanged : DeviceErrors.Describe(ex, timeout: null);
            step.Fail(message);
            ctx.Log(TaskLogLevel.Error, $"{Steps.Verify}: {message}");
            return $"{Steps.Verify}: {message}";
        }

        if (exported is null)
        {
            step.Warn("The device sent an unreadable configuration; the import could not be checked.");
            return null;
        }

        var differences = new List<string>();
        foreach (var (key, data, removed) in imported)
        {
            // Settings the device said it does not know are not differences.
            var prefix = key == ConfigSections.Param ? string.Empty : ConfigSections.Name(key) + ": ";
            differences.AddRange(ConfigCompare.Differences(data, exported[key])
                .Where(path => removed.Count == 0 || !path.Split('.').Any(removed.Contains))
                .Select(path => prefix + path));
        }

        if (differences.Count == 0)
        {
            step.Complete("All imported values match");
            return null;
        }

        foreach (var path in differences)
        {
            ctx.Log(TaskLogLevel.Warning, "Differs after the import: " + path);
        }

        var count = differences.Count == 1 ? "1 setting differs" : DeviceConfigIds.Count(differences.Count, "setting") + " differ";
        step.Warn($"{count} after the import: {Summary(differences, MaxNamedDifferences)}");
        return null;
    }

    /// <summary>"a; b; c (+4 more)".</summary>
    private static string Summary(IReadOnlyList<string> items, int max) =>
        string.Join("; ", items.Take(max)) + (items.Count > max ? string.Create(CultureInfo.InvariantCulture, $" (+{items.Count - max} more)") : string.Empty);
}
