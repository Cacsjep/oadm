# Hardening scan (core plugin)

Plugin id `oadm.hardening-scan`, rail page **Hardening scan** (icon `clipboardCheck`; `shield` already draws Lucide
shield-check for the PKI). Server part `Oadm.Plugins.HardeningScan.Server.dll`, page `Oadm.Plugins.HardeningScan.Client.dll`.

Scans every managed device against the **AXIS OS Hardening Guide**
(https://help.axis.com/en-us/axis-os-hardening-guide) in its two levels, **Basic hardening** and **Extended hardening**.
The grid has one column per check and a pass / warn / fail icon per device. Read-only: `param.cgi action=list`, VAPIX
`get*` / `list` methods, `config/rest` GETs and the SOAP `Get*` read. Never a write.

## Decisions (user, 2026-10-08)

1. **Latest AXIS OS (B2)** is information only: the column shows the version, no rating (OADM has no source for the current
   AXIS OS versions yet).
2. The extras **X1-X6** (HTTPS only, IEEE 802.1X, brute-force protection, access log, signed video, NTS) are columns of
   **Extended only**; Basic is exactly the guide's Basic list.
3. **SSH on = fail**; web interface, discovery protocols and DHCP = warn.
4. **Bonjour on = warn** like the guide; the tooltip says that OADM's Discovery and the re-find of moved devices use it.
5. **No audit entry** for a scan (read-only, like the Snapshot report).
6. **SDK change**: `IDeviceInfo` has `DhcpEnabled`, `HttpsEnabled`, `Dot1xEnabled` (DIM null), filled from the server's
   device table and the client's device rows. The scan reads the parameters only when they are null.
7. **The last results are kept** per device and level on the server (plugin setting `results`) and shown with their scan
   time after a restart.
8. **Scheduled scans later** (with the general scheduling goal); v1 scans on demand.

## Evidence (10.0.0.48, AXIS P3265-V, AXIS OS 12.11.77, read-only, 2026-10-08)

All reads answered 200 over HTTP Digest. Recorded answers (secrets blanked: SNMP communities, OAuth verify field; LLDP
neighbors trimmed) in `tests/Oadm.Plugins.HardeningScan.Tests/Fixtures/` (`config/discover` is linked from the PKI tests):

| Fixture | Read | What the camera reported |
|---|---|---|
| `paramcgi-hardening.txt` | `param.cgi?action=list&group=...` | SSH.Enabled=yes, Bonjour=yes, ZeroConf=yes, UPnP=no, WS-Discovery off, WebInterfaceDisabled=no, BootProto=dhcp, HTTPS on with exactly the guide's ciphers, RTSPS off, dot1x off, AccessLog=Off, password throttling On, Audio.A0..A7 off (Properties.Audio.Audio=yes), Storage.S0/S1 on, RemoteSyslog1 off, SignedVideo off, RemoteService=oneclick |
| `pwdgrp-get.txt` | `pwdgrp.cgi action=get` | 4 administrators, 1 operator |
| `rest-user-management-v2-get.json` | GET `/config/rest/user-management/v2` | `passphraseComplexity.policy` = `none` |
| `ntp-getNTPInfo.json` | `ntp.cgi getNTPInfo` (1.5) | NTP on, 2 static servers, NTS off, not synced |
| `disks-list.xml` | `disks/list.cgi?diskid=all` | SD card OK and not encrypted; network share disconnected |
| `applications-list.xml`, `applications-config-AllowUnsigned.xml` | `applications/list.cgi`, `config.cgi?action=get&name=AllowUnsigned` | 8 apps, 2 running, 6 not signed; AllowUnsigned=true |
| `rest-firewall-v1-get.json` | GET `/config/rest/firewall/v1` | activated, default ACCEPT, one LIMIT rule |
| `rest-snmp-v1-get.json`, `rest-oidcsetup-v1-get.json` | GETs | SNMP off; OpenID not configured |
| `rest-lldp-v1-get.json`, `rest-mdnssd-v2-get.json` | GETs | LLDP on; Bonjour on |
| `soap-GetWebServerTlsConfiguration.xml` | SOAP `aweb:GetWebServerTlsConfiguration` | policy `HttpAndHttps`, the same 6 ciphers |
| `apidiscovery-getApiList.json` | `apidiscovery.cgi getApiList` | ntp 1.5, signed-video 1.0, application 1.0, disk-management 1.0, remote-syslog 1.2, ... |

Findings that shaped the design:
- One `param.cgi action=list` with every group answers half of the checks. A group the firmware lacks does not fail the
  request: the answer has `# Error: Error -1 getting param in group 'Network.Filter'` and the other groups, but the group
  right after the error line lacks the `root.` prefix (`System.WebInterfaceDisabled=no`). `ParamList` accepts both forms
  and reads an error line as "not available".
- `System.HTTPServerHeaderComments` does not exist on 12.11 (B11 is information only).
- `config/discover` lists the REST APIs per firmware; the REST reads use it to decide "not available" instead of probing.

## Checks

Every grid cell is an icon-only `ui:StatusChip`: pass green, warning amber, fail red, read error red, does not apply / info
neutral, empty = not scanned at that level. Its tooltip (built when it opens) gives the state and value found, the rule,
the guide's recommendation with its section, and a note (Bonjour). "Does not apply" = the model or firmware lacks the
feature or API. Info rows are not shown on the page (user decision 2026-10-08).

### Basic

| # | Header | Source | Rule |
|---|---|---|---|
| B1 | - | - | Info: factory default before commissioning cannot be told |
| B2 | AXIS OS | `IDeviceInfo.FirmwareVersion` | info only (the version), not rated |
| B3 | Accounts | `pwdgrp.cgi action=get` (Users plugin's `PwdgrpApi`) | pass: an account without admin rights; warn: administrators only |
| B4 | Passwords | GET user-management v2 `passphraseComplexity.policy` | pass `length` / `complex`; warn `none`; n/a without the API |
| B5 | Static IP | `IDeviceInfo.DhcpEnabled` (else `Network.BootProto`) | pass static; warn DHCP |
| B6 | Time | `getNTPInfo` (Date and time plugin's `TimeClient`; param `Time.*` without the NTP API) | pass: on, >= 2 servers, synced; warn: 1 server or not synced; fail: off |
| B7 | Encryption | `disks/list.cgi` + `Properties.LocalStorage.DiskEncryption` | pass: every connected disk encrypted; fail: one is not; n/a: none connected or no support |
| B8 | Apps | `applications/list.cgi` + `config.cgi AllowUnsigned` (ACAP plugin's `ApplicationApiClient`) | warn: unsigned allowed, an app not signed, or installed but stopped; pass otherwise |
| B9 | Web UI | `System.WebInterfaceDisabled` | pass yes; warn no; n/a before 9.50 |
| B10 | Discovery | Bonjour, UPnP, ZeroConf, WS-Discovery parameters; LLDP from REST (detail only) | pass: all off; warn: one on (named) |
| B11 | - | - | Info |
| B12 | Audio | `Audio.A<n>.Enabled`, `Properties.Audio.Audio`, category | pass: no audio / off; warn: on; n/a for speakers, intercoms, audio devices |
| B13 | SD slot | `Storage.S<n>.Enabled` + the disks list | pass: slot off or card in use; warn: slot on without a card; n/a without a slot |
| B14 | SSH | `Network.SSH.Enabled` | pass off; **fail on** |
| B15 | UART | firmware version | pass >= 10.11; warn older |
| B16-B19 | - | - | Info (network ports, USB, Wi-Fi, Bluetooth) |
| B20 | Firewall | GET firewall v1 (else `Network.Filter.*`) | pass: on, default DROP; warn: on with ACCEPT, or IP filter on; fail: off |
| B21 | Ciphers | `HTTPS.Ciphers` (else the SOAP list), `HttpsEnabled` | pass: only the guide's TLS 1.2 ciphers; warn: others (named); fail: HTTPS off |

### Extended (= Basic + these)

| # | Header | Source | Rule |
|---|---|---|---|
| E1, E2, E8 | - | - | Info (network exposure, vulnerability scanning, anti-tampering) |
| E3 | Certificate | `CertTrustName`, `CertNotAfterUtc` | pass: trusted, > 30 days; warn: self-signed or <= 30 days; fail: untrusted, expired, HTTP only |
| E4 | Syslog | `API.RemoteSyslog1.*` | pass: on over TLS; warn: on unencrypted; fail: off (or on without a server) |
| E5 | SNMP | GET snmp v1 (else `SNMP.*`) | pass: off or v3 only; fail: v1 / v2c on |
| E6 | RTSPS | `Network.RTSPS.Enabled` | pass on; warn off; n/a without video |
| E7 | OAuth | GET oidcsetup v1 `OIDC_ProviderMetadataURL` | pass configured; warn not; n/a below 11.6 |
| X1 | HTTPS only | SOAP `GetWebServerTlsConfiguration` (PKI plugin's `WebServerTls`) | pass `Https`; warn `HttpAndHttps`; fail `Http` |
| X2 | 802.1X | `IDeviceInfo.Dot1xEnabled` (else the parameter) | pass on; warn off |
| X3 | Brute force | `System.PreventDoSAttack.ActivatePasswordThrottling` | pass On; fail Off |
| X4 | Access log | `System.AccessLog` | pass On; warn Off |
| X5 | Signed video | `Image.I<n>.MPEG.SignedVideo.Enabled` (signed-video API) | pass: on for every source; warn: off; n/a without the API or video |
| X6 | NTS | `getNTPInfo` `NTSEnabled` | pass on; warn off; n/a below ntp 1.5 |

Score = passed / rated checks (Pass, Warn, Fail, Error count; n/a and info do not). A device **passes** when no checked column
of the level is Warn or Fail.

## Server

- `HardeningScanPlugin : ICorePlugin`, no task plugins. `RequiredRole` = Operator for every method, `IsAudited` false.
- `Checks/HardeningCatalog.cs` (id, level, section, title, header, rule, recommendation, info, rated, note) and
  `Checks/HardeningChecks.cs` (`Evaluate(DeviceFacts)`: pure functions). `CacheOnly` for devices that are not contacted (B2,
  B15, E3 from the cache, the rest Error with the reason).
- `Device/DeviceFactsReader.cs`: param.cgi (one call, all groups of both levels), pwdgrp get, `config/discover` once, then the
  REST GETs it lists (user-management v2, firewall v1, lldp v1; Extended: snmp v1, oidcsetup v1), `getNTPInfo` (ntp API) or
  the Time parameters, `disks/list.cgi` (disk-management API or an SD slot), applications list + AllowUnsigned (application
  API), Extended: the SOAP web server read. 15 s per request, no retries, 2 min per device. A failed read sets only its
  checks to Error ("Timeout after 15 s", "Unauthorized - HTTP 401": the snapshot report's texts). When param.cgi gets no
  answer or is refused, the other reads are skipped with the same text (row status). Readers compiled in from other plugins
  (`<Compile Include ... Link>`, never a project reference): Users `PwdgrpApi`, Date and time `Model/` + `Vapix/`, ACAP
  `ApplicationApiClient` + `Shared/`, PKI `WebServerTls` + `DeviceHttp`, Snapshot report `SnapshotRequests` (error texts and
  refused statuses). Every device XML through `DeviceXml`.
- Not contacted: CertificateChanged, CredentialsRequired, PasswordNotSet (the snapshot report's texts) and Unreachable
  ("Unreachable - the device did not answer the last status check").
- `Scanning/HardeningScanService.cs`: one scan at a time (`startScan` while one runs returns it), at most 16 devices at once
  (plugin setting `config` `{"parallelism": 1..64}`), progress and changed rows as events every 500 ms (`results` split at
  1,000 rows and below the 1 M character event limit), `cancelScan` keeps what was scanned.
- `Scanning/ResultStore.cs`: the last result per device and level, written to the plugin setting `results` at most every
  2 s and at the end of a scan; at most 10,000 devices per level (oldest dropped), values cut at 160 and details at 600
  characters; results of removed devices are dropped on `getState`.
- Methods (`HardeningMethods`, camelCase JSON, `Shared/HardeningModels.cs`): `getState` -> catalog, column ids, compact results
  (one state character per column `p w f n e i -` plus the short values) and the job; `startScan {level, deviceIds}` -> job;
  `cancelScan {jobId}` -> job; `getDetail {deviceIds}` -> the stored results with details. Events `progress`, `results`.

## Page

- `ui:PageHeader.Subtitle` "Checks every device against the AXIS OS hardening guide. Read-only." One card.
- Toolbar: segmented **Basic** / **Extended** (changes only the columns and the summary; each column shows its newest result,
  so an Extended scan covers Basic), **Scan all** (primary), **Scan selected (N)** (the Devices page
  selection), **Stop**, **Export CSV**, status filter (All devices, Failed, Warnings, Not scanned) and `ui:SearchBox`.
  `ui:ProgressRow` while scanning ("Scanning 812 of 5,000 devices (Basic)").
- Summary: "5,000 devices · 4,812 scanned · 1,120 pass · 3,104 with warnings · 588 failed · 188 not reachable". Column header
  tooltip: title, section and "Pass 4,000 · Warn 700 · Fail 112".
- Grid: virtualized DataGrid on `RangeObservableCollection`; frozen device icon (colored by the row result, tooltip = why it
  could not be scanned), Address (IPv4 sort), Model, AXIS OS, Score, Last scan; then one column per check (built by the view
  from the catalog), sortable by state (fail first). Column order and widths, the level and the detail height are remembered
  in `LocalApplicationData/Oadm/plugins/oadm.hardening-scan/client.json`.
- Below a splitter: **Selected device** (result, check, value found + detail from `getDetail`, recommendation) or **Not checked
  automatically** (the info rows of the level with the guide's text and a link to the guide).
- Scale: rows indexed by device id, results applied per changed row, summary and per-column counters updated per changed row,
  cells are one byte per column (`HardeningRow.States`), tooltips built when they open, filter and search O(n) with one reset.

## Export

CSV (UTF-8 with BOM, RFC 4180 quoting, CRLF) of the shown rows: Address, MAC address, Model, AXIS OS, Last scan (UTC), Score,
Status, then per check of the level two columns: the result (`pass`, `warn`, `fail`, `n/a`, `error`, `info`, empty = not
scanned) and the value found. Built on the client from the loaded results (the short values; details are not exported). A PDF
report like the snapshot report is a later option.

## Fake mode

`src/Oadm.Client/Api/FakeOadmApi.HardeningScan.cs`: deterministic results per fake device (most pass, some warn on SSH,
discovery and DHCP, some fail on ciphers / SNMP v2c, error rows for devices with a bad status), a Basic scan of the day before
at start, and a scan simulated over 3 s with progress and result events.

## Tests

`tests/Oadm.Plugins.HardeningScan.Tests`: `CheckTests` (every rule from the recorded answers, old firmware without REST APIs
with `# Error` lines and unprefixed parameters, hardened and weak variants, failures isolated per read, unreachable and refused
devices, catalog), `ParserTests`, `ScanServiceTests` (bounded parallelism, device timeout, refused statuses, stop, persistence
across a restart, removed devices, event batches, parallelism setting, store bound; the fake camera records a violation for
any request that is not a read), `PageViewModelTests` (5,000 devices: state, filter, search, level switch, a 1,000-row batch;
levels merged by scan time; CSV; tooltips), `MethodRoleTests`, `FakeModeTests`, `HeadlessPageTests` (`hardening-scan-basic.png`,
`hardening-scan-extended.png`), and `HardeningHardwareTests` (`Category=Hardware`, one Extended scan of 10.0.0.48 through the
in-process server, read-only).

## Outlook (not v1): Fix

Later: contributed task plugins (group Security) under the device-safety HARD RULE: "Disable SSH", "Disable discovery
protocols" (warns: OADM's re-find uses Bonjour), "Disable web interface", "Set recommended HTTPS ciphers", "Set password
policy", "Enable remote syslog over TLS", "Disable SNMP v1/v2c", "Enable RTSPS". Existing tasks already fix some: PKI
"Enable HTTPS" (E3, X1) and "Enable IEEE 802.1X" (X2), Date and time / "Use OADM as NTP server" (B6), Network settings static
IP (B5), Upgrade firmware (B2), Applications remove / stop (B8). A "Fix" action on a column would open the matching task
for the failing devices.
