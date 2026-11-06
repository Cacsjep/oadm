# Add-on plugins (vision plugins)

Status: **built** (2026-11-04): installer, registry, client, packaging and notices; the first add-on is Text recognition.
The user downloads the ZIP and installs it through the client (decision 2026-11-04, replaces the server's own download of
2026-11-03). The path through a real release (ZIP first, its hash in the server) is proven only by a real release run.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Plugins of OADM that are not in its installers: an administrator downloads the plugin's ZIP, clicks Install on the Plugins
page and chooses it, the client sends it to the server, and every client gets the client part from the server. The same
steps work online and offline. The first ones are the vision plugins, which analyse the cameras' pictures on the server
(text recognition, `docs/spec/ocr.md`). OADM itself stays free of models and inference libraries; no extra installer.

**Name for users: "optional plugins"** (decision 2026-11-03): the card "Optional plugins" on the Plugins page,
audit "Installed optional plugin ...", log lines the Logs page shows, docs and every UI text. "Add-on" stays the name in
code, files, the release ZIPs (`oadm-addon-...`) and this spec; it is never shown to users.

**Trust** (decision 2026-11-04, `docs/spec/security.md` "Optional plugins: trust"): only the exact genuine ZIP of this
server's version and system installs. The server holds its SHA-256, built in when the server is built. Why: the server runs
as SYSTEM or root, and an add-on runs inside it, so an OADM administrator's login must never become code on the computer.
Without the check, anyone with the Plugins permission (or a stolen login) could upload a changed ZIP. The ZIPs are not
signed; the built-in hash needs neither internet nor an extra step. **Remaining risk:** the manual folder install, which
loads what is in the folder without a hash. It needs write access to the server's disk, which already means control of the
computer. A client loads the client part its server hands it: the user must trust their server.

## Rules that never break

1. **Never in the OADM installers.** Add-on plugin projects (`<OadmAddOn>true</OadmAddOn>`) are left out of `manage
   publish all`, the MSI, .deb, .pkg and portable ZIPs. OADM starts, updates and runs the same without them.
2. **Only the genuine ZIP.** The server installs a file only when its SHA-256 equals the one built into this server for that
   add-on, checked on the server over the temporary file's bytes as they arrive, before anything is unpacked. Any other file
   is refused in the row. The hash is per system, so it also settles the system and the version. The server never downloads
   an add-on and never reaches the internet for one. A build without the hash (development, `dotnet run`) refuses Install
   and says so. Third-party plugins keep the manual install (`website/.../dev/plugin-sdk.md` "Ship it").
3. **An older add-on keeps running** (decision 2026-11-05, replaces "same version as OADM"; the hash checks the install, not
   the run). At every start an add-on loads when its folder's `oadm-version.txt` is the server's version or an older one
   (`AddOnFolders.IsOlder`; a pre-release is older than its release), also a folder put in by hand. Readers and rules keep
   working after an update. The fingerprint (`oadm-content.txt`, "Release files") decides whether anything is new: the same as
   this version's ZIP: row **Up to date**, nothing to do; another or none: **Update available** with Install and the download
   link, and the dialog of "Plugins page". **The floor:** `AddOnCatalog.OldestRunningVersion` (4.10.0) is the oldest version
   whose add-ons run on this server. A release that breaks them raises it, together with `PackageValidationBaselineVersion` of
   `Oadm.Sdk` and `Oadm.Sdk.Client`: `manage package nuget` validates both SDKs against that baseline, so a binary break fails
   the release until someone raises the floor. A change only in a theme key or in the host is not seen by that check: raise the
   floor by hand. Below the floor, a newer add-on, a folder without a version, an older add-on that does not load or registers
   no plugin, and a restore of a backup on another computer (the list stays, the files are not there) stay unloaded: state
   Failed, row and dialog ask to install it again. Two pre-releases of one version count as equal, so the add-on of `rc.1` is
   not loaded on `rc.2`. Install takes only the exact ZIP of this server's version (rule 2) and is refused while an add-on is
   Up to date: to replace one, Remove it, restart the server and install. Pre-releases work like releases: their own ZIP, their
   own hash.
4. **Administrators only.** Install and Remove need the Plugins permission of turning plugins on and off; both are in
   the audit log ("Installed optional plugin Text recognition V4.11.0", "Removed optional plugin ...", a refused or failed
   install "Could not install optional plugin" with the reason). The install is audited before its last message, also when the
   caller has gone meanwhile.
5. **Install without a restart, remove at the next start.** An installed add-on loads at once
   (`PluginLoader.LoadPackage` into its own `PluginLoadContext`). A first install starts off; installing again (after an
   update, or a removed one before the restart) keeps the administrator's stored choice. Turning it on is the
   existing Turn on path. Every part that reads the plugin registry (permissions, event and action catalogue, pages)
   follows an added package as it follows Turn on and off. Remove turns it off at once and keeps it loaded; until the
   restart it cannot be turned on (the plugin list refuses it with the Removed text) and Install keeps it without a
   file; its folder is deleted at the next server start (the row says "Removed. The files go at the next restart of
   the server."). Never an unload at run time: a collectible load context unloads only when nothing holds it, which is
   not guaranteed.
6. **Clients get the client part from the server**, never from the internet: one call per add-on, the files checked
   against the SHA-256 and the whole part against the digest the server sends, kept per server (its pinned certificate
   fingerprint, hex digits only) and version in the client's own `<datafolder>/plugin-cache/` (never the server's `addons/`: a server and a
   client of the same user share the data folder), loaded into its own load context while connected (the pages follow as
   on Turn on), else at the next connect. A cached copy is used only while it matches the digest the server lists; a
   client that cannot replace one that no longer matches loads nothing. A part that fails to load is tried again at the next
   sync. A
   removed add-on's client files, and the versions an add-on no longer uses, go at the client's next start. A client
   never installs anything on its computer and needs no rights for it. **Only from a pinned server:** the client part is
   code the client runs, so the sync asks for nothing and loads nothing while the connection has no pinned certificate
   (an `http://` server address, where `GrpcServerTrust.PinnedFingerprint` is null also when the same host and port were
   pinned over TLS; fake mode): one log line, the Plugins page and the plugin list work as usual, the add-on's pages are
   missing. What an older client fetched from such a server (`plugin-cache/unpinned`) is never loaded and goes at the
   client's next start (`AddOnClientCache.DeleteRemoved`).
7. **GPU when there is one, the CPU always works, no own service.** Vision plugins run inside the server's plugin; their
   native libraries (ONNX Runtime with its GPU providers) and model files are in the plugin's own folder for the server's
   platform and load through the plugin's load context (`PluginLoadContext.LoadUnmanagedDll`). A missing or failing GPU
   falls back to the CPU with one log line, never an error. Nothing else is downloaded at run time.

## Release files

- Per add-on and platform of the server: `oadm-addon-<id>-<version>-<rid>.zip` (`win-x64`, `linux-x64`, `linux-arm64`,
  `osx-x64`, listed once in `packaging/addons/addon-rids.txt`; a test keeps it equal to `AddOnCatalog.RuntimeIds`): the
  plugin folder as the server loads it (`plugin.json`, server part, client part, native libraries of that platform,
  models, `THIRD-PARTY-NOTICES.txt`, `LICENSE.txt` and `oadm-version.txt`). Built in the same `release.yml` run, attached
  to the same release, listed in `SHA256SUMS.txt` (for people who check by hand; the server does not read it). The notices
  list the add-on's own NuGet packages, the packages it ships outside its `*.deps.json` (`OadmNoticePackage` items in its
  csproj, e.g. DirectML with its full license) and its models' licenses (`tools/Oadm.Notices --addon`,
  `docs/spec/packaging.md` "Add-on ZIPs").
- `oadm-version.txt` (`AddOnFolders.VersionFileName`, written by `manage package addons`): the version the add-on belongs
  to. The server loads a folder of its version or an older one (rule 3) and refuses a ZIP whose file is missing or names
  another version than the server's.
- `oadm-content.txt` (`AddOnFolders.ContentFileName`, written by `manage package addons`, `Get-AddOnContent` /
  `addon_content`, the same in both scripts): the fingerprint: the SHA-256 of the sorted `git ls-tree -r HEAD` lines of the
  add-on's project folders, the `plugins/Shared` files they link, `Directory.Build.props`, `Directory.Build.targets`,
  `global.json` and `packaging/addons/content-epoch.txt`, without `*.md`, `.gitignore` and `.gitattributes`; "none" outside git.
  Tree ids do not depend on line endings or the system, so every runner of a release gets one value. Dependency versions and
  `Models.targets` are in the project folders. Raise `content-epoch.txt` to offer an update for a change outside these paths
  (the packaging, the notices). The server has this version's value built in like the hash (`OadmAddOnContent:<id>`, third
  field of the hash file; `check-server-hash.sh` checks it). ZIPs from before it have no file: the first update after it offers
  the new ZIP. A change in the SDK or host is the floor's matter (rule 3), not the fingerprint's.
- The built-in hash: `manage package addons` writes `artifacts/publish/addons/<rid>/addon-hashes-<version>.txt`, one
  "`<id> <sha256>`" per line; the server's publish passes it as `-p:OadmAddOnHashFile=<file>`; `Oadm.Server.csproj` turns each
  line into assembly metadata `OadmAddOnSha256:<id>`; `AssemblyAddOnHashes` reads it (`IAddOnHashes`). Assembly metadata
  survives `PublishSingleFile` and needs no file next to the exe. Only the hashes of that system go into its server, so
  the ZIPs are built first and never again after that (`docs/spec/packaging.md` "Add-on ZIPs"). `packaging/addons/check-server-hash.sh`
  compares the published exe with the ZIP in `release.yml`.
- The client part is platform-neutral (.NET only); the server hands it to clients of any system: `plugin.json`, the
  top-level `*.Client.dll`, the managed `*.Server.dll` it references (its contracts and texts; the client loads it next to
  the client dll in the add-on's load context) with their `.pdb`, and everything under `client/`
  (`AddOnFolders.ClientPartPatterns`, `AddOnLayout.ClientFolder`). Never the server part's dependencies, native libraries
  or models. A client part with native libraries is not allowed for add-ons.
- The list of add-ons a version offers is in the server (`AddOnCatalog`: id, name, one line what it adds), so the Plugins
  page shows them without internet.

## Server

- Folder: `<datafolder>/addons/<id>/`, writable by the service. Folders starting with a dot are never add-ons
  (`.upload/`, `.new-*` and `.old-*` while one is swapped, `.update-<id>` for a waiting update) and are emptied at every start,
  a waiting update after it was used; folders of ids this version
  does not offer are deleted at the start (log line) and dropped from the list. The list of installed add-ons is the
  server setting `AddOns.Installed` (JSON array of {id, version, removed}, `SettingsAddOnListStore`); backups hold the
  list, not the files. A folder put in by hand is
  taken into the list at the next start with the version of its `oadm-version.txt`, but not when others could change it
  until this start (see Start; log line, the folder is deleted). It is the way for a build
  without the hash and must pass the version rule like any other. As a service each
  `addons/<id>` is checked like every plugin folder (`FolderGuard.CheckPluginFoldersAsync`, `docs/spec/security.md`
  "Service: firewall and folders") before it loads. The loader reads it after the plugin folder next to the exe; an id in
  both: the bundled one wins and the add-on is not loaded (log line).
- Start (`OadmServerHost`): `AddOnInstaller.LoadInstalledAsync` right after `PluginLoader.LoadFromRoots` deletes the
  folders of removed add-ons and loads every add-on whose folder names this version or an older one (no event, the start reads the
  registry). A waiting update comes first: it must pass the folder check and name this exact version, and the old folder comes
  back when it does not load. Any other installed add-on (see rule 3) stays unloaded: state
  Failed with "Not loaded. Install it again for this OADM version." (`AddOnTexts.InstallAgain`, one phrase for all of them);
  its old folder stays until the new one is in place or the add-on is removed.
- Loose folders (service only; `docs/spec/security.md` "Service: firewall and folders"): the folder check says what others
  could change about `addons/` before it fixed the data folder (`ServerFoldersCheck.AddOns`, a `LoosePlugins` like the one
  of `plugins/`), and `LoadInstalledAsync(LoosePlugins, ...)` acts on it. Something loose inside `addons/<id>`: only that
  add-on is not used, listed or not; its folder and its waiting update are deleted (log line), a listed one stays on the
  list as Failed with "Removed: other users could change its folder. Install it again." (`AddOnTexts.FolderWasLoose`,
  until the next start, then the text above), the others load. A loose `addons/.update-<id>`: only the waiting update is
  deleted, the add-on keeps running and offers the update again. The data folder or `addons/` itself loose, or facts that
  could not be read: all of them. Something loose elsewhere (logs, backups, the database, `plugins/`, the extraction
  folder) changes nothing for add-ons. A loose folder that cannot be deleted keeps the add-on stored as Removed (not
  loaded, never taken up); a Removed add-on leaves the list once its folder and its waiting update are gone, and an
  install is refused ("Could not install the optional plugin. ...") while a leftover waiting update cannot be deleted, so
  a waiting update others could change never starts.
- Wiring: `IAddOnListStore` = `SettingsAddOnListStore`, `IAddOnHashes` = `AssemblyAddOnHashes`, `IAddOnActivation` =
  `PluginActivationAddOns` (Turn off through
  `PluginActivation.SetEnabledAsync`; the stored choice through `PluginRegistry.ChoiceOf`; waits for
  `PluginActivation.AddedPackagesFollowed`), `AddOnInstallerOptions(data folder, server version,
  AddOnInstallerOptions.CurrentRuntimeId())`. The load raises `PluginRegistry.PackageAdded` (the loader, never the
  installer); Installed is reported after the server followed it (`docs/spec/plugins.md` "Packages loaded at run time").
  The add-on contracts (`AddOnCatalog`, `AddOnStatus`, `IAddOnInstaller`, `IAddOnHashes`) live in `src/Oadm.Server/AddOns/`.
- Install (`InstallFileAsync`), in this order, any failure leaving the state as it was:
  1. Refused before anything is sent: a build without the hash ("This OADM build cannot check optional plugins. Put the
     plugin into its folder by hand."), a file whose name reads `oadm-addon-<id>-<version>-<rid>.zip` for another plugin,
     version or system (one sentence each: "This ZIP holds another plugin. Choose the ZIP of Text recognition.", "This ZIP is
     for another OADM version. This server needs V4.11.0.", "This ZIP is for another system. This server needs win-x64."), a
     size over `AddOnFolders.MaxZipBytes` (1 GB) or less free space than three times the file. A renamed file ("... (1).zip")
     goes on: the name only saves a pointless upload, the hash decides.
  2. Upload: the chunks go to `addons/.upload/`, hashed on the way, never held in memory. Another hash, or more bytes than
     announced: "This file is not the ZIP of this OADM version and system. Download it again." (`AddOnTexts.WrongFile`). Fewer
     bytes, or nothing for `UploadIdleTimeout` (1 min, so a client that stands still does not block the next install):
     "The upload stopped. Try again.".
  3. Check: unpacked into a new folder on the same volume (`addons/.new-*`), at most `MaxEntries` (20,000) files and
     `MaxUnpackedBytes` (4 GB) counted while reading; a broken ZIP, a name with ':' or one the file system refuses, a file
     twice or a path outside the folder: "The file is damaged. Download it again.". Then `plugin.json` (the expected id,
     `minSdkVersion` met) and `oadm-version.txt` (this exact version).
  4. Swap in (the old folder moves to `addons/.old-*`) and load; a folder the loader refuses goes and the old one comes
     back. Nothing half-installed is ever at `addons/<id>/` (the limits, a checked name, one swap).

  With a matching hash the checks of step 3 cannot fail for a file of the release; they stay as a second line (a build error,
  a bad hash file). All server texts are `Oadm.Server.AddOns.AddOnTexts`, the card's are the client's `OptionalPluginTexts`.
- `AddOnStatus.DownloadUrl`: the ZIP of this version and system on the OADM release
  (`GitHubReleaseFeed.DownloadPrefix` + `v<version>/` + the file name); none in a build without the hash.
- One install or remove at a time; while a self update runs both are refused ("The server is updating."), and Install now
  is refused while one runs, also while its file uploads ("An optional plugin is being installed or removed. Try again when
  it is done."; `UpdateGate.TryBegin` and `TryBeginAddOnWork` decide under one lock).
- The client part is read and hashed once per version and kept in memory; one call at a time per session.
- Remove keeps the add-on's settings, records and `plugin-data/<id>` (as when a plugin is turned off); installing it
  again picks them up. An add-on that is not loaded (state Failed) is removed at once, its folder deleted; a folder that cannot
  go is stored as Removed, so no start takes it up again and the next start deletes it.
- An install past its upload runs to its end: once the hash holds, a caller that goes changes nothing, the install finishes and
  is audited as installed.
- gRPC: `PluginService.ListAddOns`, `UploadAddOn` (bidirectional: the file in, progress out: Upload, Check, Install),
  `InstallAddOn` (takes back a removed add-on that is still loaded, no file; a client that still calls it to install gets
  "Update this client to install optional plugins."), `RemoveAddOn`, `GetAddOnClientPart` (the client part's files with
  their SHA-256 and the part's digest). Rules in `AccessPolicy`; spec in `docs/spec/grpc.md`.

- **Event Optional plugin not loaded** (`AddOns/AddOnEventSource`, decision 2026-11-04, issue #33): one stateful server event of the
  host, folder Optional plugins, on while an installed add-on is not loaded (`AddOnInstaller.NotLoaded`: the state Failed, so
  a version that may not run here, a restore and a load that failed), values `count` and `plugin` (the names). A plugin that is
  not loaded sends nothing, so the host says it. Reported after every install or remove and at the start, once the rules run.

## Client

- `PluginPackageStore.LoadAsync` announces the plugin list first; then `AddOnClientSync` brings the client parts in the
  background (the assemblies load on the thread pool, the parts join the registry on the UI thread) and the list is
  announced again when parts came, so the pages follow. The card shows "Getting the optional plugins from the server"
  meanwhile.
- While the server lists an add-on as Installing (another administrator installs it) the client reads the list again every
  10 s (`PluginPackageStore.AddOnPollInterval`) until it is done, so a client connected during the install gets the pages.
- A newer version on the server while this client has the older one loaded: the new one is stored and loads at the next
  start; the row says "Restart the client to use the new version.".
- `ClientPluginLoader.LoadAddOnAsync`: the add-on's load contexts also find dependencies without deps.json next to the
  client dll and in `client/`; plugin folders keep their deps.json resolution only.

## Plugins page

- Right below the plugin list (at every window size: the list is as high as its rows and scrolls when the window is too
  small, the card follows directly) a card **Optional plugins**: one row per add-on of this version: name, one line,
  version, state (Not installed, Upload 42 %, Check, Install, Installing 42 % for another administrator's install, Up to date,
  Update available, Removed), the server's sentence when there is one (why it is not loaded, when the files go), a refusal in red, and:
  - **Download ZIP** (link, tooltip the file name, for a server without internet): opens the ZIP of the server's exact
    version and system in the browser, shown while Install asks for a file. The client opens only OADM's own release
    (`GitHubReleaseFeed.DownloadPrefix`), whatever a server sends.
  - **Install** (not installed, not loaded, Update available, removed): asks for a ZIP on this computer and sends it, with its
    progress and **Cancel** during the upload in the row. A removed add-on comes back without a file. An older add-on that runs
    is never swapped (rule 5): the new one waits in `addons/.update-<id>`, the row shows **Restart needed**, "The new version
    starts at the next restart of the server." and **Restart server**, and the next start puts it in place when it names this
    exact version.
  - **Restart server** (a waiting update, Administrators; also the dialog right after such an Install, "Restart the server?" with
    **Restart server** and **Later**): `RestartForAddOns`, `ServerRestart` as for a restore. Refused without a waiting update, while a task runs, during a
    self update and while another add-on is being installed or removed (the row says so), audited "Restarted the server for optional plugin <names>". The client reconnects by itself.
  - **Remove** (installed, not loaded; red, with the confirmation "Remove <name>? It turns off now. Its settings are kept.").

  Below the rows, while a row has the link: "Click Download ZIP, then Install and choose the file. Clients get the plugin
  from the server." A server without add-ons hides the card.
- **Dialog** (`AddOnNotice`, started by `AppShell` over the main window after the new version dialog, Administrators only): "Update optional plugins" names each add-on with Update
  available or not loaded, with **Open Plugins** and **Later**. Once per server, user and state (`ClientSettings.AddOnNotices`, stored when the dialog closed):
  it comes back when the state changes, so an OADM update with nothing new for the add-on shows nothing.
- The plugin list shows an installed add-on like every other plugin, without a mark (decision 2026-11-03: the list stays as it was).

## Tests

- Server: install, swap, load, remove; every refusal (no hash, wrong hash, a name of another plugin, version or system, another
  plugin or version in the ZIP, no plugin.json, SDK too old, zip-slip, a file twice, the size and file limits, more or fewer
  bytes than announced, no space, a client that stands still, an update running); a failed install leaves the previous state; the
  loader's refusal brings the old folder back; the state after an update and a restore; the version rule for folders put in by
  hand; the stored choice survives Install again; the hash read back from assembly metadata; the audit with a caller that has
  gone; a caller that goes right after the upload; a Failed add-on whose folder stays; an older add-on that runs, is up to date,
  registers nothing or lies below the floor; a waiting update (swap, fallback, another version, folder check, a loose one,
  one that cannot be deleted, Remove, the audit's version); loose folders (everything, one add-on, something beside it, a
  folder put in by hand; with the host and the folder check: `ServiceSecurityTests`). Hashes are injected (`IAddOnHashes`). The scripts' fingerprint rules: `AddOnPackagingTests`.
- Client: the file picker, the steps, Cancel, a refusal, the link (only OADM's own release), the row after an update; the
  client part is checked against the server's SHA-256 and digest, cached per server and version and loaded
  after the plugin list; a wrong hash is refused; a changed cache is fetched again; the client asks again while the
  server installs.
- Packaging: the OADM installers and ZIPs contain no add-on folder; each add-on ZIP contains its native libraries for its
  platform only; `check-addon-zip.sh` uses the server's limits; the scripts and `release.yml` keep the ZIPs before the server.
- Never a real download and never a real installer in tests.
