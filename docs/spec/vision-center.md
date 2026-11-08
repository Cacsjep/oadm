# Vision Center

Status: **built** (2026-11-08). Open: a run of both vision add-ons on a graphics card in one server (DirectML and CUDA),
the accuracy with real models on the development cameras (DETR, RF-DETR), macOS and Linux runs ("Open points").
Decisions of the maintainer on 2026-11-08: no model ships, the user uploads their own; pipelines branch but never loop
or merge; counts are live and reach rules as events, nothing is stored; free; detection and classification in the first
version, anomaly later; own nodes only, designed for an SDK later; page with the pipeline list, the canvas and the live
video side by side.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
The developer details: `plugins/Oadm.Plugins.Vision/README.md`.

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
   library is ever loaded; a model whose nodes or functions use an operator domain other than the default, `ai.onnx`,
   `ai.onnx.ml` and `com.microsoft`, or that needs external data files, is refused at upload ("Models"). Uploading needs
   **Manage vision models** and is audited.
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
- **Output formats**, recognised from the output names and shapes (`Models/OutputFormats`):
  - Detection **YOLO**: `[1, 4 + C, N]` (YOLOv8 and later) or `[1, N, 5 + C]` with objectness (YOLOv5); the plugin runs
    the non-maximum suppression (IoU 0.45, per class). A `[1, a, b]` output with `a < b` is v8, else v5.
  - Detection **YOLO with suppression**: `[1, N, 6]` with N up to 1,000 (x1, y1, x2, y2, score, class; models exported
    end to end).
  - Detection **DETR**: the original DETR of Facebook Research (`facebookresearch/detr`, Apache-2.0; the first model the
    maintainer tests, decision 2026-11-08) exported to ONNX: `pred_logits` `[1, Q, C + 1]` (the last class is "no
    object") and `pred_boxes` `[1, Q, 4]` (centre x, centre y, width, height, 0 to 1 of the input). Softmax per query,
    the best class other than "no object" with its probability as the score; no suppression. Input with ImageNet mean and
    deviation (preselected for this format), the picture scaled to the input with its shape kept, padded. The COCO models
    have 91 class ids with gaps ("N/A"): a class named "N/A" is never shown or chosen, and its detections are dropped.
  - Detection **RF-DETR**: RF-DETR of Roboflow as exported by `PierreMarieCurie/rf-detr-onnx` (code MIT, models
    Apache-2.0 except xlarge and 2xlarge, which are under the Platform Model License 1.0; the second model the maintainer
    tests, `rf-detr-medium.onnx`, decision 2026-11-08): output 0 boxes `[1, Q, 4]` (centre x, centre y, width, height,
    0 to 1), output 1 class logits `[1, Q, C]` with a sigmoid per class and no "no object" class, the best class per
    query, the top 300 queries; a third output (the masks of the segmentation models) is left out until segmentation
    comes ("Later"), so those models detect boxes. Input of a fixed size read from the model, ImageNet mean and
    deviation, the picture **stretched** to the input (not letterboxed: the export was trained that way). Two outputs
    without the `pred_` names: logits first is DETR, boxes first is RF-DETR.
  - Detection **DETR with labels**: boxes, scores and labels outputs (RT-DETR, D-FINE and other end-to-end exports); no
    suppression. An optional input `orig_target_sizes` `[1, 2]` is filled with the input size; boxes in input pixels or
    0 to 1; Stretch by default.
  - Classification: `[1, C]`, logits or probabilities (softmax when the values are not already probabilities), fitted by
    its model's Fit (Stretch by default).
  - **Fit**: Letterbox (YOLO), Stretch (RF-DETR, DETR with labels, classification) or Keep shape and pad (DETR),
    preselected by the format, a choice per model, since the wrong fit loses accuracy without an error. A format whose
    outputs do not fit the model is refused: "The model's outputs do not fit its format. Choose another format."
- **Input**: one picture tensor `[1, 3, H, W]` in floating point. A fixed size (8 to 4,096 per side) is read from the
  model, a free one takes the user's size (default 640 x 640, a multiple of 32 from 32 to 2,048). RGB. Scaling: 0 to 1
  (default) or ImageNet mean and deviation, a choice per model.
- **Class names**: from the model's metadata (`names`, as Ultralytics exports write it) when its count fits the classes,
  else a labels file uploaded with it (one name per line, as many as classes; another count is refused), else a built-in
  class list by the class count ("Class lists"), else "Class 0", "Class 1", ... Formats whose outputs do not give the
  class count (YOLO with suppression, DETR with labels) take the names of the metadata or the labels file, else "Class
  <id>" for the ids they report.
- **Upload** (card **Models** on the page, Manage vision models): the client sends the file in chunks like an add-on
  ZIP (`docs/spec/add-ons.md` "Server" step 2), hashed on the way, to `plugin-data/oadm.vision/models/<id>.onnx`; an
  upload without a chunk for 1 minute ends. Then the **model check** (`Models/ModelChecker`): refused domains and
  external data (rule 4), a session on the CPU, one call with a blank picture, the output format recognised. A model
  that fails the check is not kept: "This model cannot run here: <reason>." (a load error gives ONNX Runtime's message,
  at most 200 characters). A new model is named after its file ("name 2" when taken). A file whose SHA-256 is already
  stored is refused: "This model is already stored as <name>." Shown per model: kind, input size, classes, class list,
  size, the pipelines that use it, SHA-256, the time of one call on the CPU and on the card. The page's ⋯ menu of a model:
  Rename, Class list, Delete; the server also takes format, fit, scaling and input size changes (`updateModel`).
- **Limits**: at most 20 models, each at most 500 MB, 4 GB together, at most 1,000 classes. Uploads in progress count;
  a Missing model counts once its file is stored again.
- **Delete** is refused while a pipeline uses the model ("Used by 2 pipelines: Gate 1, Lane 2."), checked against every
  pipeline; a user limited to a device group sees only their own pipelines in Used by.
- **Backups** keep the model list (name, kind, settings, SHA-256) and the user's class lists, never the files, like
  add-on files. At the start a model whose file is absent or of another size is **Missing** (no hash at every start):
  its pipelines show "Upload the model Gate people again." Uploading a file with the same SHA-256 brings it back with its
  settings, the CPU time measured again.
- Idle model sessions are freed after 2 minutes.

### Class lists

Profiles of class names (decision 2026-11-08), **chosen on the model**, never on a node: the ids belong to how the model
was trained.

- Built in, read only, named by the models they fit: "COCO for DETR (91)" (`coco-91`, the COCO 2017 category ids,
  "N/A" at the gaps 0, 12, 26, 29, 30, 45, 66, 68, 69, 71, 83), "COCO for RF-DETR (90)" (`coco-90`, the same without id
  90: RF-DETR's 90 slots are the ids 0 to 89, as `class_id_to_name` of roboflow/rf-detr) and "COCO for YOLO (80)"
  (`coco-80`, the same names without gaps: couch, potted plant, dining table, tv).
- A built-in list is taken by itself only when neither metadata nor a labels file named the classes and the count is 91,
  90 or 80. A stored model of 90 classes with only the generated "Class <id>" names and no list gets the RF-DETR list at
  the start (never over names the user or the model gave).
- The user's own lists: server-wide like the models (plugin records, collection `classLists`), at most 50, names unique
  also against the built-in ones, Manage vision models, audited. An empty name becomes "Class <i>". A list's count of
  names is a model's class count: a list with another count cannot be chosen for a model, and a list in use cannot
  change its count ("Used by models with 2 classes. Keep 2 names.") or be deleted. A list no model uses fits any.
- At the start a model on a list takes the list's current names when the counts match.
- The page: the section **Class lists** below Models (built-in: View; own: Edit, Delete in red; New); its dialog holds
  Name and the names, one per line. A model's ⋯ menu **Class list** offers "Own names" and the fitting lists, and says
  below the select when none fits ("No class list has 90 names. Create one or use own names."). Detect objects shows
  the model's list read only ("Class list: COCO for DETR (91)"); the Classes pickers and every label follow the
  names in effect. Lists change no live event: the page reads them again after its own changes. Used by shows to every
  user (models are server-wide).

## Pipelines

A pipeline is a saved graph of nodes on one camera: name, mode, camera, video source, resolution, frame rate and the
nodes with their links and places on the canvas (JSON, camelCase, one row per pipeline, like the readers of Text
recognition).

- **Modes** as for Text recognition readers:
  - **Watching** (default): runs on the camera's MJPEG stream at 1, 2, 4, 8, 12 or 15 frames per second (default 8:
    tracking needs at least about 5), the same stream rules as a reader (`VapixRequestOptions.LiveStream`, reopened after a
    drop, the problems in the list).
  - **On action**: runs on one picture when a rule runs the action **Run pipeline**. Nodes that need a stream (Track,
    Line, Timer, and Count on crossings) are refused in this mode, on their node: "Track works only while Watching."
- **Resolution**: default 1280x720 or the camera's largest below it (the model sees its own input size; a larger picture
  only helps small objects). The save checks 160 to 7,680 pixels per side and video source 1 to 16, not the camera's
  own list.
- A saved pipeline restarts only when its graph, node settings, stream, owner or the models it uses change; a new name,
  node places or an eye never restart it.
- **Graph**: branches, no loops, no merges (decision 2026-11-08). The sources: exactly one Camera node, the start, and
  any number of Timer nodes. Every other node has exactly one data input and any number of links from its output.
  **Control inputs** (decision 2026-11-08) sit beside the data input: Count's **Reset** and Event's **Send now**
  (`NodeKindInfo.ControlInputs`, the link's `ToPort`); each takes at most one link, only from a Timer, and a Timer's
  pulse goes nowhere else. At most 32 nodes, at most 2 model nodes (Detect, Classify)
  per path from the camera. The server checks the graph at every save (`VisionGraph.Check`) and the editor checks it while
  the user edits, with the same rules and texts (`Graph/GraphTexts`).
- **The check**: one error per node, the first that applies in this order: its id (letters and digits, at most 32,
  unique; links name nodes by id, so nothing else can be judged without it), an unknown kind, the Camera (one, no input),
  its data input (none, a loop, more than one), the kind of its input, its control links (a known port, from a Timer, one
  each), a Line without a Track before it, Watching only, the
  model nodes on its path, its model (chosen, of the field's kind, not Missing), its classes (in its model or in the one
  of the Detect node before it; the class of Classify in the Classify node before it), every field's type and range,
  its name. Errors of the whole graph: no Camera, more than 32 nodes, a link to a node that does not exist, a pipeline
  format newer than this version. Input kinds pass on past a node with a field error, so the nodes after it still show
  their own errors. Fast enough for every edit (about 0.1 ms for 30 nodes).
- **Typed links**: every output has one kind; an input takes only the kinds its node lists. Kinds: **Picture**,
  **Objects** (boxes with class, score, attributes; **tracked** once a Track node passed them: each object keeps an id),
  **Crossings** (an object crossed a line, with its direction), **Number** (a live value), **Condition** (yes or no, from
  Compare), **Pulse** (a Timer's tick, only into a control input).
- Number fields with a known small range (scores, sizes, Confirm after, Keep lost objects, Minutes) show a slider with
  the number beside it (decision 2026-11-08).

### Nodes

| Node | Input | Output | What it does |
|---|---|---|---|
| **Camera** | none | Picture | The pipeline's camera and stream (summary "AXIS Q1798-LE · 1280x720 @ 8 fps"); Stream opens the Stream settings dialog of Text recognition |
| **Detect objects** | Picture | Objects | A detection model; Minimum score (default 50 %), Classes (all, or the chosen ones); at most 300 objects per picture, the highest scores first |
| **Classify** | Picture or Objects | Picture or Objects | A classification model. On a picture: the whole picture or the bounding box of an area drawn on it. On objects: each box cut out with a margin of 10 %, the class and score added as an attribute (`label`), at most 16 objects per picture; on tracked objects once per object and again every 2 s |
| **Filter** | Objects | Objects | Keeps the objects that pass all its checks: class, score, box height (min and max share of the picture's height), the class of a Classify node before it (case does not matter) |
| **Track** | Objects | Objects (tracked) | Gives each object an id that stays while it moves ("Tracking"). Confirm after (default 3 frames), Keep lost objects (default 1 s) |
| **Area** | Objects | Objects | A polygon of 3 to 16 points drawn on the video; passes the objects inside. Inside by (default) the box's bottom centre (feet, wheels), its centre or any overlap. With tracked objects also the time each one has been inside; a lost object keeps its time while the Track node keeps it |
| **Line** | Objects (tracked) | Crossings | A line of 2 to 8 points drawn on the video, with the direction In shown as an arrow; counts a crossing when a tracked object's bottom centre passes it. Counts In and Out, In only or Out only |
| **Count** | Objects or Crossings | Number | Objects: how many are there now. Crossings: In and Out since the last reset; its value is In, or Out for a line that counts Out only. Reset by a Timer into its **Reset**, by the rule action **Reset counter** (both also reset its Line), and on a server restart |
| **Compare** | Number | Condition | Yes while the number is greater than, at least, equal to, at most or less than **Value**, or between **Value** and **And** (both included) (decision 2026-11-08) |
| **Timer** | none | Pulse | A pulse every few minutes (**Minutes**, 1 to 59), every hour, every day **At**, or every week on **Day** **At**, in the server's local time; works only while Watching (decision 2026-11-08) |
| **Event** | Objects, Crossings, Number or Condition | none | Publishes a rule event ("Events"); its name is the event's name |

- A node has a name (unique in the pipeline whatever its case, at most 64 characters, no control characters, "Area 1"
  with the lowest free number like the areas of Text recognition); rules and dashboards name it.
- Areas and lines are stored relative to the picture (0 to 1), so a change of resolution keeps them in place.
- **Crossing direction** (decision 2026-11-08): In is from the left of the line's drawn direction to its right, as seen
  on the picture; for a line drawn from left to right, walking down the picture is In. The In arrow points to that side.
  One object never counts the same direction twice in a row (a crossing counts once); a step that ends on the line
  counts; a step over several segments of the line counts the first.
- Every node's settings are declared by the node itself (fields like `ActionField`: text, number, choice, model,
  classes, a shape on the video), so the editor draws the form from them and a node kind added later needs no editor
  change. The form follows the validation rule of the host (`ValidatingViewModel`, errors below the field).

### Timers

- A Timer's due times come from the frame's time in the server's local time zone (daylight saving included); no clock
  of its own. A pulse is taken at the first frame at or after its time, at most one per frame and Timer; times missed
  while the stream was down are not repeated. The first pulse is the first due time after the pipeline started.
- Few minutes: at the minutes of the hour that Minutes divides (15: :00, :15, :30, :45); Hour: at :00; Day: at At;
  Week: on Day at At. A local time that a daylight saving change skips is taken at the first frame after it.
- In one frame an Event's Report carries the values of that frame before a Count's reset by the same Timer, so "report,
  then reset" needs one Timer.

### Tracking

`Tracking/ObjectTracker`, own C#, ByteTrack-like, one per Track node:

- A constant-velocity Kalman filter per object on the box centre, width and height; the measured box is handed on, the
  filter only predicts where to match.
- Two rounds of IoU matching per class, greedy by the best IoU: detections with a score of at least 50 % against every
  track (IoU at least 0.2), then the lower ones against the confirmed tracks still unmatched (IoU at least 0.5). Only a
  detection of at least 50 % starts a track.
- An object gets its id (1, 2, ... per Track node) once it was seen Confirm after frames in a row, so noise uses up no
  id; a lost one keeps its id for Keep lost objects, by frame time. A trail of the last 12 bottom centres per object.
- At most 300 tracks: when they are full, the oldest lost track makes room.

### Running

- A frame runs through the graph from the camera, each node once per frame, the outputs handed by reference (no copy of
  objects per link). Branches after a model node share its result: two Lines after one Track run one model call.
  `Nodes/PipelineEvaluator` builds the nodes that can run; frames go through it one at a time, only the decoding runs in
  parallel.
- A node works only when something after it needs it: a branch that ends without an Event or Count node and is not shown
  on the page costs nothing. A preview runs every node (`EvaluatorOptions.AllNodes`).
- A node with an error, and every node after it, never runs; the other branches do.
- A failing model call ends the frame (`FrameResult.ModelFailure`). Event nodes run after all the others, so a failed
  frame sends no events; the branches that ran before keep that frame in their tracks and counts.
- Results are taken in the order of the frames: an older frame never replaces a newer one (as in Text recognition).
- A watching pipeline whose models fail 3 times in a row has a problem until a frame works, as a reader does.

## Events and the rule actions

- Per Event node, folder Vision Center / pipeline name, the camera is its fixed device (`FixedDeviceId`). Its first kind
  has the node's name; the others "<node>: object entered", "<node>: object left", "<node>: no objects found",
  "<node>: report". What it publishes follows its input:
  - **Objects**: stateful, on while at least **Minimum objects** (default 1) are there for **At least** (default 1 s),
    off once fewer stayed for **Off after** (default 2 s). Values: Count, Classes (the classes there, sorted, separated
    by a space). With tracked objects also a stateless **Object entered** and **Object left** per object, values Class,
    Object (the id), Time inside (s, one decimal: the Area's time, else from first to last seen). An object has left
    once it was gone longer than Keep lost objects of the Track node before it.
  - **Crossings**: stateless, one per crossing. Values: Direction (a choice, In / Out), Class, Object, Score (0 to 100).
  - **Number**: stateless **Count changed** when the value, In or Out changes, at most once a second with the latest
    values Count, In, Out (a count starts at 0, so a first 0 sends nothing).
  - **Condition**: stateful **Condition holds**, on once the Compare node before it said yes for **At least**, off once
    it said no for **Off after**; values Count (the number compared). It replaces the Event's Limit and Above limit
    (decision 2026-11-08).
  - **Send now** (a Timer into it): a stateless **Report** per pulse with the input's current values (Count, and In and
    Out, or Classes, as its input has them), e.g. the count of the day before a Timer resets it.
  - In On action mode an Event node on Objects fires **Objects found** (Count, Classes) or **No objects found** (Count)
    once per run, like Text read and No text read.
- Event **Pipeline problem** per pipeline and **Any pipeline problem** (server, users with all devices), as Reader
  problem and Any reader problem of Text recognition.
- Rule action **Run pipeline** (an On action or a watching pipeline): a watching one answers with its latest frame of at
  most 1 s ago or the frame in work, as Read text does, and publishes no extra events; without one it runs a picture of
  its own without events. A second run of an On action pipeline while one runs ends "Already running". The event log
  line lists the live lines of its Count and Event nodes ("Gate count: 14; Gate: On").
- Rule action **Reset counter**: a pipeline and one of its Count nodes; the counter's value is `<pipeline id>/<node id>`
  with the label "Pipeline: Counter", since a choice list cannot follow another field. "Choose a pipeline." and "Choose
  a counter." at the rule's save.
- Counts reach dashboards as event values like any other; Count is the split field (`SplitField`) of nothing: each Event
  node is its own event.

## Page

Three columns (decision 2026-11-08, "Canvas + side video"): pipelines (fixed 320 px) | the canvas | the live video above
the selected node. Page header: the description, "Runs on" with Use GPU and the load on the right, as Text recognition.

- **Left: the pipelines**: name, camera, mode, state chip ("Watching · 8 fps", orange when slower, red per kind of
  problem with the server's text as tooltip, the chips of Text recognition), one live line (the first Count node, else
  the first Event node; sent only on change, at most twice a second), **New** (the camera first), **Delete** (red,
  confirmation) and a search. The card **Models** below it for users with Manage vision models: Upload model, the models
  with their ⋯ menu (Rename, Class list, Delete in red), then Class lists. Empty page: "Upload a model, then create a
  pipeline."
- **Canvas | right column**: the shared splitter (`GridSplitter.paneSplitter`) between them. The right column is 420 px
  by default and at least that, the canvas at least 620 px (its toolbar fits); in a narrow window the canvas keeps its
  minimum first (`PageLayout.LimitsOf`). The position stays while the page lives.
- **Middle: the canvas**, our own Avalonia control (no node editor exists for Avalonia), close to n8n so users who know
  it feel at home:
  - Nodes as cards with the node's icon, its name and one live line (below). The graph runs **top to bottom**
    (decision 2026-11-08, replaces left to right: the canvas is taller than wide): the input at the top centre of a card,
    the output at its bottom centre, links as curves from an output down to the next input (1 px, selected 2 px, while
    dragged 1 px dashed). A dot grid on the theme's background. The hand cursor on the +, the eye, nodes and links; a
    crosshair on an output.
  - Add: **Add node** in the toolbar opens the node list (search, grouped: Models, Objects, Geometry, Logic, Time, Output); the **+**
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
- **Right, top: the live video** (the host's live video component, picture only, at least 226 px high), a splitter, then
  the node card below it (at least its heading and two fields). Until the splitter is dragged the video card is as tall
  as its 16:9 picture wants and follows the column's width; afterwards it keeps its height within the limits; the cards
  never overflow the column. The video has the overlay of the nodes whose **eye** is on (a toggle on every node card, tooltip "Show on video";
  the selected node always shows):
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
- **The live line on each node card** (n8n's item counts): Camera "AXIS Q1798-LE · 1280x720 @ 8 fps", Detect "5
  objects · 18 ms", Filter "3 objects", Classify "3 objects · 18 ms" (on a picture "blue 87 % · 18 ms"), Track "3
  tracked", Area "2 inside", Line "In 12 · Out 7" (one of them for a line of one direction), Count "14", Compare "14 > 10 · Yes", Timer "Next 00:00", Event "On",
  "Off" or "Fired 3 s ago" ("Fired now" below a second, then minutes and hours); the number of objects on each link while
  the page shows. Sent at most twice a second per pipeline.
- **Previews**: while the page shows a pipeline, the server runs its draft as a preview, as Text recognition does
  (`startPreview` on every change and every 10 s, a lease of 30 s, at most 8 per server and 2 per user). A saved
  unchanged watching pipeline shares its stream and shows its counts; any other draft has a stream of its own, counts
  against the watching limit and starts its counts at 0. A preview publishes no events and changes no counter of the
  saved pipeline.
- **Loading feedback**: "Reading the pipelines" and "Reading the pipeline" overlays as in Text recognition; an upload shows
  its progress and Cancel in the model's row; the model check a ring in the row.

## Load

- Its own inference workers: on the CPU `max(1, cores / 2)` model calls at once, each on one thread; all pipelines share
  one session of each model and size.
- **Graphics card**: with Use GPU on, each model is tested on the card at its first call (two calls on the card against
  one warmed call on the CPU); the model runs there unless the card is more than 1.5 times slower. Callers wait for the
  test, the page shows Starting meanwhile. The card's calls run in one lane. A test longer than 1 minute, a failing call
  or a call longer than 2 minutes ends the card's use until the next start, with one log line. "GPU available" means
  the provider was found and has not failed; "on GPU" that a model passed its test.
- A pipeline has one frame in work, up to 4 while the workers have room, as a reader ("Bounded load" of
  `docs/spec/ocr.md`); a frame without room is dropped, the newest wins.
- **Watching pipelines**: 2 per worker, at least 4 and at most 50 (a detection model costs more than an area of text);
  per site 4; previews with a stream of their own count with them. Beyond: the problem "At most 8 pipelines can watch at
  once. Set another pipeline to On action first."
- **Runtime stats** and the traffic light as for Text recognition: load bar, "All pipelines keep up", "Busy: 2 pipelines
  run slower than set", "Overloaded: lower the frames per second or the number of pipelines"; per node the time per frame
  in its live line.
- The two vision add-ons each keep their own budget; both on one server can fill its cores. The traffic lights show it
  ("Open points").

## Permissions

- **View vision pipelines**: the page, the pipelines, their live output. The video, Pause and object cut-outs need the
  host's **Live view** too.
- **Change vision pipelines**: New, Delete, Save, the canvas, drawing areas and lines, Use GPU (with all devices).
- **Manage vision models**: upload, change and delete models and class lists (Administrators by default, not Operators;
  all devices: models are server-wide).
- Audited: "Saved vision pipeline <name>", "Deleted vision pipeline <name>", "Uploaded vision model <name>", "Refused
  vision model <name>", "Changed vision model <name>", "Deleted vision model <name>", "Turned Use GPU on for Vision
  Center" (and off), "Saved class list <name>", "Deleted class list <name>".
- Running the actions is part of the event rules' own permissions.

## Parts and contract

- **Shared with Text recognition, never copied** (the reuse rule of `CLAUDE.md`): `plugins/Shared/Vision/` (linked
  source, internal, each add-on keeps its own load context) holds the frame source (`Frames/CameraFrames`,
  `MjpegParser`), the engine (`Engine/SessionFactory`, `BusyMeter`, `EngineRules`: the card test, Use GPU), the picture
  limits (`Pictures/PictureLimits`), and the previews with their leases and the runtime stats.
  `plugins/Shared/Vision.Client/` holds the client's picture overlay (`PictureOverlay`, `PicturePlace`,
  `ShapeGeometry`: letterboxing, Pause, drawing and moving shapes; rectangles of Text recognition are one case of the
  polygon editor) and the Stream settings dialog. Text recognition's behaviour and tests stay as they are.
- In the plugin: `Models/` (store, check, decoders per output format, class lists), `Inference/` (ONNX Runtime, the
  pictures), `Graph/` (`VisionGraph.Check`, the texts, the settings), `Nodes/` (one class per node kind behind
  `IVisionNode`: its kind, its input, its work on a frame; the contract an SDK may open later, decision 2026-11-08),
  `Tracking/`, `Geometry/` (inside, overlap, crossing, IoU), `Pipelines/` (the store, the runtime, previews, stats),
  `Events/`, `Api/` (the page's payloads). `Model/` holds the contracts between them (`VisionParts`: tests hand fakes).
- The pipeline JSON has a `version`; a node kind the plugin does not know (a newer pipeline after a restore on an older
  OADM) keeps the pipeline stored but not run, with its node's error "This node needs a newer Vision Center."

## Packaging

- Add-on ZIP per server platform (`docs/spec/add-ons.md` "Release files") with ONNX Runtime of the same version as Text
  recognition, SkiaSharp and no model. The client part has no native libraries.
- Both add-ons load their own copy of ONNX Runtime: three copies of `onnxruntime.dll` in one process worked on the CPU
  (`TwoRuntimesTests`). Windows loads `DirectML.dll` by name and reuses the copy already loaded (both ship 1.15.4).

## Tests

- Decoders: small ONNX models generated by the tests for each output format (YOLO, YOLO with suppression, DETR,
  RF-DETR with two and with three outputs, DETR with labels, classification), a model with a refused domain and one with
  external data.
- **Second real model: RF-DETR** (`https://huggingface.co/PierreMarieCurie/rf-detr-onnx/resolve/main/rf-detr-medium.onnx`,
  downloaded by the user, uploaded as it is): recognised as RF-DETR without a choice, the same pipeline as DETR works.
- **First real model: DETR** (`facebookresearch/detr`, DETR-R50 on COCO). The user docs give the export: the PyTorch
  Hub model, `torch.onnx.export` with the outputs named `pred_logits` and `pred_boxes`, a fixed input of 800 x 800 (or a
  free one), opset 17; the class names come from "COCO for DETR (91)". The upload, the model check and a pipeline Detect
  objects > Filter (person) > Track > Line > Count > Event must work with that file. It is slow on the CPU (about a
  second per picture at 800 x 800, to be measured): the page's load and the node's time per frame say so, a graphics card
  or a lower frame rate is the answer.
- The graph check: one test per rule and text; the same cases through the editor's view model.
- Tracker and geometry from box sequences as JSON in `Fixtures/Tracks/` (two people passing each other, a short
  occlusion, a back and forth, a crowd of 50): ids stay through a short occlusion, a crossing counts once, a back and
  forth counts In and Out, the bottom centre decides inside.
- Counts, events (on, off, At least, Off after, Condition holds, Report, Count changed at most once a second, Objects
  found), Reset counter, every Compare condition with the bounds of Between, Timers across midnight, a week and a
  daylight saving change (frame times only), the control link rules, a failing model and an errored branch through the evaluator with a
  fake inference.
- Limits, previews and leases, device groups, the permissions, the model upload with its refusals, class lists and the
  backup of the model list.
- The canvas: adding, linking, refusing a link, undo, the errors below the node; headless screenshots for the look.
- A real detection model only in tests that download a permissive one at the test build into a cache with its pinned
  SHA-256 (never shipped, never in git), like the models of Text recognition (not built yet).

## Measured

- One frame through 32 nodes with 300 objects (Detect, Track, 7 Area > Line > Count > Event branches, a fake model):
  about 7 ms on the development PC (debug build, `EvaluatorPerfTests`), without the model call.
- The graph check of 30 nodes: about 0.1 ms.

## Later

- Anomaly detection as a model kind and a node (a score per picture or area, an event above a limit).
- Models we tested (DETR-style, Apache-2.0) as downloads on oadm.app.
- Nodes from other plugins through the SDK (`IVisionNode` opened as an SDK contract).
- Segmentation and pose, merges (AND / OR of two branches), counts kept as history.
- Text recognition as a node.

## Open points

- A run of both vision add-ons on a graphics card in one server: DirectML on Windows (the shared `DirectML.dll`) and
  CUDA on Linux are not tested.
- The accuracy with DETR and RF-DETR on the development cameras, the default frame rate and resolution from those
  measurements, the real-model test with a downloaded model.
- macOS and Linux runs of the add-on.
- One worker budget for both vision add-ons instead of one each: Text recognition's CPU pool and its card memory budget
  are not shared.
- A detection below 50 % never starts a track, it only keeps one: with a Minimum score below 50 % such objects are
  never tracked.
- The page has no choice of format, fit, scaling or input size after the upload; the server takes them (`updateModel`).
