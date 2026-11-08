# Vision Center (`oadm.vision`)

Core plugin with a rail page in group Vision (icon `flow`), with `.Client`, off by default, an add-on (not in the
installers; installed on the Plugins page). Pipelines of nodes run the user's own ONNX models on one camera's pictures:
detect, classify, filter, track, areas, lines, counts and rule events. Read only for devices; no model ships. Spec:
[Vision Center](../../docs/spec/vision-center.md), [Add-ons](../../docs/spec/add-ons.md).

## Decisions

| Topic | Decision |
|---|---|
| Pictures | MJPEG while Watching, one JPEG for On action, through the shared frame source (`plugins/Shared/Vision/Frames`, device route, sites). Frames live in memory only; SkiaSharp decodes, refusing pictures over `PictureLimits` before any pixel memory is taken. |
| Models | Stored ONNX files (`plugin-data/oadm.vision/models/<id>.onnx`), the list in the plugin records (`models`). `OnnxScan` reads the file without ONNX Runtime: only the default, `ai.onnx`, `ai.onnx.ml` and `com.microsoft` domains in nodes and functions, no external data. `ModelChecker` then opens a CPU session and runs one blank picture to recognise the format. |
| Formats | `OutputFormats` recognises the outputs, `Inference/Decoders` reads them: YOLO (`[1, 4 + C, N]` v8 when 4 + C < N, else `[1, N, 5 + C]` v5, own suppression at IoU 0.45 per class), YOLO with suppression (`[1, N, 6]`, N up to 1,000), DETR (`pred_logits` with "no object" last, softmax; `pred_boxes`), RF-DETR (boxes then logits, sigmoid, top 300; a masks output left out), DETR with labels (boxes, scores, labels; `orig_target_sizes` filled with the input size; pixels or 0 to 1), classification (`[1, C]`, softmax unless already probabilities). Two outputs without `pred_` names: logits first is DETR, boxes first RF-DETR. |
| Fit and scaling | Per model: Letterbox (YOLO), Stretch (RF-DETR, DETR with labels, classification), Keep shape and pad (DETR); 0 to 1 or ImageNet mean and deviation (`Inference/Preprocess`). Free inputs a multiple of 32 from 32 to 2,048 (default 640); fixed inputs 8 to 4,096 per side. |
| Class names | Metadata `names` (Ultralytics' Python dict, a JSON list or object) when the count fits, else the labels file (another count refused), else a built-in list by count (91: `coco-91`, 80: `coco-80`), else "Class <id>". "N/A" is a gap: never shown, its detections dropped. `Models/ClassLists`: the built-in lists and the user's (collection `classLists`, at most 50); a list's count is a model's class count. |
| Graph | `Graph/VisionGraph.Check`: one error per node in a fixed order (id first), graph errors for the whole pipeline, the input kind of every reached node. Texts in `Graph/GraphTexts`, read by the page's editor too. Settings are JSON (`NodeSettings`), defaults from the fields of `Model/NodeKinds`. |
| Evaluator | `Nodes/PipelineEvaluator`: the nodes that can run, parents first, each once per frame, outputs by reference; only nodes an Event, a Count or the eye needs (`EvaluatorOptions.AllNodes` for previews); Event nodes last, so a failing model call (`FrameResult.ModelFailure`) sends no events. Time comes only with the frame; `EvaluatorOptions.Time` measures the model calls. |
| Tracking | `Tracking/ObjectTracker`: Kalman filter per axis (`BoxFilter`), two greedy IoU rounds per class (high scores of 50 % or more against every track at IoU 0.2, the lower ones against confirmed unmatched tracks at 0.5), ids once confirmed, lost tracks kept by frame time, at most 300. |
| Logic, time, motion | `Nodes/LogicNodes`: Compare, And / Or, Hold, Throttle, Statistics, Timer, Time window, Device event (`EvaluatorOptions.EventStates`); `Nodes/MotionNodes`: Dwell, Stationary, Direction (per object state that ends with the Track node's Keep lost). Timers and windows judge the frame time in `EvaluatorOptions.TimeZone`. Control inputs (`PipelineLink.ToPort`): Reset, Send now (pulse), Only while, Second (condition); the evaluator orders the nodes over data and control links. |
| Geometry | `Geometry/VisionGeometry`: point in polygon (even-odd), box overlap, IoU, the side of a directed line (In = from its left to its right on the picture), the crossing of a step over a polyline. |
| Events | `Events/VisionEventTypes`: per Event node one type per kind its input gives, folder Vision Center / pipeline; `VisionEventSink` publishes; `VisionActions`: Run pipeline and Reset counter (value `<pipeline id>/<node id>`). |
| Load | CPU workers `max(1, cores / 2)`, one thread each, one session per model and size, idle sessions freed after 2 minutes. The card is tested per model at its first call (`Shared/Vision/Engine`, `EngineRules.MostSlower` 1.5); card calls in one lane; a failure ends card use until the next start. A pipeline has up to 4 frames in decoding, one at a time in the evaluator; the newest frame wins. |
| Runtime | `Pipelines/PipelineRuntime`: watching loops, single runs, previews (`.Previews.cs`, shared lease rules), stats (`.Stats.cs`), the limits per server and site. A saved pipeline restarts only on a change of graph, settings, stream, owner or its models. |
| Page | `Oadm.Plugins.Vision.Client`: three columns (pipelines 320 px, the canvas at least 620 px, the right column at least 420 px, a splitter between the last two). `PipelineCanvas` draws the graph top to bottom (`CanvasGeometry`, `NodeLook`); `CanvasViewModel` holds the draft, undo and the check; `VideoOverlay` and `Shared/Vision.Client` draw on the live video; texts in `VisionTexts`. |

## How it works

| Folder or file | What |
|---|---|
| `VisionPlugin.cs`, `Api/VisionPlugin.Methods.cs` | `ICorePlugin`, event and action source, page methods, permissions, audit texts |
| `VisionParts.cs`, `Model/` | The contracts between the parts (`IModelLibrary`, `IInference`, `IFrameSource`, `IPipelineEvaluator`, `Pipeline`, `NodeKinds`, `VisionLimits`); tests hand fakes |
| `Models/` | `ModelLibrary` (store, chunked upload, Missing, backups), `ModelChecker`, `OnnxScan`, `OutputFormats`, `ClassNames`, `ClassLists`, `ModelTexts` |
| `Inference/` | `OnnxInference` (workers, sessions, the card), `Decoders`, `Preprocess`, `VisionFrame`, `VapixFrameSource` |
| `Graph/`, `Nodes/`, `Tracking/`, `Geometry/` | The check, the evaluator with one class per node kind, the tracker, the geometry |
| `Pipelines/` | `PipelineStore` (records `pipelines`, the owner scope), `PipelineRules` (save checks, texts), `PipelineRuntime`, `PipelineStats`, `SerialWork` |
| `Events/` | Event types, the sink, the rule actions |
| `../Shared/Vision/` | Shared with Text recognition: frames, engine, picture limits, previews, stats |
| `../Shared/Vision.Client/` | Shared with Text recognition: the picture overlay and the Stream settings dialog |

## Methods

| Method | Permission | What |
|---|---|---|
| `listPipelines`, `getPipeline` | show | The caller's pipelines with their state and live line; one pipeline |
| `savePipeline`, `deletePipeline` | change | Field and node errors in the reply, nothing stored on an error; audited |
| `checkGraph` | show | The server's check of a draft |
| `startPreview`, `stopPreview` | show | The page's preview of a draft: a 30 s lease, kept every 10 s |
| `pausePreview`, `resumePreview` | show + Live view | Holds the frame; the draft runs again on it after every change |
| `listModels` | show | The models and the pipelines that use each |
| `beginModelUpload`, `uploadModelChunk`, `finishModelUpload`, `cancelModelUpload` | models | The chunked upload and the model check; audited |
| `updateModel`, `deleteModel` | models | Rename, class names or list, format, fit, scaling, input size; delete refused while used; audited |
| `listClassLists` | show | The built-in and the user's class lists |
| `saveClassList`, `deleteClassList` | models | Create or change a list (its models follow), delete one no model uses; audited |
| `getEngine`, `setUseGpu` | show; change, all devices | What runs the models; Use GPU, audited |
| `getCameraOptions` | show | Video sources and sizes of a camera |
| `getStats` | show | The runtime stats; keeps the topic `stats` on for 30 s |

Events to the page: `pipelines`, `preview` (only to its user, at most twice a second), `models`, `stats`.

## Tests

`tests/Oadm.Plugins.Vision.Tests` (run with `--filter "FullyQualifiedName~Vision&Category!=Perf"`; no GPU, no network):

- `Models/`: generated ONNX models per format (`OnnxWriter`), the check and its refusals, the library, class lists.
- `Inference/`: decoding and fits on generated models; `TwoRuntimesTests` loads ONNX Runtime in several load contexts.
- `Graph/`: one test per rule and text of the check.
- `Tracking/`, `Geometry/`: box sequences in `Fixtures/Tracks/` (two people passing each other, a short occlusion, a
  back and forth, a crowd of 50); the generator script is not in git, the files are the reference.
- `Nodes/`: the evaluator end to end with a scripted `FakeInference` (`EvaluatorHarness`), the event state machines,
  a model failure, an errored branch; `EvaluatorPerfTests` is Perf.
- `Pipelines/`, `Events/`, `Api/`: the store, previews and leases, event types and actions, page methods and permissions.
- `Client/`: the canvas and the page view models, class lists, headless screenshots (`OADM_SCREENSHOT_DIR`).

Open: a real model test (DETR, RF-DETR) on the development cameras, a GPU run of both vision add-ons, macOS and Linux.
