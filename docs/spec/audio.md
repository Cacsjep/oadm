# Audio

Status: **spec, not built**.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

One page to set up the audio of many devices: clips, sound, the display of C17 display speakers, light and siren
profiles, and the speaker and light tests. It covers the setup that installers otherwise do device by device or with
AXIS Audio Manager Pro, through documented VAPIX only.

Core plugin `plugins/Oadm.Plugins.Audio` (+ `.Client`), id `oadm.audio`, rail page **Audio** (group Maintenance), free
(Apache-2.0), **off by default**. Decisions of 2026-10-29, asked one by one, checked on an AXIS C1410 (AXIS OS 11.11) and
an AXIS C1710 (AXIS OS 12.11) and against the AXIS OS release notes up to 13.0.

## Not part of it

- Live audio: no streaming to or from devices (Audio API `audio/receive.cgi`, RTSP `audio=1`), no paging, no background
  music, no test tone from the client.
- AXIS Audio Manager Pro and Edge: their ACAP, protocol, sites, zones, synchronized playback, central scheduler.
- The Audio relay service API (deprecated since AXIS OS 10.12).
- Network pairing (a speaker, microphone or strobe paired with another device): set up on the device.
- The Audio Multicast Controller (decision 2026-10-29, kept after the C1410 on AXIS OS 11.11 showed the final `v1`).
- Recording clips (removed from the Media clip API in AXIS OS 11.0).
- Presets (clip, display text and light profile started by one trigger): an event rule on the device, built with the
  Action Rule Editor (`oadm.action-rules`), which reads the device's own action templates. The user docs show how. A
  Presets tab may come later on the same `Shared/ActionEngine`.

## Rules that never break

1. **Every change is a task.** Uploads, renames, removals, sound, display and profile changes, tests: tasks of the
   plugin's task part (one task per device, named steps), with the HARD RULE of `CLAUDE.md` "device safety": API ids and
   versions in `CanRun` from the cached `device.Apis`, re-checked with a fresh API list before the first write
   (`DeviceNotCompatibleException`, "Nothing was changed"); all values validated before the first write against what the
   device reported (capabilities, schema, formats, ranges, free slots); a read back after each write.
2. **Choose devices first.** Nothing is read before the user chooses devices (`PageDeviceChoice` first in the toolbar,
   `plugins/README.md` "Choose devices first"); only devices of the user's device group.
3. **Read only on demand, per tab** (decision 2026-10-29). The API list (cached `device.Apis`) decides which tabs a device takes part in;
   each tab reads its own APIs when it is first opened after a device choice, on **Refresh** (the open tab), and after a
   task of the page ends (that device, that tab). No reads in polling; results live in server memory per user session,
   never in the database.
4. **Any device with the API.** Every chosen device takes part in each tab whose API it has (cameras with audio too);
   the others show Not supported in that tab's row (decision 2026-10-29). A tab without any supported device of the
   choice is hidden.
5. **Hazardous settings are never set without a confirmation.** Values the device lists in `getHazardousSettings` (on
   both test speakers only the input power type R12) need an extra confirmation that names the risk; the task carries
   the confirmation and refuses such a value without it.
6. **Tests make sound and light, so they ask first.** Play, Send message, Start (profile), Calibrate, Run speaker test
   and Run health check ask before they run and name the device count; OADM never runs them by itself.
7. **Preinstalled clips and profiles are never changed** (see "Preinstalled clips and profiles").
8. **Scale.** 30,000 devices: reads as a plugin job (`IPluginJobs`) with the shared `DeviceSweep`, at most 16 devices at
   once, 15 s per device and tab, progress row and Cancel; rows streamed in batches; every grid virtualized
   (`RangeObservableCollection`); summaries, filters and search one pass over the rows. A test with 30,000 fake devices.

## APIs

| Tab | API (discovery id) | Endpoint |
| --- | --- | --- |
| Clips | Media clip API (`mediaclip` 1.0), parameters `root.MediaClip` | `/axis-cgi/mediaclip.cgi`, `/axis-cgi/param.cgi` |
| Sound | Audio Device Control (`audio-device-control` 1.0, 2.0), Audio mixer (`audio-mixer` 1.0) | `/axis-cgi/audiodevicecontrol.cgi`, `/axis-cgi/audiomixer.cgi` |
| Display | Speaker display settings v1, Speaker display notification v1 (REST, `/config/discover`) | `/config/rest/speaker-display-settings/v1`, `/config/rest/speaker-display-notification/v1` |
| Siren and light | Siren and light v2 (REST); else the JSON-RPC `siren-and-light` 1.0 | `/config/rest/siren-and-light/v2`, `/axis-cgi/siren_and_light.cgi` |
| Health | Auto speaker test (`speakertest` 1.0, `Properties.API.SpeakerTest.AutomaticTest=yes`); the light's health check | `/vapix/axast` (not `/vapix/services`); see Siren and light |

Only the major versions OADM was tested with; another major version is a different API. The property sets per device
(`getDevicesCapabilities`, `getPluginSchema`, `/functions`, `getCapabilities`) are read from the device, never
hard-coded.

### Clips

- `param.cgi` `root.MediaClip`: `MaxGroups` (slots: 10, 20 or 50 by product), `MaxUploadSize` (MB, 1.5 when missing),
  `M<n>.Name`, `M<n>.Location`, `M<n>.Type`. Neither test speaker has `Format`; without it the formats follow AXIS OS:
  au and wav, MP3 from 10.0, OGG (Vorbis) and Opus from 10.7.
- Upload: `POST mediaclip.cgi?action=upload&media=audio&name=<name>`, one multipart file, answer `uploaded=<index>`; the
  clip keeps its file name (`/etc/audioclips/<file name>`) and takes the lowest free index (tested on the C1410: M0).
  The task uploads under a file name of its own (unique per clip) so an upload never meets an existing file.
- Remove `action=remove&clip=<index>` (answer `removed=<index>`), rename `action=update&clip=<index>&name=<name>`
  (answer `updated=<index>`; the file name stays). Upload, rename and remove tested on the C1410 2026-10-29.
- Play `action=play&clip=<index>&audiodeviceid=<id>&audiooutputid=<id>` (never `audiooutput`: removed in AXIS OS 13.0),
  stop `action=stop` (answers `playing=<index>`, `stopping`). The device ignores an unknown output id and plays anyway
  (C1410, output 9): the task takes the ids from Audio Device Control and checks them itself.

### Sound

- Audio Device Control: devices with inputs and outputs, each with connection types (C1710: internal, line, mic,
  digital), signaling and power types, channels with `gain` (a list of allowed values: output -95 to 0 dB, input -12 to
  75 dB on both) and `mute`. Version 2.0 (C1710) adds the microphone type per input (`transducers`, Axis microphone
  models, AXIS OS 12.11). Gain only through this API: until AXIS OS 12.11.64 `param.cgi` reported other ranges.
- Audio mixer plugins as the device lists them, each with its JSON schema (`getPluginSchema`, labels from the schema's
  descriptions such as "Band 5 [1 kHz]"): `soundProfile` (flat, music, voice, security; C1710), `tenBandGraphicEqualizer`
  (from AXIS OS 12.7) and `tenBandGraphicDspEqualizer` (removed in AXIS OS 13.0; the form shows it only where the new
  one is missing, C1410 on 11.11), `echoCancellation` per device (from AXIS OS 12.6; before it the parameter
  `AudioSource.A0.DynamicProcessing.EchoCancellingEnabled`). Neither test speaker has automatic gain control.
- Writes take only what changes, in the shape of the read (tested 2026-10-29 on both speakers and set back):
  `setDevicesSettings` with `devices[].outputs[].connectionTypes[].signalingTypes[].channels[]` holding only `id` and
  `gain`; `setPluginsSettings` with `devices[].outputs[].plugins[]` holding `id` and only the changed `settings` keys
  (one band of an equalizer, the sound profile `name`); both answer `"data": {}`.
- A sound profile set by AXIS Audio Manager overrides the device's own (AXIS OS 13.0 release notes): OADM does not
  detect Audio Manager sites (decision 2026-10-29; neither test speaker is in one); the user docs say it in one line.

### Display

- Settings (GET the root, PATCH each part): `appearance` (background and font color, language, date, seconds, 24 h
  clock), `brightness` (adaptive, manual 1 to 7, adaptive min and max), `powerSave` (always on, always off, presence
  detection with 1 to 60 minutes, schedule with a schedule id of the device and invert). AXIS OS 13.0 shows a
  notification at full brightness while the signaling LED is on.
- Display settings writes PATCH one part with only the changed keys (`brightness` `manualLevel`, tested on the C1710
  and set back); the device validates ranges itself (400, code 5, "9 is greater than the maximum of 7"), OADM
  validates before as well.
- Notification: `simple` (message up to 1,000 characters, text and background color, size, scroll direction and speed,
  duration as repetitions, time or whole message; answers how long it shows, e.g. `{"duration": 3718}` ms for one pass)
  and `stop`. No stored messages, no queue: the last one wins. Tested on the C1710 2026-10-29.

### Siren and light

- v2 (REST, AXIS OS 12+; C1710): `/functions` (functions such as `AudioLED`, `SignalingLED`, a siren; their patterns,
  colors, intensity, speed, priority, `healthCheckSupported`), `/profiles` (list, add, add function, change function,
  delete; at most 30), start and stop of profiles and patterns, `runHealthCheck`. Tested on the C1710 2026-10-29: add
  answers 201, a name that exists 400 with code 109 ("already exists"); add function 201; only the intensity of a
  profile's function can be changed in place (`PATCH .../functions/<f>/intensity`), the function itself, its colors,
  pattern and the description answer 405 code 9: Edit changes the intensity in place and otherwise deletes the function
  and adds it again (or the profile, for the description), in one task, putting the old one back when the add fails;
  start answers the task ids per function, the function then lists the running task; stop and delete 200.
- JSON-RPC (`siren_and_light.cgi`, before v2; C1410 on 11.11): `getCapabilities`, `getProfiles`, `addProfile`,
  `removeProfile`, `start`, `stop`, `runHealthCheck`, `getLastHealthCheck`. Requests use `apiVersion` 1.0: the C1410
  lists 1.0 and 2.0 but answers `getProfiles` with 2.0 only with an internal error. There is no change of a profile:
  Edit is remove and add in one task (the old profile is added back when the add fails). `addProfile` with a name that
  exists and `removeProfile` of an unknown name both fail with code 1202 "Requested profile is invalid": the task checks
  the names first. Add, list and remove tested on the C1410 2026-10-29.
- One model in OADM (functions, patterns, colors, intensity, speed, priority, duration, profiles), two device clients; a
  device's profiles are changed only through the API they were read with.

### Health

- Auto speaker test: `POST /vapix/axast` with `{"axast:GetSpeakerTestReport":{}}` (also
  `?format=simple&action=axast:GetSpeakerTestReport`), `axast:CalibrateSpeakerTest`, `axast:PerformSpeakerTest`.
  The report: `SpeakerTestStatus` (OK, Failed, Calibrated, Pending, Uncalibrated, DisabledInHardware, InternalError),
  `TestTimestamp`, `CalibrationTimestamp`. A test fails with `DeviceNotCalibrated` until a calibration ran; AXIS OS 11.11
  fixed faulty calibrations, which then show Uncalibrated again. Tested on both speakers 2026-10-29: a test before
  calibration answers HTTP 400 with `FaultSubCode` `axast:DeviceNotCalibrated`; calibrate and test each answer `{}` at
  once, the report shows Pending, then Calibrated (about 10 s), then OK with the test time (about 10 s); the task polls
  the report every 5 s, at most 4 minutes.
- Light health check: only functions with `healthCheckSupported` (v2) or a JSON-RPC device that does not answer
  "Service does not support health check" (code 1204). Neither test speaker has one; strobes such as the D4200-VE do.

### Action templates (for the Action Rule Editor)

SOAP `GetActionTemplates`: the C1710 lists 25, among them `com.axis.action.{fixed,unlimited}.play.audioclip`,
`fixed.stop.audioclip`, `{fixed,unlimited}.speaker.display.notification`, `{fixed,unlimited}.siren_and_light`,
`fixed.siren_and_light_stop_activities`, `{fixed,unlimited}.ledcontrol`, `fixed.autospeakertest.performtest` and
`fixed.mqttpublish`; the C1410 the same without the display ones. The Action Rule Editor's `ActionNames` lacks the
display notification and the speaker test: they are added with the Audio plugin (with their catalog descriptions), so
presets read in plain names.

## Preinstalled clips and profiles

Clips and light profiles that come with AXIS OS are hidden by **Hide preinstalled** (on by default, in the header row of
Clips and of Siren and light, like Hide AXIS apps) and protected: Rename, Remove and Delete are disabled for them with a
tooltip, and the task refuses them (decision 2026-10-29).

- Clips: neither path nor index tells them apart (uploads keep their file name; the 14 factory clips sit at M36 to M49
  of 50), so they are the factory list recorded from the devices, matched by `Location`: `/etc/audioclips/` with
  camera_clicks, abracadabra, logo, ding_dong, ringtone_1, ringtone_2, alarm, internal_message_example,
  sales_offer_example, store_closing_example, store_opening_example, birds_chirping, breaking_waves, wind_chimes (`.mp3`;
  the same on both test speakers).
- Profiles: no flag in either API; the factory list recorded from the devices, matched by name and content: LiveStream,
  Recording, SIP (both test speakers) and Steady, Blink, Alternate (C1710, `SignalingLED`).

## Page

One device choice and **Refresh** for the page, then tabs (`ui:PageTab`): **Clips**, **Sound**, **Display**,
**Siren and light**, **Health**. Each tab is one card with a device grid (address, model, AXIS OS, then that tab's
columns and a status chip) and its actions in the card's header row (actions left, search right). Selection works like
the Applications page: actions apply to the selected devices and the selection stays over a read. Where a tab lists
items of the devices (clips, profiles), it is split like the Applications page: devices left, the items of the selected
devices right, grouped by device when several are selected.

### Clips

- Devices: clips (count), free slots, status. Clips: name, file, device.
- **Upload clips**: choose audio files (like Install applications); each row says whether the device takes it (format,
  size up to `MaxUploadSize`, free slot). A clip with the same name on a device is replaced: the new one is uploaded
  first and the old one removed after; when no slot is free, the old one is removed first and the row says so. Nothing
  is kept on the server beyond the task's upload (`IUploadedFiles`).
- **Rename** and **Remove** (red, asks first) on the selected clips.
- **Play** and **Stop** (Test audio) on the selected clips, on the device's first output (the built-in speaker, device
  and output id from Audio Device Control).

### Sound

- Devices: output gain and mute of the built-in speaker, sound profile, equalizer on or off, echo cancellation,
  status.
- **Settings**: one form for the selected devices, built from the capabilities and the mixer schemas: per input and
  output its connection type, power type and microphone type where offered, gain and mute; the mixer plugins. Fields
  that differ say on how many devices; a field only some devices have says on how many; only changed values are
  written (`setDevicesSettings`, `setPluginsSettings`); hazardous values ask first (rule 5).

### Display

- Only devices with Speaker display settings (C17).
- Devices: power save mode, brightness, language, clock format, status.
- **Settings**: one form for the selected devices like Sound. A power save schedule can be chosen only from the
  schedules all selected devices have.
- **Send message** and **Stop message** (Test audio): text, colors, size, scroll, duration, as a short test.

### Siren and light

- Devices: profiles (count), functions, status. Profiles: name, functions with pattern and colors, device.
- **New profile** and **Edit**: a form from the device's functions (pattern, colors, intensity, speed, priority,
  duration; a siren's sound). With several devices, only what all of them offer can be chosen.
- **Copy to devices**: a profile to other chosen devices; a device missing a function, pattern or color is skipped with
  the reason; a profile with the same name is replaced; a device at its limit is skipped.
- **Delete** (red, asks first). **Start** and **Stop** (Test audio).

### Health

- Devices: speaker test status ("Not calibrated" for Uncalibrated, "Disabled in hardware"), last test and calibration
  time; the light health check result of the last run from OADM in this session (the device keeps no result for v2).
- **Calibrate** on the selected speakers (Test audio, rule 6; decision 2026-10-29): asks first with "Plays test tones.
  Someone on site should listen. Calibrate again after moving the speaker."
- **Run speaker test** (calibrated speakers only; the others are skipped with "Calibrate first") and **Run health
  check** (devices with a light health check), as tasks; the result per device in the grid.

## Permissions

| Permission | What | Default roles |
| --- | --- | --- |
| `oadm.audio:view` ("View audio") | the page and its reads | Administrator, Operator |
| `oadm.audio:test` ("Test audio") | Play, Stop, Send message, Stop message, Start, Calibrate, Run speaker test, Run health check | Administrator, Operator |
| `oadm.audio:change` ("Change audio") | clips, sound, display, profiles | Administrator |

Actions the role lacks are hidden; a role with view only sees the page read only. The task part checks the same
permissions (`task:oadm.audio:*`).

## Tests

Fakes for every API from the recorded C1410 and C1710 answers (`FakeCamera`; both JSON-RPC and REST siren and light,
the `apiVersion` 2.0 error of `getProfiles`), the task steps and their checks, the hazardous confirmation, the
preinstalled lists, the page at 30,000 devices, headless screenshots of each tab and form. Hardware writes only as
`Category=HardwareWrite`, only on request.

Not tested on a device yet (no strobe or siren among the test devices, 2026-10-29): siren functions and their sounds, the
light health check (v2 `runHealthCheck`, JSON-RPC `getLastHealthCheck`), maintenance mode, the other display parts
(appearance, power save). Each is tested on a device before its part is released.
