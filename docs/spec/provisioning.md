# Zero-touch provisioning

Status: **redesign agreed 2026-11-19; built** (stage 1 the server, stage 2 the client: the Devices toolbar button,
Provisioning management, the Add devices page, Run again in the tasks pane; the build of 2026-11-18, a rail page with its
own camera list and flow view for cameras of OADM's DHCP server, is replaced by this). Open (ask the maintainer): Users
and SD card take a typed password only; SD card formats with the camera's own file system and sets only the maximum age;
Configuration applies parameters, action rules and schedules (a configuration file has no stream profiles).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The installer turns provisioning on and connects factory-new cameras. Every factory-new camera OADM finds, however it
finds it, lands in the device list and gets one **task** that runs the active **profile**, a flow of nodes: a few minutes
later the camera has its password, AXIS OS, configuration, time, certificate and network settings. The progress is the
task in the tasks pane, like every other change OADM makes.

Core plugin `plugins/Oadm.Plugins.Provisioning` (+ `.Client`), id `oadm.provisioning`, **free, always on** (a built-in
feature, decision 2026-11-19: the Plugins page lists it as always on, it cannot be turned off), no rail page. Always on
changes nothing by itself: provisioning itself is **Off** until an Administrator turns it on.

## Rules that never break

1. **Only factory-new cameras.** A task starts only for an Axis device that has no password yet (Password not set),
   checked on the camera itself right before the first write, never from an old record (a camera reset to factory
   default after it was added is new again). A camera that is still starting up is asked again until it says whether it
   needs setup (at most 2 minutes), never taken as configured. Any other camera is never changed.
2. **Off by default, Administrator only.** Only an Administrator turns provisioning on or off and edits profiles
   (provisioning writes to every new camera it finds, on every network OADM sees). On and off are audited, and so is
   every task: one entry "Started task" per provisioning task, user "Provisioning (<the administrator who turned it on>)",
   target "Provision with <profile>", detail "10.0.0.12 (ACCC8E012345)", at its end "...: done", "...: done with
   warnings: <message>", "...: failed at <step>: <message>" or "...: cancelled"; Run again is one entry of the user who
   pressed it for all its cameras ("2 devices", then "2 devices: 1 done, 1 failed (...)"). The host writes them
   (`TaskAudit`, SDK 0.15.0; [security.md](security.md) "Task outcome").
3. **Set password first, and it stops.** Every profile starts with the fixed node Set password. If it fails, nothing
   after it runs for that camera. Every other node continues on error by default.
4. **One task per camera.** The task "Provision with <profile>" has one named step per node; it follows the task rules
   (`docs/spec/tasks.md`: one task per device, never a persisted payload, parallelism per plugin, at most 8 at once).
   A camera already in a running provisioning task gets no second one.
5. **Never the same address twice.** The Network node's pool hands out the next free address under a short lock (taking
   only, not the write), so two cameras never get the same address.
6. **No lock-out.** A new fixed address must answer within 2 minutes, else the step is red with both addresses and OADM
   keeps the camera at the address that answers. OADM's own account on the camera is never removed or demoted.
7. **Device safety** of task plugins for every node (CLAUDE.md): APIs checked first, input validated before the first
   write, secrets only in the master-key store, never logged.

## Where it lives

- **Devices toolbar**: a **Provisioning** button right of Export (`ProvisioningToolbarPlugin`, an `IToolbarPlugin` of the
  plugin's client part, group Manage, order 20; it gets the plugin's context through `IToolbarContext.CorePluginContext`,
  SDK 0.15.0) opens **Provisioning management**. While provisioning is on, the button reads **Provisioning active** and its
  icon is green (the theme's `Button.toolbar.ok`, as the title bar's MyAxis cloud), live from the `status` event (read with
  `state` first). Users with "show" see the button; only an Administrator with "manage" opens the dialog (the others: the
  button is disabled, its tooltip says so).
- **Provisioning management** (dialog `ProvisioningWindow`): the **On / Off** switch (the theme's `ToggleSwitch`; a refused
  On shows `TurnOnRefusal` or the server's reason directly below the switch and the switch stays Off; who turned it on and
  when beside it), the **active profile** (a choice of the profiles without errors; choosing one activates it, a refusal
  below the choice), the warnings, and the profile list (every profile with its number of nodes and its state, Active or
  Has errors; New and Edit open the profile editor with the status's DHCP server for a new Network node, Activate, Delete
  with a red confirmation). One card for the switch, one for the list, a lone Close; it follows the `status` event while
  open. On needs an active profile without errors. While no profile is active, the first one saved without errors becomes
  active.
- **What starts a task** while provisioning is on: every factory-new Axis camera OADM finds by Discovery (also the
  background discovery), Network range, Add manually, Import or a lease of OADM's DHCP server. The camera is added to
  OADM (status Password not set) and its task starts. On the Add devices page such a camera shows **Ready for
  provisioning** instead of Set password (not at a site) and is added without a password; its setup runs afterwards. The
  page asks the plugin's `state` when it opens and again right before Add: only while a camera gets its task (`ready`: on
  and the active profile without errors); else such a camera is a factory-new row as usual (Set password).
- **Progress**: the tasks pane. There is no camera list or flow view of its own: the task's named steps and its log show
  each node's result. The task engine has no Run again of its own ([tasks.md](tasks.md): no retry), so Provisioning has
  one: **Run again** in the tasks pane's header while a failed "Provision with" task is selected (`RunAgainTaskAction`, an
  `ITaskAction`, SDK 0.15.0; right "manage") starts a new task for that camera with the same profile that runs only the
  failed steps and those that never ran, in order (a camera that failed the check that it is new is checked again and runs
  everything). Nothing started: a message says so.

### How a task starts (stage 1, 2026-11-19)

- **One hook in the server**: `IDeviceAutoAdd.WatchFactoryNew` (SDK 0.15.0) reports every managed device that needs its
  first password: a device the device adder stores as Password not set (the add page's Add, an import, the waiting list,
  the automatic add of the DHCP server, an agent's add) and a managed device whose status the polling turns Password not
  set (reset to factory default): only when it had a password when OADM last reached it, or OADM does not know (offline
  since the server started); a camera that was Password not set before it went offline is not reported again. While
  provisioning is on, each local one gets its task (found together: one batch).
- **A failed run stays for Run again**: a report or a lease never starts a new task for a camera whose last run failed or
  did not end, unless the camera was just added or had a password in between (reset to factory default).
- **A lease of OADM's DHCP server** to an Axis MAC address: the camera is asked whether it is new (no answer: asked again
  for 2 minutes); a new one is added (Password not set, which starts its task) or, when OADM manages it already, gets its
  task at once; a camera with a password is neither added nor changed. On also checks the cameras that hold a lease at
  that moment.
- Cameras OADM manages as Password not set when provisioning is turned on get no task by that (they get one when they are
  found again: a lease, a reset).
- **Off** starts nothing new; running and queued provisioning tasks run to their end (a person cancels them in the tasks
  pane) and Run again still works, with the MyAxis sign-in of the administrator who turned it on until the next On. On,
  who turned it on and the last lease read survive a restart; the administrator's MyAxis sign-in does not (Upgrade AXIS
  OS online then asks to turn provisioning off and on again).
- **The task**: `oadm.provisioning.run`, hidden from the menus (only Provisioning starts it), at most 8 at once (its
  `MaxParallelDevices`, the server setting can only lower it). Its payload (memory only) is the active profile as it was
  when the task started; editing the profile changes later tasks only. A password or file a node names stays while a
  stored profile, a queued or running task or a run Run again starts names it (never deleted by saving a profile);
  what nothing names goes after a day. Steps: "Check that the camera is new" (the camera
  itself, the server waits while it is starting), then one per node: "Set the password", "Set the network settings",
  "Upgrade AXIS OS from axis.com", "Upgrade AXIS OS from a file", "Set the date and time", "Apply the configuration", "Add
  the users", "Install the certificate", "Turn on 802.1X", "Set up the SD card", "Run the autofocus", "Run the VAPIX
  commands". Success is Done, Not needed is Skipped with its reason, Warning is a warning step, Error a failed step; the
  task fails when a step failed. The nodes start the tasks of their areas as before; those show in the tasks pane too.
- **Set password** waits after the password until OADM has read the camera with it (status Ok and its API list, at most
  2 minutes), so the next steps can check their APIs; not read in time is a Warning.
- **Profiles**: the active profile cannot be deleted while provisioning is on. The search domain of Network needs DNS
  servers.

## Profiles: the flow

A profile is a name and a **straight chain of nodes** on the top-to-bottom node canvas (no branches, no loops). A new
profile starts with Set password. Nodes are added with the + below the last node or the selected one (never above Set
password), from the shared node list (`NodePicker`: search, the kinds in groups, each with its coloured icon, name and
one line), moved up and down, removed (except Set password) and configured in the side panel; every field has a "?" with
a short help (`FormField.Help`). The profile is checked before it can be active (one error per node, the same check in
the editor); a profile with errors is saved as a draft.

Every step ends **Success**, **Warning** or **Error** with a plain message ("AXIS OS 12.6.95 installed.", "No file for
this model."). A node that does not apply (no SD card, no remote focus) is Success with "Not needed: ...". Each node has
**Continue on error** (on by default; off for Set password). Warning always continues.

## Nodes

1. **Set password** (fixed, first): the root password, from OADM's credential list or typed (stored encrypted with the
   master key, added to the credential list, never shown again). The choice lists the root entries with the time each was
   added; the eye shows the chosen entry's password with the Credentials page's rules (Administrator only, through the
   host's reveal, audited, never the password in the log). After it, OADM logs in to the camera with that password.
2. **Network**: the camera's network settings, every part optional but at least one set:
   - **IP address**: **Keep** (as it is now, for example from DHCP) or **From a pool**: first and last address, subnet
     mask, default router. It takes the next address of the pool that is free (rule 5): not reserved by provisioning, no
     OADM device has it, no answer to ping and TCP 80/443 (a camera already at an address of the pool keeps it). A taken
     address is skipped and the step says so ("192.168.15.12 is in use, 192.168.15.13 assigned."). Pool used up: Error
     "No free address left in the pool." The address is always written as a fixed address, also when the camera has it
     already. A given address stays reserved while the camera's provisioning task runs or while the camera is an OADM
     device without that address; it is released when the camera refused it before any change, has it now, or is no OADM
     device (removed, never added) and no provisioning task runs for it. A pool overlapping the range of OADM's DHCP server is a warning
     in the node, never an error. A new Network node starts with the first three numbers of that DHCP range
     ("192.168.15.") and its subnet mask when OADM's DHCP server is set up, else empty with 255.255.255.0.
   - **DNS servers** and **search domain**.
   - Never the host name. The address is written last; OADM follows the moved camera.
3. **Upgrade AXIS OS online**: from axis.com with the MyAxis sign-in of the Administrator who turned provisioning on
   (expired or none: Error "Sign in to MyAxis."). Target: the latest of the camera's current track, the latest LTS, or the
   latest active track; optional **only if older than** a version (else Success "Already 12.6.95."). The Upgrade AXIS OS
   task's checks apply.
4. **Upgrade AXIS OS from files**: uploaded AXIS OS files; each camera gets the file for its model. No file for the
   model: Warning. Never a downgrade.
5. **Date and time**: NTP servers (NTS where the camera has it), the ones DHCP gives or OADM's NTP server, and the time
   zone (the shared `TimeZoneChoice`).
6. **Configuration**: a configuration file of Device configuration **or** a template camera read at run time; the areas
   to apply. Device-specific values (address, host name, certificates, passwords) are never copied; a file or template
   camera of another model works (decision 2026-11-20): the camera leaves out what it does not have, a value it refuses
   is a Warning listing it.
7. **Users**: more accounts (name, role, password from the credential list or typed); several Users nodes allowed;
   OADM's account never touched.
8. **Certificate**: a certificate by a PKI issuing policy (clock check first), **or** EST enrollment (PRO).
9. **802.1X**: with the certificate and CA of PKI's 802.1X policies.
10. **SD card**: format, encrypt (passphrase typed), maximum age. No card: Not needed.
11. **Autofocus**: starts it and waits until done (at most 2 minutes). No remote focus: Not needed.
12. **VAPIX commands**: commands of the VAPIX Commander library with their values; a command the model lacks: Warning.

Tags for the new device come from the Add devices page as usual (or none); the profile has no Add to OADM node.

Left out on purpose: Wait for approval (2026-11-19), install applications, action rules (Configuration copies them),
hardening check, VPN, restart, snapshot, notify, capture mode / rotation.

Older profiles of the 2026-11-18 build load: Add to OADM and Wait for approval are dropped, Set IP and Network settings
become one Network node at the place of the first of them (Set IP's pool, the DNS servers of Network settings, else Set
IP's, and its search domain); the converted profiles are stored again.

## Sites

Not at sites in the first version: cameras found through a site node are added as usual but get no provisioning task.

## Tests

- Only cameras that say they need setup start a task; a camera that is still starting is asked again, never taken as
  configured; a managed camera reset to factory default is new again; a configured camera is never changed.
- Off: nothing starts. On: each source (Discovery, range, Add manually, Import, DHCP lease) starts one task per camera,
  never a second one for the same camera.
- Set password failing stops the task; any other failing step continues unless Continue on error is off; Run again runs
  only the failed steps, in order.
- Network pool: parallel cameras never get the same address (gates); the next free address skips reservations, OADM
  devices and answering addresses; overlap with the DHCP range is a warning; a used-up pool is an Error; no answer at the
  new address within 2 minutes is an Error and the camera stays reachable; Keep never writes an address, a pool always
  writes a fixed one (also the camera's current address). Given addresses stay reserved (also across Off, On and
  restarts) while their task runs or their camera is an OADM device without the address; released when refused before any
  change or when the camera left OADM.
- A camera that was Password not set before it went offline gets no second task; a failed run is replaced only after a
  reset. Saving a profile keeps the passwords and files of queued tasks and of runs Run again starts. The prune of old
  runs never drops the snapshot of a task that starts meanwhile (gates). Off keeps the starter's sign-in for running tasks
  and Run again.
- At most 8 at once (gates, no timing).
- Every node with its fake device: Success, Warning, Error and Not needed with their messages.
- Older profiles load with the nodes converted as above.
- 100,000 devices in OADM: the free-address check and starting tasks for a burst of new cameras stay fast.
- Client: the toolbar button follows the `status` event and opens the dialog for administrators only; a refused On
  stays Off with the reason below the switch; the Add devices page shows Ready for provisioning instead of Set password
  (a device at a site keeps Set password) only while `state` says ready, asks again before Add and adds without a
  password; Run again only on failed provisioning tasks.
