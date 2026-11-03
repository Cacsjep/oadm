# Text recognition

Status: **spec, not built** (2026-11-03).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Reads text in the cameras' pictures (signs, container and wagon numbers, displays, meter readings, labels) and hands it
to the event rules. The first vision plugin, an add-on (`docs/spec/add-ons.md`): not in the OADM installers, installed on
demand on the Plugins page.

Core plugin `plugins/Oadm.Plugins.Ocr` (+ `.Client`), id `oadm.ocr`, rail page **Text recognition** (the new rail group **Vision**, after Monitoring, for every vision add-on; SDK 0.11.0),
free (Apache-2.0), **off by default**. License plates are not its job: AXIS License Plate Verifier on the camera does
that better (the docs say so).

## Rules that never break

1. **Read only.** The plugin only receives pictures from the cameras; it never changes a setting on a device.
2. **Pictures never stored.** Frames live in server memory while they are read; the last result of each reader (text,
   boxes, the frame for the page) is kept in memory only. The texts reach the event log as event fields, never the frames.
3. **Choose the camera first.** A reader starts from one camera (`DevicePickerWindow.ShowOneAsync`) and one of its video
   sources; nothing streams before that. Only cameras of the user's device group; readers are owned by the device scope
   that made them (`docs/spec/device-scope.md`).
4. **Bounded load.** Recognition runs on the CPU in at most `max(1, cores / 2)` workers at once, each with at most 2
   threads, so OADM stays responsive. Per reader one frame at a time: the newest frame wins, older ones are dropped,
   never queued. At most 50 readers per server, at most 20 of them watching.
5. **No GPU, nothing downloaded at run time.** ONNX Runtime (CPU) and the models come with the add-on.

## Readers

A reader is a saved setup: name, camera, video source, resolution, mode, frame rate and up to 8 areas.

- **Areas**: rectangles drawn on the picture, each with a name ("Container number") and an expected text. Stored relative
  to the picture (0 to 1), so a change of resolution keeps them in place. No area: the whole picture is one area.
- **Two ways to read**, both with the same areas:
  - **Fixed place**: a small area around where the text always is (a meter, a sign, the number on a gate lane). The
    best reading: nothing else gets in, and it is fast.
  - **Search**: a large area or the whole picture with a **Format**; the reader finds the text that matches wherever
    it is (an order number on a receipt laid anywhere on a counter, a label on a parcel). Any text over a large area is
    allowed but rarely useful for rules.
- **Mode**:
  - **On demand**: reads one picture when an event rule runs the action **Read text** (below). No stream.
  - **Watching**: reads continuously at 1, 2 or 4 frames per second (default 1) from the camera's MJPEG stream
    (`/axis-cgi/mjpg/video.cgi?fps=<n>&resolution=<w>x<h>&camera=<source>` through `ctx.Vapix` with a streamed answer,
    so device routing and sites work; one stream per reader, reopened after a drop with a growing pause up to 1 min).
- **Resolution**: default 1920x1080 or the camera's largest below it; small text needs pixels.
- **Single pictures** (On demand, the page's Read now): `/axis-cgi/jpg/image.cgi` with the same resolution and source.
- No H.264 or H.265: decoding video would need FFmpeg on the server.

## Reading

1. Decode the JPEG (SkiaSharp), cut out each area at full resolution.
2. **Find text**: the PP-OCRv5 mobile detection model per area, sides a multiple of 32, turn its map into line boxes
   (threshold, contours, box enlarged; own C#, no OpenCV). An area up to 960 px on its long side is read as it is; a
   larger one (Search over the whole picture) in overlapping tiles of 960 px at full resolution (overlap 15 %, boxes
   found twice merged), so small text in a large picture is not lost. Tiles cost time: a 1920x1080 picture is 6 tiles.
3. **Straighten** each box: a slanted line is turned level; a line clearly taller than wide (a receipt or label turned
   by 90 degrees) is turned by 90 degrees; the angle model then decides whether the line is upside down and turns it
   by 180 degrees.
4. **Read**: the PP-OCRv5 Latin recognition model per line (height 48 px), CTC decoding with its character list, a
   confidence per line (mean of the characters' probabilities). Lines below 50 % are dropped.

Languages: Latin script (English, German with umlauts, French, ...) and digits. Other scripts need another recognition
model (later, as a choice per reader).

### Several lines

An area can hold several lines. Its lines are ordered top to bottom, left to right; each keeps its box and confidence
(shown on the page, one line per row). For the events the lines are joined with a space, a line ending in "-" joined
without one ("DECAF-" and "FEINATO" give "DECAF-FEINATO"), so a rule's "contains" finds a word on any line; at most 500
characters.

- **One area per value**, never line numbers: lines are found anew in each picture and their count changes with light
  and focus, so "line 2" would point at another line. The page's empty area list says "Draw one area around each text
  you want to read."
- An area full of text (a sign, a label) is read whole and fits rules that look for a word (contains). Reading
  documents or long paragraphs is not the purpose.
- Vertical text with one character per line (container doors) is not read in this version.

### Expected text

Per area: **Any text** (default) or **Format**.

- Format: `#` a digit, `A` a letter, `*` a letter or digit; every other character must appear as written, except spaces
  and dashes, which may be missing in the text. Example: container number `AAAU ###### #`, wagon `## ## #### ###-#`,
  counter `#####`.
- With a format the reader looks for matches in each line on its own, then in neighbouring lines joined (for a number
  split over two lines). The match fixes look-alike characters by position: where a digit is expected O becomes 0, I
  and L 1, S 5, B 8, Z 2, G 6; where a letter is expected the reverse.
- Several matches (the same order number on two receipts, two parcels): the area's value is the match read with the
  highest confidence, on a tie the larger text; the event field All matches lists every different match, at most 10.
- The page shows the matched text's cut-out picture next to its value (in memory only, like the frames).
- No match: the area's value is empty and **Matches format** is No; the raw text stays in **Text read**.
- The page's field hint: "# digit, A letter, * letter or digit".

## Events and the rule action

- Event **Text read** per reader (folder Text recognition / reader name), stateless, one event per area and reading,
  fields: Area (choice of the reader's areas), Text (the value: the match with a format, else all lines), Text read (all
  lines, raw), All matches (every different match of the format, separated by a space; empty without a format),
  Number (the value as a number when it is one, "." or "," as the decimal sign, e.g. a meter reading;
  empty otherwise), Confidence (number, 0 to 100), Matches format (Yes / No). Conditions check them with the field checks
  of `docs/spec/event-rules.md` (equals, contains, starts with; greater or less than for Number and Confidence).
- **Watching** publishes an area's event only when its value changes, and only once the same value was read in 2 frames
  in a row (no flicker between two readings of one picture). An area that becomes empty publishes once with Text empty.
- Rule action **Read text** (`IActionPlugin`): parameter the reader (On demand or Watching). Reads one picture now and
  publishes every area's event, changed or not. A reading already running for that reader: the action ends "Already
  reading" and nothing is queued. Loops (a Text read event that runs Read text of the same reader) are held by the rule
  limits (dead time, maximum per minute).

## Page

- Left: the readers (name, camera, mode, last text), **New** and **Delete** (red, with a confirmation).
- Right, for the selected reader: the camera choice, video source, resolution, mode, frame rate; the picture of the last
  reading with the areas and the found lines drawn on it; **Draw area** and per area its name and expected text;
  **Read now** (one picture); **Save**.
- While a reader is shown and Watching, the page shows its readings live (latest frame with boxes, at most the reader's
  rate). An On demand reader shows the last reading and Read now.
- Below the picture the results: Area, Text, Confidence (right-aligned), Matches format, the time.
- Empty page: "Create a reader to read text from a camera."

## Permissions

- **View text recognition**: the page, the readers and their results.
- **Change text readers**: New, Delete, Save, Read now.
- Running the action is part of the event rules' own permissions.

## Packaging and dependencies

- Add-on (`docs/spec/add-ons.md`): one ZIP per server platform with `Microsoft.ML.OnnxRuntime` (CPU, MIT) native library
  for that platform, SkiaSharp (MIT; Linux: `NativeAssets.Linux.NoDependencies`), the PP-OCRv5 mobile detection, Latin
  recognition and angle models as ONNX files and the character list (Apache-2.0, in the plugin's notices). About 30 to
  50 MB per platform. The client part has no native libraries.
- No OpenCV, no Tesseract, no Python, no GPU libraries.
- Expected speed (to measure at the build): 100 to 500 ms per area on one worker; one watching reader with 2 areas at
  1 frame per second is a small part of one core.

## Tests

- Recognition from recorded camera pictures with known text (`tests/Oadm.Plugins.Ocr.Tests/Fixtures`, recorded read-only
  from the development cameras): lines found, text and order, several lines, a slanted line.
- Format: the syntax, look-alike fixes by position, a number split over two lines, no match.
- Watching: a value published only after 2 equal frames and only on change; newest frame wins; a dropped stream reopens
  with the growing pause (`FakeTimeProvider`).
- The action: one reading, "Already reading", the events of every area.
- Limits: at most `max(1, cores / 2)` workers held by a gate; 50 readers, 20 watching; device groups.
- MJPEG parsing from a recorded `multipart/x-mixed-replace` answer (boundary, Content-Length, a part without it).

## Build plan (parallel work)

Same waves as `docs/spec/add-ons.md` "Build plan". Each package owns its folder under `plugins/Oadm.Plugins.Ocr(.Client)`
and `tests/Oadm.Plugins.Ocr.Tests`; nobody else changes it.

**Wave 1, skeleton (with the add-on contracts, same agent):** the projects from the template (`manage new`) in
`Oadm.sln`, `plugin.json` (add-on, off by default, `minSdkVersion`), and the shared types in `Model/`: `Reader`, `Area`,
`ExpectedText`, `TextLine` (text, box, confidence), `AreaResult`, `Reading`; the interfaces `ITextRecognizer`
(`ReadAsync(jpeg, areas, ct)` to lines per area), `IFrameSource` (`SingleAsync`, `WatchAsync(fps)` as an
`IAsyncEnumerable` of JPEG frames) and `ITextMatcher`; the `InvokeAsync` methods and their JSON payloads (`Api/`), the
event and action ids, the permissions.

**Wave 2, in parallel:**

| Package | Owns | Builds | Against |
|---|---|---|---|
| O1 Recognition | `Recognition/`, `Models/`, its tests and fixtures | ONNX Runtime sessions, JPEG decode and crop (SkiaSharp), detection pre and post (contours in C#), straightening, recognition with CTC, the worker gate | `ITextRecognizer`; fixtures recorded read-only from the development cameras |
| O2 Matching | `Matching/`, its tests | line order, several lines, the format syntax, look-alike fixes, a number over two lines | `ITextMatcher` (pure C#, no I/O) |
| O3 Frames | `Frames/`, its tests | MJPEG `multipart/x-mixed-replace` parser, the stream through `ctx.Vapix` (streamed answer), reopening with the growing pause, single picture | `IFrameSource`; a recorded MJPEG answer |
| O4 Readers | `Readers/`, `Events/`, `Api/` handlers, its tests | reader store and limits, watching (newest frame wins, 2 equal frames, only changes), events, the action Read text, device groups, permissions | fakes of `ITextRecognizer`, `IFrameSource`, `ITextMatcher` |
| O5 Page | `plugins/Oadm.Plugins.Ocr.Client/`, client tests | reader list, camera choice, picture with areas and lines, Draw area, results, Read now, Save; headless screenshots | the `Api/` payloads with a fake server |

**Wave 3:** the real parts wired in place of the fakes, the add-on ZIP (A4), a measurement of the speed on the
development cameras (the numbers go into "Packaging and dependencies"), the plugin's `README.md`, review and full suite.
