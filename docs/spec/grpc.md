# gRPC services

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

- Every call needs `authorization: Bearer <token>` in the metadata (plus `oadm-client-machine`), except
  `AuthService.Status`, `Login`, `CreateFirstAdmin`; UNAUTHENTICATED without a valid token, PERMISSION_DENIED with what the
  role lacks (`Oadm.Server.Auth.AccessPolicy`, see "Security and access", Roles and permissions).
- `AuthService` (`auth.proto`): `Status` (server name, version, `needs_first_admin`, `setup_code_required` when the
  caller is not loopback), `Login(user, password, remember)` -> {token, UserInfo} (UNAUTHENTICATED one message for
  wrong password / unknown / disabled user, RESOURCE_EXHAUSTED while locked), `CreateFirstAdmin(user, password,
  setup_code, remember)` (FAILED_PRECONDITION users exist, PERMISSION_DENIED setup code, INVALID_ARGUMENT), `Logout`
  (revokes the call's token), `Me`, `WatchSession` (the caller's user with role and permissions now and after every
  change). `UserInfo` carries `role` (the two-value role of older clients: ADMIN for Administrator), `role_id`,
  `role_name`, and for the caller `permissions` + `all_permissions`. `UserService` (Admin): `List`, `Add` (`role_id`, else
  `role`), `Update` (optional `role_id` / `role` / disabled, new password; a password or disabling ends the user's
  sessions, a role applies to the next call), `Delete`; FAILED_PRECONDITION for the own account or the last enabled
  administrator. `AuditService` (Admin): `List(limit, offset)` newest first + `total_count` (limit 0 = 10,000).
  Devices of a user (`docs/spec/device-scope.md`): `UserInfo.all_devices = 12` (optional: unset only from servers before
  device groups; always true for the Administrator role), `device_group_id = 13`, `device_group_name = 14`;
  `AddUserRequest.all_devices = 5` / `device_group_id = 6` (neither: all devices, older clients; `all_devices` false
  without a group: INVALID_ARGUMENT "Choose All devices or a device group."); `UpdateUserRequest.all_devices = 6` /
  `device_group_id = 7` (unset = keep; audited "devices Building B"; ends the user's streams like a new role).
- `DeviceGroupService` (`device_groups.proto`, Admin): `List` (`DeviceGroupInfo` {id, name, description, rules, revision,
  device_count, user_names, summary "Site Building B, tag PTZ, 3 devices, except 1", created, changed} sorted by name +
  `total_devices`), `Get`, `Save(SaveDeviceGroupRequest {id empty = new, name, description, rules, revision})` (audited
  "Created device group" / "Changed device group"; INVALID_ARGUMENT, ALREADY_EXISTS name, NOT_FOUND, FAILED_PRECONDITION
  older revision "Another administrator changed this device group meanwhile. ...", RESOURCE_EXHAUSTED over 200),
  `Delete` (reply `user_names` who have no devices now, `disabled` "3 event rules"; audited "Deleted device group"),
  `Preview(rules, group_id)` (`device_count`, `total_devices`: the editor's live count). `DeviceGroupRules` {this_server,
  site_ids, tag_ids, serials, except_serials}. Shared messages: `DeviceOwner` {all_devices, group_id, group_name}
  (`RuleSummary.owner = 11`, `SaveRuleRequest.owner = 2`, `ComplianceCheckSummary.owner = 19`; unset in a save = the
  caller's devices for a new object, keep for a changed one; only administrators change it), `MyDevices`
  (`MyRole.devices = 4`: all devices or the group's name, summary without excepted devices, site and tag names, single
  device count, device_count of the own view). `DeviceTag.group_names = 6` and `Site.group_names = 20` (the groups that
  use a tag or site, for users with all devices), `ServerSettings.action_destinations = 12` (`ActionDestinationList`:
  where rules of device groups may send). Wave 1 packages fill these fields; the foundation fills the user fields,
  `MyRole.devices` and the group service.
- Device ids outside the caller's view answer like unknown ids: NOT_FOUND `DeviceMessages.NoLongerAvailable` ("3 devices
  are no longer available."), the whole request refused (`DevicesNotAvailableException`, mapped by `AuthInterceptor`).
  Methods for users with all devices only (`AccessRule.AllDevices`) answer PERMISSION_DENIED "Only users with all
  devices can do this." for users with a device group.
- `RoleService` (`roles.proto`, Admin except `Mine`): `GetMatrix` (every `PermissionInfo` {key, name, description, group,
  icon_key, operator_default, viewer_default, full_device_control, available} in matrix order and every `RoleInfo` {id,
  name, description, kind, permissions, user_count, version, start_page}), `SaveRoles(roles)` (create = empty id, change; one transaction; `RoleChange.start_page` unset = keep, INVALID_ARGUMENT "Choose a start page from the list." for anything but a start page, the only change the Administrator role accepts;
  FAILED_PRECONDITION for the Administrator role and for a change whose `version` is older than the stored role (two
  administrators: "Another administrator changed the role X meanwhile. Click Discard and make your changes again.";
  version 0 = no check), INVALID_ARGUMENT names / over 50 roles), `DeleteRole(id)` (custom roles
  without users), `Mine` (the caller's role name and its permissions with names). `ServerSettings.roles_notice = 22`
  (administrators only): what operators lost with the upgrade to roles.
- `DeviceService`: `List` (legacy: one message with every device, 23 MB for 5,000 devices with their
  API lists; clients do not use it), `Watch(WatchDevicesRequest)` (stream of DeviceChanged events: one
  ADDED per device, then with `snapshot_end_marker = 1` one `SNAPSHOT_END` (kind 4, no device), then live
  changes; the request is wire compatible with the former Empty), `Remove` and `SetCredentials` (one
  transaction for all ids), `Refresh`, `GetWebUiUrl`, `LogIn(device_ids, user_name, password,
  save_to_credential_list)` (the context menu's "Log in", see "Main Window"; Operator: per device
  (`Oadm.Core.Devices.DeviceLoginService`, 8 at a time, 15 s each) the stored address, scheme and pinned certificate with the
  new credential, authenticated `basicdeviceinfo`, the serial must be the record's; CertificateChanged devices are refused
  without a request; the accepting devices get the credential in one transaction, their cached client dropped and a full
  refresh queued (that sets Ok); reply `results` {device_id, ok, rejected, message: "The user name or password is
  wrong.", "Unreachable - ...", "Another device answers at this address.", "Certificate changed - accept the new
  certificate first"} in request order + `credential_list_note`; save to the credential list only for an Admin caller
  and only when a device accepted it, a full list is the note "The credential list is full (20 entries); the login was
  not saved there.", never a failed login; audit "Device login" {user name, "5 devices, 3 logged in"}; INVALID_ARGUMENT
  without devices, user name or password), `GetCredentialUserName(DeviceIds)` (the stored user name the devices share,
  else empty: prefill of the dialog), `SetFirstPassword(device_ids, password)` (the context menu's "Set password", see
  "Main Window"; Operator: per device (`Oadm.Core.Devices.DeviceFirstPasswordService`, 8 at a time, 15 s per request)
  anonymous HTTPS first (a device stored with HTTPS keeps its pin, one stored with HTTP is asked over HTTPS, then HTTP),
  the anonymous `getAllUnrestrictedProperties` serial must be the record's ("Another device answers at this address.
  Nothing was changed."), systemready must say `needsetup=yes` right before writing (else "The device already has a
  password. Nothing was changed.", nothing sent), then pwdgrp.cgi add root exactly like the add page (POST body only);
  a refusal is "The device rejected the password: <device text>."; CertificateChanged devices are refused without a
  request; the devices that took it get root + password stored in one transaction, their client dropped and a full refresh
  queued; reply `results` {device_id, ok, message} in request order; audit "First password set" {"root", "3 devices, 2
  set"}, never the password; INVALID_ARGUMENT without devices or for a password that is not 1-64 printable ASCII),
  `GetPassphrasePolicies(DeviceIds)` (only devices of the caller's view, else NOT_FOUND before any device is asked; anonymous systemready per device: `policies` {device_id, policy none / length /
  complex, empty when unreadable}; the dialog's hint).
- `DiscoveryService`: `StartZeroConf`, `StartRangeScan(from, to)`, `ProbeAddress(address, user_name = 2,
  password = 3, expected_serial = 4)` (one entered IP or host name, optional port/scheme; INVALID_ARGUMENT for an unusable or
  unresolvable address, only one of user name (1..64) and password (1..256), or an expected serial that is not 12 hex;
  the optional credentials of an imported line belong to that session only: tried first, then the credential list,
  server memory only, never saved to the list, never logged; `DiscoveryAuthenticator.AddSessionCredential`; with
  `expected_serial` (a line of an ADM export, `SetExpectedSerial`) a device with another serial gets no password at all:
  UNREACHABLE "Another device answers at this address (X, expected Y). No password was sent.", RetryAuth refused), `WatchDiscovered` (stream; every device with its automatic login result
  `auth_state = 14` (PENDING, AUTHENTICATED, PASSWORD_NOT_SET, LOGIN_FAILED, UNREACHABLE,
  ALREADY_ADDED), `auth_user_name = 15`, `credential_id = 16` ("list:<id>",
  "entered"), `auth_detail = 17`, `passphrase_policy = 18`, `entered_address = 19`, `dns_name = 20` (the verified DNS name of the
  address, see "Add Devices Page"); every scan
  (zero-conf after `Discovery.ZeroConfSeconds`, range scans, address probes, or `StopScan`) ends with
  scan_finished and the stream ends once every login of the session finished; watching a finished
  session again replays its devices with their current login result, then streams the logins still
  running), `StopScan` (ends the scan early, the session and its devices stay; unknown ids ignored),
  `Stop` (forgets the session; the add page calls it on close).
- `AddDevicesService`: `RetryAuth(session_id, discovered_id, user_name, password,
  save_to_credential_list, related_session_ids = 6)` (returns the updated DiscoveredDevice, also pushed
  to the stream; on success the credential is tried on the failed devices of the session and of the
  related sessions, see "Add Devices Page"; NOT_FOUND, FAILED_PRECONDITION factory default / already
  added, INVALID_ARGUMENT),
  `Commit(session_id, discovered_ids, initial_passwords map = 6, initial_root_password, use_host_name = 3)` (the page's
  "Use DNS names instead of IP addresses"; reply:
  `device_ids`, `results = 3` with discovered_id, device_id, status), `Prepare` (legacy, unused).
  See "Add Devices Page".
- `TaskService`: `ListTaskPlugins(ListTaskPluginsRequest)` (context-menu entries incl. those contributed by Core
  plugins; `TaskPluginInfo.display_name` normalized by the server, `group = 8` never empty; runnable sets
  cached by `TaskPluginRunnableCache` per device-table version, at most 10 s old; with `compact = 1` a
  plugin carries the shorter of `runnable_device_ids` or `runnable_on_all_except = 9` +
  `not_runnable_device_ids = 10`: 74 KB instead of 1.8 MB for 5,000 devices x 10 plugins; `not_runnable_groups = 11`
  (`NotRunnableGroup` {reason, device_ids, count, other_devices}): why the plugin cannot run on the other devices
  (`ITaskPlugin.NotSupportedReason`, computed in the same cache), grouped by reason, most devices first, every id sent
  once: with `runnable_on_all_except` the not-runnable ids are sorted by group and each group takes its `count` of them,
  otherwise the most common reason is `other_devices` (no ids) and the rarer ones list their ids; 5,000 devices x 15
  plugins: compact reply 109 KB, the reasons of the "all except" plugins 833 bytes), `Run(pluginId, deviceIds, payloadJson)` (one task per device, reply `task_ids`;
  `task_id` is deprecated = first id; all tasks of a Run written in one transaction), `List(ListTasksRequest)`
  (newest first; `limit`/`offset` paging, `TaskList.total_count = 2`; limit 0 = all, legacy),
  `Watch(WatchTasksRequest)` (stream: snapshot of every active task plus the newest `snapshot_limit`
  (0 = all), `SNAPSHOT_END` (kind 4, no task) when `snapshot_end_marker`, then live changes), `Cancel`, `Delete`,
  `DeleteAll` (cancels and waits for active tasks, then one bulk delete), `GetLog(taskId)` (per-task log, oldest first, live while running); `TaskInfo` carries
  `repeated TaskStep steps = 13` (index, name, `TaskStepState`, detail, progress, started, finished) and
  `current_step_index = 14` (-1 none),
  `Query(pluginId, deviceId, method, payloadJson)` (read-only `ITaskPluginQuery` for task
  dialogs, 30 s timeout; NOT_FOUND, UNIMPLEMENTED, FAILED_PRECONDITION for an incompatible device,
  changed certificate or rejected credentials, UNAVAILABLE, DEADLINE_EXCEEDED, INVALID_ARGUMENT;
  the status detail is the user message).
- `FileService`: `Upload` (client stream: header {name, size} then 256 KB data chunks; returns
  id, name, size, SHA-256; INVALID_ARGUMENT, RESOURCE_EXHAUSTED over `Uploads.MaxMegabytes`, over the 10 GB of
  all uploads together or when less than 1 GB of disk would stay free, see "Security and access" 4),
  `Delete(fileId)`. Uploads live in `<datafolder>/uploads/<id>.bin` + `<id>.json`, are deleted
  after `Uploads.RetentionHours` (checked every 15 min) and reach tasks through `IUploadedFiles`.
- `PluginService`: `ListCorePlugins` (navigation pages; `CorePluginInfo.no_page = 4` for a core plugin without a page,
  `ICorePlugin.HasPage` false: the client adds no rail entry), per-plugin generic
  `Invoke(pluginId, method, payloadJson)` for Core plugin UI pages (NOT_FOUND unknown plugin or
  object, FAILED_PRECONDITION not running, INVALID_ARGUMENT for an `ArgumentException` of the
  plugin, INTERNAL otherwise; the status detail is the message), `Watch(plugin_id)` (stream of `PluginEvent`
  {plugin_id, topic, payload_json} the plugin publishes through `ICorePluginContext.Events` from the call on; NOT_FOUND
  unknown plugin; `Oadm.Core.Plugins.PluginEventHub` fans out with 256 events buffered per watcher, oldest dropped),
  `Download(InvokeRequest)` (stream of `DownloadChunk` {file_name, length (-1 unknown), data}: a file of a core plugin that
  implements `ICorePluginDownloads`, e.g. a recording export passed on from a camera; the first message carries the name
  and length, every data message at most 256 KB, uncompressed, nothing stored on the server; the permission, the audit
  (detail `PluginDownload.AuditDetail`) and the errors of Invoke; UNIMPLEMENTED for a plugin without downloads; the
  download is disposed at the end or when the client goes away; tests `PluginDownloadTests`).
  Users: "Snapshot report", "VAPIX Commander", "NTP server", "DHCP server", "PKI", "Metadata Monitor", "Hardening scan",
  "Image Health Dashboard", "I/O Monitor", "Device triggers", "Dashboards", "Edge Recording Export" (search; its downloads through
  `Download`), "System report" (from its toolbar button through `IToolbarContext.InvokePluginAsync`).
  `ListPackages` (every loaded plugin package with id, display name, version, enabled, enabled_by_default, has_page,
  menu_task_count, always_on, description, pro; the list's pro_active; any user) and `SetPackageEnabled(id, enabled)`
  (Admin; NOT_FOUND unknown package; FAILED_PRECONDITION always on, or a PRO plugin without an active license; reply:
  the whole list), see "Plugins on and off".
- `SettingsService`: `Get`, `Set` (`ServerSettings.zero_conf_seconds = 8`, 0 keeps the stored value; read-only
  `server_version = 21` in both replies for the About page),
  `ListCredentials`, `AddCredential(user_name, password)`
  (INVALID_ARGUMENT, RESOURCE_EXHAUSTED over 20 entries; an identical pair returns the existing
  entry; a new entry is tried on the failed devices of every open add session), `RemoveCredential(id)` (NOT_FOUND), `RevealCredential(id)` (reply `RevealedCredential { password = 1 }`:
  the stored password of one credential list entry for the Credentials page eye and copy buttons; NOT_FOUND;
  logged as "Credential list password of <user> revealed", never the password). Credential entries carry id,
  user name and created time, never a password.
- `LiveViewService`: `Watch(device_id, max_width, max_height, fps, accepted_codecs, camera)`
  (stream of encoded access units), `ListSources(device_id)` (view areas / sensors / channels),
  `GetOptics(device_id)` (live view permission; zoom and focus state, `supported` false for a camera without them, one
  OADM cannot log in to or that does not answer, never an error), `SetOptics(device_id, one of magnification,
  focus_position, focus_window, autofocus, reset_focus)` ("Optics Control"; INVALID_ARGUMENT unless exactly one,
  FAILED_PRECONDITION with the camera's refusal; audited "Changed zoom or focus"), `GetRtspPort(device_ids)` (any user:
  the shared port, 554 = default, `mixed`) and `SetRtspPort(device_ids, port)` ("Optics Control"; 0 or 554 = default;
  stored in OADM, the devices are not changed; audited "Changed RTSP port in OADM"), `GetPtz(device_id, camera)` (live
  view permission; presets, home, move, zoom, center of a PTZ channel, `supported` false otherwise, never an error) and
  `MovePtz(device_id, camera, move, x, y, value, preset)` ("Optics Control"; not audited, a drag sends several a second;
  FAILED_PRECONDITION "The camera refused: <camera text>"). See "Live view".
- `EventService` (`events.proto`, event rules, see "Event rules").
- `ServerLogService` (`logs.proto`, permission `logs.server`): `List` (the server's log files, a page newest first), see
  "Logs page".
- `DeviceWebService` (`deviceweb.proto`, the Web View relay, see "Web View"): `Open(device_id)` (Operator; refuses
  Certificate changed, Credentials required, Password not set with the shared text; audit "Opened web interface"),
  `Exchange` (one bidirectional call per HTTP request or WebSocket of the embedded browser; the server sends it to the
  device with the stored login and the pinned certificate; FAILED_PRECONDITION for the refused statuses of Open (no
  request reaches the device) and while the Web View plugin is turned off, INVALID_ARGUMENT for a path that leaves the
  device; a user's requests to a device without an Open in the last 8 hours write the audit entry once; request bodies
  over 16 MB are spooled to `<datafolder>/web-relay/`, at most 4 at once, 1 GB of disk kept free, the folder emptied once
  per start; a `Location` to the device's host on any scheme or port becomes a path).
- `ComplianceService` (`compliance.proto`, see "Compliance"): `GetCatalog`, `ListChecks`, `WatchChecks`, `GetCheck`,
  `SaveCheck`, `DeleteCheck`, `SetCheckEnabled`, `ListChoices`, `CheckNow`, `WatchResults` (snapshot in batches of 5,000 with
  `snapshot_end`, then changes; `checking` while a result was asked for), `SetEnabled` (compliance on / off, off by default;
  `ComplianceCheckList.enabled`).
- `TagService` (`tags.proto`, device tags, see "Device tags"): `List` (every definition sorted by name with its
  `device_count`, then names used on devices without a definition, `defined = false`), `Watch` (the whole `TagList` on
  subscribe and after every change of the definitions; small, no ChangeBatcher needed), `Create(name, color)`
  (ALREADY_EXISTS, INVALID_ARGUMENT, RESOURCE_EXHAUSTED over 500 definitions; `TAG_COLOR_UNSPECIFIED` = next palette
  color), `Update(name, new_name, color)` (Admin; rename and / or recolor, empty / UNSPECIFIED keep; a rename rewrites
  every device's tag in one transaction; an undefined name gets a definition), `Delete(name)` (Admin; removes the tag
  from every device in one transaction, reply `devices_changed`), `SetDeviceTags(device_ids, add, remove)` (one call
  and one transaction for the whole selection, names in `add` without a definition are created with the next palette
  color, unknown ids ignored, RESOURCE_EXHAUSTED when a device would get more than 20 tags; reply `devices_changed`,
  `created`). Every write publishes one Updated device change per changed device.
- `LicenseService` (`license.proto`, see `docs/spec/licensing.md`): `GetLicense` (every signed-in user), `ImportLicense`
  and `RemoveLicense` (Admin, audited; INVALID_ARGUMENT with the reason).
- `SupportService` (`support.proto`, see `docs/spec/settings-backup.md` "Support report"): `CreateReport` (Admin, audited
  "Created support report"; stream of `DownloadChunk`: name and length first, 256 KB chunks, uncompressed).
