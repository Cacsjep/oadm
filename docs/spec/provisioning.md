# Zero-touch provisioning

Status: **redesign agreed 2026-11-19, not built yet** (the build of 2026-11-18, a rail page with its own camera list and
flow view for cameras of OADM's DHCP server, is replaced by this). Open (ask the maintainer): Users and SD card take a
typed password only; SD card formats with the camera's own file system and sets only the maximum age; Configuration
applies parameters, action rules and schedules (a configuration file has no stream profiles).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The installer turns provisioning on and connects factory-new cameras. Every factory-new camera OADM finds, however it
finds it, lands in the device list and gets one **task** that runs the active **profile**, a flow of nodes: a few minutes
later the camera has its password, AXIS OS, configuration, time, certificate and network settings. The progress is the
task in the tasks pane, like every other change OADM makes.

Core plugin `plugins/Oadm.Plugins.Provisioning` (+ `.Client`), id `oadm.provisioning`, **free, on by default**, no rail
page. On by default changes nothing by itself: provisioning itself is **Off** until an Administrator turns it on.

## Rules that never break

1. **Only factory-new cameras.** A task starts only for an Axis device that has no password yet (Password not set),
   checked on the camera itself right before the first write, never from an old record (a camera reset to factory
   default after it was added is new again). A camera that is still starting up is asked again until it says whether it
   needs setup (at most 2 minutes), never taken as configured. Any other camera is never changed.
2. **Off by default, Administrator only.** Only an Administrator turns provisioning on or off and edits profiles
   (provisioning writes to every new camera it finds, on every network OADM sees). On and off are audited.
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

- **Devices toolbar**: a **Provisioning** button right of Export opens **Provisioning management**. While provisioning is
  on, the button is green and reads **Provisioning active**.
- **Provisioning management** (dialog): the **On / Off** switch, the **active profile** (one at a time) and the profile
  list (every profile with its number of nodes and its state, Active or Has errors; New and Edit open the profile editor,
  Activate, Delete with a red confirmation). On needs an active profile without errors. While no profile is active, the
  first one saved without errors becomes active.
- **What starts a task** while provisioning is on: every factory-new Axis camera OADM finds by Discovery (also the
  background discovery), Network range, Add manually, Import or a lease of OADM's DHCP server. The camera is added to
  OADM (status Password not set) and its task starts. On the Add devices page such a camera shows **Provisioning**
  instead of Set password and is added like any other; its setup runs afterwards.
- **Progress**: the tasks pane. A failed task is retried with the task's usual Run again (only the failed steps, in
  order). There is no camera list or flow view of its own.

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
     OADM device has it, no answer to ping and TCP 80/443. A taken address is skipped and the step says so
     ("192.168.15.12 is in use, 192.168.15.13 assigned."). Pool used up: Error "No free address left in the pool." Given
     addresses stay reserved until they are OADM devices. A pool overlapping the range of OADM's DHCP server is a warning
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
   to apply. Device-specific values (address, host name, certificates, passwords) are never copied; settings the model
   lacks: Warning listing them.
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
become one Network node (Set IP's pool, the DNS servers and search domain of either).

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
  new address within 2 minutes is an Error and the camera stays reachable; Keep never writes an address.
- At most 8 at once (gates, no timing).
- Every node with its fake device: Success, Warning, Error and Not needed with their messages.
- Older profiles load with the nodes converted as above.
- 100,000 devices in OADM: the free-address check and starting tasks for a burst of new cameras stay fast.
