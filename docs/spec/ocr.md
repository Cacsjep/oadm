# Text recognition

Status: **built** (2026-11-03): recognition, matching, frames, readers, page and the real parts wired; the hardware runs
on the development cameras are open. Developer notes: `plugins/Oadm.Plugins.Ocr/README.md`.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Reads text in the cameras' pictures (signs, container and wagon numbers, displays, meter readings, labels) and hands it
to the event rules. The first vision plugin, an add-on (`docs/spec/add-ons.md`): not in the OADM installers, installed on
demand on the Plugins page.

Core plugin `plugins/Oadm.Plugins.Ocr` (+ `.Client`), id `oadm.ocr`, rail page **Text recognition** (the new rail group **Vision**, after Monitoring, for every vision add-on; SDK 0.10.0),
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
   threads, so OADM stays responsive. The limit counts model calls (a tile, a batch of lines), so an idle server reads the
   tiles of one picture at once; at most as many pictures as workers are in work. Per reader one frame at a time: the
   newest frame wins, older ones are dropped, never queued. At most 50 readers per server, at most 20 of them watching on
   the CPU, 50 on a GPU, and at most 4 watching readers per site (a site node's link carries their streams). At most 8
   page previews per server; a preview with a stream of its own counts against the watching limits; a preview ends 30 s
   after the page's last call and never publishes events ("Page").
5. **GPU when there is one, the CPU always works.** ONNX Runtime and the models come with the add-on; nothing is
   downloaded at run time. A missing or failing GPU falls back to the CPU (one log line); the page says what reads.
6. **Measured accuracy.** Every change to reading keeps or raises the accuracy on the recorded test set ("Accuracy"
   below); a test fails when it drops.

## Readers

A reader is a saved setup: name, camera, video source, resolution, mode, frame rate and up to 8 areas.

- **Areas**: rectangles drawn on the picture, each with a name ("Container number") and an expected text. Stored relative
  to the picture (0 to 1), so a change of resolution keeps them in place. **Text is read only inside areas**, never in
  the whole picture by itself: a reader without an area reads nothing (no stream, no events, no preview reading; the page
  asks "Start by drawing an area"), and the action Read text with it ends "The reader has no area. Draw one on the Text
  recognition page." An area may cover the whole picture.
- **Two ways to read**, both with the same areas:
  - **Fixed place**: a small area around where the text always is (a meter, a sign, the number on a gate lane). The
    best reading: nothing else gets in, and it is fast.
  - **Search**: a large area (up to the whole picture) with a **Format**; the reader finds the text that matches wherever
    it is (an order number on a receipt laid anywhere on a counter, a label on a parcel). Any text over a large area is
    allowed but rarely useful for rules.
- **Mode**:
  - **Watching** (new readers start with it): reads continuously at 1, 2 or 4 frames per second (default 1; a graphics
    card adds 8, 12 and 15) from the camera's MJPEG stream
    (`/axis-cgi/mjpg/video.cgi?fps=<n>&resolution=<w>x<h>&camera=<source>` through `ctx.Vapix` with a streamed answer,
    so device routing and sites work; one stream per reader, reopened after a drop with a growing pause from 1 s up to
    1 min; a stream that sent pictures and then none for 3 s counts as dropped; the camera has 15 s to answer a request or
    open the stream).
  - **On action**: reads one picture when an event rule runs the action **Read text** (below). No stream (the page's
    preview aside, "Page"). Choosing it on the page shows a short message (`ui:MessageWindow`, OK only) once per reader
    and client session: "Reads only on an action", "This reader reads only when an event rule runs the action Read text
    with it." The code's enum is `ReaderMode.OnAction` (JSON `"OnAction"`; named On demand before the first release).
- **Resolution**: default 1920x1080 or the camera's largest below it; small text needs pixels.
- **Single pictures** (the action Read text): `/axis-cgi/jpg/image.cgi` with the same resolution and source.
- No H.264 or H.265: decoding video would need FFmpeg on the server.

## Reading

1. Decode the JPEG (SkiaSharp), cut out each area at full resolution.
2. **Find text**: the PP-OCRv5 detection model per area (mobile on the CPU, server on a graphics card and for Accurate),
   sides a multiple of 32, turn its map into line boxes (threshold 0.3, box score 0.6, box enlarged by 1.5; own C#, no
   OpenCV). An area up to 960 px on its long side is read as it is (a small one scaled up to 64 px, a 16 px margin
   around it); a larger one (Search over the whole picture) in tiles at full resolution: the fewest tiles that cover it
   at up to 960 px each with at least 15 % overlap, all of one size and spread evenly (full HD: 3 x 2 = 6 tiles of
   736 x 608). On the CPU each tile is a model call of its own, on a graphics card all tiles are one call.
3. **At most 30 text boxes** per reading, over all areas together: the boxes with the highest detection scores are
   read, the others dropped, and the reading carries the note "More than 30 texts found. Only the first 30 are read. Draw
   smaller areas." (`Reading.Note`, `ReaderRules.TooManyTexts`), shown on the page below the video. A busy picture (a
   large area over a shelf of labels) never floods the recognition, and stray text far from the value never wins.
4. **Merge boxes**: boxes found twice (in two tiles) and pieces of one line the text finder split (the digit drums of a
   meter) become one line: same direction, about the same height, on one line, overlapping or closer than 0.6 line
   heights.
5. **Straighten** each box: a slanted line is turned level; a line clearly taller than wide (a receipt or label turned
   by 90 degrees) is turned by 90 degrees. The angle model names the lines that may be upside down (probability at
   least 0.3): those are read both ways and the reading the recognition is surer of wins (the angle model alone turned
   coloured print).
6. **Contrast**: a dark or flat line (brightness spanning less than 100 of 255) is stretched so its darkest and brightest
   percent become black and white, at most 4 times; lines with contrast and the text finder's input stay as they are.
7. **Read**: the PP-OCRv5 Latin recognition model per line (height 48 px), CTC decoding with its character list. Lines
   whose characters' mean probability is below 50 % are dropped. A line's confidence is the lowest probability of its
   characters (of its format match when it has one).
8. **Second reading of a fixed-place area**: an area with a format whose lines gave no match, a strip (at least 1.5
   times wider than tall, up to 960 px), is read once more as one line; the text finder can miss a part of a value (a
   dark drum of a meter), the whole strip often still reads. That reading counts only when its format match holds.

There is no Latin server recognition model: PaddleOCR publishes Latin PP-OCRv5 recognition as mobile only, and the
multilingual server model read Latin text worse (no spaces between words). Every device reads with the mobile Latin
model; "the server models" means the server text finder (`plugins/Oadm.Plugins.Ocr/Models/README.md` "Choices").

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
- **Watching** publishes an area's event only when its value changes, and only once the vote of the last 5 frames ("Accuracy") gives the same value in 2 frames
  in a row (no flicker between two readings of one picture). An area that becomes empty publishes once with Text empty.
- Rule action **Read text** (`IActionPlugin`): parameter the reader (On action or Watching). Reads one picture now and
  publishes every area's event, changed or not; a reader without an area ends "The reader has no area. Draw one on the
  Text recognition page." and reads nothing. A reading already running for that reader: the action ends "Already
  reading" and nothing is queued. Loops (a Text read event that runs Read text of the same reader) are held by the rule
  limits (dead time, maximum per minute).

## Page

- Left: the readers (name, camera, mode, last text), **New** and **Delete** (red, with a confirmation).
- The selected reader in two columns (so: readers | live video | settings), no toolbar row:
  - **Left: the camera's live video**, the shared live video component (`ICorePluginClientContext.LiveVideo`,
    docs/spec/live-view.md) of the reader's camera and video source, with the picture only (no channel switch,
    fullscreen, quality switch or controls: nothing sits under the areas or leaves them behind). Over it
    (`ReadingPicture`) the areas (dashed accent, solid when selected, name tag) and the found lines of the latest reading
    (teal boxes with their text), placed on the reader's resolution centered in the video's space: the same letterboxing
    as the video's picture (Stretch Uniform). The video shows the camera's own picture shape; a resolution of another
    shape than the video source (4:3 on a 16:9 camera) draws the areas on the reader's shape. Without the host's live
    video (an older OADM) the overlay sits on the dark video surface. Below the video the mode chip ("Watching · 2 fps",
    "On action" or the reader's problem) and "Last reading 14:32:06" or "Waiting for the first picture."
  - **Right, stacked**: the form (Name, Camera, Video source, Resolution, Mode with Watching and On action, Frames per
    second as a select of the server's choices: 1, 2, 4, on a graphics card also 8, 12, 15; only while Watching), the
    **Areas** panel (header: Areas, **Draw area** as a flat command button with its icon, "2 of 8"; per area its name and
    expected text), the results grid (Area, Text, Confidence right-aligned, Matches format, the time), then the save
    status left of **Save**. The column scrolls; Save stays at the bottom.
- **Areas on the live video**: Draw area, then a drag draws one (at least 2 % of the picture each way; Esc stops). A
  press on an area or on its row in the Areas panel selects it: solid with handles at the corners and edge midpoints. A
  drag inside moves it, a drag on a handle resizes it; both stay inside the picture and keep the drawing minimum
  (`AreaGeometry`); Esc cancels the drag in progress; the arrow keys move it by 1 % (Shift: 5 %). The cursor shows move
  over the body and resize over the handles. Every change marks the reader Not saved like any other edit. Without the
  change right nothing moves.
- **Live preview, no Read now**: while the page shows a reader (the page is visible), the server reads it live as a
  preview in both modes, at the reader's rate (On action: 1 per second), so the results follow while areas are set up.
  The page sends its draft as it is (saved or not, a new reader too): on every change and every 10 s as the keep-alive
  (`startPreview`); the server takes new areas without opening the stream again, a new camera, source, resolution or
  rate opens it again. A preview publishes **no** events: the rule events come only from Watching and from the action.
  - The draft is read as far as it can be (`ReaderRules.PreviewOf`): camera and resolution must be right; areas outside
    the picture are left out, a missing area name reads as "Area 2", a format with a problem reads any text, a frame rate
    not offered the default.
  - A saved watching reader whose draft is unchanged shares the reader's stream (its readings are the preview's); any
    other preview has its own stream through the watching loop with the vote and the events off, and shows each frame's
    own result.
  - Limits: at most 8 previews per server ("At most 8 readers can be shown live at once. Try again when another page
    closes."); a preview's own stream counts with the watching readers against 20 on the CPU (50 on a graphics card) and
    4 per site; beyond: "No live results now: OADM already reads as many cameras as it can." The texts show where the
    results are.
  - A lease: a preview ends 30 s after its last `startPreview` (checked every 5 s), so a closed client never leaves one
    running; the page ends it with `stopPreview` when it hides the reader or the page.
  - Device groups: a preview belongs to the caller's device scope (only that scope's pages hear of it, get or stop it); a
    saved reader of another scope answers like an unknown id; the camera must be in the caller's devices.
  - On open the page shows the saved reader's last reading (`getReading`) until the preview's first one arrives.
- Empty page: "Create a reader to read text from a camera."
- One contract: the page uses the server part's payloads (`Api/`) and the texts of `ReaderRules`, never copies. The
  camera's video sources and sizes (largest first) come from `getCameraOptions` (read only, the caller's devices, kept
  10 min per camera). A save refused by a check answers `{"errors":[{"field","message"}]}` with stable field keys
  (`name`, `camera`, `videoSource`, `resolution`, `mode`, `fps`, `areas`, `areas[i].name|box|format`); the page shows each
  below its field, the watching limits and the frame rate below Mode. Refusals without a field (reader gone, 50 readers)
  stay errors in the host's message.
- The preview's calls (permission "View text recognition"):
  - `startPreview` `{"previewId":"","reader":{...the draft...}}` → `{"previewId":"9f3c...","problem":"..."}` (no
    `problem` while it reads); with a known `previewId` of the caller's scope it keeps that preview alive and takes the
    draft, an unknown or ended one starts a new preview. Refusals: the 8 previews, a camera outside the caller's
    devices, another scope's reader.
  - `stopPreview` `{"previewId":"9f3c..."}` → `{}`.
  - `getPreview` `{"previewId":"9f3c..."}` → `{"reading":{...},"details":[...]}` with the cut-outs, no picture (`{}`
    without a reading).
  - Event `preview` `{"previewId":"9f3c...","reading":{...},"details":[...]}` after each reading of a preview (without
    the cut-outs), to users with all devices and the preview's scope; the page fetches with `getPreview` only its own,
    one call at a time (newest wins).

## Runtime stats

The page shows whether the recognition keeps up, in the readers column below Settings, live every 2 s while the page is
open. Measured on the server, cheap: each reading adds to counters (no allocation per picture); every 2 s a sample takes
the counters (`Readers/ReaderStats`); every figure is the newest sample against the oldest of the last 10 s (shorter right
after the start). All time through the plugin's `TimeProvider`.

- **Traffic light** (`ui:StatusChip` ok, warning, error; `ReaderRules` texts):
  - green "All readers keep up": every watching reader reaches at least 90 % of its frame rate and the load is below 75 %;
  - orange "Busy: 2 readers read slower than set" ("Busy: 1 reader reads slower than set") when a reader reads below
    90 % of its rate; "Busy: recognition is at 82 %" when only the load is 75 % or more;
  - red "Overloaded: lower the frames per second or the number of readers" when a reader reads below 50 % of its rate,
    or the load stayed at 95 % or more for 30 s (every sample from the first at 95 % on).
  - A reader is judged only once it watched for the whole 10 s (a reader that just started is never orange).
- **Recognition load** (bar with %): the share of the last 10 s the recognition was busy: the sum of the time every model
  call held its worker (`IRecognizerLoad.BusyTime`, `Recognition/BusyMeter`) divided by the window times the capacity
  (`IRecognizerLoad.Capacity`). On the CPU the capacity is the workers (`max(1, cores / 2)`), each call counted while it
  holds its slot; on a graphics card it is 1, because the recognizer runs its model calls there one at a time on one
  shared set (the time a call holds the card counts). So 100 % means no more model calls fit: on the CPU every worker,
  on a graphics card the card itself never idles.
- **Numbers**: pictures per second (all readers, previews included); the average reading time (ms per picture, the
  recognizer's call per picture); readers watching "3 of 50" (the limit of the CPU or the graphics card); previews "1 of
  8"; camera streams open (watching readers and previews with a stream of their own), per site "Harbour 2 of 4" when
  sites exist; the OADM server process's CPU ("Server CPU": `Environment.CpuUsage.TotalTime` growth divided by the window
  times the processor count, cross-platform).
- **Per reader**: each watching reader's pictures per second over the last 10 s; below 90 % of its rate its list chip
  turns orange: "Watching · 8 fps (reads 5.1)".
- `getStats` `{}` → the stats (permission "View text recognition"):
  `{"state":"Busy","stateText":"Busy: 1 reader reads slower than set","load":42.5,"picturesPerSecond":12.3,"averageReadMs":85,"watching":3,"maxWatching":50,"previews":1,"maxPreviews":8,"streams":4,"sites":[{"name":"Harbour","streams":2,"max":4}],"serverCpu":23.1,"readers":[{"readerId":"9f3c...","fps":8,"reads":5.1,"slow":true}]}`.
  Each call keeps the live event `stats` (the same JSON every 2 s) on for the caller's scope for 30 s; the page calls it
  on open and every 10 s, so the event runs only while a page is open.
- Device groups: a group user sees the counts, sites and readers of their own group only, plus the server-wide load, CPU,
  pictures per second and reading time (no other scope's reader); each scope's event goes to that scope.

## Permissions

- **View text recognition**: the page, the readers and their results.
- **Change text readers**: New, Delete, Save, moving and drawing areas.
- Running the action is part of the event rules' own permissions.

## Accuracy

The goal is to read better than the commercial readers (VAXTOR Generic Reader as the benchmark): the right value more
often, from worse pictures, and never a wrong value reported as sure.

- **Format-guided reading**: with a format, the recognition's character choice is limited per position (digits where
  `#`, letters where `A`) while decoding, not fixed afterwards; the look-alike table stays as a last step. A check
  digit is checked where the format has one (ISO 6346 for container numbers: a format starting `AAAU` checks its last
  digit; a wrong check digit is no match).
- **Several frames vote** (Watching): per area the last 5 readings are combined character by character, weighted by
  confidence; the value is published once it holds: the vote must give the same value in 2 frames in a row.
  A single bad frame (motion blur, a hand in front) changes nothing.
- **Small text is enlarged** before reading: a line lower than 24 px is scaled up (bicubic) to the model's 48 px; lines
  below 10 px are left out with "Text too small. Draw a smaller area or choose a higher resolution." on the page.
- **Two text finders**: the PP-OCRv5 mobile text finder on the CPU, the server text finder (finds small text the mobile
  one misses, about 6 times the work) on a GPU; one Latin reader everywhere. "Accurate" (the server text finder on the
  CPU, at most 2 such model sets at once) exists in the recognition; the reader setting for it is not built yet.
- **Confidence that means something**: the area's confidence is the lowest character probability of its value, not the
  mean; rules can require "Confidence greater than 90".
- **Test set and score**: `tests/Oadm.Plugins.Ocr.Tests/Fixtures/AccuracySet` holds recorded pictures (development
  cameras, read only; the box on 10.0.0.48; receipts, labels, signs, meters, container numbers printed and filmed; day,
  dim, slanted, blurred) with the expected value of each area. A test reads them all on the CPU, makes each value with
  the readers' own matcher and writes exact-value rate and character error rate per group to the test output; a group
  below its baseline (`Fixtures/AccuracySet/baseline.json`, one value of slack for the CPU maths of other platforms)
  fails the test. Goal: at least 98 % exact values with a format, at least 95 % without.
- **Baseline** (measured 2026-11-03, mobile text finder on the CPU; with a format 88.9 % exact, any text 91.7 %):

  | Group | Values | Exact | Character errors |
  |---|---|---|---|
  | camera-box (the box on 10.0.0.48, fixed place) | 8 | 100 % | 0 % |
  | camera-search (the box, whole picture with a format) | 2 | 100 % | 0 % |
  | container-fixed, container-search | 6, 4 | 100 % | 0 % |
  | meter, receipt-search, sign, no-text | 8, 12, 8, 2 | 100 % | 0 % |
  | hard-meter | 6 | 100 % | 0 % |
  | hard-container | 6 | 83.3 % | 16.7 % |
  | hard-sign | 6 | 66.7 % | 3.2 % |
  | hard-receipt-search | 8 | 37.5 % | 42.5 % |

  The hard receipts read better with the server text finder (62.5 % exact): a graphics card helps there.

## GPU

| Server | Runs on | Needs |
|---|---|---|
| Windows x64 | DirectML (any graphics card: NVIDIA, AMD, Intel) | nothing; DirectML comes with the add-on |
| Linux x64 with NVIDIA | CUDA | the NVIDIA driver and the CUDA 12 and cuDNN 9 runtime on the server (the docs say how); without them the CPU |
| Linux arm64, macOS x64 | CPU | nothing |

- The plugin picks at start: GPU when the provider loads and a test reading succeeds, else the CPU. Settings card of the
  page: "Runs on: NVIDIA GeForce RTX 5070 Ti" or "Runs on: CPU (8 cores)", and **Use the graphics card** (on by default)
  to force the CPU. `getEngine` / `setUseGpu` answer `{runsOn, gpuAvailable, useGpu, fpsChoices}`; `setUseGpu` is
  server-wide: the change right and all devices (`RequiresAllDevices`; the page disables the box for a user with a device
  group), audited. The setting is a plugin setting handed to the recognizer (`IEngineInfo`) at start and on change; the
  next readings run where it says, the graphics card keeps its models until the plugin stops. Without a recognizer: the
  CPU, no graphics card.
- On a GPU the readers' work is batched: the lines of all areas of all readers waiting at that moment go through
  recognition together. Limits on a GPU: frames per second 1, 2, 4, 8, 12 or 15; at most 50 watching readers.
- Speed goals: one Search reader over a full HD picture at 10 frames per second or more on the GPU, at least 1 on the
  CPU; 20 fixed-place readers at 4 frames per second on the GPU.
- Measured at the build (2026-11-03, development PC, RTX 5070 Ti, `RecognitionSpeedTests`, Perf): CPU, Search over full
  HD 1.6 pictures per second, a fixed-place area 20 per second; DirectML, Search about 6 per second, a fixed-place area
  23 per second, 20 fixed-place readers together about 50 pictures per second (2.5 per reader). The CPU goal holds; the
  GPU's Search goal does not yet (the server text finder on 6 tiles), nor 20 readers at 4 per second.

## Packaging and dependencies

- Add-on (`docs/spec/add-ons.md`): one ZIP per server platform with ONNX Runtime (MIT: `Microsoft.ML.OnnxRuntime.DirectML`
  on Windows, the GPU build with the CUDA provider on Linux x64, the CPU build elsewhere), SkiaSharp (MIT; Linux:
  `NativeAssets.Linux.NoDependencies`), the PP-OCRv5 mobile and server detection and Latin recognition models and the
  angle model as ONNX files with the character list (Apache-2.0, in the plugin's notices). The size is measured at the
  build (Linux x64 is the largest because of the CUDA provider). The client part has no native libraries.
- The models are never in git: the build downloads each from its pinned Hugging Face revision into a local cache, checks
  its SHA-256 (another hash fails the build) and copies it into the output's `Models/` (`Models/Models.targets`,
  `Models/README.md` "Download at build", `docs/spec/packaging.md` "Add-on ZIPs"). The plugin folder without `Models/`
  reads nothing: every reading ends "Text recognition is not installed completely. Install the add-on again."
- No OpenCV, no Tesseract, no Python; the CUDA runtime is the server's own, never shipped.

## Parts and contract

One contract between the parts, in `Model/`; the readers (`Readers/ReaderRuntime`) work only against it, the plugin builds
the real parts once per start (`ReaderParts.Real`), the tests hand fakes:

| Contract | Real part | What it does |
|---|---|---|
| `IRecognizerLoad` (optional) | `Recognition/OnnxTextRecognizer` (`BusyMeter` in the CPU slots and the graphics card's calls) | busy time and capacity for the runtime stats |
| `IFrameSource` | `Frames/VapixFrameSource` (the plugin's full device access, `ctx.System.Vapix`) | single pictures and the MJPEG stream of a camera |
| `ITextRecognizer` (+ `IEngineInfo`) | `Recognition/OnnxTextRecognizer` (the plugin folder's `Models/`) | lines per area with box and confidence, the too small areas; what reads, Use the graphics card |
| `ITextMatcher` (+ `IAreaValueMatcher`) | `Matching/TextMatcher` | an area's value, every match, its confidence and box |
| `IPictureCutter` | `Recognition/PictureCutter` (SkiaSharp) | the value's cut-out for the page |
| the vote | `Matching/FrameVote` inside `Readers/AreaWatch` | the last 5 frames and the publish rule (2 in a row, only on change) |

The page uses the server part's payloads (`Api/`) and texts (`ReaderRules`), never copies ("Page").

## Tests

- Text only inside areas: no area, no stream, no reading, no event (watching, the action, the preview, the recognizer
  without decoding); at most 30 text boxes, the strongest, and the reading's note.
- Recognition from recorded camera pictures with known text (`tests/Oadm.Plugins.Ocr.Tests/Fixtures`, recorded read-only
  from the development cameras): lines found, text and order, several lines, a slanted line.
- Format: the syntax, look-alike fixes by position, a number split over two lines, no match.
- Watching: the vote over 5 frames (a single bad frame changes nothing), a value published only when the vote holds in 2 frames and only on change; newest frame wins; a dropped stream reopens
  with the growing pause (`FakeTimeProvider`).
- The action: one reading, "Already reading", the events of every area.
- The preview (`Readers/PreviewTests`): the draft read live without rule events, new areas without a new stream, the
  lease (`FakeTimeProvider`), 8 previews and the watching limit, sharing an unchanged watching reader's stream, device
  groups, what a draft allows.
- The page (`Client/`): the live video follows camera and source, the preview's draft on every change and the keep-alive,
  only the newest reading fetched, moving, resizing and nudging areas held inside the picture, the On action message
  once, new readers watching, the frame rate select; headless screenshots dark and light (the live video as a still
  frame).
- Runtime stats (`Readers/StatsTests`, `FakeTimeProvider`): the 10 s window, the load on the CPU and a graphics card, the
  server CPU, a reader judged only after a whole window, the traffic light's thresholds, red only after 30 s at 95 %, the
  live event only while asked, device groups; the busy time each worker call adds (`WorkerTests`).
- Limits: at most `max(1, cores / 2)` workers held by a gate; 50 readers, 20 watching on the CPU and 50 on a GPU, 4 per site; GPU fallback to the CPU; the accuracy set and its score; device groups; Use the graphics card only with all devices.
- MJPEG parsing from a recorded `multipart/x-mixed-replace` answer (boundary, Content-Length, a part without it).
- End to end with the real parts (`EndToEndTests`): recorded pictures of the coffee box through a watching reader give
  each value once and an empty value once when the box is gone; the action Read text reads a container number with its
  format and the page gets its cut-out.
- The model sources: `Models.targets` and `Models/README.md` name the same files, revisions and SHA-256.
- Speeds (`RecognitionSpeedTests`, Perf, not in `manage test unit`): the numbers of "GPU".

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
| O4 Readers | `Readers/`, `Events/`, `Api/` handlers, its tests | reader store and limits, watching (newest frame wins, the vote over 5 frames, only changes), events, the action Read text, device groups, permissions | fakes of `ITextRecognizer`, `IFrameSource`, `ITextMatcher` |
| O5 Page | `plugins/Oadm.Plugins.Ocr.Client/`, client tests | reader list, camera choice, picture with areas and lines, Draw area, results, Save; headless screenshots | the `Api/` payloads with a fake server |

**Wave 3:** the real parts wired in place of the fakes (done: "Parts and contract"), the models downloaded at build
(done), the add-on ZIP (A4), a measurement of the speed (done: "GPU"), the plugin's `README.md` (done), review and full
suite; open: the hardware runs on the development cameras.
