# Help (offline docs)

Status: built 2026-10-27 (docs build, installers, Help window, docs server); the release workflow change runs first in CI with the next release.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The client ships the user docs of oadm.app and shows them in a window of its own, without internet and always in the
installed version.

## Rules that never break

1. **Always the local copy.** Help never loads oadm.app. Pages that exist only online (developer docs, SDK reference,
   Get started, Activate, the intro video) are links to oadm.app that open in the system browser.
2. **Loopback only.** The docs server listens on `127.0.0.1` only, serves only files below the docs folder (a path with
   `..`, an absolute path or a link out of the folder is refused with 404) and only GET and HEAD.
3. **No secrets, no login.** The docs are public; the server needs no token and sees nothing of the OADM session.
4. **Client only.** The OADM Site Node app has no Help.

## Docs build

- `OADM_DOCS_OFFLINE=1` switches the website to the offline docs; `website/src/offline.mjs` holds every difference and
  changes nothing without the flag (oadm.app stays as it is):
  - `src/content.config.ts`: Astro's glob loader with Starlight's pattern and the exclusions `dev/**`, `api/**`,
    `start/**` (Get started: install and first start, read before OADM runs), `index.mdx` (the splash page with the intro video) and `activate.mdx` instead of `docsLoader()`.
  - `astro.config.mjs` `offlineSidebar`: the social links (top right), the "Edit page" links and "Get started" are left out, and the group "Developers" (with the SDK reference) becomes one link "Developers" to
    `https://oadm.app/dev/plugin-sdk/`.
  - `offlineRedirects`: `/` is a plain redirect page (meta refresh, no script) to the first page of "Devices",
    `/devices/add/`.
  - The integration `oadm-offline-docs` (`astro:build:done`) removes `intro.mp4`, `intro-poster.jpg` and `og-image.png`
    from the output (public/ stays as it is) and turns every link of the built pages to `dev/`, `api/` or `activate/`
    into a link to oadm.app (Markdown, components and the sidebar alike; Astro 7's Markdown processor runs no rehype
    plugins without `@astrojs/markdown-remark`).
  - Pagefind indexes only the built pages, so search finds no left-out page.
- `npm run build:offline` (`website/scripts/build-offline.mjs`: sets the flag, writes the changelog page, builds into
  `website/dist-offline/`, git-ignored) and then checks the output: `index.html` exists, no `dev/`, `api/` or `activate/`
  page, no `intro.mp4`, no Pagefind fragment of a left-out page, every `href` and `src` to a local path has its file.
  `node scripts/build-offline.mjs --check` checks an existing output only. `pages.yml` runs it after the site build
  (not published).
- Measured 2026-10-27 (4.5.2 docs): 51 pages (50 in search), 323 files, 9.7 MB, about 5 MB as tar.gz.
- `release.yml` job `docs` builds them once per release (Node 22 like `pages.yml`) as the artifact `docs-offline`; each
  installer job downloads it to `artifacts/docs-offline/`, and `manage publish` copies it into the client as `docs/`
  next to the client executable (MSI and .deb: the client folder, both "Server and client" and "Client only"; .pkg:
  `OADM.app/Contents/Resources/docs`, no code in `Contents/MacOS`, so the client also looks there). The server, its
  portable ZIP and the OADM Site Node carry no docs. Checks: the published client has `docs/index.html` and the server
  no `docs/`; the MSI has one `docs` folder; the .deb's client has `/opt/oadm/client/docs/index.html`, the server none;
  the .pkg has `Contents/Resources/docs/index.html`; the smoke tests check the installed `docs\index.html` (MSI both
  ways, .deb both ways).
- Developer builds: `manage build docs-offline` (needs Node.js; `npm ci` when `website/node_modules` is missing) builds
  them into `artifacts/docs-offline/` and copies them next to every built client (`src/Oadm.Client/bin/<configuration>/
  <tfm>/docs`) for `manage run client`; `manage publish` and `manage package` take them from there and warn when they
  are missing. A developer build without `docs/` shows "Help is not part of this build." in the window.

## Help in the client

- **Button**: an icon-only `ui:ToolbarButton` (`TitleHelp`, icon `help`: a question mark in a circle, tooltip "Help") in the
  title bar, directly left of the user and Log out (top right, before the window buttons); styled like Log out. Not in
  the rail. Command `MainWindowViewModel.OpenHelpCommand`.
- **Window**: `Shell/HelpWindow` with `Shell/HelpWindowViewModel`, a window of its own (not modal), 1200 x 800 at the
  start, resizable, an empty title bar like the plugin windows (the docs name their pages); title "Help · OADM"
  (`HelpDocs.WindowTitle`). One at a time: `MainWindowViewModel.Help` holds the open one, Help again raises
  `HelpWindowRequested` with the same view model and the main window brings its window to the front. It closes with the
  main window and on logout (`Stop` raises `CloseHelpRequested`); a closed window calls `HelpClosed`, which disposes the
  view model and so stops the docs server. The view models come from a `Func<HelpWindowViewModel>` registered in
  `ServiceRegistration` (a new one per opened window).
- **Docs folder** (`Help/HelpDocs.FindRoot`): the first folder with an `index.html` of: the environment variable
  `OADM_DOCS_DIR` (development: e.g. `OADM_DOCS_DIR=G:\odam\website\dist-offline`), `docs` next to the client executable
  (`AppContext.BaseDirectory`), and on macOS `OADM.app/Contents/Resources/docs` (`ClientPluginLoader.AppBundleResources`;
  codesign keeps non-code files out of `Contents/MacOS`).
- **Content**: the embedded browser of the OS (`Avalonia.Controls.WebView` `NativeWebView`: WebView2, WKWebView,
  WebKitGTK, as the Web View plugin, the same WebView2 user data folder `LocalApplicationData/Oadm/WebView2` and options)
  on the docs server's start page, created by the window's code-behind (`HelpBrowser`; tests replace
  `HelpWindow.BrowserFactory`). Links that leave the docs server (another host or port, `mailto:`) are cancelled and open
  in the system browser (`HelpWindowViewModel.LeavesDocs`, `IUrlLauncher`); links that ask for a new window open in the
  same one.
- **No docs**: "Help is not part of this build." (`HelpDocs.NotInBuild`, one secondary line); the browser is not checked.
- **No embedded browser** (Linux without WebKitGTK, Windows without the WebView2 runtime; `Help/EmbeddedBrowser.Check`,
  the same check as the Web View page): the window says what to install with the same text as the Web View page
  (`Oadm.Sdk.Client.EmbeddedBrowserTexts`, shared by both), in an error `ui:StatusChip`; no system browser fallback.
- **Appearance**: Starlight follows the OS light or dark setting; the client theme is not passed on.

## Docs server

- `Oadm.Client/Help/DocsServer`: a small static HTTP/1.1 server next to `DeviceWebRelay` (same sanctioned listener: a
  `SuppressMessage` and an entry in `RoutingRulesTests.AllowedBannedApiUsers`; it reuses the relay's `HttpHeadReader`, heads
  at most 64 KB), `127.0.0.1` on a free port, started by `HelpWindowViewModel.Start` when the window opens and stopped
  (`Dispose`, the port is free again) when it closes.
- Serves the files of the docs folder with their media type (html, css, js, mjs, json, svg, png, jpg, webp, avif, ico,
  woff, woff2, txt, xml, wasm; the Pagefind files `pf_fragment`, `pf_index`, `pf_meta` and unknown types as
  `application/octet-stream`), a folder path with a slash to its `index.html` (without the slash a 301 to it, so the
  page's relative links work), a missing file or a refused path to `404.html` with status 404 (a plain 404 without one).
- Refused: `..` and `.` segments, empty segments, `%2f`, `%5c`, `%00`, backslashes, `:` (drive letters, streams), a `%`
  left after decoding (double encoding), and links (symbolic links, junctions) that lead out of the folder (each link on
  the way is resolved and must stay below the folder). Only GET and HEAD (others 405 with `Allow: GET, HEAD`), a request
  with a body or another Host is 400.
- Responses carry `Content-Length`, `Cache-Control: no-cache`, `X-Content-Type-Options: nosniff` and no cookies.
  Keep-alive is bounded: at most 1,000 requests per connection, 30 s idle (`TimeProvider`), 32 connections at a time.

## Tests

- `HelpDocsServerTests` (raw requests over TCP): media types, folder to index and the redirect, 404 page and plain 404,
  traversal variants (`..`, `%2e%2e`, `%2f`, `%5c`, backslashes, double encoding, drive letters, `::$DATA`) refused,
  links out of the folder (a junction where symbolic links need a privilege), only GET/HEAD, HEAD without body,
  keep-alive, idle timeout with a fake clock, loopback only, stopping frees the port, the docs folder order.
- `HelpWindowTests`: one at a time, closes with the main window and on logout, the missing-docs and missing-browser
  texts, links that leave the docs, the Help button left of the user (headless, no real browser; screenshots
  `client-help-titlebar-dark|light.png`, `client-help-no-docs-dark|light.png`).
- Website: the offline build has no `dev/` or `api/` pages, no `intro.mp4`, and its Pagefind index names no left-out page
  (a check in the pages workflow).
