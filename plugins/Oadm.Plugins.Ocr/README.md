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
| Large areas | The fewest tiles that cover the area at up to 960 px each with at least 15 % overlap (full HD: 6 tiles of 736 x 608); boxes found twice and split pieces of one line (meter drums) merged. |
| Upside down | The angle model only names candidates; a candidate is read both ways and the surer reading wins. |
| Confidence | A line's confidence is the lowest probability of its characters (of its format match when it has one); lines whose mean is below 50 % are dropped. The area's confidence is the lowest of the lines that make its value. |
| Format | Format-guided CTC decoding (digits where `#`, letters where `A`), the ISO 6346 check digit for `AAAU ###### #`, then `TextMatcher` (look-alikes by position, two neighbouring lines, every match). A fixed-place strip with a format and no match is read once more as one line. |
| Vote | `FrameVote` per area of a watching reader: the last 5 frames character by character, weighted by confidence; published once the vote holds 2 frames in a row and only on change; an area that becomes empty publishes once. |
| Graphics card | DirectML on Windows x64, CUDA on Linux x64 when a test reading passes, else the CPU (one log line). "Use the graphics card" is a plugin setting, server-wide (all devices only), applied at run time. |
| Limits | 50 readers, 20 watching on the CPU (50 on a graphics card), 4 watching per site; 8 areas per reader; 1, 2 or 4 frames per second (1 to 15 on a graphics card). |
| Models | Never in git: downloaded at build into a checked cache (`Models/README.md`); missing `Models/` in the plugin folder means "Text recognition is not installed completely. Install the add-on again." |

## How it works

| File | What |
|---|---|
| `OcrPlugin.cs`, `Api/OcrPlugin.Methods.cs` | `ICorePlugin`, event source, action source, methods, permissions |
| `Model/` | The shared contract: `Reader`, `Area`, `TextLine`, `Reading`, `ITextRecognizer`, `IFrameSource`, `ITextMatcher` and the optional `IAreaValueMatcher`, `IEngineInfo`, `IPictureCutter` |
| `Readers/ReaderParts.cs` | The parts; `ReaderParts.Real` builds the installed ones from the context |
| `Readers/ReaderRuntime.cs` | Watching loops, single readings, the last reading per reader, the limits per server and site |
| `Readers/AreaWatch.cs` | One area of a watching reader over `FrameVote` |
| `Readers/ReaderStore.cs`, `ReaderRules.cs` | Stored readers (plugin records, collection `readers`, with the owner scope), checks and texts |
| `Recognition/OnnxTextRecognizer.cs` | Decode, tiles, text finder, boxes, straighten, angle, read, format decoding; CPU workers and the graphics card |
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
| `readNow` | change | One picture now, every area's event |
| `getReading` | show | The last reading with picture and cut-outs |
| `getEngine` | show | `{runsOn, gpuAvailable, useGpu, fpsChoices}` |
| `setUseGpu` | change, all devices | Use the graphics card, audited |
| `getCameraOptions` | show | Video sources and sizes of a camera of the caller, kept 10 minutes |

Events to the page: `readers` (the list after a change), `reading` (each reading without cut-outs). Both go to users with
all devices and to the reader's owner scope.

## Tests

`tests/Oadm.Plugins.Ocr.Tests`: `Recognition/` (the real models on the CPU: geometry, decoding, workers, the accuracy set
with its baseline, the model sources; `RecognitionSpeedTests` is Perf), `Matching/`, `Frames/` (recorded MJPEG,
`FakeTimeProvider`), `Readers/` and `Events/` (fakes of the parts: watching, the vote, limits, the action),
`DeviceGroupTests`, `PageMethodsTests`, `EndToEndTests` (the real parts with recorded pictures), `Client/` (the page,
headless screenshots). The models come from the build into the test output's `Models/`.

Open: the hardware runs on the development cameras; the reader setting "Accurate".
