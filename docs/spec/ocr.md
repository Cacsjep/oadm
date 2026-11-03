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
2. **Pictures never stored.** Frames live in server memory while they are read (with a coarse brightness print per area
   for unchanged areas, "Reading" step 8); the last result of each reader (text,
   boxes, the frame for the page) is kept in memory only. The texts reach the event log as event fields, never the frames.
3. **Choose the camera first.** A reader starts from one camera (`DevicePickerWindow.ShowOneAsync`) and one of its video
   sources; nothing streams before that. Only cameras of the user's device group; readers are owned by the device scope
   that made them (`docs/spec/device-scope.md`).
4. **Bounded load.** Recognition runs on the CPU in at most `max(1, cores / 2)` workers at once, each with at most 2
   threads, so OADM stays responsive. The limit counts model calls (a tile, a batch of lines), so an idle server reads the
   tiles of one picture at once; at most as many pictures as workers are in work. Per reader one frame at a time: the
   newest frame wins, older ones are dropped, never queued. At most 50 readers per server; watching readers by the
   server's cores, 4 per worker, at least 8 and at most 50 ("Limits"); at most 4 watching readers per site (a site node's
   link carries their streams; they take none of the site's large transfer slots). At most 8 page previews per server
   and 2 per user; a preview with a stream of its own counts against the watching limits after the saved readers; a
   preview ends 30 s after the page's last call and never publishes events ("Page").
5. **GPU when there is one, the CPU always works.** ONNX Runtime and the models come with the add-on; nothing is
   downloaded at run time. A missing or failing GPU falls back to the CPU (one log line); the page says what reads.
6. **Measured accuracy.** Every change to reading keeps or raises the accuracy on the recorded test set ("Accuracy"
   below); a test fails when it drops.

## Readers

A reader is a saved setup: name, camera, video source, resolution, mode, frame rate and up to 8 areas.

- **Camera**: chosen once, when the reader is created (New, then the camera). A saved reader keeps it: a save with
  another camera is refused (`camera`: "A saved reader keeps its camera. Create a new reader for another camera."); the
  page offers no camera change.

- **Areas**: rectangles drawn on the picture, each with a name and an expected text. The page names a new area "Area n"
  with the lowest free number (Area 2 again after Area 2 was deleted; the others keep theirs, rules name them); Rename
  gives it any other name. The server checks names as before (not empty, at most 64 characters, unique in the reader),
  no pattern. Stored relative
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
  - **Watching** (new readers start with it): reads continuously at 1, 2, 4, 8, 12 or 15 frames per second (default 1)
    from the camera's MJPEG stream
    (`/axis-cgi/mjpg/video.cgi?fps=<n>&resolution=<w>x<h>&camera=<source>` through `ctx.Vapix` with a streamed answer
    marked `VapixRequestOptions.LiveStream`, so device routing and sites work and the stream holds none of a site's large
    transfer slots, `docs/spec/sites.md`; one stream per reader, reopened after a drop with a growing pause from 1 s up to
    1 min; a stream that sent pictures and then none for 3 s counts as dropped; the camera has 15 s to answer a request or
    open the stream). Why a stream gives no pictures is the reader's problem in the list (and the preview's): the camera's
    refusal ("Unauthorized - HTTP 401 (check the credentials)"), "The camera does not offer this resolution. Choose another
    one." or "... this video source ..." (from the camera's error text), "The camera sends no pictures." (not opened in
    15 s); it clears with the next picture.
  - A rename of the reader or of its areas goes on in the running loop (the vote and the published values stay); a new
    camera place, source, resolution, rate or area place or format starts it again.
  - **On action**: reads one picture when an event rule runs the action **Read text** (below). No stream (the page's
    preview aside, "Page"). Choosing it on the page shows a short message (`ui:MessageWindow`, OK only) once per reader
    and client session: "Reads only on an action", "This reader reads only when an event rule runs the action Read text
    with it." The code's enum is `ReaderMode.OnAction` (JSON `"OnAction"`; named On demand before the first release).
- **Resolution**: default 1920x1080 or the camera's largest below it; small text needs pixels.
- **Single pictures** (the action Read text): `/axis-cgi/jpg/image.cgi` with the same resolution and source.
- No H.264 or H.265: decoding video would need FFmpeg on the server.

## Reading

1. Decode the JPEG (SkiaSharp), only the part the areas need (each area with a margin of 16 px plus its shorter side:
   rows above skipped, rows below never read, columns outside cut by the JPEG decoder; full HD with one small area 2.5
   instead of 16 ms), and cut out each area at full resolution. The page's cut-out of a value is decoded the same way.
2. **Find text**: the PP-OCRv5 detection model per area (mobile on the CPU, server for Search on a graphics card and for
   Accurate),
   sides a multiple of 32, turn its map into line boxes (threshold 0.3, box score 0.6, box enlarged by 1.5; own C#, no
   OpenCV). An area up to 960 px on its long side is read as it is (a small one scaled up to 64 px, a 16 px margin
   around it); a larger one (Search over the whole picture) in tiles at full resolution: the fewest tiles that cover it
   at up to 960 px each with at least 15 % overlap, all of one size and spread evenly (full HD: 3 x 2 = 6 tiles of
   736 x 608). On the CPU each tile is a model call of its own; with a graphics card an area read in tiles goes to the
   card, its tiles in calls of one size within the card's memory cap ("GPU").
3. **At most 30 text boxes** per reading, over all areas together: the boxes with the highest detection scores are
   read, the others dropped, and the reading carries the note "More than 30 texts found. Only the first 30 are read. Draw
   smaller areas." (`Reading.Note`, `ReaderRules.TooManyTexts`), shown on the page below the video. A busy picture (a
   large area over a shelf of labels) never floods the recognition, and stray text far from the value never wins.
4. **Merge boxes**: boxes found twice (in two tiles) and pieces of one line the text finder split (the digit drums of a
   meter) become one line: same direction, about the same height, on one line, overlapping or closer than 0.6 line
   heights.
5. **Straighten** each box: a slanted line is turned level; a line clearly taller than wide (a receipt or label turned
   by 90 degrees) is turned by 90 degrees. Each line is read first as it is; the angle model looks only at the lines
   not read surely upright (fewer than 3 characters, a character below 90 %, or fewer than 2 letters that read as
   nothing upside down, so lines of digits always): it names the lines that may be upside down (probability at least
   0.3), those are read turned too and the reading the recognition is surer of wins (the angle model alone turned
   coloured print).
6. **Contrast**: a dark or flat line (brightness spanning less than 100 of 255) is stretched so its darkest and brightest
   percent become black and white, at most 4 times; lines with contrast and the text finder's input stay as they are.
7. **Read**: the PP-OCRv5 Latin recognition model per line (height 48 px), CTC decoding with its character list. Lines
   whose characters' mean probability is below 50 % are dropped. A line's confidence is the lowest probability of its
   characters (of its format match when it has one).
8. **Second reading of a fixed-place area**: an area with a format whose lines gave no match, a strip (at least 1.5
   times wider than tall, up to 960 px), is read once more as one line; the text finder can miss a part of a value (a
   dark drum of a meter), the whole strip often still reads. That reading counts only when its format match holds.
9. **Unchanged areas** (watching readers and previews: `RecognitionOptions.ReuseUnchanged` with the reader's id or the
   preview's own id as `Source`; never the action's single reading): an area whose pixels have barely changed since its last real reading gives that reading
   again without running the models. The print is the mean brightness of cells of about 8 x 8 px (at most 48 x 48
   cells); unchanged means no cell moved by more than 6 of 255 levels and the mean change is at most 1.5 (a changed
   digit moves its cells by tens of levels, compression noise by about one). Compared with the print of the last real
   reading, so a slow drift ends in a new reading too; after 10 uses in a row the area is read anew anyway. Only the
   changed areas of a picture are read. A reading cut at 30 text boxes is never kept (the next picture reads again and
   carries the note). Prints and readings live in memory only (at most 512 areas). A picture whose areas were all
   unchanged runs no model and counts in the runtime stats as "Unchanged pictures per second", not as a picture read.

Model calls reuse their buffers (rented, the input pinned for ONNX Runtime, the output read where it lies). On the CPU
the lines go to the reader in pairs of similar width, each pair a call of its own, so the lines of one picture spread
over the free workers. On DirectML the card's text finder sides come in the steps 256, 512, 768 and 1024, and the
tiles of an area go in calls of one size (`GpuSessionBudget.TilesPerCall`), so few input sizes exist (each is a session
of its own there, bounded by memory: "GPU"). Session sets of a CPU worker unused for 2 minutes are freed; stopping the
recognizer waits for the readings in work (at most 30 s) before it frees the models.

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
  counter `#####`. A match never starts or ends inside a run of letters and digits; a format that starts or ends with a
  sign (`(###)`) may touch them.
- **Container numbers** (`Matching/ContainerCheck`, the one rule of the matcher and the format-guided decoding): a
  format whose first 11 letters and digits are `AAAU` and seven `#` checks the 11th as the ISO 6346 check digit of the
  10 before it; what follows (`AAAU ###### # 22G1` with the size and type code) is not checked; `AAAU ######` has no
  check digit and checks nothing. A wrong check digit is no match: no character is ever changed to make it fit
  (decision 2026-11-03; a misread check digit never becomes a sure value).
- With a format the reader looks for matches in each line on its own, then in neighbouring lines joined (for a number
  split over two lines). The match fixes look-alike characters by position: where a digit is expected O becomes 0, I
  and L 1, S 5, B 8, Z 2, G 6; where a letter is expected the reverse.
- Several matches (the same order number on two receipts, two parcels): the area's value is the match read with the
  highest confidence, on a tie the larger text; the event field All matches lists every different match, at most 10.
- The page shows the matched text's cut-out picture next to its value (in memory only, like the frames).
- No match: the area's value is empty and **Matches format** is No; the raw text stays in **Text read**.
- **Any text** has no format to match: Matches format is always No (decision 2026-11-03: a rule "Matches format is
  Yes" fires only for a format that matched; "text was read" is a Text that is not empty).
- The page's format field tooltip: "# digit, A letter, * letter or digit".
- Not part of this version: taking a read text as the format: later (decision 2026-11-03).

### Runtime safety

- Every sync of the readers' loops (a save, a delete, Use the graphics card, a device group's change, the 1-minute check)
  reads the stored readers under the runtime's lock, never a list taken before it: an older list never wins, a deleted
  reader never streams or fires again.
- A stop cancels the single readings of the action Read text and waits for them before the recognition is disposed; a
  reading asked for afterwards ends "The Text recognition plugin is not running." Timer and event work never throws (a
  failure is logged); the runtime's locks are never disposed under late callbacks.
- Cut-outs are cut only when a page asks for a reading (`getReading`, `getPreview`), once per reading, never per frame,
  and only for a user with the host's **Live view** (`HostPermissionKeys.DevicesLiveView`; they are pieces of the
  camera's picture). The page never gets the picture itself: it shows the live video.
- The event `reading` reaches the pages only when a reader's values or note change, else at most once a second per reader;
  a preview's readings go only to the user who started it.
- A reading is kept as the reader's last and fires its events only while the reader is still stored with the same camera,
  source, resolution and areas (`ReaderRuntime.SamePicture`): a reading in work when the reader is deleted is dropped
  (the action ends "The reader no longer exists."), one of the old areas fires (the rule asked for it) but is not kept;
  a save drops the last reading after the sync when the picture changed.
- Decoding refuses a picture whose header says more than 7680 px on a side or more than 7680 x 4320 pixels before any
  pixel memory is taken (`Recognition/Picture`, the page's cut-outs too); a save refuses such a resolution.
- The reader list (and its event to a group) leaves Last text and the problem blank for a camera the user does not see
  (its problem reads "The reader's camera is no longer in its device group.").

## Events and the rule action

- Event **Text read** per reader (folder Text recognition / reader name), stateless, one event per area and reading,
  fields: Area (choice of the reader's areas), Text (the value: the match with a format, else all lines), Text read (all
  lines, raw), All matches (every different match of the format, separated by a space; empty without a format),
  Number (the value as a number when it is one, "." or "," as the decimal sign, e.g. a meter reading; written with "."
  without leading zeros or trailing decimal zeros, every digit kept, never through a floating point number,
  `Matching/NumberText`; empty otherwise), Confidence (number, 0 to 100), Matches format (Yes / No). Conditions check them with the field checks
  of `docs/spec/event-rules.md` (equals, contains, starts with; greater or less than for Number and Confidence).
- **Watching** publishes an area's event only when its value changes, and only once the vote of the last 5 frames ("Accuracy") gives the same value in 2 frames
  in a row (no flicker between two readings of one picture). An area that becomes empty publishes once with Text empty.
- Rule action **Read text** (`IActionPlugin`): parameter the reader (On action or Watching; a rule without one is refused
  at its save: "Choose a reader."). Reads one picture now and publishes every area's event, changed or not; a reader
  without an area ends "The reader has no area. Draw one on the Text recognition page." and reads nothing. A watching
  reader answers with its own reading of at most 1 s ago (`ReaderRuntime.FreshReading`, the voted values), else with the
  reading its stream has in work once it ends, else with a picture of its own: its stream reads all the time, so the
  action never ends "Already reading" because of it. Another single reading already running for an On action reader:
  the action ends "Already reading" and nothing is queued. Loops (a Text read event that runs Read text of the same
  reader) are held by the rule limits (dead time, maximum per minute).

## Page

- Three columns, no splitter: readers (fixed 320 px) | live video and results (the rest) | settings (fixed 320 px, the
  narrowest where the form, the format field and the areas fit; labels above their fields). The video gets every pixel
  the two fixed columns leave.
- Left: the readers (name, camera, mode, last text), **New** and **Delete** (red, with a confirmation); below the list
  Settings (Runs on, Use the graphics card) and the runtime stats ("Runtime stats"). The "Runs on" text is the server's.
  - The reader's chip: "Watching · 2 fps" (green), "Watching · 8 fps (reads 5.1)" (orange, slower than set), "On action",
    or a short red chip per kind of problem with the server's full text as its tooltip (`OcrTexts.ProblemChip`, one
    phrase per concept): Limit reached (the watching limits, also per site), Login refused (credentials required, no
    password, Basic only, HTTP 401 and 403), Certificate changed and Site offline (the device list's names), Not reachable
    (timeouts, no answer, no route), Stream error (no pictures, a resolution or video source not offered, other HTTP
    errors), Not in device group, Not installed; a text the page does not know: Not reading.
  - The camera texts ("AXIS Q1798-LE · 10.0.0.48") are kept for the readers' cameras only (at most 50), found in one pass
    over the devices; a device change reads them again while the page shows, else once when it shows again.
  - While the list is read a loading overlay ("Reading the readers") covers it; while a reader opens one covers the
    reader ("Reading the reader"). An open that fails leaves the shown reader and its row selected, the server's reason
    below the list; New ends every open still on its way (a late answer never replaces the new draft). The page's view
    model goes with its view (disposed when the view gets another one).
- The selected reader in two columns, no toolbar row:
  - **Left: the camera's live video**, the shared live video component (`ICorePluginClientContext.LiveVideo`,
    docs/spec/live-view.md) of the reader's camera and video source, with the picture only (no channel switch,
    fullscreen, quality switch or controls: nothing sits under the areas or leaves them behind). Over it
    (`ReadingPicture`) the areas (dashed accent, solid when selected, name tag) and the found lines of the latest reading
    (teal boxes with their text), placed on the reader's resolution centered in the video's space: the same letterboxing
    as the video's picture (Stretch Uniform). The video shows the camera's own picture shape; a resolution of another
    shape than the video source (4:3 on a 16:9 camera) draws the areas on the reader's shape. Without the host's live
    video (an older OADM) the overlay sits on the dark video surface. Below the video the reading's note when it has one
    (warning chip: "More than 30 texts found. ..."), then the **results grid** (Area, Text, Confidence right-aligned,
    Matches format, the time) in the rest of the column's height; while there are none "Waiting for the first picture."
    or why the preview reads nothing. The results, their details and cut-outs belong to the area at the same place in the
    reader (names may repeat while they are edited). A row shows the value's cut-out only when the server sent one (users
    with Live view); without one, or one that does not decode, the text alone.
  - **No area yet**: the video is dimmed a little (the theme's video dim) with "Start by drawing an area" in its middle;
    nothing is read (the preview has no stream), no lines, no results. New in the Areas header stays the way to start;
    while drawing the drawing hint shows instead.
  - **Right, a grid of four rows**: the form (Name, Mode with Watching and On action, **Stream settings**), the Areas
    header, the area rows (the only part that scrolls: with many areas New above and Save below stay in reach), then the
    save status left of **Save**. No camera field (the camera is the reader's for good, "Readers").
  - **Stream**: a secondary button (video icon) that opens a small dialog (single operation: no card,
    `Border.dialogBody`, 400 px wide, OK and Cancel): Video source, Resolution and, only while Watching, Frames per second
    as a select of the server's choices (1, 2, 4, 8, 12, 15; the select follows the choices of each reader list and of
    `getEngine`; a stored rate no longer offered shows as the highest offered below it, the reader Not saved until
    Save stores it). The camera's choices come from the newest `getCameraOptions` call. OK hands them to the reader (Not
    saved like any edit, the preview and the live video follow); Cancel changes nothing. The field is labelled Stream and spans the column; the
    button itself shows the summary, "Configure · 1920x1080 @ 4 fps" (no rate for On action; no hint below); a refusal of the server for the
    video source, the resolution or the frame rate shows in the summary's place and, in the dialog, below its field.
  - **Name and Mode** side by side (decision 2026-11-03): the reader's name, and Mode as a select (Watching, On action).
    No status line under the video: the reader's state is in the reader list.
  - **Areas panel**: header Areas left and **New** at the right edge (flat command button, plus icon; it starts drawing on
    the video; tooltip "Draw an area on the video"; at 8 areas off with "A reader has at most 8 areas."); no count. Each
    area in two lines (decision 2026-11-03): the name as plain text (trimmed, the whole name as tooltip; a refusal of the
    name or the box below it, each under its own key: a rename never clears the box's, a move or resize does) with Rename
    and delete at the right; below it, indented, Mode and Format. Mode is a select (Any text, Format); the Format field stays in place, empty and off for Any text (the format
    typed before comes back with Format), its example "AAAU ###### #" as the placeholder and the format syntax as its
    tooltip; errors directly below the field. **Rename** (icon only, tag icon, tooltip "Rename") opens a small dialog
    (single operation, one Name field: required, unique in the reader, at most 64 characters, the error below it; Rename
    and Cancel). The rows are a list (`ListBox.areaRows`): the selected area's row is softly highlighted (the hover fill
    and an accent bar on its left edge, so the inputs and a red error stay readable), and selecting a row (or a field in
    it) selects the area on the video. A refusal of the area list as a whole (`areas`: too many areas) shows directly
    below the area rows (small red text) until the areas change, never in the host's message.
- **Areas on the live video**: New, then a drag draws one (at least 2 % of the picture each way, `OcrLimits.MinAreaSide`
  like the server; Esc stops). A press on an area or on its row in the Areas panel selects it: solid with handles at the
  corners and edge midpoints. A drag inside moves it, a drag on a handle resizes it; on a small area (a side below
  28 px on screen) a press inside always moves it and its handles are taken just outside it, so it can always be moved.
  Both stay inside the picture and keep the drawing minimum (`AreaGeometry`); Esc cancels the drag in progress; the arrow keys move it by 1 % (Shift: 5 %). The cursor shows move
  over the body and resize over the handles. Every change marks the reader Not saved like any other edit. Without the
  change right nothing moves. While an area is drawn, moved or resized the found lines are hidden (they belong to the old
  areas); they show again when the drag ends. The drag's end is final: the overlay hands the new box to the reader
  before it lets go of the pointer (releasing the pointer capture cancels a drag still in progress, which made areas
  snap back), and nothing the preview sends replaces the areas.
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
    closes.") and 2 per user (a third ends that user's oldest: a page closed without a word); a preview's own stream
    counts with the watching readers against the watching limit ("Limits") and 4 per site, after them: a saved reader
    that starts watching takes the stream of the youngest preview; beyond: "No live results now: OADM already reads as
    many cameras as it can." The texts show where the results are. A preview's own stream that gives no pictures says
    why in its `problem`, like a reader.
  - A lease: a preview ends 30 s after its last `startPreview` (checked every 5 s), so a closed client never leaves one
    running; the page ends it with `stopPreview` when it hides the reader or the page.
  - The page makes the preview's id itself (32 hex digits, new per start) and sends it from the first call, so `stopPreview`
    can always name it; a start answer that comes after the page left ends that preview once more (the start may have
    overtaken the stop). A page shown again starts its preview at once (a call still on its way never blocks it). The
    server keeps a page's id unless another preview uses it; any other text gets an id of the server's.
  - Fetched readings show in the order their calls started: the saved reader's last reading never replaces a newer
    preview reading that arrived while it was decoded.
  - A preview belongs to the user who started it, in their device scope: only that user hears of it, gets or stops it
    (another user's id answers like an ended one); a saved reader of another scope answers like an unknown id; the camera
    must be in the caller's devices.
  - On open the page shows the saved reader's last reading (`getReading`) until the preview's first one arrives.
- Empty page: "Create a reader to read text from a camera."
- One contract: the page uses the server part's payloads (`Api/`) and the texts of `ReaderRules`, never copies. The
  camera's video sources and sizes (largest first) come from `getCameraOptions` (read only, the caller's devices, kept
  10 min per camera). A save refused by a check answers `{"errors":[{"field","message"}]}` with stable field keys
  (`name`, `camera`, `videoSource`, `resolution`, `mode`, `fps`, `areas`, `areas[i].name|box|format`); the page shows each
  below its field: the watching limits below Mode, the video source, the resolution and the frame rate below Stream
  settings, `areas` below the area rows, an area's name and box below its name, its format below the format. Refusals without a field on the page (the camera, reader gone, 50 readers) stay errors in the host's message.
- The preview's calls (permission "View text recognition"):
  - `startPreview` `{"previewId":"9f3c...","reader":{...the draft...}}` → `{"previewId":"9f3c...","problem":"..."}` (no
    `problem` while it reads); with a known `previewId` of the caller it keeps that preview alive and takes the
    draft, an unknown or ended one starts a new preview. Refusals: the 8 previews, a camera outside the caller's
    devices, another scope's reader.
  - `stopPreview` `{"previewId":"9f3c..."}` → `{}`.
  - `getPreview` `{"previewId":"9f3c..."}` → `{"reading":{...},"details":[...]}` with the cut-outs (Live view only), no
    picture (`{}` without a reading).
  - Event `preview` `{"previewId":"9f3c...","reading":{...},"details":[...]}` after each reading of a preview (without
    the cut-outs), only to the user who started it (`PluginEventAudience.User`); the page fetches with `getPreview` only
    its own, one call at a time (newest wins).
  - `getReading` → `{"reading":{...},"details":[...]}`: the reader's last reading with the cut-outs (Live view only),
    never the picture.

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
  (`IRecognizerLoad.Capacity`, the CPU workers `max(1, cores / 2)`), each CPU call counted while it holds its slot. With a
  graphics card (Search's text finder there, one call at a time) the busy time grows per sample by the larger of the
  workers' time and the card's time times the workers (`Recognition/CombinedBusy`): the load is the larger of the CPU's
  and the card's. So 100 % still means no more fits: every CPU worker busy, or the card never idle.
- **Numbers** (in `getStats` only, never shown on the page: decision 2026-11-03, the traffic light already says whether
  all is well): pictures per second (all readers, previews included; pictures whose areas were all unchanged are not
  counted here); unchanged pictures per second (their last reading given again, no model ran: `ReaderStats.CountReused`,
  JSON `unchangedPerSecond`); the average reading time (ms per picture read); readers watching and its limit; previews
  and their limit; camera streams open, per site with the site's limit; the OADM server process's CPU
  (`Environment.CpuUsage.TotalTime` growth divided by the window times the processor count).
- **Per reader**: each watching reader's pictures per second over the last 10 s; below 90 % of its rate its list chip
  turns orange: "Watching · 8 fps (reads 5.1)".
- `getStats` `{}` → the stats (permission "View text recognition"):
  `{"state":"Busy","stateText":"Busy: 1 reader reads slower than set","load":42.5,"picturesPerSecond":12.3,"unchangedPerSecond":4.5,"averageReadMs":85,"watching":3,"maxWatching":50,"previews":1,"maxPreviews":8,"streams":4,"sites":[{"name":"Harbour","streams":2,"max":4}],"serverCpu":23.1,"readers":[{"readerId":"9f3c...","fps":8,"reads":5.1,"slow":true}]}`.
  Each call keeps the live event `stats` (the same JSON every 2 s) on for the caller's scope for 30 s; the page calls it
  on open and every 10 s, so the event runs only while a page is open.
- Device groups: a group user sees the counts, sites and readers of their own group only, plus the server-wide load, CPU,
  pictures per second and reading time (no other scope's reader); each scope's event goes to that scope.

## Limits

Decision 2026-11-03 (with the graphics card reading only Search's text finder, "GPU"): the limits follow the CPU on every
server, the traffic light ("Runtime stats") is the real guard.

- **Frames per second**: 1, 2, 4, 8, 12 or 15 for every reader on every server (`OcrLimits.FpsChoices`; default 1). A
  fixed area costs a worker about 28 ms (35 pictures per second), an unchanged one about 5 ms, so one reader at 15 is
  about half a worker; a server that cannot keep up turns the light orange or red and says which reader reads slower.
- **Watching readers**: 4 per recognition worker (`max(1, cores / 2)`), at least 8 and at most 50
  (`OcrLimits.MaxWatching(cores)`): 2 or 4 cores 8, 8 cores 16, 16 cores 32, 24 cores 48. Measured on 24 cores: 20
  fixed-place readers at once 113 to 138 pictures per second, so 48 readers at 2 frames per second (96 per second) fit,
  at 4 they need unchanged areas or the light says so. Previews with a stream of their own count with them.
- **Per site**: 4 watching readers (and previews with their own stream) per site; their streams take none of the site's
  large transfer slots (`VapixRequestOptions.LiveStream`), so AXIS OS upgrades, applications and exports at the site
  never wait for them.
- Readers stored beyond a limit (the readers moved to a server with fewer cores, a camera moved to a site) read nothing
  and show the limit as their problem ("At most 16 readers can watch at once. Set another reader to On action first.",
  the site's text); a stored frame rate that is no choice reads at the next one below.
- `listReaders` and `getStats` give the server's limit (`maxWatching`); `getEngine` the frame rates.

## Permissions

- **View text recognition**: the page, the readers and their results.
- **Change text readers**: New, Delete, Save, moving and drawing areas.
- Running the action is part of the event rules' own permissions.

## Accuracy

The goal is to read better than the commercial readers (VAXTOR Generic Reader as the benchmark): the right value more
often, from worse pictures, and never a wrong value reported as sure.

- **Format-guided reading**: with a format, the recognition's character choice is limited per position (digits where
  `#`, letters where `A`) while decoding, not fixed afterwards; the look-alike table stays as a last step. A check
  digit is checked where the format has one (ISO 6346 for container numbers, "Expected text"); a wrong check digit is no
  match, never repaired by changing a character (the decoder once changed one character to make it fit; dropped
  2026-11-03, the accuracy set kept its baseline).
- **Several frames vote** (Watching): per area the last 5 readings are combined character by character, weighted by
  confidence; the value is published once it holds: the vote must give the same value in 2 frames in a row. With a
  format the combined value must be a whole match of it, check digit included (`FrameVote.AcceptorOf`): characters of
  different readings that together break it are never published; the whole reading with the most weight wins instead.
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

- The plugin picks at start: the graphics card when the provider loads and a test reading succeeds, else the CPU only.
  Settings card of the page: "Runs on: CPU (24 cores) · search on NVIDIA GeForce RTX 5070 Ti" or "Runs on: CPU (8
  cores)", and **Use the graphics card** (on by default) to force the CPU. `getEngine` / `setUseGpu` answer
  `{runsOn, gpuAvailable, useGpu, fpsChoices}`; `setUseGpu` is server-wide: the change right and all devices
  (`RequiresAllDevices`; the page disables the box for a user with a device group), audited. The setting is a plugin
  setting handed to the recognizer (`IEngineInfo`) at start and on change; the next readings run where it says, the
  graphics card keeps its sessions until they idle or the plugin stops. Without a recognizer: the CPU, no graphics card.
- **The graphics card reads Search only** (decision 2026-11-03, by the measurements below): the text finder of an area
  read in tiles (larger than 960 px on its long side, `OnnxTextRecognizer.TileSize`; Search over the whole picture)
  runs on the card with the server text finder (`Recognition/CardTextFinder`, one call at a time). Everything else runs
  on the CPU workers as without a card: the text finder of fixed-place areas (mobile, Accurate: server), the reader and
  the angle model for every line, Search's lines too. Measured: fixed areas are as fast on the card as on the CPU, 20
  readers at once slower (the card's calls run one at a time), and Search's lines on the card would make one full HD
  Search about 80 ms faster (the card read 32 lines in one call of 82 ms, the old path's reader 29 ms per picture) but
  take a fifth more of the card's time per picture, which several Search readers share, and need reader sessions on
  the card; on the CPU's 12 workers they run beside the card's next text finder call.
- **Graphics card memory** (`Recognition/GpuSessionBudget`, no setting): the card holds only the text finder's sessions.
  On DirectML every input size is a session of its own that keeps its own copy of the model and its buffers on the card
  until it is disposed (the provider frees nothing while a session lives). The budget counts each session by the pixels
  of its calls: `512 MB + 1,500 bytes x count x height x width` (`Estimate`, never below a measured session), and keeps
  the sum at or below **4 GB** (`MaxBytes`) whatever the cameras' resolution:
  - a Search is split into calls of one size of at most half the cap (`TilesPerCall`: the fewest calls, the tiles spread
    evenly, blank tiles fill the last one), so two Search sizes (two cameras' resolutions) stay on the card together; a
    single tile of 1024 x 1024 (1.96 GB estimated) always fits. Full HD: 6 calls of one 768 x 768 tile (two would be
    2.15 GB); QHD: 6 calls of one 1024 x 1024 tile; 4K: 15. The calls' inputs and boxes are made on the CPU while the card
    runs another call (at most 3 calls ahead);
  - before a new session the least recently used ones go until it fits (made again when needed, 0.5 to 1 s) and the
    first time the server logs "Text recognition keeps at most 4096 MB of model sessions on the graphics card"; a session
    larger than the cap is never made;
  - a session unused for 2 minutes is freed (like the CPU workers' sets), so an idle server gives the card back;
  - every call runs inside the budget's gate, so a session is never freed while it runs; memory pattern off and
    sequential execution as the DirectML provider asks;
  - CUDA: one session takes every size; its memory pool grows by what a call needs
    (`arena_extend_strategy=kSameAsRequested`, not the next power of two), bounded by the same calls per Search.
  - Measured (2026-11-03, development PC, RTX 5070 Ti 16 GB, DirectML, the desktop holds 1.5 GB), the text finder's
    session on the card:

    | Text finder input | On the card | Estimate |
    |---|---|---|
    | 1 x 256 x 256 | 0.3 GB | 0.59 GB |
    | 1 x 512 x 512 | 0.65 GB | 0.87 GB |
    | 1 x 768 x 768 (a full HD Search tile) | 1.25 to 1.3 GB | 1.32 GB |
    | 1 x 1024 x 1024 (a QHD or 4K Search tile) | 1.5 to 1.7 GB | 1.96 GB |
    | 2 x 768 x 768 | 2.1 GB | 2.15 GB |
    | 6 x 768 x 768 (the old full HD call) | 5.0 GB | 5.44 GB |
    | 2, 4, 6 x 1024 x 1024 (the old calls above full HD) | 3.1, 6.0, 8.6 GB | 3.43, 6.36, 9.29 GB |

    Before (until 2026-11-03) at most 6 sessions were kept by count: one 4K Search alone needed 14.6 GB and 3 Search
    sizes filled the 16 GB card (the driver reset, TDR).
  - Measured with the cap (same PC and day, the process's share of the card at its peak, the desktop not counted):
    fixed place, 4 areas and 20 readers 0.1 GB (no session on the card); full HD Search 1.25 GB; QHD and 4K Search after
    it 2.7 GB (the full HD and the 1024 x 1024 sessions, 3.29 GB estimated, none freed); the accuracy set 1.3 GB. No
    driver reset in any run.
- The limits do not depend on the graphics card ("Limits"): it reads only Search's text finder, the CPU every fixed area
  and every line.
- Speed goals: one Search reader over a full HD picture at 10 frames per second or more on the GPU, at least 1 on the
  CPU; 20 fixed-place readers at 4 frames per second.
- Measured (2026-11-03, development PC: Ryzen 9 3900X, 24 cores, RTX 5070 Ti with nothing else on the card,
  `RecognitionBenchmarkTests`, Perf; the full HD picture of the coffee box, QHD and 4K the same picture scaled up; ms per
  picture, one after the other; 20 readers: all at once, ms per picture in all; other work ran on the PC, the best of 2
  runs):

  | Case | CPU before | CPU now | All on DirectML (before) | Search on DirectML (now) |
  |---|---|---|---|---|
  | Fixed place, 1 area | 53 | 28 | 26 | 28 (CPU) |
  | Reader with 4 areas | 225 | 79 | 75 | 79 (CPU) |
  | Search, full HD | 643 | 483 | 207 (5 GB on the card) | 321 (3.1 per second) |
  | Search, QHD | | 945 | (8.6 GB) | 377 (2.7 per second) |
  | Search, 4K | | 2,190 | (14.6 GB) | 923 (1.1 per second) |
  | 20 fixed-place readers at once | 16.6 (60 per second) | 7.3 (138 per second) | 14.2 (71 per second) | 8.8 (113 per second, CPU) |
  | Fixed place, unchanged (watching) | 53 | 5 | 5 | 5 |
  | 4 areas, unchanged (watching) | 225 | 13 | 13 | 13 |
  | 20 readers, unchanged (watching) | 16.6 | 1.0 | 1.4 | 0.9 |

  Rows marked CPU run the same CPU path in both columns (the difference is the other work on the PC). Search on the card
  per stage (ms per picture, the CPU's summed over threads): full HD text finder on the card 175 (6 calls; 146 in 3 calls
  of 2 tiles, which the cap does not allow), reader on the CPU 280, decode 15; QHD text finder 257, 4K 645. The card's
  time per Search picture bounds the Search readers of one card: about 5.7 full HD, 3.9 QHD or 1.5 4K pictures per
  second in all. Where the time goes on the CPU (fixed place): decode 2.5, text finder 10, reader 15 ms. Kept: the partial
  decode (fixed place 53 to 38 ms), the angle model only for unsure lines (38 to 30 ms, a reader with 4 areas 197 to 147),
  the lines in pairs (4 areas 147 to 87), unchanged areas, rented buffers (no large objects per picture). Tried and left
  out: ONNX Runtime threads that sleep instead of spinning (20 readers 97 to 76 per second), batches of 1 or 4 lines (no
  better than 2), reusing the line boxes of a changed area (a new line in a small part of the area would be missed until
  the next search), all models on the card (above), the card's calls of one area one after the other without preparing
  the next (full HD 369 instead of 321 ms). Earlier DirectML runs while another OADM server held 10 GB of the card made
  the driver reset (TDR).
  The CPU goal holds. On the graphics card the Search goal (10 per second) is open: Search is 1.5 (full HD) to 2.5 (QHD,
  4K) times faster than on the CPU; 20 fixed-place readers at 4 per second (80 per second) hold on the CPU.
- Accuracy set with the graphics card (Search on the card, 2026-11-03, `RecognitionBenchmarkTests.Accuracy_set_with_the_graphics_card`,
  6.0 s for 69 pictures): every group at or above the CPU baseline; hard-receipt-search 50 % exact (CPU 37.5 %, the
  server text finder), the other groups as the CPU (camera-box 100 %, hard-sign 66.7 % with 3.2 % character errors).
  The old all-card path read camera-box 75 % and hard-sign with 4.8 % character errors (its server text finder on the
  fixed areas).

## Packaging and dependencies

- Add-on (`docs/spec/add-ons.md`): one ZIP per server platform with ONNX Runtime (MIT: `Microsoft.ML.OnnxRuntime.DirectML`
  on Windows, the GPU build with the CUDA provider on Linux x64, the CPU build elsewhere), SkiaSharp (MIT; Linux:
  `NativeAssets.Linux.NoDependencies`), the PP-OCRv5 mobile and server detection and Latin recognition models and the
  angle model as ONNX files with the character list (Apache-2.0, in the plugin's notices). The size is measured at the
  build (Linux x64 is the largest because of the CUDA provider). The client part has no native libraries.
- DirectML itself (`DirectML.dll`, NuGet `Microsoft.AI.DirectML`, Windows only) is not open source: "MICROSOFT SOFTWARE
  LICENSE TERMS – DIRECTML" allow passing it on inside applications for Windows, and its section 2a says the software
  may collect data and send it to Microsoft. Its full license is in the plugin's notices (`OadmNoticePackage` item in
  the csproj; `check-addon-zip.sh` fails a Windows ZIP without it). The user docs say that the graphics card on Windows
  runs on Microsoft DirectML under Microsoft's terms; turning off **Use the graphics card** keeps it unused (CPU only:
  ONNX Runtime MIT, SkiaSharp MIT, models Apache-2.0).
- The models are never in git: the build downloads each from its pinned Hugging Face revision into a local cache, checks
  its SHA-256 (another hash fails the build) and copies it into the output's `Models/` (`Models/Models.targets`,
  `Models/README.md` "Download at build", `docs/spec/packaging.md` "Add-on ZIPs"). The plugin folder without `Models/`
  reads nothing: every reading ends "Text recognition is not installed completely. Install it again on the Plugins page."
- No OpenCV, no Tesseract, no Python; the CUDA runtime is the server's own, never shipped.

## Parts and contract

One contract between the parts, in `Model/`; the readers (`Readers/ReaderRuntime`) work only against it, the plugin builds
the real parts once per start (`ReaderParts.Real`), the tests hand fakes:

| Contract | Real part | What it does |
|---|---|---|
| `IRecognizerLoad` (optional) | `Recognition/OnnxTextRecognizer` (`BusyMeter` in the CPU slots and the graphics card's calls) | busy time and capacity for the runtime stats |
| `IFrameSource` | `Frames/VapixFrameSource` (the plugin's full device access, `ctx.System.Vapix`) | single pictures and the MJPEG stream of a camera |
| `ITextRecognizer` (+ `IEngineInfo`) | `Recognition/OnnxTextRecognizer` (the plugin folder's `Models/`) | lines per area with box and confidence, the too small areas, unchanged areas given again for a reuse source (`ReadAsync(jpeg, areas, reuseSource, ct)`; `IDetailedTextRecognizer` adapts it); what reads (`OnGpu`, `RunsOn`), Use the graphics card |
| `ITextMatcher` | `Matching/TextMatcher` | an area's value (`AreaValue`): whether it matches, the raw text, every match, its confidence and box |
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
- The page (`Client/`): fixed columns without a splitter and the results below the video; Name, Mode and Stream settings
  with its summary (no camera field), the area rows under one header with the name as text, the mode select, Rename and
  the row selection following the area; with 8 areas in a short window only the rows scroll, New and Save stay in reach; a real pointer drag that moves
  and resizes an area which stays there (`PageLayoutTests`, headless); the lines hidden while an area is drawn or
  adjusted, the start hint without areas, the reading's note, the stats panel and a slow reader's orange chip
  (`PageAreasAndStatsTests`); the live video follows camera and source, the preview's draft on every change and the keep-alive,
  only the newest reading fetched, moving, resizing and nudging areas held inside the picture, the On action message
  once, new readers watching, the frame rate select; headless screenshots dark and light (the live video as a still
  frame): watching with the results below the video and green stats, the stats orange and red, an area being moved
  (lines hidden), a reader without an area (dimmed with the hint), the problem chips with the area list's refusal, the
  loading overlays of the list and of a reader. Where the server and the user overtake each other
  (`PageEdgeCaseTests`): a stored rate no longer offered (Not saved), the choices following the server, the `areas`
  refusal below the rows, a box refusal through a rename and a move, a failed and a late open, the newest camera
  options, results by the area's place, readings without cut-outs, the camera texts at 100,000 devices (one pass), the
  view model leaving its view, a small area moved from inside; each problem's chip (`PageViewModelTests`). Headless tests return a value from
  `Session.Dispatch(async () => ...)`: it has no overload for a plain `Task`, so without one the assertions after the
  first await never reach the test.
- Runtime safety (`Readers/RuntimeSafetyTests`, `Client/PagePreviewSafetyTests`): a deleted reader stays gone whatever sync
  runs late, racing saves and deletes end with the stored readers streaming, a stop cancels and awaits a single reading,
  cut-outs only on request and once, the `reading` event on change or once a second, a group's preview only to its
  group, the page's own preview id, a late start answer ended, an older reading never over a newer one.
- Runtime stats (`Readers/StatsTests`, `FakeTimeProvider`): the 10 s window, the load on the CPU and a graphics card
  (`Recognition/SearchRoutingTests`: the larger of the CPU workers' and the card's busy time), the
  server CPU, a reader judged only after a whole window, the traffic light's thresholds, red only after 30 s at 95 %, the
  live event only while asked, device groups; the busy time each worker call adds (`WorkerTests`).
- Limits: at most `max(1, cores / 2)` workers held by a gate; 50 readers, the watching readers by the cores (4 per worker, 8 to 50) and 4 per site, a reader beyond them with the limit as its problem, the saved readers before the previews' own streams, 2 previews per user; GPU fallback to the CPU; the accuracy set and its score; device groups; Use the graphics card only with all devices.
- MJPEG parsing from a recorded `multipart/x-mixed-replace` answer (boundary, Content-Length, a part without it).
- End to end with the real parts (`EndToEndTests`): recorded pictures of the coffee box through a watching reader give
  each value once and an empty value once when the box is gone; the action Read text reads a container number with its
  format and the page gets its cut-out.
- The model sources: `Models.targets` and `Models/README.md` name the same files, revisions and SHA-256.
- Speeds (`RecognitionSpeedTests` and `RecognitionBenchmarkTests` with the time of each stage, Perf, not in `manage
  test unit`): the numbers of "GPU".
- Partial decode (`PictureTests`: the same pixels as a full decode), unchanged areas (`UnchangedAreasTests`: reuse,
  a changed area read anew, other readers, 10 uses in a row), the angle model's skip rule, idle session sets freed
  (`FakeTimeProvider`), a set in use freed only after its call.
- Watching readers and previews reuse under their own source, the action never, unchanged pictures counted apart
  (`UnchangedReuseTests`); the graphics card's memory: sessions counted by pixels and never above the cap, least
  recently used out, a session over the cap never made, Search split into calls of half the cap, idle sessions freed
  (`GpuSessionBudgetTests`, fake sessions); fixed-place areas on the CPU and Search on the card, full HD, QHD and 4K
  Search within the cap, the runs-on text (`SearchRoutingTests`, fake runners, no graphics card).
- A reader's life (`Readers/ReaderLifecycleTests`): a rename keeps the stream and the published values, a reading of a
  deleted reader is neither kept nor fired, one of old areas not kept, a stream's problem in the list until pictures
  come, Read text from a watching reader's fresh reading or the one in work, cut-outs only with Live view, no text of a
  camera the user does not see. The stream's problem texts once per change and the live stream option
  (`VapixFrameSourceTests`), a picture claiming 65,535 x 65,535 refused (`PictureTests`), the container rule and the
  vote's format check (`TextMatcherTests`, `FrameVoteTests`, `DecodingTests`), Number with every digit
  (`TextReadEventTests`).

## Build plan

Built in three waves of parallel packages (skeleton; recognition, matching, frames, readers and page; the real parts
wired). Open: the hardware runs on the development cameras.

## Rotation and perspective (measured 2026-11-03)

How far a text can be turned in the picture and tilted away from the camera and still read. Measured on the CPU with the
mobile text finder by `Recognition/AngleSweepTests` (Perf, about 10 minutes on 24 cores; the What_if test alone 9.5) and
pinned by `Recognition/AngleLimitsTests` (a normal test, 5 s).

- **Pictures**: made in memory (`Recognition/AngleSweep.cs`, SkiaSharp), full HD, the plate in the middle: the container
  number `MSKU 123456 5` (`AAAU ###### #`, `45G1` below it) on a ribbed door, `4/12` (`#/##`, `INTENSITY` above it) on a
  label, `LOADING DOCK 3` (Any text, one line) on a sign, `36F1A` (`##A#A`, `TOTAL EUR 10.60` below it) on a receipt.
  20 variants per text and angle: Roboto Bold and Regular (from the repository), Arial Bold, Consolas Bold, cap heights
  24 to 40 px, each block of 5 clean, blurred (1.2 px), noisy (sigma 8) or strong JPEG (quality 40).
- **Fixed place**: one square area around the plate, large enough for any turn (the same area at every angle). **Search**:
  the whole picture, only the three texts with a format (Search with Any text gives every text of the picture), 10 variants.
- **Tilt**: the plate on a plane turned around the vertical axis ("side") or the horizontal axis ("top"), seen through a
  60° lens at the distance where it is drawn at its size, the near edge kept at its size.

**Turned in the picture, fixed place** (exact values, 20 per text; a / b is the angle and its mirror, 15° and 345°):

| Turn | With a format | Container | 4/12 | Order code | Sign (any text) |
|---|---|---|---|---|---|
| 0° | 100 % | 100 % | 100 % | 100 % | 100 % |
| 5° / 355° | 100 / 100 % | 100 / 100 % | 100 / 100 % | 100 / 100 % | 100 % |
| 10° / 350° | 96.7 / 96.7 % | 100 / 100 % | 100 / 100 % | 90 / 90 % | 100 % |
| 15° / 345° | 95.0 / 93.3 % | 100 / 100 % | 100 / 100 % | 85 / 80 % | 100 % |
| 30° / 330° | 90.0 / 88.3 % | 90 / 85 % | 100 / 100 % | 80 / 80 % | 100 % |
| 45° / 315° | 91.7 / 90.0 % | 95 / 80 % | 100 / 100 % | 80 / 90 % | 100 / 95 % |
| 60° / 300° | 76.7 / 80.0 % | 80 / 90 % | 95 / 90 % | 55 / 60 % | 100 / 95 % |
| 85° / 275° | 81.7 / 83.3 % | 95 / 90 % | 100 / 100 % | 50 / 60 % | 100 % |
| 90° / 270° | 100 / 95.0 % | 100 / 85 % | 100 / 100 % | 100 / 100 % | 100 % |
| 95° / 265° | 86.7 / 75.0 % | 100 / 85 % | 100 / 90 % | 60 / 50 % | 100 % |
| 120° / 240° | 68.3 / 66.7 % | 75 / 60 % | 85 / 95 % | 45 / 45 % | 100 % |
| 135° / 225° | 76.7 / 81.7 % | 80 / 65 % | 90 / 95 % | 60 / 85 % | 100 % |
| 150° / 210° | 86.7 / 88.3 % | 85 / 85 % | 100 / 95 % | 75 / 85 % | 100 % |
| 165° / 195° | 91.7 / 95.0 % | 100 / 100 % | 95 / 100 % | 80 / 85 % | 100 % |
| 170° / 190° | 95.0 / 96.7 % | 95 / 100 % | 100 / 100 % | 90 / 90 % | 100 % |
| 175° / 185° | 98.3 / 96.7 % | 100 / 100 % | 100 / 100 % | 95 / 90 % | 100 % |
| 180° | 100 % | 100 % | 100 % | 100 % | 100 % |

All 40 angles: 88.2 % exact with a format, 99.8 % any text. Search over the whole picture with a format (10 per text):
0°, 30°, 90°, 180°, 270° 100 %; 15° and 75° 96.7 %; 45° 90.0 %; 60° 86.7 %; 135° 83.3 %; 225° and 315° 93.3 %.

**Tilted away, fixed place** (exact values with a format / any text):

| Tilt | Side | Top | Side + 15° turn | Top + 15° turn |
|---|---|---|---|---|
| 0° | 100 / 100 % | 100 / 100 % | 95.0 / 100 % | 95.0 / 100 % |
| 10° | 100 / 100 % | 100 / 100 % | 95.0 / 100 % | 96.7 / 100 % |
| 20° | 100 / 100 % | 100 / 100 % | 93.3 / 100 % | 96.7 / 100 % |
| 30° | 100 / 100 % | 100 / 100 % | 91.7 / 100 % | 93.3 / 100 % |
| 40° | 100 / 100 % | 100 / 100 % | 95.0 / 100 % | 96.7 / 100 % |
| 50° | 100 / 100 % | 100 / 100 % | 95.0 / 100 % | 96.7 / 100 % |
| 60° | 98.3 / 100 % | 100 / 100 % | 91.7 / 80 % | 96.7 / 100 % |

Search with a format, tilted: 100 % up to 60° to the side, up to 50° from the top, 96.7 % at 60° from the top.

**Limits** (the user docs' "Camera angle"):

- With a format, 95 % exact or better: turned up to 10° either way from upright or upside down (170° to 190°); a
  container number up to 15°. Turned exactly on its side (90°, 270°) reads too, 5° to 10° off it does not (75 to 88 %).
- Any text, 90 % exact or better: a single line at every turn (95 % at 300° and 315°, 100 % elsewhere).
- Tilt: up to 60° to the side or from the top (the end of the sweep) as well as straight on: 98.3 % or better with a
  format, 100 % any text. Tilt and a 15° turn together: the turn sets the limit (91.7 to 96.7 %); 60° to the side with a
  turn drops a single line to 80 % (`LOADING DOCK3`: the far, squeezed space is lost).

**Why it breaks**:

- **Two lines close together** (the order code above `TOTAL EUR`, the container number above `45G1`, about one cap height
  apart): turned 15° to 75° off an axis, the text finder's regions of both lines join into one box (the receipt at 15°:
  one box 342 x 178 px; at 60°: 129 x 224, turned by the 90° rule). The box is then read across both lines and dropped
  (mean below 50 %) or misread (`36A0O`, `36G1A`). 139 of the 286 misses had no line at all. A single line (the sign) never
  breaks, a line with more room to the next one (`4/12`) rarely does.
- **Short lines of digits upside down** rely on the angle model: `4/12` at 150° got 0.24 to 0.38 for upside down (the
  candidate threshold is 0.3), so it is read turned only sometimes.
- **The picture**: of the misses 117 had strong JPEG and 129 Consolas (wide letter spacing), 10 noise.
- Near-square boxes at 45° were not a problem (`4/12` 100 % at 45°).
- Any text with several lines upside down gives the lines in reversed order (`TOTAL EUR 10.60 36F1A`); a format is not
  affected.

**Tried, no gain** (`What_if_rotation`, the same 3,200 pictures through a wrapped model runner, nothing in the plugin
changed): every unsure line read both ways (the angle model skipped) 88.2 % with a format, the text finder's pixel threshold
at 0.5 instead of 0.3 88.2 %, both 88.3 % (as it is: 88.2 %). The joined lines stay one region at 0.5; keeping them
apart needs another step than a threshold (not measured: split a box that holds two rows at the gap in the text finder's
map).
