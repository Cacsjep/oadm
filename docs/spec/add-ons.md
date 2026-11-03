# Add-on plugins (vision plugins)

Status: **built** (2026-11-03): installer, registry, client, packaging and notices; the first add-on is Text recognition.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Plugins of OADM that are not in its installers: an administrator installs them on demand on the Plugins page, the server
downloads them from the OADM release on GitHub, and every client gets their client part from the server. The first ones
are the vision plugins, which analyse the cameras' pictures on the server (text recognition, `docs/spec/ocr.md`). OADM
itself stays free of models and inference libraries; no extra installer.

**Name for users: "optional plugins"** (decision 2026-11-03): the card "Optional plugins" on the Plugins page,
audit "Installed optional plugin ...", log lines the Logs page shows, docs and every UI text. "Add-on" stays the name in
code, files, the release ZIPs (`oadm-addon-...`) and this spec; it is never shown to users.

**Trust** (decision 2026-11-03, `docs/spec/security.md` "Optional plugins: trust"): optional plugins are not signed. The
server trusts them through the OADM release on GitHub of its own version and that release's `SHA256SUMS.txt`; a client
loads the client part its server hands it. The user must trust their server and the release.

## Rules that never break

1. **Never in the OADM installers.** Add-on plugin projects (`<OadmAddOn>true</OadmAddOn>`) are left out of `manage
   publish all`, the MSI, .deb, .pkg and portable ZIPs. OADM starts, updates and runs the same without them.
2. **Only from OADM's own release, checked.** The server downloads an add-on only from the GitHub release of its own
   version (`https://github.com/Cacsjep/oadm/releases/download/v<version>/`, the hosts and limits of self update:
   `GitHubReleaseFeed.AllowedHosts`, HTTPS, size limit, timeouts) and installs it only when its SHA-256 matches the
   release's `SHA256SUMS.txt`. No other source, no upload of a file: add-ons run with the server's rights. Third-party
   plugins keep the manual install (`website/.../dev/plugin-sdk.md` "Ship it").
3. **Same version as OADM.** The server always installs the add-on of its own version, compared as exact version strings
   (the folder of `4.11.0-rc.1` is not the add-on of `4.11.0`). After a server update it fetches the new version of every
   installed add-on by itself before it loads them (an add-on whose download fails stays unloaded with "Could not
   download <name> V<version>. Install it again.", OADM runs).
4. **Administrators only.** Install and Remove need the Plugins permission of turning plugins on and off; both are in
   the audit log ("Installed optional plugin Text recognition V4.11.0", "Removed optional plugin ...", a failed install
   "Could not install optional plugin" with the reason). The install is audited before its last message, also when the
   caller has gone meanwhile.
5. **Install without a restart, remove at the next start.** An installed add-on loads at once
   (`PluginLoader.LoadPackage` into its own `PluginLoadContext`). A first install starts off; installing again (after a
   failed download, or a removed one before the restart) keeps the administrator's stored choice. Turning it on is the
   existing Turn on path. Every part that reads the plugin registry (permissions, event and action catalogue, pages)
   follows an added package as it follows Turn on and off. Remove turns it off at once and keeps it loaded; until the
   restart it cannot be turned on (the plugin list refuses it with the Removed text) and Install keeps it without a
   download; its folder is deleted at the next server start (the row says "Removed. The files go at the next restart of
   the server."). Never an unload at run time: a collectible load context unloads only when nothing holds it, which is
   not guaranteed.
6. **Clients get the client part from the server**, never from the internet: one call per add-on, the files checked
   against the SHA-256 and the whole part against the digest the server sends, kept per server (its pinned certificate
   fingerprint) and version in the client's own `<datafolder>/plugin-cache/` (never the server's `addons/`: a server and a
   client of the same user share the data folder), loaded into its own load context while connected (the pages follow as
   on Turn on), else at the next connect. A cached copy is used only while it matches the digest the server lists. A
   removed add-on's client files, and the versions an add-on no longer uses, go at the client's next start. A client
   never installs anything on its computer and needs no rights for it.
7. **GPU when there is one, the CPU always works, no own service.** Vision plugins run inside the server's plugin; their
   native libraries (ONNX Runtime with its GPU providers) and model files are in the plugin's own folder for the server's
   platform and load through the plugin's load context (`PluginLoadContext.LoadUnmanagedDll`). A missing or failing GPU
   falls back to the CPU with one log line, never an error. Nothing else is downloaded at run time.

## Release files

- Per add-on and platform of the server: `oadm-addon-<id>-<version>-<rid>.zip` (`win-x64`, `linux-x64`, `linux-arm64`,
  `osx-x64`, listed once in `packaging/addons/addon-rids.txt`; a test keeps it equal to `AddOnCatalog.RuntimeIds`): the
  plugin folder as the server loads it (`plugin.json`, server part, client part, native libraries of that platform,
  models, `THIRD-PARTY-NOTICES.txt`, `LICENSE.txt` and `oadm-version.txt`). Built in the same `release.yml` run, attached
  to the same release, listed in `SHA256SUMS.txt`. The notices list the add-on's own NuGet packages, the packages it
  ships outside its `*.deps.json` (`OadmNoticePackage` items in its csproj, e.g. DirectML with its full license) and its
  models' licenses (`tools/Oadm.Notices --addon`, `docs/spec/packaging.md` "Add-on ZIPs").
- `oadm-version.txt` (`AddOnFolders.VersionFileName`, written by `manage package addons`): the release the add-on belongs
  to. The server records a folder put in by hand with it (a folder without it is downloaded again) and refuses a
  download whose file names another release.
- The client part is platform-neutral (.NET only); the server hands it to clients of any system: `plugin.json`, the
  top-level `*.Client.dll`, the managed `*.Server.dll` it references (its contracts and texts; the client loads it next to
  the client dll in the add-on's load context) with their `.pdb`, and everything under `client/`
  (`AddOnFolders.ClientPartPatterns`, `AddOnLayout.ClientFolder`). Never the server part's dependencies, native libraries
  or models. A client part with native libraries is not allowed for add-ons.
- The list of add-ons a version offers is in the server (`AddOnCatalog`: id, name, one line what it adds), not read from
  GitHub, so the Plugins page shows them without internet.

## Server

- Folder: `<datafolder>/addons/<id>/`, writable by the service. Folders starting with a dot are never add-ons
  (`.download/`, `.new-*` and `.old-*` while one is swapped) and are emptied at every start; folders of ids this version
  does not offer are deleted at the start (log line) and dropped from the list. The list of installed add-ons is the
  server setting `AddOns.Installed` (JSON array of {id, version, removed}, `SettingsAddOnListStore`); backups hold the
  list, not the files: after a restore the server downloads them again, as after an update. A folder put in by hand is
  taken into the list at the next start with the release of its `oadm-version.txt`, but not when the service had to fix
  the data folder's permissions at this start (until then others could write it; log line). As a service each
  `addons/<id>` is checked like every plugin folder (`FolderGuard.CheckPluginFoldersAsync`, `docs/spec/security.md`
  "Service: firewall and folders") before it loads. The loader reads it after the plugin folder next to the exe; an id in
  both: the bundled one wins and the add-on is not loaded (log line).
- Start (`OadmServerHost`): `AddOnInstaller.LoadInstalledAsync` right after `PluginLoader.LoadFromRoots` (deletes the
  folders of removed add-ons, loads every add-on of this version; no event, the start reads the registry);
  `FetchAfterUpdateAsync` in the background once the start is over (after `PluginActivation.LoadAsync`, the licenses and
  `PluginRegistry.MarkStarted`): an add-on of another version or without its folder (after an update or a restore) is
  downloaded, loaded and followed like one installed now, with its stored choice; it counts as loaded right after the
  load (a failing store or follow is logged, the row says Installed); OADM runs meanwhile, and a server that stops ends
  it with a log line.
- A development build or a pre-release downloads nothing: the row says "This OADM version has no optional plugins to
  download." and Install stays off (except for a removed add-on that is still loaded: Install keeps it); folders put in
  by hand load as they are.
- Wiring: `IAddOnListStore` = `SettingsAddOnListStore`, `IAddOnFiles` = `GitHubAddOnFiles` (the self update's
  `IReleaseFeed` and `UpdateDownloader`), `IAddOnActivation` = `PluginActivationAddOns` (Turn off through
  `PluginActivation.SetEnabledAsync`; the stored choice through `PluginRegistry.ChoiceOf`; waits for
  `PluginActivation.AddedPackagesFollowed`), `AddOnInstallerOptions(data folder, server version,
  AddOnInstallerOptions.CurrentRuntimeId())`. The load raises `PluginRegistry.PackageAdded` (the loader, never the
  installer); Installed is reported after the server followed it (`docs/spec/plugins.md` "Packages loaded at run time").
  The add-on contracts (`AddOnCatalog`, `AddOnStatus`, `IAddOnInstaller`, `IAddOnFiles`) live in
  `src/Oadm.Server/AddOns/`.
- Install: download into `<datafolder>/addons/.download/`, check SHA-256, unpack into a new folder (a broken ZIP, a name
  with ':' or one the file system refuses, a file twice, a path outside the folder or more than the limits: "The download
  is damaged. Try again."), check `plugin.json` (id as expected, `minSdkVersion` met) and `oadm-version.txt`, then swap it
  in and load it. Any failure leaves nothing behind and says why ("Could not download the optional plugin. Check that the
  server can reach github.com.", "The download is damaged. Try again."). All server texts are
  `Oadm.Server.AddOns.AddOnTexts`, the card's are the client's `OptionalPluginTexts`.
- One install or remove at a time; while a self update runs both are refused ("The server is updating.").
- Whether the server reaches the release: one check at a time, kept 5 minutes (`AddOnInstallerOptions.ReachCacheTime`),
  only for the card (`ListAddOns` with `check_reach`); a client's sync uses the last answer and never waits for GitHub.
- The client part is read and hashed once per version and kept in memory; one call at a time per session.
- Remove keeps the add-on's settings, records and `plugin-data/<id>` (as when a plugin is turned off); installing it
  again picks them up. A failed add-on (not loaded) is removed at once, its folder deleted.
- gRPC: `PluginService.ListAddOns`, `InstallAddOn` (streams progress: Download, Check, Install), `RemoveAddOn`,
  `GetAddOnClientPart` (the client part's files with their SHA-256 and the part's digest). Rules in `AccessPolicy`; spec
  in `docs/spec/grpc.md`.

## Client

- `PluginPackageStore.LoadAsync` announces the plugin list first; then `AddOnClientSync` brings the client parts in the
  background (the assemblies load on the thread pool, the parts join the registry on the UI thread) and the list is
  announced again when parts came, so the pages follow. The card shows "Getting the optional plugins from the server"
  meanwhile.
- While the server lists an add-on as Installing (it downloads it after its update) the client reads the list again every
  10 s (`PluginPackageStore.AddOnPollInterval`) until it is done, so a client connected during the fetch gets the pages.
- A newer version on the server while this client has the older one loaded: the new one is stored and loads at the next
  start; the row says "Restart the client to use the new version.".
- `ClientPluginLoader.LoadAddOnAsync`: the add-on's load contexts also find dependencies without deps.json next to the
  client dll and in `client/`; plugin folders keep their deps.json resolution only.

## Plugins page

- Right below the plugin list (at every window size: the list is as high as its rows and scrolls when the window is too
  small, the card follows directly) a card **Optional plugins**: one row per add-on of this version: name, one line,
  version, state (Not installed, Installing 42 %, Installed, Removed), the server's sentence when there is one (why it
  cannot be installed, when the files go, a failed download) and **Install** (not installed, failed, removed) or
  **Remove** (installed, failed; red, with the confirmation "Remove <name>? It turns off now. Its settings are kept.").
  Below the rows: "Optional plugins come from the OADM release on GitHub and are checked before they are installed.
  Clients get them from the server." A server without add-ons hides the card.
- The plugin list shows an installed add-on like every other plugin, without a mark (decision 2026-11-03: the list stays as it was).
- Without access to GitHub the row says "The server cannot reach github.com." and Install stays off; the add-on can then
  be put into `<datafolder>/addons/<id>/` by hand from the release ZIP (Help page names the folder).

## Tests

- Server: install, swap, load, remove and the failures (wrong SHA-256, wrong id, wrong release, SDK too old, damaged
  names, no network, an update running) with fakes for the download and the files; the add-on of the new version is
  fetched after an update; the stored choice survives Install again; the audit with a caller that has gone.
- Client: the client part is checked against the server's SHA-256 and digest, cached per server and version and loaded
  after the plugin list; a wrong hash is refused; a changed cache is fetched again; the client asks again while the
  server installs.
- Packaging: the OADM installers and ZIPs contain no add-on folder; each add-on ZIP contains its native libraries for its
  platform only; `check-addon-zip.sh` uses the server's limits.
- Never a real download in tests.
