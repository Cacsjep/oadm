# Text recognition (`oadm.ocr`)

Core plugin with a rail page in group Vision (icon `text`), with `.Client`, off by default, an add-on (not in the
installers; installed on the Plugins page). Readers take pictures of one camera and read the text in their areas; each
reading publishes the event "Text read" for the event rules, and the rule action "Read text" reads one picture now. Read
only for devices. Spec: [Text recognition](../../docs/spec/ocr.md), [Add-ons](../../docs/spec/add-ons.md).

## Decisions

| Topic | Decision |
|---|---|
| Pictures | MJPEG (`mjpg/video.cgi`) while Watching, `jpg/image.cgi` for one picture, through `ctx.System.Vapix` (device route, sites). No H.264 or H.265 (no FFmpeg on the server). Frames and readings live in memory only. |
| Recognition | ONNX Runtime with PaddleOCR PP-OCRv5 models: text finder (mobile on the CPU, server on a graphics card), Latin reader (mobile everywhere, no Latin server model exists), angle model. Own C# post-processing, SkiaSharp for JPEG, crop, turn and scale. No OpenCV, Tesseract or Python. |
| Load | `max(1, cores / 2)` CPU workers of 2 threads; the limit counts model calls, so an idle server reads the tiles of one picture at once. Per reader one reading at a time, the newest frame wins. |
| Areas only | Text is read only inside the drawn areas: a reader without an area opens no stream and reads nothing; the action refuses with `ReaderRules.NoAreas`. At most 30 text boxes per reading over all areas (the highest detection scores, `OnnxTextRecognizer.Strongest`); more set `Reading.Note` to `ReaderRules.TooManyTexts`. |
| Large areas | The fewest tiles that cover the area at up to 960 px each with at least 15 % overlap (full HD: 6 tiles of 736 x 608); boxes found twice and split pieces of one line (meter drums) merged. |
| Upside down | The angle model only for lines not read surely upright (every line of digits); it only names candidates, a candidate is read both ways and the surer reading wins. |
| Speed | Only the part of the JPEG the areas need is decoded; rented model buffers, outputs read in place; CPU lines in pairs over the free workers; DirectML sides and tile batches in steps; while watching (readers and previews, never the action) an unchanged area gives its last reading again (`ReadingMaker` sets `RecognitionOptions.ReuseUnchanged` with the reader or preview id as `Source`, at most 10 in a row; all areas unchanged counts as `unchangedPerSecond`, not as a picture). Numbers: spec "GPU". |
| Confidence | A line's confidence is the lowest probability of its characters (of its format match when it has one); lines whose mean is below 50 % are dropped. The area's confidence is the lowest of the lines that make its value. |
| Format | Format-guided CTC decoding (digits where `#`, letters where `A`), the ISO 6346 check digit for `AAAU ###### #`, then `TextMatcher` (look-alikes by position, two neighbouring lines, every match). A fixed-place strip with a format and no match is read once more as one line. |
| Not part of this version | Taking a read text as the format: later (decision 2026-11-03). |
| Vote | `FrameVote` per area of a watching reader: the last 5 frames character by character, weighted by confidence; published once the vote holds 2 frames in a row and only on change; an area that becomes empty publishes once. |
| Graphics card | DirectML on Windows x64, CUDA on Linux x64 when a test reading passes, else the CPU (one log line). "Use the graphics card" is a plugin setting, server-wide (all devices only), applied at run time. |
| Limits | 50 readers, 20 watching on the CPU (50 on a graphics card), 4 watching per site; 8 areas per reader; 1, 2 or 4 frames per second (also 8, 12, 15 on a graphics card); 8 page previews, their own streams counted with the watching readers. |
| Page | Fixed columns (readers 320 px with the runtime stats, settings 320 px), the video takes the rest with the results below it. Readers, the host's live video of the reader's camera with the areas and lines drawn over it (`ReadingPicture`: draw, select, move, resize, nudge; `AreaGeometry` keeps areas inside the picture), the settings column (Video source and Resolution in one row; the areas a `ListBox` of one row each, Name, Mode select, Format, delete, under one header row; New in the Areas header starts drawing). No Read now: the shown reader is read live as a preview (the draft, saved or not, no events, a 30 s lease kept alive every 10 s). Lines hidden while an area is drawn or adjusted; without areas the video is dimmed with "Start by drawing an area". A drag ends (`AreaChanged`) before the pointer capture is released: the capture's loss cancels a drag still running. |
| Runtime safety | Syncs read the stored readers under the lock (`ReaderRuntime(..., readers)`); `StopAsync` cancels and awaits the single readings; timers and events run through `QuietlyAsync`; cut-outs cut on request once per reading (`ReplyOf`); the `reading` topic only on change or once a second; preview events only to the preview's scope; the page makes the preview id and drops fetched readings older than the shown one. |
| Runtime stats | `Readers/ReaderStats`: counters per picture and per watching reader, a sample every 2 s, figures over 10 s; load = busy time of the model calls (`BusyMeter`) / (window x capacity: the CPU workers, 1 on a graphics card); server CPU from `Environment.CpuUsage`. `getStats` keeps the live topic `stats` on for the caller's scope for 30 s. |
| Models | Never in git: downloaded at build into a checked cache (`Models/README.md`); missing `Models/` in the plugin folder means "Text recognition is not installed completely. Install it again on the Plugins page." |

## How it works

| File | What |
|---|---|
| `OcrPlugin.cs`, `Api/OcrPlugin.Methods.cs` | `ICorePlugin`, event source, action source, methods, permissions |
| `Model/` | The shared contract: `Reader`, `Area`, `TextLine`, `Reading`, `ITextRecognizer`, `IFrameSource`, `ITextMatcher` and the optional `IAreaValueMatcher`, `IEngineInfo`, `IPictureCutter` |
| `Readers/ReaderParts.cs` | The parts; `ReaderParts.Real` builds the installed ones from the context |
| `Readers/ReaderRuntime.cs` | Watching loops, single readings, the last reading per reader, the limits per server and site |
| `Readers/ReaderRuntime.Previews.cs` | The page's previews: the draft read live without events, its own stream or a watching reader's, the 30 s lease, 8 at most |
| `Readers/ReaderStats.cs`, `ReaderRuntime.Stats.cs` | The runtime stats: the samples, the traffic light, each scope's view and the live event |
| `Readers/AreaWatch.cs` | One area of a watching reader over `FrameVote` |
| `Readers/ReaderStore.cs`, `ReaderRules.cs` | Stored readers (plugin records, collection `readers`, with the owner scope), checks and texts |
| `Recognition/OnnxTextRecognizer.cs` | Decode, tiles, text finder, boxes, straighten, angle, read, format decoding; CPU workers and the graphics card |
| `Recognition/Picture.cs` | Partial JPEG decode, cut-outs, model inputs |
| `Recognition/UnchangedAreas.cs` | The print of each area and its last reading, used again while it barely changes |
| `Recognition/ModelRunner.cs`, `ModelSet.cs`, `ShapeSessions.cs` | CPU workers (idle sets freed), the graphics card runner, the sessions per input size |
| `Recognition/GpuSessionBudget.cs` | The graphics card's sessions: at most 6 over all models (3 of the text finder), least recently used out, idle 2 minutes freed |
| `Recognition/StageClock.cs` | Time per stage for the speed measurements |
| `Recognition/PictureCutter.cs` | The value's cut-out for the page |
| `Matching/` | `TextMatcher`, `TextFormat`, `LineOrder`, `FrameVote` |
| `Frames/` | `VapixFrameSource` (reopen pause 1 s doubling to 1 min, stall 3 s, request 15 s), `MjpegParser` |
| `Events/` | The event "Text read" per reader, the action "Read text" |

## Methods

| Method | Permission | What |
|---|---|---|
| `listReaders` | show | The caller's readers with their last text and problem; the frame rates and watching limit of now |
| `getReader` | show | One reader of the caller |
| `saveReader` | change | Field errors as `{"errors":[{"field","message"}]}`; counts the watching readers per site (system access) |
| `deleteReader` | change | Deletes; its event type goes |
| `startPreview` | show | Starts or keeps the page's preview of a draft: `{"previewId","reader"}` to `{"previewId","problem"}` |
| `stopPreview` | show | Ends a preview of the caller's scope |
| `getPreview` | show | The preview's last reading with cut-outs, no picture |
| `getReading` | show | The last reading with picture and cut-outs |
| `getStats` | show | The runtime stats of the caller's readers and the server-wide load; keeps the live topic `stats` on for 30 s |
| `getEngine` | show | `{runsOn, gpuAvailable, useGpu, fpsChoices}` |
| `setUseGpu` | change, all devices | Use the graphics card, audited |
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
