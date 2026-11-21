# Removing plugins a release no longer ships

Status: **built 2026-11-21** (agreed 2026-11-21). Recording firewall rules per plugin is not built ("Firewall rules").

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

## Rule

What a release ships is the truth. A bundled plugin the new version does not ship is gone after the update: its folder
does not exist and its state is deleted. There is no hand-maintained list of removed plugins.

- No retired list, no version texts ("Removed in OADM x").
- No rename or successor migration. A future rename is a new id; the old id is simply no longer shipped.
- No license handling (no plugin license keys exist yet).
- `MergedPlugins` (`src/Oadm.Core/Plugins/MergedPlugins.cs`, `oadm.est` merged into `oadm.pki`) stays as it is: its
  takeover still matters for restoring old backups that hold `oadm.est` data, and `oadm.est` is a core plugin id of PKI.

## Shipped manifest

- The publish step writes `plugins/bundled.json` next to the bundled plugin folders of the server and the client:
  `{ "plugins": ["oadm.acap", ...] }`, the `plugin.json` ids of the folders actually in the stage
  (`publish_plugins` in `manage.sh` and `manage.ps1`: `write_bundled_manifest` / `Write-BundledManifest`, stage
  `artifacts/publish/plugins`; `copy_plugins` / `Copy-Plugins` copy it with the folders). Never written by hand.
- Reading, the check and the clean are one class for both apps: `Oadm.Contracts.Plugins.BundledPlugins`.
- The loaders enumerate folders only, so the file in the root does not disturb them.
- `--self-check` of the published server and client fails when the manifest is missing or does not list exactly the
  folders next to it (runs in `release.yml` "Self check of the published apps").
- A root without a manifest (development `artifacts/plugins`, `--plugin-dir`, the data folder) loads as today. Stale
  development folders go with `manage clean`. A root with a manifest is a bundled root; the data folder's `plugins` is
  never one, even with a manifest in it.

## Folders

### Installers

| Installer | Old plugin folders after an update | Change |
|---|---|---|
| MSI | Removes files the new package lacks | None |
| .rpm | Removes files the new package lacks | None |
| .deb | dpkg removes the old package's files; files created later stay | `postinst` and `client-postinst` keep their `rm -rf .../plugins/oadm.est` lines (`packaging/linux/postinst`, `packaging/linux/client-postinst`); the start-up clean covers the rest |
| macOS .pkg | Keeps files the new package lacks | `preinstall` (server) and `client-scripts/preinstall` (client) `rm -rf` the whole bundled plugins folder (`/Library/Application Support/OADM/server/plugins`, `/Applications/OADM.app/Contents/Resources/plugins`) instead of the `oadm.est` lines |
| Portable ZIP | Unpacked over an old folder keeps everything | Start-up clean |

### At start

- Server (`PluginPaths.Bundled`) and client (`<app>/plugins`; on macOS `Contents/Resources/plugins`,
  `ClientPluginLoader.AppBundleResources`): before loading, every folder of the bundled root that has a `plugin.json`
  whose id is not in the manifest is skipped, with one log line "Plugin <id> skipped: not part of this OADM version"
  (not a load error).
- Then it is deleted where the process may write: rename the folder first to `<name>.removing` (fails cleanly when
  read-only), then delete the renamed folder; a rename that fails means skip loading only. A renamed folder left by a
  crash is never loaded and is deleted at the next start.
- Server: `BundledPluginsAtStart.PrepareRoots` before `PluginLoader.LoadFromRoots` (`PluginLoader.SkipFolder` keeps a
  folder that stayed from loading). Client: the `ClientPluginLoader` constructor (the data folder root passed as
  `installedRoot`).
- Never delete inside the macOS app bundle (it breaks the code signature): skip loading only. The client preinstall
  covers it.
- Never touched: `<datafolder>/plugins` (hand-installed third-party plugins), `<datafolder>/addons/<id>` (already
  cleaned by `AddOnInstaller.LoadInstalledAsync` for ids `AddOnCatalog` no longer offers), the client cache
  `<datafolder>/plugin-cache/<server>/<id>/` (`AddOnClientCache`, follows what the server lists).

## State

### What the server remembers

- Setting `Plugins.Bundled` (`Oadm.Core.Plugins.RemovedPlugins.SettingKey`): JSON array of the ids the previous start
  had from the bundled roots (`BundledPluginsAtStart.BundledIds`); not written and nothing cleaned at a start without a
  bundled root (a development run). Ids means the package
  ids plus every task, core and action plugin id those packages registered: settings, records, data folders and
  permissions belong to the core plugin id, on/off to the package (`docs/spec/plugins.md` "Loading and packaging"; PKI
  holds `oadm.pki`, `oadm.est`, `oadm.radius`).
- At start, after the plugins loaded and before `RoleStore.SyncCatalogAsync` and `BackupManager.RecordRestoreAsync`:
  gone = `Plugins.Bundled` minus the ids known now. Known now: every package and plugin id loaded from any root, the ids
  of `<datafolder>/plugins` folders, every id `AddOnCatalog` offers and every installed add-on, `MergedPlugins.Ids`.
- Any plugin load error: nothing is deleted this start (a package that failed hides its inner plugin ids), the stored
  set stays. Same precedent as `ProtectedTagRegistry.LoadAsync`.
- An id that is only turned off, PRO-locked or failed to load is never gone.
- Each gone id: delete all its state (below, `RemovedPlugins.CleanAsync`), then write the new set. Idempotent: every
  step deletes what is there, a start that stops halfway repeats the rest. A data folder that cannot be deleted keeps its
  id in the stored set, so the next start tries again.
- One audit entry (user "system", action "Deleted the data of a plugin no longer in OADM",
  `AuditActions.PluginDataDeleted`) and one log line per gone id when something was deleted: "Plugin <id> is no longer
  part of OADM: its settings and data were deleted." (`RemovedPlugins.DeletedText`).

### State items

| Item | Where |
|---|---|
| On/off choice: entry in `Plugins.Enabled` | `src/Oadm.Server/Plugins/PluginActivation.cs` (`SettingKey`), `PluginRegistry.ApplyEnabledStates` |
| Settings `Plugin:<id>:*` | `src/Oadm.Core/Settings/ServerSettings.cs` (`SettingKeys.PluginKey`), table Settings |
| Records | table PluginRecords, `src/Oadm.Core/Plugins/PluginRecordStore.cs` |
| Data folder `plugin-data/<id>` | `OadmPaths.PluginDataDirectory` (`src/Oadm.Core/Persistence/OadmPaths.cs`) |
| Role permission keys `<id>:<key>`, `task:<id>`, `task:<id>:<key>` in every role (built-in and custom), both `PermissionsJson` and `KnownJson` | `RoleStore.ForgetPluginsAsync` (`src/Oadm.Core/Auth/RoleStore.cs`; other unknown keys are kept), formats in `PermissionCatalog` |
| Role start page `plugin:<id>`: reset to the default (Devices, stored empty) | `RoleEntity.StartPage`, `src/Oadm.Core/Auth/AuthEntities.cs` |
| Windows firewall rules the plugin opened | see below |
| Client plugin settings `<id>/<key>` | `ClientSettings.PluginSettings`, `src/Oadm.Client/Infrastructure/ClientSettings.cs`: the client keeps its own previous shipped set the same way (`ClientSettings.BundledPlugins`, `RemovedClientPlugins.Clean`; nothing on a client load failure) |

Kept as history: task rows (`TaskEntity.PluginId`, they show their stored name), the audit log, the event log.
Licenses (`Plugins.Licenses`, `Plugins.TrialStarted`): out of scope.

### Firewall rules

- Names come from `FirewallRule.ForService` / `ForServiceRange` (`src/Oadm.Sdk/Network/FirewallRule.cs`):
  "OADM Server (<service>, <protocol> <port>)". The service word is the plugin's own (DHCP, NTP, Load test) and the port
  may be a setting, so the name cannot be derived from the plugin id.
- Not built (future work): the host would record each rule a plugin opens (rule name per plugin id, a setting) in its
  `IFirewallRules` implementation (`src/Oadm.Server/Hosting/NetshFirewallRules.cs`) and delete the recorded rules of a
  gone id. The host passes one shared `IFirewallRules` to every plugin today, so the record needs a per-plugin wrapper in
  `CorePluginHost`. Until then a gone plugin's rule stays only when the server stopped without the plugin closing it
  (`FirewallRuleKeeper` closes its rule when the plugin stops).

### After a restore

- The restored database brings the backup's `Plugins.Bundled`: the next start cleans the ids that version shipped and
  this one does not, the same way (restored data of a plugin this version does not have).
- A backup made before `Plugins.Bundled` existed holds no record: nothing of it is deleted.
- `Plugins.BundledVersion` (`RemovedPlugins.VersionKey`) holds the OADM version that wrote the set; a set of a newer
  version (a backup of it restored on an older one) is neither cleaned nor overwritten, a missing version cleans as before;
  the client does the same with `ClientSettings.BundledPluginsVersion` (an older client sharing the data folder).
- The restore report (`BackupManager.RecordAsync`, `src/Oadm.Server/Backup/BackupManager.cs`, given the deleted ids by
  `RecordRestoreAsync`) says for a deleted id "Plugin <id> is no longer part of OADM: its settings and data were
  deleted."; "not installed on this server; its settings were kept." stays only for ids that were kept (third-party
  plugins, add-ons).

## References that remain

Rules are never rewritten automatically. Texts that would say something wrong today:

- Recipient of a type no plugin declares: `ActionRunner.TestRecipientAsync` and the session test
  (`src/Oadm.Core/Events/ActionRunner.cs`) and `RecipientSessionPool` say "Not available: this type of recipient is no
  longer installed" when the catalog has no such type and "Not available: this type of recipient is turned off" for a
  type that exists but is off (`RecipientChecks.TypeNotInstalled`, `TypeTurnedOff`, `TypeUnavailable`).
- Already right: rules ("an action of this rule is not installed", "an event of this rule no longer exists",
  `RuleTexts`), compliance ("this kind of check no longer exists", `ComplianceEngine`).
- Provisioning nodes start other plugins' tasks by literal ids (`plugins/Oadm.Plugins.Provisioning/Nodes/TaskNode.cs`,
  `DateTimeNode.cs`, `UpgradeNodes.cs`, `CertificateNodes.cs`, `ConfigurationNode.cs`): the source scan test below keeps
  them on shipped ids.

## Client

- `PluginPackageStore.IsEnabled` treats a package the server does not list as on (older server). A newer client never
  loads a not-shipped bundled folder, so this needs no change.

## Verified

- "Start from" and Copy on the Permissions page (`src/Oadm.Client/Settings/RolesViewModel.cs` `CopyRole`, `ApplyEditor`)
  copy only the permissions; the start page comes from the dialog's choice (Devices by default), so a copy of a role
  whose start page is a gone plugin never sends `plugin:<id>`. Not a bug (`RolesMatrixTests`
  `A_copy_of_a_role_whose_start_page_is_gone_starts_on_Devices`). An existing role whose start page is gone keeps it
  listed as "<id> (off)" until the start-up clean resets it.

## Guard tests

`tests/Oadm.Server.Tests/Plugins/PluginRemovalTests.cs`, `tests/Oadm.Core.Tests/Plugins/RemovedPluginsTests.cs`,
`tests/Oadm.Client.Tests/PluginRemovalTests.cs`, the source scan in `tests/Oadm.Plugins.Provisioning.Tests`
(`Every_task_a_node_starts_is_a_task_of_a_shipped_plugin`).

- Manifest: `publish_plugins` in both manage scripts writes it; `--self-check` checks it against the folders.
- A bundled folder not in the manifest is skipped and deleted (server and client); a read-only root only skips; the
  macOS app bundle only skips.
- A `<datafolder>/plugins` plugin and an add-on are never touched, also when not in the manifest.
- State of a formerly shipped id is deleted once, all items above, also in custom roles; a second start changes
  nothing.
- An id that is turned off, PRO-locked or failed to load keeps its state; any load error deletes nothing.
- A restore of a backup whose `Plugins.Bundled` holds a gone id cleans it at the next start, and the report says so.
- `oadm.est` state stays (merged, a core plugin id of PKI).
- Source scan: no literal plugin id in `src/` or `plugins/` code that is not shipped (Provisioning's literal task ids
  must all be shipped ids).
- The macOS preinstall scripts remove the whole bundled plugins folder.
