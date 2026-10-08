# VAPIX Commander command library

The bundled commands of the VAPIX Commander plugin. One file per category,
`<Category>.json` = `{ "formatVersion": 1, "category": "...", "commands": [ ... ] }`, in the format
of `plugins/Oadm.Plugins.VapixCommander/Schema/command-format.md`, validated against `plugins/Oadm.Plugins.VapixCommander/Schema/command.schema.json`.
`I/O` lives in `IO.json` because `/` cannot be part of a file name.

161 commands: 58 readers (`writes: false`) and 103 setters/actions (`writes: true`, 17 of them `dangerous`).

| Category | Commands | File |
|---|---|---|
| Common | 6 | `Common.json` |
| System | 11 | `System.json` |
| Network | 15 | `Network.json` |
| Security | 9 | `Security.json` |
| Users | 5 | `Users.json` |
| Time | 8 | `Time.json` |
| Video | 26 | `Video.json` |
| Image | 25 | `Image.json` |
| PTZ | 10 | `PTZ.json` |
| Audio | 8 | `Audio.json` |
| I/O | 10 | `IO.json` |
| Events | 9 | `Events.json` |
| Storage | 8 | `Storage.json` |
| Applications | 4 | `Applications.json` |
| Maintenance | 7 | `Maintenance.json` |

## Conventions

- **Ids** are `<category>.<object>[.<detail>].<verb>`, lower case: `image.daynight.shiftlevel.set`,
  `time.ntp.get`. I/O uses `io.`, Applications `apps.`. Ids never change once released; a changed
  command gets `version` + 1.
- **Readers** are named "Get ..." / "List ...", use GET, or POST with a JSON read method (`get*`, `list`,
  `listAll`, `status`, `systemready`) or a SOAP `Get*` operation. Every param.cgi and JSON reader has
  `response.extract` so the technician sees the current values in the step detail.
- **Setters** are named "Set ..." or say what they do ("Restart device"). Restart, factory default,
  firmware rollback, capture mode (restarts), network addressing/DNS/host name, HTTP security policies
  and all user changes are `dangerous: true`. Formatting a disk and removing an application are too.
- **Video**: every Video, Image and PTZ command sets `hasVideoOnly: true` (plus tampering and snapshot).
- **Indexes**: `source` = `ImageSource.I#` (0-based sensor), `channel` = `Image.I#` (0-based video
  channel), `camera` = VAPIX camera number (1-based, ptz.cgi, overlays, Zipstream).
- **JSON APIs** send `"apiVersion": "1.0"` (`"2.0"` for supervised I/O) and `"context": "oadm"`.
  Axis devices answer with the highest minor they support (10.0.0.48 answers 1.38 to a 1.0
  network-settings request), so the lowest major.0 works on every firmware that has the method.
- **requires** names the `apidiscovery.cgi getApiList` id with `minVersion` `<major>.0`.
  Legacy CGIs without an own id (`systemlog.cgi`, `serverreport.cgi`, `geolocation/*.cgi`,
  `io/port.cgi`, `virtualinput/*.cgi`, `/vapix/services` SOAP) require `param-cgi 1.0` as the
  always-present baseline. Mapping of the other CGIs: `pwdgrp.cgi` = `user-management`,
  `applications/*.cgi` = `packagemanager`, `ptz.cgi`/`ptzconfig.cgi` = `ptz-control`,
  `jpg/image.cgi` = `media-cgi`, `privacymask.cgi` = `privacy-mask`, `disks/*.cgi` = `disk-management`,
  `disks/networkshare/*` = `disk-network-share`, `record/list.cgi` = `recording`,
  `mediaclip.cgi` = `mediaclip`, `firmwaremanagement.cgi` = `fwmgr`.
- **Response kinds**: legacy CGIs that answer `OK`/`key=value` text and report failures as
  `Error: ...` or `# Error: ...` with HTTP 200 (`ptz.cgi`, `ptzconfig.cgi`, `io/port.cgi`,
  `pwdgrp.cgi`, `mediaclip.cgi`, `applications/control.cgi`, `temperaturecontrol.cgi`) use kind
  `param-cgi`, so the error line is detected and `key=value` bodies can be extracted. `privacymask.cgi
  ?query=listpxjson` is plain JSON without an `error` object and uses `json-rpc`.
- **Booleans** in param.cgi render `yes`/`no`; parameters with other spellings set `trueValue`/`falseValue`
  (`System.AccessLog` On/Off, `System.BoaProtViewer` anonymous/password, WS-Discovery Yes/No,
  `PTZ.ImageSource.I#.PTZEnabled` true/false).
- **Passwords** (`users.add`, `users.password.set`, `events.mqtt.client.configure`) only travel in POST
  bodies (form or JSON), never in the URL; a test enforces this.
- Network addressing (static IP, DHCP switch) is not in the library: the Network plugin does it and
  follows the device to its new address; a raw command would lose the device.

## Verification

Every parameter, method and response path was checked against developer.axis.com and, where possible
without writing, against the dev camera **10.0.0.48** (AXIS P3265-V Dome Camera, AXIS OS 12.11.77):

- **Readers**: all 59 were executed on 10.0.0.48 by the hardware test (HTTP 2xx, no VAPIX error, every
  `response.extract` resolved). `image.light.info` reached the device, which answers with its documented
  error 1005 "No light hardware found" (the model lists `light-control` but has no IR light).
- **param.cgi setters**: every parameter name and every enum/boolean value the command can send was
  checked against `param.cgi?action=listdefinitions&listformat=xmlschema` read on 10.0.0.48 (the
  device's own type definitions). They were never sent.
- **Other setters**: method and parameter names are from the VAPIX documentation. 10.0.0.48 lists
  their API and its readers work there; the setter itself was never sent (the camera is read-only for
  this work).

Recorded responses of 20 readers are in
`tests/Oadm.Plugins.VapixCommander.Library.Tests/Fixtures/<command id>.txt`; a test checks that every
extract of those commands resolves on them.

### Tests

`tests/Oadm.Plugins.VapixCommander.Library.Tests` (no reference to the plugin projects):

- every file validates against `command.schema.json` (JsonSchema.Net 8.0.5, MIT, format validation on);
- ids and names unique across files, file name = category, every command's category = file category;
- every `{{placeholder}}` in the request and in `response.extract` has a field, every field is used;
- enum defaults are options, `min <= default <= max`, integer defaults are whole numbers, string
  defaults match their pattern, password fields have no default;
- readers are GET or use read methods only, param.cgi setters use `action=update` and expect `OK`,
  `dangerous` implies `writes`, restart/factory default/network/user changes are dangerous;
- docs links point to developer.axis.com, no `Authorization` header, passwords never in the URL;
- default rendering leaves no placeholder behind; JSON numbers/booleans stay typed;
- **Hardware** (`Category=Hardware`, needs `dev-cameras.yaml` or `OADM_DEV_CAMERAS`): runs every reader
  whose `requires` are met on each dev camera with a minimal standalone HTTP client (Digest, Basic over
  HTTPS only, any certificate). `OADM_RECORD_FIXTURES=<folder>` saves the responses.

```
dotnet test tests/Oadm.Plugins.VapixCommander.Library.Tests --filter Category!=Hardware
dotnet test tests/Oadm.Plugins.VapixCommander.Library.Tests --filter Category=Hardware
```

### Notes from 10.0.0.48

- JSON CGIs answer `Content-Type: application/json; charset=utf8`; .NET `ReadAsStringAsync` throws on
  `utf8`, so clients must decode the bytes as UTF-8 themselves.
- `apidiscovery` versions and `getSupportedVersions` disagree: power-settings 1.8 vs 1.7, network-settings
  1.37 vs 1.38, systemready 1.4 vs 1.5, mdnssd 1.1 vs 1.2, video-streaming-indicator 1.1 vs 1.2.
  `requires` therefore only uses `<major>.0`.
- Errors are not uniform: most JSON APIs return HTTP 200 with `error.code` as a number,
  `daynight.cgi` returns HTTP 500 with `error.code` as a string ("2103"); `ptz.cgi` returns HTTP 200
  `Error:` text; with PTZ disabled `query=presetposcam` answers "PTZ disabled" without `Error:`, and
  `query=position` answers 204 with an empty body.
- The legacy `Network.IPAddress` (192.168.0.90) is the stored static address, not the active one
  (10.0.0.48 via DHCP); use `network.info.get` (`devices.0.IPv4.addresses`).
- Many APIs exist only as REST under `/config/rest/...` (SNMP, firewall, LLDP, cert, crypto policy,
  network-time-sync, user-management v2, action rules v2). They are listed by `/config/discover`, not by
  `getApiList`, so `requires` cannot express them; the library uses the param.cgi/JSON equivalents.
- `usermanagement.cgi` does not exist although `user-management 1.2` is listed; users are managed via
  `pwdgrp.cgi`.

## What the engine must support (format notes)

The library relies on these readings of command-format.md, which the engine must implement:

1. `response.extract` `param`/`path` may contain `{{placeholders}}` (e.g. `Image.I{{channel}}.Stream.FPS`)
   and `path` segments may be array indices (`data.devices.0.IPv4.addresses.0.address`). Arrays and
   objects are shown as compact JSON. `param` keys are matched without the `root.` prefix and with
   surrounding quotes removed (`pwdgrp.cgi action=get` answers `admin="root,joe"`).
2. Query keys and values are URL-encoded by the engine (`io.output.pulse` sends `action=1:/1000\`).
3. A JSON body string that is exactly `"{{field}}"` keeps the type of integer, number and boolean fields;
   enum fields are only used in strings (JSON numbers use integer fields, e.g. MQTT QoS).
4. `raw` for `jpg/image.cgi` returns JPEG bytes; the step detail should show size and content type, not
   a text excerpt.

Gaps in format v1 found while writing the library:

- `requires` only knows `getApiList` ids. REST APIs (`/config/discover`) and legacy CGIs without an id
  cannot be expressed; legacy CGIs use `param-cgi 1.0`, REST APIs were left out.
- `text` has no failure rule, so a legacy CGI that reports `Error: ...` with HTTP 200 would count as
  success; such commands use `param-cgi`. A `response.errorPattern` would be cleaner.
- `extract` has no XML form (disk list, Zipstream status, geolocation, SOAP lists show the raw body);
  an XPath-like `xpath` would let them show values.
- No list field type: a server list needs one fixed field per entry, so `network.dns.set` and
  `time.ntp.static.set` set exactly one server.
- Placeholders in JSON object keys are unspecified, so custom HTTP header *set* was left out.

## All commands

| Id | Name | Category | Writes | Verification |
|---|---|---|---|---|
| `common.device.info` | Get device information | Common | no | executed on 10.0.0.48 |
| `common.firmware.status` | Get firmware status | Common | no | executed on 10.0.0.48 |
| `common.identify.flash` | Identify device (flash status LED) | Common | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `common.identify.get` | Get status LED setting | Common | no | executed on 10.0.0.48 |
| `common.statusled.set` | Set status LED | Common | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `common.systemready.get` | Get system ready status | Common | no | executed on 10.0.0.48 |
| `system.geolocation.get` | Get geolocation | System | no | executed on 10.0.0.48 |
| `system.geolocation.set` | Set geolocation | System | yes | docs only (API present on 10.0.0.48, never sent) |
| `system.power.dynamic.get` | Get dynamic power mode | System | no | executed on 10.0.0.48 |
| `system.power.dynamic.set` | Set dynamic power mode | System | yes | docs only (API present on 10.0.0.48, never sent) |
| `system.power.status` | Get power consumption | System | no | executed on 10.0.0.48 |
| `system.remotesyslog.disable` | Disable remote syslog | System | yes | docs only (API present on 10.0.0.48, never sent) |
| `system.remotesyslog.enable` | Enable remote syslog | System | yes | docs only (API present on 10.0.0.48, never sent) |
| `system.remotesyslog.get` | Get remote syslog | System | no | executed on 10.0.0.48 |
| `system.remotesyslog.setup` | Set remote syslog server | System | yes | docs only (API present on 10.0.0.48, never sent) |
| `system.temperature.get` | Get temperatures | System | no | executed on 10.0.0.48 |
| `system.webserver.get` | Get web server settings | System | no | executed on 10.0.0.48 |
| `network.bonjour.get` | Get Bonjour (mDNS) | Network | no | executed on 10.0.0.48 |
| `network.bonjour.set` | Set Bonjour (mDNS) | Network | yes | docs only (API present on 10.0.0.48, never sent) |
| `network.discovery.get` | Get discovery protocols | Network | no | executed on 10.0.0.48 |
| `network.dns.set` | Set DNS server | Network | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `network.hostname.set` | Set host name | Network | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `network.info.get` | Get network settings | Network | no | executed on 10.0.0.48 |
| `network.ipv6.set` | Enable or disable IPv6 | Network | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `network.qos.get` | Get QoS (DSCP) | Network | no | executed on 10.0.0.48 |
| `network.qos.set` | Set QoS (DSCP) | Network | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `network.rtsp.get` | Get RTSP settings | Network | no | executed on 10.0.0.48 |
| `network.rtsp.set` | Set RTSP server | Network | yes (dangerous) | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `network.snmp.get` | Get SNMP settings | Network | no | executed on 10.0.0.48 |
| `network.snmp.trap.set` | Set SNMP trap receiver | Network | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `network.snmp.v2c.set` | Set SNMP v1/v2c | Network | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `network.upnp.set` | Set UPnP | Network | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `security.anonymousviewer.set` | Set anonymous viewer access | Security | yes (dangerous) | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `security.bruteforce.get` | Get brute-force protection | Security | no | executed on 10.0.0.48 |
| `security.bruteforce.set` | Set brute-force protection | Security | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `security.httpauth.set` | Set HTTP authentication policy | Security | yes (dangerous) | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `security.httpheaders.get` | Get custom HTTP headers | Security | no | executed on 10.0.0.48 |
| `security.https.get` | Get HTTPS settings | Security | no | executed on 10.0.0.48 |
| `security.https.policy.set` | Set HTTPS connection policy | Security | yes (dangerous) | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `security.ssh.get` | Get SSH state | Security | no | executed on 10.0.0.48 |
| `security.ssh.set` | Enable or disable SSH | Security | yes | docs only (API present on 10.0.0.48, never sent) |
| `users.add` | Add user | Users | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `users.list` | List users | Users | no | executed on 10.0.0.48 |
| `users.password.set` | Change user password | Users | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `users.remove` | Remove user | Users | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `users.role.set` | Change user role | Users | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `time.datetime.set` | Set date and time (UTC) | Time | yes | docs only (API present on 10.0.0.48, never sent) |
| `time.get` | Get date and time | Time | no | executed on 10.0.0.48 |
| `time.ntp.dhcp.set` | Use NTP servers from DHCP | Time | yes | docs only (API present on 10.0.0.48, never sent) |
| `time.ntp.disable` | Disable NTP | Time | yes | docs only (API present on 10.0.0.48, never sent) |
| `time.ntp.get` | Get NTP status | Time | no | executed on 10.0.0.48 |
| `time.ntp.static.set` | Set NTP server | Time | yes | docs only (API present on 10.0.0.48, never sent) |
| `time.posixtimezone.set` | Set POSIX time zone | Time | yes | docs only (API present on 10.0.0.48, never sent) |
| `time.timezone.set` | Set time zone | Time | yes | docs only (API present on 10.0.0.48, never sent) |
| `video.bitrate.abr.set` | Set bitrate mode ABR | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.bitrate.mbr.set` | Set bitrate mode MBR | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.bitrate.vbr.set` | Set bitrate mode VBR | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.capturemode.get` | Get capture modes | Video | no | executed on 10.0.0.48 |
| `video.capturemode.set` | Set capture mode | Video | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `video.compression.set` | Set default compression | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.fps.set` | Set default frame rate | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.gop.set` | Set GOP length | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.overlay.remove` | Remove overlay | Video | yes | docs only (API present on 10.0.0.48, never sent) |
| `video.overlay.text.add` | Add text overlay | Video | yes | docs only (API present on 10.0.0.48, never sent) |
| `video.overlay.text.set` | Change text overlay text | Video | yes | docs only (API present on 10.0.0.48, never sent) |
| `video.overlays.list` | List overlays | Video | no | executed on 10.0.0.48 |
| `video.powerlinefrequency.set` | Set power line frequency | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.privacymasks.list` | List privacy masks | Video | no | executed on 10.0.0.48 |
| `video.resolution.set` | Set default resolution | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.rotation.set` | Set image rotation | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.signed.set` | Set signed video | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.stream.get` | Get video stream settings | Video | no | executed on 10.0.0.48 |
| `video.streamingindicator.get` | Get video streaming indicator | Video | no | executed on 10.0.0.48 |
| `video.streamprofile.create` | Create stream profile | Video | yes | docs only (API present on 10.0.0.48, never sent) |
| `video.streamprofile.remove` | Remove stream profile | Video | yes | docs only (API present on 10.0.0.48, never sent) |
| `video.streamprofiles.list` | List stream profiles | Video | no | executed on 10.0.0.48 |
| `video.streams.status` | Get active streams | Video | no | executed on 10.0.0.48 |
| `video.zipstream.get` | Get Zipstream status | Video | no | executed on 10.0.0.48 |
| `video.zipstream.gop.set` | Set Zipstream dynamic GOP | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `video.zipstream.strength.set` | Set Zipstream strength | Video | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.brightness.set` | Set brightness | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.contrast.set` | Set contrast | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.daynight.dwell.set` | Set day/night dwell times | Image | yes | docs only (API present on 10.0.0.48, never sent) |
| `image.daynight.get` | Get day/night configuration | Image | no | executed on 10.0.0.48 |
| `image.daynight.shiftlevel.set` | Set day/night shift level | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.defog.set` | Set defog | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.exposure.mode.set` | Set exposure mode | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.exposure.priority.set` | Set exposure priority | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.exposure.zone.set` | Set exposure zone | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.ircut.set` | Set IR cut filter | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.light.capabilities` | Get light (IR) capabilities | Image | no | executed on 10.0.0.48 |
| `image.light.disable` | Disable light (IR) | Image | yes | docs only (API present on 10.0.0.48, never sent) |
| `image.light.enable` | Enable light (IR) | Image | yes | docs only (API present on 10.0.0.48, never sent) |
| `image.light.info` | Get light (IR) information | Image | no | executed on 10.0.0.48 (device answers documented error: no light hardware) |
| `image.light.intensity.set` | Set light (IR) intensity | Image | yes | docs only (API present on 10.0.0.48, never sent) |
| `image.maxgain.set` | Set maximum gain | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.optics.autofocus` | Run autofocus | Image | yes | docs only (API present on 10.0.0.48, never sent) |
| `image.optics.focus.set` | Set focus position | Image | yes | docs only (API present on 10.0.0.48, never sent) |
| `image.optics.get` | Get focus and zoom | Image | no | executed on 10.0.0.48 |
| `image.optics.zoom.set` | Set zoom (magnification) | Image | yes | docs only (API present on 10.0.0.48, never sent) |
| `image.saturation.set` | Set saturation | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.settings.get` | Get image settings | Image | no | executed on 10.0.0.48 |
| `image.sharpness.set` | Set sharpness | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.wdr.set` | Set WDR | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `image.whitebalance.set` | Set white balance | Image | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `ptz.digital.set` | Enable digital PTZ | PTZ | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `ptz.home` | Go to home position | PTZ | yes | docs only (API present on 10.0.0.48, never sent) |
| `ptz.home.set` | Set current position as home | PTZ | yes | docs only (API present on 10.0.0.48, never sent) |
| `ptz.limits.get` | Get PTZ limits | PTZ | no | executed on 10.0.0.48 |
| `ptz.move.absolute` | Move to position | PTZ | yes | docs only (API present on 10.0.0.48, never sent) |
| `ptz.position.get` | Get PTZ position | PTZ | no | executed on 10.0.0.48 |
| `ptz.preset.goto` | Go to preset | PTZ | yes | docs only (API present on 10.0.0.48, never sent) |
| `ptz.preset.remove` | Remove preset | PTZ | yes | docs only (API present on 10.0.0.48, never sent) |
| `ptz.preset.save` | Save current position as preset | PTZ | yes | docs only (API present on 10.0.0.48, never sent) |
| `ptz.presets.list` | List PTZ presets | PTZ | no | executed on 10.0.0.48 |
| `audio.clip.play` | Play audio clip | Audio | yes | docs only (API present on 10.0.0.48, never sent) |
| `audio.clip.stop` | Stop audio clip | Audio | yes | docs only (API present on 10.0.0.48, never sent) |
| `audio.clips.list` | List audio clips | Audio | no | executed on 10.0.0.48 |
| `audio.devices.get` | Get audio device settings | Audio | no | executed on 10.0.0.48 |
| `audio.encoding.set` | Set audio encoding | Audio | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `audio.input.set` | Set audio input | Audio | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `audio.output.set` | Set audio output gain | Audio | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `audio.settings.get` | Get audio settings | Audio | no | executed on 10.0.0.48 |
| `io.output.pulse` | Pulse output | I/O | yes | docs only (API present on 10.0.0.48, never sent) |
| `io.output.set` | Set output state | I/O | yes | docs only (API present on 10.0.0.48, never sent) |
| `io.port.name.set` | Rename I/O port | I/O | yes | docs only (API present on 10.0.0.48, never sent) |
| `io.ports.active` | Check I/O ports active | I/O | no | executed on 10.0.0.48 |
| `io.ports.get` | Get I/O ports | I/O | no | executed on 10.0.0.48 |
| `io.supervised.count` | Get supervised I/O ports | I/O | no | executed on 10.0.0.48 |
| `io.supervised.get` | Get supervised input state | I/O | no | executed on 10.0.0.48 |
| `io.supervised.set` | Set input supervision | I/O | yes | docs only (API present on 10.0.0.48, never sent) |
| `io.virtualinput.activate` | Activate virtual input | I/O | yes | docs only (API present on 10.0.0.48, never sent) |
| `io.virtualinput.deactivate` | Deactivate virtual input | I/O | yes | docs only (API present on 10.0.0.48, never sent) |
| `events.actionrules.list` | List action rules | Events | no | executed on 10.0.0.48 |
| `events.mqtt.client.activate` | Connect MQTT client | Events | yes | docs only (API present on 10.0.0.48, never sent) |
| `events.mqtt.client.configure` | Configure MQTT client | Events | yes | docs only (API present on 10.0.0.48, never sent) |
| `events.mqtt.client.deactivate` | Disconnect MQTT client | Events | yes | docs only (API present on 10.0.0.48, never sent) |
| `events.mqtt.client.get` | Get MQTT client status | Events | no | executed on 10.0.0.48 |
| `events.mqtt.publication.get` | Get MQTT event publication | Events | no | executed on 10.0.0.48 |
| `events.mqtt.publication.set` | Publish events to MQTT | Events | yes | docs only (API present on 10.0.0.48, never sent) |
| `events.tampering.get` | Get tampering detection | Events | no | executed on 10.0.0.48 |
| `events.tampering.set` | Set tampering detection | Events | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `storage.cleanup.set` | Set storage cleanup | Storage | yes | param names and values checked on 10.0.0.48 (listdefinitions, not sent) |
| `storage.disks.list` | List storage disks | Storage | no | executed on 10.0.0.48 |
| `storage.format` | Format disk | Storage | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `storage.mount` | Mount disk | Storage | yes | docs only (API present on 10.0.0.48, never sent) |
| `storage.networkshares.list` | List network shares | Storage | no | executed on 10.0.0.48 |
| `storage.recordings.list` | List recordings | Storage | no | executed on 10.0.0.48 |
| `storage.sd.get` | Get SD card settings | Storage | no | executed on 10.0.0.48 |
| `storage.unmount` | Unmount disk | Storage | yes | docs only (API present on 10.0.0.48, never sent) |
| `apps.list` | List applications | Applications | no | executed on 10.0.0.48 |
| `apps.remove` | Remove application | Applications | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `apps.start` | Start application | Applications | yes | docs only (API present on 10.0.0.48, never sent) |
| `apps.stop` | Stop application | Applications | yes | docs only (API present on 10.0.0.48, never sent) |
| `maintenance.auditlog.get` | Get audit log | Maintenance | no | executed on 10.0.0.48 |
| `maintenance.factorydefault` | Factory default | Maintenance | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `maintenance.firmware.commit` | Commit firmware | Maintenance | yes | docs only (API present on 10.0.0.48, never sent) |
| `maintenance.firmware.rollback` | Roll back firmware | Maintenance | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `maintenance.restart` | Restart device | Maintenance | yes (dangerous) | docs only (API present on 10.0.0.48, never sent) |
| `maintenance.serverreport.get` | Get server report | Maintenance | no | executed on 10.0.0.48 |
| `maintenance.systemlog.get` | Get system log | Maintenance | no | executed on 10.0.0.48 |
