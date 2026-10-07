# Oadm.Plugins.DateTime

Task plugin `oadm.datetime`: **Date and time** in the device context menu (group Maintenance, icon `clock`, no
toolbar button). A clone of the ADM / AXIS Camera Station **Set date and time** dialog for any number of devices.

| Part | Project | Assembly |
|---|---|---|
| Server (task + read-only query) | `plugins/Oadm.Plugins.DateTime` | `Oadm.Plugins.DateTime.Server.dll` |
| Client (Avalonia dialog) | `plugins/Oadm.Plugins.DateTime.Client` | `Oadm.Plugins.DateTime.Client.dll` |
| Tests | `tests/Oadm.Plugins.DateTime.Tests` | |

Both parts deploy to `artifacts/plugins/oadm.datetime/`. Root namespace `Oadm.Plugins.DateAndTime` (a namespace
segment `DateTime` would hide `System.DateTime`). The client references the server project for the payload, the
validation rules and the time zone list.

## ADM behavior (research)

- The ADM user manual (https://help.axis.com/en-us/axis-device-manager) has no date and time chapter; its only time
  related text is "Select **Set NTP servers** in the drop-down menu" in the "Configure devices > Advanced > Set
  configuration" example. The dialog ADM and AXIS Camera Station share is documented in the **AXIS Camera Station 5**
  manual (https://help.axis.com/en-us/axis-camera-station-5, "Set date and time"):
  - "The date and time settings for your Axis devices can be synchronized with the server computer time, with an NTP
    server, or set manually." Right-click the device and select **Set date and time**.
  - "**Device time** lists the current date and time for your Axis device. When selecting multiple devices, Device time
    is not available."
  - "Select the time zone you want to use with your Axis product from the **Time zone** drop-down list."
  - "Select **Automatically adjust for daylight saving time changes** if your product is located in an area that uses
    daylight saving time."
  - "Time zone can be set when selecting the Synchronize with NTP server or Set manually time mode."
  - **Time mode**: "**Synchronize with server computer time**", "**Synchronize with NTP server** ... Enter the IP address,
    DNS or hostname of the NTP server", "**Set manually**". Then OK.
- Older AXIS Camera Station manual (https://www.manualslib.com/manual/814746/Axis-Camera-Station.html?page=54): under
  Synchronize with NTP server "**Obtain from DHCP** - use an NTP server whose IP address was discovered using DHCP" and
  "**Use server** - manually fill in the correct IP address".
- ACS 5 / ACS Pro "Time synchronization" page: NTP source Static / DHCP, primary and secondary NTP server, Synced
  (firmware 9.1+), and "Send alarm when the time difference between server and device is more than 2 seconds".
- AXIS OS web interface (https://help.axis.com/en-us/axis-os-web-interface-help, System > Date and time): Automatic date
  and time (manual NTS KE servers / NTP servers using DHCP / manual NTP servers), Custom date and time ("Get from
  system"), Time zone DHCP or manual.

How OADM clones it (`DateTimeWindow`):

| ADM / ACS | OADM |
|---|---|
| Device time (single device only) | **Device time** card for the first selected device (read-only query): device time with offset, time zone, time mode with sync state and NTP offset, server time and the difference ("device and server agree", "device is 3.2 s ahead"). With several devices the card says which device it shows. |
| Time zone drop-down | **Time zone** card: the 313 IANA zones of AXIS OS 12.11 in a DataGrid (UTC offset, City, Time zone, DST) sorted by offset like the Windows list, `ui:SearchBox` (city, id or "UTC+05:30"). Single device with an IANA zone: preselected. |
| Automatically adjust for daylight saving time changes | Same check box. Off = the zone's standard-time POSIX rule with daylight saving off (`setPosixTimeZone enableDst=false`). |
| Time zone only with NTP / manual | Server time mode disables the zone: the devices get the OADM server's time zone (shown). |
| Synchronize with server computer time | NTP off, the OADM server's UTC time sent once per device when its task runs (one-shot, like ACS). |
| Synchronize with NTP server: Obtain from DHCP / Use server | Same radio buttons; "Use servers" takes up to 5 host names or addresses, one per line; **Use NTS (Network Time Security)** when the devices support it (NTS KE servers). |
| Set manually | Date + Time in the device's time zone, "Use this computer's time"; NTP off. |
| (always writes everything) | Extra first choice **Keep unchanged** (time mode) and an unselected / "Keep unchanged" time zone: only what the user changes is written (multi-device safety). |
| OK | OK (enabled when something changes and every field is valid; field errors under the inputs via INotifyDataErrorInfo). |

## VAPIX research

- **Time API** `time-service` (`/axis-cgi/time.cgi`, JSON POST, AXIS OS 9.30+,
  https://developer.axis.com/vapix/network-video/time-api/): `getDateTimeInfo`, `getAll` (+ IANA zone list),
  `setDateTime` (`dateTime` "YYYY-MM-DDThh:mm:ssZ", UTC), `setTimeZone` (`timeZone` IANA), `setPosixTimeZone`
  (`posixTimeZone`, `enableDst`), `resetTimeZone` (DHCP zone), `getSupportedVersions`. 1.1 added `dhcpTimeZone` /
  `dhcpTimeZoneUtilized`. Deprecated as of AXIS OS 12.4 in favor of the device-config REST Time API, still listed on
  12.11.
- **NTP API** `ntp` (`/axis-cgi/ntp.cgi`, AXIS OS 9.10+, https://developer.axis.com/vapix/network-video/ntp-api/):
  `getNTPInfo`, `setNTPClientConfiguration` (`enabled`, `serversSource` "DHCP"/"static", `staticServers`, `NTSEnabled`,
  `staticNTSKEServers`, `NTSKEServerCACerts`), `getSupportedVersions`. No per-version history is published; `timeOffset`
  is in milliseconds, DHCP "falls back to static if none were obtained".
- **param.cgi** `Time.*` (https://developer.axis.com/vapix/network-video/system-settings/): `Time.SyncSource` (PC, NTP,
  None), `Time.ObtainFromDHCP` (NTP server from DHCP), `Time.NTP.Server` (one server), `Time.NTP.VolatileServer`
  (read-only, from DHCP), `Time.POSIXTimeZone`, `Time.DST.Enabled` (removed in AXIS OS 13.0).
- **date.cgi** (`/axis-cgi/admin/date.cgi?action=set&year=...`): removed in AXIS OS 11.0, semantics of its time zone
  argument unverified. **Not used**: devices without the Time API cannot take a date and time from OADM.
- **Device-config REST** `/config/rest/time/v2` and `/config/rest/network-time-sync/v1` (listed only in
  `/config/discover`, not in `apidiscovery.cgi`, so `CanRun` cannot see them): not used yet; the successor when
  time.cgi disappears (AXIS OS 13). Recorded for reference in the fixtures.

## What 10.0.0.48 reports (AXIS P3265-V, AXIS OS 12.11.77, read-only)

- `apidiscovery`: `time-service 1.1`, `ntp 1.5`, `param-cgi 1.0`; `getSupportedVersions` lists only 1.1 / 1.5, but a
  request with apiVersion 1.0 is answered (as 1.1 / 1.5). apiVersion 2.0 = error 4001 "The specified version is not
  supported", unknown method = 4000 "Method does not exist". Requests need `Content-Type: application/json`, otherwise
  2000 "Invalid request".
- `getDateTimeInfo`: `dateTime` 2026-10-07T16:24:01Z, `localDateTime` +02:00, `maxYearSupported` 2069, a POSIX zone
  `<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00` with `dstEnabled` true and **no** `timeZone` (no IANA zone set).
- `getNTPInfo`: enabled, `serversSource` "static", `staticServers` ["10.0.0.17"], `maxSupportedStaticServers` 5,
  `NTSEnabled` false, empty NTS lists, synced, `timeOffset` 0.016 ms, minpoll 6, maxpoll 10.
- param.cgi: `Time.SyncSource=NTP`, `Time.ObtainFromDHCP=no`, `Time.NTP.Server=10.0.0.17`,
  `Time.NTP.VolatileServer=0.0.0.0`, same POSIX zone, `Time.DST.Enabled=yes`. `date.cgi` answers 404.
- REST `time/v2`: same values plus `dhcp.enabled` false; `iana/getTimeZoneList` (POST `{"data":null}`) lists 313 IANA
  zones, bundled as `Model/TimeZones.txt`. REST `network-time-sync/v1`: NTP as above, PTP supported (stopped).
- Fixtures: `tests/Oadm.Plugins.DateTime.Tests/Fixtures`. No write was sent to the device.

## Decision table (per device, decided again in `ExecuteAsync` from a fresh API list)

| Change | Fresh `getApiList` contains | Request | Otherwise |
|---|---|---|---|
| Read time | `time-service` 1.0+ | `getDateTimeInfo` (listed version) | param.cgi list `Time` |
| Read NTP | `ntp` 1.0+ | `getNTPInfo` | param.cgi `Time` (skipped when already read) |
| Time zone, DST on | `time-service` 1.0+ | `setTimeZone {timeZone}` | `param-cgi` 1.0: `Time.POSIXTimeZone` + `Time.DST.Enabled=yes` (POSIX from the IANA rule) |
| Time zone, DST off | `time-service` 1.0+ | `setPosixTimeZone {posixTimeZone, enableDst:false}` | param.cgi `Time.DST.Enabled=no` |
| NTP servers / DHCP | `ntp` 1.0+ | `setNTPClientConfiguration {enabled, serversSource, staticServers}` | param.cgi `Time.SyncSource=NTP`, `Time.ObtainFromDHCP`, `Time.NTP.Server` (one server only, more = refused) |
| NTS KE servers | `ntp` **1.5+** and `getNTPInfo` reports `NTSEnabled` | `... NTSEnabled:true, staticNTSKEServers` | refused, nothing changed |
| NTS fields kept | `ntp` 1.5+ with NTS | `NTSEnabled` / `staticNTSKEServers` sent with their current values | not sent |
| NTP off (server time, manual) | `ntp` 1.0+ | `setNTPClientConfiguration {enabled:false, ...current}` | param.cgi `Time.SyncSource=None` |
| Date and time (server time, manual) | `time-service` 1.0+ | `setDateTime {dateTime: UTC}` | refused (`DeviceNotCompatibleException`), nothing changed |
| anything | only a different major (e.g. 2.x) or nothing | none | `DeviceNotCompatibleException` at Check compatibility |

`CanRun` needs status Ok/Unknown and `time-service` 1.0 or `param-cgi` 1.0 in the cached list. NTS is required from
1.5 because that is the version verified with NTS fields (no history published). All `Require(...)` calls happen in
the Validate step, before the first write. Values the device already has are not written ("Already Europe/Vienna",
"Already set").

## Per-device steps

1. **Check compatibility** (fresh API list; detail "time-service 1.1, ntp 1.5" or "param.cgi").
2. **Read current time settings** (`getDateTimeInfo` or param.cgi).
3. **Read NTP settings** (`getNTPInfo`, or param.cgi; Skipped when param.cgi was already read).
4. **Validate settings**: payload rules again, the device limits (max static servers, max year, one server on
   param.cgi), NTS support, the manual time converted to UTC in the target zone (a time in the spring gap is refused),
   then the plan. Any problem fails here with "Nothing was changed".
5. **Set time zone** (planned when the zone changes or in server time mode; Warning when the server's zone is not an
   AXIS zone).
6. **Set NTP configuration** (NTP mode) or **Turn off NTP** (server time, manual).
7. **Set date and time** (server time: the server's UTC at that moment; manual: the converted UTC).
8. **Verify time settings**: reads again; zone / POSIX / DST must match and the clock must be within 3 s of the time
   set (+ elapsed); otherwise Warning "Check failed: ...". Skipped when neither zone nor time changed.
9. **Verify NTP settings** (only when the time mode changes): enabled, source, servers, NTS must match; detail adds "the
   device synchronizes within a few minutes" while not yet synced.

A failing write fails its step with the device text ("setTimeZone failed (2002): Invalid time zone.") or the HTTP /
transport mapping ("Forbidden - HTTP 403 (administrator rights are required)", "Connection refused", "Timeout: the
device did not answer"), plus "Nothing was changed." or "Already applied: Time zone Europe/Vienna.".

## Payload and task names

`DateTimePayload` (camelCase JSON): `timeZone` (IANA or null = keep), `daylightSaving` (default true), `mode`
(`Keep`, `Ntp`, `ServerTime`, `Manual`), `ntp` {`source` Dhcp/Static, `servers`, `nts`}, `manualDateTime`
("yyyy-MM-ddTHH:mm:ss", device local time). No secrets. `GetTaskName(payloadJson)` on the plugin class gives the exact
task name: "Set time zone Europe/Vienna", "Set NTP servers 10.0.0.17, pool.ntp.org" ("a, b, c +2"), "Set NTP servers
from DHCP", "Set NTS KE servers ...", "Sync with server time", "Set date and time 2026-10-07 18:00", or "Change date
and time" when the zone and the time mode change. It has the signature of the optional SDK hook
`ITaskPlugin.GetTaskName(string? payloadJson)` that is being added in parallel; once that hook is on the interface the
method implements it without changes.

## Query

`getTimeSettings` (read-only, `ITaskPluginQuery`): `CurrentTimeSettings` of the device (getDateTimeInfo + getNTPInfo or
param.cgi) plus `serverUtc`, `serverTimeZone` (IANA id of the OADM server's zone, null when AXIS devices do not know
it) and `serverTimeZoneName`.

## Time zones

`Model/TimeZones.txt` = the device's 313 IANA ids. Labels and offsets come from the OS time zone database
(`TimeZoneInfo`, IANA ids on Windows through ICU). Old ICU data (Windows 10 1809) lacks a few new ids; same-rules
aliases cover them (Europe/Kyiv -> Europe/Kiev, Pacific/Kanton -> Pacific/Enderbury, America/Ciudad_Juarez ->
America/Denver, America/Coyhaique -> America/Punta_Arenas, Asia/Urumqi -> Asia/Dhaka); Antarctica/Troll has none there
(listed as UTC, set by IANA id only). POSIX strings use the device's own style: Europe/Vienna =
`<UTC1>-1<UTC2>-2,M3.5.0/2:00:00,M10.5.0/3:00:00` (exactly what 10.0.0.48 reports), Asia/Kolkata = `<UTC530>-5:30`.
