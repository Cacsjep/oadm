# Live View (`oadm.live-view`)

Core plugin with a rail page in group Monitoring (icon `grid`; New view `gridAdd`), with `.Client`, off by default. Saved
views of several cameras at once, like the live view of a VMS: each view is a grid of cameras, opened in a tab. Spec:
[Bundled plugins](../../docs/spec/bundled-plugins.md#live-view-core-plugin).

## Decisions

| Topic | Decision |
|---|---|
| Storage | On the server, shared by every user (plugin setting `views`, JSON). A save carries the version it was made on; an older one is refused ("Another user changed the view X meanwhile. Click Cancel and make your changes again."). Every change is pushed to every open page (event `views` with the whole list). An unreadable stored list is kept as `viewsUnreadable` and the list starts empty. |
| Grid | 20 x 20 cells that always fill the tab (no scrolling); a camera is a rectangle of cells, at least 2 x 2; cameras never overlap. |
| Limits | 16 cameras per view (decoding stays smooth on an average PC), 200 views, names 1..64 characters, unique case-insensitive. |

## Page

| Topic | Decision |
|---|---|
| Page | Left card: **Views** (icon button New view; the list of names with Edit and Delete view (red, asks first) at the right of each row for "Change views"; a click opens the view's tab or brings it to the front) and **Cameras** (search, the devices with video grouped by tag, the shared `ui:DeviceTree`). Collapse / Expand at the bottom of the card, like the navigation rail: collapsed it is a narrow strip with New view and the views as icons (the name as tooltip); session only. 5 px between the tabs and the grid. Right card: one row Close all tabs (while editing also Name, Save, Cancel), then the tabs (`ui:PageTab`); below it the grid of the active view, edge to edge. A failed save stands below that row. |
| Editing | New view opens a tab in edit mode named "View N". The grid shows its cell lines; every camera gets a frame with its address and an X (remove); dragging inside the frame moves it, dragging an edge or a corner resizes it (the cursor shows which); everything snaps to the cells and stops at other cameras. While a camera is dragged from the tree, a frame with its address shows where it will land: the largest free square at the drop point (up to 10 x 10), else the largest free square nearest to it; a click on a camera puts it in the first free place. The channel chosen with the channel buttons while editing is saved with the camera. Cancel puts the stored view back (a new view closes); closing a tab with changes asks first. |
| Video | Each camera is the host's live video component (`ICorePluginClientContext.LiveVideo`): adaptive quality (small tiles ask for small resolutions), fullscreen with a double-click, the channel buttons, the controls button; the controls start closed in a tile and open in fullscreen. Only the active tab is visible, so the cameras of other tabs stop after 2 s and resume with the last picture. |
| One camera | Outside editing, a click on a camera in the tree opens it alone in its own tab, filling the tab (title: its address); a second click brings that tab to the front. Never edited or saved (no Edit), closes without asking (`ViewTab.IsSingleCamera`, `LiveViewPageViewModel.ClickCamera`). |
| Hover | The camera under the pointer gets a thin accent frame (theme `Border.tileHover` of the shared `ui:TileGrid`, not while editing: the edit frame has its own). |
| Removed devices | A camera OADM no longer manages keeps its place and shows "Removed device 1a2b3c4d" (shared `DeviceTexts.Removed`). |
| Other users | An open view that is not edited follows another user's change (cameras of the same device and channel keep their picture); a deleted view closes its tab. An edited view keeps the user's changes; its save is then refused (version). |

## Methods

| Method | Payload | Answer | Permission |
|---|---|---|---|
| `list` | none | `ViewList` {views: [`SavedView` {id, name, version, tiles: [{deviceId, camera, x, y, width, height}]}]}, sorted by name | show |
| `save` | `SaveViewRequest` {id (empty = new), name, version, tiles} | the stored `SavedView` (version + 1) | manage |
| `delete` | `DeleteViewRequest` {id} | none (an unknown id is nothing to do) | manage |

Errors are `ArgumentException` with the user text (INVALID_ARGUMENT): the texts are `ViewRules` (shared by the page,
which checks the name and the layout before it sends).

## Permissions

"Show the page" (automatic, Operator and Viewer: a monitoring page), **Change views** (`manage`: create, change, delete; Operator yes, Viewer no). The pictures need the host's "Live view" right (`devices.liveView`); without it the tiles say "Your role does not allow live view.". Save and delete are audited ("Plugin action").

## Device groups

Every view belongs to the device scope of the user who made it (`SavedView.owner`: a group's id, none = all devices; set by the server only). Users of a group list, change and delete only their group's views, and `list` and the `views` event show them only the cameras the group has now; users with all devices see every view. A view of another group answers like a deleted one ("The view no longer exists. Another user deleted it."), its delete is nothing to do. An edit keeps the owner (also an administrator's); a save refuses cameras outside the owner's devices (NOT_FOUND "1 device is no longer available.", nothing stored; views of all devices keep removed devices). Names are unique per owner. After a change, users with all devices get every view (`PluginEventAudience.AllDevices`), the users of the view's group their group's views (`DeviceScope`). A deleted group's views are seen only by users with all devices; an administrator's save gives such a view to all devices. A user with all devices who saves a group's view whose cameras left the group gets the save: those tiles are dropped and the answer says how many (`SavedView.dropped`, never stored; the page shows "Saved without 1 camera that is no longer in the device group."); a camera the save adds outside the group, and every outside id of a group user, is still NOT_FOUND. See [Device groups](../../docs/spec/device-scope.md).

## Tests

`tests/Oadm.Plugins.LiveView.Tests`: `DeviceGroupTests` (the plugin canary, views per group, an administrator keeps the
owner, cameras that left the group, a deleted group); storage, versions and every refusal of the server part; the drop placement and
moves of `ViewLayout` (the cameras of a view on the shared `TileLayout`; the grid itself is the shared `ui:TileGrid`, its
rules tested in `tests/Oadm.Core.Tests/Layout/TileLayoutTests`); the page (new view, drop, channel, save, other users, Cancel, close with changes, roles, removed
devices; Perf: the camera tree of 5,000 devices); a headless screenshot with fake-mode video (`plugin-live-view.png`,
`plugin-live-view-edit.png`). Fake mode (`FakeOadmApi.LiveView.cs`): the view "Entrance" with four cameras.
