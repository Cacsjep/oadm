# Text recognition (`oadm.ocr`)

Core plugin with a rail page in group Vision (icon `text`), with `.Client`, off by default, an add-on (not in the
installers; installed on the Plugins page). Readers take pictures of one camera and read the text in their areas; a
reading that found text publishes the event "Text read" for the event rules, and the rule action "Read text" reads one picture now. Read
only for devices. Spec: [Text recognition](../../docs/spec/ocr.md), [Add-ons](../../docs/spec/add-ons.md).

## Decisions

| Topic | Decision |
|---|---|
| Pictures | MJPEG (`mjpg/video.cgi`) while Watching, `jpg/image.cgi` for one picture, through `ctx.System.Vapix` (device route, sites). No H.264 or H.265 (no FFmpeg on the server). Frames and readings live in memory only. |
| Recognition | ONNX Runtime with PaddleOCR PP-OCRv5 models: text finder (mobile on the CPU and for fixed places, server for Search on a graphics card), Latin reader (mobile everywhere, no Latin server model exists), angle model. Own C# post-processing, SkiaSharp for JPEG, crop, turn and scale. No OpenCV, Tesseract or Python. |
| Load | `max(1, cores / 2)` CPU workers of one thread each on one shared session; the limit counts model calls, so an idle server reads the tiles of one picture at once. Per reader one frame in work, up to 4 while there is room, the newest frame wins. |
| Areas only | Text is read only inside the drawn areas: a reader without an area opens no stream and reads nothing; the action refuses with `ReaderRules.NoAreas`. At most 30 text boxes per reading over all areas (the highest detection scores, `OnnxTextRecognizer.Strongest`); more set `Reading.Note` to `ReaderRules.TooManyTexts`. |
| Large areas | The fewest tiles that cover the area at up to 960 px each with at least 15 % overlap (full HD: 6 tiles of 736 x 608); boxes found twice and split pieces of one line (meter drums) merged. |
| Upside down | The angle model only for lines not read surely upright (every line of digits); it only names candidates, a candidate is read both ways and the surer reading wins. |
| Speed | Only the part of the JPEG the areas need is decoded; rented model buffers and scores, outputs read in place; on the CPU one thread per call on one shared session, every line a call of its own; a reader overlaps up to 4 frames while there is room, results in order; on the card two lanes and shared calls of few sizes (spec "GPU", "Measured: one reader"). |
| Confidence | A line's confidence is the lowest probability of its characters (of its format match when it has one); lines whose mean is below 50 % are dropped. The area's confidence is the lowest of the lines that make its value. |
| Format | Format-guided CTC decoding (digits where `#`, letters where `@`), the ISO 6346 check digit for formats starting `@@@U ###### #` (also `@@@J`, `@@@Z`, `@@@?` for any of them) (`ContainerCheck`, one rule for decoder and matcher; a wrong check digit is no match, never repaired), then `TextMatcher` (look-alikes by position, two neighbouring lines, every match). A dash of the format must be a dash in the text: the decoder may pass one by, but `LineReader.Accept` drops such a match, and a match with dashes must hold exactly the dashes of the free reading in its frames; spaces stay optional. A fixed-place strip with a format and no match is read once more as one line. |
| Use read text as format | The area's ⋯ menu: the format field takes the shape of the text read now, digits `#`, letters `@`, the rest as written, a container number with a fitting check digit gets `@@@?` and a message asks to check it (`TextFormat.FromText`). Live and paused, from the current frame, never from a row. |
| Vote | `FrameVote` per area of a watching reader: the last 5 frames character by character, weighted by confidence; published once the vote holds 2 frames in a row and only on change; an area left with no text at all publishes No text read once. Each candidate keeps a reference to its frame, box and turn (`ValueFrame`), at most the 5 frames of the window, so a row shows the frame its value was read in. |
| Moving objects | `Reader.MovingObjects`, only while Watching: `TwoFrames` replaces the vote: a value counts once read in 2 frames in a row, from the second one, a value seen once is ignored. Previews follow the setting with no events; with it off a preview stays without a vote. Rules: spec "Moving objects". |
| Graphics card | DirectML on Windows x64, CUDA on Linux x64 when a test reading passes, else the CPU (one log line). Nothing else. The plugin's start never waits for the test: it runs in the background and the readers wait for it, then start on the card, or on the CPU when it fails or passes 1 minute (`ReaderRuntime.StartEngine`, chip Starting). With Use GPU off no provider loads at start (no DirectML in the process); turning it on tries the card without a restart. The models run on the card (`Recognition/CardModels`) unless a test call fails or takes more than 1.5 times the CPU's time (`CardRoute`); a call that fails, or hangs for 2 minutes (`CallLimit`), sends the readings back to the CPU, a call over the memory cap only itself. Fixed areas at their exact size, line widths about 1.25 apart: more padding loses readings. |
| Limits | 50 readers; watching readers by the cores (4 per recognition worker, 8 to 50: 8 cores 16, 24 cores 48), 4 watching per site (their MJPEG streams take no large transfer slot of the site, `VapixRequestOptions.LiveStream`); 8 areas per reader; 1, 2, 4, 8, 12 or 15 frames per second on every server; 8 page previews, 2 per user, their own streams counted with the watching readers after them. A reader beyond a limit shows it as its problem. |
| Page | Four cards in fixed columns (readers 320 px with the runtime stats, settings 320 px); the video and the results below it take the rest, with the shared splitter between them: the video card stops at 30 % of the column's height, the results card at 140 px (`OcrView.LimitsOf`, rules: spec "Page"). Readers, the host's live video of the reader's camera with the areas and lines drawn over it (`ReadingPicture`: draw, select, move, resize, nudge; `AreaGeometry` keeps areas inside the picture), the settings column (Name and Mode side by side, Stream: a full-width button that shows the summary ("Configure · 1920x1080 @ 4 fps") and opens a dialog with Video source, Resolution and Frames per second; no camera field, a saved reader keeps its camera and the server refuses another; the areas a `ListBox.areaRows`, one row each: the name as text, the format field (empty reads any text) and the ⋯ menu (Use read text as format, Rename in a dialog, Delete); only the rows scroll; New in the Areas header starts drawing, a new area is "Area n" with the lowest free number). No Read now: the shown reader is read live as a preview (the draft, saved or not, no events, a 30 s lease kept alive every 10 s). Lines hidden while an area is drawn or adjusted; without areas the video is dimmed with "Start by drawing an area". A drag ends (`AreaChanged`) before the pointer capture is released: the capture's loss cancels a drag still running. A small area moves from inside, its handles just outside. The list's chip names the kind of problem (`OcrTexts.ProblemChip`: Limit reached, Credentials required, Unreachable, Stream error, ...; the device grid's status names where they exist), the server's text as its tooltip. Results match their area by place; cut-outs only when the server sent them. |
| The last 20 reads | Per saved reader and per preview, memory only (`RecentReadList`, `ReaderRuntime.Recent.cs`): a row is a new value in an area, not a frame; with its Text read event on a watching reader, by the same rule for the action. Each row keeps a cut-out of at most 640 px and 64 KB, upright by its line's turn (`TextLine.Turn`, `LineTurn`, level when a value spans lines), cut by one worker off the frame's loop; the row holds its frame only until then. The rows come in `recent` of `getReading` and `getPreview`, each cut-out once (`knownCutouts`). The page keeps a row by id with its decoded thumbnail; a click opens `CutOutWindow`. Paused the table stays frozen; the held frame's result per area is `HeldAreas`, shown in the note above Save (live alerts stay 5 s there, `OcrLimits.NoteHold`, so the video never moves; "Text too small" only after 10 s without a value, `OcrLimits.SmallTextWait`). Rules: spec "The last 20 reads". |
| Pause | The server keeps the frame of the preview's latest reading (the client's video is another picture): `pausePreview` answers it, the draft re-reads it (`RereadHeld`: one worker, latest wins, no reuse), a saved reader never hears of it. Live view only. In server memory, gone on Resume, the lease, another picture or a closed page. The button sits on the picture (`PicturePlace`, the rectangle of `ReadingPicture`), never takes the focus. Rules: spec "Pause". |
| Runtime safety | Syncs read the stored readers under the lock (`ReaderRuntime(..., readers)`); `StopAsync` cancels and awaits the single readings; timers and events run through `QuietlyAsync`; cut-outs cut once per new value or per reading on request (`ReplyOf`), never per frame, and sent only to users with Live view, the picture only on Pause; the `reading` topic only on change or once a second; previews belong to their user (events only to that user, 2 per user); a reading is kept and fired only while its reader is stored with the same picture (`SamePicture`); a stream's failure is the reader's problem; decoding refuses pictures over 8K before allocating; the page makes the preview id and drops fetched readings older than the shown one. |
| Runtime stats | `Readers/ReaderStats`: counters per picture and per watching reader, a sample every 2 s, figures over 10 s; load = busy time of the model calls (`BusyMeter`; with a card `CombinedBusy`: per sample the larger of the workers' time and the card's time x workers) / (window x the CPU workers), so 100 % means the workers or the card are full; server CPU from `Environment.CpuUsage`. `getStats` keeps the live topic `stats` on for the caller's scope for 30 s. |
| Models | Never in git: downloaded at build into a checked cache (`Models/README.md`); missing `Models/` in the plugin folder means "Text recognition is not installed completely. Install it again on the Plugins page." |

## How it works

| File | What |
|---|---|
| `OcrPlugin.cs`, `Api/OcrPlugin.Methods.cs` | `ICorePlugin`, event source, action source, methods, permissions |
| `Model/` | The shared contract: `Reader`, `Area`, `TextLine`, `Reading`, `ITextRecognizer`, `IFrameSource`, `ITextMatcher` (the value with every match, confidence and box) and the optional `IEngineInfo`, `IRecognizerLoad`, `IPictureCutter` |
| `Readers/ReaderParts.cs` | The parts; `ReaderParts.Real` builds the installed ones from the context |
| `Readers/ReaderRuntime.cs` | Watching loops, single readings, the last reading per reader, the limits per server and site |
| `Readers/RecentReadList.cs`, `Readers/ReaderRuntime.Recent.cs` | The last 20 reads: the list and its bounds, the rows and their cut-outs |
| `Readers/ReaderRuntime.Previews.cs` | The page's previews: the draft read live without events, its own stream or a watching reader's, the 30 s lease, 8 at most; Pause: one held frame per preview (`HeldFrame`), re-read latest wins without reuse, memory only |
| `Readers/ReaderStats.cs`, `ReaderRuntime.Stats.cs` | The runtime stats: the samples, the traffic light, each scope's view and the live event |
| `Readers/AreaWatch.cs` | One area of a watching reader over `FrameVote` or `TwoFrames` |
| `Readers/ReaderStore.cs`, `ReaderRules.cs` | Stored readers (plugin records, collection `readers`, with the owner scope), checks and texts |
| `Recognition/OnnxTextRecognizer.cs` | Decode, tiles, text finder, boxes, straighten, angle, read, format decoding; CPU workers and the graphics card |
| `Recognition/Picture.cs` | Partial JPEG decode, cut-outs, model inputs |
| `Recognition/UnchangedAreas.cs` | The print of each area and its last reading, used again while it barely changes |
| `Recognition/ModelRunner.cs`, `ModelSet.cs`, `ShapeSessions.cs` | CPU workers (one shared session set, freed when idle), the CPU runner, the sessions per input size |
| `Recognition/CardModels.cs` | The models on the card: two lanes, shared calls, the route of each model, `CardRunner` with the fallback to the CPU |
| `Recognition/GpuSessionBudget.cs` | The graphics card's memory: each session estimated by its input pixels (measured on an RTX 5070 Ti), held for its call, on DirectML at most 80 % of the card's memory together (4 GB when it does not tell), least recently used out, idle 2 minutes freed; a Search split into calls of at most half the cap (`TilesPerCall`). Numbers: spec "GPU" |
| `Recognition/StageClock.cs` | Time per stage for the speed measurements |
| `Recognition/PictureCutter.cs` | The value's cut-out for the page, within 640 px and 64 KB |
| `Matching/` | `TextMatcher`, `TextFormat`, `LineOrder`, `FrameVote`, `TwoFrames` |
| `Frames/` | `VapixFrameSource` (reopen pause 1 s doubling to 1 min, stall 3 s, request 15 s), `MjpegParser` |
| `Events/` | The events "Text read", "No text read" and "Reader problem" per reader, "Any reader problem", the action "Read text" |

## Methods

| Method | Permission | What |
|---|---|---|
| `listReaders` | show | The caller's readers with their last text and problem; the frame rates and watching limit of now |
| `getReader` | show | One reader of the caller |
| `readNow` | change | An On action reader reads one picture now with the real events, audited |
| `saveReader` | change | Field errors as `{"errors":[{"field","message"}]}`; counts the watching readers per site (system access) |
| `deleteReader` | change | Deletes; its event type goes |
| `startPreview` | show | Starts or keeps the page's preview of a draft: `{"previewId","reader"}` to `{"previewId","problem"}` |
| `stopPreview` | show | Ends a preview of the caller's scope |
| `getPreview` | show | The preview's last reading and last reads (`recent`, each cut-out once; `knownCutouts`), no picture |
| `pausePreview` | show + Live view | Holds the frame of the latest reading and answers it with the reading: `{"paused","picture","reading","details"}` or `{"problem"}` |
| `resumePreview` | show | Drops the held frame, the preview reads live again; answers like `startPreview` |
| `getReading` | show | The last reading with its cut-outs and the last reads (`recent`) |
| `getStats` | show | The runtime stats of the caller's readers and the server-wide load; keeps the live topic `stats` on for 30 s |
| `getEngine` | show | `{runsOn, gpuAvailable, useGpu, fpsChoices}` |
| `setUseGpu` | change, all devices | Use GPU, audited |
| `getCameraOptions` | show | Video sources and sizes of a camera of the caller, kept 10 minutes |

Events to the page: `readers` (the list after a change), `reading` (each reading without cut-outs), `preview` (each
reading of a preview, with its id, without cut-outs), `stats` (the runtime stats every 2 s while a page of the scope asked
within 30 s). They go to users with
all devices and to the reader's owner scope (a preview: the scope that started it).

## Tests

`tests/Oadm.Plugins.Ocr.Tests`: `Recognition/` (the real models on the CPU: geometry, decoding, workers, the accuracy set
with its baseline, the model sources, partial decode, unchanged areas; `RecognitionSpeedTests` and `RecognitionBenchmarkTests`
are Perf), `Matching/`, `Frames/` (recorded MJPEG,
`FakeTimeProvider`), `Readers/` and `Events/` (fakes of the parts: watching, the vote, limits, the action, the preview),
`DeviceGroupTests`, `PageMethodsTests`, `EndToEndTests` (the real parts with recorded pictures), `Client/` (the page with
a fake live video, moving and resizing areas, the preview's draft,
headless screenshots). The models come from the build into the test output's `Models/`.

Open: the hardware runs on the development cameras; the reader setting "Accurate".
