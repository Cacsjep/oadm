# Device tags

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Built-in feature (not a plugin). Technicians label devices ("Building A", "PTZ") and work
with the labels in the grid, the search, the export and the import; plugins read them through `IDeviceInfo.Tags`.

- **Definitions**: a name (1..64 characters, trimmed, unique case-insensitive, no ";" and no control characters) and a
  color of the fixed palette of eight: Blue, Green, Teal, Amber, Orange, Red, Pink, Violet (theme brushes
  `Oadm.Tag.<Name>Brush`, readable as text on the dark cards; `Oadm.Tag.NeutralBrush` grey for names on devices without
  a definition, e.g. older data). A tag created without a color gets the next palette color (definition count modulo 8).
  Server: `TagDefinition` / `DeviceTagStore`, gRPC `TagService` (see "gRPC services"). Roles: operators tag devices and
  create tags; rename, recolor and delete are Admin only. Every change is audited.
- **Client store**: `Tags/TagStore` (fed by `TagService.Watch` in `ServerConnection`, owned by `DeviceStore.Tags`) keeps
  one shared `TagInfo` per name (case-insensitive); device rows hold `TagChips` of those shared objects, so a recolor
  updates every chip and group header without touching 5,000 rows. `DeviceRowViewModel.Tags` / `TagsText`
  ("Building A; PTZ", sort key and export text); the device search also matches tag names.
- **Tags dialog** (`Tags/DeviceTagsWindow`, `DeviceTagsViewModel`; context menu "Tags"): title bar, one card with the
  intro ("Tags of 12 selected devices. ..."), `ui:SearchBox` + **New tag** (inline editor: Name in `ui:FormField` with the
  error below the field, Color with `c:TagColorPicker` = eight round swatches, Create tag / Cancel; created at once on the
  server and checked), the virtualized tag list (three-state check box: all / some / none of the selected devices, a
  click makes some or none all and all none; color dot, name in the tag color, "42 devices" = all devices with the tag;
  administrators also get Rename or recolor (inline editor below the list, saved at once) and Delete tag (confirmation in
  `ui:MessageWindow`: "Remove tag X from 42 devices?"), a failed server call directly below the list, footer Cancel /
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
- **Tests**: `tests/Oadm.Server.Tests/TagServiceTests` + Perf `TagScaleTests` (5,000 devices: SetDeviceTags 4.4 s, rename
  2.2 s, delete 2.0 s, list 35 ms); `tests/Oadm.Client.Tests/DeviceTagsTests`, `DeviceTagsHeadlessTests`, Perf
  `TagGroupScaleTests`.
