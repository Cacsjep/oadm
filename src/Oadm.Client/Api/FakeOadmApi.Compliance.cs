using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode: compliance checks in memory. The default checks, "Object Analytics running", "Matches base-config.cfg" and a
/// certificate check; results are worked out from the sample devices (the device-reading kinds fail on a fixed share of
/// them) when a page subscribes, after a save and on Check now.
/// </summary>
public sealed partial class FakeOadmApi
{
    private const string ReachableType = "oadm.compliance.reachable";
    private const string LoginType = "oadm.compliance.login-works";
    private const string CertificateType = "oadm.compliance.certificate";
    private const string FirmwareType = "oadm.compliance.firmware";
    private const string AppType = "oadm.acap.running";
    private const string ConfigType = "oadm.device-config.matches-file";
    private const string SdType = "oadm.edge-storage.sd-card";

    private readonly List<ComplianceCheckDetail> _complianceChecks = [];
    private readonly Broadcast<ComplianceCheckList> _complianceCheckEvents = new();
    private readonly Broadcast<ComplianceResultBatch> _complianceResultEvents = new();
    private Dictionary<(string Check, string Device), ComplianceResultEntry> _complianceResults = [];
    private bool _complianceSeeded;

    /// <summary>Compliance is off by default, like on a server.</summary>
    public bool ComplianceEnabled { get; private set; }

    public Task<ComplianceCheckList> SetComplianceEnabledAsync(bool enabled, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedComplianceLocked();
            ComplianceEnabled = enabled;
            RecomputeComplianceLocked();
            return Task.FromResult(ComplianceListLocked());
        }
    }

    public Task<ComplianceCatalog> GetComplianceCatalogAsync(CancellationToken ct)
    {
        var catalog = new ComplianceCatalog();
        catalog.Entries.Add(Type(ReachableType, "Reachable", "Devices", "The device answers OADM.", false));
        catalog.Entries.Add(Type(LoginType, "Login works", "Devices", "OADM can log in to the device.", false));
        var certificate = Type(CertificateType, "Certificate", "Devices", "The HTTPS certificate of the device is valid and trusted.", false, ComplianceSeverity.ComplianceWarning);
        certificate.Fields.Add(new ActionFieldEntry { Key = "expired", Label = "Fail when expired", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "true" });
        certificate.Fields.Add(new ActionFieldEntry { Key = "expiresSoon", Label = "Fail when it expires soon", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "true" });
        certificate.Fields.Add(new ActionFieldEntry
        {
            Key = "days", Label = "Days left at least", Kind = ActionFieldKind.ActionFieldNumber, Required = true, Min = 1, Max = 3650, DefaultValue = "30",
            VisibleWhenKey = "expiresSoon", VisibleWhenValue = "true",
        });
        certificate.Fields.Add(new ActionFieldEntry { Key = "notTrusted", Label = "Fail when not trusted (self-signed or unknown issuer)", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false" });
        catalog.Entries.Add(certificate);
        var firmware = Type(FirmwareType, "Firmware", "Devices", "The device runs at least the AXIS OS version you set.", false);
        firmware.Fields.Add(new ActionFieldEntry { Key = "minimum", Label = "At least AXIS OS", Kind = ActionFieldKind.ActionFieldText, Required = true, MaxLength = 32, Hint = "For example 12.5.56" });
        catalog.Entries.Add(firmware);
        var app = Type(AppType, "Application running", "Applications", "The application is installed and running on the device.", true, source: "Applications (ACAP)");
        app.Fields.Add(new ActionFieldEntry { Key = "application", Label = "Application", Kind = ActionFieldKind.ActionFieldDynamicChoice, Required = true, ChoicesFromDevice = true, Hint = "Read the list from a device that has it." });
        var state = new ActionFieldEntry { Key = "state", Label = "Must be", Kind = ActionFieldKind.ActionFieldChoice, Required = true, DefaultValue = "running" };
        state.Choices.Add(new ActionChoiceEntry { Value = "running", Label = "Running" });
        state.Choices.Add(new ActionChoiceEntry { Value = "installed", Label = "Installed" });
        app.Fields.Add(state);
        app.Fields.Add(new ActionFieldEntry { Key = "minimumVersion", Label = "At least version", Kind = ActionFieldKind.ActionFieldText, MaxLength = 32, Hint = "Optional" });
        app.Fields.Add(new ActionFieldEntry { Key = "license", Label = "License must be valid", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false" });
        catalog.Entries.Add(app);
        var config = Type(ConfigType, "Matches configuration file", "Configuration", "The device has the settings of a configuration file (parameters, action rules, stream profiles).", true, source: "Device configuration");
        var file = new ActionFieldEntry { Key = "file", Label = "Configuration file", Kind = ActionFieldKind.ActionFieldFile, Required = true, MaxLength = 1024 * 1024, Hint = "Made with Create configuration file or AXIS Device Manager." };
        file.Accept.Add(".cfg");
        config.Fields.Add(file);
        catalog.Entries.Add(config);
        var sd = Type(SdType, "SD card", "Storage", "The device has a working SD card with enough free space.", true, source: "Edge storage");
        sd.Fields.Add(new ActionFieldEntry { Key = "minFreeGb", Label = "At least free (GB)", Kind = ActionFieldKind.ActionFieldNumber, Min = 1, Max = 100_000, Hint = "Optional" });
        sd.Fields.Add(new ActionFieldEntry { Key = "minFreePercent", Label = "At least free (%)", Kind = ActionFieldKind.ActionFieldNumber, Min = 1, Max = 99, Hint = "Optional" });
        catalog.Entries.Add(sd);
        return Task.FromResult(catalog);
    }

    private static ComplianceTypeEntry Type(string id, string name, string group, string description, bool readsDevices,
        ComplianceSeverity severity = ComplianceSeverity.ComplianceProblem, string source = "Built in") =>
        new() { Id = id, DisplayName = name, Group = group, Description = description, ReadsDevices = readsDevices, DefaultSeverity = severity, Available = true, SourceName = source };

    public async IAsyncEnumerable<ComplianceCheckList> WatchComplianceChecksAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<ComplianceCheckList> channel;
        ComplianceCheckList first;
        lock (_gate)
        {
            ThrowIfOffline();
            SeedComplianceLocked();
            channel = _complianceCheckEvents.Subscribe();
            first = ComplianceListLocked();
        }

        try
        {
            yield return first;
            await foreach (var list in channel.Reader.ReadAllAsync(ct))
            {
                yield return list;
            }
        }
        finally
        {
            _complianceCheckEvents.Unsubscribe(channel);
        }
    }

    public async IAsyncEnumerable<ComplianceResultBatch> WatchComplianceResultsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<ComplianceResultBatch> channel;
        ComplianceResultBatch first;
        lock (_gate)
        {
            ThrowIfOffline();
            SeedComplianceLocked();
            channel = _complianceResultEvents.Subscribe();
            first = new ComplianceResultBatch { SnapshotEnd = true };
            first.Results.AddRange(_complianceResults.Values);
        }

        try
        {
            yield return first;
            await foreach (var batch in channel.Reader.ReadAllAsync(ct))
            {
                yield return batch;
            }
        }
        finally
        {
            _complianceResultEvents.Unsubscribe(channel);
        }
    }

    public Task<ComplianceCheckDetail> GetComplianceCheckAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            SeedComplianceLocked();
            var check = _complianceChecks.Find(c => c.Summary.Id == id) ?? throw new RpcException(new Status(StatusCode.NotFound, "The check no longer exists."));
            return Task.FromResult(check.Clone());
        }
    }

    public Task<ComplianceCheckSummary> SaveComplianceCheckAsync(ComplianceCheckDetail check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        lock (_gate)
        {
            ThrowIfOffline();
            SeedComplianceLocked();
            if (check.Summary.Name.Trim().Length == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Enter a name."));
            }

            var stored = check.Clone();
            DeviceOwner? before = _complianceChecks.Find(c => c.Summary.Id == stored.Summary.Id && stored.Summary.Id.Length > 0)?.Summary.Owner;
            stored.Summary.Owner = ComplianceOwnerLocked(check.Summary.Owner, before, stored.Summary.Enabled);
            if (string.IsNullOrEmpty(stored.Summary.Id))
            {
                stored.Summary.Id = Guid.NewGuid().ToString();
                stored.Summary.Version = 1;
                _complianceChecks.Add(stored);
            }
            else
            {
                var index = _complianceChecks.FindIndex(c => c.Summary.Id == stored.Summary.Id);
                if (index < 0)
                {
                    throw new RpcException(new Status(StatusCode.NotFound, "The check no longer exists."));
                }

                var old = _complianceChecks[index].Summary;

                stored.Summary.Version = old.Version + 1;
                _complianceChecks[index] = stored;
            }

            RecomputeComplianceLocked();
            return Task.FromResult(ComplianceListLocked().Checks.First(c => c.Id == stored.Summary.Id));
        }
    }

    /// <summary>
    /// Like the server: unset keeps the owner (a new check: the caller's devices), only administrators choose another one,
    /// a check without an owner (its group was deleted) cannot be turned on.
    /// </summary>
    private DeviceOwner ComplianceOwnerLocked(DeviceOwner? requested, DeviceOwner? before, bool enabled)
    {
        DeviceOwner caller = ViewRulesLocked() is null
            ? new DeviceOwner { AllDevices = true }
            : new DeviceOwner { GroupId = _users.First(u => u.UserName == _currentUserName).DeviceGroupId };
        DeviceOwner owner = (requested ?? before ?? caller).Clone();
        if (requested is not null && !IsAdminUserLocked && !requested.Equals(before ?? caller))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Only administrators choose who owns it."));
        }

        if (!owner.AllDevices && owner.GroupId.Length > 0)
        {
            SeedGroupsLocked();
            owner.GroupName = _groups.Find(g => g.Id == owner.GroupId)?.Name
                ?? throw new RpcException(new Status(StatusCode.NotFound, "The device group does not exist (any more)."));
        }

        if (enabled && !owner.AllDevices && owner.GroupId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, Oadm.Sdk.Devices.DeviceGroupMessages.ChooseDevices));
        }

        return owner;
    }

    public Task DeleteComplianceCheckAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var check = _complianceChecks.Find(c => c.Summary.Id == id) ?? throw new RpcException(new Status(StatusCode.NotFound, "The check no longer exists."));
            _complianceChecks.Remove(check);
            RecomputeComplianceLocked();
            return Task.CompletedTask;
        }
    }

    public Task<ComplianceCheckSummary> SetComplianceCheckEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var check = _complianceChecks.Find(c => c.Summary.Id == id) ?? throw new RpcException(new Status(StatusCode.NotFound, "The check no longer exists."));
            check.Summary.Enabled = enabled;
            RecomputeComplianceLocked();
            return Task.FromResult(ComplianceListLocked().Checks.First(c => c.Id == id));
        }
    }

    public Task<IReadOnlyList<ActionChoiceEntry>> ListComplianceChoicesAsync(string typeId, string fieldKey, string? deviceId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActionChoiceEntry>>(typeId == AppType && deviceId is not null
            ?
            [
                new ActionChoiceEntry { Value = "fflprapp", Label = "AXIS License Plate Verifier" },
                new ActionChoiceEntry { Value = "objectanalytics", Label = "AXIS Object Analytics" },
                new ActionChoiceEntry { Value = "imagehealth", Label = "AXIS Image Health Analytics" },
            ]
            : []);

    /// <summary>The asked results show as checking at once; a moment later every result is worked out again.</summary>
    public Task CheckComplianceNowAsync(IEnumerable<string> checkIds, IEnumerable<string> deviceIds, CancellationToken ct)
    {
        var checks = checkIds.ToHashSet(StringComparer.Ordinal);
        var devices = deviceIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            ThrowIfOffline();
            if (!ComplianceEnabled)
            {
                return Task.CompletedTask;
            }

            var reads = _complianceChecks.Where(c => c.Summary.Enabled && c.Summary.TypeId is AppType or ConfigType or SdType
                && (checks.Count == 0 || checks.Contains(c.Summary.Id))).Select(c => c.Summary.Id).ToHashSet(StringComparer.Ordinal);
            var okDevices = _devices.Where(d => d.Status == DeviceStatus.Ok).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var checking = new ComplianceResultBatch();
            foreach (var result in _complianceResults.Values.Where(r => reads.Contains(r.CheckId) && okDevices.Contains(r.DeviceId)
                && (devices.Count == 0 || devices.Contains(r.DeviceId))))
            {
                var entry = result.Clone();
                entry.Checking = true;
                checking.Results.Add(entry);
            }

            _complianceResultEvents.Publish(checking);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(_tick * 6, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                RecomputeComplianceLocked();
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private void SeedComplianceLocked()
    {
        if (_complianceSeeded)
        {
            return;
        }

        _complianceSeeded = true;
        _complianceChecks.Add(FakeCheck("6d0a7c52-6f8e-4c4b-9a51-0c0a3b1f0001", "Reachable", ReachableType, builtIn: true));
        _complianceChecks.Add(FakeCheck("6d0a7c52-6f8e-4c4b-9a51-0c0a3b1f0002", "Login works", LoginType, builtIn: true));
        var app = FakeCheck(Guid.NewGuid().ToString(), "Object Analytics running", AppType, interval: 60,
            settings: new JsonObject { ["application"] = "objectanalytics", ["state"] = "running", ["license"] = false });
        app.Summary.Devices = new ComplianceDevices { Tags = { "Building A", "Building C" } };
        _complianceChecks.Add(app);
        _complianceChecks.Add(FakeCheck(Guid.NewGuid().ToString(), "Matches base-config.cfg", ConfigType, interval: 240,
            settings: new JsonObject { ["file"] = new JsonObject { ["name"] = "base-config.cfg", ["data"] = Convert.ToBase64String("<AcmDeviceParameterExport/>"u8.ToArray()) } }));
        var certificate = FakeCheck(Guid.NewGuid().ToString(), "Certificate 30 days", CertificateType,
            settings: new JsonObject { ["expired"] = true, ["expiresSoon"] = true, ["days"] = 30, ["notTrusted"] = false });
        certificate.Summary.Severity = ComplianceSeverity.ComplianceWarning;
        _complianceChecks.Add(certificate);
        RecomputeComplianceLocked(publish: false);
    }

    private static ComplianceCheckDetail FakeCheck(string id, string name, string type, bool builtIn = false, int interval = 0, JsonObject? settings = null) => new()
    {
        Summary = new ComplianceCheckSummary
        {
            Id = id, Name = name, TypeId = type, BuiltIn = builtIn, Enabled = true, Version = 1, IntervalMinutes = interval,
            Devices = new ComplianceDevices { All = true },
        },
        SettingsJson = (settings ?? []).ToJsonString(),
    };

    /// <summary>Every result again; the changes go to the result watchers, the counts to the list watchers.</summary>
    private void RecomputeComplianceLocked(bool publish = true)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var next = new Dictionary<(string, string), ComplianceResultEntry>();
        foreach (var check in _complianceChecks.Where(c => c.Summary.Enabled && ComplianceEnabled))
        {
            var settings = JsonNode.Parse(check.SettingsJson) as JsonObject ?? [];
            for (var i = 0; i < _devices.Count; i++)
            {
                var device = _devices[i];
                if (!InScope(check.Summary.Devices, device))
                {
                    continue;
                }

                var (outcome, detail) = FakeEvaluate(check.Summary.TypeId, settings, device, i);
                var key = (check.Summary.Id, device.Id);
                var since = _complianceResults.TryGetValue(key, out var old) && old.Outcome == outcome ? old.SinceUnixMs : now;
                next[key] = new ComplianceResultEntry
                {
                    CheckId = check.Summary.Id, DeviceId = device.Id, Outcome = outcome, Detail = detail, CheckedUnixMs = now, SinceUnixMs = since,
                };
            }
        }

        var changes = new ComplianceResultBatch();
        changes.Results.AddRange(next.Values);
        changes.Results.AddRange(_complianceResults.Keys.Where(k => !next.ContainsKey(k))
            .Select(k => new ComplianceResultEntry { CheckId = k.Check, DeviceId = k.Device, Removed = true }));
        _complianceResults = next;
        if (publish)
        {
            _complianceResultEvents.Publish(changes);
            _complianceCheckEvents.Publish(ComplianceListLocked());
        }
    }

    private static bool InScope(ComplianceDevices devices, Device device) =>
        devices.All || devices.DeviceIds.Contains(device.Id) || device.Tags.Any(t => devices.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));

    private static (ComplianceOutcome, string) FakeEvaluate(string type, JsonObject settings, Device device, int index)
    {
        var reads = type is AppType or ConfigType or SdType;
        if (reads && device.Status != DeviceStatus.Ok)
        {
            return (ComplianceOutcome.ComplianceNotChecked, device.Status switch
            {
                DeviceStatus.Unreachable => "Unreachable - the device does not answer",
                DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
                DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
                DeviceStatus.SiteOffline => FakeSiteOfflineText(device),
                _ => "Certificate changed. Remove the device and add it again to trust the new certificate.",
            });
        }

        switch (type)
        {
            case ReachableType:
                return device.Status switch
                {
                    DeviceStatus.Unreachable => (ComplianceOutcome.ComplianceFailed, "Unreachable"),
                    DeviceStatus.SiteOffline => (ComplianceOutcome.ComplianceNotChecked, FakeSiteOfflineText(device)),
                    _ => (ComplianceOutcome.CompliancePassed, string.Empty),
                };
            case LoginType:
                return device.Status switch
                {
                    DeviceStatus.CredentialsRequired => (ComplianceOutcome.ComplianceFailed, "Credentials required"),
                    DeviceStatus.PasswordNotSet => (ComplianceOutcome.ComplianceFailed, "Password not set"),
                    DeviceStatus.CertificateChanged => (ComplianceOutcome.ComplianceFailed, "Certificate changed"),
                    DeviceStatus.Unreachable => (ComplianceOutcome.ComplianceNotChecked, "Unreachable"),
                    DeviceStatus.SiteOffline => (ComplianceOutcome.ComplianceNotChecked, FakeSiteOfflineText(device)),
                    _ => (ComplianceOutcome.CompliancePassed, string.Empty),
                };
            case CertificateType:
                if (device.HttpsEnabled == false)
                {
                    return (ComplianceOutcome.ComplianceNotApplicable, "HTTPS is off");
                }

                if (device.CertNotAfter is null)
                {
                    return (ComplianceOutcome.ComplianceNotChecked, "The certificate was not read yet");
                }

                var days = (int)Math.Floor((device.CertNotAfter.ToDateTime() - DateTime.UtcNow).TotalDays);
                var minimum = settings["days"]?.GetValue<int>() ?? 30;
                return days < 0 ? (ComplianceOutcome.ComplianceFailed, "Expired")
                    : days < minimum ? (ComplianceOutcome.ComplianceFailed, string.Create(CultureInfo.InvariantCulture, $"{days:N0} days left"))
                    : (ComplianceOutcome.CompliancePassed, string.Create(CultureInfo.InvariantCulture, $"{days:N0} days left"));
            case FirmwareType:
                var wanted = settings["minimum"]?.GetValue<string>() ?? string.Empty;
                return Version.TryParse(device.FirmwareVersion, out var have) && Version.TryParse(wanted, out var need) && have < need
                    ? (ComplianceOutcome.ComplianceFailed, $"AXIS OS {device.FirmwareVersion}, needs {wanted}")
                    : (ComplianceOutcome.CompliancePassed, "AXIS OS " + device.FirmwareVersion);
            case AppType:
                return !device.HasVideo ? (ComplianceOutcome.ComplianceFailed, "objectanalytics is not installed")
                    : index % 4 == 1 ? (ComplianceOutcome.ComplianceFailed, "AXIS Object Analytics is stopped")
                    : (ComplianceOutcome.CompliancePassed, "AXIS Object Analytics 1.26.205 running");
            case ConfigType:
                return index % 5 == 2
                    ? (ComplianceOutcome.ComplianceFailed, "2 settings differ\nImageSource.I0.CaptureFrequency: 50Hz (file: 60Hz)\nAction rule Motion to SD card: missing")
                    : (ComplianceOutcome.CompliancePassed, "214 settings match");
            default:
                return index % 6 == 0 ? (ComplianceOutcome.ComplianceFailed, "No SD card") : (ComplianceOutcome.CompliancePassed, "45.2 GB free of 59.5 GB (76 %)");
        }
    }

    private ComplianceCheckList ComplianceListLocked()
    {
        var list = new ComplianceCheckList { Enabled = ComplianceEnabled };
        foreach (var check in _complianceChecks.OrderByDescending(c => c.Summary.BuiltIn).ThenBy(c => c.Summary.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var summary = check.Summary.Clone();
            var results = _complianceResults.Values.Where(r => r.CheckId == summary.Id).ToList();
            summary.Passed = results.Count(r => r.Outcome == ComplianceOutcome.CompliancePassed);
            summary.Failed = results.Count(r => r.Outcome == ComplianceOutcome.ComplianceFailed);
            summary.NotChecked = results.Count(r => r.Outcome == ComplianceOutcome.ComplianceNotChecked);
            summary.NotApplicable = results.Count(r => r.Outcome == ComplianceOutcome.ComplianceNotApplicable);
            summary.CouldNotCheck = results.Count(r => r.Outcome == ComplianceOutcome.ComplianceCouldNotCheck);
            summary.TypeName = summary.TypeId switch
            {
                ReachableType => "Reachable",
                LoginType => "Login works",
                CertificateType => "Certificate",
                FirmwareType => "Firmware",
                AppType => "Application running",
                ConfigType => "Matches configuration file",
                _ => "SD card",
            };
            summary.ReadsDevices = summary.TypeId is AppType or ConfigType or SdType;
            summary.Summary = summary.TypeId switch
            {
                AppType => "objectanalytics running",
                ConfigType => "Matches base-config.cfg (214 settings)",
                CertificateType => "Certificate not expired, at least 30 days left",
                _ => summary.TypeName,
            };
            list.Checks.Add(summary);
        }

        return list;
    }
}
