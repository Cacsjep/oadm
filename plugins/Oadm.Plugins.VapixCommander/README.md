# VAPIX Commander (`oadm.vapix-commander`)

Core plugin (+ `.Client`), rail page **VAPIX Commander** (group Automation, icon `command`), on by default. A technician
picks one or more VAPIX commands, fills their fields and rolls them out to any number of devices, or sends one now to
one device (Postman-like). Command format v1 and JSON schema: `Schema/command-format.md` + `Schema/command.schema.json`
(the contract; category `Custom` for saved commands, optional `response.errorPattern`). The bundled commands:
`Library/README.md`.

## Library

Every `Library/*.json` next to the plugin assembly (`ICorePluginContext.PluginDirectory`, fallback
`AppContext.BaseDirectory/Library`), loaded once at start with strict JSON (unknown properties are errors) and
`CommandValidator`; broken files and invalid or duplicate commands are skipped, logged and listed on the page ("N
library entries could not be loaded"). The bundled library (161 commands) is checked by
`tests/Oadm.Plugins.VapixCommander.Library.Tests` and `BundledLibraryTests` (every command loads and validates with this
engine).

## Saved commands

Server side in the plugin setting `savedCommands` (one JSON document, at most 500), shared by all clients, editable,
deletable, export / import as a library file (`formatVersion` 1, category of the commands or `Custom`; import keeps valid
ids, the same id replaces, invalid ones are reported). Saving with an id (`SaveCommandRequest.PreviousId`) replaces that
command; renaming keeps it. Password field values are encrypted with the server master key (`ICorePluginContext.Secrets`,
AES-256-GCM, purpose `vapix-commander:<id>:<field>`), never listed, never exported; without a protector they are dropped.
A saved command lists `storedSecretFields`; leaving such a field empty uses the stored value.

## Engine (server)

The payload stays in memory only; password values are never logged and are masked as `***` in request lines and bodies
shown to the user.

- `FieldValues` checks values per type (integer, number, boolean, enum, string / password with pattern and min / max
  length; numbers min / max) before anything is sent.
- `CommandRenderer` fills `{{field}}` in the path (URL-escaped), query (escaped, "," and ":" kept), headers (no
  Authorization / Cookie / Host), body (json: a value that is exactly `{{field}}` is typed; form; text; xml: XML-escaped;
  SOAP content type for envelopes) and extract paths. Booleans are yes / no for `param-cgi`, true / false elsewhere,
  unless `trueValue` / `falseValue`. Timeout via `VapixRequestOptions.Timeout` (default 15 s, at most 600 s). Paths must
  stay on the device (`CommandValidator.IsDevicePath`, checked again after the fields are filled in).
- `CommandExecutor` sends with the factory's cached client (never disposed by the plugin), decodes the body as UTF-8
  itself (Axis sends `charset=utf8`), shows binary answers as "image/jpeg, 123.4 KB", pretty-prints JSON / XML (cut at
  256 KB) and interprets per response kind (`ResponseInterpreter`, table in `command-format.md`): the kind decides how
  success, errors and extracted values are read, because Axis devices often answer an error with HTTP 200. Values only
  some models have are `"optional": true` and left out when missing (WDR, an IR cut filter, audio input and output, a zoom
  lens, values of a newer AXIS OS, I/O port 0); a param.cgi group the device lacks (Network.RTSPS on AXIS OS 11.9) is no
  error when all its values are optional.
- Errors: the device text (`# Error: ...` line, JSON `message (code N)`, REST error / problem+json, SOAP fault, Axis
  `GeneralError`, first text line, HTML title) or the transport text ("Timeout after 15 s", "Unreachable - Connection
  refused", "Unreachable - no route to the device", "TLS/certificate error: ...", "Unauthorized - HTTP 401 (check the
  credentials)", "Not Found - HTTP 404 (API not available on this firmware)", "Bad Request - HTTP 400: <device text>",
  "Server error - HTTP 500: <device text>"). HTTP 204 / an empty 2xx is success.

## Rollout

The page method `rollout` validates every command and value first (nothing starts on an error); writes need `confirmed`;
then `ITaskRunner.RunAsync` with the contributed, hidden task plugin `oadm.vapix-commander.run` (`ShowInMenus = false`;
one task per device, owner = the client user; task name: the command name, or "<first command> +N more").

- Steps: **Check compatibility** (a fresh `GetApiListAsync` when a command writes or nothing is cached, else the cached
  API list; detail "All 3 commands supported (fresh API list)"), then one step per command named like the command ("(2)"
  for repeats) with the extracted result ("Product: P3265-V") or the error as detail.
- A requirement means the same major version and at least the minor one; `"minVersion": "any"` accepts the API in any
  version (CGIs whose interface does not follow the listed major: PTZ `ptz-control` 1.x / 2.x, `privacy-mask` 2.x / 3.x).
  The Applications commands require `application` (identified by a parameter, added to the API list when the device does
  not name it). `LibraryTests` checks every requirement against the API list of a real AXIS OS 12.11 camera.
- An incompatible command fails its step "Missing API x. Nothing was sent."; a failed command does not stop the others on
  that device; the task ends Failed with "<command>: <error>" (+N more failed) as the device message; every request is
  logged (masked).
- **Stop on first error** (`RolloutRegistry`, server memory): the first failure aborts the rollout; the failing device
  skips its remaining commands ("Not run: stopped on the first error."), tasks not started yet are cancelled ("Cancelled
  before start."), running devices finish their current command, skip the rest ("Not run: the rollout stopped after an
  error on another device.") and end Cancelled.

## Page methods

`CommanderMethods` (camelCase JSON, records in `CommanderContract.cs`): `listLibrary`, `listSaved`, `save`, `delete`,
`export`, `import`, `tryRequest` (deviceId, command ref library / saved / inline, values, confirmed; read-only commands
run at once, writes need `confirmed` and re-check a fresh API list; refuses Certificate changed devices and
cached-incompatible ones; returns status, reason, duration, headers, the pretty body, the request line and the
interpreted result), `checkCompatibility` (device ids x command refs against the cached `device.Apis`: Compatible,
Missing API, Version too old, API list not read yet, Needs a video device), `rollout`. User-level problems come back as
`error` in the reply, not as gRPC errors. VAPIX Commander methods are Operator; `tryRequest` and `rollout` are audited.

## Page

`HasOwnCards`: three cards, shared controls only, compact icon buttons (`Button.toolbar.iconOnly` + `ui:OadmIcon` with a
tooltip); the shared tasks pane sits below the page (`ShowTasksPane`), so a started rollout shows no message.

- **Library**: **+** "New command" and Import icons right of the title; library messages (import, export, save, delete) in
  a status chip under the title; a search box filling the card width (`ui|SearchBox.stretch`); a tree Built-in / Saved by
  category without badges (write / dangerous are in the tooltip, the Kind column, the header of the selected command
  "Write · dangerous · <description>" and the run confirmation). A click on a command adds it to the rollout set, a click
  on a group expands or collapses it (acting when the button goes down: selecting a row can scroll the tree and lose a
  tap; a double click is one action); Enter does the same for the selected node. Saved commands show Edit (pen), Export
  and Delete icons on hover (Delete key on a selected saved command).
- **Rollout set**: title, "Send to <device>" and **Send** for the selected row; a grid #, Command, Category, Kind, Source
  and an icon column with Move up, Move down, Save with values (a saved copy in Custom with the entered values) and
  Remove, disabled when not possible; "Remove all" in that column's header. Keyboard: Delete removes the selected row,
  Ctrl+Up / Ctrl+Down move it. The selected row's field form is below, checked like the server while typing
  (`ui:PasswordBox` for passwords). **Send** shows the result card below (`TryResultView`).
- **Result card** (`TryResultView`, also in the request builder): status chip, "HTTP 200 OK · 84 ms · address", the
  interpreted result, the request line, body and headers in `ui:CodeView`s. The body language comes from the content
  type (else from the text: param.cgi key=value); JSON and XML are shown pretty-printed (2 spaces, also when the device
  sends them minified) with a **Pretty / Raw** switch (Raw = the exact device text, `TryOutcome.RawBody`, sent only when
  it differs), the content type and "Cut at 256 KB." next to it.
- **Target devices**: the Devices page selection preselected; the purple device button of the event rules ("Choose
  devices", "Selected devices: Tag Building A and 3 devices") opens the shared device picker (All devices, chosen devices
  and tags; a tag means the devices that have it when the rollout starts); summary
  "1,500 devices · 1,169 compatible · 214 not compatible · 117 not checked"; below it only the chosen devices with a problem
  (not reachable, or the first command problem "<command>: Missing API x (+N more)", tooltip every command). The run controls are docked at the bottom: a status chip only for errors, the summary "3 commands × 2 devices ·
  1 write", **Stop on first error** (default on), **Run on N devices**: the confirmation lists every command with its
  kind, the incompatible devices and the stop mode when a command writes; dangerous commands need a second confirmation.

## Request builder

Its own dialog (`RequestBuilderWindow` / `RequestBuilderViewModel`, 1360x760).

- The Library's **+** asks first: "Start from scratch" (an empty request; the path field shows an example) or "Start from
  an existing command" (search + a virtualized list of every built-in and saved command, double click = Next; the copy is
  always saved as a new command, a saved one named "<name> (copy)"). The pen icon of a saved command opens it directly
  ("Edit command") and **Save** replaces it. There is no "Save as new": that is **+** from the saved command.
- Left: `RawEditorView` (`RawEditorViewModel`): Name, Category, Description, then method, path, query and header rows
  (the header table hidden for GET unless the command has headers; Make field and Remove icons in each row), body type and
  body (hidden for GET unless the command has one; `ui:CodeEditor`, JSON and XML colored while typing), timeout, response
  kind, the fields from `{{placeholders}}` typed by hand (a placeholder renamed or broken while typing keeps its field's
  label, type and value; a field's remove icon puts its value back in place of the placeholder, typed in a JSON body),
  "Requires API" prefilled from the path (param.cgi -> param-cgi 1.0, `/config/rest/<api>/v<n>` -> `<api> n.0`, known
  CGIs), "Changes the device" (prefilled from method and action) and "Dangerous". A loaded command keeps what the editor
  does not show (choices, limits, docs, extracts).
- Right: "Send to <device>" + **Send** (checks everything but name and category) and the result card.
- Footer: Cancel, Back (new command), Next / **Save** (disabled with the first problem as tooltip; a refused save left of
  it). An unsaved request cannot go to the rollout set: save it, then click it in the library.

## Scale

The problem list is virtualized; `Targets`, `SelectedTargets` and `ProblemTargets` are replaced as whole lists (one
reset), sorted by IPv4 address numerically; the choice is resolved in one O(n) pass (ids and tags in hash sets) after
every choice and device change; compatibility is computed on the client from each device's cached API list
(`Compatibility.Check`; the server checks a fresh list again before writes), no request per device. With 5,000 devices
All devices, the devices page selection, a tag choice and the compatibility each stay under 200 ms (test).

## Tests

`tests/Oadm.Plugins.VapixCommander.Tests`: rendering of every field type, typed JSON, booleans per kind, validation,
every response kind with error texts and transport mapping against a fake device, stop on first error through the real
task engine, saved commands (encryption, export, import), compatibility, the page and request builder view models, the
5,000-device timing, the bundled library and headless screenshots. `tests/Oadm.Client.Tests/CodeViewTests`;
`tests/Oadm.Server.Tests/VapixCommanderServerTests` (routing, hidden task); read-only hardware test
`VapixCommanderHardwareTests` (param.cgi list Brand and basicdeviceinfo, Send and a rollout on the development camera
through the in-process server); `ReaderHardwareTests` runs every read-only library command on every dev camera (passed on
AXIS OS 10.12, 11.9 and 12.11: P3265-V, M3215-LVE, M3206-LVE, P12 Mk III; the I/O port readers are skipped on a camera
without ports, "No light hardware found" is the right answer of a camera without lights). Not in fake mode.

## Permissions

`oadm.vapix-commander:show` (library, saved commands, compatibility), `:send` "Send to one device" (`tryRequest`),
`:rollout` "Run on many devices" (`rollout`) and `:saved` "Change saved commands" (`save`, `delete`, `import`). Send and
rollout are full device control. The page hides Send, Run, New command (unless send or saved), Import, edit / delete of
saved commands and "Save a copy" accordingly; the request builder hides Send and Save. Operator default: all. The event
rule action "Run VAPIX command" runs as the server and follows "Change event rules".
