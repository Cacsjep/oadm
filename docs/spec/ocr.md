# Text recognition

Status: **built** (2026-11-03): recognition, matching, frames, readers, page and the real parts wired; the hardware runs
on the development cameras are open. Developer notes: `plugins/Oadm.Plugins.Ocr/README.md`.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Reads text in the cameras' pictures (signs, container and wagon numbers, displays, meter readings, labels). The first
vision plugin, an add-on (`docs/spec/add-ons.md`): not in the OADM installers, installed on
demand on the Plugins page.

Core plugin `plugins/Oadm.Plugins.Ocr` (+ `.Client`), id `oadm.ocr`, rail page **Text recognition** (the new rail group **Vision**, after Monitoring, for every vision add-on; SDK 0.10.0),
free (Apache-2.0), **off by default**. License plates are not its job: AXIS License Plate Verifier on the camera does
that better (the docs say so).

## Rules that never break

1. **Read only.** The plugin only receives pictures from the cameras; it never changes a setting on a device.
2. **Pictures never stored.** Frames live in server memory while they are read (with a coarse brightness print per area
   for unchanged areas, "Reading" step 8); the last result of each reader (text,
   boxes, the frame for the page) is kept in memory only. A paused preview holds one frame in server memory, one per
   preview ("Pause"): it goes on Resume, when the preview's lease ends, when the reader changes and when the page closes,
   never on disk, never in a log. The last 20 reads ("The last 20 reads") keep one cut-out per row, at most 20 per reader
   and per preview, each at most 640 px on its longer side and 64 KB: in server memory only, never on disk, never in a log,
   dropped with their row, gone on a server restart, with a deleted reader, with the preview and when the plugin turns off.
   The vote keeps a reference to the frames of its last 5 readings per area, none for an empty value, released as the window
   moves on, so a value's cut-out comes from the frame it was read in ("Several frames vote"). The texts reach the event log as
   event fields, never the frames.
3. **Choose the camera first.** A reader starts from one camera (`DevicePickerWindow.ShowOneAsync`) and one of its video
   sources; nothing streams before that. Only cameras of the user's device group; readers are owned by the device scope
   that made them (`docs/spec/device-scope.md`).
4. **Bounded load.** Recognition runs on the CPU in at most `max(1, cores / 2)` model calls at once, each on one thread, so
   half the machine stays free and OADM stays responsive; all calls share one session of each model. At most 3 large text
   finder calls at once (`OcrLimits.LargeTextFinderCalls`, measured: more only burn the CPU). The limit counts model calls (a
   tile, a line), so an idle server reads the lines of one picture at once; at most as many pictures as workers are in work.
   A reader has one frame in work, up to 4 while the recognition has room (`OcrLimits.MaxFramesInWork`,
   `IRecognizerLoad.HasRoom`: a CPU worker free, the card with room and nothing waiting), so an idle machine carries one reader at full speed and busy
   workers fall back to one frame per reader, shared as before. Results are taken in the order of the frames: an older
   frame's result never replaces a newer one. A frame without room is dropped, the newest wins, nothing queues, so the lag is
   bounded by the frames in work. At most 50 readers per server; watching readers by the
   server's cores, 4 per worker, at least 8 and at most 50 ("Limits"); at most 4 watching readers per site (a site node's
   link carries their streams; they take none of the site's large transfer slots). At most 8 page previews per server
   and 2 per user; a preview with a stream of its own counts against the watching limits after the saved readers; a
   preview ends 30 s after the page's last call and never publishes events ("Page").
5. **GPU when there is one, the CPU always works.** With a usable card the models run there unless one is clearly slower ("GPU"), so the CPU stays free. ONNX Runtime and the models come with the add-on; nothing is
   downloaded at run time. A missing or failing GPU, also during a run, falls back to the CPU (one log line); the page says what reads.
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
  - **Watching** (new readers start with it): reads continuously at 1, 2, 4, 8, 12 or 15 frames per second (default 4)
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
    camera place, source, resolution, rate, Moving objects or area place or format starts it again.
  - **On action**: reads one picture when an event rule runs the action **Read text** (below). No stream (the page's
    preview aside, "Page"). Choosing it on the page shows a short message (`ui:MessageWindow`, OK only) once per reader
    and client session: "Reads only on an action", "This reader reads only when an event rule runs the action Read text
    with it." The code's enum is `ReaderMode.OnAction` (JSON `"OnAction"`; named On demand before the first release).
- **Resolution**: default 1920x1080 or the camera's largest below it; small text needs pixels.
- **Single pictures** (the action Read text): `/axis-cgi/jpg/image.cgi` with the same resolution and source.
- No H.264 or H.265: decoding video would need FFmpeg on the server.

### Moving objects

A checkbox per reader in the settings form (decision 2026-11-04, `Reader.MovingObjects`), off by default, shown only for
Watching, its tooltip tells Off and On in full, like the format help (decision 2026-11-04). Additive in JSON: an older record reads as off, and a reader
switched to On action keeps the value without using it (`ReaderRules.IsMoving`). A change of it starts the loop again.

- **No vote.** A value counts once it was read in 2 frames in a row, same value and same area (`Matching/TwoFrames` inside
  `Readers/AreaWatch`). It comes from the second frame: its row, event, cut-out and box all belong to that frame. Frames in a
  row are frames the reader read, so a slower server counts every frame it reaches; at 1 frame per second that is 2 s.
- A value seen once changes nothing, which filters single misreads; an empty area needs 2 frames too. A value that stays is
  one row and one event. A, B, A gives three, each confirmed by 2 frames. A value lost for 2 frames in a row and read again is
  a new read, as in "The last 20 reads".
- Between values the area shows the last confirmed value, without a box when this frame does not hold it. It keeps no frame
  beyond the one of that value.
- **A preview follows the setting**: with it on the preview's own stream uses the same 2 frames rule, on the same stream when
  the draft changes it, and still publishes no events. With it off a preview stays as before, without a vote, each frame's own
  result. A draft with another setting never shares the saved reader's stream. Pause reads the held frame once with the draft,
  so the note shows that frame's own value.
- Sites and rule events work as for any reader; backups keep the field; device groups do not change.

## Reading

1. Decode the JPEG (SkiaSharp), only the part the areas need (each area with a margin of 16 px plus its shorter side, at most 128 px more:
   rows above skipped, rows below never read, columns outside cut by the JPEG decoder; full HD with one small area 2.5
   instead of 16 ms), and cut out each area at full resolution. The page's cut-out of a value is decoded the same way.
2. **Find text**: the PP-OCRv5 detection model per area (mobile for fixed-place areas and on the CPU, server for Search on a graphics card and for
   Accurate),
   sides a multiple of 32, turn its map into line boxes (threshold 0.3, box score 0.6, box enlarged by 1.5; own C#, no
   OpenCV). An area up to 960 px on its long side is read as it is (a small one scaled up to 64 px, a 16 px margin
   around it); a larger one (Search over the whole picture) in tiles at full resolution: the fewest tiles that cover it
   at up to 960 px each with at least 15 % overlap, all of one size and spread evenly (full HD: 3 x 2 = 6 tiles of
   736 x 608). The text finder never gets a smaller or larger picture than this: both lose text (measured: a cap of 640 px reads 6 of 25 moving values with 4 px blur instead of 25; a padded 768 x 768 reads 25 of 31 small labels instead of 31). On the CPU each tile is a model call of its own; with a graphics card an area read in tiles goes to the
   card, its tiles in calls of one size within the card's memory cap ("GPU").
3. **At most 30 text boxes** per reading, over all areas together: the boxes with the highest detection scores are
   read, the others dropped, and the reading carries the note "More than 30 texts found. Only the first 30 are read. Draw
   smaller areas." (`Reading.Note`, `ReaderRules.TooManyTexts`), shown on the page below the video. A busy picture (a
   large area over a shelf of labels) never floods the recognition, and stray text far from the value never wins.
4. **Merge boxes**: boxes found twice (in two tiles) and pieces of one line the text finder split (the digit drums of a
   meter) become one line: same direction, about the same height, on one line, overlapping or closer than 0.6 line
   heights.
5. **Straighten** each box: a slanted line is turned level; a line clearly taller than wide (a receipt or label turned
   by 90 degrees) is turned by 90 degrees; the line keeps this rotated box and its turn for its upright cut-out
   (`TextLine.Turn`, server only). Each line is read first as it is; the angle model looks only at the lines
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

Model calls reuse their buffers (rented, the input pinned for ONNX Runtime, the output read where it lies; the reader's
scores rented too, handed back when the reading ends). On the CPU every line is a model call of its own at its own width
(at least 64 px, no padding to 320: a short line costs a third and the accuracy set reads better), so the lines of one
picture spread over the free workers. On a graphics card calls come in few sizes ("GPU"). The session set of the CPU
workers unused for 2 minutes is freed; stopping the recognizer waits for the readings in work (at most 30 s) before it
frees the models.

There is no Latin server recognition model: PaddleOCR publishes Latin PP-OCRv5 recognition as mobile only, and the
multilingual server model read Latin text worse (no spaces between words). Every device reads with the mobile Latin
model; "the server models" means the server text finder (`plugins/Oadm.Plugins.Ocr/Models/README.md` "Choices").

Languages: Latin script (English, German with umlauts, French, ...) and digits. Other scripts need another recognition
model (later, as a choice per reader).

### Several lines

An area can hold several lines. They are put in rows, so what stands on one line of the text stays on one line, at any
turn of the text: an article and its price on a receipt, a label and its value on a sign. Always on, no setting (decision
2026-11-06). No model and no pixels: arithmetic on the rotated boxes the recognition already has (`TextLine.Turn`), in
pixels of the picture (`Matching/LineOrder`, used by the matcher and by the recognition's line order).

1. **Blocks by direction.** The longest line sets a direction; every line within 15 degrees of it, either way round, joins
   its block. A short line (shorter than 1.5 times its height) has no reliable direction and joins the first block. The
   next line left over starts the next block, so two receipts at different angles stay apart. Blocks are read top to
   bottom in the picture, blocks side by side (they overlap top to bottom by half of the smaller one) left to right; the
   order never depends on which block holds the longest line.
2. **The block's axis.** The length-weighted mean of the directions with the angles doubled (a line has no front), of the
   lines long enough to have a direction. Its side is the way most of the text length reads, so a line of digits read the
   wrong way round does not turn the block. One axis per block, never a line's own direction: 2 degrees are 17 px at
   500 px, more than half a line height, and a wide table would fall apart.
3. **Rows.** Two lines share a row when their centres lie, across the axis, within 0.55 of the smaller line's height, or
   when the smaller one lies on the larger one's height with at most a quarter of its own height outside and stands next
   to it, at most the larger height away along the axis: a small caption on the baseline of a large value ("Nr. 4711"), a
   raised small number. The second only adds lines more than 1.6 times as high as their neighbour. Measured against the
   table below: a looser overlap rule for all lines joined far prices with the wrong row (28/32 on the wide table), and
   without "next to it" one high box found by mistake joined the rows beside it (38/40); as built the table is unchanged. Rows
   are the connected groups of that relation (at most 30 lines, so at most 435 pairs). No limit on the gap inside a row: a
   table with its prices far right is what this is for.
4. **Order.** Rows by their mean place across the axis, the lines of a row along it.
5. **Found twice.** Two lines that lie on one row by themselves (rule 3 between the two, not only through a third line)
   and overlap along the axis by more than half of the shorter one are one text (the tile overlap gives `5.80 5.80` or
   `5.80 .80`): the narrower one is left out of the values. Its box stays (one box per line found), and a format still
   looks at it: a tile's cut piece can be the wider one and the whole value the narrower.

A line without `Turn` has only its level box: then every line of the area uses its level box (pixels and shares of the
picture never mix).

| Value | Holds | Used by |
|---|---|---|
| `Text` | one line: the lines in the order of the rows, joined with a space, a line ending in "-" without one ("DECAF-" and "FEINATO" give "DECAF-FEINATO"); at most 500 characters | rule checks, the change check, the vote, `Number` |
| `Text read` | one row per line, the lines of a row joined with a space; at most 500 characters, never ending in a line break | messages, the results table |

- **`Text` never holds a line break** (decision 2026-11-06). An `equals` on a text of two rows would break, and a row that
  splits in one frame would count as a new value and fire an event. So a rule's "contains" finds a word on any row.
- **No new event value** (decision 2026-11-06). With a format `Text` is the match and `Text read` the whole area in rows:
  no value holds the whole area on one line then.
- `AreaResult.TextRead` is the source (`TextMatch.RawText`); `TextReadEvents.Data` hands it on as it is.
  `TextReadEvents.Join` makes one line of it for `Number` and for the log line of the action Read text.
- A voted value takes `Text read` from the reading that stands for the vote (`FrameVote.Current`), so where the vote
  changed a character the two differ by that character. Accepted.
- A rule on `Text read` with `equals`, or with `contains` across rows, does not match; `contains` inside a row does. A
  rule that sends `{textRead}` sends line breaks: MQTT text, HTTP plain, the email text, Modbus and OPC UA text.
- **Cost** (`Matching/LineOrderSpeedTests`, Perf, a Debug build on 24 cores): 30 lines, the most a reading has, take about
  25 µs for the rows and about 40 µs and 9.6 KB for the whole value; at 15 frames per second 0.6 ms a second, about 0.15 %
  of a small area's reading. Unchanged areas run no model and stay cheap.
- **Measured** (`Recognition/RowsSweepTests`, Perf, 2026-11-06: receipts of the accuracy set's generator, Search over the
  whole full HD picture, Any text, the CPU). "Both read" is what the recognition delivered: rows can reach no more.

  | Test | Both read | In the right row | Before rows: the price straight after its article |
  |---|---|---|---|
  | Every 15 degrees, scale 0.8 | 80/96 | 80/96 | 6/96 |
  | Every 15 degrees, scale 1.3 | 80/96 | 80/96 | 10/96 |
  | Up to 10 degrees from level or upside down | 40/40 | 40/40 | 16/40 |
  | Wide table, the price 25 characters away, 8 turns | 29/32 | 29/32 | 3/32 |
  | Tilted to the side by 20 degrees | 16/16 | 16/16 | 7/16 |
  | Tilted from the top by 50 degrees | 16/16 | 16/16 | 8/16 |
  | Tilted to the side by 50 degrees | 16/16 | 16/16 | 7/16 |
  | Tilted both ways by 40 degrees, turned 30 degrees | 6/8 | 6/8 | 0/8 |
  | The 20 receipts of the accuracy set | 48/80 | 47/80 | 15/80 |

  The rows found came in the receipt's order in every picture. The one miss is a damaged receipt whose row took a
  second price. A real-model test pins a receipt at 0, 90, 180, 270 and 15 degrees (`Recognition/RowsRecognitionTests`).
  The accuracy set reads as before rows: 90.7 % exact with a format, 91.7 % any text, the same misses.
- **Limits.** Two objects side by side at the same angle: their rows mix (one area per object; different angles stay
  apart). Under a strong tilt a row can split in two and the fan of directions can split a text into two blocks. What the
  recognition loses stays lost: two close lines merge at about 45 to 60 degrees ("Why it breaks"); rows do not repair that.

- **Where the line breaks of `Text read` go** (checked in the code, 2026-11-06). Two rules: a value never fakes a log
  line, and a value never adds a line at a TCP receiver, where a line is often a command.

  | Output | What happens to a line break |
  |---|---|
  | Event log and server log | One line per entry: the host turns line breaks into a space in every action's log lines and failure text (`ActionRunner`, [event-rules.md](event-rules.md)); the action Read text logs through `TextReadEvents.Join`; Log entry writes one line |
  | Email | The subject is one line, the text body keeps the rows, an HTML body shows one line unless the template uses `<pre>` |
  | HTTP | A header gets a space; URL and form are encoded, JSON and XML escaped; a plain body keeps the rows |
  | MQTT | A JSON payload is escaped, a text payload keeps the rows; in the topic the value is refused ("cannot go in a topic", nothing sent) |
  | Send TCP message | Text and XML get a space (`PlaceholderEscape.SingleLine`), JSON is escaped: never a second line |
  | VAPIX command | Path, query and form are encoded, JSON escaped, a header is refused; a text or XML body takes the break as it is (open point below) |
  | Modbus and OPC UA text | The break is written as it is; Modbus refuses a value over the set length |
  | Dashboard | The table shows one line (a break becomes a space, cut at 100 characters); a Text widget shows the rows |

- **Open before a release.** Real pictures from the cameras: every number above is from generated receipts. The text body
  of a VAPIX command: accept the line break (the command's author sets the field's pattern) or fill it with
  `SingleLine`; not decided. Whether a mean of the axis per neighbourhood gets strongly tilted rows without losing the
  wide table (not measured).

- **One area per value**, never line numbers: lines are found anew in each picture and their count changes with light
  and focus, so "line 2" would point at another line. The page's empty area list says "Draw one area around each text
  you want to read."
- An area full of text (a sign, a label) is read whole and fits rules that look for a word (contains). Reading
  documents or long paragraphs is not the purpose.
- Vertical text with one character per line (container doors) is not read in this version.

### Expected text

Per area: **Any text** (default) or **Format**. The page has one field: empty (the default) reads any text, anything else
is a format (decision 2026-11-04, no select; `*` keeps its one meaning in a format).

- Format: `#` a digit, `@` a letter (decision 2026-11-04, `A` until then: every letter now means itself), `*` a letter or digit; every other character must appear as written. A `-` must be a `-` in the text, with spaces
  around it allowed (decision 2026-11-04, a barcode line fit `@@-####-##` until then: `CX 1152 7244 0 AT` gave `CX-1152-72`); a space
  may be missing in the text. Example: container number `@@@U ###### #`, wagon `## ## #### ###-#`,
  counter `#####`. A match never starts or ends inside a run of letters and digits; a format that starts or ends with a
  sign (`(###)`) may touch them.
- **Container numbers** (`Matching/ContainerCheck`, the one rule of the matcher and the format-guided decoding): a
  format whose first 11 letters and digits are `@@@`, the equipment category and seven `#` checks the 11th as the ISO
  6346 check digit of the 10 before it. The category is U (freight container), J (detachable equipment) or Z (trailer or
  chassis), in either case like every letter of a format (decision 2026-11-04: `@@@Z` and `@@@u` checked nothing until
  then), or `?` for any of the three (`@@@? ###### #`, decision 2026-11-04: an area with containers and chassis; the value has
  the letter read; a `?` anywhere else is a plain character); `@@@@ ###### #` stays unchecked; what follows (`@@@U ###### # 22G1` with the size and type code) is not checked; `@@@U ######` has no
  check digit and checks nothing. A wrong check digit is no match: no character is ever changed to make it fit
  (decision 2026-11-03; a misread check digit never becomes a sure value).
- **Format-guided reading never makes a dash up or leaves one out**: the search may pass a dash of the format by, but such a match is
  dropped and the line keeps its free reading; a match with dashes must hold exactly the dashes the reader's own best reading has
  in its frames (`LineReader.DashesAsRead`).
  Measured below ("Measured: dashes").
- With a format the reader looks for matches in each line on its own, then in neighbouring lines joined (for a number
  split over two lines). The match fixes look-alike characters by position: where a digit is expected O becomes 0, I
  and L 1, S 5, B 8, Z 2, G 6; where a letter is expected the reverse.
- Several matches (the same order number on two receipts, two parcels): the area's value is the match read with the
  highest confidence, on a tie the larger text; the event field All matches lists every different match, at most 10.
- The page shows the value's cut-out picture next to it (in memory only, like the frames; "The last 20 reads").
- No match: the area's value is empty and **Matches format** is No; the raw text stays in the event's **Text read**, the page
  shows neither a box nor the raw text (decision 2026-11-04).
- **Any text** has no format to match: Matches format is always No (decision 2026-11-03: a rule "Matches format is
  Yes" fires only for a format that matched; an area with Any text fires only with text).
- The page's format field has the format help as its tooltip.
- **Use read text as format** (area's ⋯ menu, decision 2026-11-04; off without a read text in that area or the change
  right): the format field takes the shape of the text read now, digits `#`, letters `@`, the rest as written
  (`Matching/TextFormat.FromText`); a container number with a fitting check digit (category U, J or Z) gets `@@@? ###### #`, so its format checks it (decision 2026-11-04, a misread stays `@@@@`), and a short message says so ("Check this format", OK only: the format then refuses other text). The user then adjusts it and saves.

### Runtime safety

- Every sync of the readers' loops (a save, a delete, Use GPU, a device group's change, the 1-minute check)
  reads the stored readers under the runtime's lock, never a list taken before it: an older list never wins, a deleted
  reader never streams or fires again.
- A stop cancels the single readings of the action Read text and waits for them before the recognition is disposed; a
  reading asked for afterwards ends "The Text recognition plugin is not running." Timer and event work never throws (a
  failure is logged); the runtime's locks are never disposed under late callbacks.
- Cut-outs are made once per new value ("The last 20 reads"), never per frame, and once per reading of a held frame or of
  `getReading`. They reach only a user with the host's **Live view** (`HostPermissionKeys.DevicesLiveView`; they are pieces
  of the camera's picture). The page gets the picture itself only on Pause ("Pause"), under the same rule; else it shows the
  live video.
- The event `reading` reaches the pages only when a reader's values or note change, else at most once a second per reader;
  a preview's readings go only to the user who started it.
- A reading cancelled on the graphics card ends cancelled, never as a reading with blank lines: nothing keeps it or fires it,
  the unchanged areas neither.
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
  fields: Area (choice of the reader's areas), Text (the value on one line, never a line break: the match with a format,
  else all lines joined with a space), Text read (the whole area in rows, one row per line, "Several lines"), All matches (every different match of the format, separated by a space; empty without a format),
  Number (the value as a number when it is one, "." or "," as the decimal sign, e.g. a meter reading; written with "."
  without leading zeros or trailing decimal zeros, every digit kept, never through a floating point number,
  `Matching/NumberText`; empty otherwise), Confidence (number, 0 to 100), Matches format (Yes / No). Conditions check them with the field checks
  of `docs/spec/event-rules.md` (equals, contains, starts with; greater or less than for Number and Confidence). The type
  declares the reader's camera as its fixed device (`FixedDeviceId`) and Area as its split field (`SplitField`): on a
  dashboard the event is chosen per reader, the widget takes the camera by itself and shows the area chosen in its Area
  select ([bundled-plugins.md](bundled-plugins.md#dashboards-core-plugin)). No text read is no event there: it blanks the area's Text read values (`DashboardsPlugin.ClearedText`).
- Event **Reader problem** per reader (same folder, stateful, on the reader's camera, decision 2026-11-04, issue #33): on at once
  while the reader's chip on the page is red (`ReaderRuntime.ProblemOf`), off when it reads again; no waiting time. Values:
  Reader (no check), Problem (the server's full text, the chip's tooltip). **Any reader problem** (folder Text recognition,
  stateful, server, for users with all devices): on while at least one reader has a problem, values Readers (the count) and
  Reader (the names); a new reader needs no rule of its own. `ReaderProblemReporter` (`Events/ReaderProblemEvents.cs`) sends each type one full report, then
  changes; a new text while on updates the values without an edge. `ReaderRules.Starting` is no problem, the type waits
  until the graphics card's test ended. A watching reader whose recognition fails 3 times in a row (`OcrLimits.FailedReadingsForProblem`, a
  single bad frame is none) has a problem until a reading works; one without its models has "not installed" (`ReaderRules.NotInstalled`).
  An On action reader has no stream, so a failed action Read text is its problem too until a reading works or its picture or mode changes
  (`Already reading` and a deleted reader are none; a run cut by the rule's 30 s is "timed out", not while the card's test holds it), and
  the chip shows it. Problems live in memory, so after a restart a watching reader that is still broken goes off, then on again
  (decision 2026-11-04), a save that restarts its stream too; an On action reader stays off until its next failed action. A plugin that is not loaded sends nothing: that is the host's event
  Optional plugin not loaded (`docs/spec/add-ons.md`).
- **Watching** publishes an area's event only when its value changes, and only once the vote of the last 5 frames ("Accuracy") gives the same value in 2 frames
  in a row (no flicker between two readings of one picture); with Moving objects once the value itself was read in 2 frames in a row ("Moving objects"). No text at all is never an empty Text read, for watching readers and the action alike (decision 2026-11-05: an empty event ran rules and its dead time held back the real read after it): the area fires the reader's own stateless event **No text read** (`oadm.ocr.no-text.<reader id>`, value Area, same tree folder), once when a watching area's value goes empty, whatever raw text the frame still holds (decision 2026-11-05; an area that has held no value stays quiet), and for every area with no text on each action, a watching reader's too. Text that does not match the format: a watching reader fires nothing for it; the action fires Text read with the raw text in Text read and Matches format No.
- Rule action **Read text** (`IActionPlugin`): parameter the reader (On action or Watching; a rule without one is refused
  at its save: "Choose a reader."). Reads one picture now and publishes Text read of every area with text and No text read of the others, changed or not (the event log line, always one line, gives the raw text on a mismatch, "no text" for an empty area); a reader
  without an area ends "The reader has no area. Draw one on the Text recognition page." and reads nothing. A watching
  reader answers with its own reading of at most 1 s ago (`ReaderRuntime.FreshReading`, the voted values), else with the
  reading its stream has in work once it ends, else with a picture of its own: its stream reads all the time, so the
  action never ends "Already reading" because of it. Another single reading already running for an On action reader:
  the action ends "Already reading" and nothing is queued. Loops (a Text read event that runs Read text of the same
  reader) are held by the rule limits (dead time, maximum per minute).
- A rule's dead time counts per camera (`RuleEngine.Limit`), so it also skips another area or the next value. That stays
  (decision 2026-11-04, issue #32): such a rule sets Dead time 0, the docs say so; a watching reader never repeats a value
  and Max per minute still holds.

## Page

- Three fixed columns, four cards: readers (fixed 320 px) | live video above results (the rest) | settings (fixed 320 px, the
  narrowest where the form, the format field and the areas fit; labels above their fields). The columns have no splitter;
  the video and results cards get every pixel the two fixed columns leave.
- **The resizer** (decision 2026-11-04): the shared `GridSplitter.paneSplitter` between the video card and the results card, as
  their 16 px gap. Dragging it up gives the table more height. At first the video card is as large as the picture wants, while
  the results keep 280 px (`ResultsMinHeight`). A drag stops at 30 % of the column's height for the video card
  (`VideoMinShare`) and at 140 px for the results card (`ResultsDragMinHeight`: the table's header and one row). `OcrView.LimitsOf`
  sets the limits when the column changes; when they do not all fit, in this order: the cards never overflow the
  column, the video keeps its 30 %, the results their minimum, the video what its card holds (padding, the video surface's
  120 px). The client's window minimum (640 px) leaves room for all of them. The position lasts as long as the page's view, one
  connection (the page keeps no client settings), for every reader.
- Left: the readers (name, camera, mode, last text), **New**, **Delete** (red, with a confirmation) and a search; below them
  the rest of its height for the list. "Runs on" and Use GPU sit in the page header right of the load
  (decision 2026-11-04); the "Runs on" text is the server's.
  - The reader's chip: "Watching · 2 fps" (green), "Watching · 8 fps (reads 5.1)" (orange, slower than set), "On action",
    or a short red chip per kind of problem with the server's full text as its tooltip (`OcrTexts.ProblemChip`, one
    phrase per concept): Limit reached (the watching limits, also per site), Credentials required (the stored login is refused, no
    password, Basic only, HTTP 401 and 403), Certificate changed and Site offline (the device list's names), Unreachable
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
    fullscreen, quality switch or controls: nothing sits under the areas or leaves them behind; the one exception is the
    Pause button in the picture's corner, decision 2026-11-04, "Pause"). Over it
    (`ReadingPicture`) the areas (dashed grey, the selected one solid in the accent, a press beside every area clears the selection, decision 2026-11-04; name tag) and the found lines of the latest reading
    (teal boxes with their text; with a format one box, the value at its place, none without a match or for a voted value this frame does not hold, also while paused, decision 2026-11-04), placed on the reader's resolution centered in the video's space: the same letterboxing
    as the video's picture (Stretch Uniform). The video shows the camera's own picture shape; a resolution of another
    shape than the video source (4:3 on a 16:9 camera) draws the areas on the reader's shape. Without the host's live
    video (an older OADM) the overlay sits on the dark video surface. Nothing in the video card changes height: the video never
    moves when a note comes or goes, and the Pause button does not jump (decision 2026-11-04).
  - **Below it, in a card of its own: the results grid** (Time, Area, Text with the cut-out, Confidence right-aligned, Matches
    format): the last 20 reads, newest on top ("The last 20 reads"), in the rest of the column's height (on a wide screen the
    video shrinks and keeps its shape, the results stay on the page); while there are none "Waiting for the first picture."
    or why the preview reads nothing, while paused "No reads yet." A row shows the value's cut-out only when the server sent
    one (users with Live view); without one, or one that does not decode, the text alone. A read without a format shows its
    rows as lines ("Several lines"); the cell wraps and the row grows.
  - **No area yet**: the video is dimmed a little (the theme's video dim) with "Start by drawing an area" in its middle;
    nothing is read (the preview has no stream), no lines, no results. New in the Areas header stays the way to start;
    while drawing the drawing hint shows instead.
  - **Right, a grid of four rows**: the form (Name, Mode with Watching and On action, **Stream**, Moving objects), the Areas
    header, the area rows (the only part that scrolls: with many areas New above and Save below stay in reach), then the
    reading's note and below it the save status left of **Save**. No camera field (the camera is the reader's for good, "Readers").
  - **The note** (decision 2026-11-04: below the video it moved the picture and on a belt it flickered too fast to read): a warning
    chip directly above the Save row, its lines as needed, the area rows give it their height. A live alert ("More than 30 texts
    found. ...") stays `OcrLimits.NoteHold` (5 s) after the last reading that had it (`PreviewTime`, the page's `TimeProvider`).
    "Lane: Text too small. ..." shows live only when that area had no value for `OcrLimits.SmallTextWait` (10 s, counted from
    the preview's first reading of the area) and small text was seen within that time (`AreaDetails.SmallText`; decision
    2026-11-04: small sender addresses beside a value that reads say nothing), and goes with the next value. With a format a
    value is a match; with Any text anything readable counts. A new alert joins the others, each ends by its own time, so the
    text changes only when an alert comes or goes. While paused it holds the frame's own lines at once, without a hold ("Text
    too small": `AreaDetails.TooSmall`, small text and nothing else read in that frame). It goes with the reader and the frame.
  - **Stream**: a secondary button (video icon) that opens a small dialog **Stream settings** (single operation: no card,
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
    area in one row (decision 2026-11-04): the name as plain text (trimmed, the whole name as tooltip; a refusal of the
    name or the box below it, each under its own key: a rename never clears the box's, a move or resize does), the
    format field and the ⋯ menu (`ui:RowActions`: Use read text as format, Rename, then Delete in red, decision 2026-11-04): empty for any text ("Any text" as the placeholder),
    otherwise the format. Its tooltip is the whole format help with examples (decision 2026-11-04, longer than other
    tooltips on purpose); errors directly below the field. **Rename** opens a small dialog
    (single operation, one Name field: required, unique in the reader, at most 64 characters, the error below it; Rename
    and Cancel). The rows are a list (`ListBox.areaRows`): the selected area's name is in the accent colour, without a fill (decision
    2026-11-04), and selecting a row (or a field in
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
- **Live preview**: while the page shows a reader (the page is visible), the server reads it live as a
  preview in both modes, at the reader's rate (On action: 1 per second), so the results follow while areas are set up.
  The page sends its draft as it is (saved or not, a new reader too): on every change and every 10 s as the keep-alive
  (`startPreview`); the server takes new areas without opening the stream again, a new camera, source, resolution or
  rate opens it again. A preview publishes **no** events: the rule events come from Watching, the action and Read now. **Read now** (decision 2026-11-05; the zap icon right of the Mode select, only for a saved On action reader whose draft is On action too and with "Change text readers", `OcrMethods.ReadNow`, audited) reads one picture and fires the real Text read and No text read events like the action, so the rules on the reader run; a watching reader is refused.
  - The draft is read as far as it can be (`ReaderRules.PreviewOf`): camera and resolution must be right; areas outside
    the picture are left out, a missing area name reads as "Area 2", a format with a problem reads any text, a frame rate
    not offered the default.
  - A saved watching reader whose draft is unchanged shares the reader's stream (its readings are the preview's); any
    other preview has its own stream through the watching loop with the vote and the events off, and shows each frame's
    own result; with Moving objects its values follow the 2 frames rule ("Moving objects").
  - Limits: at most 8 previews per server ("At most 8 readers can be shown live at once. Try again when another page
    closes.") and 2 per user (a third ends that user's oldest: a page closed without a word); a preview's own stream
    counts with the watching readers against the watching limit ("Limits") and 4 per site, after them: a saved reader
    that starts watching takes the stream of the youngest preview; beyond: "No live results now: OADM already reads as
    many cameras as it can." The texts show where the results are. A preview's own stream that gives no pictures says
    why in its `problem`, like a reader.
  - Another camera under the same id is a new preview: nothing of the old one stays (its reading, frame and rows).
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
- **Pause** (decision 2026-11-04, the running belt: a format cannot be tuned against a moving picture). The server keeps the
  frame: the client's video and the frames the server reads are different pictures, so the lines would not fit a frozen
  video.
  - The button: one icon-only button in the bottom left corner of the picture, inside the letterboxing, 10 px from its
    edges (`PicturePlace` on the rectangle of `ReadingPicture`), tooltip Pause or Resume, on the video's own backing
    (`Border.videoOverlay`, like the live video's buttons). It is the one thing over the areas: only its own rectangle is
    covered, a press on it never draws, selects or moves an area, it never takes the keyboard focus (Esc and the arrow keys
    stay with the picture, and it cannot be reached with Tab). A drag that started beside it passes over it; an area
    under it is chosen on its row and moved with the arrow keys. Next to it the chip Paused (tooltip "Saved readers keep
    reading.") and a ring while a Pause, a Resume or a reading of the held frame is on its way.
  - Off until this page's preview has a reading; not there without the host's **Live view**.
  - Pause holds the frame of the preview's latest reading: the page shows that exact picture in the video's place, drawn on
    the rectangle of the overlay, with its areas and lines, and the live video is hidden (it stops streaming).
  - **The table stays the last 20 reads, frozen** (decision 2026-11-04: pause and look whether the readings were OK): from the
    press on Pause until Resume no reply changes it, also not one with newer rows, so no row arrives or moves, and a click on a
    crop still opens it. A reply on its way around a Pause or Resume never empties it. Resume brings it up to date with the next
    fetch; rows that stay keep their pictures. A re-read of the held frame never adds a row.
  - **The held frame's result** is a list of its own (`ReaderEditorViewModel.HeldAreas`, one per area by its place in the reader,
    names may repeat while they are edited), never in the table. Use read text as format takes the area's text of the current
    frame, live and paused, never a row. While paused the note above Save shows it, one line per area with a format
    ("Area 1: DE-1011-22", "Area 1: no match"), after the reading's own note and "Text too small"; the lines go with the frame.
  - While paused the draft re-reads the **same frame**: a changed format, a moved, resized, new or removed area. The
    note and the lines follow. Use read text as format works on it, Save too (the pause stays). A frame of a shared
    stream carries the vote's values, and a draft changed since it was read no longer fits: such a frame, and a frame of a preview
    with Moving objects, is read once with the draft on Pause.
  - One worker per held frame, latest wins: a reading in work ends, then the newest draft is read; a burst of edits never
    queues readings, a reading of an older draft is never shown. It goes through the same recognition workers and counts in
    the stats. Every area is read: no reuse (`reuseSource` null), the pixels never change, so "unchanged" would answer with
    the old format's reading.
  - Preview only: a saved watching reader keeps its stream, its vote and its events; its readings no longer reach the
    paused preview. A preview publishes no rule events, paused or not.
  - A paused preview's own stream stops and a shared one is left; Resume opens or shares it again, within the limits of the
    moment. It stays one of the 8 previews and 2 per user, needs the keep-alive and ends with the lease.
  - The frame is the reading's bytes, not a copy, within the limits of a reading: at most 10 MB and the pixel limits of
    `Recognition/Picture` (`Picture.Fits`, refused with "This picture is too large to pause. Choose a lower resolution.").
  - It goes on Resume, on `stopPreview`, when the lease ends, when another camera, video source or resolution is sent
    (name, mode and rate keep it), when the reader is deleted, when the camera leaves the owner's devices and when the
    user no longer has Live view (the next keep-alive). The reading that keeps its bytes goes with it. The page leaves the
    pause when `startPreview` answers `paused: false`.
  - An offline camera does not end a pause: the frame needs no camera.
- Empty page: "Create a reader to read text from a camera."
- **The last 20 reads** (decision 2026-11-04): the results grid shows what the reader read lately, so a value that went by
  can be looked at with its picture.
  - The server keeps per saved reader the last `OcrLimits.RecentReads` (20) reads in memory only (`Readers/RecentReadList`), the
    oldest drops first. Nothing is ever on disk: gone on a server restart, with a deleted reader and when the plugin turns off.
  - **A read is a new value in an area**, not a frame: a row is added when an area's value differs from that area's last value
    and is not empty (with a format only a match, with any text the text read). A value that goes A, B, A gives three rows, A,
    empty, A two. On a Watching reader that is the moment of its Text read event, after the vote, so the list fills with the page
    closed. The action Read text adds its values by the same rule: the same value twice is one row. A change of the reader's
    picture (`Forget`) keeps the rows; each area's next value is a new one.
  - **The row's text**: the value. The change check works on `Text`, so rows that split or join add no row. Without a format
    the row holds the area's rows with line breaks (`Text read`, at most 500 characters) when they are the value, spaces
    and line breaks aside (`ReaderRuntime.SameText`); where the vote changed a character it holds the value on one line.
  - **A preview** of an unsaved or changed draft has no events: its own stream's new values go into a list of that preview (one
    per preview, no vote, with Moving objects by the 2 frames rule), which ends with the preview. A preview that shares a saved watching reader shows that reader's
    list. The page of a saved On action reader whose draft is unchanged shows the preview's list and the reader's together, newest
    first: the action's reads would else never show, since the page's preview always reads.
  - **Cut-outs**: every row keeps its value's cut-out, also the old ones, at most `OcrLimits.MaxCutoutSide` (640 px) on its longer
    side (a larger one scaled down) and `OcrLimits.MaxCutoutBytes` (64 KB): lower JPEG quality, then half the size, a longer one
    is not kept. So a list holds at most 1.3 MB of pictures. Cut from the frame the value was read in and at its box, never the
    whole area for a voted value: each candidate of the vote remembers its frame, its box and its turn (`ValueFrame`, a value the
    vote put together those of the newest reading of its length), and a reply of Read text that answers with a voted value
    uses them too (`ReaderReading.Sources`). A row's cut-out, thumbnail and the dialog show the text upright, the way the reader
    saw it: the line's rotated box with its 90 degree and upside down turns (`LineTurn`) and a margin of a quarter of its height;
    a value joined over two lines or several lines of any text have no one direction and stay level. A line that cuts wrongly
    falls back to the level cut-out, never to none. Perspective is not undone: the line cut has only the rotated box. `TextLine.Box`
    stays level for the overlay. One worker cuts the rows, a row per list in
    turn, newest first (`ReaderRuntime.Recent`): the frame's loop never waits for it, a row holds its frame only until it is cut,
    and a row that drops first is never cut. A reply cuts what the worker has not reached.
  - Per frame: comparing the values allocates nothing, a value that did not change costs no row, no picture, no call.
  - **The calls**: the rows come with the page's reading calls (`recent` in the replies of `getReading` and `getPreview`), no
    polling of their own, with the same device group and permission checks; a reply holds only the rows of that reader or
    preview, at most 20, with at most 500 characters of text each. A row's cut-out comes once: the page names what it has
    (`knownCutouts`, ids unique on the server and never repeated by a new start) and those rows come without it. Live the reading's
    own cut-outs are not cut. While paused `recent` is empty and the page keeps its rows.
  - **The grid**: one `ReplaceAll` when the rows changed, nothing when they did not. A row stays by its id with its decoded
    thumbnail, so 20 rows at 15 frames per second decode only what is new; a row that leaves disposes its bitmap. Times with
    seconds.
  - **Open a cut-out**: a click on a thumbnail opens a dialog (no card, `CutOutWindow`, Close): the picture at its real pixels, a
    small one enlarged by a whole number (at most 8 times, up to 640 x 360 px), without smoothing so characters can be judged,
    with the text, the area and the time. It decodes its own copy: the row may leave while it is open.
  - **Text too small** has no row of its own: the areas it applies to show in the reading's note, by the rule of the note above.
- One contract: the page uses the server part's payloads (`Api/`) and the texts of `ReaderRules`, never copies. The
  camera's video sources and sizes (largest first) come from `getCameraOptions` (read only, the caller's devices, kept
  10 min per camera). A save refused by a check answers `{"errors":[{"field","message"}]}` with stable field keys
  (`name`, `camera`, `videoSource`, `resolution`, `mode`, `fps`, `areas`, `areas[i].name|box|format`); the page shows each
  below its field: the watching limits below Mode, the video source, the resolution and the frame rate below Stream, `areas` below the area rows, an area's name and box below its name, its format below the format. Refusals without a field on the page (the camera, reader gone, 50 readers) stay errors in the host's message.
- The preview's calls (permission "View text recognition"):
  - `startPreview` `{"previewId":"9f3c...","reader":{...the draft...}}` → `{"previewId":"9f3c...","problem":"..."}` (no
    `problem` while it reads); with a known `previewId` of the caller it keeps that preview alive and takes the
    draft, an unknown or ended one starts a new preview. Refusals: the 8 previews, a camera outside the caller's
    devices, another scope's reader.
  - `stopPreview` `{"previewId":"9f3c..."}` → `{}`.
  - `getPreview` `{"previewId":"9f3c...","knownCutouts":[17]}` → `{"reading":{...},"details":[...],"recent":[{"id":18,"time":"...","area":"Gate","text":"MSKU 123456 7","confidence":96,"hasFormat":true,"matchesFormat":true,"cutout":"<JPEG, base64>"}]}`,
    the last reads newest first, each cut-out once; the reading's own cut-outs only while paused; Live view only, no picture
    (`{}` for an ended preview).
  - `pausePreview` `{"previewId":"9f3c..."}` → `{"paused":true,"picture":"<JPEG, base64>","reading":{...},"details":[...]}`
    with the cut-outs; `{"problem":"No picture to pause yet."}` without a reading, `{}` for an ended preview or another
    user's. Also needs the host's Live view (refused otherwise, fail closed) and the camera in the caller's devices.
  - `resumePreview` `{"previewId":"9f3c..."}` → as `startPreview`: drops the frame and reads live again.
  - `startPreview` and `pausePreview` also answer `rereading` (a reading of the held frame follows as the event) and
    `heldRead`, `startPreview` also `paused`. The ring shows from `rereading` until that reading's event. Each reading of a
    held frame has its number in the event (`heldRead`); an answer whose number is lower than an event's was overtaken and
    turns no ring on. A draft the server did not take turns it off.
  - Event `preview` `{"previewId":"9f3c...","heldRead":0,"reading":{...},"details":[...]}` after each reading of a preview (without
    the cut-outs), only to the user who started it (`PluginEventAudience.User`); the page fetches with `getPreview` only
    its own, one call at a time (newest wins).
  - `getReading` `{"readerId":"...","knownCutouts":[17]}` → `{"reading":{...},"details":[...],"recent":[...]}`: the reader's
    last reading and last reads with the cut-outs (Live view only), never the picture.

## Runtime stats

The page shows whether the recognition keeps up at the right end of its header (`ui:PageHeader.Trailing`: the state chip
in one line and the load bar, decision 2026-11-04), live every 2 s while the page is open. Measured on the server, cheap: each reading adds to counters (no allocation per picture); every 2 s a sample takes
the counters (`Readers/ReaderStats`); every figure is the newest sample against the oldest of the last 10 s (shorter right
after the start). All time through the plugin's `TimeProvider`.

- **Traffic light** (`ui:StatusChip` ok, warning, error; `ReaderRules` texts):
  - green "All readers keep up": every watching reader reaches at least 90 % of its frame rate and the load is below 75 %;
  - orange "Busy: 2 readers read slower than set" ("Busy: 1 reader reads slower than set") when a reader reads below
    90 % of its rate; "Busy: recognition is at 82 %" when only the load is 75 % or more;
  - red "Overloaded: lower the frames per second or the number of readers" only when the machine has no room left: the load
    stayed at 95 % or more for 30 s (every sample from the first at 95 % on) and a reader reads slower than set, or no reader is judged. A reader far below its rate with room left is orange (decision 2026-11-04: "Overloaded" at 16 % load was wrong); a reader that keeps up at that load uses the room it found: orange.
  - A reader is judged only once it watched for the whole 10 s (a reader that just started is never orange).
- While the graphics card's test of a start runs the traffic light says "Starting: testing the graphics card." (`LoadState.Starting`,
  no colour) and the bar moves without a number.
- **Recognition load** (bar with %): the share of the last 10 s the recognition was busy: the sum of the time every model
  call held its worker (`IRecognizerLoad.BusyTime`, `Recognition/BusyMeter`) divided by the window times the capacity
  (`IRecognizerLoad.Capacity`, the CPU workers `max(1, cores / 2)`), each CPU call counted while it holds its slot. With a
  graphics card the busy time grows per sample by the larger of the
  workers' time and the busier card lane's time times the workers (`Recognition/CombinedBusy`), a reader call counted by how full it is (blank places take the next lines at no cost): the load is the larger of the CPU's
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

The limits follow the CPU on every
server, the traffic light ("Runtime stats") is the real guard.

- **Frames per second**: 1, 2, 4, 8, 12 or 15 for every reader on every server (`OcrLimits.FpsChoices`; default 4, decision 2026-11-05: the vote needs about 4 pictures, so a text change shows after about 1 s). A
  small fixed area costs a worker about 28 ms (35 pictures per second), an unchanged one about 5 ms, so one reader at 15 is
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

- **View text recognition**: the page, the readers and their results. Pause needs the host's **Live view** too.
- **Change text readers**: New, Delete, Save, Read now, moving and drawing areas, Use GPU (with all devices).
- Running the action is part of the event rules' own permissions.

## Accuracy

The goal is to read better than the commercial readers (VAXTOR Generic Reader as the benchmark): the right value more
often, from worse pictures, and never a wrong value reported as sure.

- **Format-guided reading**: with a format, the recognition's character choice is limited per position (digits where
  `#`, letters where `@`) while decoding, not fixed afterwards; the look-alike table stays as a last step. A check
  digit is checked where the format has one (ISO 6346 for container numbers, "Expected text"); a wrong check digit is no
  match, never repaired by changing a character (the decoder once changed one character to make it fit; dropped
  2026-11-03, the accuracy set kept its baseline).
- **Several frames vote** (Watching, unless Moving objects is on): per area the last 5 readings are combined character by character, weighted by
  confidence; the value is published once it holds: the vote must give the same value in 2 frames in a row. With a
  format the combined value must be a whole match of it, check digit included (`FrameVote.AcceptorOf`): characters of
  different readings that together break it are never published; the whole reading with the most weight wins instead.
  A single bad frame (motion blur, a hand in front) changes nothing. Each candidate remembers the frame it was read in and its
  box (a reference; at most the 5 frames of the window per area, shared between the areas of a frame), so the row of a voted value
  always shows that value.
- **Small text is enlarged** before reading: a line lower than 24 px is scaled up (bicubic) to the model's 48 px; lines
  below 10 px are left out with "Text too small. Draw a smaller area or choose a higher resolution." on the page.
- **Two text finders**: the PP-OCRv5 mobile text finder on fixed-place areas and on the CPU, the server text finder (finds small text the mobile
  one misses, about 6 times the work) for Search on a GPU; one Latin reader everywhere. "Accurate" (the server text finder on the
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

- **The start** (decision 2026-11-04): the plugin's start never waits for the card. With **Use GPU** on the card's test runs in the
  background (`IEngineInfo.SetUseGpu` returns at once, its task ends with the test) and **the readers wait for it**, then start
  where they stay: no phase on the CPU, no move in a running reader. Watching readers and previews open no stream meanwhile
  (`ReaderRuntime.StartEngine`, `Starting`: the problem `ReaderRules.Starting`, the list's chip **Starting**, no red), the action
  Read text waits, the load shows it, Use GPU is off with its ring and the tooltip "Testing the graphics card" (`getEngine`:
  `gpuStarting`; the page reads the engine again when the stats leave `Starting`). A card that fails, or a test past
  `OnnxTextRecognizer.CardStartLimit` (1 minute, one log line, no second try until the next start), leaves the CPU at once. A
  plugin that stops during the test does not wait for it: what the test still makes is freed when it ends. With Use GPU off the provider
  is never loaded at start (no DirectML in the process) and nothing waits; turning it on tries the card then, without a restart (the box is
  off with a ring beside it meanwhile, `setUseGpu` answers after the try, running readers go on), and `gpuAvailable` stays true while the setting is off so the box can be used.
  Off during a run moves the readings to the CPU; DirectML, once loaded, leaves with the next start.
- **What the test costs** (`CardModels.ProbeAsync`; log line "the graphics card's test took ..."): no session for a name or a provider
  check (a model's own test says whether it runs), the server text finder is built at the first Search and takes no
  card memory before that. First all sessions of the test side by side (3 on the card outside the budget's lock, 3 on the CPU), then
  the calls one at a time so no time holds another's: each model twice on the card (the first warms it, the second counts)
  and once warmed on the CPU. The calls have sizes readings use (reader 4 lines of 320, angle model 16 lines), so their
  sessions stay; the text finder's 512 x 512 usually does not, an area's size builds on its first picture.
  Measured 2026-11-04, RTX 5070 Ti, DirectML, 24 cores: 2.1 s (sessions 0.5 s, calls 1.5 s), all three models on the card. The
  same way with the models on the CPU: 2.0 s (sessions 0.14 s, calls 1.2 s).
  Page header, short ("CPU 24 cores · RTX 5070 Ti", the full text as tooltip): "Runs on: CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti" or "Runs on: CPU (8
  cores)", and **Use GPU** (on by default) to force the CPU. `getEngine` / `setUseGpu` answer
  `{runsOn, gpuAvailable, useGpu, gpuStarting, fpsChoices}`; `setUseGpu` is server-wide: the change right and all devices
  (`RequiresAllDevices`; the page disables the box for a user with a device group), audited. The setting is a plugin
  setting handed to the recognizer (`IEngineInfo`) at start, before any card is tried, and on change; the next readings run where it says, the
  graphics card keeps its sessions until they idle or the plugin stops. Without a recognizer: the CPU, no graphics card.
- **With a usable card the models run there** (decision 2026-11-04, replaces "the graphics card reads Search only" of
  2026-11-03 by the measurements in "Measured: one reader"): text finder, reader and angle model on the card, Search with
  the server text finder, fixed-place areas with the mobile one; the CPU decodes, cuts and decodes the text. Each model is
  tried at the start of the card (`CardModels.ProbeAsync`): a model whose test call fails or takes more than 1.5 times the CPU's
  time (`CardRoute`) stays on the CPU, one log line. A card call that fails during a run (a driver reset) ends the card's use:
  that call and all later ones run on the CPU, one log line, `OnGpu` false. So does a call that hangs: a reading waits at most
  `CardModels.CallLimit` (2 minutes, `TimeProvider`) and for its token, then its queued jobs leave and what waits goes to the CPU at
  once. A lane never touches a job its reading left: each copy into a model input runs under the job's lock, Search runs on a
  copy of its own and the output reader takes the line's width from the job. A card disposed with a lane still in a call waits
  5 s per lane and leaves that lane's sessions to the process, never freed under a call. Accurate stays on the CPU. Fixed areas were as
  fast on the card as on the CPU only for small areas; the old rule also held all the lines on the CPU, by far the largest
  stage on a label (525 of 670 core-ms).
- **Two lanes, shared calls** (`Recognition/CardModels`): the text finder and angle model run one call at a time on one lane,
  the reader on the other, each on a thread of its own, so a text finder call never waits for lines (one lane: busy 89 % of
  the time with the card busy 48 %). What waits goes into the next call, so the lines and pictures of several readers share
  calls. Text finder: 1 picture, or 4 when more wait (blank places fill the call; 4 pictures take twice the time of 1).
  Reader: lines of one width in calls of 4 or 16 (blank places change no reading). Angle model: 1 or 16.
- **Exact sizes, little padding.** The card must read what the CPU reads, so no size is bought with a reading:
  - A fixed-place area goes to the text finder at the CPU's size (a multiple of 32). The old steps of 256, 512, 768 and 1024
    lost text: small labels 25 of 31 at 768 x 768, 31 of 31 at 640 x 736. Search's tiles keep the steps: a tile holds picture to
    its edges.
  - Line widths are about 1.25 apart (`CardModels.LineWidths`): 128, 160, 200, 256, 320, 400, 512, 640, 800, 1024, 1280, 1600,
    3200. Moving labels with 4 px blur: steps of 2 read 19 of 25, steps of 1.4 read 24, steps of 1.25 all 25, as on the CPU.
    Only lines of one width share a call: narrower lines in a wider call read 2 of 25. Padding stays 0: repeating the line's
    last column read far worse (1 of 25).
  - A text finder size whose session does not fit beside the others reads on the CPU (`CardModels.FinderFits`), so many area
    sizes never make the card build sessions at every picture.
  - A call whose session is over the cap on its own (a small card: a Search tile, 4 pictures) reads on the CPU for that call
    only (`SessionTooLargeException`, one log line): no card failure, the card stays in use.
- **Graphics card memory** (`Recognition/GpuSessionBudget`, no setting): on DirectML every input size is a session of its own
  that keeps its own copy of the model and its buffers on the card until it is disposed (the provider frees nothing while a
  session lives). The budget counts each session by the pixels of its calls: server text finder `512 MB + 1,500 bytes x
  count x height x width` (`Estimate`, never below a measured session), mobile text finder `128 MB + 400 bytes` per pixel
  (`EstimateFinder`), reader and angle model `128 MB + 300 bytes` per pixel (`EstimateReader`), and keeps the sum at or below
  **80 % of the card's memory** as the card reports it (`CardShare`, decision 2026-11-04; 4 GB, `MaxBytes`, when it does not tell), whatever the cameras' resolution; a Search call may take half of that, so a large card sends more tiles per call. More sizes than that do not pay: 8 line widths in 5 batch sizes filled
  the budget and rebuilt sessions at every picture (5.1 pictures per second, the card 22 % busy):
  - a Search is split into calls of one size of at most half the cap (`TilesPerCall`: the fewest calls, the tiles spread
    evenly, blank tiles fill the last one), so two Search sizes (two cameras' resolutions) stay on the card together; a
    single tile of 1024 x 1024 (1.96 GB estimated) always fits. At the 4 GB cap: full HD 6 calls of one 768 x 768 tile (two would
    be 2.15 GB), QHD 6 calls of one 1024 x 1024 tile, 4K 15; a card of 16 GB (cap 12.8 GB) takes a whole full HD Search in one call. The calls' inputs and boxes are made on the CPU while the card
    runs another call (at most 3 calls ahead);
  - before a new session the least recently used ones go until it fits (made again when needed, 0.5 to 1 s) and the
    first time the server logs "Text recognition keeps at most <cap> MB of model sessions on the graphics card"; a session
    larger than the cap is never made;
  - no session exists outside the budget: a session is made for a size that runs (`ShapeSessions.Prepare` only inside the
    card's test, taken by its first call);
  - a session unused for 2 minutes is freed (like the CPU workers' sets), so an idle server gives the card back;
  - every call holds its session (`Hold`, `Free`), so a session is never freed while it runs, a new one that does not fit waits; memory pattern off and
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
- The limits do not depend on the graphics card ("Limits"): the CPU decodes every picture and works when the card fails;
  the traffic light shows what a server carries.
- Speed goals: one Search reader over a full HD picture at 10 frames per second or more on the GPU, at least 1 on the
  CPU; 20 fixed-place readers at 4 frames per second.
- Speeds of small fixed areas and Search before the models moved to the card (2026-11-03, development PC: Ryzen 9 3900X, 24 cores, RTX 5070 Ti with nothing else on the card,
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

## Measured: one reader

Measured 2026-11-04 on the development PC (Ryzen 9 3900X, 24 threads, dual-channel memory, RTX 5070 Ti 16 GB, driver 616.92,
Release, the desktop on the card), `Recognition/OneReaderSpeedTests` (Perf; `OADM_SPEED_CARD=1` for the card, by hand): a
belt of parcels made in memory, a label of 15 lines moving 12 px per frame, about 16 lines in view, 1 area of 600 x 700 px,
format `@@-####-##`, frames sent at the set rate to a real watching reader for 18 s, the first 4 not counted. The ship target
is 25 frames per second on 1920 x 1080. The page offers at most 15 (decision 2026-11-04): the margin keeps the machine calm.

| One reader, 25 fps set | Before (2 threads per call, 12 sessions) | CPU now | DirectML |
|---|---|---|---|
| Reads per second | 4.5 (at 15 set) | 8.4 | 25.0 |
| Latency of a picture | 223 ms | 390 ms | 101 ms |
| Load, state | 26 %, Overloaded | 55 %, Busy: 1 reader reads slower than set | 44 %, All readers keep up |
| Process CPU (cores of 24) | 7.2 (30 %) | 6.8 (28 %) | 1.4 (6 %) |
| Core-ms per picture | 1,654 | 802 | 58 |
| Working set, every worker busy | 2.7 GB (Search 3.7, peak 3.9) | 0.65 GB (Search 1.07, peak 1.24) | 1.2 GB (Search 1.4, peak 1.7) |
| Allocated per picture | 4.6 MB | 2.0 MB | 1.3 MB |
| GC pause | | 1.6 ms per second | 2.4 ms per second |
| On the card | | | 2.3 GB, about half busy |

**The target needs the card.** On the CPU 25 per second is out of reach on this PC. Two stages stop it:

- The text finder at 640 x 736 tops out at about 14 calls per second however many threads or sessions run it (alone 8 per
  second at 122 core-ms; 2 at once 12; 4 at once 14 at 270; 12 at once 13 at 892): a shared limit of the machine, most likely
  its memory. So at most 3 large calls run at once (`OcrLimits.LargeTextFinderCalls`).
- The reader costs 32 core-ms per line on one thread. 16 lines at 25 per second are 13 cores, more than half this CPU.

| A picture, per stage | Before: core-ms (wall ms) | CPU now: core-ms (wall ms) | Worth moving |
|---|---|---|---|
| Decode | 15 (15) | 7.6 (7.3) | no: little, and needs the CPU |
| Text finder's input, boxes | 5 | 4 | no |
| Text finder model | 157 (80) | 116 (117) | yes |
| Cut lines | 8 (8) | 8 (8) | no |
| Reader model and input | 1,276 (89) | 525 (67) | yes: by far the most |
| Angle model | 5.6 (8) | 5.1 (11) | little, goes along |
| Second reading of the area | 0 (the area is no strip) | 0 | no |
| Decoding the text, matching | 5 | 5 | no |
| Whole picture | 1,563 (207) | 670 (217) | |

On the card a whole picture costs about 50 to 60 core-ms, of which model calls about 25 (they wait for the card) and the rest
decode, cut and decode the text. 720p: 12.6 per second on the CPU (before 6.6), 15 of 15 on the card.

| Many readers | Before | CPU now | DirectML | CUDA (same calls, no plugin) |
|---|---|---|---|---|
| 1 reader at most, pictures per second | 4.5 | 8.4 | 31 | 44 |
| 10 readers, pictures per second in all | 6 | 10 at 9.5 cores | 47 at 2.2 cores | 71 |
| 10 readers at 4 fps | 0.6 each | 1.0 each | 4.0 each, 2.0 cores, keeps up | |
| 10 readers at 8 fps | | 1.0 each | 4.9 each | about 7 each |
| 10 readers at 25 fps | 0.6 each | 1.0 each | 4.9 each | about 7 each |
| 20 small fixed areas at once, in all | 150 | 260 | 620 | |
| 20 label readers at once, in all | 6 | 10 | 56 | |
| Search over full HD, one reader | 2.0 at 10.5 cores | 2.6 at 5.8 cores | 3.8 to 4.7 at 0.8 cores | |
| Search over full HD, one reader set to 15 fps, with the 80 % cap (2026-11-04) | | | 6.9 at 0.81 cores, card 93 % busy, 7.8 GB on the card with about 1.7 GB of the desktop, reading 543 ms | |
| Model process memory | | | 0.5 GB | 1.3 GB |
| First call after the start | | | 2 s | 9 to 79 s |

What the numbers changed:

- **One thread per call, one session per model.** 12 calls of 2 threads with sessions of their own: 100 lines per second at
  223 core-ms. 12 calls of 1 thread on one session: 226 lines per second at 44 core-ms, 131 instead of 310 MB. More threads per
  call barely help (1 thread 31 ms, 2 threads 17, 4 threads 13, 8 threads 14 at 115 core-ms), 24 calls at once collapse to half
  (the hardware threads share their cores). Batches of 8 lines and more are worse per line on the CPU.
- **No padding to 320 on the CPU**: a short line costs a third, the accuracy set reads better (hard receipts 37.5 to 50 %
  exact), moving labels no worse.
- **Frames overlap only with room**: without a free worker a reader stays at one frame, as before; 20 readers at once read
  more, never less.
- Kept: ONNX Runtime's arena and memory pattern (without the arena 20 times slower, without the pattern a third slower).

Tried, not kept:

- **A smaller text finder input.** A cap of 640 px on the long side reads 6 of 25 moving labels with 4 px blur, 480 px 10 of
  25, all 25 at full size. The accuracy set does not show it: its fixed areas are smaller.
- **More frames in work.** 8 frames: fewer pictures at twice the latency (the text finder's limit above).
- **Reusing a line's reading from the frame before.** Only 58 % of the lines had the same text a frame earlier, and half of
  those lay as far from their print as other text does: little gain, and the risk of a stale digit.
- **The second reading of a fixed-place area, and the angle model, as suspects:** an area of 600 x 700 is no strip, so no
  second reading; 1.4 lines per picture ask the angle model, 5 core-ms. Nothing to gain.
- **A CPU thread pool over all calls** (intra 12 on a shared session, no spinning): 120 lines per second at 183 core-ms.
- Not tried: quantized models. They could help a CPU-only server; they need the accuracy set to hold.

**Accuracy** (`AccuracySetTests`, `Moving_and_blurred_labels_read_their_values`, 2026-11-04): the set on the CPU 90.7 % exact
with a format (88.9 % before, hard receipts 50 %), any text 91.7 %; on DirectML every group at or above the baseline, hard
receipts 50 %, hard signs 66.7 %. Moving labels, the same on the CPU and on DirectML: sharp, 2 px and 4 px motion blur 25 of
25, small labels 31 of 31, with 2 px blur 28 of 31, very small 32 of 32, 720p 25 of 25, one wrong match over all.

**Open**: DirectML is measured on one NVIDIA card only. AMD and Intel cards and the plugin on CUDA (Linux) are not tested; a
test call and the 1.5 rule guard them, a card that reads differently would not be noticed. Search's goal of 10 per second
is still open (6.9). 25 frames per second from a real camera's MJPEG stream is not measured.

## Measured: dashes

Measured 2026-11-04 on the CPU, `Dash_formats_on_moving_labels` (Perf): the belt of "Measured: one reader" with a barcode line
(`CX 1379 2379 0 AT`) and a reference (`CP013790 78 3 AT`) on each label, 2 whole parcels, 110 frames, 15 frames per second, the
variants sharp, 2 and 4 px blur, small with 2 px blur, very small with strong JPEG, 720p with 2 px blur; 1,188 values in view.
Before, a dash of the format could be missing in the text.

| Format | Frames read | Frames with a wrong value | Rows with a wrong value, vote | With Moving objects |
|---|---|---|---|---|
| `@@-####-##` | 377 to 368 of 396 | 130 to 8 | 21 to 0 | 25 to 0 |
| `####-####` | 234 to 332 of 396 | 224 to 15 | 35 to 2 | 33 to 1 |
| `####-##-##` | 330 to 331 of 396 | 90 to 6 | 16 to 0 | 14 to 0 |
| All | 941 to 1,031 | 444 to 29 | 72 to 2 | 72 to 1 |

The frames lost are on 4 px blur, where the dash itself goes (`@@-####-##` 49 to 40 of 61 frames); every parcel still gives its row
(right rows 20 before and after). `####-####` gains frames: `2026-11-04` no longer passes as `2026-1104`, so the label's order number
wins. What stays wrong are single digits misread on a line with its real dashes. `Moving_and_blurred_labels_read_their_values`:
every variant reads as before (25 of 25 up to 28 of 31), matches on other lines 742 to 0, wrong values 1 to 0. The accuracy set has no
dash format and stays the same: 90.7 % exact with a format, any text 91.7 %.

**Dashes in the guided decoding**: optional in the search, then a match with dashes must hold exactly the dashes of the free reading
in its frames (`LineReader.DashesAsRead`). Required in the search would force a dash onto frames without one and need a limit of its
own. The check against the free reading works both ways: it refuses a dash found under another character (no effect on this belt)
and a dash the search left out (157 of the 188 wrong frames left by the first rule). A format without a dash behaves as before.

**Moving objects** against the vote on the same frames: right rows 50 and 50, wrong rows 1 and 2; with every second frame read (a CPU
at 8 per second) right rows 47 and 47, wrong none.

## Packaging and dependencies

- Add-on (`docs/spec/add-ons.md`): one ZIP per server platform with ONNX Runtime (1.24.4, on Intel Macs 1.23.2, the last version with `osx-x64` libraries; MIT: `Microsoft.ML.OnnxRuntime.DirectML`
  on Windows, the GPU build with the CUDA provider on Linux x64, the CPU build elsewhere), SkiaSharp (MIT; Linux:
  `NativeAssets.Linux.NoDependencies`), the PP-OCRv5 mobile and server detection and Latin recognition models and the
  angle model as ONNX files with the character list (Apache-2.0, in the plugin's notices). The size is measured at the
  build (Linux x64 is the largest because of the CUDA provider). The client part has no native libraries.
- DirectML itself (`DirectML.dll`, NuGet `Microsoft.AI.DirectML`, Windows only) is not open source: "MICROSOFT SOFTWARE
  LICENSE TERMS – DIRECTML" allow passing it on inside applications for Windows, and its section 2a says the software
  may collect data and send it to Microsoft. Its full license is in the plugin's notices (`OadmNoticePackage` item in
  the csproj; `check-addon-zip.sh` fails a Windows ZIP without it). The user docs say that the graphics card on Windows
  runs on Microsoft DirectML under Microsoft's terms; with **Use GPU** off it is not loaded at a start (CPU only:
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
| `ITextRecognizer` (+ `IEngineInfo`) | `Recognition/OnnxTextRecognizer` (the plugin folder's `Models/`) | lines per area with box and confidence, the too small areas, unchanged areas given again for a reuse source (`ReadAsync(jpeg, areas, reuseSource, ct)`; `IDetailedTextRecognizer` adapts it); what reads (`OnGpu`, `RunsOn`), Use GPU |
| `ITextMatcher` | `Matching/TextMatcher` | an area's value (`AreaValue`): whether it matches, the raw text, every match, its confidence and box |
| `IPictureCutter` | `Recognition/PictureCutter` (SkiaSharp) | the value's cut-out for the page, upright when its turn is known, within the size limits ("The last 20 reads") |
| the vote | `Matching/FrameVote` inside `Readers/AreaWatch` | the last 5 frames and the publish rule (2 in a row, only on change) |
| Moving objects | `Matching/TwoFrames` inside `Readers/AreaWatch` | a value read in 2 frames in a row, from the second one, no vote |

The page uses the server part's payloads (`Api/`) and texts (`ReaderRules`), never copies ("Page").

## Tests

- Text only inside areas: no area, no stream, no reading, no event (watching, the action, the preview, the recognizer
  without decoding); at most 30 text boxes, the strongest, and the reading's note.
- Recognition from recorded camera pictures with known text (`tests/Oadm.Plugins.Ocr.Tests/Fixtures`, recorded read-only
  from the development cameras): lines found, text and order, several lines, a slanted line.
- Rows (`Matching/LineOrderTests`, constructed `LineTurn` values): a receipt at every 15 degrees, upside down, a wide table,
  two blocks at different angles and their order, lines without a turn, a text found twice, columns and stacked small print
  kept, a digit line read the wrong way round, a short line, the cut at 500 characters. The values (`TextMatcherTests`,
  `Events/TextReadEventTests`, `Readers/RowsTests`): `Text` without a line break, `Text read` with the rows, `Number`
  unchanged, a format still finding its match in a line found twice, a row that splits in one frame fires no event (the vote
  and Moving objects), the results row, the one log line of Read text. The real models (`Recognition/RowsRecognitionTests`):
  a receipt at 0, 90, 180, 270 and 15 degrees. The page (`Client/PageRecentReadsTests`): a read's rows as lines, with a
  screenshot. Perf: `LineOrderSpeedTests` (the cost), `RowsSweepTests` (the measured table).
- Format: the syntax, look-alike fixes by position, a number split over two lines, no match; a dash must be a dash, on the matcher and on what the reader read (`TextMatcherTests`, `DecodingTests`).
- Moving objects and a value's frame (`Readers/AreaWatchTests`, `Readers/MovingObjectsTests`): off equals the plain vote, on needs 2 frames in a row, a misread in one frame gives no row and no event, A, B, A three, the second frame's picture, a voted value's cut-out from its own frame, also through the action, the frames held per area, a preview with and without the setting and changing it on a running stream, a draft that never shares another setting's stream, Pause; the setting saved, restarted and missing in an older record. The page (`Client/PageNoteTests`): the checkbox only while Watching and in the draft, the note above Save, the video's height unchanged, the 5 s hold on a fake clock, an alert that joins without a change, the paused lines at once, a short window, screenshots at 2000 x 870.
- Upright cut-outs (`Recognition/UprightCutOutTests`): labels turned by 180, 90, -90, 15, -30 and 165 degrees read for real on the CPU, the cut-out wide, not upside down, reading its value again; no turn or a bad one never fails; the limits; a screenshot of the dialog.
- Watching: the vote over 5 frames (a single bad frame changes nothing), a value published only when the vote holds in 2 frames and only on change; newest frame wins; a dropped stream reopens
  with the growing pause (`FakeTimeProvider`).
- The action: one reading, "Already reading", the events of every area.
- The preview (`Readers/PreviewTests`): the draft read live without rule events, new areas without a new stream, the
  lease (`FakeTimeProvider`), 8 previews and the watching limit, sharing an unchanged watching reader's stream, device
  groups, what a draft allows.
- Pause (`Readers/PauseTests`): the frame that was read answered and read again by each edit, Resume, a saved reader
  running and firing beside it, a burst of edits gated to the newest draft, the lease and the 8 previews, another picture,
  a deleted reader, Live view and device groups, the pixel limits; an edit never answered with the old reading
  (`UnchangedReuseTests`). The page (`Client/PagePauseTests`): the frame in the video's place, the ring, leaving the pause, a
  refusal, no Live view, a real pointer press on the button that touches no area and leaves the keys with the picture,
  a drag across it; screenshots live, paused and after an edit, and the frames the alignment is measured on.
- The page (`Client/`): fixed columns without a splitter and the results below the video, also in a wide window; Name, Mode and Stream
  with its summary (no camera field), the area rows under one header with the name as text, the ⋯ menu, the format help and
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
- Limits: at most `max(1, cores / 2)` workers held by a gate, one session set shared by all calls, 3 large text finder calls at once, a frame beside another only with room, in order (`WatchingTests`); 50 readers, the watching readers by the cores (4 per worker, 8 to 50) and 4 per site, a reader beyond them with the limit as its problem, the saved readers before the previews' own streams, 2 previews per user; GPU fallback to the CPU; the accuracy set and its score; device groups; Use GPU only with all devices.
- MJPEG parsing from a recorded `multipart/x-mixed-replace` answer (boundary, Content-Length, a part without it).
- End to end with the real parts (`EndToEndTests`): recorded pictures of the coffee box through a watching reader give
  each value once and No text read once when the box is gone; the action Read text reads a container number with its
  format and the page gets its cut-out.
- The model sources: `Models.targets` and `Models/README.md` name the same files, revisions and SHA-256.
- Speeds (`RecognitionSpeedTests`, `RecognitionBenchmarkTests` and `OneReaderSpeedTests` with the time and CPU of each stage, Perf, not in `manage
  test unit`): the numbers of "GPU" and "Measured: one reader". The card's rules without a card (`CardRoutingTests`): a model's route, the calls' sizes, the fallback, the load by how full a call is, sessions held for their calls.
- Partial decode (`PictureTests`: the same pixels as a full decode), unchanged areas (`UnchangedAreasTests`: reuse,
  a changed area read anew, other readers, 10 uses in a row), the angle model's skip rule, idle session sets freed
  (`FakeTimeProvider`), a set in use freed only after its call.
- Watching readers and previews reuse under their own source, the action never, unchanged pictures counted apart
  (`UnchangedReuseTests`); the graphics card's memory: sessions counted by pixels and never above the cap, least
  recently used out, a session over the cap never made, Search split into calls of half the cap, idle sessions freed
  (`GpuSessionBudgetTests`, fake sessions); a hung call ended at the limit on a fake clock, a cancelled reading, queued jobs
  out, nothing touched after a reading left, a call over the cap, Dispose with a hung call (`CardHangTests`, fake sessions at a
  gate); the start: readers, previews and the action wait, the page's state, a failed test, a stop during the test, the limit, off
  never tries the card (`Readers/CardStartTests`); fixed-place areas on the CPU and Search on the card, full HD, QHD and 4K
  Search within the cap, the runs-on text (`SearchRoutingTests`, fake runners, no graphics card).
- The last 20 reads (`Readers/RecentReadsTests`, `Client/PageRecentReadsTests`): the 20 and the oldest dropped with its cut-out,
  newest first, a value twice one row, A, B, A three, no row for an empty value or a non-match, a row with each event of a
  watching reader, a preview's list against the saved reader's, an On action reader's page, a deleted reader, no row from a
  held frame's re-reads, device groups, no cut-out without Live view, a cut-out once per row, the frame gone once cut, the
  memory bound, no allocation for an unchanged value, the real cutter within its limits on noise; the page: one reset per
  change and none without, one decode per row, a bitmap disposed when its row leaves, the note, a table frozen while paused (newer
  rows, re-reads, replies around a Pause and a Resume) and up to date on Resume, Use read text as format with older rows, a real
  click on a thumbnail, the dialog's whole-number scale without smoothing; screenshots with 20 rows at 2000 x 870, paused and the
  dialog.
- The resizer (`Client/PageLayoutTests`): a real pointer drag, the stops at 30 % and at the results' minimum, a short window, the
  order of the limits; screenshots at 2000 x 870 by default and dragged up to the limit. The paused note (`Client/PagePauseTests`):
  match, no match, eight areas in a short window.
- A reader's life (`Readers/ReaderLifecycleTests`): a rename keeps the stream and the published values, a reading of a
  deleted reader is neither kept nor fired, one of old areas not kept, a stream's problem in the list until pictures
  come, Read text from a watching reader's fresh reading or the one in work, cut-outs only with Live view, no text of a
  camera the user does not see. The stream's problem texts once per change and the live stream option
  (`VapixFrameSourceTests`), a picture claiming 65,535 x 65,535 refused (`PictureTests`), the container rule and the
  vote's format check (`TextMatcherTests`, `FrameVoteTests`, `DecodingTests`), Number with every digit
  (`TextReadEventTests`).

## Rotation and perspective (measured 2026-11-03)

How far a text can be turned in the picture and tilted away from the camera and still read. Measured on the CPU with the
mobile text finder by `Recognition/AngleSweepTests` (Perf, about 10 minutes on 24 cores; the What_if test alone 9.5) and
pinned by `Recognition/AngleLimitsTests` (a normal test, 5 s).

- **Pictures**: made in memory (`Recognition/AngleSweep.cs`, SkiaSharp), full HD, the plate in the middle: the container
  number `MSKU 123456 5` (`@@@U ###### #`, `45G1` below it) on a ribbed door, `4/12` (`#/##`, `INTENSITY` above it) on a
  label, `LOADING DOCK 3` (Any text, one line) on a sign, `36F1A` (`##@#@`, `TOTAL EUR 10.60` below it) on a receipt.
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
- Any text with several lines upside down gave the lines in reversed order (`TOTAL EUR 10.60 36F1A`) when this was
  measured; since rows (2026-11-06, "Several lines") they read from the text's own top. A format was never affected.

**Tried, no gain** (`What_if_rotation`, the same 3,200 pictures through a wrapped model runner, nothing in the plugin
changed): every unsure line read both ways (the angle model skipped) 88.2 % with a format, the text finder's pixel threshold
at 0.5 instead of 0.3 88.2 %, both 88.3 % (as it is: 88.2 %). The joined lines stay one region at 0.5; keeping them
apart needs another step than a threshold (not measured: split a box that holds two rows at the gap in the text finder's
map).
