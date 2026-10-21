# Compliance

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Built-in feature (not a plugin): which devices need attention at first glance. Core `Oadm.Core.Compliance`, server `Oadm.Server.Compliance`, gRPC `ComplianceService`, client
`Compliance/`, SDK `Oadm.Sdk.Compliance`.

- **Off by default** (server setting `Compliance.Enabled`): the page shows an empty state with **Enable Compliance Checks**
  (then a short message about the Devices column), **Turn off compliance** in its toolbar (confirmed); off = nothing is
  checked, no events, results removed, checks kept; the Devices column chooser greys Compliance out while it is off.
- **Check** = a check type + its settings + its devices (always chosen with the shared picker: All devices, tags, chosen
  devices) + Problem or Warning + on / off + (types that read devices) every 15..1440 minutes (default 60). At most 50.
  Default checks **Reachable** and **Login works**: created once per server (setting `Compliance.DefaultsCreated`), then
  ordinary checks (change, turn off, delete; New check adds them again).
- **Types**: host (cached data, never contact a device, follow every device change): Reachable, Login works, Certificate
  (expired, fewer than N days left (default 30), not trusted), Firmware (at least a version). Plugins: **Application
  running** (Applications), **Matches configuration file** (Device configuration: a `.cfg` of Create configuration file or
  ADM kept in the check; parameters, action rules and stream profiles by name; per-device values and passwords never
  compared, values the model lacks skipped), **SD card** (Edge storage: no card fails; optional free GB / %; only while Edge
  storage is on). New types: SDK `IComplianceCheckType`, no host change.
- Rules that must hold: checks are read only; devices whose status is not Ok are not contacted (Not checked with the
  reason); 32 devices at once, 15 s per check, all due checks of a device in one run sharing their reads; results kept per
  check and device (outcome, detail, checked, since) and written in batches; a changed check is checked again on every
  device; a type whose plugin is off says so and does not run. Every check is a stateful event of the event rules
  ("Compliance: <name>", on while it fails on a device). No fix actions on the page.
- **Page** (rail, right below Devices): a count card per check (a click shows the devices that fail it: the tabs grey out,
  "Failing: <check>" and **Show all devices** next to them; Edit and Delete on the card on hover), tabs All | Problems |
  Warnings (All first), the devices with a Result column and one column per check
  (the result text next to the icon, on by default, toggle; several detail lines on one line: "1 setting differs: X: 60Hz
  (file: 50Hz)"; the tooltip over the whole cell), Group by tag, Choose columns (per client), New check, Check now
  (a spinning "Checking..." per device and check until the result is in); right: Turn off compliance, Export. Dialog `ComplianceCheckWindow` (single
  operation: Name, Devices, Kind of check, its settings through the shared `SettingsFormView` of the event rules).
  **Devices grid**: the Compliance column (off by default) or, while it is on, one column per check (toggle, per client).
- Roles: "See compliance" (read, Check now), "Change compliance checks" (create, change, delete; audited).
- Scale (30,000 devices x 50 checks): scope and failing devices as sets per check, events as single changes, one
  `ComplianceInvocation` per check version (a configuration file is serialized and parsed once), results written in key
  order 5,000 per transaction, the client keeps ids and repeated texts once. Measured: server enable 5.4 s, results written in 19.5 s,
  1,000 device changes 0.6 s (`ComplianceScaleTests`); client 1.5 million results applied in 0.8 s, filter, tab, card and
  search 27 ms (`ComplianceTests`, Perf); read checks of 30,000 devices at 32 parallel take about 16 minutes a round.
- Developer guide: website Developers > Compliance checks (`dev/compliance-checks.md`); `plugins/README.md`.
- Tests: `tests/Oadm.Core.Tests/Compliance` (+ Perf `ComplianceScaleTests` 30,000 devices x 50 checks),
  `tests/Oadm.Server.Tests/ComplianceServiceTests`, the check types in their plugins' tests,
  `tests/Oadm.Client.Tests/ComplianceTests` (+ Perf 30,000 devices x 50 checks, screenshots). Fake mode:
  `FakeOadmApi.Compliance.cs`.
