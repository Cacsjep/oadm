# Live view

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Clicking the device icon (first grid column) of a video device opens the live video in a panel
on the right of the device card, inside the main window, so nobody has to log in to the camera
web UI.

- Panel: reusable `Controls/LiveViewPanel` in a card next to the device card (about 65 : 35,
  resizable with the vertical `paneSplitter`; the device card keeps at least 900 px, toolbar entries that do not fit go
  into the More menu, Address and Status keep minimum widths so they stay readable; the panel at least 320 px). Slides in from the right (250 ms, cubic ease-out).
  Header, one row: `IconLabel` with video icon and the model (" · <channel name>" with several channels), close button.
  Below it the shared live video. `LiveView/LiveViewViewModel` only opens, switches and closes it and remembers the chosen
  channel per device for the client session.
- **Live video component** (`LiveView/LiveVideoViewModel` + `LiveVideoView`, the one live picture of the client: side
  panel, fullscreen, the tiles of the Live View and Dashboards plugins through `ILiveVideoAccess`, which must stay smooth
  with 16 at once). Parts on or off per place through
  `LiveVideoOptions` (channel switch, fullscreen, quality switch, controls button, each PTZ part, zoom, focus; `MaxFps`
  25; the start quality; delays); client-wide `LiveVideoSettings` (Settings > Client: "Show video details over the
  picture", off; "Frame rate (adaptive quality)", 20, 1..30; `ClientSettings.ShowVideoDetails` / `AdaptiveFps`; plus
  "Optics Control" of the role). The picture area shows only the picture (`Stretch=Uniform`), the `ui:ProgressRing`
  (connecting without a picture, reconnecting with the reason below it, or a new stream slow to deliver) or the error
  (`ui:StatusChip`). Over it, small plates at the edges (`Border.videoOverlay`): top left on hover fullscreen and the
  quality button (**Adaptive**: the smallest resolution of the channel's list that fills the picture's size in device
  pixels without enlarging, at the adaptive frame rate; **Full**: the largest at `MaxFps`; default Adaptive; both take
  **16:9** whenever the channel lists 16:9 resolutions (almost every monitor is 16:9, also a 4:3 sensor offers them), else
  the sensor's aspect: `LiveVideoViewModel.PreferredAspect`); top right the
  orange GPU warning icon (always, what to do as tooltip) and on hover the channel switch ("1 2 ...", tooltip = name; the
  place can turn it off and set the channel), below them the details card when the setting is on (`Border.videoDetails`,
  one row each: Codec, Resolution, Frame rate, Bit rate, Decoding GPU or Processor; `VideoDetails`; top right because the
  bottom belongs to the controls); at the bottom one row: the zoom and focus or PTZ bar over the width and at its right end
  the controls button (`Icon.sliders`, only for a camera with zoom and focus or PTZ and a role with "Optics Control"; on
  hover only while the controls are closed): it opens and closes the bar, **open from the start** (`LiveVideoOptions.ControlsOpen`,
  default true; a grid tile can start closed); the user's choice holds while the component lives and goes to fullscreen; the
  PTZ circle shows only while it is open. Hover parts: theme `Border.liveVideo:pointerover
  :is(Control).videoHover`.
  **Silent swap**: a new size (after it rests 500 ms), a new quality, a new adaptive frame rate, a finished GPU test or a
  restart after hiding opens a second stream only when its request (channel, resolution, fps) differs; the old picture
  keeps showing until the new stream's first picture, then the old stream closes; after 2 s without a picture the ring
  shows over the old picture; a new stream that fails is dropped and logged, the old one stays. Switching the channel
  clears the picture. **Visibility**: the view reports attached, effectively visible, inside the visible part of a
  scrolling parent and window not minimized (checked every 500 ms and on `EffectiveViewportChanged`); hidden for 2 s the
  stream stops and the last picture stays (state Paused); shown again it streams with the silent swap. **Fullscreen**
  (button or double-click): `LiveView/FullscreenVideoWindow` (borderless, full screen, owned by the main window) with its
  own `LiveVideoViewModel` and stream of the same channel at the screen's size; the video below counts as hidden while it
  is open; Esc, the button or a double-click close it. The resolution list comes from the server
  (`LiveViewSource.resolutions = 6`, `LiveViewResolution {width, height}`; the request asks for exactly that size).
- **Zoom and focus** (`LiveView/OpticsViewModel`, `OpticsBar`, `FocusAreaOverlay`; server `Core/LiveView/IDeviceOptics`,
  `VapixClient.Optics.cs`, Optics Control API `axis-cgi/opticscontrol.cgi`): a bar over the bottom of the picture
  (`Border.opticsBar`, overlay brush, opened with the controls button) only for a camera with remote zoom or focus (`getCapabilities`: zoom with
  `maxMagnification`, focus) and a role with "Optics Control"; other cameras show nothing, no error. Zoom slider 1 to the
  maximum, Focus slider near to far (0..1 on the device), small knobs (`Slider.optics`), no values shown; icon buttons
  Autofocus (`Icon.focus`), Autofocus area (`Icon.focusArea`: the current area drawn over the picture, dimmed around it,
  a drag draws a new one; Set sends it and runs autofocus, Cancel puts the camera's area back) and Reset focus (`reset`
  focus, then autofocus). A slider is sent once it rests for 300 ms and keeps the user's value (the camera answers while
  the lens still moves); autofocus waits for the camera (about 4 s, 45 s timeout) and its refusal shows in the bar
  ("The camera could not find focus. ..."). The server checks the fresh capabilities and clamps every value before
  sending (zoom 1..maximum, focus 0..1, the area inside the picture with at least 5 % per side); refused statuses
  without a request. Per source: the lens of the live view source's sensor (`OpticsRequest.sensor` = the 0-based
  `LiveViewSource.sensor` = opticsId, else the first optics); switching the source reads it again. Verified on the AXIS P3265-V (AXIS OS 12.11, API 1.3): set calls
  answer at once, zoom also moves focus, out-of-range values are error 2104; opt-in `OpticsHardwareWriteTests`
  (`OADM_OPTICS_WRITE=1`, puts zoom, focus and area back). Fake mode: the P3265-V models have zoom and focus.
- **PTZ** (`LiveView/PtzViewModel`, `PtzOverlay`, `PtzBar`; server `Core/LiveView/DevicePtz`, `VapixClient.Ptz.cs`, PTZ API
  `axis-cgi/com/ptz.cgi`): for a camera with `Properties.PTZ.PTZ=yes` (these have no Optics Control) and a role with
  "Optics Control", per channel (the live view's source; both channels of a bispectral camera move the same head) what
  `info=1` lists, while the controls are open: a circle with a cross in the middle of the picture: dragging from it moves the camera
  (`continuouspantiltmove`, speed by the distance, reach half the picture, 8 % dead zone, up on screen = tilt up), repeated
  every 300 ms with `timeout=1` so the camera stops by itself 1 s after the last (a lost client never leaves it turning),
  release sends a stop; moves go out one at a time with the newest speed (pointer moves in between are skipped, so a stop
  is never overtaken); only the left button drags, another button or finger is ignored; the drag also ends when the window
  loses the focus, the overlay hides or the role loses "Optics Control"; the mouse wheel zooms (`rzoom`, 400 steps a notch, sent once the wheel rests); a click elsewhere
  centers the camera on that point (`center`, thousandths of the picture). The bar (`Border.opticsBar`): Home
  (`move=home`, `Icon.home`), the presets (`query=presetposall&format=json`, choosing one = `gotoserverpresetname`) and a
  zoom slider (`zoom=1..9999`); the zoom is read again after a move. Errors are HTTP 200 "Error:..." (`PtzException`).
  Verified with an AXIS Q6411-LE of the AXIS Virtual Loan Tool (AXIS OS 13.0.88) behind port forwards; opt-in
  `PtzHardwareWriteTests` (`OADM_PTZ_URL`, `_USER`, `_PASSWORD`, `_RTSP_PORT`; also streams over the forwarded RTSP port).
  Fake mode: the Q6135-LE (10.0.0.22) is a PTZ camera with three presets.
- **RTSP port** (context menu **OADM > RTSP port**, video devices, "Optics Control"; `Devices/RtspPortWindow` +
  `RtspPortViewModel`, single operation): the port OADM connects to for live view and events of the selection, for
  cameras behind a port forward (AXIS Virtual Loan Tool, NAT); one call for the selection; empty or 554 = default,
  "Use default"; "The devices use different ports." when they differ; intro "Used for live view and events. Not set on the
  camera."; Save waits for the current port to be read (a failed
  read says so below the field). Stored in the server setting `Devices.RtspPorts` (`DeviceEventStreams`; one read and one
  write for the whole selection, `IDeviceEventStreams.GetRtspPortsAsync` / `SetRtspPortsAsync`, ports of removed devices
  dropped in the same write), read by the live view (`AxisLiveVideoSourceFactory`) and the Metadata Monitor; nothing is
  written to the devices. A saved port reopens the shown live view on a new camera connection (`LiveViewHub.Invalidate`:
  the stream key carries a generation per device, new viewers never join a connection on the old port).
- Open/close: the icon of the shown device or the close button or Escape closes the panel and
  stops the stream; another device's icon switches. Removing the shown device closes it. The icon
  cell has a hover state and the tooltip "Live preview"; devices without video are disabled
  (`LiveViewSupport.IsSupported`: the device's `HasVideo` from its category, see [data-model.md](data-model.md); a device
  whose category is still Unknown is allowed and the stream tells).
- Sources: `ListSources` returns every `Image.I<n>` that is not disabled as camera `n+1` with its
  `Image.I<n>.Name` (view areas on single-sensor cameras, sensors and quad view on multisensor
  cameras, inputs on encoders), each with every resolution it lists (all aspects; the client picks, see above). With more than one source the live
  video shows the channel switch; switching restarts the stream with `camera=<n>`; the side panel remembers the choice
  per device for the client session. The development camera (AXIS P3265-V) reports one sensor
  (`ImageSource.NbrOfSources=1`), 8 view areas, of which "View Area 1" (full frame) and
  "View Area 2" (cropped) are enabled.
- Transport: the server opens RTSP/1.0 over TCP to port 554 with RTP interleaved on the same
  connection, Digest authentication with the stored credentials (Basic on plain RTSP only while the server setting
  `Devices.AllowBasicOverHttp` is on, `RtspClient.AllowBasic`, passed by `AxisLiveVideoSourceFactory` and
  `DeviceEventStreams` (Metadata Monitor, Device triggers, Dashboards); Digest stays preferred when offered; with the
  setting off a camera that offers only Basic fails with `DeviceMessages.BasicOnlyOverHttp` and no password is sent;
  credentials never reach the client), `Blocksize: 64000`, a keyframe every second
  (`videokeyframeinterval=fps`). Own RTSP client and depacketizers (H.264 RFC 6184, H.265 RFC 7798)
  in `Oadm.Core/LiveView`. Video itself is not encrypted on the camera link (no RTSP over HTTPS). Devices with status `CertificateChanged` are refused.
- Negotiation: H.265 > H.264 among the codecs the camera lists in `Properties.Image.Format` and
  the client can decode; when opening a codec fails the next is tried. No MJPEG. Resolution: a size the source lists is
  taken as asked (the client asks for one of the list, see the live video component); otherwise the largest of the
  source's resolutions with the sensor aspect that fits the requested box (default 1920x1080), at the requested frame rate.
- Sharing: one upstream camera connection per (device, camera, codec, resolution, fps) shared by
  all viewers, opened on the first and closed when the last viewer leaves. A joining viewer gets
  the cached GOP first (picture within one frame time). Slow viewers never buffer: a full queue
  (60 frames) is dropped and the viewer resumes at the next keyframe. Packet loss drops access
  units until the next keyframe.
- **Stream probes** (SDK 0.6.0 `ICorePluginContext.StreamProbes`, `Core/LiveView/Probes`; for the Load test,
  [load-test.md](load-test.md)): measuring streams on the server, never shared with the live view, nothing
  decoded or passed on. `ListSourcesAsync`: the sources of `ListSources` above with their largest resolution (pixel
  count), the frame rate of the sensor's enabled capture mode (`capturemode.cgi` getCaptureModes `maxFPS`, else 30) and
  H.265 > H.264 (no MJPEG). `OpenAsync` (`RtspStreamProbe`): the stored credentials and Basic rules, port and route as
  above, `CertificateChanged` refused before any connection, `videokeyframeinterval=fps`, the camera's own compression
  and Zipstream; a lower `a=framerate` in the SDP becomes the probe's frame rate.
  - RTP over **UDP** only here (live view and event streams stay TCP): `Transport: RTP/AVP;unicast;client_port=a-b`
    (`RtspTransport`), a pair from `RtpUdpPorts` UDP 50000-50799 (even RTP, odd RTCP, 400 pairs; a pair another
    program holds is skipped), bound through `DeviceRouter.BindUdp` (the server's own network only). Only datagrams from
    the camera's address of the RTSP connection count. One RTP and one RTCP packet right after PLAY open a stateful
    firewall on the way; an RTCP receiver report every 5 s (RFC 3550 6.4.2, LSR / DLSR from the camera's sender
    reports) and OPTIONS within the session timeout keep the stream. TCP interleaved instead for devices at a site, a
    461 answer, no free pair or `PreferUdp` false. UDP that plays but brings no packet in 3 s
    (`RtspStreamProbe.UdpStartTime`, on the probe's `TimeProvider`) is dropped on the way: TEARDOWN, the same request
    over TCP, and `IDeviceStreamProbe.UdpBlocked` true (a 461 or a site is not blocked).
  - Counters (`RtpStreamCounter`): loss from sequence gaps once 50 newer packets arrived (16-bit wrap; reorders inside
    the window and duplicates are not lost; a far backward jump confirmed by the next packet restarts the sequence),
    frames by RTP marker, expected frames from RTP time and the frame rate, late frames (the marker more than 2 frame
    times after the quickest frame's transit), payload bytes, RFC 3550 6.4.1 jitter.
  - The probe ends (`Completion` faulted with `DeviceStreamException`) when the camera closes the session or nothing
    arrives for 30 s; dispose sends TEARDOWN and frees the pair. A group user's call opens probes only for devices of
    the group (`KeyNotFoundException` otherwise, `PluginCallGuard`).
- gRPC: each `LiveViewFrame` carries one Annex B access unit, the keyframe flag, parameter sets
  on keyframes, RTP timestamp, resolution and camera. Client channel max message size 32 MB. A user with a device group
  watches only devices of the group; the video ends ("1 device is no longer available.") when the device leaves it
  (`DeviceViewWatcher`, [device-scope.md](device-scope.md)).
- Client: FFmpeg decode on a background thread (low delay, slice threads), swscale to BGRA into a
  new `WriteableBitmap` (through `BgraConversion`: swscale writes into FFmpeg's own row-padded picture, then the rows are
  copied; straight into the bitmap it wrote 8 to 32 bytes past the buffer for widths not a multiple of 16, test
  `SwscaleBoundsTests`), swapped on the UI thread, old bitmaps disposed. Reconnect with backoff
  1, 2, 4, 8, 10 s; permanent errors (unknown device, credentials, no common codec, certificate
  changed) stop with the server's message. If FFmpeg cannot be loaded the picture area shows
  "Video decoder not available on this platform" (no fallback).
- **GPU decoding** (`LiveView/GpuDecoding.cs`, `FfmpegVideoDecoder`): client setting "Decode video on the graphics card"
  (`ClientSettings.GpuDecoding`, default on, Settings > **Client**, saved at once, every user). At every start (and
  when the setting is turned on) `GpuDecodingService` runs the client itself with `--check-gpu-decoding` in a **separate
  process** (20 s timeout; a driver crash or hang only ends that process): it opens the first device type that works
  (Windows d3d11va, dxva2; macOS videotoolbox; Linux vaapi, cuda; `OADM_GPU_DEVICES` overrides the list) and decodes the
  embedded H.264 and H.265 clips on it; output `device=`, `h264=ok|failed: ...`, `h265=...`, `problem=`. Only after it
  passed does the client open the device in its own process (`HwDevice`, one per device type, shared by all streams).
  `FfmpegVideoDecoderFactory` creates GPU decoders for the codecs that passed, else processor decoders with the warning
  text. A GPU decoder that fails on a stream (no hardware format, a copy that fails, no picture after two keyframes)
  reopens on the processor and says why; decoding never throws. Linux: the bundled FFmpeg has VA-API built in with the
  driver path of its build machine, so `HwDevice.PrepareEnvironment` (start of `Main`) sets `LIBVA_DRIVERS_PATH` to the
  system's `dri` folders through libc `setenv` unless the user set it. The details say "GPU" or "Processor", and an
  orange warning icon top right over the picture (tooltip: what to do, `GpuDecodingTexts`: Linux "Install the video driver of the graphics card (Intel:
  intel-media-va-driver, AMD: mesa-va-drivers, NVIDIA: the NVIDIA driver) or turn off ...", no device, the driver stopped
  the test, a codec the card cannot decode) whenever the setting is on and the stream runs on the processor; off = no
  warning. A finished test or a changed setting opens a new stream with the right decoder (silent swap). Verified on Windows
  (NVIDIA: d3d11va, dxva2, cuda decode both codecs). Tests: `GpuDecodingTests` (every device type decodes every picture or
  says why, the separate process and a crashing one), `LiveVideoViewModelTests`.
- Tests: `LiveVideoViewModelTests` (resolution choice, Full, the silent swap, a failing or slow new stream, hidden and
  shown, channels, reconnect and errors, details, controls and options, fullscreen), `LiveVideoViewHeadlessTests`
  (hover parts, controls, ring, error, warning, fullscreen and Esc), `LiveViewViewModelTests` (the side panel).
- Fake mode replays recorded video of the development camera (H.265 or H.264) and reports two view areas for the
  P3265-V and four sensors plus quad view for the P3727-PLE.
- Measured on the development camera at 640x360, 10 fps: first frame 0.3-0.6 s after the request through the
  server; about 160-250 kbit/s for both H.264 and H.265 (scene dependent, keyframe every second).
