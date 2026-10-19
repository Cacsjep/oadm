# Compliance (draft spec)

Status: agreed with the maintainer on 2026-10-19 and built (server, check types, client). CLAUDE.md has the short
section "Compliance"; this file is the detailed spec (like `docs/plugin-sdk.md`).

Goal: see at first glance which devices need attention, without sorting the device grid. Out of the box OADM shows which
devices it cannot reach or log in to; users add their own checks ("Object Analytics running on the tag Parking",
"matches base-config.cfg", "certificate valid for at least 30 days"). Checks come from the host and from plugins through
one small SDK contract; nothing moves out of the plugins.

## Decisions

| Topic | Decision |
|---|---|
| Name and place | Built-in host page **Compliance** (not a plugin), rail entry directly below Devices, always there. Key `HostPages.Compliance`, icon `squareActivity` (Lucide square-activity, new geometry). |
| Check | A check = a check type + its settings + the devices + "When it fails: Problem / Warning" + on / off + (read checks) an interval. |
| Devices | **Every check has its devices**, always chosen by the user with the shared picker (All devices, tags, chosen devices; at least one device or tag). The user decides which devices a check is for (e.g. only cameras with an SD card); the check type does not guess. |
| Default checks | **Reachable** and **Login works**, built in, always on, not deletable, Problem, cached data only. They start with All devices; the user may change their devices. |
| Check types in v1 | Certificate, Firmware (host, cached data); Application running (Applications plugin); Matches configuration file (Device configuration plugin); SD card (Edge storage). No hardening checks for now. |
| Where the code lives | The SDK has only the contract (`IComplianceCheckType`). The host registers its own types internally against the same interface (like `BuiltInEvents`); every plugin registers its types where its readers already are: a public class with a parameterless constructor in its `*.Server.dll` (found by the loader like an action) or returned by a core plugin's `IComplianceSource`. Third parties use the same contract. A type of a plugin that is off shows "Not available: <plugin> is turned off" (the SD card check needs Edge storage on). |
| Schedule | Cached checks follow every device change at once (no device traffic). Read checks run per check every N minutes (default 60, 15..1440) and on **Check now**. |
| Severity | Set per check; each type has a default. |
| Alerts | Every check is an event of the event rules: **"Compliance: <check name>"**, stateful per device, on while the check fails; values `result`, `detail`. Alerts through the existing actions (email, HTTP, TCP, Set output). |
| Devices grid | One **Compliance** column (worst result) or, with a toolbar toggle next to Group by tag, one column per check. |
| Roles | New host permissions **See compliance** (`compliance.view`; Operator and Viewer yes) and **Manage compliance checks** (`compliance.manage`; Operator and Viewer no, like event rules). Check now needs See. Changes audited. |
| Config file | The user uploads a configuration file (Create configuration file, or ADM `.cfg` format 3) into the check; the server keeps it as the check's baseline. Everything in the file is compared: parameters (incl. connection settings such as HTTPS), action rules and stream profiles by name. |

## Results

Per device and check one result:

| Result | Look | Counts as |
|---|---|---|
| Passed | green check | ok |
| Failed | red (Problem) or amber (Warning), by the check's severity | problem / warning |
| Not applicable | neutral, reason ("HTTPS is off"); only where the check cannot mean anything for the device | ok |
| Not checked | neutral, reason: the device is not contacted (Unreachable, Credentials required, Password not set, Certificate changed: the default checks report those) or was not checked yet | neither |
| Could not check | neutral with the reason ("Timeout after 15 s") | neither; the card detail says "3 not checked" |

Every result has a short detail ("Object Analytics is stopped", "12 settings differ") and the time it was checked and
since when it holds ("Failed since 14:05"). A device outside the check's devices has no result.

## SDK (`Oadm.Sdk.Compliance`)

```csharp
public interface IComplianceSource                     // on a core plugin, like IActionSource
{
    IReadOnlyList<IComplianceCheckType> ComplianceCheckTypes { get; }
}

public interface IComplianceCheckType
{
    string Id { get; }                                 // "oadm.acap.running"
    string Name { get; }                               // "Application running"
    string Description { get; }
    string IconKey { get; }
    IReadOnlyList<ActionField> Fields { get; }         // the settings form, drawn by the host
    ComplianceSeverity DefaultSeverity { get; }        // Problem or Warning
    bool UsesCachedDataOnly { get; }                   // true: never contacts the device, evaluated on device changes
    IReadOnlyList<string> Validate(ComplianceSettings settings);
    string Describe(ComplianceSettings settings);      // "Object Analytics running, at least 1.4"
    Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IComplianceQueryContext ctx, string fieldKey, CancellationToken ct);
    Task<ComplianceResult> EvaluateAsync(IComplianceContext ctx, ComplianceSettings settings, CancellationToken ct);
}

public interface IComplianceContext
{
    IDeviceInfo Device { get; }
    IVapixClient? Vapix { get; }                       // null for cached-only types
    IComplianceReads Reads { get; }                    // one read per device and key in a run, shared by all checks
    DateTimeOffset Now { get; }
    ILogger Logger { get; }
}

public interface IComplianceReads
{
    Task<T> GetOrAddAsync<T>(string key, Func<CancellationToken, Task<T>> read, CancellationToken ct);
}
```

`ComplianceResult.Passed(detail)`, `Failed(detail)`, `NotApplicable(reason)`, `CouldNotCheck(reason)`; the host applies
the severity. A type never writes to a device (read-only by contract, like `ITaskPluginQuery`).

Two field additions on `ActionField` (usable by actions later, the rule dialog refuses them for now):
- `ChoicesFromDevice`: a `DynamicChoice` whose choices are read from one device. The form shows "Read from device"
  (`DevicePickerWindow.ShowOneAsync`), then the select with what `ListChoicesAsync` returned for it (`IComplianceQueryContext.Device`).
  Example: the applications installed on the chosen camera.
- `ActionFieldType.File` (`Accept` ".cfg", `MaxLength` = most bytes): the form shows `ui:FileRow`; the client puts the
  file into the settings as {"name", "data" (base64)} (`ActionFile.From` reads it), so the baseline is part of the check
  (at most 1 MB for a configuration file; no upload with a retention).

No client part in v1: every v1 type works with the generated form. An own dialog (`IComplianceCheckDialog` in
Oadm.Sdk.Client, like `ITaskPluginDialog`) is a later addition when a type needs more.

## Check types in v1

| Type | Source | Settings (default) | Fails when |
|---|---|---|---|
| **Reachable** | host, cached | none | Status Unreachable |
| **Login works** | host, cached | none | Credentials required, Password not set, Certificate changed |
| **Certificate** | host, cached | Expired (on), Expires within N days (on, 30, Warning-worthy), Not trusted: Self-signed / Untrusted (off) | any chosen condition holds; Not applicable when HTTPS is off |
| **Firmware** | host, cached | At least version (required) | the device's AXIS OS is older; Not applicable without a version. One minimum per check: devices on another AXIS OS track (LTS) get a second check with their own devices |
| **Application running** | Applications plugin | Application (`ChoicesFromDevice`), Must be: Running (default) / Installed, At least version (optional), License valid (off) | not installed, stopped, older, license missing or expired. One `applications/list.cgi` per device and run (`Reads`) |
| **Matches configuration file** | Device configuration plugin | Configuration file (`File`, `.cfg`, 4 MB) | a parameter, action rule or stream profile differs; detail "12 settings differ", the list (first 50: name, expected, found) in the tooltip / detail |
| **SD card** | Edge storage | At least free GB (optional), at least free % (optional) | **No SD card** (also on a device without a slot: the user chose the devices), card not OK (`EdgeDisk.Problem`), less free space |

**Matches configuration file**, details:
- The file is read with `AdmConfigFile.Read` when the check is saved (a file that does not parse is a field error).
- Parameters: one `param.cgi action=list` for the groups in the file per device and run; values compared like the verify
  step of Configure devices (trimmed; choices case as the device reports). A parameter the device does not have is
  skipped (counted in the detail: "3 settings not on this model"); a file of another model is fine.
- Per-device values (addresses, host name, device names) are never in a file (Create configuration file leaves them out).
- Action rules and stream profiles: a rule / profile of the file's name must exist with the file's settings
  (`DeviceSettingsReader`, `StreamProfileApi`); extra rules or profiles on the device are fine (a merge keeps them, like
  `ConfigCompare`).
- Passwords: a configuration file normally holds none; a password value in the file is not compared (the device never
  sends it back).

## Server

- Tables (new migration): `ComplianceChecks` (Id, Name, TypeId, SettingsJson, DevicesJson (`DeviceSelector`), Severity,
  IntervalMinutes, Enabled, Version, CreatedUtc, ChangedUtc; at most 50) and `ComplianceResults` (CheckId, DeviceId,
  Result, Detail (max 1000), CheckedUtc, SinceUtc; key CheckId + DeviceId; rows of removed devices and deleted checks
  removed with them).
- `Oadm.Core.Compliance`: `ComplianceCatalog` (host types + every enabled plugin's `IComplianceSource`, rebuilt when plugins
  turn on or off; a check whose type is gone shows "Not available: <plugin> is turned off" and keeps its last results
  dimmed), `ComplianceStore`, `ComplianceRunner`.
- Cached types: evaluated for the affected devices on every `DeviceChangeFeed` batch, O(1) per device and check.
- Read types: `ComplianceRunner` collects per device the checks that are due and runs them together (one VAPIX client,
  one `IComplianceReads` cache), 32 devices at once, 15 s per device, spread over the interval like the polling round.
  Devices with a refused status are not contacted (Not checked). Results are written in batches; only changes are
  published.
- Events: `ComplianceEvents` (an `IDynamicEventSource` of the host, group "Compliance"): one stateful type per check,
  `ReplaceStates` after start, `SetState` per changed result; a deleted check removes its type.
- gRPC `ComplianceService` (`compliance.proto`): `GetCatalog`, `ListChecks`, `WatchChecks` (with the counts per check),
  `SaveCheck`, `DeleteCheck`, `SetCheckEnabled`, `ListChoices` (type, field, device), `CheckNow(check ids, device ids)`,
  `WatchResults` (snapshot then changes, `KeyedCoalescingChannel`, client through `ChangeBatcher`).
- Audit: "Created / Changed / Deleted / Turned on / Turned off compliance check".

## Client

- **Compliance page** (`Compliance/ComplianceViewModel` + `ComplianceView`, rail key `HostPages.Compliance`, icon
  `squareActivity`, right below Devices while the role may see compliance; the client mirror `Compliance/ComplianceStore`
  is fed by `ServerConnection` and gives every device row one `DeviceCompliance`):
  - Toolbar: **New check** (primary), Edit, Delete (red, confirmed), **Check now** (the selected devices, else every device
    of the shown checks), Export; search right.
  - Count cards (`ui:StatCard` in `Button.statCardButton`), one per check that is on, in a wrapping row: "Object Analytics
    running" / "12" / "failed · 3 not checked"; a click shows the devices that fail it (tab All; again or a tab: off) and
    selects the check for Edit and Delete.
  - Segmented switch **Problems** | **Warnings** | **All** with counts; Problems is the start view.
  - Grid (virtualized, 30,000 devices): device icon, Address, Model, Tags, Result ("2 checks failed"), then one icon column
    per check (`ui:StatusChip` icon only, tooltip: result, detail, checked, since). Sortable.
- **Check dialog** (`Compliance/ComplianceCheckWindow`, single operation): Name, Type (grouped select: Devices,
  Applications, Configuration, Storage; the type's description in an info icon), Devices (required: purple "Selected devices"
  button, `DevicePickerOptions.Full` without "The device of the event"), the type's settings form, When it fails
  (Problem / Warning), Check every (min) for read types, Turned on. Errors below the fields; Save runs the check at once.
- **Devices grid**: column **Compliance** after Status (`ComplianceSummaryCell`: "2 problems, 1 warning" as a chip, "OK" as
  plain text, the failed checks as tooltip; sorted problems first; in the export). Toolbar icon toggle **Show each
  compliance check** (icon `squareActivity`) next to Group by tag (class `active` while on): the Compliance column becomes
  one column per check (`ComplianceResultCell`, built by `ComplianceView.CheckColumn`). Per client
  (`ClientSettings.ExpandCompliance`). Without "See compliance" neither the column nor the toggle shows.
- Fake mode: the two default checks plus "Object Analytics running" and "Matches base-config.cfg" with sample results.

## Scale

30,000 devices x 50 checks = 1.5 million results: the store indexes by check and device, the client mirror keeps one
compact array per device, counters per check are kept per change, the page filters in O(n). Read checks: 30,000 devices
every 60 minutes at 32 parallel and about 1 s per device = about 16 minutes per round. Perf tests: 30,000 devices x 10
checks (page filter, tab switch, column toggle, a round of changes).

## Tests

`tests/Oadm.Core.Tests/Compliance` (runner with a fake clock, shared reads, refused statuses, cached evaluation),
`tests/Oadm.Server.Tests/ComplianceServiceTests` (roles, audit, events), each type in its plugin's tests (recorded
fixtures: application list, param.cgi, disks, rules), `tests/Oadm.Client.Tests/ComplianceTests` (+ Perf, screenshots).

## Not in scope

No fix actions on the page: it shows what fails, the user fixes it with the existing tools (Log in, Set password,
Configure devices, Applications, Edge storage).
