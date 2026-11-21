# Text overlays (`oadm.overlays`)

Package of four task plugins with their dialogs (`.Client`), on by default (an administrator can turn it off), in the
device context menu group **Overlays** (`TaskGroups.Overlays`, icon `text`), no toolbar button:

| Entry | Task id | Dialog |
|---|---|---|
| Add overlays | `oadm.overlays.add` | `AddOverlaysDialog` |
| Replace overlays | `oadm.overlays.replace` | `ReplaceOverlaysDialog` |
| Remove matching overlays | `oadm.overlays.remove-matching` | `RemoveMatchingOverlaysDialog` |
| Remove all overlays (last, after a separator: `IsDestructive`) | `oadm.overlays.remove-all` | `RemoveAllOverlaysDialog`: a confirmation only |

All four derive from `OverlaysTaskPlugin`, which fixes the `OverlaysMode`; one task per camera. Text only: image
overlays, widgets, privacy masks and scene annotations are never touched. The spec is
[docs/spec/overlays.md](../../docs/spec/overlays.md).

| Part | Project | Assembly |
|---|---|---|
| Server (task + read-only query) | `plugins/Oadm.Plugins.Overlays` | `Oadm.Plugins.Overlays.Server.dll` |
| Client (Avalonia dialog) | `plugins/Oadm.Plugins.Overlays.Client` | `Oadm.Plugins.Overlays.Client.dll` |
| Tests | `tests/Oadm.Plugins.Overlays.Tests` | |

The client references the server project for the payload, the rules and the names (`OverlaysContract.cs`,
`OverlayRules.cs`).

## Device API

Dynamic overlay API, `POST /axis-cgi/dynamicoverlay/dynamicoverlay.cgi`, JSON, Operator (`DynamicOverlayApi`).

- **Detection: API discovery id `dynamicoverlay`**, major version 1 (1.0 or later). The VAPIX page names only
  `Properties.DynamicOverlay.DynamicOverlay=yes`, but every camera recorded lists the id: AXIS P3265-V and M3215-LVE on
  AXIS OS 12.11.147 `dynamicoverlay` 1.8, AXIS M3206-LVE on 10.12.363 1.7 (with `Properties.DynamicOverlay.Version` the
  same). `CanRun` checks the cached list; the task and the query check a fresh one.
- Requests always send `"apiVersion":"1.0"` (every camera supports it; they answer with their newest, 1.10 or 1.7) and
  always a `params` object: `list` without one is refused with 102 "Mandatory parameter(s) missing".
- `getOverlayCapabilities`: `maxTextLength` 512, `minFontSize` 16, `maxFontSize` 200 (48 on the M3206-LVE),
  `numAvailableSlots` 8 (4), `slotsPerTextOverlay` 1, `slotsPerImageOverlay` 1. **`numAvailableSlots` is the total of one
  view area**: it stays 8 after an add. The misspelled `numberAvailbleSlots` of the documentation's example is read too.
- `list` returns every view area: `textOverlays[]` (`identity`, `camera`, `text`, `position` a name or `[x, y]`, the
  three colours, `fontSize`, ...), `imageOverlays[]`, `imageFiles[]`. Only view area 1 is planned and written.
- `addText` (`camera` 1, `text`, `position`, `textColor`, `textBGColor`, `textOLColor`, `fontSize` only when set) answers
  `{camera, identity}`. The deprecated `reference` and `scrollSpeed` are never sent, nor `rotation`.
- `remove` takes **only** `identity`: with `camera` as well it answers 103 "Unknown parameter supplied".
- **Line breaks**: in the JSON body a real line break makes a new line on the video. "%0A" (the documentation's form for
  the URL form) is read as the modifier `%A` and shows the day of the week. The dialog and the task send `\n`
  (`OverlayText.Normalize` turns CR LF into LF); a text read from a camera keeps what the camera has.
- Errors come back as HTTP 200 with `error.code` and `error.message` (`ParseAnswer`): 300 "The camera has no free overlay
  slot.", 302 "The overlay is no longer on the camera.", 303/304/306-309 "The camera does not accept this position / text
  / colour / size.", every other code "The camera refused the request: <message> (error <code>)."
- The modifiers the dialog offers (%F, %X, %a, %c, #n, #i, #M, #R, #b, #D1-#D16) are all in the recorded
  `overlaymodifiers.cgi getOverlayModifiers` of AXIS OS 12.11 (`ParserTests`).

Verified on 2026-11-20 with own test overlays (`HardwareWriteTests`): 10.0.0.48 (P3265-V, 12.11.147, Add and Remove
matching next to its own overlay), 10.0.0.202 (M3215-LVE, 12.11.147) and 10.0.0.200 (M3206-LVE, 10.12.363) with Replace;
each camera's list was the same afterwards. 10.0.0.12 (AXIS OS 11.9.65) did not answer.

## Task

Steps: **Check the overlay support** (fresh API list; without the API `DeviceNotCompatibleException` "The camera does not
support text overlays. Nothing was changed."), **Read the overlay limits**, **Read the overlays**, **Check the overlays**
(`OverlayPlanner.Build`: every limit before the first write), then one step per request: **Add the overlay "x"**
and **Remove the overlay "x"** (`OverlayPlan.AddFirst`: additions first while the free slots take them, so a refused add
leaves the old overlays in place; else removals first, they free slots), then **Verify the overlays** (list again; a difference is a
warning). Nothing to change: the writes are left out and Verify is skipped "Nothing was changed.".

- **No stored identities.** The overlays are listed right before the writes and matched by content (text, position,
  colours, and the size when one was asked for); identities can change after a restart.
- **Add overlays**: an overlay the camera already shows exactly like this is skipped ("2 already on the camera"), so a
  second run changes nothing.
- **Replace overlays**: the text overlays of view area 1 the list does not have are removed, the missing ones added; at
  least one overlay. Image overlays and other view areas stay.
- **Remove matching overlays**: removes the text overlays of view area 1 whose text equals one of the list (only the text
  counts); a text no overlay has is named in the check step, no warning.
- **Remove all overlays**: removes every text overlay of view area 1. Image overlays and other view areas stay.
- **Checks before the first write**: text length against `maxTextLength`, size against `minFontSize`..`maxFontSize`,
  free slots of view area 1 = `numAvailableSlots` - text overlays staying x `slotsPerTextOverlay` - image overlays x
  `slotsPerImageOverlay`; "The camera has room for 1 more overlay in view area 1, not 2. Nothing was changed."
- A refused write after an earlier one says what was done: "... Already done: 1 removed, 0 added."
- Task names, with the whole text on one line (line breaks as spaces), shortened only past 120 characters: "Add text
  overlay "Lobby floor 1"", "Add 2 text overlays", "Set text overlay ...", "Set 3 text overlays", "Remove text overlay
  ...", "Remove 2 text overlays", "Remove all text overlays".
- Payloads (JSON camelCase): Add, Replace and Remove matching take `OverlayListPayload` (`overlays[]`: `text`, `position`,
  `customPosition` [x, y] for `custom`, `textColor`, `backgroundColor`, `outlineColor`, `fontSize`; 1 to 8), Remove all
  takes `RemoveAllPayload` (`{}`). The task adds its mode (`OverlaysTaskPlugin.Read` gives the `OverlaysPayload` the
  planner and runner use). `ValidatePayload` holds the dialog's rules at their fields; the agents' input is each task's
  schema (no secrets).
- Permissions: the default `task:<task id>` of each task.

## Dialog

`OverlaysWindow` over `OverlaysDialogViewModel`, opened by Add, Replace and Remove matching overlays with the mode fixed;
1180 px wide, as high as its content (8 rows scroll). The title is the entry's name, the main button names the action.
**Read from a camera** is a flat command button in the title bar left of the X (`DialogTitleBar.Actions`): the
one-device picker over the user's devices (unsupported ones greyed with the reason), then the query `listOverlays`
replaces the list with the camera's text overlays of view area 1, at most 8. Above the table, only when needed: "7 of
250 cameras are not supported." (`OverlayTexts.Unsupported`), Replace's warning chip, the read result.

The overlays are a table: the column headers once (`TextBlock.fieldLabel`; the Size header's tooltip gives the range of
the camera read), one line per overlay, each input a label-less `ui:FormField` (`wide inline`), so its error shows
directly below it and only that row grows:

- **Text** (330 px column): one line high. `AcceptsReturn` stays on with the height of one line: a text read from a
  camera keeps its line breaks and Enter adds one; the box scrolls to the caret's line, a long text shows the theme's
  thin 4 px scroll bar below the text. The icon-only button next to it (tooltip "Insert a value") opens one menu: Date
  (%F), Time (%X), Day of the week, Date and time, Camera name, IP address, MAC address, Frame rate, Bitrate, Dynamic
  text > 1 to 16. The code goes at the end of the text ("Camera" + Camera name = "Camera#n").
- **Position** (the six places, **Custom position** only for an overlay read with one), **Size** (placeholder Default),
  **Text colour** (Black, White, Red), **Background**, **Outline** (Black, White, Red, Transparent, Semi-transparent; new
  overlays white on black). The columns are wide enough for "Semi-transparent" and "Custom position". Remove matching shows the Text
  column only (and checks only the text); its footer button is red.
- The red remove icon at the row's end; **Add overlay** below the rows, disabled at 8 with "At most 8 overlays." as tooltip.

Remove all overlays has no form: `MessageWindow.ConfirmAsync` "Remove all text overlays of 243 cameras? Image overlays
stay." (and the unsupported line), red Remove; it returns `{}`.

On open the dialog reads the limits of the first compatible selected camera (one query, whatever the selection) for the
text length and the size range; without them the payload's widest range applies and each camera checks its own.

## Tests

`tests/Oadm.Plugins.Overlays.Tests`: parsers from the recorded fixtures (`Fixtures/`, one docs-written file
`docs-getOverlayCapabilities-example.json`), the planner, the task against `OverlayCamera` (a fake with the recorded
behaviour), the agents' schema and rules, the view model, the 100,000-device dialog (`ScaleTests`, one query), headless
screenshots (`HeadlessTests`, `OADM_SCREENSHOT_DIR`, `OADM_TEST_APPEARANCE=Light`) and the opt-in `HardwareWriteTests`
(`Category=HardwareWrite`, `OADM_OVERLAYS_WRITE=1` and `OADM_DEV_CAMERAS`; only overlays with its own marker, Replace only
on a camera without own text overlays in view area 1, its overlays removed in `finally`, the list compared afterwards).
