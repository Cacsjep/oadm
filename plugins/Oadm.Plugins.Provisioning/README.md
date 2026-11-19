# Provisioning

Core plugin `oadm.provisioning`, rail page **Provisioning**, free, on by default. Sets up factory-new cameras that get an
address from OADM's DHCP server, while provisioning is started on the page. The spec is
[`docs/spec/provisioning.md`](../../docs/spec/provisioning.md); this file holds the decisions of the code.

Built: the server part with every node of the spec, the page methods and the tools; the page `Oadm.Plugins.Provisioning.Client` with the canvas and the profile editor.

## How it works

- **Profiles** (`Model/Profiles.cs`, setting `profiles`): a name and a straight list of `FlowNode`s (kind, settings
  record as JSON, Continue on error). One profile is active. `ProfileCheck.Check` gives one error per node (its place
  first, then its settings) and one warning per node; the editor (`checkProfile`), Save, Activate and Start run the same
  check. Save refuses only errors of the whole profile (name, the fixed nodes); a profile with node errors is a draft and
  cannot be active.
- **Session** (`Engine/ProvisioningEngine.cs`, setting `session`): Start copies the active profile into the session; changes
  to the profile apply at the next Start. Start is refused without a running DHCP server ("Turn on the DHCP server
  first."), clears the rows of the last session and takes the cameras that hold an active lease right away. The session
  (profile, reserved Set IP addresses by MAC, the last DHCP acknowledgement read) is stored until Stop.
- **Leases**: the DHCP server offers `IDhcpServerLeases` (SDK 0.15.0, read only). The engine reads the acknowledgements
  every 2 s by sequence number; a DHCP server that started again starts its numbers again, the engine notices. Only Axis
  MAC blocks (`AxisOui`, linked from the DHCP server) get a row. Provisioning never changes the DHCP server.
- **Rows** (records collection `cameras`, one per MAC): Queued, Running (or waiting for approval), Done, Done with
  warnings, Failed, Not new. At most 10,000 rows per session. Changes go to the database and the page at most every
  500 ms (topic `cameras`: changed rows in parts of 500, removed MACs, counts); the page reads `listCameras` in pages
  of at most 1,000 by MAC.
- **Flow** of a camera: a place (at most 8 at once, `ProvisioningOptions.MaxParallel`), then the look
  (`IDeviceAutoAdd.LookAsync`: the add page's probe and Axis check, no password; retried every 10 s for 2 minutes while
  the camera starts). Not factory-new: "Not new, left as it is", nothing is sent. Factory-new: the nodes in order; an
  Error stops the flow when the node always stops (Set password) or its Continue on error is off; a Warning always
  continues. Done: no red node; Done with warnings: a yellow one. **Run failed again** runs the red nodes and the ones
  that never ran, in order, without the look (Set password checks "no password yet" itself).
- **Restart**: a row that was running ends Failed with "The server restarted while this ran." on the node it was on;
  queued rows run again. **Stop**: flows stop waiting (a task a node started runs to its end), rows stay until the next
  Start, the reservations go.

## Nodes

| Node | How | Results |
|---|---|---|
| Set password (fixed, first, always stops) | `IDeviceAutoAdd.SetFirstPasswordAsync` with a credential list entry of root: the add page's and the context menu's path, serial and "no password yet" checked right before writing | "Password set." or the camera's refusal |
| Add to OADM (fixed, second) | `IDeviceAutoAdd.AddWithLoginAsync` with the same entry and the tags (for all devices; the tags put it into device groups), then waits up to 2 minutes until OADM has read it (status Ok, API list) | Success; Warning when OADM has not read it yet or a tag could not be set; Error with the reason |
| Wait for approval | Releases its place and waits for Continue on the row | "Continued by anna." |
| Set IP | Takes the address under a lock (`TakeAddressAsync`), then the Network plugin's task "Assign IP address" (`oadm.network.assign-ip`) with a static payload | "192.168.15.12 is in use, 192.168.15.13 assigned."; "No free address left in the pool."; on failure both addresses |
| Date and time | The Date and time task (`oadm.datetime`), mode NTP, the servers or the ones DHCP gives; with OADM's NTP server then the NTP server plugin's "Use OADM NTP server" (`oadm.ntp-server.use`), which fills in OADM's address towards the camera | "Time zone Europe/Vienna, NTP 10.0.0.17.", "Time zone Europe/Vienna, OADM's NTP server.", "Time zone ... set. OADM's NTP server was not set: ..." or the task's message |
| Upgrade AXIS OS online | Upgrade AXIS OS (`oadm.firmware`) with `"latest":{"target"}` (own track, latest LTS, latest active; the task picks the version on axis.com for the camera's model), started with the tasks of the administrator who started provisioning (`INodeHost.StarterTasks`: their MyAxis sign-in; null after a restart). Only if older than: the device's version first, no task | "AXIS OS from axis.com installed.", "Already 12.6.95.", Error "Sign in to MyAxis." (no or expired sign-in), "Stop and start provisioning again: ..." |
| Upgrade AXIS OS from files | Upgrade AXIS OS with every file of the node as an upload (`files`), the task takes the file whose product is the camera's model; never a downgrade (`allowDowngrade` false: the task refuses an older file before any write, the node maps that refusal to Success) | "AXIS OS 12.6.95 installed.", "Already 12.6.95." (same or newer AXIS OS on the camera), Warning "No file for this model.", Error "The file is no longer on the server. Choose it again." |
| Network settings | Network settings (`oadm.network`): the host name per device (`{model}`, `{serial}`, `{mac}`, `{n}` = start number + the camera's number of the session, `INodeHost.NumberAsync`, kept until Stop), DNS servers and search domain (none: the camera keeps its domains) | "Host name P3265-LVE-10, DNS 10.0.0.1, search domain site.local." or the task's message |
| Configuration | Import configuration (`oadm.device-config.import`) of the file, or of the template camera's export read now (`GET /config/rest/$export`, read only, uploaded for the task, never kept); the areas `param.v2`, `action-rules.v2`, `event-schedules.v2`; the task leaves out device-specific values | "Parameters and action rules applied.", Warning with the task's list of settings the camera lacks, Not needed "this is the template camera.", Error "The template camera ... is not in OADM." |
| Users | One Users task (`oadm.users`, mode add) per account, the password from the vault in the payload; the task protects OADM's account | "Added op1 and view1.", Warning or Error per account ("op1: ...") |
| Certificate | PKI's "Enable HTTPS" (`oadm.pki.https-enable`) with the policy (none: the first HTTPS certificate policy), or "Set up EST" (`oadm.est.setup`); the clock is never confirmed (a clock that is behind refuses) | "Certificate installed, HTTPS on.", "EST enrollment set up.", Error "OADM PRO is not active. Nothing was changed." |
| 802.1X | PKI's "Enable IEEE 802.1X" (`oadm.pki.dot1x-enable`) with PKI's 802.1X policy | "802.1X on." or the task's message |
| SD card | Edge storage's task (`oadm.edge-storage.task`): `encryptionOn` with the passphrase from the vault (formats too) or `format`, then `retention` with the days | "SD card encrypted and formatted, recordings kept 30 days.", Not needed "no SD card." (the first task's "No SD card"), Error with the task's text |
| Autofocus | No task: the optics API of the live view (`optics-control` in a fresh API list, `getCapabilities` with focus, `performAutofocus`, which answers when done) within `AutofocusTimeout` (2 minutes) | "Focus found.", Not needed "no remote focus.", Error "The camera could not find focus. Set focus by hand." or "The autofocus did not end within 2 minutes." |
| VAPIX commands | The VAPIX Commander's task (`oadm.vapix-commander.run`) with the commands of the library copied into this plugin (`Library/`, sources linked) and their values (`FieldValues.Resolve` in the check), confirmed; commands the fresh API list lacks are left out | "Ran Set overlay text.", Warning "Ran ... Not on this camera: Run autofocus.", Error with the task's message |

**Set password secrets.** The profile never holds a password. A typed password (`saveProfile` with `password`, the tool's
secret) is added to the credential list as root (`IDeviceAutoAdd.AddCredentialAsync`, encrypted with the master key) and
the node keeps the entry's id. It is never logged, audited or sent back.

**Secrets and files** (`Engine/NodeVault.cs`). A node names a typed password or passphrase by `secretId` and a file by
`fileId`, never the value: the page's `storeSecret` (encrypted with `ctx.Secrets`, setting `secrets`) and `storeFile`
(a file of the caller's own uploads, copied into `ctx.DataDirectory/files`, setting `files`) answer the ids. When a node
runs, a file goes to its task as a system upload (`ctx.Uploads`, SDK 0.15.0), added again when the server deleted the
last one after a day. What no profile and no session names is deleted a day after it was stored (`PruneAsync` at start,
save and delete). A plugin never reads a password of the credential list, so Users and SD card take typed ones only.

**Set IP addresses.** Under one lock per server: the camera's own reservation of this session, else the first address of
the pool that is not reserved in this session, no OADM device has (one read of the device list per take) and that answers
neither ping nor TCP 80/443 (`AddressProbe.cs`, linked from the Network plugin). The reservation is stored before the lock
ends; the write runs outside it, so cameras write in parallel. The Network task checks the address again, waits for the
camera at the new address (its own 90 s) and moves the OADM record; on failure OADM keeps the camera at its old address.
A pool that overlaps the DHCP range is a warning in the node and at Start.

## Adding a node

Most nodes start the existing task of their area with a payload; the task keeps its own checks (API requirements,
validation before the first write), the node maps its outcome.

1. Add the kind to `NodeKind` (append; names are stored, never rename) and a settings record next to the others in
   `Model/Profiles.cs`.
2. Write the node. For a task of another plugin derive from `TaskNode<TSettings>`:

   ```csharp
   public sealed class SdCardNode : TaskNode<SdCardSettings>
   {
       public override NodeKind Kind => NodeKind.SdCard;
       public override string Name => "SD card";
       public override string TaskPluginId => "oadm.edge-storage.format";
       public override object DefaultSettings => new SdCardSettings();

       // One error, the same in the editor and before Start; cheap.
       protected override string? CheckSettings(SdCardSettings s, ProfileCheckContext context) => ...;

       // The task's payload JSON for this camera (its ITaskPlugin.PayloadType); the task validates it again.
       protected override string Payload(SdCardSettings s, NodeRun run) => ...;

       protected override string Done(SdCardSettings s, NodeRun run) => "SD card formatted.";

       // Optional: a result without a task, e.g. NodeResult.NotNeeded("no SD card.").
       protected override Task<NodeResult?> BeforeAsync(SdCardSettings s, NodeRun run, CancellationToken ct) => ...;
   }
   ```

   The default `Map` turns Done into Success (`Done`), Done with warnings into Warning and Failed or Cancelled into Error,
   each with the task's message; override it for more. `PayloadAsync` builds a payload that needs a read (a file as an
   upload; an `InvalidOperationException` is the node's Error), `TaskFor` picks another task by the settings, `RunnerFor`
   another runner (`StarterTasks`), `AfterAsync` runs a second task. A node with several tasks or with VAPIX itself
   derives from `DeviceNode<TSettings>` (`RunOnAsync`). A node that is not a task implements `IProvisioningNode` itself
   (`RunAsync` never throws for a device problem, it returns a `NodeResult`). `NeedsDevice` (default true) makes the
   engine refuse the node before Add to OADM gave the camera its device.
3. Register it in `NodeRegistry.Default`.
4. Test it with `Harness` (`tests/Oadm.Plugins.Provisioning.Tests/Fakes.cs`): Success, Warning, Error and Not needed with
   their messages, the payload, and the check. Write the spec line and the table row above.

The engine is the node's `INodeHost`: `AutoAdd`, `Tasks`, `Devices`, `Time`, `Options`, `TakeAddressAsync`,
`WaitForApprovalAsync`, `DeviceAdded`, `AddressChanged`. `TaskRuns.RunTaskAsync` starts a task and waits for its end
(every 2 s, at most 30 minutes by default) for nodes that need more than one task.

## The page

`Oadm.Plugins.Provisioning.Client` (own cards, `ProvisioningView`):

- **DHCP card**: the DHCP server's state, interface and range from `status.dhcp`; Turn on / Edit open the DHCP server's
  own page (`OpenAsync("plugin:oadm.dhcp-server")`), never a copy of its form; the pool overlap warning below.
- **Toolbar**: Start provisioning (disabled with the reason as tooltip: no DHCP server, no active profile, profile with
  errors), Stop provisioning (confirmed), the active profile (choosing one calls `activateProfile`, a refusal goes back),
  Manage profiles, then the camera actions and the search at the right end, all in one row (the buttons go into the
  overflow menu when the flow card leaves too little room).
- **Manage profiles** (`ProfilesWindow`, `ProfilesDialogViewModel`): the profiles with their nodes and state (Active, Has
  errors); New and Edit (also a double click) open the profile editor over it, Activate (off for a draft or the active
  one, the reason as tooltip), Delete (red confirmation); a refusal directly below the list.
- **Cameras** columns: Time (`CameraRow.Seen`), MAC address, model, IP address, state, node, problem; the search also
  matches the serial number. `listCameras` in pages of 1,000, then the batched `cameras` events applied by MAC address (a row's older
  version never overwrites a newer one); `RangeObservableCollection`, one reset per search or batch; Run failed again,
  Continue and Remove on the selected rows. The empty line says what to do first.
- **A camera's flow**: the row clicked last opens a card right of the table with the session's profile
  (`status.sessionProfile`) on the shared node canvas (`NodeChainCanvas` of the client SDK, the look of Vision Center's
  canvas): green (Success, Not needed), yellow, red with the message below, violet while it runs, grey when not run.
- **Profile editor** (`ProfileEditorWindow`): the same canvas, Add node (the kinds of `nodeKinds` that are not fixed),
  Move up, Move down, Remove (never the fixed two). The side panel shows the selected node's form (`NodeForms`: one per
  kind, the server's check field by field; choices show and hide fields), errors below the fields; `checkProfile` after
  every change (one at a time), its error on the node, its warning below it in amber. A long chain keeps the cards
  readable (at least 80 %) and pans the selected one into view.
- **Drafts** (decision 2026-11-18): a profile with node errors is saved as a draft (Save on, the error on each node, the
  tooltip says so); only the name refuses a save. Activate and Start stay refused while errors exist; the profile choice
  marks such a profile "Has errors".
- **Secrets and files**: a typed root password goes with Save (`SaveProfileRequest.Password`); the other passwords and
  passphrases (Users, SD card) go to `storeSecret` once, with Save, and the node keeps the id; a stored one shows "Kept
  on the server". Files (Configuration, Upgrade AXIS OS from files) are uploaded when chosen and kept with `storeFile`.
  The template camera is chosen with the device choice (`DevicePickerWindow.ShowOneAsync`), stored by serial number.
  VAPIX commands come from `vapixCommands` (the library's commands a profile can run); their values are checked with
  the Commander's own `FieldValues.Convert`.

A new node kind gets an entry in `NodeForms` (its fields and their rules) and `NodeLook` (icon and colour).

## Page methods and events

`ProvisioningContract.cs`: `getStatus`, `listProfiles`, `getProfile`, `checkProfile`, `nodeKinds`, `credentials` (ids and
user names only), `saveProfile`, `deleteProfile`, `activateProfile`, `start`, `stop`, `listCameras` (paged), `getCamera`,
`runFailedAgain`, `continue`, `removeCameras`, `vapixCommands` (the commands a VAPIX commands node can run, with their
fields), `storeSecret` (the id only comes back), `storeFile` (an upload id of the
caller). Events `status` (after Start, Stop and profile changes) and `cameras` (batched). `getStatus` and `status`
carry the profile the session runs (`sessionProfile`: the page draws a camera's flow with it). Nothing grows with the
device count.

## Rights

Permissions "show" (reads) and "manage" (everything else), both off for Operator and Viewer: administrators by default.
Every method only for users with all devices (`RequiresAllDevices`); changes are audited without the payload.

## Tools

`oadm_provisioning_status`, `_profiles`, `_profile`, `_credentials`, `_vapix_commands`, `_cameras` (paged); changes with
preview and run:
`_profile_save` (secret `password` when `new_password`), `_secret_store` (secret `secret`: a node's password or
passphrase), `_file_store` (`upload_id`), `_profile_delete`, `_profile_activate`, `_start`, `_stop`,
`_cameras_run_failed_again`, `_cameras_continue`, `_cameras_remove`. A node's settings are snake_case at any depth (the
values of a VAPIX command keep its field names); `NodeArgs.SettingsHelp` lists them per kind.

## Tests

`tests/Oadm.Plugins.Provisioning.Tests`: fakes for the add pipeline, the DHCP view and the address probe, a fake clock,
gates for the 8 places and the address lock; every node with its task's outcomes (`Harness.Outcomes`, `Decide` per run),
scripted VAPIX for Autofocus and the template camera, the vault with a marking fake protector (`VaultTests`); the Perf test takes addresses with 100,000 devices and pages 2,000 rows.
The page (`Client/`): the view models against canned answers and the server's own profile check (no Avalonia), the
table at 100,000 cameras (Perf, with a 1,000-row twin), headless screenshots of the page, a camera's flow, the editor and
the empty page (`OADM_SCREENSHOT_DIR`). No hardware test yet.
