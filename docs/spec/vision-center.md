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
    end to end: YOLOv10, YOLO26).
  - Detection **YOLOX** (Megvii, Apache-2.0, as its releases export it; 2026-11-08): `[1, N, 5 + C]` with one row per
    grid cell of the strides 8, 16 and 32 of the input (N = the cells, YOLOv5 has three anchors per cell), row by row:
    x and y offsets in the cell, log width and height in strides, objectness and class scores after their sigmoid. The
    plugin decodes the cells, scores objectness x class and runs the suppression like YOLO. Letterbox, the pixel values 0
    to 255 in the order blue, green, red (trained on OpenCV's pictures). An export with its decoding inside (the centres
    of the blank call beyond half the input) is read as YOLO.
  - Detection **DETR**: the original DETR of Facebook Research (`facebookresearch/detr`, Apache-2.0; the first model the
    maintainer tests, decision 2026-11-08) exported to ONNX: `pred_logits` `[1, Q, C + 1]` (the last class is "no
    object") and `pred_boxes` `[1, Q, 4]` (centre x, centre y, width, height, 0 to 1 of the input). Softmax per query,
    the best class other than "no object" with its probability as the score; no suppression. Input with ImageNet mean and
    deviation (preselected for this format), the picture scaled to the input with its shape kept, padded. The COCO models
    have 91 class ids with gaps ("N/A"): a class named "N/A" is never shown or chosen, and its detections are dropped.
    Hugging Face exports of DETR (Transformers.js, e.g. `Xenova/detr-resnet-50`) name the logits `logits` and have a
    second input `pixel_mask` `[1, h, w]` (integers): it is filled with ones of its declared size (or the input's when
    free), "all picture", as the original DETR without a mask (2026-11-08).
  - Detection **RT-DETR** (2026-11-08): Hugging Face exports of DETR models without a "no object" class (RT-DETR, D-FINE,
    the Transformers.js YOLO26; onnx-community): `logits` `[1, Q, C]` with a sigmoid per class and `pred_boxes` `[1, Q,
    4]` (centre x, centre y, width, height, 0 to 1), found by name; the best class per query; no suppression. Stretch, 0
    to 1, a free input taking 640 (their processors resize to 640 x 640 without normalising). The same names as a
    Hugging Face DETR: the blank call of the model check tells them apart. A model with a "no object" class makes it the
    best class of nearly every query on a blank picture (DETR R50: 100 of 100, also quantized), one without never does
    (RT-DETR v2, D-FINE S: 0 of 300); half the queries or more with the last class best is DETR, else RT-DETR. The
    original names `pred_logits` and `pred_boxes` stay DETR whatever the blank picture gives.
  - Detection **RF-DETR**: RF-DETR of Roboflow as exported by `PierreMarieCurie/rf-detr-onnx` (code MIT, models
    Apache-2.0 except xlarge and 2xlarge, which are under the Platform Model License 1.0; the second model the maintainer
    tests, `rf-detr-medium.onnx`, decision 2026-11-08): output 0 boxes `[1, Q, 4]` (centre x, centre y, width, height,
    0 to 1), output 1 class logits `[1, Q, C]` with a sigmoid per class and no "no object" class, the best class per
    query, the top 300 queries; a third output (the masks of the segmentation models) is left out until segmentation
    comes ("Later"), so those models detect boxes. Input of a fixed size read from the model, ImageNet mean and
    deviation, the picture **stretched** to the input (not letterboxed: the export was trained that way). The order
    decides, also with the `pred_` names (rf-detr-onnx names its outputs `pred_boxes`, `pred_logits`): logits first is
    DETR, boxes first is RF-DETR. `rf-detr-medium.onnx` gives 91 class columns, the COCO category ids (0 unused), and
    takes "COCO, 91 ids" (measured 2026-11-08).
  - Detection **DETR with labels**: boxes, scores and labels outputs (RT-DETR, D-FINE and other end-to-end exports); no
    suppression. An optional input `orig_target_sizes` `[1, 2]` is filled with the input size; boxes in input pixels or
    0 to 1; Stretch by default.
  - Classification: `[1, C]`, logits or probabilities (softmax when the values are not already probabilities), fitted by
    its model's Fit (Stretch by default). A free input takes 224 x 224, the size ImageNet models are trained at.
    Scaling ImageNet mean and deviation (torchvision, timm, Hugging Face and the ONNX Model Zoo train that way), except
    Ultralytics' exports (metadata `author`, `task` or `names`), which take 0 to 1 (2026-11-08).
  - **Fit**: Letterbox (YOLO, YOLOX), Stretch (RF-DETR, RT-DETR, DETR with labels, classification) or Keep shape and pad (DETR),
    preselected by the format, a choice per model, since the wrong fit loses accuracy without an error. A format whose
    outputs do not fit the model is refused: "The model's outputs do not fit its format. Choose another format."
- **Input**: one picture tensor `[1, 3, H, W]` in floating point, 32 or 16 bits (an export with `--half`, its outputs
  in 16 bits too, converted; 2026-11-08). A fixed size (8 to 4,096 per side) is read from the
  model, a free one takes the user's size (default 640 x 640, a multiple of 32 from 32 to 2,048). RGB. Scaling: 0 to 1
  (default), ImageNet mean and deviation, or 0 to 255 BGR (YOLOX), a choice per model.
- **Class names**: from the model's metadata (`names`, as Ultralytics exports write it) when its count fits the classes
  and the names are more than the ids (`{0: '0', 1: '1', ...}` of onnx-community's YOLOv10 names nothing; its count
  still gives the classes, so 80 take "COCO, 80 classes"),
  else a labels file uploaded with it (one name per line, as many as classes; another count is refused; the page loads one in Import model's Labels step instead), else a built-in
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
  Rename, **Settings** (format, fit, scaling, and the input size of a model without a fixed one; refusals below their
  fields; each of the three with an info icon whose tooltip draws every choice, the chosen one in the accent:
  `ModelSettingDiagram`, the picture in a 16:9 camera picture letterboxed, stretched or padded into the square input, the
  numbers one orange pixel gives per scaling, the formats with the models they read), Class list, Delete in red
  (`updateModel`).
- **Import model** (decisions 2026-11-09: a wizard that guides users who are no experts; first only the file, then
  everything found, the labels last; the card's button, never "Upload model"; `ModelImportViewModel`, the shared
  `ui:WizardSteps`): one form per step, Back / Next, Import on the last.
  1. **Model**: a drawing of what happens (`ModelImportDrawing`: a camera picture, your model, the objects found with a
     box), one line ("Choose your ONNX model. OADM checks it, finds out what it is and sets it up.") and the file
     button. Next sends the file (progress and Cancel in the step), then the model check; the model is stored from here
     on. A refusal shows below the file. The same file again after Back is not sent again; another file replaces the
     stored one.
  2. **Settings**: centered, a green check, "Model detected" and what the check found ("Detection · 576x576 · 91
     classes"; decided 2026-11-10); then the Name (a tag icon; the server's name for the file; unique), the **Model
     family** the check found (`ModelFamilies.Of`: by the format, a classifier by its scaling; only the families of the
     model's kind: YOLOv8 / YOLO11 / YOLO12, YOLOv10 / YOLO26 end to end, YOLOX, RF-DETR, DETR, RT-DETR / D-FINE, DETR with
     labels, ImageNet classifier, YOLO classifier; another family sets its format, fit and scaling) and the Settings form
     (`ModelSettingsForm`, shared with the dialog). Family, Format, Fit and Scaling carry a green check while they hold
     what the check found and a warning once the user picks another, with "The check found Stretch." below it
     (`ui:LeadingIcon` classes `found` / `changed`); their info icons hold the same texts and drawings as the Settings
     dialog, no drawing or line below a select.
  3. **Labels**: **Load labels file** (a `.txt`, one name per line: its lines become the own names; another count shows
     below them) and the Class list form (`ModelClassListForm`, shared with the dialog).
  Import saves name, settings and labels in one `updateModel`; a refused setting (a format whose outputs do not fit)
  returns to Settings below its field. Cancel or closing the window ends a running upload and deletes the model the
  wizard stored, except a Missing model the file brought back.
- **Limits**: at most 20 models, each at most 500 MB, 4 GB together, at most 1,000 classes. Uploads in progress count;
  a Missing model counts once its file is stored again.
- **Delete** is refused while a pipeline uses the model ("Used by 2 pipelines: Gate 1, Lane 2."), checked against every
  pipeline; a user limited to a device group sees only their own pipelines in Used by.
- **Backups** keep the model list (name, kind, settings, SHA-256) and the user's class lists, never the files, like
  add-on files. At the start a model whose file is absent or of another size is **Missing** (no hash at every start):
  its pipelines show "Import the model Gate people again." Importing a file with the same SHA-256 brings it back with its
  settings, the CPU time measured again.
- Idle model sessions are freed after 2 minutes.

### Class lists

Profiles of class names (decision 2026-11-08), **chosen on the model**, never on a node: the ids belong to how the model
was trained.

- Built in, read only, named by what they hold (decision 2026-11-08): "COCO, 91 ids" (`coco-91`, for DETR and RF-DETR; the COCO 2017
  category ids, "N/A" at the gaps 0, 12, 26, 29, 30, 45, 66, 68, 69, 71, 83) and "COCO, 80 classes" (`coco-80`, for YOLO, RT-DETR and D-FINE; the
  same names without gaps: couch, potted plant, dining table, tv). No list of 90 (decision 2026-11-08: it came only from
  RF-DETR read as DETR).
- A built-in list is taken by itself only when neither metadata nor a labels file named the classes and the count is 91
  or 80.
- The user's own lists: server-wide like the models (plugin records, collection `classLists`), at most 50, names unique
  also against the built-in ones, Manage vision models, audited. An empty name becomes "Class <i>". A list's count of
  names is a model's class count: a list with another count cannot be chosen for a model, and a list in use cannot
  change its count ("Used by models with 2 classes. Keep 2 names.") or be deleted. A list no model uses fits any.
- At the start a model on a list takes the list's current names when the counts match. A model on a list that is not
  there (a user's list missing after a restore, the `coco-90` of an earlier build) keeps the names it holds as its own,
  one log line, never a failed start. An RF-DETR model stored by such a build keeps the format DETR: delete it and
  upload it again.
- The page: the section **Class lists** below Models (built-in: View; own: Edit, Delete in red; New); its dialog holds
  Name and the names, one per line. A model's ⋯ menu **Class list** offers "Own names" and the fitting lists, and says
  below the select when none fits ("No class list has 90 names. Create one or use own names."). Detect objects does not
  repeat the model's list (decided 2026-11-10); the Classes pickers and every label follow the names in effect. Lists change no live event: the page reads them again after its own changes. Used by shows to every
  user (models are server-wide).

## Pipelines

A pipeline is a saved graph of nodes on one camera: name, mode, camera, video source, resolution, frame rate and the
nodes with their links and places on the canvas (JSON, camelCase, one row per pipeline, like the readers of Text
recognition).

- **Modes** as for Text recognition readers:
  - **Watching** (default): runs on the camera's MJPEG stream at 1, 2, 4, 8, 12, 15, 20 or 25 frames per second (default 8:
    tracking needs at least about 5), the same stream rules as a reader (`VapixRequestOptions.LiveStream`, reopened after a
    drop, the problems in the list).
  - **On action**: runs on one picture when a rule runs the action **Run pipeline**. Nodes that need a stream (Track,
    Line, Dwell, Stationary, Direction, Timer, and Count on crossings) are refused in this mode, on their node: "Track works only while Watching."
- **Resolution**: default 1280x720 or the camera's largest below it (the model sees its own input size; a larger picture
  only helps small objects). The save checks 160 to 7,680 pixels per side and video source 1 to 16, not the camera's
  own list.
- A saved pipeline restarts only when its graph, node settings, stream, owner or the models it uses change; a new name,
  node places or an eye never restart it.
- **Graph**: branches, no loops, no merges (decision 2026-11-08). The sources: exactly one Camera node, the start, and
  any number of Timer, Time window and Device event nodes. Every other node has exactly one data input and any number of
  links from its output. **Control inputs** (decision 2026-11-08) sit beside the data input (`NodeKindInfo.ControlInputs`,
  the link's `ToPort`), each of one kind (`ControlInput.Accepts`) and with at most one link: Reset of Count and
  Statistics and Send now of Event take a pulse; Only while of Count and Event and Second of And / Or take a condition;
  Second is required (`ControlInput.Required`), the others optional. A pulse goes only into a control input or a
  Throttle. Loops count data and control links. At most 32 nodes, at most 2 model nodes (Detect, Classify)
  per path from the camera. The server checks the graph at every save (`VisionGraph.Check`) and the editor checks it while
  the user edits, with the same rules and texts (`Graph/GraphTexts`).
- **The check**: one error per node, the first that applies in this order: its id (letters and digits, at most 32,
  unique; links name nodes by id, so nothing else can be judged without it), an unknown kind, the Camera (one, no input)
  or another source (no input, Watching only for a Timer), its data input (none, a loop, more than one), the kind of its
  input (a pulse: "A Timer links only to Reset, Send now or Throttle."), a Line without a Track before it, a Dwell
  without an Area on its path, Watching only, the model nodes on its path, its control links (a declared port, of the
  port's kind, one each, a required one linked), its model (chosen, of the field's kind, not Missing), its classes (in its model or in the one
  of the Detect node before it; the class of Classify in the Classify node before it), every field's type and range,
  its name. Errors of the whole graph: no Camera, more than 32 nodes, a link to a node that does not exist, a pipeline
  format newer than this version. Input kinds pass on past a node with a field error, so the nodes after it still show
  their own errors. Fast enough for every edit (about 0.1 ms for 30 nodes).
- **Typed links**: every output has one kind; an input takes only the kinds its node lists. Kinds: **Picture**,
  **Objects** (boxes with class, score, attributes; **tracked** once a Track node passed them: each object keeps an id),
  **Crossings** (an object crossed a line, with its direction), **Number** (a live value), **Condition** (yes or no),
  **Pulse** (a Timer's tick, into a control input or a Throttle).
- Number fields with a known small range (scores, sizes, Confirm after, Keep lost objects, Minutes, Movement allowed,
  Tolerance, Minimum movement) show a slider with the number beside it (decision 2026-11-08). A field may show only for
  a value of another (`VisibleWhenKey`: And of Compare only for Between); a hidden field is not checked.

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
| **Dwell** | Objects (tracked) | Objects (tracked) | The objects inside the Area before it longer than **Longer than** (default 30 s); works only while Watching |
| **Stationary** | Objects (tracked) | Objects (tracked) | The objects whose bottom centre stayed within **Movement allowed** (default 3 % of the picture) of where it settled for **Still for** (default 60 s); a larger move settles it again where it is; works only while Watching |
| **Direction** | Objects (tracked) | Objects (tracked) | The objects whose bottom centre moved over the last second within **Tolerance** (default 45°) of the arrow drawn on the video (2 points) and at least **Minimum movement** (default 5 % of the picture's height); angles in picture pixels; works only while Watching |
| **Count** | Objects or Crossings | Number | Objects: how many are there now. Crossings: In and Out since the last reset; its value is In, or Out for a line that counts Out only. Reset by a pulse into its **Reset** (after this frame's value, so a Report of the same pulse still sees it), by the rule action **Reset counter** (both also reset its Line), and on a server restart. With **Only while** linked it counts only while that says yes and keeps its value meanwhile |
| **Statistics** | Number | Number | The highest, lowest, average or sum of the number over the frames since a pulse into its **Reset** or the start |
| **Compare** | Number | Condition | Yes while the number is greater than, at least, equal to, at most or less than **Value**, or between **Value** and **And** (both included) |
| **And / Or** | Condition, Second (Condition) | Condition | Both or either yes |
| **Hold** | Condition | Condition | Yes once its input said yes for **Yes after** (default 5 s), no once it said no for **No after** (default 5 s) |
| **Throttle** | Crossings or Pulse | the same | Passes at most one per **At most one per** (default 60 s); the others are dropped |
| **Device event** | none | Condition | Yes while the chosen event of the event rules (`event`: type and device) is on (`IEventStates`) |
| **Timer** | none | Pulse | A pulse every few minutes (**Minutes**, 1 to 59), every hour, every day **At**, or every week on **Day** **At**, in the server's local time; works only while Watching |
| **Time window** | none | Condition | Yes from **From** until **To** on the chosen **Days** (none: every day), the server's local time at the frame; a window over midnight belongs to the day it starts, From equal to To is the whole day |
| **Event** | Objects, Crossings, Number or Condition; Send now, Only while | none | Publishes a rule event ("Events"); its name is the event's name |

- Compare, And / Or, Hold, Throttle, Statistics, Timer, Time window, Device event, Dwell, Stationary and Direction:
  decision 2026-11-08 with the user. Compare replaced the Event's Limit and Above limit; Timer replaced Count's Reset
  daily at. The node list's groups (`NodeKindInfo.Group`): Models (Detect objects, Classify), Objects (Filter, Track,
  Dwell, Stationary, Direction), Geometry (Area, Line), Logic (Compare, And / Or, Hold, Throttle, Device event), Time
  (Timer, Time window), Output (Count, Statistics, Event).

- A node has a name (unique in the pipeline whatever its case, at most 64 characters, no control characters ("Use letters, digits and punctuation only.", the rules' phrase), "Area 1"
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
  track (IoU at least 0.2), then the lower ones against the tracks seen last frame and still unmatched (IoU at least 0.5).
  Any unmatched detection starts a track (decision 2026-11-09: a model whose scores stay at 20 to 30 % tracked nothing
  when only 50 % started one; the Detect node's minimum score and a Filter already chose the objects, Confirm after keeps
  noise from an id).
- An object gets its id (1, 2, ... per Track node) once it was seen Confirm after frames in a row, so noise uses up no
  id; a lost one keeps its id for Keep lost objects, by frame time. A trail of the last 12 bottom centres per object.
- At most 300 tracks: when they are full, the oldest lost track makes room.

### Running

- A frame runs through the graph from the camera, each node once per frame, the outputs handed by reference (no copy of
  objects per link). Branches after a model node share its result: two Lines after one Track run one model call.
  `Nodes/PipelineEvaluator` builds the nodes that can run; frames go through it one at a time. Beside it run, for up to
  `MaxFramesInWork` (4) frames: the decoding and the model calls that need nothing but the picture (a Detect node right
  after the Camera, `IPipelineEvaluator.Prefetch`, decision 2026-11-09: a pipeline whose model takes longer than a frame
  kept the free workers idle; yolo11n on one CPU thread 9 fps, now 29). Everything with state (Track, Line, Count, the
  rest) runs in the frames' order; a frame is freed only after its early calls ended.
- A frame is decoded only as large as the pipeline's models need (`PictureNeed`, decision 2026-11-09): a JPEG decodes at
  a half, a quarter or an eighth of its size in a fraction of the time, the smallest of them whose picture still fills
  every Detect input at least as finely as the whole picture would (720p into a 640 input: a half). A pipeline with a
  Classify node, whose crops need every pixel, decodes the whole picture. Boxes stay relative to the picture.
- A model's input is drawn with linear sampling from the smallest of the picture's halves that is still at least as
  large as the input needs (`Inference/PictureLevels`: each half made once per frame with a 2 x 2 box filter and shared by
  the frame's calls; what mipmaps are, without Skia building them again on every call), so nothing is scaled down by more
  than half in one step, as the models' own training resizes do.
- A preview's state carries the trails only on the Track node and every box and point rounded to 1/100,000 of the
  picture without its computed edges (`RelativeJson`); the page reads it off the UI thread and applies only the newest.
- A node works only when something after it needs it: a branch that ends without an Event or Count node and is not shown
  on the page costs nothing. A preview runs every node (`EvaluatorOptions.AllNodes`).
- A node with an error, and every node after it, never runs; the other branches do.
- A failing model call ends the frame (`FrameResult.ModelFailure`). Event nodes run after all the others, so a failed
  frame sends no events; the branches that ran before keep that frame in their tracks and counts.
- Results are taken in the order of the frames: an older frame never replaces a newer one (as in Text recognition).
- A watching pipeline whose models fail 3 times in a row has a problem until a frame works, as a reader does.

### Copy to cameras

Decision 2026-11-08: a saved pipeline is copied to other cameras in one call (`copyPipeline`), one independent pipeline per
camera, at most 100 cameras at once ("Copy to at most 100 cameras at once.").

- A copy has a new id and the original's nodes, links, mode and frame rate; areas, lines and arrows keep their place in
  the picture (they are relative). Its name is "<original name> (<camera host name or address>)", made unique with " 2",
  " 3"; its owner is the caller's device scope.
- Video source and resolution stay when the camera offers them (its options read once per camera, a few at a time),
  else source 1 and 1280x720 or the camera's largest below it.
- Refused per camera, the others are made: the pipeline's own camera ("This is the pipeline's own camera."), a camera
  outside the caller's devices (as an unknown one), a copy whose save checks fail (the first error), the copies beyond the
  pipeline limit ("OADM keeps at most 50 pipelines."). The watching limits work as for any pipeline: a copy beyond them is
  stored and shows the limit as its problem.
- All copies are stored in one batch, then one sync, one change of the event types and one list event. Needs Change vision
  pipelines; audited ("Copied vision pipeline Gate 1 people to 5 cameras").

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
  - **Send now** (a pulse into it): a stateless **Report** per pulse with the input's current values (Objects: Count and
    Classes; Crossings: In and Out; Number: Count, with In and Out on crossings; Condition: Count), e.g. the count of the
    day before a Timer resets it.
  - **Only while** linked: while it says no the stateful kinds count as "not there" (and go off after Off after) and
    nothing stateless is sent; Object entered and left still follow the objects, so none is sent twice.
  - In On action mode an Event node on Objects fires **Objects found** (Count, Classes) or **No objects found** (Count)
    once per run, like Text read and No text read.
- Event **Pipeline problem** per pipeline and **Any pipeline problem** (server, users with all devices), as Reader
  problem and Any reader problem of Text recognition.
- Rule action **Run pipeline** (an On action or a watching pipeline): a watching one answers with its latest frame of at
  most 1 s ago or the frame in work, as Read text does, and publishes no extra events; without one it runs a picture of
  its own without events. A second run of an On action pipeline while one runs ends "Already running"; so does a second picture of its own for a
  watching pipeline (at most one at a time, decision 2026-11-08 after the review). The event log
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
  confirmation) and a search. The card **Models** below it for users with Manage vision models: Import model, the models
  with their ⋯ menu (Rename, Settings, Class list, Delete in red); below it the card **Class lists** (decision
  2026-11-08: its own card). Each pipeline row has a ⋯ menu: Copy to cameras, then Delete in red. Empty page: "Import a model, then create a
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
  - A control input without a link (Reset, Send now, Only while, Second) adds what it takes (decision 2026-11-09: a Timer
    was hard to find): a click on its dot, or on the **+** left of it on the selected node (a dashed stub to the dot),
    opens the node list with only the kinds whose output it takes (a Timer and a Throttle for a pulse; the yes-or-no
    nodes for Only while and Second). The new node goes up and left of the node, its output linked into that input. The
    node list is 340 px wide (its descriptions fit).
  - Move nodes by dragging, pan by dragging the empty canvas, zoom with the wheel (25 % to 200 %), **Fit view** (centred
    across, at most 24 px from the top: a new pipeline's camera sits at the top, the graph grows downwards) and
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
- **Right, below: the selected node**: three cards, its settings form, for Detect objects and Classify **What the model
  sees** (decided 2026-11-10), and its **live output**, the feedback the user asked for. The cards use the column's width
  (`ui:FlowColumns`, decided 2026-11-10): side by side once each gets 380 px (`PageLayout.NodeCardMinWidth`, at most 3,
  each into the shortest column), one below the other in a narrow column; the fields in the settings card flow into
  columns of at least 260 px instead of growing. Nothing selected: the pipeline's settings in one card The fields keep the spacing of every form; each field of a node has an info icon with one short sentence (`NodeFieldInfo.Info`, decided 2026-11-10; a test keeps every field covered), Mode too. The Camera has no live output card (its stream size is its Stream field).
  - What the model sees: the model's input of the preview's latest (or held) frame as the server draws it for the model
    call (`getModelView`, `IInference.ModelView`, the same `Preprocess.Draw` as the tensor, the frame decoded with the
    pipeline's `PictureNeed`), before the values are scaled; a caption "576x576 · Letterbox" and Refresh. Classify on
    objects: one tile per object (the box with its margin), at most 8. Read when the node is selected, with the next frame
    after its model or area changed, and on Refresh; never live. Live view only, as Pause; in memory only, never stored.
  - Detect, Filter, Track, Area: objects in this frame, a table of them (Object, Class, Score, Size: the height as a share of the picture's, as a Filter counts it; Time inside, an Area only), a Filter also the sizes of what came in ("Sizes 6 to 12 %"), Detect objects how many of its classes scored below its minimum, down to 10 % ("3 below 50 %", `NodeOutput.Below`: only the preview asks the model down to 10 %, a saved pipeline never), the node's
    time per frame. Line: the last 20 crossings (Time, Direction, Class, Object). Count: the value. Event: whether it is
    on, the last 20 times it fired with its values (a preview's own list: a preview never publishes, "Previews").
  - Nothing selected: the pipeline's own settings (name, mode).
- **The live line on each node card** (n8n's item counts): Camera "AXIS Q1798-LE · 1280x720 @ 8 fps", Detect "5
  objects · 18 ms", Filter "3 objects", Classify "3 objects · 18 ms" (on a picture "blue 87 % · 18 ms"), Track "3
  tracked", Area "2 inside", Line "In 12 · Out 7" (one of them for a line of one direction), Count "14", Statistics "Highest 14", Compare "14 > 10 · Yes" ("2 ≤ 4 ≤ 6 · Yes" for Between), And / Or, Device event
  "Yes" or "No", Hold "Yes" or "Yes in 3 s", Throttle "1 passed · 4 held back", Dwell, Stationary and Direction "2
  objects", Timer "Next 00:00" ("Next Tue 06:30" weekly), Time window "Yes until 18:00" or "No until 08:00" ("No until
  Fri 22:00" more than a day ahead), Event "On",
  "Off" or "Fired 3 s ago" ("Fired now" below a second, then minutes and hours); the number of objects on each link while
  the page shows. Sent at most twice a second per pipeline.
- **Previews**: while the page shows a pipeline, the server runs its draft as a preview, as Text recognition does
  (`startPreview` on every change and every 10 s, a lease of 30 s, at most 8 per server and 2 per user). A saved
  unchanged watching pipeline shares its stream and shows its counts; any other draft has a stream of its own, counts
  against the watching limit and starts its counts at 0. A preview publishes no events and changes no counter of the
  saved pipeline.
- **Loading feedback**: "Reading the pipelines" and "Reading the pipeline" overlays as in Text recognition; Import model
  shows the upload's progress, then "Checking the model", below the Model step's fields, Cancel usable.

## Load

- Its own inference workers: on the CPU `max(1, cores / 2)` model calls at once, each on one thread; all pipelines share
  one session of each model and size.
- **Graphics card**: with Use GPU on, each model is tested on the card at its first call (two calls on the card against
  one warmed call on the CPU); the model runs there unless the card is more than 1.5 times slower. Callers wait for the
  test, the page shows Starting meanwhile. The card's calls run in one lane. A test longer than 1 minute, a failing call
  or a call longer than 2 minutes ends the card's use until the next start, with one log line; the calls waiting for the
  lane then go to the CPU (a hung test may hold the lane for good). "GPU available" means
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

## Hardening (review 2026-11-08)

- **Sizes before checks.** A draft or stored pipeline with more than 32 nodes, more than 128 links
  (`VisionLimits.MaxLinks`, "A pipeline has at most 128 links.") or one node's settings over 16 KB is refused before any
  graph check (`PipelineRules.Oversized`); `VisionGraph.Check` stops at once after too many nodes. No unbounded draft
  is ever checked under the runtime's lock.
- **Device event nodes.** A per-device event needs its device ("Choose a device." on the node); `IEventStates.IsOn`
  without a device answers for server events only. Previews run the same Device event checks as a save. The devices
  of Device event nodes must be in the caller's devices and in the owner group's devices (an administrator cannot put
  another device into a group's pipeline); copying checks the same. A late event of an old subscription is ignored
  (a generation number per subscription).
- **The model scan reads the file as protobuf does.** A tag above 32 bits, a tenth varint byte above 1 and a nested
  message running past its parent are refused; function default attributes (`attribute_proto`) are walked like a
  node's attributes. Any exception of the model check is a refusal ("it does not load (...)"); a fixed mask side
  outside 1 to 4096 is refused before memory is taken.
- **Uploads.** A failed write or flush ends the upload: "The file could not be written on the server. Import the model
  again." An upload being checked keeps its bytes and place counted until stored or refused; the count and size rule
  is one rule at the start and again in the store's write lock: models with a file, plus uploads in progress, plus
  uploads being checked. Missing models count at neither step (they are listed and can be deleted). Stored record
  keys other than 32 lower-case hex digits are left out at load (one log line).
- **Audit.** A save refused for its fields or graph is audited "Could not save vision pipeline X"; the audit's name
  cache keeps at most 256 entries, each read once; beyond them the oldest goes, never the names of calls still running.
- **A stream that ends by itself** (a frame source that failed for good, logged) ends its loop; the next check of the
  pipelines (`RecheckInterval`, 1 minute) starts it again. A run of Run pipeline its caller cancels is no problem of the
  pipeline (only a run that timed out on its own is "timed out").
- A paused preview checks the held frame's size from the picture's header (`IInference.Fits`, `PictureLimits`), never by
  decoding it.
- **Paused previews.** A failing re-run of a held frame is logged and the worker stops; the next change runs again;
  stopping never rethrows (`LatestWinsWorker`, Text recognition unchanged).

## Permissions

- **View vision pipelines**: the page, the pipelines, their live output. The video, Pause, What the model sees and object
  cut-outs need the host's **Live view** too.
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
  Covered by the real-model tests below (2026-11-08).
- **First real model: DETR** (`facebookresearch/detr`, DETR-R50 on COCO). The user docs give the export: the PyTorch
  Hub model, `torch.onnx.export` with the outputs named `pred_logits` and `pred_boxes`, a fixed input of 800 x 800 (or a
  free one), opset 17; the class names come from "COCO, 91 ids". The upload, the model check and a pipeline Detect
  objects > Filter (person) > Track > Line > Count > Event must work with that file. It is slow on the CPU (about a
  second per picture at 800 x 800, to be measured): the page's load and the node's time per frame say so, a graphics card
  or a lower frame rate is the answer.
- The graph check: one test per rule and text; the same cases through the editor's view model.
- Tracker and geometry from box sequences as JSON in `Fixtures/Tracks/` (two people passing each other, a short
  occlusion, a back and forth, a crowd of 50): ids stay through a short occlusion, a crossing counts once, a back and
  forth counts In and Out, the bottom centre decides inside.
- Counts, events (on, off, At least, Off after, Condition holds, Report, Count changed at most once a second, Objects
  found), Reset counter, every Compare condition with the bounds of Between, Timers across midnight, a week and a
  daylight saving change (frame times only), Time windows over midnight and on chosen days, Hold, Throttle, Statistics,
  And / Or, Device event, Only while, Dwell, Stationary and Direction, the control link rules, a failing model and an errored branch through the evaluator with a
  fake inference.
- Limits, previews and leases, device groups, the permissions, the model upload with its refusals, class lists and the
  backup of the model list.
- The canvas: adding, linking, refusing a link, undo, the errors below the node; headless screenshots for the look.
- **Real models** (`tests/Oadm.Plugins.Vision.Tests/RealModels/`, category `RealModel`, `manage test models`; never in
  `manage test unit` or CI): RF-DETR medium and two COCO val2017 photos, downloaded at the test build only when asked
  (`-p:OadmRealModels=true`) into the model cache of Text recognition, pinned by revision and SHA-256 (sources in the
  plugin README), never shipped, never in git; without them the tests skip with the reason. On the CPU only (no card is
  looked for, a session on anything else fails the test): the model check of a chunked upload through the real
  `ModelLibrary` (RF-DETR, 576 x 576, Stretch, ImageNet, 91 classes, "COCO, 91 ids"), the real `OnnxInference` on
  both photos against COCO's boxes (every person and car at an overlap of 50 % or more, scores of 50 % or more, no "N/A",
  no person where COCO has none), and Camera > Detect objects > Filter (person) > Count through the real evaluator
  (Count 2 on the photo with two skaters: COCO's annotation has two people and the photo shows no one else).
  The YOLO family the same way (2026-11-08, minimum score 25 % as Ultralytics' default; sources in the plugin README):
  YOLOv8n, YOLO11n (free input), YOLO11n in 16-bit floats, YOLO11n without names, YOLOv5n (v7.0 release, 16-bit),
  YOLOv10n (onnx-community), YOLO26n, YOLOX nano and tiny. The format, input size, fit, scaling and the 80 names or
  "COCO, 80 classes" are checked; a nano model may miss only the COCO objects it was measured to miss ("Measured"),
  every other person and car at an overlap of 50 % or more, no person where COCO has none.
  The DETR family the same way (2026-11-08, minimum score 50 %; the nano YOLO26 25 %): DETR R50 of Transformers.js in
  full precision and quantized, RT-DETR v2 R18, D-FINE S, YOLO26n of Transformers.js, RF-DETR small. Three ImageNet
  classifiers (ResNet-50 of Transformers.js, MobileNetV2 of the ONNX Model Zoo, YOLO11n-cls) uploaded with a labels
  file of the 1,000 ImageNet names (the `id2label` of the ResNet-50 export's `config.json`: none of the files names its
  classes) classify the two cars of COCO val2017 97679 (COCO's boxes as the regions): a car class of ImageNet at 20 % or
  more.
- **On the graphics card** (`tests/Oadm.Plugins.Vision.Tests/Gpu/`, category `Gpu`, `manage test gpu`; never in
  `manage test unit`, `manage test all` or CI, one test process at a time, nothing else on the card): skipped with the
  reason without a DirectML or CUDA card (found without ONNX Runtime) or without the real models. The real
  `OnnxInference` with Use GPU on and the real provider: YOLO11n (opset 19, 32 and 16 bits), YOLO26n, RF-DETR medium and
  ResNet-50 pass their card test, `OnGpu` and `RunsOn` name the card, the detections on both COCO photos are the CPU's
  (same classes, overlap 0.9 or more, scores within 5 points), Use GPU off sends the next call to a CPU worker and on
  again to the card; Ultralytics' YOLO11n of opset 22 runs where its card test says. A fresh process (the test assembly
  started as a program, `GpuChild`) with Use GPU off checks and runs a model and never loads `DirectML.dll`.
  **Both add-ons at once**: a fresh process loads Text recognition and Vision Center through `PluginLoader.LoadPackage`
  from two plugin folders (`-p:OadmGpuPlugins=true` copies both build outputs, with Text recognition's models, into
  `gpu-plugins/`; `OADM_GPU_PLUGINS` points at other folders, such as published add-ons), starts both with Use GPU on
  (Text recognition first, Vision Center first, both at once) and lets them read the camera box of the accuracy set and
  detect on the COCO photo (YOLO11n and RF-DETR medium) together for 20 s: both stay on the card, every result is the one
  of the same add-on's CPU run, no call fails, each add-on's `onnxruntime.dll` and `DirectML.dll` come from its own folder.

## Measured

- The work around a model call (`StageTimesTests`, RealModel, release build, one thread, 2026-11-09), a 720p camera
  picture into a 640 letterbox input:

  | Stage | Before | Now |
  |---|---|---|
  | JPEG decode 720p / 1080p | 6.3 / 14 ms | 3.0 / 6.7 ms (decoded at half, for a 640 input) |
  | The picture into the input | 8.2 ms (Skia's mipmaps built on every call) | 1.6 ms (720p), 3.3 ms (1080p) |
  | Decode and input per frame, 720p / 1080p | about 14.5 / 22 ms | 6.2 / 11.4 ms |
  | A pipeline with yolo11n on the CPU | 9.1 fps | 29 fps (4 frames in work) |
  | A busy preview's state (100 objects, 5 nodes) | 116 KB, read 3.8 ms on the UI thread | 58 KB (6.5 KB with gzip), read 2.7 ms beside it |

  The work around the model is now under 1 ms of a yolo11n call (106 ms on one thread) and within the noise of RF-DETR's.
  The tracker takes 0.3 ms for 60 objects, the evaluator 2.6 ms for 32 nodes and 300 objects (release).
- One frame through 32 nodes with 300 objects (Detect, Track, 7 Area > Line > Count > Event branches, a fake model):
  about 7 ms on the development PC (debug build, `EvaluatorPerfTests`), without the model call.
- The graph check of 30 nodes: about 0.1 ms.
- RF-DETR medium (576 x 576) on the CPU, one thread per call as a worker runs it: about 860 ms per picture on the
  development PC (24 cores, 12 workers; real-model tests, 2026-11-08); the model check's CPU time about 880 ms.
- The YOLO family on the same PC and CPU thread (real-model tests, 2026-11-08; two COCO photos, 640 x 480 and 640 x
  427, minimum score 25 %). Missed: the objects below the score or under 50 % overlap.

  | Model | Format | Classes | ms per picture | Result |
  |---|---|---|---|---|
  | YOLOv8n | YOLO, 640 | 80 from the metadata | 104 | all but the car behind the fence |
  | YOLO11n (free input) | YOLO, 640 | 80 from the metadata | 107 | all but the car behind the fence (20 %) and the person behind (overlap 0.47, without the arm COCO's box holds) |
  | YOLO11n, 16-bit | YOLO, 640 | 80 from the metadata | 104 | the same boxes as in 32 bits |
  | YOLO11n, no names | YOLO, 640 | 80, "COCO, 80 classes" | 90 | as YOLO11n |
  | YOLOv5n, 16-bit | YOLO (v5), 640 | 80 from the metadata | 89 | both people; both parked cars, the car behind the fence and the person behind missed |
  | YOLOv10n | YOLO with suppression, 640 | 80, "COCO, 80 classes" | 89 | all but the right parked car (overlap 0.42) and the person behind |
  | YOLO26n | YOLO with suppression, 640 | 80 from the metadata | 79 | all but the right parked car (11 %) and the person behind |
  | YOLOX nano | YOLOX, 416 | 80, "COCO, 80 classes" | 27 | all but the right parked car (overlap 0.44) and the car behind the fence |
  | YOLOX tiny | YOLOX, 416 | 80, "COCO, 80 classes" | 74 | all but the car behind the fence |

  The boxes of every format land where COCO has them (the people within a few pixels); the misses are the nano
  models' accuracy, not the reading: RF-DETR medium finds all of them.
- The DETR family and the classifiers on the same PC and CPU thread (real-model tests, 2026-11-08; detection minimum score
  50 %, the classifiers on the two cars of COCO 97679).

  | Model | Format | Classes | ms per picture | Result |
  |---|---|---|---|---|
  | DETR R50, Transformers.js | DETR, free input at 640, Keep shape and pad, ImageNet | 91, "COCO, 91 ids" | 690 | all but the person behind (overlap 0.47) |
  | DETR R50, Transformers.js, quantized (8-bit weights) | DETR, as above | 91, as above | 990 | all; passes the scan, slower than full precision on this CPU |
  | RT-DETR v2 R18 | RT-DETR, free input at 640 | 80, "COCO, 80 classes" | 710 | all; a person at 51 % where COCO has none (read at 60 %) |
  | D-FINE S | RT-DETR, free input at 640 | 80, "COCO, 80 classes" | 460 | all but the right parked car (boxed with the skater, overlap 0.41) and the person behind (0.48) |
  | YOLO26n, Transformers.js | RT-DETR, 640 | 80, "COCO, 80 classes" | 85 | at 25 %: all but the person behind (0.49) |
  | RF-DETR small | RF-DETR, 512 | 91, "COCO, 91 ids" | 645 | all |
  | ResNet-50, Transformers.js | Classification, free input at 224, ImageNet | 1,000 from the labels file | 76 | minivan 100 % and 90 % |
  | MobileNetV2, ONNX Model Zoo | Classification, 224, ImageNet | 1,000 from the labels file | 10 | minivan 28 % and 71 % |
  | YOLO11n-cls (no metadata) | Classification, 224, ImageNet; set to 0 to 1 | 1,000 from the labels file | 8 | minivan 66 % and 69 % at 0 to 1; with ImageNet "spotlight" 100 % |

- On the graphics card (2026-11-08, development PC, RTX 5070 Ti 16 GB, driver 616.92, DirectML 1.15.4 of the add-on,
  `Gpu` tests, debug build; ms per picture with decode, fit and decoding, median of 9 warm runs; the card test's blank
  call in brackets): results identical to the CPU (overlap 1.0, scores within 0.0001) except the 16-bit YOLO11n
  (overlap 0.97 or more, scores within 0.012).

  | Model | Card | One CPU thread |
  |---|---|---|
  | YOLO11n, opset 19 (Kalray's export) | 9 to 10 (3.2) | 89 |
  | YOLO11n, 16-bit | 23 (17 to 20) | 102 |
  | YOLO26n | 6 to 8 (2.3) | 80 |
  | RF-DETR medium | 15 (8.3); 33 to 40 in runs where the card had not clocked up | 855 |
  | ResNet-50, one region | 4 to 5 (2.6) | 74 |
  | YOLO11n of Ultralytics, opset 22, free input | (150 to 165) | 106 to 113 |

  After the faster input (2026-11-09, release build): YOLO11n opset 19 6 to 8 ms, YOLO11n 16-bit 10 ms, YOLO26n 4.2
  to 4.4 ms, RF-DETR medium 13.5 ms per picture on the card; beside Text recognition YOLO11n 6.3 ms and RF-DETR 13.8 ms,
  Text recognition's camera box 62 to 64 ms; card memory 1,829 MB before, 2,804 MB with both, 1,829 MB after.
  The Gpu tests' child process keeps its plugin loader to the end as the server's singleton does: a release build keeps
  no local of an async method past its last use, and a collectible load context nothing holds starts unloading from its
  finalizer.
  The 16-bit model spends about 14 ms converting its input and output on the CPU, on the card as on the CPU.
  Ultralytics' v8.4.0 YOLO11n is opset 22: ONNX Runtime 1.24's DirectML has no Conv and MaxPool of opset 22, so 88 Conv
  and 3 MaxPool nodes run on the CPU between the card's (`Node placements` of the verbose log) and a fixed input size
  does not help; the same model at opset 19 or 20 (YOLOv8n of the same release, opset 20: 2.5 ms per blank call) runs on the card. Its card test
  sits at the rule's edge (1.5 times one CPU call): it ran on the card in some runs and stayed on the CPU in others.
- **Both add-ons at once** on that card (2026-11-08, one process, each add-on from its own folder, in all three orders,
  also from published win-x64 folders): both on the card, Text recognition with text finder, reader and angle model
  there. Text recognition's camera box (full HD, 4 fixed areas) 58 ms per picture alone and 67 ms beside Vision Center;
  Vision Center 10 ms (YOLO11n) alone and beside it, RF-DETR medium 17 ms; in 20 s about 300 readings and 1,470
  detections, every one as on the CPU. The card's memory 1.75 GB before (the desktop), 2.73 GB with both started and
  at the peak, 1.75 GB after both stopped. The process holds two `onnxruntime.dll` and two `DirectML.dll`, each from
  its add-on's folder; no driver reset.
  Before 2026-11-08 the Hugging Face DETR was refused (its `pixel_mask` input), RT-DETR and D-FINE read as DETR with 79
  classes (softmax, the last class taken for "no object"), and ResNet-50 ran at 640 x 640 with 0 to 1 (MobileNetV2 then
  says "mosquito net" for a car).

## Later

- Anomaly detection as a model kind and a node (a score per picture or area, an event above a limit).
- Models we tested (DETR-style, Apache-2.0) as downloads on oadm.app.
- Nodes from other plugins through the SDK (`IVisionNode` opened as an SDK contract).
- Segmentation and pose, merges (AND / OR of two branches), counts kept as history.
- Text recognition as a node.

## Open points

- Both vision add-ons on a graphics card in one server: done for DirectML on Windows (2026-11-08, "Measured": each
  add-on's own `DirectML.dll`, no shared one needed). Open: CUDA on Linux x64 (no card at hand), other cards than the
  RTX 5070 Ti, and the card's memory: Text recognition's session budget counts only its own sessions, Vision Center's
  are outside it.
- The card rule compares one call on the card with one on a CPU thread, while the card runs one call at a time and the
  CPU has its workers: a model 1.4 times slower on the card (Ultralytics' YOLO11n of opset 22 with DirectML) goes to the
  card and carries far fewer pictures per second than the workers would, and a model near 1.5 times flips between runs.
  Whether the rule should weigh the workers is not decided.
- Models of opset 22 run largely on the CPU with DirectML (ONNX Runtime 1.24); the user docs ask for opset 20 or lower.
- The accuracy with DETR and RF-DETR on the development cameras, the default frame rate and resolution from those
  measurements.
- macOS and Linux runs of the add-on.
- One worker budget for both vision add-ons instead of one each: Text recognition's CPU pool and its card memory budget
  are not shared.
- A detection below 50 % never starts a track, it only keeps one: with a Minimum score below 50 % such objects are
  never tracked.
- YOLO26n as onnx-community exports it for Transformers.js is read as RT-DETR since the blank call tells a "no object"
  class apart (2026-11-08, "Output formats"). A sigmoid model with one or two classes can still look like DETR to that
  rule; the model's Settings choose the format.
- Classifiers without metadata that want 0 to 1 (an Ultralytics classifier whose metadata was stripped, as mobilint's
  YOLO11n-cls) get ImageNet scaling and answer nonsense until the model's Settings set 0 to 1.
- Hugging Face and ONNX Model Zoo classifiers carry no class names: the user uploads a labels file (the 1,000 ImageNet
  names from the export's `config.json` `id2label`); a built-in ImageNet list is not decided. Google's MobileNet of
  Hugging Face (1,001 classes with a background class, mean and deviation 0.5) is not covered by the scalings.
- "COCO, 80 classes" is also the list of RT-DETR, D-FINE and Transformers.js YOLO26 (the same 80 names): its name says
  YOLO only.
- DETR of Hugging Face is trained with the shorter side at 800; the free input takes 640 by default (690 ms per
  picture), 800 is not measured.
- Not tested, no ready ONNX file: DEIM (Intellindust publishes PyTorch weights, OpenVINO files only), a YOLO
  classification export of Ultralytics with its metadata (only `.pt` releases).
- YOLO exports that end before the decoding (three raw head outputs, e.g. Kalray's `yolov8n.optimized.onnx`) are refused
  as outputs of no known format; exports with the channels last (`[1, 640, 640, 3]`, AMD's quantized YOLOv5s) as having
  no picture input.
- YOLOv5n of the v7.0 release (16-bit) finds fewer objects than the other nano models at 25 %; whether the 16 bits or
  the model cost it is not measured (no 32-bit YOLOv5n release to compare).
