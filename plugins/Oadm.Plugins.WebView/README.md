# Web View (`oadm.web-view`)

Core plugin (+ `.Client`), rail page **Web View** (group Utilities, icon `globe`), on by default, switchable. Opens the web
interface of the managed devices in browser tabs inside OADM, logged in with the login OADM stores for the device.

## Decisions

| Topic | Decision |
|---|---|
| Login | Through the server: the browser talks to a relay in the client, the server sends every request to the device with the stored login. Passwords never reach the client; the device's pinned certificate is checked by the server; the client does not need to reach the device. |
| Certificates | No "allow untrusted" switch: the server connects with the device's stored scheme and pinned certificate like every other request. A device with Certificate changed shows that text. |
| Tree | Left card, below the page select and the search: devices grouped by tag like the grid's Group by tag ("No tag" last; a device with several tags under each; no counts), devices by IPv4 address with the shared `ui:DeviceIcon` (category icon, amber or red by status, the status message on hover) and the address only (no model). Search over address, model, MAC, host name and tags. |
| Tabs | One tab per device: a click on an open device brings its tab to the front. One row above the page: Close all tabs, Reload, Back (the browser's own history), then the tabs (the height of a select field), scrolling sideways when they do not fit. The page fills the card from edge to edge. |
| Page select | Above the search: Web interface (`/`), Apps (`/camera/index.html#/apps`), Overlays (`/camera/index.html#/video/overlays`), Privacy masks (`/camera/index.html#/video/privacymasks`), Recordings (`/camera/index.html#/recordings`), Security (`/camera/index.html#/system/security`), Image Health Analytics (`/local/AXISImageHealthAnalytics/index.html`), Object Analytics (`/local/objectanalytics/index.html#/`), License Plate Verifier (`/local/fflprapp/setup.html#/home`), Custom (a path field below the select, opened with Enter or when the field is left; error below the field when it does not start with "/" or leaves the device). The select shows the active tab's page; changing it opens the page in the active tab; new tabs use the page chosen last. |
| Roles | Operators and administrators (operators may already open a device's web interface from the grid). Every open is in the audit log ("Opened web interface", the device). |
| Refused | Certificate changed, Credentials required and Password not set are refused with the shared status text (the tab shows it); the device's own login prompt never appears. |

## How it works

- **Server** (host, `Oadm.Server/DeviceWeb/DeviceWebGrpcService`, gRPC `DeviceWebService`): `Open` checks the status and
  audits; `Exchange` is one bidirectional call per HTTP request or WebSocket. Requests go through the device's cached
  `VapixClient` (stored login, pinned certificate; paths must start with one "/" and stay on the device), answers are
  streamed in 64 KB chunks (no size limit: video, downloads), 10 minutes for the answer's headers. Hop-by-hop headers,
  `Authorization` and `WWW-Authenticate` are never relayed; `Origin` / `Referer` of the relay become the device's
  address; a `Location` on the device's address becomes a path. Login like a browser (`VapixClient.SendRelayAsync`): the
  login goes with the first try (Basic over HTTPS, Digest with the device's last challenge and the next nonce count),
  because `login.cgi` hands out a session cookie with every 401 and accepts only the login together with that cookie; a
  refused try is sent once more with the device's new cookies (which the page gets too). Request bodies are buffered so
  they can be sent again (in memory up to 16 MB, beyond that in `<datafolder>/web-relay/`, deleted on close; at most
  4 GB). A device that does not answer gives a small error page
  (502) with the shared device text. WebSockets: `VapixClient.ConnectRelayWebSocketAsync` (the page brings its own session
  token; a 401 of the upgrade is answered with the stored login, `WebSocketLogin`: Digest, Basic only over HTTPS), messages both ways, close handshake passed through.
- **Client** (host, `Oadm.Client/DeviceWeb/`): `DeviceWebRelay` is a small HTTP/1.1 server, one per tab, each on its own
  loopback address `127.1.x.y:<random port>`: browsers keep cookies per host, not per port, and every Axis device uses
  the same login cookie names, so tabs on one host overwrote each other's login (only the first tab worked). macOS answers
  only on 127.0.0.1, which is the fallback there; the session cookie carries the port in its name
  (`oadm_relay_<port>`) on every system. Only `StartAddress` (one-time token) hands out the session
  cookie (HttpOnly, SameSite=Strict); requests without it get 403 (page title `DeviceWebPages.SessionLostTitle`: the tab
  opens the page again through the start address), a Host header other than the relay's own gets 400.
  Request bodies with a length or chunked, responses with their length or chunked, keep-alive, WebSocket upgrade
  (`WebSocket.CreateFromStream`). Device cookies lose `Domain`, `Secure` and `SameSite=None` (the relay is plain HTTP on
  loopback; the link to the device is the server's). `DeviceWebAccess` is the page's `ICorePluginClientContext.DeviceWeb`
  and stops its relays when the page closes.
- **Page** (`Oadm.Plugins.WebView.Client`): `WebViewPageViewModel` (tree rows in one reset, tabs, presets, Custom with
  `ValidatingViewModel`), `WebViewPageView` (code-behind owns one `NativeWebView` per tab, kept alive while hidden; links
  that open a new window open in the same tab). Back follows the browser's history: the web interfaces are single-page
  apps whose steps load no page, and the control passes on no history event, so after every page load a small script in
  the page reports pushState, replaceState, back / forward and hash changes through the control's message channel
  (`invokeCSharpAction`), and Back is checked then. No limit of our own: the browser keeps about 50 entries per tab.
- **Browser runtime** (`WebRuntime.Check`, `WebViewAdapterInfo`): WebView2 on Windows, WKWebView on macOS, WPE WebKit or
  WebKitGTK on Linux. Missing: the page says what to install ("The Microsoft Edge WebView2 Runtime is not installed ...",
  the apt / dnf / pacman package of WebKitGTK 4.1). The `oadm-client` .deb recommends `libwebkit2gtk-4.1-0`. WebView2
  keeps its data in `%LOCALAPPDATA%\Oadm\WebView2`: its default, a folder next to the exe, is not writable in Program
  Files (access denied in the installed client).
- **Fake mode** (`FakeOadmApi.DeviceWeb.cs`): every device answers with a small page naming it and the path; WebSockets
  echo.

## Device groups

SDK 0.4.0 (`minSdkVersion`). The plugin has no page methods. The tree shows the user's devices (the host's device list),
and `DeviceWebService` takes every device from the user's view: when a device leaves the view, its relay requests end
with "1 device is no longer available." and its tab closes like the tab of a removed device.

## Tests

- `tests/Oadm.Server.Tests/DeviceWebTests`: Open (refusals, audit), a request with body and headers through the fake
  network (stored login, Origin / Referer, Location, cookies), unreachable device, paths that leave the device, changed
  certificate. Read-only hardware tests (camera 10.0.0.202 of `dev-cameras.yaml`): `Hardware/DeviceWebHardwareTests` (the
  start page, `param.cgi`, the event WebSocket with and without a session token) and `DeviceWebLoginHardwareTests` (a
  SOAP read, `login.cgi` with its session cookie). `tests/Oadm.Core.Tests/Vapix/WebSocketLoginTests` (Digest against the
  RFC example, SHA-256, Basic only over HTTPS, cookie merge).
- `tests/Oadm.Client.Tests/DeviceWebRelayTests`: cookie and token, 403 / 400 for others, bodies with a length and chunked,
  WebSocket both ways, dispose, refusals.
- `tests/Oadm.Plugins.WebView.Tests`: tree, tabs, Close all, Back, presets and Custom, refusals, removed devices, missing
  runtime, 5,000 devices, the shared device icon, the device-group canary (`CanaryTests`); headless `web-view-page.png`, `web-view-custom-error.png`, `web-view-runtime-missing.png`
  (the native browser needs a real window: a stand-in shows where the page goes).

## Permissions

`oadm.web-view:show` (the page) and `:open` "Open devices" (`DeviceWebService.Open` and `Exchange`, full device control:
the relay logs in with the stored login). Both are off for the built-in Operator by default (the plugin declares "show"
to change its default). Without "open" a click on a device does nothing and the page says "Your role does not allow
opening devices."
