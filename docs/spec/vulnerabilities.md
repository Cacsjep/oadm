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
- NVD configurations "running on" exactly one hardware model (`AND` with one `cpe:2.3:h:axis:<model>`) are that model's range,
  never every AXIS OS device, its `axis_os` range cut around the LTS tracks NVD fixes apart (for that model or every
  device); a configuration with several models (the devices of a chip) stays AXIS OS-wide. NVD's ranges taken for the
  record's AXIS OS entry keep that entry's chips; NVD's single-model ranges come along once and only for models the record
  does not give ranges of its own (its own entry wins; one left out for free text or an open end does not count).
- Chips: an entry's `platforms` (cve.org) limit it to those chips, short like the SoC column ("ARTPEC 8" and "ARTPEC-8" are
  `A8`, "Ambarella CV25" is `CV25`, "i.MX6 SX" stays). A platform without a digit ("Linux") is no chip and limits nothing.
- One entry per model and chips, each range once: NVD repeats ranges per configuration, and a record can name a product
  twice.
- OADM's format (`VulnerabilityList`, JSON): the list's date, where it came from (`release`, `server`, `client`) and per CVE:
  id, published, CVSS score and severity (the CNA's, else the ADP's, else NVD's), the English description, the Axis advisory
  link (a reference on axis.com) and the affected versions (`model` or every AXIS OS device, ranges with `from`, `lessThan`
  or `lessThanOrEqual`, or a whole `track`, and optional `socs`: the chips it is limited to). A CVE without a matchable range
  is kept out. New fields are optional: older clients ignore them (a list with `socs` still reads, without the chip limit).
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
limits (5,000 CVEs, 2 MB, valid ids and versions, at most 32 chips of at most 64 characters per entry, none empty). An
empty list and a list dated more than 10 minutes after the server's clock are refused too, from every source
(`VulnerabilityStore.Refusal`: such an upload would blank the column for everyone and no newer list could replace it); a
stored file like that loses to the base list at the start and the next sound list replaces it. The card shows the count, the list's date and where it came from.

## Devices page

- Column **Vulnerabilities** (column chooser, on by default, after AXIS OS): "2 · High" (count and the highest severity of
  the certain matches), empty when none is known. `ui:StatusChip` red for High and Critical, amber for Medium, plain text
  for Low or unrated. Possible matches (see Matching) are not counted and set no level: a muted "+3 possible" after it, or
  "3 possible" alone. Sorting by it: highest certain severity, then the most certain CVEs, then the most possible ones; a
  device with only possible ones sorts below every device with a certain one.
- The cell's tooltip (`VulnerabilityTip`, built when it opens, none for a device without CVEs) lists the CVEs worst first,
  the possible ones last, in three aligned columns: the id, the level (only the level colored: Critical and High red,
  Medium amber, Low plain, "Possibly affected" muted) and "fixed in 11.11.93" (the end of the range of the device's track;
  a possible one "fixed in 11.11 (check the advisory for your LTS)"); at most 12 rows, then "and 17 more". Its own ToolTip,
  wider than the theme's text tooltips (640 px), with the theme's look.
- Context menu **Vulnerabilities** (shield-alert icon, after Tags): only when a selected device has known CVEs, certain or
  possible, never an empty window. The window (`DeviceVulnerabilitiesWindow`, one card) names the device (model, address,
  AXIS OS version) or says "3 of 5 selected devices", gives the count ("2 CVEs, 3 possible"; "3 possible" without certain ones),
  and lists every CVE once per kind, worst first, the possible ones last (a CVE certain on some selected devices and
  possible on others has a row for each, each with its devices): CVE, Level (the column's chips; "Possibly affected" a
  neutral chip), Score,
  Fixed in (the fixed version of each affected device's track, lowest first, "Not listed" without one), Devices (several
  devices only: how many it affects, the first 12 in its tooltip, then "and 5 more"), the full description (wraps, no line
  limit: `TextBlock.cellText.fullText`) and an "Advisory" link (https only, opens the browser). A snapshot taken when it
  opens; the selection is grouped by its shared per-model-version-and-chip result first (one pass at 100,000 devices).
- Search: a CVE id ("CVE-2024-6509", any case) shows the devices it may affect, certain and possible.
- Matching: model and AXIS OS version against the ranges, numbers compared part by part (6.50.5.19, 11.11.93,
  12.10.36; text after the numbers ignored). Ranges for every AXIS OS device apply from 5.0 on: devices with their own
  firmware line (door controllers on 1.x such as the A1001) only match ranges naming their model. An entry with `socs`
  matches only devices with one of those chips (the device's `Soc`, compared short, upper case, letters and digits only); a
  device whose chip is not known yet may be affected and matches.
- Possible matches (`DeviceVulnerability.Possible`): the records often give one range for every track ("6.50 - 11.10",
  fixed in 11.11) while Axis fixed the LTS tracks later with builds only the advisory names (10.12.249, 9.80.78, ...;
  OADM reads no advisory: data only from the CVE records and NVD). A match of an every-AXIS OS entry is possible when the
  device runs an LTS track (`AxisOsTracks.Lts`: 6.50, 8.40, 9.80, 10.12, 11.11, 12.11; the same list as the Upgrade AXIS OS
  plugin's `AxisOsUpgradePath.LtsTracks`) below the track of the range's end. It stays certain when the range ends right
  after the device's track (a whole track named: "10.11 - 10.12" is below 10.13) and for ranges of a model. When an entry
  has ranges ending on the device's LTS track (its own fix), only those count: a later build of that track is not affected.
  A certain match of a CVE wins over a possible one (a model's own entry).
- Done in the client per model, AXIS OS version and chip and cached
  (100,000 devices share a few dozen versions): a new list for 100,000 devices about 100 ms, a search by CVE about 70 ms. "May be affected": the ranges do not say whether the affected feature is in use.

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
and the NVD page: exact ranges, free text with NVD's ranges, models, a track list, Camera Station left out, a chip with NVD's
repeated and single-model ranges; `Platforms/`: exact ranges per chip), every text form, model codes, platforms as chips, the
version compare, the matcher (worst first, fixed build, 5.0 rule, chips and an unknown chip, possible on an LTS track, cache), the list's limits; the store
(newest wins across a restart, broken file, newer release, an empty list or one dated in the future); the service (modes,
permissions, audit, refused uploads, no outgoing call while off). `tests/Oadm.Client.Tests`: the client update with a fake fetch and clock, column text with possible ones, sort and tooltip, rows following
a new list, an AXIS OS upgrade and a chip, the CVE search (possible too), the Settings card, the context menu entry and the window with possible rows
(`DeviceVulnerabilitiesWindowTests`); headless screenshots (`VulnerabilityHeadlessTests`,
`OADM_SCREENSHOT_DEVICES` renders a JSON list of real devices); Perf at 100,000 devices.
