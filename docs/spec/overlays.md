# Text overlays

Status: **built 2026-11-20**.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The text overlays of many cameras from one dialog, like the Overlay page of a camera's web interface: add the same date,
time or camera name to every selected camera, copy one camera's text overlays to many, or remove them.

Task plugin `plugins/Oadm.Plugins.Overlays` (+ `.Client`), id `oadm.overlays`, context menu group **Video**, **Text
overlays**, free, on by default (switchable, `docs/spec/plugins.md` "Plugins on and off"). Details, steps and the recorded
API in the plugin's README.

## Rules that never break

1. **Text only.** Only text overlays are listed, added and removed. Image overlays, widgets, the streaming indicator,
   scene annotations and privacy masks are never touched; a Replace leaves image overlays where they are.
2. **View area 1.** Every overlay goes to view area 1 (`camera=1`) and only its text overlays are planned and removed;
   other view areas stay as they are. The dialog has no view area field.
3. **Compatibility first.** API discovery id `dynamicoverlay`, major version 1: `CanRun` on the cached list, the task
   on a fresh one before the first request to the API ("The camera does not support text overlays. Nothing was
   changed.").
4. **Everything checked before the first write**: the payload's rules (`OverlayRules`: at most 8 overlays, a text of at
   most 512 characters without tabs or control characters, a size from 1 to 999, a custom place of two numbers from -1
   to 1), then per camera its text length, size range and free slots of view area 1 from `getOverlayCapabilities`.
5. **No stored identities.** Overlays are listed right before the writes and found by their content; an identity is
   used only in the same task, right after the list that returned it.
6. **One request per step**, removals before additions, a refusal after an earlier write says what was already done.
   Errors in an HTTP 200 answer are failures with a plain message.
7. **Never the deprecated parameters** `reference` and `scrollSpeed`.
8. **The same twice changes nothing**: an overlay the camera already shows exactly like this is not added again.
9. **Scale**: the dialog reads one camera when it opens and one on Read from a camera, never one per selected device;
   it counts the compatible devices in one pass (100,000 devices, `ScaleTests`).

## What happens on each camera

- **Add**: the list goes next to the camera's overlays.
- **Replace text overlays**: the camera ends up with exactly this list of text overlays in view area 1; an empty list
  removes all of them. The dialog shows a warning under the choice.
- **Remove matching**: removes the text overlays whose text equals one in the list; only the text counts, the dialog
  hides the other fields.

## Dialog

A list of at most 8 text overlays: Text (several lines; Date, Time and More values insert a modifier at the caret:
day of the week, date and time, camera name, IP address, MAC address, frame rate, bitrate, dynamic text 1 to 16),
Position (Top left, Top, Top right, Bottom left, Bottom, Bottom right; Custom position only for an overlay read from a
camera with its own place, which is kept when copied), Size (optional, the camera's default when empty), Text colour,
Background, Outline (Black, White, Red, Transparent, Semi-transparent; new overlays white on black). **Read from a
camera** fills the list from one camera the user may see (the read-only query `listOverlays`). The selection's summary
"N devices: M compatible, K not supported" is the card's description. Every error sits below its field; the list's own
error ("Add an overlay.") below the list.

## AI agents

The payload is the task input (`PayloadType`, no secrets); `ValidatePayload` holds the dialog's rules at their fields;
the plan skips devices that cannot run it and warns what Replace removes. The read is `oadm_task_query` with
`listOverlays`.

## Facts from the cameras

Recorded on AXIS OS 12.11.147 (P3265-V, M3215-LVE) and 10.12.363 (M3206-LVE), 2026-11-20:

- **The slot limit is per view area.** `numAvailableSlots` is the total of one view area and does not drop after an add.
  On the M3215-LVE (8 view areas) view area 1 took 8 text overlays, the ninth was refused with 300, and view area 2
  still took one. The free slots are counted from the list.
- In the JSON body a real line break makes a new line; "%0A" is read as the modifier `%A` (seen on a snapshot).
- `remove` takes only the identity; `list` needs a `params` object.
- **Open: whether overlays survive a restart.** The VAPIX documentation says only that "Overlay IDs may change after a
  reboot"; no camera was restarted for this. The plugin never relies on either: it lists before every write.
