using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// "Import configuration" (Maintenance, dialog): imports a configuration file into the device, one settings area at a time
/// (a file of another model too: the device leaves out what it does not have and refuses single values it cannot take), always as a merge and without the device's own network identity (<see cref="ConfigIdentity"/>). Only the
/// areas OADM knows are sent (<see cref="ConfigSections.Known"/>). The device
/// validates each area itself; OADM shows its answer. Steps: Read configuration file, Check compatibility, Import &lt;area&gt;
/// (one per area; a failing area does not stop the others), Import connection settings (only when chosen, always last: it can
/// cut OADM off from the device), Verify configuration. Below AXIS OS 12.5 (no configuration API) the parameters are set
/// through param.cgi like Configure devices sets them (checked against the device's own definitions first, the same values
/// left out), every other area is skipped with a warning.
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
            ConfigSections.UserManagement => "Import password policy",
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

    /// <summary>AXIS OS 12.5 or later, or an older one with the parameter API (it takes only the parameters).</summary>
    public bool CanRun(IDeviceInfo device) => DeviceConfigSupport.CanRun(device) || ConfigureSupport.CanRun(device);

    public string? NotSupportedReason(IDeviceInfo device) => CanRun(device) ? null : ConfigureSupport.NotSupportedReason(device);

    /// <summary>The device's AXIS OS has no configuration API: only the parameters are set, the old way.</summary>
    public static bool IsOlderOs(string? version) => !FirmwareVersions.SupportsConfigFiles(version);

    /// <summary>The areas a device below AXIS OS 12.5 takes: the parameters (and their connection part).</summary>
    public static bool IsParameterArea(string key) => key is ConfigSections.Param or ConfigSections.Connection;

    /// <summary>Why an area other than the parameters is skipped below AXIS OS 12.5.</summary>
    public const string OlderOsReason = "Needs AXIS OS 12.5 or later";

    /// <summary>Nothing of the chosen areas is left to import on this device; no write.</summary>
    public const string NoneOnDevice = "None of the settings in the file exist on this device. Nothing was changed.";

    /// <summary>
    /// Importing the connection settings (HTTPS, ports, login method, 802.1X, DNS, the network link: every parameter of
    /// <see cref="DangerousParameters"/>) is a right of its own.
    /// </summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(DeviceConfigPermissions.Import, "Import configuration", "Import settings from a configuration file, without the connection settings."),
        new(DeviceConfigPermissions.Connection, "Import connection settings", "Also import HTTPS, ports, login method and IEEE 802.1X; devices can become unreachable.") { OperatorDefault = false },
    ];

    /// <summary>Every import needs "import"; one with the connection settings area also "connection".</summary>
    public IReadOnlyList<string>? RequiredPermissions(string? payloadJson) =>
        ImportPayload.ParseShared(payloadJson).Sections.Contains(ConfigSections.Connection)
            ? [DeviceConfigPermissions.Import, DeviceConfigPermissions.Connection]
            : [DeviceConfigPermissions.Import];

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

    /// <summary>The input for AI agents (SDK 0.13.0): one uploaded <c>.json</c> for every device (any model), and the areas.</summary>
    public Type? PayloadType => typeof(ImportMcpPayload);

    /// <summary>The names and area keys the task reads.</summary>
    public JsonSerializerOptions? PayloadJsonOptions => ImportPayload.JsonOptions;

    /// <summary>The payload's rules (<see cref="ImportPayload.Check"/>) before any task starts, each at its JSON field.</summary>
    public IReadOnlyList<Oadm.Sdk.Tasks.PayloadError> ValidatePayload(string? payloadJson, ICallContext caller) =>
        ImportPayload.Check(payloadJson).Errors;

    /// <summary>The plan without the uploads (the dialog's own check): the devices the task can run on and the dialog's left-out ones.</summary>
    public Task<Oadm.Sdk.Tasks.TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct) =>
        PlanCoreAsync(payloadJson, devices, null, ct);

    /// <summary>
    /// The plan of a run, from the cached devices and the uploaded file (never a device request): a device the dialog left out
    /// and one the task cannot run on are skipped with the reason; the warnings are the dialog's notes and confirmation (the
    /// settings are replaced, connection settings can make the devices unreachable, devices of another model than the file's).
    /// </summary>
    public Task<Oadm.Sdk.Tasks.TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, IUploadedFiles files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        return PlanCoreAsync(payloadJson, devices, files, ct);
    }

    private async Task<Oadm.Sdk.Tasks.TaskPlan> PlanCoreAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, IUploadedFiles? files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var payload = ImportPayload.Parse(payloadJson);
        var connection = payload.Sections.Contains(ConfigSections.Connection);
        var warnings = new List<string> { ImportTexts.ReplaceNote };
        if (connection)
        {
            warnings.Add(ImportTexts.ConnectionNote);
        }

        if (payload.Sections.Contains(ConfigSections.Param))
        {
            warnings.Add(ImportTexts.IdentityNote);
        }

        ConfigFile? file = null;
        string? fileProblem = null;
        var named = new List<Oadm.Sdk.Tasks.TaskPlanFile>();
        if (files is not null)
        {
            if (await files.FindAsync(payload.FileId, ct).ConfigureAwait(false) is not { } upload)
            {
                fileProblem = ImportTexts.FileGone;
            }
            else
            {
                named.Add(new(upload.Name, upload.Sha256));
                if (!payload.IsZip)
                {
                    (file, fileProblem) = await ReadPlannedFileAsync(files, upload, ct).ConfigureAwait(false);
                }
            }
        }

        if (file is not null)
        {
            if (file.HasRecipients && payload.Sections.Contains(ConfigSections.ActionRules))
            {
                warnings.Add(ImportTexts.PasswordNote);
            }

            if (ConfigSections.UnknownNote(file.NotImported) is { } unknown)
            {
                warnings.Add(unknown);
            }
        }

        var plan = new List<Oadm.Sdk.Tasks.TaskPlanDevice>(devices.Count);
        var hasParameters = payload.Sections.Any(IsParameterArea);
        var older = 0;
        foreach (var device in devices)
        {
            string? reason;
            if (!CanRun(device))
            {
                reason = NotSupportedReason(device) ?? TaskSupportReasons.Default;
            }
            else if (IsOlderOs(device.FirmwareVersion) && !hasParameters)
            {
                reason = TaskSupportReasons.NeedsFirmware(DeviceConfigIds.MinFirmwareText, device);
            }
            else if (fileProblem is not null)
            {
                reason = fileProblem;
            }
            else if (payload.EntryFor(device.Id) is null)
            {
                reason = payload.LeftOut.GetValueOrDefault(device.Id) ?? DeviceConfigIds.NoFile;
            }
            else
            {
                reason = null;
            }

            plan.Add(new(device.Id, reason));
            older += reason is null && IsOlderOs(device.FirmwareVersion) ? 1 : 0;
        }

        if (older > 0 && payload.Sections.Any(key => !IsParameterArea(key)))
        {
            warnings.Add(ImportTexts.OlderOsNote(older, plan.Count(p => p.SkipReason is null)));
        }

        if (payload.ForEveryDevice && file is not null)
        {
            var runs = 0;
            var others = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                if (plan[i].SkipReason is null)
                {
                    runs++;
                    others += ModelNames.Same(file.Model, devices[i].Model) ? 0 : 1;
                }
            }

            if (others > 0)
            {
                warnings.Add(ImportTexts.OtherModelNote(others, runs));
            }
        }

        return new Oadm.Sdk.Tasks.TaskPlan(plan) { Warnings = warnings, Files = named };
    }

    /// <summary>The planned file read like the task reads it; its problem in the dialog's words instead of an exception.</summary>
    private static async Task<(ConfigFile? File, string? Problem)> ReadPlannedFileAsync(IUploadedFiles files, UploadedFile upload, CancellationToken ct)
    {
        if (upload.Size > DeviceConfigIds.MaxFileBytes)
        {
            return (null, DeviceConfigIds.NotAConfigFile + ": larger than 2 MB");
        }

        try
        {
            var stream = await files.OpenReadAsync(upload.Id, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return (await ConfigFile.ReadAsync(stream, ct).ConfigureAwait(false), null);
            }
        }
        catch (ConfigFileException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, ImportTexts.FileGone);
        }
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = ImportPayload.ParseShared(payloadJson); // one entry per device: parsed once per run, not per device
        if (payload.EntryFor(device.Id) is not { } entry)
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
        Dictionary<string, ParamDefinition>? olderDefinitions = null;
        using (var step = ctx.BeginStep(Steps.CheckCompatibility))
        {
            if (TaskSupportReasons.ForStatus(device) is { } refused)
            {
                throw new ConfigDeviceException(refused + ". Nothing was changed.");
            }

            var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            if (IsOlderOs(info.Version))
            {
                // No configuration API: the parameters the old way, checked against this device's own definitions.
                olderDefinitions = await CheckOlderAsync(ctx, file, sections, info.Version, ct).ConfigureAwait(false);
                available = new HashSet<string>(StringComparer.Ordinal);
            }
            else
            {
                available = await ConfigApi.DiscoverAsync(ctx.Vapix, ct).ConfigureAwait(false);
                if (!sections.Any(key => ConfigSections.IsKnown(key) && file.Data[ConfigSections.SourceKey(key)] is JsonObject && available.Contains(ConfigSections.SourceKey(key))))
                {
                    throw new ConfigDeviceException(NoneOnDevice);
                }
            }

            // Another model takes only what it has: the device leaves out unknown settings and refuses single values.
            step.Complete(CompatibilityText(file, info));
        }

        if (olderDefinitions is not null)
        {
            await ImportOlderAsync(ctx, file, sections, olderDefinitions, ct).ConfigureAwait(false);
            return;
        }

        var imported = new List<(string Key, JsonNode Data, HashSet<string> Removed)>();
        var failures = new List<string>();
        foreach (var key in sections)
        {
            var name = Steps.Import(key);
            if (!ConfigSections.IsKnown(key))
            {
                // An area OADM does not know the identity values of: never sent, whatever the payload asks for.
                ctx.SkipStep(name, ConfigSections.UnknownReason);
                continue;
            }

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

    /// <summary>"AXIS P3265-V, AXIS OS 12.11.77 (file of AXIS Q6135-LE)".</summary>
    private static string CompatibilityText(ConfigFile file, Oadm.Sdk.Vapix.BasicDeviceInfo info) =>
        $"{ModelNames.Display(info.ProdNbr)}, AXIS OS {info.Version}"
        + (ModelNames.Same(file.Model, info.ProdNbr) ? string.Empty : $" (file of {ModelNames.Display(file.Model)})");

    /// <summary>
    /// The check of a device below AXIS OS 12.5, before any write: the parameter API (a fresh API list), parameters among the
    /// chosen areas, and this device's definitions of their groups (each value is checked against them before the write).
    /// </summary>
    private static async Task<Dictionary<string, ParamDefinition>> CheckOlderAsync(ITaskExecutionContext ctx, ConfigFile file, IReadOnlyList<string> sections,
        string version, CancellationToken ct)
    {
        var needs = $"Needs AXIS OS {DeviceConfigIds.MinFirmwareText} or later (this device has {version}). Nothing was changed.";
        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        if (apis.Count > 0 && !apis.Any(a => string.Equals(a.Id, ConfigureDevicesTask.ParamApi, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConfigDeviceException(needs);
        }

        if (file.Data[ConfigSections.Param] is not JsonObject param || !sections.Any(IsParameterArea))
        {
            throw new ConfigDeviceException(needs);
        }

        // Only identity and connection values (connection settings not chosen): nothing to import, whatever the AXIS OS.
        var names = sections.Where(IsParameterArea).SelectMany(key => ConfigParameters.Flatten(ConfigIdentity.Clean(key, param))).Select(p => p.Name).ToList();
        if (names.Count == 0)
        {
            throw new ConfigDeviceException(NoneOnDevice);
        }

        var definitions = new Dictionary<string, ParamDefinition>(StringComparer.Ordinal);
        foreach (var definition in await ParamDefinitionsReader.ReadAsync(ctx.Vapix, ct, names).ConfigureAwait(false))
        {
            definitions[definition.Name] = definition;
        }

        return definitions;
    }

    /// <summary>
    /// The import on a device below AXIS OS 12.5: each parameter area through param.cgi with the values of the 12.5 import
    /// (identity, read-only and, unless chosen, connection values left out), only parameters this device can set, each
    /// checked against its definition first, a refused one skipped by name; connection settings last. Every other area is
    /// skipped, with one warning at the end. Then the written values are read back.
    /// </summary>
    private static async Task ImportOlderAsync(ITaskExecutionContext ctx, ConfigFile file, IReadOnlyList<string> sections,
        Dictionary<string, ParamDefinition> definitions, CancellationToken ct)
    {
        var written = new List<NameValue>();
        var notApplied = new List<string>();
        var failures = new List<string>();
        var connectionWritten = false;
        foreach (var key in sections)
        {
            var name = Steps.Import(key);
            if (!ConfigSections.IsKnown(key))
            {
                ctx.SkipStep(name, ConfigSections.UnknownReason);
                continue;
            }

            if (file.Data[ConfigSections.SourceKey(key)] is not JsonObject section)
            {
                ctx.SkipStep(name, "Not in the file");
                continue;
            }

            if (!IsParameterArea(key))
            {
                ctx.SkipStep(name, OlderOsReason);
                notApplied.Add(ConfigSections.Name(key));
                continue;
            }

            var values = ConfigParameters.Flatten(ConfigIdentity.Clean(key, section));
            if (values.Count == 0)
            {
                ctx.SkipStep(name, "Nothing to import");
                continue;
            }

            var secrets = values.Where(v => definitions.GetValueOrDefault(v.Name)?.Kind == ParamKind.Password || SecretNames.IsSecret(v.Name)).Select(v => v.Value).ToList();
            using var step = ctx.BeginStep(name);
            try
            {
                var apply = new List<NameValue>();
                var skipped = new List<string>();
                var missing = 0;
                foreach (var value in values)
                {
                    if (!definitions.TryGetValue(value.Name, out var definition))
                    {
                        missing++; // not on this device, or one nobody may set (read-only, internal)
                    }
                    else if (ParamRules.Problem(definition, value.Value) is { } problem)
                    {
                        skipped.Add($"{value.Name} ({problem.TrimEnd('.')})");
                    }
                    else
                    {
                        // Written like Configure devices writes it: trimmed; text and passwords exactly as in the file.
                        apply.Add(definition.Kind is ParamKind.Text or ParamKind.Password ? value : value with { Value = value.Value.Trim() });
                    }
                }

                foreach (var (bad, message) in await ParamWriter.UpdateSkippingRefusedAsync(ctx.Vapix, apply, secrets, ct).ConfigureAwait(false))
                {
                    skipped.Add($"{bad} (the device refused it)");
                    ctx.Log(TaskLogLevel.Warning, $"{ConfigSections.Name(key)}: {message}");
                }

                foreach (var item in skipped)
                {
                    ctx.Log(TaskLogLevel.Warning, $"{ConfigSections.Name(key)}: skipped {item}");
                }

                written.AddRange(apply.Where(v => definitions[v.Name].Kind != ParamKind.Password));
                connectionWritten |= key == ConfigSections.Connection && apply.Count > 0;
                var done = DeviceConfigIds.Count(apply.Count, "value")
                    + (missing > 0 ? string.Create(CultureInfo.InvariantCulture, $", {missing:N0} not on this device or read-only") : string.Empty);
                if (skipped.Count > 0)
                {
                    step.Warn(string.Create(CultureInfo.InvariantCulture, $"{done}, {skipped.Count:N0} skipped: {Summary(skipped, 3)}"));
                }
                else
                {
                    step.Complete(done);
                }
            }
            catch (Exception ex) when (!DeviceErrors.IsCancelled(ex, ct))
            {
                // A changed certificate stops the import: every later request fails the same way.
                if (DeviceErrors.IsCertificateChanged(ex))
                {
                    throw new ConfigDeviceException(DeviceMessages.CertificateChanged, ex);
                }

                var message = ParamWriter.Scrub(DeviceErrors.Describe(ex, ParamWriter.Timeout), secrets);
                step.Fail(message);
                ctx.Log(TaskLogLevel.Error, $"{ConfigSections.Name(key)}: {message}");
                failures.Add($"{ConfigSections.Name(key)}: {message}");
            }
        }

        if (await VerifyOlderAsync(ctx, written, connectionWritten, ct).ConfigureAwait(false) is { } verifyFailure)
        {
            failures.Add(verifyFailure);
        }

        if (failures.Count > 0)
        {
            throw new ConfigDeviceException(failures.Count == 1
                ? failures[0]
                : string.Create(CultureInfo.InvariantCulture, $"{failures[0]} (+{failures.Count - 1} more failed)"));
        }

        // Last, so it is the task's message: what the user chose and this device cannot take.
        if (notApplied.Count > 0)
        {
            ctx.ReportWarning(ImportTexts.NotAppliedNote(notApplied));
        }
    }

    /// <summary>Reads the values written through param.cgi back (passwords are not); differences are a warning.</summary>
    private static async Task<string?> VerifyOlderAsync(ITaskExecutionContext ctx, List<NameValue> written, bool connectionWritten, CancellationToken ct)
    {
        if (written.Count == 0)
        {
            ctx.SkipStep(Steps.Verify, "Nothing was imported");
            return null;
        }

        using var step = ctx.BeginStep(Steps.Verify);
        Dictionary<string, string> now;
        try
        {
            now = await ParamWriter.ReadBackAsync(ctx.Vapix, written.Select(v => v.Name), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!DeviceErrors.IsCancelled(ex, ct))
        {
            var message = DeviceErrors.IsCertificateChanged(ex) ? DeviceMessages.CertificateChanged : DeviceErrors.Describe(ex, timeout: null);
            if (connectionWritten)
            {
                // After new connection settings the device may be gone for OADM: expected, a warning.
                step.Warn("The device could not be read after the connection settings changed: " + message);
                return null;
            }

            step.Fail(message);
            ctx.Log(TaskLogLevel.Error, $"{Steps.Verify}: {message}");
            return $"{Steps.Verify}: {message}";
        }

        var differences = written.Where(v => !now.TryGetValue(v.Name, out var value) || !string.Equals(value, v.Value, StringComparison.Ordinal)).Select(v => v.Name).ToList();
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
