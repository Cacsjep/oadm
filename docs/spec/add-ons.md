# Add-on plugins (vision plugins)

Status: **built** (2026-11-03): installer, registry, client, packaging and notices; the first add-on is Text recognition.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Plugins of OADM that are not in its installers: an administrator installs them on demand on the Plugins page, the server
downloads them from the OADM release on GitHub, and every client gets their client part from the server. The first ones
are the vision plugins, which analyse the cameras' pictures on the server (text recognition, `docs/spec/ocr.md`). OADM
itself stays free of models and inference libraries; no extra installer.

**Name for users: "optional plugins"** (decision 2026-11-03): the card "Optional plugins" on the Plugins page,
audit "Installed optional plugin ...", docs and every UI text. "Add-on" stays the name in code, files, the
release ZIPs (`oadm-addon-...`) and this spec; it is never shown to users.

## Rules that never break

1. **Never in the OADM installers.** Add-on plugin projects (`<OadmAddOn>true</OadmAddOn>`) are left out of `manage
   publish all`, the MSI, .deb, .pkg and portable ZIPs. OADM starts, updates and runs the same without them.
2. **Only from OADM's own release, checked.** The server downloads an add-on only from the GitHub release of its own
   version (`https://github.com/Cacsjep/oadm/releases/download/v<version>/`, the hosts and limits of self update:
   `GitHubReleaseFeed.AllowedHosts`, HTTPS, size limit, timeouts) and installs it only when its SHA-256 matches the
   release's `SHA256SUMS.txt`. No other source, no upload of a file: add-ons run with the server's rights. Third-party
   plugins keep the manual install (`website/.../dev/plugin-sdk.md` "Ship it").
3. **Same version as OADM.** The server always installs the add-on of its own version. After a server update it fetches
   the new version of every installed add-on by itself before it loads them (an add-on whose download fails stays
   unloaded with "Could not download <name> V<version>. Try again on the Plugins page.", OADM runs).
4. **Administrators only.** Install and Remove need the Plugins permission of turning plugins on and off; both are in
   the audit log ("Installed optional plugin Text recognition V4.11.0", "Removed optional plugin ...").
5. **Install without a restart, remove at the next start.** An installed add-on loads at once
   (`PluginLoader.LoadPackage` into its own `PluginLoadContext`), off; turning it on is the existing Turn on path. Every
   part that reads the plugin registry (permissions, event and action catalogue, pages) follows an added package as it
   follows Turn on and off. Remove turns it off at once and keeps it loaded; its folder is deleted at the next server
   start (the page says "Removed. The files go at the next restart of the server."). Never an unload at run time: a
   collectible load context unloads only when nothing holds it, which is not guaranteed.
6. **Clients get the client part from the server**, never from the internet: one call per add-on, the files checked
   against the SHA-256 the server sends, kept per server version in the client's data folder, loaded into its own load
   context while connected (the pages follow as on Turn on), else at the next connect. A removed add-on's client files go
   at the client's next start.
   A client never installs anything on its computer and needs no rights for it.
7. **GPU when there is one, the CPU always works, no own service.** Vision plugins run inside the server's plugin; their
   native libraries (ONNX Runtime with its GPU providers) and model files are in the plugin's own folder for the server's
   platform and load through the plugin's load context (`PluginLoadContext.LoadUnmanagedDll`). A missing or failing GPU
   falls back to the CPU with one log line, never an error. Nothing else is downloaded at run time.

## Release files

- Per add-on and platform of the server: `oadm-addon-<id>-<version>-<rid>.zip` (`win-x64`, `linux-x64`, `linux-arm64`,
  `osx-x64`): the plugin folder as the server loads it (`plugin.json`, server part, client part, native libraries of that
  platform, models, `THIRD-PARTY-NOTICES.txt` and `LICENSE.txt`). Built in the same `release.yml` run, attached to the
  same release, listed in `SHA256SUMS.txt`. The notices list the add-on's own NuGet packages and its models' licenses
  (`tools/Oadm.Notices --addon`, `docs/spec/packaging.md` "Add-on ZIPs").
- The client part is platform-neutral (.NET only); the server hands it to clients of any system: `plugin.json`, the
  top-level `*.Client.dll`, the managed `*.Server.dll` it references (its contracts and texts; the client loads it next to
  the client dll in the add-on's load context) with their `.pdb`, and everything under `client/`
  (`AddOnFolders.ClientPartPatterns`). Never the server part's dependencies, native libraries or models. A client part
  with native libraries is not allowed for add-ons.
- The list of add-ons a version offers is in the server (`AddOnCatalog`: id, name, one line what it adds, size per
  platform), not read from GitHub, so the Plugins page shows them without internet.

## Server

- Folder: `<datafolder>/addons/<id>/`, writable by the service. Folders starting with a dot are never add-ons
  (`.download/`, `.new-*` and `.old-*` while one is swapped) and are emptied at every start. The list of installed add-ons
  is the server setting `AddOns.Installed` (JSON array of {id, version, removed}, `SettingsAddOnListStore`); backups hold
  the list, not the files: after a restore the server downloads them again, as after an update. A folder put in by hand
  is taken into the list at the next start. The loader reads it after the plugin folder next to the exe; an id in both:
  the bundled one wins and the add-on is not loaded (log line).
- Start (`OadmServerHost`): `AddOnInstaller.LoadInstalledAsync` right after `PluginLoader.LoadFromRoots` (deletes the
  folders of removed add-ons, loads every add-on of this version; no event, the start reads the registry);
  `FetchAfterUpdateAsync` in the background once the start is over (after `PluginActivation.LoadAsync`, the licenses and
  `PluginRegistry.MarkStarted`): an add-on of another version or without its folder (after an update or a restore) is
  downloaded, loaded and followed like one installed now, with its stored choice; OADM runs meanwhile.
- A development build or a pre-release downloads nothing: the row says "This OADM version has no optional plugins to
  download." and Install stays off; folders put in by hand load as they are.
- Wiring: `IAddOnListStore` = `SettingsAddOnListStore`, `IAddOnFiles` = `GitHubAddOnFiles` (the self update's
  `IReleaseFeed` and `UpdateDownloader`), `IAddOnActivation` = `PluginActivationAddOns` (Turn off through
  `PluginActivation.SetEnabledAsync`; waits for `PluginActivation.AddedPackagesFollowed`), `AddOnInstallerOptions(data
  folder, server version, AddOnInstallerOptions.CurrentRuntimeId())`. The load raises `PluginRegistry.PackageAdded` (the
  loader, never the installer); Installed is reported after the server followed it (`docs/spec/plugins.md` "Packages
  loaded at run time").
- Install: download into `<datafolder>/addons/.download/`, check SHA-256, unpack into a new folder, check `plugin.json`
  (id as expected, `minSdkVersion` met), then swap it in and load it. Any failure leaves nothing behind and says why
  ("Could not download the optional plugin. Check that the server can reach github.com.", "The download is damaged. Try
  again."). All server texts are `Oadm.Core.Plugins.AddOns.AddOnTexts`, the card's are the client's `AddOnTexts`.
- One install or remove at a time; while a self update runs both are refused ("The server is updating.").
- Remove keeps the add-on's settings, records and `plugin-data/<id>` (as when a plugin is turned off); installing it
  again picks them up.
- gRPC: `PluginService.ListAddOns`, `InstallAddOn` (streams progress: Download, Check, Install), `RemoveAddOn`,
  `GetAddOnClientPart` (the client part's files with their SHA-256). Rules in `AccessPolicy`; spec in
  `docs/spec/grpc.md`.

## Plugins page

- Below the plugin list a card **Optional plugins**: one row per add-on of this version: name, one line, size, state (Not
  installed, Installing 42 %, Installed), and **Install** or **Remove** (red, with the confirmation "Remove <name>? It
  turns off now. Its settings are kept."). Below the rows: "Optional plugins come from the OADM release on GitHub and are
  checked before they are installed. Clients get them from the server." A server without add-ons hides the card.
- The plugin list shows an installed add-on like every other plugin, without a mark (decision 2026-11-03: the list stays as it was).
- Without access to GitHub the row says "The server cannot reach github.com." and Install stays off; the add-on can then
  be put into `<datafolder>/addons/<id>/` by hand from the release ZIP (Help page names the folder).

## Tests

- Server: install, swap, load, remove and the failures (wrong SHA-256, wrong id, SDK too old, no network, an update
  running) with fakes for the download and the files; the add-on of the new version is fetched after an update.
- Client: the client part is checked against the server's SHA-256, cached per server version and loaded on connect;
  a wrong hash is refused.
- Packaging: the OADM installers and ZIPs contain no add-on folder; each add-on ZIP contains its native libraries for its
  platform only.
- Never a real download in tests.

## Build plan (parallel work)

Built in three waves; the packages of one wave run at the same time (one agent each). Each package owns its folders;
files shared by several packages are changed only in wave 1 or wave 3. Text recognition (`docs/spec/ocr.md`) is built
in the same waves.

**Wave 1, contracts (one agent, first, small):** everything the others build against, compiled and tested empty.
- `src/Oadm.Contracts/Protos/plugin.proto`: `ListAddOns`, `InstallAddOn` (progress stream), `RemoveAddOn`,
  `GetAddOnClientPart`; `AccessPolicy` rules; `docs/spec/grpc.md`.
- `src/Oadm.Core/Plugins/AddOns/`: `AddOnCatalog` (entries), `AddOnState`, interfaces `IAddOnInstaller`
  (install, remove, list, progress), `IAddOnFiles` (download and check, faked in tests), `IPluginRegistryChanges`
  (an added package, raised by the registry).
- MSBuild property `OadmAddOn` read by the publish scripts (no behaviour yet).

**Wave 2, in parallel:**

| Package | Owns | Builds | Against |
|---|---|---|---|
| A1 Install | `src/Oadm.Server/AddOns/`, `tests/Oadm.Server.Tests/AddOns/` | download (reusing `GitHubReleaseFeed`, `SHA256SUMS.txt`), check, unpack, swap, load; remove at the next start; fetch after an update; gRPC service | `IAddOnInstaller`, `IAddOnFiles`, `PluginLoader.LoadPackage` |
| A2 Registry | `src/Oadm.Core/Plugins/` (except `AddOns/`), its tests | every reader of the registry (permissions, event and action catalogue, page list, compliance types) follows an added package like Turn on | `IPluginRegistryChanges` |
| A3 Client | `src/Oadm.Client/Plugins/AddOns/`, `src/Oadm.Client/Settings/` Plugins page, client tests | client part fetch, SHA-256 check, cache per server version, load while connected; Add-ons card | the wave 1 proto, a fake server |
| A4 Packaging | `manage.*`, `packaging/`, `.github/workflows/release.yml` | add-ons left out of the installers and ZIPs; `oadm-addon-<id>-<version>-<rid>.zip` per platform; `SHA256SUMS.txt`; packaging tests | `OadmAddOn` |

**Wave 3, together (one agent):** wiring in `OadmServerHost` and the client's services, `CHANGELOG.md`,
`docs/spec/plugins.md`, the user docs, screenshots for the user, one review and the full test suite.
