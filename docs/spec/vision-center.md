# Vision Center

Status: **spec, not built** (2026-11-08). Decisions of the maintainer on 2026-11-08: no model ships, the user uploads
their own; pipelines branch but never loop or merge; counts are live and reach rules as events, nothing is stored;
free; detection and classification in the first version, anomaly later; own nodes only, designed for an SDK later; page
with the pipeline list, the canvas and the live video side by side.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Runs the user's own ONNX models on the cameras' pictures and lets the user build what they need from nodes, like n8n:
objects in an area, objects that cross a line, counting both, filtered by class, score or size, with rule events at the
end. The second vision plugin, an add-on like Text recognition (`docs/spec/add-ons.md`, `docs/spec/ocr.md`): not in the
OADM installers, installed on demand on the Plugins page.

Core plugin `plugins/Oadm.Plugins.Vision` (+ `.Client`), id `oadm.vision`, rail page **Vision Center** in the rail group
**Vision** (after Text recognition), free (Apache-2.0), **off by default**.

## Rules that never break

1. **Read only.** The plugin only receives pictures from the cameras; it never changes a setting on a device.
2. **Pictures never stored.** Frames live in server memory while a pipeline works on them, never on disk, never in a log.
   A paused preview holds one frame per preview, as in Text recognition ("Pause"). Events carry values (counts, classes,
   directions), never pictures. Counts live in memory only (decision 2026-11-08): gone on a server restart.
3. **No model ships.** The add-on holds the runtime, never a model (decision 2026-11-08). An administrator uploads the
   models; models we tested may be offered later as downloads on oadm.app, never fetched by the server.
4. **A model is data, never code.** A model runs only through ONNX Runtime's standard operators: no custom operator
   library is ever loaded, a model with an operator domain other than `ai.onnx`, `ai.onnx.ml` and `com.microsoft` or with
   external data files is refused at upload ("Models"). Uploading needs **Manage vision models** and is audited.
5. **Choose the camera first.** A pipeline starts from one camera (`DevicePickerWindow.ShowOneAsync`) and one of its
   video sources; nothing streams before that. Only cameras of the user's device group; pipelines are owned by the device
   scope that made them (`docs/spec/device-scope.md`). A saved pipeline keeps its camera.
6. **Bounded load.** Inference has its own worker budget and drops frames instead of queueing them, the newest wins
   ("Load"). At most 50 pipelines per server, watching ones by the server's cores, 4 per site.
7. **GPU when there is one, the CPU always works.** Same engine rules as Text recognition ("GPU" of `docs/spec/ocr.md`):
   DirectML on Windows x64, CUDA on Linux x64, else the CPU; a missing or failing card falls back to the CPU with one log
   line.
8. **The graph is checked before it runs.** A pipeline whose graph has an error (a loop, a missing input, a node that
   needs another kind of input, a missing model) is never run, saved pipelines included; the error shows on its node.
9. **What a model detects is the user's choice.** OADM ships no face, person identity or plate model; the docs say that
   the user is responsible for the models they run and for the privacy law of their site.

## Models

A model is a stored ONNX file with a name, a kind and what it needs to be read. Server-wide, not per device group.

- **Kinds** (first version): **Detection** (boxes with class and score) and **Classification** (one class and score per
  picture). Anomaly detection (a score and a heat map per picture, e.g. models exported from Anomalib) is the next kind:
  the node model below takes it without a change of the pipeline format ("Later").
- **Output formats**, recognised from the output names and shapes, the user confirms or picks:
  - Detection **YOLO**: `[1, 4 + C, N]` (YOLOv8 and later) or `[1, N, 5 + C]` with objectness (YOLOv5); the plugin runs
    the non-maximum suppression (IoU 0.45, per class).
  - Detection **YOLO with suppression**: `[1, N, 6]` (x1, y1, x2, y2, score, class; models exported end to end).
  - Detection **DETR**: the original DETR of Facebook Research (`facebookresearch/detr`, Apache-2.0; the first model the
    maintainer tests, decision 2026-11-08) exported to ONNX: `pred_logits` `[1, Q, C + 1]` (the last class is "no
    object") and `pred_boxes` `[1, Q, 4]` (centre x, centre y, width, height, 0 to 1 of the input). Softmax per query,
    the best class other than "no object" with its probability as the score; no suppression. Input with ImageNet mean and
    deviation (preselected for this format), the picture scaled to the input with its shape kept, padded. The COCO models
    have 91 class ids with gaps ("N/A"): the labels file names them, a class named "N/A" is never shown or chosen.
  - Detection **RF-DETR**: RF-DETR of Roboflow as exported by `PierreMarieCurie/rf-detr-onnx` (code MIT, models
    Apache-2.0 except xlarge and 2xlarge, which are under the Platform Model License 1.0; the second model the maintainer
    tests, `rf-detr-medium.onnx`, decision 2026-11-08): output 0 boxes `[1, Q, 4]` (centre x, centre y, width, height,
    0 to 1), output 1 class logits `[1, Q, C]` with a sigmoid per class and no "no object" class, the best class per
    query, the top 300 queries; a third output (the masks of the segmentation models) is left out until segmentation
    comes ("Later"), so those models detect boxes. Input of a fixed size read from the model, ImageNet mean and
    deviation, the picture **stretched** to the input (not letterboxed: the export was trained that way).
  - **Fit**: Letterbox (YOLO), Stretch (RF-DETR) or Keep shape and pad (DETR), preselected by the format, a choice per
    model, since the wrong fit loses accuracy without an error.
  - Detection **DETR with labels**: boxes, scores and labels outputs (RT-DETR, D-FINE and other end-to-end exports); no
    suppression.
  - Classification: `[1, C]`, logits or probabilities (softmax when the values are not already probabilities).
- **Input**: one picture tensor `[1, 3, H, W]`. A fixed size is read from the model, a free one takes the user's size
  (default 640 x 640, a multiple of 32). The picture is fitted as the model's Fit says (detection) or resized
  (classification), RGB.
  Scaling: 0 to 1 (default) or ImageNet mean and deviation, a choice per model.
- **Class names**: from the model's metadata (`names`, as Ultralytics exports write it) when there is one, else a
  labels file uploaded with it (one name per line, as many as classes), else a built-in class list by the class count
  (91: COCO with its ids, 80: COCO for YOLO), else "Class 0", "Class 1", ...
- **Class lists** (decision 2026-11-08): profiles of class names, **chosen on the model**, never on a node (the ids
  belong to how the model was trained). Built in, read only: "COCO (91 ids)" (`coco-91`, the COCO category ids DETR and
  RF-DETR use, gaps "N/A") and "COCO (80 classes)" (`coco-80`, YOLO). The user's own lists are server-wide like the
  models (at most 50, Manage vision models, audited); a list with another count of names than the model's classes
  cannot be chosen for it; a list in use cannot be deleted. A model's ⋯ menu **Class list** offers the fitting lists
  and "Own names" (edited one per line). Detect objects shows the model's list read only; the Classes pickers and every
  label follow the names in effect. Backups keep the user's lists.
- **Upload** (Settings card **Models** on the page, Administrators): the client sends the file in chunks like an add-on
  ZIP (`docs/spec/add-ons.md` "Server" step 2), hashed on the way, to `plugin-data/oadm.vision/models/<id>.onnx`. Then the
  **model check**: refused domains and external data (rule 4), a session on the CPU, one call with a blank picture, the
  output format recognised. A model that fails the check is not kept: "This model cannot run here: <reason>." Shown
  per model: name, kind, input size, classes, size, SHA-256, the time of one call on the CPU and on the card.
- **Limits**: at most 20 models, each at most 500 MB, 4 GB together, at most 1,000 classes.
- **Delete** is refused while a pipeline uses the model ("Used by 2 pipelines: Gate 1, Lane 2.").
- **Backups** keep the model list (name, kind, settings, SHA-256), never the files, like add-on files. After a restore a
  model without its file is **Missing**: its pipelines show "Upload the model Gate people again." Uploading a file with
  the same SHA-256 brings it back with its settings.

## Pipelines

A pipeline is a saved graph of nodes on one camera: name, mode, camera, video source, resolution, frame rate and the
nodes with their links and places on the canvas (JSON, camelCase, one row per pipeline, like the readers of Text
recognition).

- **Modes** as for Text recognition readers:
  - **Watching** (default): runs on the camera's MJPEG stream at 1, 2, 4, 8, 12 or 15 frames per second (default 8:
    tracking needs at least about 5), the same stream rules as a reader (`VapixRequestOptions.LiveStream`, reopened after a
    drop, the problems in the list).
  - **On action**: runs on one picture when a rule runs the action **Run pipeline**. Nodes that need a stream (Track,
    Line, and Count on crossings) are refused in this mode, on their node: "Track works only while Watching."
- **Resolution**: default 1280x720 or the camera's largest below it (the model sees its own input size; a larger picture
  only helps small objects).
- **Graph**: branches, no loops, no merges (decision 2026-11-08). Exactly one Camera node, the start. Every other node has
  exactly one input and any number of links from its output. At most 32 nodes, at most 2 model nodes (Detect, Classify)
  per path from the camera. The server checks the graph at every save (`VisionGraph.Check`) and the editor checks it while
  the user edits, with the same rules and texts.
- **Typed links**: every output has one kind; an input takes only the kinds its node lists. Kinds: **Picture**,
  **Objects** (boxes with class, score, attributes; **tracked** once a Track node passed them: each object keeps an id),
  **Crossings** (an object crossed a line, with its direction), **Number** (a live value).

### Nodes

| Node | Input | Output | What it does |
|---|---|---|---|
| **Camera** | none | Picture | The pipeline's camera and stream (summary "AXIS Q1798-LE · 1280x720 @ 8 fps"); Stream opens the Stream settings dialog of Text recognition |
| **Detect objects** | Picture | Objects | A detection model; Minimum score (default 50 %), Classes (all, or the chosen ones); at most 300 objects per picture, the highest scores first |
| **Classify** | Picture or Objects | Picture or Objects | A classification model. On a picture: the whole picture or an area drawn on it. On objects: each box cut out with a margin of 10 %, the class and score added as an attribute (`label`), at most 16 objects per picture; on tracked objects once per object and again every 2 s |
| **Filter** | Objects | Objects | Keeps the objects that pass all its checks: class, score, box size (min and max share of the picture), the attribute of a Classify node (class, score) |
| **Track** | Objects | Objects (tracked) | Gives each object an id that stays while it moves (own C#, ByteTrack-like: Kalman filter and two rounds of IoU matching). Confirm after (default 3 frames), Keep lost objects for (default 1 s) |
| **Area** | Objects | Objects | A polygon of 3 to 16 points drawn on the video; passes the objects inside. Inside by (default) the box's bottom centre (feet, wheels), its centre or any overlap. With tracked objects also the time each one has been inside |
| **Line** | Objects (tracked) | Crossings | A line of 2 to 8 points drawn on the video, with the direction In shown as an arrow; counts a crossing when a tracked object's bottom centre passes it. Directions: In, Out or both |
| **Count** | Objects or Crossings | Number | Objects: how many are there now. Crossings: In, Out and In minus Out since the last reset. Reset daily at a time (optional), by the rule action **Reset counter**, and on a server restart |
| **Event** | Objects, Crossings or Number | none | Publishes a rule event ("Events"); its name is the event's name |

- A node has a name (unique in the pipeline, at most 64 characters, "Area 1" with the lowest free number like the areas
  of Text recognition); rules and dashboards name it.
- Areas and lines are stored relative to the picture (0 to 1), so a change of resolution keeps them in place.
- Every node's settings are declared by the node itself (fields like `ActionField`: text, number, choice, model,
  classes, a shape on the video), so the editor draws the form from them and a node kind added later needs no editor
  change. The form follows the validation rule of the host (`ValidatingViewModel`, errors below the field).

### Running

- A frame runs through the graph from the camera, each node once per frame, the outputs handed by reference (no copy of
  objects per link). Branches after a model node share its result: two Lines after one Track run one model call.
- A node works only when something after it needs it: a branch that ends without an Event or Count node and is not shown
  on the page costs nothing.
- Results are taken in the order of the frames: an older frame never replaces a newer one (as in Text recognition).
- A watching pipeline whose models fail 3 times in a row has a problem until a frame works, as a reader does.

## Events and the rule actions

- Per Event node, folder Vision Center / pipeline name, the event name is the node's name; the camera is its fixed
  device (`FixedDeviceId`). What it publishes follows its input:
  - **Objects**: stateful, on while at least **Minimum objects** (default 1) are there for **At least** (default 1 s),
    off once fewer stayed for **Off after** (default 2 s). Values: Count, Classes (the classes there, separated by a
    space). With tracked objects also a stateless **Object entered** and **Object left** per object, values Class,
    Object (the id), Time inside (s).
  - **Crossings**: stateless, one per crossing. Values: Direction (In / Out), Class, Object, Score.
  - **Number**: stateless **Count changed**, at most once a second with the latest value, values Count, In, Out; and
    stateful **Above limit**, on while the count is at least **Limit**.
  - In On action mode an Event node on Objects fires **Objects found** (Count, Classes) or **No objects found** once per
    run, like Text read and No text read.
- Event **Pipeline problem** per pipeline and **Any pipeline problem** (server, users with all devices), as Reader
  problem and Any reader problem of Text recognition.
- Rule action **Run pipeline** (an On action or a watching pipeline; a watching one answers with its latest frame of at
  most 1 s ago, as Read text does) and **Reset counter** (a pipeline and one of its Count nodes; "Choose a pipeline." and
  "Choose a counter." at the rule's save).
- Counts reach dashboards as event values like any other; Count is the split field (`SplitField`) of nothing: each Event
  node is its own event.

## Page

Three columns (decision 2026-11-08, "Canvas + side video"): pipelines (fixed 320 px) | the canvas (the rest) | the live
video above the selected node (fixed, about 420 px). Page header: the description, "Runs on" with Use GPU and the load
on the right, as Text recognition.

- **Left: the pipelines**: name, camera, mode, state chip ("Watching · 8 fps", orange when slower, red per kind of
  problem with the server's text as tooltip, the chips of Text recognition), **New** (the camera first), **Delete** (red,
  confirmation) and a search. The card **Models** below it for users with Manage vision models: Upload model, the models
  with their ⋯ menu (Rename, Classes, Delete in red). Empty page: "Upload a model, then create a pipeline."
- **Middle: the canvas**, our own Avalonia control (no node editor exists for Avalonia), close to n8n so users who know
  it feel at home:
  - Nodes as cards with the node's icon, its name and one live line (below). The graph runs **top to bottom**
    (decision 2026-11-08, replaces left to right: the canvas is taller than wide): the input at the top centre of a card,
    the output at its bottom centre, links as curves from an output down to the next input. A dot grid on the theme's
    background.
  - Add: **Add node** in the toolbar opens the node list (search, grouped: Models, Objects, Geometry, Output); the **+**
    below a node's output opens the same list with only the nodes that fit its kind, and links the new node below it (a
    second child beside the first).
  - Drag from an output to an input links them; while dragging, inputs that do not fit are dimmed and a drop there
    does nothing. A link is selected by a click and deleted with Delete.
  - Move nodes by dragging, pan by dragging the empty canvas, zoom with the wheel (25 % to 200 %), **Fit view** and
    **Tidy up** (lays the graph out by depth from top to bottom, branches side by side, a parent centred above its
    children) in the toolbar. Places are stored with the pipeline.
  - Select a node with a click; Delete removes it with its links; Ctrl+Z and Ctrl+Y undo and redo inside the draft until
    it is saved; Ctrl+C and Ctrl+V copy nodes within the pipeline.
  - A node with an error has a red border and its error directly below it on the canvas (the validation rule of
    `CLAUDE.md`); Save is off while one exists, its tooltip says why.
  - Not saved, the save status and **Save** at the canvas's bottom right, as in Text recognition.
- **Right, top: the live video** (the host's live video component, picture only) with the overlay of the nodes whose
  **eye** is on (a toggle on every node card, tooltip "Show on video"; the selected node always shows):
  - Detect objects: boxes with class and score. Filter: the kept boxes, the dropped ones dashed. Track: boxes with the id
    and a short trail. Classify: the attribute on the box. Area: the polygon, the objects inside filled lightly. Line:
    the line with the In arrow and "In 12 · Out 7". Count: the number in the corner.
  - Each node keeps one colour on the canvas and on the video, from the theme's chart colours.
  - Areas and lines are drawn and edited on the video like the areas of Text recognition (New, drag, handles, Esc, the
    arrow keys). An Area or Line node added on the canvas starts drawing at once.
  - Pause holds the frame as in Text recognition; while paused the draft runs again on the same frame after every change,
    so a minimum score or a filter can be tuned on one picture.
- **Right, below: the selected node**: its settings form, then its **live output**, the feedback the user asked for:
  - Detect, Filter, Track, Area: objects in this frame, a table of them (Object, Class, Score, Time inside), the node's
    time per frame. Line: the last 20 crossings (Time, Direction, Class, Object). Count: the value. Event: whether it is
    on, the last 20 times it fired with its values (a preview's own list: a preview never publishes, "Previews").
  - Nothing selected: the pipeline's own settings (name, mode).
- **The live line on each node card** (n8n's item counts): Detect "5 objects · 18 ms", Track "3 objects", Area "2
  inside", Line "In 12 · Out 7", Count "14", Event "On" or "Fired 3 s ago"; the number of objects on each link while the
  page shows. Sent at most twice a second per pipeline.
- **Previews**: while the page shows a pipeline, the server runs its draft as a preview, as Text recognition does
  (`startPreview` on every change and every 10 s, a lease of 30 s, at most 8 per server and 2 per user, a saved unchanged
  watching pipeline shares its stream, the others count against the watching limit). A preview publishes no events and
  changes no counter of the saved pipeline; its counts start at 0.
- **Loading feedback**: "Reading the pipelines" and "Reading the pipeline" overlays as in Text recognition; an upload shows
  its progress and Cancel in the model's row; the model check a ring in the row.

## Load

- Its own inference workers: on the CPU `max(1, cores / 2)` model calls at once, each on one thread; all pipelines share
  one session of each model and size. With a card the models run there when they are not clearly slower (the card test
  of Text recognition, per model at its upload and at the start).
- A pipeline has one frame in work, up to 4 while the workers have room, as a reader ("Bounded load" of
  `docs/spec/ocr.md`); a frame without room is dropped, the newest wins.
- **Watching pipelines**: 2 per worker, at least 4 and at most 50 (a detection model costs more than an area of text);
  per site 4; previews with a stream of their own count with them. Beyond: the problem "At most 8 pipelines can watch at
  once. Set another pipeline to On action first."
- **Runtime stats** and the traffic light as for Text recognition: load bar, "All pipelines keep up", "Busy: 2 pipelines
  run slower than set", "Overloaded: lower the frames per second or the number of pipelines"; per node the time per frame
  in its live line.
- The two vision add-ons each keep their own budget; both on one server can fill its cores. The traffic lights show it
  (open point below).

## Permissions

- **View vision pipelines**: the page, the pipelines, their live output. The video, Pause and object cut-outs need the
  host's **Live view** too.
- **Change vision pipelines**: New, Delete, Save, the canvas, drawing areas and lines, Use GPU (with all devices).
- **Manage vision models**: upload, rename, delete models (Administrators by default, all devices: models are
  server-wide); audited "Uploaded vision model <name>", "Deleted vision model <name>".
- Running the actions is part of the event rules' own permissions.

## Parts and contract

- **Shared with Text recognition, never copied** (the reuse rule of `CLAUDE.md`): the frame source (MJPEG through
  VAPIX), the engine (ONNX Runtime sessions, the card test, Use GPU, the busy meter), previews and their leases and the
  runtime stats move from `plugins/Oadm.Plugins.Ocr` into `plugins/Shared/Vision/` (linked source, each add-on keeps its
  own load context). The client's picture overlay (letterboxing, Pause, drawing and moving shapes, `PicturePlace`,
  `AreaGeometry`) and the Stream settings dialog move into `plugins/Shared/Vision.Client/`; rectangles of Text recognition
  become one case of the polygon editor. Text recognition's behaviour and tests stay as they are.
- In the plugin: `Models/` (store, check, decoders per output format), `Graph/` (the pipeline format, `VisionGraph.Check`,
  the node kinds), `Nodes/` (one class per node kind, behind `IVisionNode`: declared inputs, output, fields, overlay;
  the contract an SDK may open later, decision 2026-11-08), `Tracking/`, `Geometry/` (inside, crossing), `Pipelines/` (the
  runtime, previews), `Events/`, `Api/` (the page's payloads).
- The pipeline JSON has a `version`; a node kind the plugin does not know (a newer pipeline after a restore on an older
  OADM) keeps the pipeline stored but not run, with its node's error "This node needs a newer Vision Center."

## Packaging

- Add-on ZIP per server platform (`docs/spec/add-ons.md` "Release files") with ONNX Runtime of the same version as Text
  recognition, SkiaSharp and no model. The client part has no native libraries.

## Tests

- Decoders: small ONNX models generated by the tests for each output format (YOLO, YOLO with suppression, DETR,
  RF-DETR with two and with three outputs, DETR with labels, classification), a model with a refused domain and one with
  external data.
- **Second real model: RF-DETR** (`https://huggingface.co/PierreMarieCurie/rf-detr-onnx/resolve/main/rf-detr-medium.onnx`,
  downloaded by the user, uploaded as it is): recognised as RF-DETR without a choice, the same pipeline as DETR works.
- **First real model: DETR** (`facebookresearch/detr`, DETR-R50 on COCO). The user docs give the export: the PyTorch
  Hub model, `torch.onnx.export` with the outputs named `pred_logits` and `pred_boxes`, a fixed input of 800 x 800 (or a
  free one), opset 17; and the COCO labels file. The upload, the model check and a pipeline Detect objects > Filter
  (person) > Track > Line > Count > Event must work with that file. It is slow on the CPU (about a second per picture at
  800 x 800, to be measured): the page's load and the node's time per frame say so, a graphics card or a lower frame rate
  is the answer.
- The graph check: loops, merges, wrong kinds, a Line without Track, On action with Track, too many nodes and model nodes,
  an unknown node kind. The same cases through the editor's view model.
- Tracker and geometry from recorded detections (box sequences as JSON in `Fixtures/`): ids stay through a short
  occlusion, a crossing counts once, a back and forth counts In and Out, the bottom centre decides inside.
- Counts, events (on, off, At least, Off after, Above limit, Count changed at most once a second), Reset counter, the
  daily reset with a `FakeTimeProvider`.
- Limits, previews and leases, device groups, the permissions, the model upload with its refusals and the backup of the
  model list.
- The canvas: adding, linking, refusing a link, undo, the errors below the node; headless screenshots for the look.
- A real detection model only in tests that download a permissive one at the test build into a cache with its pinned
  SHA-256 (never shipped, never in git), like the models of Text recognition.

## Later

- Anomaly detection as a model kind and a node (a score per picture or area, an event above a limit).
- Models we tested (DETR-style, Apache-2.0) as downloads on oadm.app.
- Nodes from other plugins through the SDK (`IVisionNode` opened as an SDK contract).
- Segmentation and pose, merges (AND / OR of two branches), counts kept as history.
- Text recognition as a node.

## Open points

- ONNX Runtime and DirectML loaded by two add-ons in one server process (two load contexts, the same native library
  names): to be tested before it is built; if it fails, one shared runtime folder for the vision add-ons.
- One worker budget for both vision add-ons instead of one each.
- The default frame rate and resolution, from measurements with a real model on the development cameras.
