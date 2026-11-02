# Vulnerabilities (CVE)

Which managed devices may be affected by a published Axis CVE. Built into OADM (not a plugin: plugins cannot add device
grid columns), read only, nothing is ever written to a device.

## Data

- Source: the CVEs Axis publishes as CNA (`product-security@axis.com`), only device firmware: product "AXIS OS" (every
  AXIS OS device) or a product naming a model ("AXIS Q3527-LVE", "AXIS A1610 (-B) Network Door Controller"). Camera Station,
  Device Manager, License Plate Verifier, Optimizer, 2N and other software are left out.
- The ids come from the NVD API (`services.nvd.nist.gov/rest/json/cves/2.0?sourceIdentifier=product-security@axis.com`, one
  request, about 100 records); each record from the CVE Program as Axis wrote it (`cveawg.mitre.org/api/cve/<id>`,
  `containers.cna.affected`).
- Ranges, in this order:
  1. Axis' exact ranges (`version` + `lessThan` / `lessThanOrEqual`, one per track) always win: NVD's own ranges lag behind
     and are wrong where checked (11.x marked affected that Axis lists as unaffected, a start version dropped, an LTS-only
     range for a fix that covers 11.8 too).
  2. Older records give free text ("AXIS OS 10.5 – 11.5", "AXIS OS 5.51 or later", "AXIS OS 11.8, 10.12, 6.50", "All
     firmware versions prior to 5.65") without the fixed build per track. For AXIS OS they take NVD's ranges (CPE
     configurations, derived from Axis' advisory): `axis_os_2016/2018/2020/2022/2024` are the LTS tracks 6.50, 8.40, 9.80,
     10.12, 11.11 up to their fixed build, `axis_os` every other version up to its end (the LTS tracks cut out), a
     `<model>_firmware` product only that model.
  3. Without NVD's ranges the free text is read as it stands; an open end ("or later") is left out (it would hit every
     current device). A track end ("10.5 – 11.5") means the whole track (below 11.6).
- OADM's format (`VulnerabilityList`, JSON): the list's date, where it came from (`release`, `server`, `client`) and per CVE:
  id, published, CVSS score and severity (the CNA's, else the ADP's, else NVD's), the English description, the Axis advisory
  link (a reference on axis.com) and the affected firmware (`model` or every AXIS OS device, ranges with `from`, `lessThan`
  or `lessThanOrEqual`, or a whole `track`). A CVE without a matchable range is kept out.
- **Base list:** `src/Oadm.Server/Vulnerabilities/axis-vulnerabilities.json` (about 60 CVEs, 60 KB), embedded, made by
  `tools/Oadm.Vulnerabilities` (online, or `--nvd <answer> --records <folder>` from saved answers; prints what it left out
  or took from NVD; `--if-changed` keeps the file when only the date would change, `--summary <file>` writes the added,
  changed and removed CVEs). The weekly workflow `.github/workflows/vulnerabilities.yml` runs it and opens a pull request
  when a CVE changed; merged before a release, never fetched during the release build (a tag always ships the list on
  main). Every installation knows the CVEs up to its release.

## Update online (opt-in)

Server setting `Vulnerabilities.Update`, Settings card **Vulnerabilities** ("Update the list online"), administrators:

- **Off** (default): no outgoing call for this, ever; the list is the base list of the OADM release.
- **By the server**: the server fetches the list when it is older than a day (checked hourly) and on **Check now**.
- **By the clients**: a client fetches it after connecting (and hourly) when the server's list is older than a day, and on
  **Check now**, and hands it to the server (`VulnerabilityService.Upload`, any signed-in user, audited "Updated
  vulnerability list"). For a server in a closed network while the laptops have internet.

No device data leaves the network: the calls only read the public CVE list. The server keeps the newest list
(`vulnerabilities.json` in the data folder; a newer release's base list wins over an older stored one, a broken file falls
back to the base list), refuses an upload older than the one it has and one that does not parse or is larger than the
limits (5,000 CVEs, 2 MB, valid ids and versions). The card shows the count, the list's date and where it came from.

## Devices page

- Column **Vulnerabilities** (column chooser, on by default, after Firmware): "2 · High" (count and the highest severity),
  empty when none is known. `ui:StatusChip` red for High and Critical, amber for Medium, plain text for Low or unrated.
  Sorting by it puts the highest severity and the most CVEs at one end.
- The cell's tooltip (`VulnerabilityTip`, built when it opens, none for a device without CVEs) lists the CVEs worst first in
  three aligned columns: the id, the level (only the level colored: Critical and High red, Medium amber, Low plain) and
  "fixed in 11.11.93" (the end of the range of the device's track); at most 12 rows, then "and 17 more". Its own ToolTip,
  wider than the theme's text tooltips (640 px), with the theme's look.
- Context menu **Vulnerabilities** (shield-alert icon, after Tags): only when a selected device has known CVEs, never an
  empty window. The window (`DeviceVulnerabilitiesWindow`, one card) names the device (model, address, firmware) or says
  "3 of 5 selected devices", gives the count, and lists every CVE once, worst first: CVE, Level (the column's chips), Score,
  Fixed in (the fixed version of each affected device's track, lowest first, "Not listed" without one), Devices (several
  devices only: how many it affects, the first 12 in its tooltip, then "and 5 more"), the full description (wraps, no line
  limit: `TextBlock.cellText.fullText`) and an "Advisory" link (https only, opens the browser). A snapshot taken when it
  opens; the selection is grouped by its shared per-model-and-firmware result first (one pass at 100,000 devices).
- Search: a CVE id ("CVE-2024-6509", any case) shows the devices it may affect.
- Matching: model and firmware version against the ranges, numbers compared part by part (6.50.5.19, 11.11.93,
  12.10.36; text after the numbers ignored). Ranges for every AXIS OS device apply from 5.0 on: devices with their own
  firmware line (door controllers on 1.x such as the A1001) only match ranges naming their model. Done in the client per
  model and firmware and cached (100,000 devices share a few dozen versions): a new list for 100,000 devices about 50 ms,
  a search by CVE about 70 ms. "May be affected": the ranges do not say whether the affected feature is in use.

## Where

- `src/Oadm.Contracts/Vulnerabilities`: the format and its limits, the version compare, the matcher and the feed (NVD +
  cve.org, `HttpClient` given by the caller); shared by server, client and the tool.
- `src/Oadm.Server/Vulnerabilities`: `VulnerabilityStore` (base list, stored list, newest wins), `VulnerabilityGrpcService`
  (`Get` signed in, `Upload` signed in and only while the setting is "clients", `Refresh` administrators and only while it
  is "server"), `VulnerabilityUpdater` (the daily fetch by the server, `IVulnerabilitySource`).
- `src/Oadm.Client/Vulnerabilities`: `VulnerabilityCatalog` (the client's copy, the per-row match), `VulnerabilityUpdates`
  (the fetch by the clients), `ServerConnection`'s loop (at connect, then hourly); the column, the search, the Settings card
  (`SettingsViewModel.Vulnerabilities.cs`), the context menu entry and its window (`DeviceVulnerabilitiesViewModel`,
  `DeviceVulnerabilitiesWindow`); fake mode serves the base list.

## Tests

`tests/Oadm.Server.Tests/Vulnerabilities`: the feed from recorded answers (`Fixtures/Vulnerabilities`: six cve.org records
and the NVD page: exact ranges, free text with NVD's ranges, models, a track list, Camera Station left out), every text form,
model codes, the version compare, the matcher (worst first, fixed build, 5.0 rule, cache), the list's limits; the store
(newest wins across a restart, broken file, newer release); the service (modes, permissions, audit, no outgoing call while
off). `tests/Oadm.Client.Tests`: the client update with a fake fetch and clock, column text, sort and tooltip, rows following
a new list and a firmware update, the CVE search, the Settings card, the context menu entry and the window
(`DeviceVulnerabilitiesWindowTests`); headless screenshots (`VulnerabilityHeadlessTests`,
`OADM_SCREENSHOT_DEVICES` renders a JSON list of real devices); Perf at 100,000 devices.
