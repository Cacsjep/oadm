# MyAxis sign-in

Status: **built, not verified with a real MyAxis account** (server side; the client follows).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Each OADM user can sign in to their own MyAxis account. With it, Upgrade firmware downloads AXIS OS from axis.com
(plugin README "Firmware online"). Decisions of 2026-11-02, asked one by one. A clean-room build: the version parked on
2026-10-29 (`git stash` "firmware-online-beta") is not reused.

## Rules that never break

1. **OADM never sees the MyAxis password or one-time code.** The user types them on Axis's own page in OADM's sign-in
   window. OADM keeps only the session cookie `axis_connect_session_sid` of www.axis.com. That one cookie is enough for
   the firmware downloads: the MIT tool rs4a (`crates/authentication`, `crates/firmware-inventory`) sends only it.
2. **Per OADM user.** Each user has their own sign-in; no user sees, uses or ends another user's sign-in, an
   Administrator included. A download always uses the session of the user who started the task.
3. **Kept on the server, encrypted.** The session is a secret like a device password (AES-256-GCM with the master key,
   [security.md](security.md)): never logged, never in a gRPC answer, never in a backup. It lasts across client and
   server restarts until the user signs out, Axis ends it or the user is deleted.
4. **Sent only to axis.com.** Only over HTTPS to `www.axis.com`, only as the one `Cookie` header of a firmware download
   below `/ftp/pub_soft/MPQT/` or `/ftp/pub_soft/PACS/` and of the sign-in check. The HTTP handler keeps no cookies
   (`UseCookies = false`) and follows no redirect.
5. **Plugins never get the session.** They ask the host to download a file (see "For plugins").
6. **Every sign-in and sign-out is in the audit log** (user, time; never the session).

## In the client

- **Button**: an icon-only `ui:ToolbarButton` (`TitleMyAxis`) in the title bar, directly left of Help, styled like Help.
  Every logged-in user sees it; hidden while the server has no MyAxis sign-in (an older server). Tooltip "Sign in to MyAxis"; signed in: "Signed in to MyAxis"; expired: "Sign in to
  MyAxis again" with a warning dot.
- **Signed in**: a click opens a small flyout: "Signed in to MyAxis since 2 Nov 2026" and **Sign out**.
- **Sign-in window**: `MyAxis/MyAxisSignInWindow`, an OADM window (about 520 x 720) with the embedded browser
  (`NativeWebView` of Avalonia.Controls.WebView, the package of Help and Web View), never the pop-up `NativeWebDialog`
  (on Linux it has no way to the cookies). Start page
  `https://www.axis.com/my-axis/login?axis_destination=https%3A%2F%2Fwww.axis.com%2Fftp%2Fpub_soft%2F` (a light page to
  return to). Links that open a new window open in the same one; no developer tools.
- After each page that finished loading on `www.axis.com` outside `/my-axis/login` (also with a language prefix such as
  `/en-us/my-axis/login`; pages of the login hosts such as `auth.axis.com` never count) the window reads the cookies of `https://www.axis.com`. With `axis_connect_session_sid` it
  sends `name=value` to the server (`MyAxisService.SignIn`) and shows "Checking the sign-in" until the server answers;
  accepted: the window closes. The cookie can come a moment before axis.com finished the sign-in: a refusal (or no
  cookie yet) is tried again up to 6 times, 500 ms apart, before "MyAxis did not accept the sign-in. Try again." and the
  login page again. The client learns of an expired sign-in when it connects and when the button is clicked.
- The browser keeps nothing on disk: WebView2 InPrivate, WKWebView a non-persistent store, WebKitGTK ephemeral, WPE a
  temporary folder deleted when the window closes (the package has no ephemeral option for WPE).
- No embedded browser (the Help texts `EmbeddedBrowserTexts`) or no way to the cookies: the button's window says
  "Sign-in is not available here." and Upgrade firmware works with chosen files only.

## Reading the cookie

- **Windows, macOS, Linux with WPE 2.0**: `NativeWebView.TryGetCookieManager().GetCookiesAsync()` of the package.
- **Linux with WebKitGTK** (all Ubuntu releases, Debian 12): the package has no cookie manager there (12.1.1 and main as of
  2026-11-02; its docs say it has). OADM's own glue `MyAxis/WebKitGtkCookies` (client, Linux only, used only when the
  package returns none):
  - the browser object from the public `IGtkWebViewPlatformHandle.WebKitWebView` (`TryGetPlatformHandle()`);
  - `webkit_web_view_get_context`, `webkit_web_context_get_cookie_manager`, `webkit_cookie_manager_get_cookies` for
    `https://www.axis.com` and its `_finish`, called on the GLib main context (`g_main_context_invoke`), the callback kept
    alive until it ran;
  - the `GList` of libsoup 3 cookies read with `soup_cookie_get_name`, `_get_value`, `_get_domain`, then freed;
  - only reads, only in the sign-in window; any error counts as "no cookie".
  - An issue at AvaloniaUI/Avalonia.Controls.WebView asks for WebKitGTK support; the glue goes once a release has it.

## On the server

- **Sign-in check**: before keeping a session, the server asks axis.com for the headers (HEAD) of one firmware file (the
  `latest/` .bin of the first product in the MPQT listing; both listings are read with the session too) and follows no
  redirect. 2xx: kept. Anything else: refused, nothing kept; no answer at all: UNAVAILABLE. The client may try again a
  moment later (the cookie can come before axis.com finished the sign-in); the server checks once per call.
- **Not accepted** (sign-in check and downloads): 401, 403, or a 3xx whose `Location` is a login page (a path with
  `/login`, or a host `login.*`, `*.login.*` such as `eu.login.connect.axis.com`, or `auth.*`). Another 3xx is not
  followed either: a download fails with "axis.com answered 302" and the session stays.
- **Code**: `Oadm.Core.MyAxis` (`MyAxisCookies` the validation, `MyAxisSessionStore`, `AxisComClient` the HTTP calls and
  the URL allowlist `AxisDownloadUrls`, `AxisDownloadService` the cache); `Oadm.Server.MyAxis` (`MyAxisGrpcService`,
  `AxisComHandler` the one `SocketsHttpHandler`, `AxisDownloadSweep` at the start). Tests give `AxisComClient` a fake
  handler (`tests/Shared/FakeAxisCom.cs`); no unit test reaches the network.
- **Storage**: one row per user (`MyAxisSessions`: user id, the encrypted session with the user id as associated data,
  signed in at, last accepted at, expired); EF migration `MyAxisSessions`. Deleted on Sign out, with the user (foreign
  key, cascade), and when axis.com refuses it (the row stays with expired set and no session, so the button shows it).
  Removed from every backup copy.
- **Expired**: a firmware download answered with a 3xx to a login page, 401 or 403 deletes the user's session; the task
  fails with "Your MyAxis sign-in has expired. Sign in to MyAxis again. Nothing was changed." and the button shows it.
  A sign-in lasts about 4 weeks: `axis_connect_session_sid` expires about 27 days after the sign-in.

## For plugins

Server SDK, default interface members (`plugins.md`): `ctx.AxisDownloads` of a task.

- `HasSession`: whether the user who started the task is signed in.
- `DownloadAsync(uri, sha256, size, progress, ct)`: the file, from the cache or downloaded with that user's session.
  Refused for any URL outside the two download folders above. One download per URL at a time: tasks that need the same
  file wait for it and see the same progress.
- **Cache**: `<datafolder>/axis-downloads/`, kept 7 days after the last use and at most 5 GB in all (the oldest go
  first, never a file in use). Its SHA-256 is checked before every use; a mismatch deletes it. Only a signed-in user's task
  uses it.
- **Size and checksum**: the download stops when it grows 2 % + 1 MB over the listed size; a different size or SHA-256
  fails with "The download did not match the checksum on axis.com. Nothing was changed." and nothing is kept.
- **No connection**: "The firmware could not be downloaded from axis.com (<short reason>). Nothing was changed." (no
  connection to axis.com, axis.com was not found, no answer in time, the connection was lost, axis.com answered 404).
  axis.com has 60 s for the headers and between two reads of the body.
- **Texts**: `AxisDownloadTexts` of the SDK (`Oadm.Sdk.Plugins`), the same for the host and `FakeAxisDownloads`.
- **Cache layout**: `axis-downloads/<sha256>/<file name of the URL>`, partial downloads in `axis-downloads/tmp/`; the last
  use is the file's modification time. A sweep at the server's start and after every download.
- A download that another user's task started and that fails because that user's sign-in expired is started again with
  the waiting user's own session. A task the system started gets no `AxisDownloads` (null).
- Testing: `FakeAxisDownloads` of `Oadm.Sdk.Testing` (files by URL, `Expire()`, `Failure`), `FakeTaskContext.AxisDownloads`.

Client SDK: `ITaskDialogContext.MyAxis` with `IsSignedIn` and `SignInAsync()` (opens the sign-in window), so a dialog can
offer Sign in to MyAxis.

## gRPC

`MyAxisService` ([grpc.md](grpc.md)), every logged-in user, always the caller's own session:

- `Get`: signed in or not, since when, expired.
- `SignIn` (session): the check above, then kept. `INVALID_ARGUMENT` "The MyAxis sign-in could not be read. Try again."
  for anything but one pair `axis_connect_session_sid=<visible ASCII without ; , " \ or spaces>` (not empty), at most
  4,096 characters; sent exactly so as the one `Cookie` header.
- `SignOut`: deletes it.

## Not verified

No MyAxis account was used for the build. Known from a working browser extension and rs4a: axis.com answers HEAD on a
.bin, and a sign-in lasts about 4 weeks. To check with a real account: that the login returns to `axis_destination`,
the listings' format (the server reads the `href`s of the HTML listing), and the WebKitGTK glue on Ubuntu 24.04.

## Not part of it

- Storing the MyAxis password or signing in without the user.
- Any other axis.com service (licenses, warranty, the device registry).
- Sharing one MyAxis sign-in between users.

## Tests

The session rules (per user, encrypted with associated data, never in an answer, a backup or the log), the URL allowlist,
no redirect, expiry on 3xx/401/403, the cache (reuse, 7 days and 5 GB on a `FakeTimeProvider`, checksum before use, a file
in use kept), one download per URL with joined progress, size and checksum refusals, the sign-in window's page rule
(which pages read cookies) with a fake cookie source, the title bar states headless with screenshots. The WebKitGTK glue
and a real sign-in are manual tests, written down in the client README.
