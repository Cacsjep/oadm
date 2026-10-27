# Metadata Monitor (`oadm.metadata-monitor`)

Core plugin with a rail page in group Monitoring (icon `activity`), with `.Client`, off by default. A port of the AXIS
Metadata Monitor tool: pick one camera, Start, and watch its event stream live (I/O ports, virtual inputs, storage,
temperature, tampering, ACAP events). Read only for devices. Spec:
[Bundled plugins](../../docs/spec/bundled-plugins.md#metadata-monitor-core-plugin).

## Decisions

| Topic | Decision |
|---|---|
| Who connects | The **server** opens the RTSP metadata stream with the stored credentials (like the live view); passwords never reach the client. |
| Content | **Events only**: exactly `rtsp://<device>/axis-media/media.amp?video=0&audio=0&event=on` (no analytics scene data, no free address field). |
| Cameras | **One camera at a time**, chosen in the shared device picker (a drop-down of every camera cannot be used with tens of thousands of devices); switching the camera stops the running stream. |
| Saving | **No saving**: no export, no capture files. A single message can be copied from the detail view. |
| RTSP port | The stream uses the port set with the context menu **OADM > RTSP port** ([Live view](../../docs/spec/live-view.md)); the page has no port field. `start` ignores the `rtspPort` an older client sends and never changes the stored port. Verified with an AXIS Q3548-LVE behind NAT (RTSP 554 forwarded to 32077). |

## Device API

- RTSP/1.0 over TCP to port 554 (or the stored RTSP port), RTP interleaved, Digest with the stored credentials (Basic
  never on plain RTSP), DESCRIBE / SETUP of the one `application` media (`vnd.onvif.metadata`) / PLAY, keep-alive
  (GET_PARAMETER or OPTIONS before the session timeout), TEARDOWN on Stop.
- RTP payload: XML fragments of one `tt:MetadataStream` document per message, the RTP marker bit ends a document;
  fragments are joined by sequence number, a gap drops the document (counted, "1 message lost"). At most 1 MB per
  document.
- The development camera (AXIS P3265-V, AXIS OS 12.11) sends one document per RTP packet, one notification per document,
  an empty `tt:MetadataStream` first and 128 Initialized messages at PLAY (64 virtual inputs, PTZ, storage, temperature,
  ...), the first about 2.6 s after Start through the server.

## How it works

- SDK `IDeviceEventStreams` on `ICorePluginContext.EventStreams` (DIM null; `IDeviceEventSource`,
  `DeviceMetadataDocument`, `DeviceStreamException` with Unreachable / Unauthorized / NotSupported / Protocol),
  implemented by `Oadm.Core.LiveView.DeviceEventStreams` (device address + `CredentialStore`) on `RtspMetadataSource`
  (Core `LiveView/Rtsp`: the live view's `RtspClient`, `SdpMetadataTrack` for the `m=application ... vnd.onvif.metadata`
  media, OPTIONS keep-alive on a timer every half session timeout (at most 10 s), a silence watchdog: no packet and no
  keep-alive answer for 2 x keep-alive + 5 s = connection lost; event streams can be quiet for minutes,
  `RtspClient.SinceLastReceive`) and `Rtp/MetadataDepacketizer` (a gap drops the document it hits; a packet after a gap is
  kept only when it starts a new XML document).
- Plugin: `MetadataMonitorPlugin` (sessions per page, `MetadataMonitorOptions`), `Streaming/MonitorSession` (read loop,
  reconnect with backoff 1, 2, 4, 8, 10 s and status "Reconnecting", batches split below the 1 M character event limit,
  raw XML per message cut at 256 K characters).
- Parsing (`Parsing/MetadataParser`, XDocument without DTDs): every `wsnt:NotificationMessage` in a document becomes one
  message: topic (`wsnt:Topic`, prefixes resolved by namespace, `tns1:` / `tnsaxis:` dropped for the tree text:
  `Device/IO/VirtualInput`, other prefixes kept), UTC time and `PropertyOperation` (Initialized, Changed, Deleted) of
  `tt:Message`, `Source` / `Key` / `Data` SimpleItems (name, value), the raw XML of the notification (pretty-printed for
  the detail view), and the capture time (server receive time). Category = "Event". Malformed XML becomes one message
  with Category "Invalid" and the raw text.
- Info text (like the AXIS tool's Info column): `[INIT] port = 33; active = 0;` = `[INIT]` / `[CHANGED]` / `[DELETED]`
  from the property operation, then every Source, Key and Data item as `name = value;` in document order.
- Refused without a request: Certificate changed, Credentials required, Password not set and Site offline, with the
  shared texts of the other plugins (`DeviceMessages.Refusal`, "Site offline: Building B"). At most 16 streams at once (`MaxStreams`; "Too many event
  streams are open (16); stop one first"): `start` takes a place in one step before the job starts and the job gives it
  back when it ends, so Starts at the same time never open more.
- One stream per page session; it ends on Stop, when the page's watch ends (client closed or switched page) or without
  its keep-alive: the page calls `keepAlive` every 5 s while a stream runs, the server stops streams without one for
  20 s.

## Page

One card (`HasOwnCards` false), `ui:PageHeader.Subtitle` "Shows the events a camera sends, live.".

- One toolbar row (fits the 1280 px minimum window with the rail expanded): the camera command button ("10.0.0.48 ·
  P3265-V", else "Choose a camera") opens the shared `DevicePickerWindow.ShowOneAsync` (search, virtualized, 30,000
  devices) with the managed video and I/O devices (others are not offered); the view hands the picker to the view model as
  `PickCamera`; another camera stops the running stream, a removed one clears the choice. Then **Start** / **Stop**
  (command button with the start or stop icon), `ui:SearchBox` filter ("Filter events", live, case-insensitive over
  topic, info and raw XML), **Clear** (empties the list, the stream keeps running), **Autoscroll** toggle and the count
  ("134 messages", "5 of 15 messages"); on the right the status chip (Connecting accent, Live ok "Live", plus " · 3
  messages lost" when documents were lost, Reconnecting warning, Error red with the text).
- Autoscroll follows the grid's vertical scroll bar (wheel and bar): at the end on, above off; the newest row stays
  visible while on.
- The empty list says "Choose a camera and press Start."; the detail pane and its splitter appear only while a message
  is selected.
- List (virtualized DataGrid, `RangeObservableCollection`, batches appended in one step): Seq# (per stream, from 1),
  Timestamp (UTC, the message's UtcTime, `yyyy-MM-dd HH:mm:ss.fff`), Event topic, Capture time (UTC, `HH:mm:ss.fff`),
  Property operation, Info (star width); Category is hidden (always "Event"), no raw Data column (it is in the detail
  view). The client keeps the newest **10,000** messages (oldest removed in one step); the selection is kept through the
  grid's Reset of each batch.
- Detail (below the list, splitter, height in `LocalApplicationData/Oadm/plugins/oadm.metadata-monitor/client.json`): the
  selected message's XML in `ui:CodeView` (Xml, always pretty-printed and highlighted) with a copy button (copies the
  pretty XML through the TopLevel clipboard).
- Scale: a camera can send hundreds of Initialized messages at Start (every virtual input); batching, one collection
  change per batch and the virtualized grid keep the UI responsive; the filter is O(n) over at most 10,000 rows.

## Methods

`MetadataMethods`. Messages are pushed through `ICorePluginContext.Events`: topic `messages` (batched every 250 ms, at
most 500 per batch; above that the oldest of the batch are dropped and counted) and `state` (Connecting, Live,
Reconnecting, Stopped, Error + text, message count, lost count).

| Method | Payload | Answer |
|---|---|---|
| `start` | {deviceId} | {streamId}, or `error` ("The device has no event stream", "Unauthorized - HTTP 401 (check the credentials)", "Unreachable - ...") |
| `stop` | {streamId} | none |
| `keepAlive` | {streamId}, every 5 s while running | none |
| `getRtspPort` | {deviceId} | {port}, the stored RTSP port |

## Device groups

A stream is a job of its user (`ctx.Jobs`). `stop` and `keepAlive` look it up for the user; another user's id and an
unknown one fail alike ("The event stream has ended.", `MetadataMethods.StreamEnded`; Stop on the page then shows
Stopped), and so does `keepAlive` for a job whose stream already ended (its lease expired): the page then shows Stopped and
keeps its messages, never Live without events. `start` and `getRtspPort` take the camera from the user's call (outside the
user's group: NOT_FOUND "1 device is no longer available."); the stream is opened with `caller.EventStreams`, so it ends
with Error "1 device is no longer available." when the camera alone leaves the user's view, and Stopped when the user's
group or its rules change (the host ends the job). Messages and states go only to the page's session. See
[Device groups](../../docs/spec/device-scope.md).

## Tests

- `tests/Oadm.Plugins.MetadataMonitor.Tests`: parser (namespaces, several notifications per document, malformed XML, Info
  text), lifecycle with `FakeEventStreams` (start, stop, reconnect, refused statuses, watch ended), `CanaryTests` (the
  device-group canary and stream owners), view model (camera button: offered devices, the chosen camera into Start, a
  removed camera stops the stream, Perf 30,000 devices; filter, clear, autoscroll, 10,000 cap, selection survives
  batches), fake mode, headless screenshots `metadata-monitor-page.png` (live list + detail) and `-error.png`, Perf
  `ScaleTests` (10,000 messages, a burst of 2,000 in one batch).
- Core: `MetadataDepacketizerTests` (recorded packets of the development camera), `RtspMetadataSourceTests` (scripted
  RTSP server), `DeviceEventStreamsTests`, `MetadataRecorderTests` (Hardware; re-records `Fixtures/LiveView/events.sdp` +
  `.rtp` with `OADM_RECORD_RTP_DIR`, read-only stream).
- Read-only hardware test `MetadataHardwareTests` against the development camera through the in-process server and gRPC
  Watch (Start, at least one Initialized message, Stop).
- Fake mode (`FakeOadmApi.MetadataMonitor.cs`): a generated burst (64 virtual inputs, digital input, storage,
  temperature), then a change every 1.5 s; statuses refused like the server.
