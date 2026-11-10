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
| Formats | `OutputFormats` recognises the outputs, `Inference/Decoders` reads them: YOLO (`[1, 4 + C, N]` v8 when 4 + C < N, else `[1, N, 5 + C]` v5, own suppression at IoU 0.45 per class), YOLO with suppression (`[1, N, 6]`, N up to 1,000), YOLOX (`[1, N, 5 + C]` with N one row per grid cell of the strides 8, 16, 32 of the input: offsets in the cell and log sizes decoded, objectness x class, own suppression; an export with its decoding inside, centres beyond half the input in the blank call, is read as YOLO), DETR (`pred_logits` with "no object" last, softmax; `pred_boxes`; a Hugging Face `pixel_mask` input filled with ones), RT-DETR (Hugging Face `logits` with a sigmoid per class and `pred_boxes` by name: RT-DETR, D-FINE, Transformers.js YOLO26; told from a Hugging Face DETR, also named `logits`, by the blank call: "no object" the best class of half the queries or more is DETR), RF-DETR (boxes then logits, sigmoid, top 300; a masks output left out), DETR with labels (boxes, scores, labels; `orig_target_sizes` filled with the input size; pixels or 0 to 1), classification (`[1, C]`, softmax unless already probabilities; a free input takes 224, ImageNet scaling unless Ultralytics' metadata says 0 to 1). The order decides between DETR and RF-DETR, also with the `pred_` names (rf-detr-onnx: `pred_boxes`, `pred_logits`): logits first is DETR, boxes first RF-DETR. |
| Fit and scaling | Per model: Letterbox (YOLO, YOLOX), Stretch (RF-DETR, RT-DETR, DETR with labels, classification), Keep shape and pad (DETR); 0 to 1, ImageNet mean and deviation, or 0 to 255 BGR (YOLOX) (`Inference/Preprocess`). A picture input and outputs in 16-bit floats (exports with `--half`) are converted (`Models/HalfPrecision`). Free inputs a multiple of 32 from 32 to 2,048 (default 640); fixed inputs 8 to 4,096 per side. |
| Class names | Metadata `names` (Ultralytics' Python dict, a JSON list or object) when the count fits and they are more than the ids (`{0: '0', 1: '1'}` names nothing, its count still counts), else the labels file (another count refused), else a built-in list by count (91: `coco-91` "COCO, 91 ids", 80: `coco-80` "COCO, 80 classes"), else "Class <id>". A model on a list that is gone keeps its names as its own (one log line). "N/A" is a gap: never shown, its detections dropped. `Models/ClassLists`: the built-in lists and the user's (collection `classLists`, at most 50); a list's count is a model's class count. |
| Graph | `Graph/VisionGraph.Check`: one error per node in a fixed order (id first), graph errors for the whole pipeline, the input kind of every reached node. Texts in `Graph/GraphTexts`, read by the page's editor too. Settings are JSON (`NodeSettings`), defaults from the fields of `Model/NodeKinds`. |
| Evaluator | `Nodes/PipelineEvaluator`: the nodes that can run, parents first, each once per frame, outputs by reference; only nodes an Event, a Count or the eye needs (`EvaluatorOptions.AllNodes` for previews); Event nodes last, so a failing model call (`FrameResult.ModelFailure`) sends no events. Time comes only with the frame; `EvaluatorOptions.Time` measures the model calls. |
| Tracking | `Tracking/ObjectTracker`: Kalman filter per axis (`BoxFilter`), two greedy IoU rounds per class (high scores of 50 % or more against every track at IoU 0.2, the lower ones against the unmatched tracks seen last frame at 0.5), any unmatched detection starts a track, ids once confirmed, lost tracks kept by frame time, at most 300. |
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
| `copyPipeline` | change | Copies of a saved pipeline on other cameras, at most 100; audited |
| `checkGraph` | show | The server's check of a draft |
| `startPreview`, `stopPreview` | show | The page's preview of a draft: a 30 s lease, kept every 10 s |
| `pausePreview`, `resumePreview` | show + Live view | Holds the frame; the draft runs again on it after every change |
| `getModelView` | show + Live view | What a Detect objects or Classify node's model gets of the preview's frame |
| `listModels` | show | The models and the pipelines that use each |
| `beginModelUpload`, `uploadModelChunk`, `finishModelUpload`, `cancelModelUpload` | models | The chunked upload and the model check; audited |
| `updateModel`, `deleteModel` | models | Rename, class names or list, format, fit, scaling, input size; delete refused while used; audited |
| `listClassLists` | show | The built-in and the user's class lists |
| `saveClassList`, `deleteClassList` | models | Create or change a list (its models follow), delete one no model uses; audited |
| `getEngine`, `setUseGpu` | show; change, all devices | What runs the models; Use GPU, audited |
| `getCameraOptions` | show | Video sources and sizes of a camera |
| `getStats` | show | The runtime stats; keeps the topic `stats` on for 30 s |
| `listEventTypes` | show | The on and off events a Device event node may follow |

Events to the page: `pipelines`, `preview` (only to its user, after a new frame, at most 25 times a second), `models`, `stats`.

## Tests

`tests/Oadm.Plugins.Vision.Tests` (run with `--filter "FullyQualifiedName~Vision&Category!=Perf&Category!=RealModel&Category!=Gpu"`; no GPU, no network):

- `Models/`: generated ONNX models per format (`OnnxWriter`), the check and its refusals, the library, class lists.
- `Inference/`: decoding and fits on generated models; `TwoRuntimesTests` loads ONNX Runtime in several load contexts.
- `Graph/`: one test per rule and text of the check.
- `Tracking/`, `Geometry/`: box sequences in `Fixtures/Tracks/` (two people passing each other, a short occlusion, a
  back and forth, a crowd of 50); the generator script is not in git, the files are the reference.
- `Nodes/`: the evaluator end to end with a scripted `FakeInference` (`EvaluatorHarness`), the event state machines,
  a model failure, an errored branch; `EvaluatorPerfTests` is Perf.
- `Pipelines/`, `Events/`, `Api/`: the store, previews and leases, event types and actions, page methods and permissions.
- `Client/`: the canvas and the page view models, class lists, headless screenshots (`OADM_SCREENSHOT_DIR`).
- `RealModels/` (category `RealModel`, `manage test models`, never in the unit run or CI): RF-DETR medium, the YOLO
  family (`YoloTests`: YOLOv8n, YOLO11n with a free input, in 16-bit floats and without names, YOLOv5n, YOLOv10n, YOLO26n,
  YOLOX nano and tiny) and the DETR family (`DetrFamilyTests`: DETR R50 of Transformers.js, also quantized, RT-DETR v2
  R18, D-FINE S, YOLO26n of Transformers.js, RF-DETR small) on two COCO photos, and three ImageNet classifiers
  (`ClassificationTests`: ResNet-50, MobileNetV2, YOLO11n-cls with a labels file from `resnet-50-config.json`) on the two
  cars of a third COCO photo, CPU only: the model check of a chunked upload, the detections against
  COCO's boxes (`RealModelChecks`; a nano model may miss only the objects it was measured to miss, `MayMiss`, written
  out with its closest box at 5 %), Detect objects > Filter (person) > Count through the real evaluator (RF-DETR). `RealModels.targets` downloads the files below only with
  `-p:OadmRealModels=true` into the model cache of Text recognition (`$(OADM_MODEL_CACHE)`, else
  `%LOCALAPPDATA%\Oadm\model-cache` or `~/.cache/oadm/models`), one folder per SHA-256; a failed download or another
  hash leaves the file out and the tests skip. Never shipped, never in git. A test checks this table against the targets.
- `Gpu/` (category `Gpu`, `manage test gpu`, never in the unit run, `manage test all` or CI; one test process at a time,
  nothing else on the card): skips with the reason without a DirectML or CUDA card (`GpuCard`, no ONNX Runtime at
  discovery) or the real models. `VisionOnTheCardTests`: the real `OnnxInference` with Use GPU on (YOLO11n of opset 19
  in 32 and 16 bits, YOLO26n, RF-DETR medium, ResNet-50): card test passed, `RunsOn` names the card, the CPU's
  detections, card and CPU times written out, Use GPU off and on again; Ultralytics' YOLO11n of opset 22 runs where its
  card test says (DirectML has no opset 22 Conv). `BothAddOnsOnTheCardTests` starts the test assembly as a fresh process
  (`GpuChild`, the assembly's entry point) that loads Text recognition and Vision Center through `PluginLoader` from
  `gpu-plugins/` (copied by `-p:OadmGpuPlugins=true`; `OADM_GPU_PLUGINS` for other folders, such as published add-ons)
  and runs both on the card at once in each order; the same way a fresh process with Use GPU off must not load
  `DirectML.dll`.

| File | Source | Licence and attribution | SHA-256 |
|---|---|---|---|
| `rf-detr-medium.onnx` | `https://huggingface.co/PierreMarieCurie/rf-detr-onnx/resolve/94f91976449c187be38e649863e3dadecc312437/rf-detr-medium.onnx` (120,758,287 bytes) | Apache-2.0; RF-DETR by Roboflow, ONNX export by PierreMarieCurie | `18250b5a3a9552ae117d592f8e8c27fe52438d70c914a77a8913fc45d8f29e69` |
| `yolov8n.onnx` | `https://github.com/ultralytics/assets/releases/download/v8.4.0/yolov8n.onnx` (12,851,049 bytes) | AGPL-3.0 (Ultralytics' weights: tested, never shipped) | `b2bc52f40e8e1c532427d5bde3575a5d5b571b739fab2c6df443733ed1589cbd` |
| `yolo11n.onnx` | `https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo11n.onnx` (10,930,182 bytes, free input size) | AGPL-3.0 (Ultralytics) | `634279b40c07c6391472c51ad45b81ebc48706a9a1fe72dd3396322acd0c053b` |
| `yolo11n-fp16.onnx` | `https://huggingface.co/unity/inference-engine-yolo/resolve/1aea2ce54d688c056bc80ab94ffbc55f1e279b44/models/yolo11n.onnx` (5,384,988 bytes, 16-bit input and output) | AGPL-3.0 (Ultralytics' weights; the repository says GPL-3.0), export by Unity | `0690c675a942f4dc4f463b3e4e89520117aeea3fd767784df2934705c91ed11b` |
| `yolo11n-no-names.onnx` | `https://huggingface.co/Kalray/yolo11/resolve/1ca8238e34795b3beaa40c3a24e9ff4fb168f511/yolo11n.onnx` (10,719,255 bytes, no metadata) | AGPL-3.0 (Ultralytics' weights; the repository says Apache-2.0), export by Kalray | `d59282c9467eff7b8d567f61ea9a7a8adca1681761edad39c12877e39909e8bd` |
| `yolov5n.onnx` | `https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5n.onnx` (3,981,910 bytes, 16-bit input and output) | AGPL-3.0 (Ultralytics) | `04f0e55c26f58d17145b36045780fe1250d5bd2187543e11568e5141d05b3262` |
| `yolov10n.onnx` | `https://huggingface.co/onnx-community/yolov10n/resolve/57657320425ee34056408a57ad9d29c4d4815bd8/onnx/model.onnx` (9,386,116 bytes) | AGPL-3.0; YOLOv10 by THU-MIG, export by onnx-community | `a77dd863933f184a19e84361c64b788228a7c7dacc2c78939239a96ad3efca3b` |
| `yolo26n.onnx` | `https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.onnx` (9,941,957 bytes) | AGPL-3.0 (Ultralytics) | `2e947b787d9e787b93a16772a5f55b1d4d8c4d86f53146149c5d6a642442d6f7` |
| `yolox_nano.onnx` | `https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.onnx` (3,659,407 bytes) | Apache-2.0; YOLOX by Megvii | `c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d` |
| `yolox_tiny.onnx` | `https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_tiny.onnx` (20,219,662 bytes) | Apache-2.0; YOLOX by Megvii | `427cc366d34e27ff7a03e2899b5e3671425c262ea2291f88bb942bc1cc70b0f7` |
| `rf-detr-small.onnx` | `https://huggingface.co/PierreMarieCurie/rf-detr-onnx/resolve/94f91976449c187be38e649863e3dadecc312437/rf-detr-small.onnx` (114,424,202 bytes) | Apache-2.0; RF-DETR by Roboflow, ONNX export by PierreMarieCurie | `0e0817f4cafa479ccba17662a142092932b0b10c98947e7cf60f3badd0f5c219` |
| `detr-resnet-50.onnx` | `https://huggingface.co/Xenova/detr-resnet-50/resolve/8be7ab59ff663484ee9ba2e8d8f267330d5ad03e/onnx/model.onnx` (166,789,212 bytes) | Apache-2.0; facebook/detr-resnet-50, Transformers.js export by Xenova | `5dffb3205e68b0be1686eea283ecf87e7592d3dceed5dfc83249f7303e9e95e8` |
| `detr-resnet-50-quantized.onnx` | `https://huggingface.co/Xenova/detr-resnet-50/resolve/8be7ab59ff663484ee9ba2e8d8f267330d5ad03e/onnx/model_quantized.onnx` (43,102,531 bytes, 8-bit weights) | Apache-2.0; as above | `cae09a307ed9247da7e2ce8bcf81522a6817f1ea2e82b9c4dde59f5964b62b4f` |
| `rtdetr-v2-r18vd.onnx` | `https://huggingface.co/onnx-community/rtdetr_v2_r18vd-ONNX/resolve/936f90b6a476c6da4dfe053fc521af55285976ba/onnx/model.onnx` (81,057,510 bytes) | Apache-2.0; PekingU/rtdetr_v2_r18vd, export by onnx-community | `583a236ac21c95a7fd94f284fc21485e42355bfef82c27011ba78fbc09ee87e2` |
| `dfine-s-coco.onnx` | `https://huggingface.co/onnx-community/dfine_s_coco-ONNX/resolve/a3cf03147a9b86c78475139115c8ac142577352d/onnx/model.onnx` (41,535,197 bytes) | Apache-2.0; ustc-community/dfine_s_coco (D-FINE), export by onnx-community | `cd8a49a945feda6d28c6304ae8ae85c2759ba1d78a5a83a22c5ce8db82ef7238` |
| `yolo26n-transformersjs.onnx` | `https://huggingface.co/onnx-community/yolo26n-ONNX/resolve/a8dc7e14743e1cea8ccd493bd99b4c2827de1acf/onnx/model.onnx` (9,890,454 bytes) | AGPL-3.0 (Ultralytics' weights: tested, never shipped), export by onnx-community | `cda08d9440217e243e075ee839f40383c59b3f973e493f9f6c7452922a69436e` |
| `resnet-50.onnx` | `https://huggingface.co/Xenova/resnet-50/resolve/a16f29143f08c4e5c5a4a89f6f185d8edac4b6af/onnx/model.onnx` (102,157,946 bytes) | Apache-2.0; microsoft/resnet-50, Transformers.js export by Xenova | `7e6fd9fa712c93139c2dab22fe109232d9fb760e3ddea117110f72de9d4f665a` |
| `resnet-50-config.json` | `https://huggingface.co/Xenova/resnet-50/resolve/a16f29143f08c4e5c5a4a89f6f185d8edac4b6af/config.json` (69,729 bytes, the ImageNet names in `id2label`) | Apache-2.0; as above | `47c111a56be828e8b0c4b7860be8279abf78aae0287c9baf5981dc813bf43606` |
| `mobilenetv2-12.onnx` | `https://media.githubusercontent.com/media/onnx/models/c5cae0f1942c8d7727372c7b1f6b485aa39e4421/validated/vision/classification/mobilenet/model/mobilenetv2-12.onnx` (13,964,571 bytes) | Apache-2.0; ONNX Model Zoo | `c0c3f76d93fa3fd6580652a45618618a220fced18babf65774ed169de0432ad5` |
| `yolo11n-cls.onnx` | `https://huggingface.co/mobilint/YOLO11n-cls/resolve/ea1f8e6d3e3f4441bbc09052ab532d36e694186d/yolo11n-cls.onnx` (11,255,379 bytes, no metadata) | AGPL-3.0 (Ultralytics' weights: tested, never shipped), file of mobilint | `fe2a60c3a3dd68bc5ef28195caa0300e5622ef814cfab01f6c14c1e5589edd55` |
| `coco-000000097679.jpg` | `http://images.cocodataset.org/val2017/000000097679.jpg` (a silver sedan and a white station wagon) | CC BY 2.0, photo by tedpercival on Flickr (https://www.flickr.com/photos/tedpercival/2727641554/), from COCO val2017 (annotations CC BY 4.0) | `7d636d406352e1643cab6f85925dd2fccd7ac11c471f1c58bd627a9512154efc` |
| `coco-000000278705.jpg` | `http://images.cocodataset.org/val2017/000000278705.jpg` (two skaters, two cars) | CC BY 2.0, photo by essers on Flickr (https://www.flickr.com/photos/essers/2875114693/), from COCO val2017 (annotations CC BY 4.0) | `d01c906a85c3f77702bdbf9dc7cedf11310e9aa47c6730927f51d9651dece310` |
| `coco-000000065350.jpg` | `http://images.cocodataset.org/val2017/000000065350.jpg` (three people at a skate bowl, cars and bicycles behind) | CC BY 2.0, photo by valdeklaur on Flickr (https://www.flickr.com/photos/valdeklaur/4869131880/), from COCO val2017 (annotations CC BY 4.0) | `963a129714e3675b99b6a405a2cb46eca5678241358bb6b6bbefc3c3f1943ae0` |

Measured 2026-11-08 (development PC, 24 cores, one CPU thread per call): RF-DETR medium about 860 ms per picture, the
YOLO nano models 80 to 110 ms, YOLOX nano 27 ms, YOLOX tiny 74 ms, DETR R50 at 640 about 690 ms (quantized 990 ms),
RT-DETR v2 R18 710 ms, D-FINE S 460 ms, RF-DETR small 645 ms, ResNet-50 76 ms, MobileNetV2 10 ms, YOLO11n-cls 8 ms (table
in the spec, "Measured").

Open: a real model test (DETR, RF-DETR) on the development cameras, a CUDA run on Linux, macOS and Linux.
