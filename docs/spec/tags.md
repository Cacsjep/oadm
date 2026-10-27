# Device tags

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Built-in feature (not a plugin). Technicians label devices ("Building A", "PTZ") and work
with the labels in the grid, the search, the export and the import; plugins read them through `IDeviceInfo.Tags`.

- **Definitions**: a name (1..64 characters, trimmed, unique case-insensitive, no ";" and no control characters) and a
  color of the fixed palette of eight: Blue, Green, Teal, Amber, Orange, Red, Pink, Violet (theme brushes
  `Oadm.Tag.<Name>Brush`, readable as text on the dark cards; `Oadm.Tag.NeutralBrush` grey for names on devices without
  a definition, e.g. older data). A tag created without a color gets the next palette color (definition count modulo 8).
  Server: `TagDefinition` / `DeviceTagStore`, gRPC `TagService` ([grpc.md](grpc.md)). Permissions: "Tag devices" (tag
  devices and create tags, operators by default) and "Rename and delete tags" (rename, recolor, delete; not for operators by
  default, users with all devices only). Every change is audited.
- **Device groups** ([device-scope.md](device-scope.md)): `DeviceTagStore` is the only writer of device tags and holds the guard:
  `SetDeviceTagsAsync` takes `ViewedDevices` (the ids come from the caller's view; an unknown or outside id refuses the
  call, NOT_FOUND, for every user); for a view of a group no added or removed name may be a tag a device group uses
  (`TagInUseByGroupException`, PERMISSION_DENIED "A device group uses the tag X. Only users with all devices can add or
  remove it. Nothing was changed."). The guard reads the groups inside the write transaction, which holds SQLite's write
  lock from its start (BEGIN IMMEDIATE), so a group saved during the tagging commits after it. Rename, recolor and delete
  need all devices (`AccessRule.AllDevices`); a rename keeps the tag in its groups (groups name tags by id), a delete takes
  it out of them. `List` / `Watch` count only the devices of the caller's view and fill `DeviceTag.group_names` (the
  groups that use the tag) only for users with all devices; the client asks before deleting such a tag ("Used by device
  group Building B"). Every move of a device into or out of a group by tagging or deleting a tag is audited per group
  ("Left device group" / "Entered device group", target the group, detail "3 devices (+PTZ): serials"):
  `DeviceGroupMembership` (`CaptureAsync` before the write, so a deleted tag still resolves for the devices that left;
  `ChangesAsync` with the devices before and after; `WriteAsync`). The waiting list tags an added device through the queuing
  user's view, so the guard applies to file imports too.
- **Protected tags**: a core plugin whose settings act on devices by tag protects those tag names
  (`ctx.ProtectedTags.Protect(names, refusal, changeRefusal)`, [plugins.md](plugins.md); today the tags of PKI's policies,
  [pki.md](pki.md) "Policies").
  - **Add and remove**: only users with all devices add them to or remove them from devices (also an Operator with all
    devices and "Tag devices": they manage VLAN membership). For a view of a group `SetDeviceTagsAsync` refuses an added or
    removed protected name (by name, case-insensitive, with or without a definition) in the same transaction as the group
    guard (`ProtectedTagException`, PERMISSION_DENIED with the plugin's text: "The tag Floor 3 is used by a PKI policy. Only
    users with all devices can add or remove it. Nothing was changed."; a plugin text that fails or is empty: "Only users
    with all devices can add or remove the tag Floor 3. Nothing was changed."). Both refusals are
    `TagNeedsAllDevicesException` (the waiting list logs them like the group one).
  - **Rename and delete while the plugin runs**: refused for every user, administrators too (the plugin's rules keep tags
    by name and would silently stop matching): `UpdateAsync` with a new name (a change of case only is no rename:
    `TagNames.Comparer`) and `DeleteAsync` throw `ProtectedTagChangeException` (FAILED_PRECONDITION) with the plugin's text
    ("The tag Floor 3 is used by a PKI policy. Change the policy first. Nothing was changed.") or the host's "A plugin's
    settings use the tag Floor 3. Change them first. Nothing was changed."; a recolor is allowed. The client shows the text
    below the tag list.
  - **While the plugin is stopped, off or faulted** (also a lapsed PRO license, which turns a PRO package off) the rename
    or delete is allowed (nobody could change the plugin's settings): only a running plugin's set (`Live`) refuses it; the
    add and remove refusal for group users stays. The plugin's rules match by name, so a renamed or deleted tag matches
    nothing (fail closed) and a tag made again under the name is protected again.
  - A new protected set waits for tag writes in flight (`BeginTagWrite` in the group user's tagging, rename and delete;
    `Protect` returns once the writes that started before it ended), so no write checked against the older names commits
    after the new set is in force.
  - `ProtectedTagRegistry` (server, `Oadm.Core.Devices`) holds one set per plugin (name -> {refusal, change}). **A set is
    kept while the plugin is installed**: a stop, a fault, a package turned off or a lapsed PRO license only closes the
    plugin's views (late calls change nothing). The sets are stored in the server setting `Tags.Protected` (JSON plugin id
    -> name -> { "refusal", "change" }, "" = the default text; part of the database, so a backup and its restore keep them)
    and restored at server start right after the plugins are loaded, before any tag write (gRPC and the waiting list start
    later). A plugin's new `Protect` call replaces its set. A set is forgotten only when its plugin is removed: at server
    start, a stored plugin id that no loaded core plugin (on or off) has; while a plugin failed to load, every stored set is
    kept (it may still be installed). The writes run in the background, one at a time (the last state wins); the server
    stop waits for them (`FlushAsync`).
- **Client store**: `Tags/TagStore` (fed by `TagService.Watch` in `ServerConnection`, owned by `DeviceStore.Tags`) keeps
  one shared `TagInfo` per name (case-insensitive); device rows hold `TagChips` of those shared objects, so a recolor
  updates every chip and group header without touching 5,000 rows. `DeviceRowViewModel.Tags` / `TagsText`
  ("Building A; PTZ", sort key and export text); the device search also matches tag names.
- **Tags dialog** (`Tags/DeviceTagsWindow`, `DeviceTagsViewModel`; context menu "Tags"): title bar, one card with the
  intro ("Tags of 12 selected devices. ..."), `ui:SearchBox` + **New tag** (inline editor: Name in `ui:FormField` with the
  error below the field, Color with `c:TagColorPicker` = eight round swatches, Create tag / Cancel; created at once on the
  server and checked), the virtualized tag list (three-state check box: all / some / none of the selected devices, a
  click makes some or none all and all none; color dot, name in the tag color, "42 devices" = all devices with the tag;
  users with "Rename and delete tags" and all devices also get Rename or recolor (inline editor below the list, saved at once) and Delete tag (confirmation in
  `ui:MessageWindow`: "Remove tag X from 42 devices?", plus "Used by device group Building B." from `TagInfo.GroupNames` =
  `DeviceTag.group_names`), a failed server call directly below the list (OK shows the server's own refusal, e.g. the
  device-group guard for a group user), footer Cancel /
  **OK**: the changed check boxes as one `SetDeviceTags` call for the whole selection; nothing changed = no call.
- **Group mode** (toolbar "Group by tag", persisted per client): the grid shows a collapsible group per tag in tag
  name order and "No tag" last, header "● Building A · 42 devices" in the tag color (theme style
  `DataGrid.tagGroups DataGridRowGroupHeader`, chevron expander `ToggleButton.groupExpander`); the Tags column is hidden
  while grouping (the group header names the tag). **A device with several tags
  appears under every one of them**: the rows are `DeviceTagRow` wrappers (device x tag, `Tags/
  DeviceTagGrouping`), grouped by Avalonia's `DataGridCollectionView` with the groups as explicit `GroupKeys` (so the group
  order stays when the user sorts a column; sorting works within the groups). Every grid column binds through
  `IDeviceGridItem.Row`, so device rows and wrappers share the columns. The grid selection goes to `SelectedGridItems`;
  `SelectedDevices` holds each device once (status line, context menu, toolbar, actions). The wrappers are rebuilt
  (one reset, rows reused) only when the shown devices, a device's tags or the tag names change; status updates reach
  the grid through bindings. Measured with 5,000 devices x up to 3 tags (8,750 rows): build 24 ms, search + clear 18 ms,
  select all 8 ms, 1,000 tag changes 92 ms (view model); headless grid: group mode on + render 0.5 s, select all 0.5 s,
  1,000 tag changes while grouped and sorted 1.9 s (DataGridCollectionView regroups).
- **Export / import**: the device export has a "Tags" column ("Building A; PTZ"); the import (add page, Import from
  file) reads an optional "Tags" (or "Tag") column, ";"-separated, and assigns those tags to the devices that get added,
  one `SetDeviceTags` call per distinct tag set; missing definitions are created by the server with the next palette
  color. A tag longer than 64 characters (CSV and ADM export) is cut to 64 (`DeviceImportFile.CutTags`), never a
  problem of the line. In the grid a chip is at most 200 px wide and ends with "…"; the tooltip names every tag.
- **Fake mode**: Building A (blue), Building C (green), PTZ (amber), Outdoor (teal) on the sample devices; every
  `TagService` call works in memory (`FakeOadmApi.Tags.cs`).
- **Tests**: `tests/Oadm.Core.Tests/DeviceGroups/TagGuardTests` (guard, group save during tagging, membership audit),
  `tests/Oadm.Server.Tests/DeviceGroups/SharedCore/TagScopeTests`, `tests/Oadm.Server.Tests/TagServiceTests` + Perf `TagScaleTests` (5,000 devices: SetDeviceTags 4.4 s, rename
  2.2 s, delete 2.0 s, list 35 ms); `tests/Oadm.Client.Tests/DeviceTagsTests`, `DeviceTagsHeadlessTests`, Perf
  `TagGroupScaleTests`.
