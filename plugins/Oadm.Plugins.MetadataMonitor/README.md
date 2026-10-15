# Metadata Monitor (`oadm.metadata-monitor`)

`plugins/Oadm.Plugins.MetadataMonitor` (+ `.Client`), id `oadm.metadata-monitor`, rail page **Metadata Monitor** (icon
`activity`), **off by default**. A port of the AXIS Metadata Monitor tool: pick one camera, Start, and watch its event stream live, the way a
technician checks which events a device sends (I/O ports, virtual inputs, storage, temperature, tampering, ACAP events).
Read-only for devices.

| Topic | Decision |
|---|---|
| Who connects | The **server** opens the RTSP metadata stream with the stored credentials (like the live view); passwords never reach the client. |
| Content | **Events only**: exactly `rtsp://<device>/axis-media/media.amp?video=0&audio=0&event=on` (no analytics scene data, no free address field). |
| Cameras | **One camera at a time**; switching the camera stops the running stream. |
| Saving | **No saving**: no export, no capture files. A single message can be copied from the detail view. |

- **Transport (server, `Oadm.Core/LiveView` RTSP client reused):** RTSP/1.0 over TCP to port 554, RTP interleaved, Digest
  with the stored credentials (Basic never on plain RTSP), DESCRIBE / SETUP of the one `application` media
  (`vnd.onvif.metadata`) / PLAY, keep-alive (GET_PARAMETER or OPTIONS before the session timeout), TEARDOWN on Stop.
  RTP payload: XML fragments of one `tt:MetadataStream` document per message, the RTP marker bit ends a document;
  fragments are joined by sequence number, a gap drops the document (counted, "1 message lost"). At most 1 MB per
  document. Devices with status CertificateChanged / CredentialsRequired / PasswordNotSet are refused with the device
  status text. One stream per page session; the server ends it when the page's watch ends (client closed or switched
  page) or after Stop. Reconnect with backoff 1, 2, 4, 8, 10 s on a broken connection, status "Reconnecting".
- **Parsing (server):** every `wsnt:NotificationMessage` in a document becomes one message: topic (`wsnt:Topic`, namespace
  prefixes resolved, `tns1:` / `tnsaxis:` dropped for the tree text: `Device/IO/VirtualInput`), UTC time and
  `PropertyOperation` (Initialized, Changed, Deleted) of `tt:Message`, `Source` / `Key` / `Data` SimpleItems (name, value),
  the raw XML of the notification (pretty-printed for the detail view), and the capture time (server receive time).
  Category = "Event". Malformed XML becomes one message with Category "Invalid" and the raw text.
- **Info text** (like the AXIS tool's Info column): `[INIT] port = 33; active = 0;` = `[INIT]` / `[CHANGED]` / `[DELETED]`
  from the property operation, then every Source, Key and Data item as `name = value;` in document order.
- **Page methods / events (`MetadataMethods`):** `start` ({deviceId}) -> {streamId} or `error` ("The device has no event
  stream", "Unauthorized - HTTP 401 (check the credentials)", "Unreachable - ..."), `stop`, `keepAlive` (every 5 s while running). Messages are pushed through `ICorePluginContext.Events`
  (topic `messages`, batched every 250 ms, at most 500 per batch; above that the oldest of the batch are dropped and counted)
  and a `state` event (Connecting, Live, Reconnecting, Stopped, Error + text, message count, lost count).
- **Page (client, `HasOwnCards` false: one card):** `ui:PageHeader.Subtitle` "Shows the events a camera sends, live.".
  One toolbar row (fits the 1280 px minimum window with the rail expanded): the camera button (secondary, it names
  the chosen camera "10.0.0.48 · P3265-V", else "Choose a camera") opens the shared `DevicePickerWindow.ShowOneAsync`
  (search, virtualized, 30,000 devices) with the managed video and I/O devices (others are not offered); the view hands the
  picker to the view model as `PickCamera`; another camera stops the running stream, a removed one clears the choice. The
  primary **Start** / **Stop** sits directly right of it, `ui:SearchBox` filter (live, case-insensitive
  over topic, info and raw XML; "Filter events"), **Clear** (empties the list, the stream keeps running), **Autoscroll**
  toggle (on: the newest row stays visible; scrolling up turns it off, scrolling to the end turns it on again) and the
  count ("134 messages", "5 of 15 messages"); on the right the status chip (Connecting accent, Live ok "Live", plus
  " · 3 messages lost" when documents were lost, Reconnecting warning, Error red with the text). The empty list says
  "Choose a camera and press Start."; the detail pane and its splitter appear only while a message is selected.
  List (virtualized DataGrid, `RangeObservableCollection`, batches appended in one step): Seq# (per stream, from 1),
  Timestamp (UTC, the message's UtcTime, `yyyy-MM-dd HH:mm:ss.fff`), Event topic, Capture time (UTC, `HH:mm:ss.fff`),
  Property operation, Info (star width); Category is hidden (always "Event"). No raw Data column (it is in the detail view). The client keeps the newest **10,000**
  messages (oldest removed in one step).
  Detail (below the list, splitter, height persisted per client): the selected message's XML in `ui:CodeView` (Xml,
  always pretty-printed and highlighted, no Raw switch) with a copy button (copies the pretty XML).
- **Scale:** a camera can send hundreds of Initialized messages at Start (every virtual input); batching, one collection
  change per batch and the virtualized grid keep the UI responsive; filter is O(n) over at most 10,000 rows. Test with
  10,000 messages and a burst of 2,000 in one batch.
- **Tests:** RTP metadata depacketizer from recorded packets of the development camera (`OADM_RECORD_RTP_DIR`, read-only stream), XML
  parsing incl. namespaces, multiple notifications per document, malformed XML, Info text; server stream lifecycle with a
  fake RTSP source (start, stop, reconnect, refused statuses, watch ended); page view model (camera button: offered devices, the
  chosen camera into Start, a removed camera stops the stream, Perf 30,000 devices; filter, clear, autoscroll, 10,000 cap,
  selection survives batches); headless screenshots `metadata-monitor-page.png` (live list + detail),
  `-error.png`; read-only hardware test against the development camera (Start, at least one Initialized message, Stop).
- **Implementation:** SDK `IDeviceEventStreams` on `ICorePluginContext.EventStreams` (DIM null; `IDeviceEventSource`,
  `DeviceMetadataDocument`, `DeviceStreamException` with Unreachable / Unauthorized / NotSupported / Protocol), implemented
  by `Oadm.Core.LiveView.DeviceEventStreams` (device address + `CredentialStore`) on `RtspMetadataSource` (Core
  `LiveView/Rtsp`: the live view's `RtspClient`, `SdpMetadataTrack` for the `m=application ... vnd.onvif.metadata` media,
  OPTIONS keep-alive on a timer every half session timeout (at most 10 s), a silence watchdog (no packet and no keep-alive
  answer for 2 x keep-alive + 5 s = connection lost; event streams can be quiet for minutes, `RtspClient.SinceLastReceive`))
  and `Rtp/MetadataDepacketizer` (a gap drops the document it hits; a packet after a gap is kept only when it starts a new
  XML document). The development camera (AXIS P3265-V, AXIS OS 12.11) sends one document per RTP packet, one notification per document, an empty
  `tt:MetadataStream` first and 128 Initialized messages at PLAY (64 virtual inputs, PTZ, storage, temperature, ...),
  the first about 2.6 s after Start through the server. Plugin: `MetadataMonitorPlugin` (sessions per page,
  `MetadataMonitorOptions`), `Streaming/MonitorSession` (read loop, reconnect, batches split below the 1 M character event
  limit, raw XML per message cut at 256 K characters), `Parsing/MetadataParser` (XDocument without DTDs, prefixes resolved
  by namespace: `tns1:` / `tnsaxis:` dropped, other prefixes kept). Device refusals use the snapshot report's status
  texts ("Credentials required - the device rejects the stored credentials", ...). A page's stream also ends without its
  keep-alive: the page calls `keepAlive` ({streamId}) every 5 s while a stream runs, the server stops streams without one
  for 20 s (client closed or gone; switching pages stops the stream at once). The camera is chosen in the shared device
  picker, not a drop-down: a drop-down of every camera cannot be used with tens of thousands of devices. Autoscroll follows the
  grid's vertical scroll bar (wheel and bar: at the end on, above off); the selection is kept through the grid's Reset of
  each batch. Detail height in `LocalApplicationData/Oadm/plugins/oadm.metadata-monitor/client.json`; copy = the shown
  text (pretty or raw) through the TopLevel clipboard. Fake mode (`FakeOadmApi.MetadataMonitor.cs`): generated burst
  (64 virtual inputs, digital input, storage, temperature) then a change every 1.5 s, statuses refused like the server.
  Test project `tests/Oadm.Plugins.MetadataMonitor.Tests` (parser, lifecycle with `FakeEventStreams`, view model, fake
  mode, headless screenshots, Perf `ScaleTests`, hardware `MetadataHardwareTests` through the in-process server and gRPC
  Watch); Core: `MetadataDepacketizerTests`, `RtspMetadataSourceTests` (scripted RTSP server), `DeviceEventStreamsTests`,
  `MetadataRecorderTests` (Hardware; re-records `Fixtures/LiveView/events.sdp` + `.rtp` with `OADM_RECORD_RTP_DIR`).

- **RTSP port**: cameras behind NAT whose RTSP port is forwarded to another port get it in the device list, context menu
  **OADM > RTSP port** (permission "Optics Control"; server setting `Devices.RtspPorts` = JSON object device id -> port,
  written by `Oadm.Core.LiveView.DeviceEventStreams`). Every event stream of the device and the live view use it. The page
  has no port field any more: `start` ignores the `rtspPort` an older client sends and never changes the stored port;
  `getRtspPort` ({deviceId}) still answers. Verified with an AXIS Q3548-LVE behind NAT (RTSP 554 forwarded to 32077).
