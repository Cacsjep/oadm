# Provisioning

Core plugin `oadm.provisioning`, rail page **Provisioning**, free, on by default. Sets up factory-new cameras that get an
address from OADM's DHCP server, while provisioning is started on the page. The spec is
[`docs/spec/provisioning.md`](../../docs/spec/provisioning.md); this file holds the decisions of the code.

Stage 1 (built): the server part, the nodes Set password, Add to OADM, Wait for approval, Set IP and Date and time, the
page methods and the tools. The client page and the canvas come next (no `.Client` project yet: the host shows "not
installed" for the page).

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
| Date and time | The Date and time task (`oadm.datetime`), mode NTP, the servers or the ones DHCP gives | "Time zone Europe/Vienna, NTP 10.0.0.17." or the task's message |

**Set password secrets.** The profile never holds a password. A typed password (`saveProfile` with `password`, the tool's
secret) is added to the credential list as root (`IDeviceAutoAdd.AddCredentialAsync`, encrypted with the master key) and
the node keeps the entry's id. It is never logged, audited or sent back.

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
   each with the task's message; override it for more. A node that is not a task implements `IProvisioningNode` itself
   (`RunAsync` never throws for a device problem, it returns a `NodeResult`). `NeedsDevice` (default true) makes the
   engine refuse the node before Add to OADM gave the camera its device.
3. Register it in `NodeRegistry.Default`.
4. Test it with `Harness` (`tests/Oadm.Plugins.Provisioning.Tests/Fakes.cs`): Success, Warning, Error and Not needed with
   their messages, the payload, and the check. Write the spec line and the table row above.

The engine is the node's `INodeHost`: `AutoAdd`, `Tasks`, `Devices`, `Time`, `Options`, `TakeAddressAsync`,
`WaitForApprovalAsync`, `DeviceAdded`, `AddressChanged`. `TaskRuns.RunTaskAsync` starts a task and waits for its end
(every 2 s, at most 30 minutes by default) for nodes that need more than one task.

## Page methods and events

`ProvisioningContract.cs`: `getStatus`, `listProfiles`, `getProfile`, `checkProfile`, `nodeKinds`, `credentials` (ids and
user names only), `saveProfile`, `deleteProfile`, `activateProfile`, `start`, `stop`, `listCameras` (paged), `getCamera`,
`runFailedAgain`, `continue`, `removeCameras`. Events `status` (after Start, Stop and profile changes) and `cameras`
(batched). Nothing grows with the device count.

## Rights

Permissions "show" (reads) and "manage" (everything else), both off for Operator and Viewer: administrators by default.
Every method only for users with all devices (`RequiresAllDevices`); changes are audited without the payload.

## Tools

`oadm_provisioning_status`, `_profiles`, `_profile`, `_credentials`, `_cameras` (paged); changes with preview and run:
`_profile_save` (secret `password` when `new_password`), `_profile_delete`, `_profile_activate`, `_start`, `_stop`,
`_cameras_run_failed_again`, `_cameras_continue`, `_cameras_remove`.

## Tests

`tests/Oadm.Plugins.Provisioning.Tests`: fakes for the add pipeline, the DHCP view and the address probe, a fake clock,
gates for the 8 places and the address lock; the Perf test takes addresses with 100,000 devices and pages 2,000 rows.
No hardware test yet.
