# VAPIX Commander (`oadm.vapix-commander`)

Core plugin (+ `.Client`) with the rail page **VAPIX Commander** (group Automation, icon `command`), on by default. A
technician picks VAPIX commands, fills their fields and rolls them out to any number of devices, or sends one to one
device. Rules: [Bundled plugins](../../docs/spec/bundled-plugins.md#vapix-commander-core-plugin). Command format and
response kinds: [Schema/command-format.md](Schema/command-format.md) + `Schema/command.schema.json` (the contract). The
bundled commands: [Library/README.md](Library/README.md).

## Library

Every `Library/*.json` next to the plugin assembly (`ICorePluginContext.PluginDirectory`, fallback
`AppContext.BaseDirectory/Library`), loaded once at start with strict JSON (unknown properties are errors) and
`CommandValidator`. Broken files and invalid or duplicate commands are skipped, logged and listed on the page ("N
library entries could not be loaded"). 176 bundled commands in 16 files.

## Saved commands

- Plugin setting `savedCommands` (one JSON document, at most 500, `SavedCommandStore.MaxCommands`), shared by all
  clients; export / import as a library file (`formatVersion` 1, the commands' category or `Custom`; import keeps valid
  ids, the same id replaces, invalid ones are reported).
- Saving with an id (`SaveCommandRequest.PreviousId`) replaces that command; renaming keeps it.
- Password values are encrypted with the master key (`ICorePluginContext.Secrets`, purpose
  `vapix-commander:<id>:<field>`), never listed, never exported; without a protector they are dropped. A saved command
  lists `storedSecretFields`; leaving such a field empty uses the stored value.

## Engine (server)

The payload stays in memory only; password values are never logged and are masked as `***` in request lines and bodies.

- `FieldValues` checks every value per type before anything is sent (integer, number, boolean, enum, string / password
  with pattern and length; numbers min / max; file: {"name", "data"} base64, at most 1 MB, a name without folders or
  quotes).
- Files: a `file` field is filled with **Choose file** and travels inline with the command, so Send, rollouts, saved
  commands ("Save with values" keeps the file) and the event rule action carry it; there is no upload store. It may only
  be the `file` of a `multipart` part (`CommandValidator.ValidateFileUses`); the request line shows `file "profiles", 2
  KB`, never the bytes. Used by Siren and light **Upload siren and light profiles**.
- `CommandRenderer` fills the fields (rules in [command-format.md](Schema/command-format.md#placeholders)). Timeout via
  `VapixRequestOptions.Timeout` (default 15 s, at most 600 s). Paths must stay on the device
  (`CommandValidator.IsDevicePath`, checked again after the fields are filled in).
- `CommandExecutor` sends with the factory's cached client (never disposed by the plugin), decodes the body as UTF-8
  (Axis sends `charset=utf8`), shows binary answers as "image/jpeg, 123.4 KB", pretty-prints JSON / XML (cut at 256 KB)
  and interprets the answer per response kind (`ResponseInterpreter`): Axis devices often answer an error with HTTP 200.
- Errors: the device text (`# Error: ...` line, JSON `message (code N)`, REST error / problem+json, SOAP fault, Axis
  `GeneralError`, first text line, HTML title) or the shared transport texts of `DeviceMessages` ("Timeout after 15 s",
  "Unreachable - Connection refused", "Not Found - HTTP 404 (API not available on this firmware)", ...). HTTP 204 / an
  empty 2xx is success.

## Page

`HasOwnCards`: three cards, shared controls only, icon buttons `Button.toolbar.iconOnly` + `ui:OadmIcon` with a tooltip;
the shared tasks pane sits below the page (`ShowTasksPane`), so a started rollout shows no message.

- **Library**: New command (**+**) and Import right of the title; library messages (import, export, save, delete) in a
  status chip under the title; a stretched `ui:SearchBox`; a tree Built-in / Saved by category without badges (write and
  dangerous are in the tooltip, the Kind column, the header of the selected command "Write · dangerous · <description>"
  and the run confirmation). A click on a command adds it to the rollout set, a click on a group expands or collapses it
  (acting on pointer down: selecting a row can scroll the tree and lose a tap; a double click is one action); Enter does
  the same. Saved commands show Edit, Export and Delete on hover (Delete key on a selected saved command).
- **Rollout set**: "Send to <device>" and **Send** for the selected row; a grid #, Command, Category, Kind, Source and an
  icon column with Move up, Move down, Save a copy with these values (a saved copy in Custom) and Remove, disabled when
  not possible; Remove all in that column's header. Keyboard: Delete removes the selected row, Ctrl+Up / Ctrl+Down move
  it. The selected row's field form is below, checked like the server while typing (`ui:PasswordBox` for passwords).
  **Send** shows `ui:ProgressRow` "Sending the request to 10.0.0.48", then the result card.
- **Result card** (`TryResultView`, also in the request builder): status chip, "HTTP 200 OK · 84 ms · address", the
  interpreted result, the request line, body and headers in `ui:CodeView`. The body language comes from the content type
  (else from the text: param.cgi key=value); JSON and XML are pretty-printed with a **Pretty** / **Raw** switch (Raw = the
  exact device text, `TryOutcome.RawBody`, sent only when it differs), the content type and "Cut at 256 KB.".
- **Target devices**: the Devices page selection to start with; the device choice button (`Button.secondary` with the
  devices icon: "Choose devices", "Selected devices: Tag Building A and 3 devices") opens the shared device picker (All
  devices, tags, chosen devices; a tag means its devices when the rollout starts). Summary "1,500 devices · 1,169
  compatible · 214 not compatible · 117 not checked"; below it only the chosen devices with a problem (not reachable, or
  the first command problem "<command>: Missing API x (+N more)", tooltip every command). Docked at the bottom: a status
  chip for errors, "3 commands × 2 devices · 1 write", **Stop on first error** (on by default), **Run on N devices**. The
  confirmation lists every command with its kind, the incompatible devices and the stop mode when a command writes;
  dangerous commands need a second confirmation. While the server creates the tasks, `ui:ProgressRow` "Starting the
  rollout on 3,000 devices" stands above the button (Run and Send disabled).
- Scale: the problem list is virtualized; `Targets`, `SelectedTargets` and `ProblemTargets` are replaced as whole lists
  (one reset), sorted by IPv4 address; the choice is resolved in one O(n) pass after every choice and device change;
  compatibility comes from each device's cached API list on the client (`Compatibility.Check`), no request per device.
  At 30,000 devices All devices, the Devices page selection, a tag and the compatibility each stay under 200 ms
  (`ScaleTests`).

## Request builder

Its own dialog (`RequestBuilderWindow` / `RequestBuilderViewModel`, 1360x760).

- New command asks first: **Start from scratch** (an empty request) or **Start from an existing command** (search + a
  virtualized list of every built-in and saved command, double click = Next; the copy is always saved as a new command,
  a saved one named "<name> (copy)"). Edit on a saved command opens it directly ("Edit command") and **Save** replaces
  it; a new command from it is New command.
- Left: `RawEditorView`: Name, Category, Description, then method, path, query parameters and headers (headers hidden
  for GET unless the command has some), body type and body (hidden for GET unless the command has one; `ui:CodeEditor`),
  timeout, response kind, the fields from `{{placeholders}}` (a placeholder renamed or broken while typing keeps its
  field's label, type and value; removing a field puts its value back in place of the placeholder), "Requires API"
  prefilled from the path (param.cgi -> param-cgi 1.0, `/config/rest/<api>/v<n>` -> `<api> n.0`, known CGIs), "Changes
  the device" (prefilled from method and action) and "Dangerous (restart, reset, network, users)". A loaded command keeps
  what the editor does not show (choices, limits, docs, extracts).
- Right: "Send to <device>" + **Send** (checks everything but name and category), the progress row, the result card.
- Footer: Cancel, Back (new command), Next / **Save** (disabled with the first problem as tooltip; a refused save left of
  it). An unsaved request cannot go to the rollout set: save it, then click it in the library.

## Task steps

`rollout` validates every command and value first (nothing starts on an error); writes need `confirmed`. Then
`ITaskRunner.RunAsync` with the hidden task plugin `oadm.vapix-commander.run` (`ShowInMenus = false`; one task per
device; task name: the command name, or "<first command> +N more").

| Step | What happens |
|---|---|
| Check compatibility | a fresh `GetApiListAsync` when a command writes or nothing is cached, else the cached API list; detail "All 3 commands supported (fresh API list)" |
| one step per command, named like the command ("(2)" for repeats) | the extracted result ("Product: P3265-V") or the error as detail |

- A requirement means the same major version and at least the minor one; `"minVersion": "any"` is explained in
  [command-format.md](Schema/command-format.md#requires).
- An incompatible command fails its step "Missing API x. Nothing was sent."; a failed command does not stop the others
  on that device; the task ends Failed with "<command>: <error>" (+N more failed); every request is logged (masked).
- **Stop on first error** (`RolloutRegistry`, server memory): the first failure aborts the rollout; the failing device
  skips its remaining commands ("Not run: stopped on the first error."), tasks not started yet are cancelled ("Cancelled
  before start."), running devices finish their current command, skip the rest ("Not run: the rollout stopped after an
  error on another device.") and end Cancelled.

## Methods

`CommanderMethods` (camelCase JSON, records in `CommanderContract.cs`). User-level problems come back as `error` in the
reply, not as gRPC errors. `tryRequest` and `rollout` are audited.

| Method | What it does |
|---|---|
| `listLibrary`, `listSaved` | the bundled and the saved commands |
| `save`, `delete`, `import`, `export` | the saved commands |
| `tryRequest` | deviceId, a command ref (library / saved / inline), values, confirmed: read-only commands run at once, writes need `confirmed` and re-check a fresh API list; refuses Certificate changed and cached-incompatible devices; returns status, reason, duration, headers, the body, the request line and the interpreted result |
| `checkCompatibility` | device ids x command refs against the cached `device.Apis`: Compatible, Missing API, Version too old, API list not read yet, Needs a video device |
| `rollout` | see Task steps |

## Event rule action "Run VAPIX command"

`RunCommandAction` (`oadm.vapix-commander.command`, group Devices): a saved command (with its stored values and
passwords) or a library command that needs no input, when the rule turns on or fires, and optionally another "When the
rule turns off". The same device safety as a rollout: the command must fit the cached API list, a write re-checks a
fresh one, dangerous commands need "Allow dangerous commands". Available while the plugin is on. Spec:
[event rules](../../docs/spec/event-rules.md).

## Permissions

`oadm.vapix-commander:show` (library, saved commands, compatibility), `:send` "Send to one device" (`tryRequest`),
`:rollout` "Run on many devices" (`rollout`) and `:saved` "Change saved commands" (`save`, `delete`, `import`). Send and
rollout are full device control. The page hides Send, Run, New command (unless send or saved), Import, Edit and Delete of
saved commands and Save with values accordingly; the request builder hides Send and Save. Operator default: all. The
event rule action runs as the server and follows "Change event rules".

## Device groups

Page methods get the user's call (`InvokeAsync(caller, ...)`); see [device groups](../../docs/spec/device-scope.md).

- `tryRequest`, `checkCompatibility` and `rollout` reach devices, clients and tasks only through the call. For a group
  user an id outside the view refuses the whole request like an unknown id (NOT_FOUND "1 device is no longer
  available."), nothing is sent and no task starts.
- `save`, `delete` and `import` change the saved commands everyone shares: `RequiresAllDevices`. The page hides New
  command, Import, Edit, Delete and Save with values for group users (`MaySaved` / `MaySave` also need `HasAllDevices`).
- Stored passwords of a saved command are only for users with all devices; a group user who leaves such a field empty
  gets `CommanderTexts.SavedPasswordsRefused` ("Only users with all devices can use the saved passwords. Enter the
  passwords."). The event rule action is for users with all devices like every event rule.
- The owner of rollout tasks and saved commands is the caller's user name, never a name the page sends.
- A rollout belongs to the user who started it (`RolloutRegistry.Rollout.OwnerName`). A task started by hand through the
  task service that names another user's rollout id is refused before any step ("This rollout was started by another
  user. Nothing was changed."). Tasks the host starts without a user join as before.

## Tests

- `tests/Oadm.Plugins.VapixCommander.Tests`: rendering of every field type, typed JSON, booleans per kind, validation,
  files, every response kind with error texts and transport mapping against a fake device, stop on first error through
  the real task engine, saved commands (encryption, export, import), compatibility, the event rule action, the page and
  request builder view models, `ScaleTests` (30,000 devices), `BundledLibraryTests`, `DeviceGroupTests` (`PluginCanary`
  with every method and its id fields, the owner, stored passwords, the page), headless screenshots.
- `tests/Oadm.Plugins.VapixCommander.Library.Tests`: the bundled library; see [Library/README.md](Library/README.md#tests).
- `tests/Oadm.Client.Tests/CodeViewTests`; `tests/Oadm.Server.Tests/VapixCommanderServerTests` (routing, hidden task).
- Read-only hardware test `VapixCommanderHardwareTests` (param.cgi list Brand and basicdeviceinfo, Send and a rollout on
  the development camera through the in-process server). Not in fake mode.
