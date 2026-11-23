# Provisioning

Core plugin `oadm.provisioning`, free, always on (a built-in feature), no rail page. While provisioning is on, every
factory-new camera OADM finds gets one task "Provision with <profile>" that runs the active profile. The spec is
[`docs/spec/provisioning.md`](../../docs/spec/provisioning.md); this file holds the decisions of the code.

Built (redesign of 2026-11-19): the server part, the task, every node of the spec, the methods and the tools (stage 1);
the client part: the Devices toolbar button, Provisioning management with the profile editor, Run again in the tasks
pane (stage 2). The Add devices page's Ready for provisioning is the host's (it reads `state`: `ready`).

## How it works

- **Profiles** (`Model/Profiles.cs`, setting `profiles`): a name and a straight list of `FlowNode`s (kind, settings
  record as JSON, Continue on error). One profile is active. `ProfileCheck.Check` gives one error per node (its place
  first, then its settings) and one warning per node; the editor (`checkProfile`), Save, Activate and On run the same
  check. Save refuses only errors of the whole profile (name, Set password first); a profile with node errors is a draft
  and cannot be active. Profiles of the build of 2026-11-18 are converted when they load and stored again
  (`Engine/ProfileUpgrade.cs`): Add to OADM and Wait for approval dropped, Set IP and Network settings merged into one
  Network node; `NodeKind` keeps their names only for that.
- **On and Off** (`Engine/ProvisioningService.cs`, setting `state`: on, who, when, the last DHCP acknowledgement read).
  On needs an active profile without errors; it keeps the caller's tasks in memory (`INodeHost.StarterTasks`, their
  MyAxis sign-in, null after a restart). Off starts nothing new; running and queued tasks run to their end and Run again
  still works, so Off keeps the starter's tasks; the next On replaces them.
- **What starts a task**: `IDeviceAutoAdd.WatchFactoryNew` (SDK 0.15.0): the server reports a device its device adder
  stored as Password not set and a managed device the polling turned Password not set (`FactoryNewDeviceNotices`,
  `DeviceAdder`, `DevicePollingService`). The polling reports a device only when it had a password when a poll last
  reached it (`HadPassword`) or OADM does not know (offline since the server started); a camera that was Password not set
  before it went offline is not reported again. An automatic start (a report, a lease) never replaces a run that did not
  end well (`RunRecord.CanRunAgain`: Run again repeats it) unless the device was just added or `HadPassword`. The report only queues; a loop starts the tasks of everything queued in one
  `RunAsync` (one task per device). Devices at a site get none. The DHCP server's acknowledgements (`IDhcpServerLeases`,
  read every 2 s while on; On also takes the active leases) of Axis MAC blocks (`AxisOui`, linked from the DHCP server)
  are looked at, at most 8 at once: `IDeviceAutoAdd.LookAsync` (the camera itself, the server waits while it is
  starting; no answer: again every 10 s for 2 minutes); a new unmanaged camera is added with `AddAsync` (Password not
  set: its report starts the task), a new managed one gets its task at once, any other is left alone. Provisioning never
  changes the DHCP server.
- **Never twice**: the devices whose task is starting or active are kept by id (`ITaskRunner.IsActive` drops one that
  ended without running). A camera found again while its task runs or waits gets none.
- **The task** (`Engine/ProvisioningTaskPlugin.cs`, `oadm.provisioning.run`): hidden from the menus, a page task (never
  through TaskService.Run or the task tools), `MaxParallelDevices` 8. Payload (`RunPayload`, memory only): the profile as
  it was when the task started, the nodes to run (null: all) and whether the camera is checked first. Steps: "Check that
  the camera is new" (`LookAsync`; a password, a camera still starting after 2 minutes, another serial or no answer fail
  it before any write), then one step per node (`IProvisioningNode.StepName`): Success Done, Not needed Skipped, Warning
  a warning step, Error a failed step; an Error stops the task when the node always stops (Set password) or its Continue
  on error is off. The task fails when a step failed ("2 steps failed. <the first>").
- **Runs** (records `runs`, key the device id; records `snapshots`, key the SHA-256 of the profile's JSON): the last
  run of each device with each node's result, written after every step. **Run again** (`runAgain`) starts, per camera
  whose task is not active and whose last run has a red node or one that never ran, a task with the same profile that
  runs only those nodes in order and skips the check; a camera that failed the check runs everything again. All cameras of
  one profile are one run (each camera's nodes in `RunPayload.Plans`), so the audit log has one entry for the Run again.
  Runs of
  removed devices and snapshots no run names go when the plugin starts (in the background, under the snapshot lock that a
  start also holds from its snapshot to its runs, so the prune never drops the snapshot of a task that starts meanwhile).
- **Audit**: the automatic starts ask for one entry per task (`TaskAudit` with `EachTask`, user `AuditUserOf(OnBy)`:
  "Provisioning (admin)", target `ProvisioningTaskPlugin.TaskNameOf`); Run again asks for one entry of the caller. The
  host adds the outcome when the tasks end (docs/spec/provisioning.md rule 2).
- **Restart**: the task engine fails the running and queued tasks (their payload is gone); Run again repeats them.

## Nodes

| Node | How | Results |
|---|---|---|
| Set password (fixed, first, always stops) | `IDeviceAutoAdd.SetFirstPasswordAsync` with a credential list entry of root: the add page's and the context menu's path, serial and "no password yet" checked right before writing; the device keeps the password as its login. Then it waits until OADM read the camera (status Ok, API list; 2 minutes) | "Password set.", Warning "Password set. OADM has not read the camera yet; the next steps may fail.", or the camera's refusal |
| Network | DNS servers and search domain first with Network settings (`oadm.network`; none: the camera keeps its domains), then the address with "Assign IP address" (`oadm.network.assign-ip`, static, the same DNS servers, domains kept): the next free address of the pool, taken under a lock (`TakeAddressAsync`); Keep writes no address; never the host name. A failed DNS write writes no address | "192.168.15.12 is in use, 192.168.15.13 assigned. DNS 10.0.0.1, search domain site.local.", "192.168.15.20 assigned." (its current address, written as a fixed one), "No free address left in the pool.", on failure both addresses |
| Upgrade AXIS OS online | Upgrade AXIS OS (`oadm.firmware`) with `"latest":{"target"}` (own track, latest LTS, latest active; the task picks the version on axis.com for the camera's model), started with the tasks of the administrator who turned provisioning on (`INodeHost.StarterTasks`: their MyAxis sign-in, kept after Off; null after a restart). Only if older than: the device's version first, no task | "AXIS OS from axis.com installed.", "Already 12.6.95.", Error "Sign in to MyAxis." (no or expired sign-in), "Turn provisioning off and on again: ..." |
| Upgrade AXIS OS from files | Upgrade AXIS OS with every file of the node as an upload (`files`), the task takes the file whose product is the camera's model; never a downgrade (`allowDowngrade` false: the task refuses an older file before any write, the node maps that refusal to Success) | "AXIS OS 12.6.95 installed.", "Already 12.6.95." (same or newer AXIS OS on the camera), Warning "No file for this model.", Error "The file is no longer on the server. Choose it again." |
| Configuration | Configure devices (`ConfigureIds.Configure`, `oadm.device-config.configure`) with the payload of the kept `.cfg` (Create configuration file or ADM; the only source) for the ticked areas Parameters, Action rules (with their own schedules), Stream profiles: `AdmConfigApply.ForEveryDevice`, the dialog's payload for every setting of the file on several devices without the dangerous right (no address, per-device name, identity or connection parameter, no empty password). A file that is gone, no `.cfg`, not readable or refused by the payload check is an Error before any task. Any AXIS OS with `param.cgi`, any model: the task checks each value against the camera's own parameters | "Parameters, action rules and stream profiles applied.", Warning with the task's list of skipped values, Error with the task's text |
| Users | One Users task (`oadm.users`, mode add) per account, the password from the vault in the payload; the task protects OADM's account | "Added op1 and view1.", Warning or Error per account ("op1: ...") |
| Certificate | PKI's "Enable HTTPS" (`oadm.pki.https-enable`) with the chosen HTTPS certificate policy (`IIssuingPolicies` of PKI: chosen, and still in PKI, else an error), or "Set up EST" (`oadm.est.setup`); the clock is never confirmed (a clock that is behind refuses) | "Certificate installed, HTTPS on.", "EST enrollment set up.", Error "OADM PRO is not active. Nothing was changed." |
| 802.1X | PKI's "Enable IEEE 802.1X" (`oadm.pki.dot1x-enable`) with the chosen IEEE 802.1X certificate policy (checked like Certificate's) | "802.1X on." or the task's message |
| SD card | Edge storage's task (`oadm.edge-storage.task`): `encryptionOn` with the passphrase from the vault (formats too) or `format`, then `retention` with the days | "SD card encrypted and formatted, recordings kept 30 days.", Not needed "no SD card." (the first task's "No SD card"), Error with the task's text |
| Autofocus | No task: the optics API of the live view (`optics-control` in a fresh API list, `getCapabilities` with focus, `performAutofocus`, which answers when done) within `AutofocusTimeout` (2 minutes) | "Focus found.", Not needed "no remote focus.", Error "The camera could not find focus. Set focus by hand." or "The autofocus did not end within 2 minutes." |
| VAPIX commands | The VAPIX Commander's task (`oadm.vapix-commander.run`) with the commands of the library copied into this plugin (`Library/`, sources linked) and their values (`FieldValues.Resolve` in the check), confirmed; commands the fresh API list lacks are left out | "Ran Set overlay text.", Warning "Ran ... Not on this camera: Run autofocus.", Error with the task's message |


**Set password secrets.** The profile never holds a password. A typed password (`saveProfile` with `password`, the tool's
secret) is added to the credential list as root (`IDeviceAutoAdd.AddCredentialAsync`, encrypted with the master key) and
the node keeps the entry's id. It is never logged, audited or sent back. The editor's credential choice shows when
each root entry was added (`DeviceCredentialName.Added`). Its eye (Administrators only) shows the chosen entry's stored
password through the host (`ICorePluginClientContext.RevealCredentialAsync`, the Credentials page's call, audited by the
server); `PasswordFieldViewModel` keeps it only while shown and forgets it on the second click, another choice and when
the editor closes. It never reaches the server part.

**Secrets and files** (`Engine/NodeVault.cs`). A node names a typed password or passphrase by `secretId` and a file by
`fileId`, never the value: the page's `storeSecret` (encrypted with `ctx.Secrets`, setting `secrets`) and `storeFile`
(a file of the caller's own uploads, copied into `ctx.DataDirectory/files`, setting `files`) answer the ids. When a node
runs, a file goes to its task as a system upload (`ctx.Uploads`, SDK 0.15.0), added again when the server deleted the
last one after a day. What nothing names is deleted a day after it was stored (`PruneAsync` at start, save and delete):
neither a stored profile nor the snapshot of a run that did not end well (a queued or running task, a run Run again
starts; `WithProfilesInUseAsync`, under the snapshot lock). The server part never reads a password of the credential list, so Users and SD card take typed ones only.

**Network addresses.** Under one lock per server: the camera's own reservation when it is in the pool, else its current
address when that is in the pool, else the first address of the pool that is not reserved for another camera, no other
OADM device has (one read of the device list per take) and that answers neither ping nor TCP 80/443 (`AddressProbe.cs`,
linked from the Network plugin). The reservation (setting `reserved`, by serial number) is stored before the lock ends;
the write runs outside it, so cameras write in parallel. The address is always written as a fixed one, also when the
camera has it already (a DHCP lease in the pool). A reservation stays (also across Off, On and restarts) while a
provisioning task runs for its camera or while its camera is an OADM device that does not have the address yet; the next
take releases the others (the camera has it, was removed from OADM or never added). A write the camera refused before any
change ("Nothing was changed.") releases it at once (`ReleaseAddressAsync`). The Network task checks the address again, waits for the camera at the new address (its
own 90 s) and moves the OADM record; on failure OADM keeps the camera at its old address. A pool that overlaps the DHCP
range is a warning in the node and in the status. A new Network node starts with the first three numbers of the DHCP
range and its subnet mask (`NetworkNode.DefaultsFor`, the default of `nodeKinds`).

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
       public override string StepName => "Set up the SD card"; // its step in the task: imperative, with the article
       public override string TaskPluginId => "oadm.edge-storage.format";
       public override object DefaultSettings => new SdCardSettings();

       // One error, the same in the editor and before activation; cheap.
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
   (`RunAsync` never throws for a device problem, it returns a `NodeResult`).
3. Register it in `NodeRegistry.Default`.
4. Test it with `Harness` (`tests/Oadm.Plugins.Provisioning.Tests/Fakes.cs`): Success, Warning, Error and Not needed with
   their messages, the payload, and the check. Write the spec line and the table row above.

Schedules (`plugins/Oadm.Plugins.Schedules`) compiles the node sources of its actions into its own package (its csproj
lists them): a change to a node changes that action too; run the Schedules tests as well. `Nodes/RestartNode.cs` is a
Schedules action only: it is not in `NodeRegistry`, so a profile never offers it and one that names it has the error of an
unknown node. `ProvisioningOptions` sits in its own file so Schedules can link it without the registry.

The service is the node's `INodeHost`: `AutoAdd`, `Tasks`, `Devices`, `Vapix`, `StarterTasks`, `Vault`, `Time`, `Options`,
`Owner`, `TakeAddressAsync`. `TaskRuns.RunTaskAsync` starts a task and waits for its end (every 2 s, at most 30 minutes by
default) for nodes that need more than one task.

## The client part

`Oadm.Plugins.Provisioning.Client` holds:

- `ProvisioningToolbarPlugin` (`IToolbarPlugin`, group Manage, order 20: right of Export; right "show") with
  `ProvisioningButtonViewModel`: reads `state`, then follows the `status` event while the button is in the window (the
  watch ends when it leaves); "Provisioning active" with the theme's `ok` class while on. Opens Provisioning management
  only for an Administrator with "manage". The plugin's context comes from `IToolbarContext.CorePluginContext` (SDK 0.15.0).
- Provisioning management (`ProvisioningWindow`, `ProvisioningDialogViewModel`): the switch (`ToggleCommand`; a known
  `TurnOnRefusal` is shown without a call; the switch is told to show the server's state again after every flip), the
  active profile's choice (profiles without errors; a choice activates), the warnings, the profile list (New, Edit,
  Activate, Delete). It reads `getStatus` and `listProfiles` and follows the `status` event while open; the status's DHCP
  server goes to the editor for a new Network node.
- `RunAgainTaskAction` (`ITaskAction`, SDK 0.15.0; right "manage"): Run again in the tasks pane for a failed
  `oadm.provisioning.run` task; `runAgain` with its camera, a message when nothing started.
- The profile editor (`ProfileEditorWindow`, `ProfileEditorViewModel`: the shared node canvas `NodeChainCanvas`, the +
  below every node, also below Set password; the node list `NodePicker`; one form per kind in `NodeForms`, the Network
  form with Keep or From a pool). Configuration's file preview (`ConfigPreviewViewModel`, below Continue on error): the
  kept file read once per file through `readConfigFile`, parsed off the UI thread with `AdmConfigFile` and listed from
  `AdmConfigApply.ForEveryDevice`, the payload the run sends (Device configuration's `Shared/` sources `AdmConfigFile`,
  `AdmConfigApply`, `ConfigureModels`, `ParamRules`, `DeviceConfigIds` are compiled into the server part, which the client
  part references; a project reference would copy the other plugin's Server.dll); rows per ticked area in one virtualized
  `ListBox.readOnlyList`. A file that is no configuration file or that the payload check refuses is the file field's
  error (`FileFieldViewModel.SetContentError`).

A new node kind gets an entry in `NodeForms` (its fields and their rules) and `NodeLook` (icon and colour).

## Methods and events

`ProvisioningContract.cs`: `state` (whether it is on, and `ready`: on and the active profile without errors; every signed-in user, also with a device group: the Add devices
page), `getStatus` (on, who, when, the active profile, `turnOnRefusal`, the running provisioning tasks, the warnings),
`listProfiles`, `getProfile`, `checkProfile`, `nodeKinds`, `credentials` (ids and user names only), `saveProfile`,
`deleteProfile` (not the active one while on), `activateProfile`, `turnOn`, `turnOff`, `runAgain` (device ids: started and
skipped), `getRun` (the last run of a camera with each node's name and result, and whether Run again would start),
`vapixCommands`, `storeSecret` (the id only comes back), `storeFile` (an upload id of the caller), `readConfigFile` (a
kept `.cfg` of at most 1 MB as text, for the editor's file preview; a read, right "show"). Event `status` (a
`StatusReply`, to users with "show") after On, Off and changes of the profiles. Nothing grows with the device count.

## Rights

Permissions "show" (reads) and "manage" (everything else), both off for Operator and Viewer: administrators by default.
Every method only for users with all devices (`RequiresAllDevices`) except `state` (a login only); On, Off, Run again and
the profile changes are audited without the payload.

## Tools

`oadm_provisioning_status`, `_profiles`, `_profile`, `_credentials`, `_vapix_commands`, `_camera` (the last run of one
camera); changes with preview and run: `_profile_save` (secret `password` when `new_password`), `_secret_store` (secret
`secret`: a node's password or passphrase), `_file_store` (`upload_id`), `_profile_delete`, `_profile_activate`,
`_turn_on`, `_turn_off`, `_run_again` (device ids). A node's settings are snake_case JSON text at any depth (the values of
a VAPIX command keep its field names); `NodeArgs.SettingsHelp` lists them per kind.

## Tests

`tests/Oadm.Plugins.Provisioning.Tests`: fakes for the add pipeline (the look asks the fake camera, the add reports a
factory-new one like the server), the DHCP view and the address probe, a fake clock; `ServiceTests` (what starts a task,
never twice, sites, leases, the steps, Set password stopping, Continue on error, Run again, restart), `NetworkNodeTests`
(gates for the address lock, reservations, the pool's checks), every node with its task's outcomes (`Harness.Outcomes`,
`Decide` per run; the Configuration node with the `.cfg` ADM wrote from an AXIS P3265-V, a fixture of Device
configuration's tests), every task id a node starts found in a shipped plugin's source
(`Every_task_a_node_starts_is_a_task_of_a_shipped_plugin`), scripted VAPIX for Autofocus, the vault with a marking fake protector
(`VaultTests`), the conversion of older profiles (`ProfileCheckTests`); the Perf test takes addresses and starts the tasks
of 2,000 new cameras with 100,000 devices. The client part (`Client/`): the editor, Provisioning management, the toolbar
button and Run again against canned answers and the server's own profile check; headless screenshots of the editor,
the dialog (a refused On by a real click on the switch) and the toolbar. No hardware test yet.
