# Zero-touch provisioning

Status: **draft spec; server part built** (every node below, the page methods and tools; the page and the canvas are
next). Flow design of 2026-11-18; replaces the step list of 2026-10-27. Open (ask the maintainer): Users and SD card take
a typed password only (a plugin never reads a password of the credential list); SD card formats with the camera's own
file system and sets only the maximum age (Edge storage has no file system choice and no cleanup level); Configuration
applies parameters, action rules and schedules (a configuration file has no stream profiles).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The installer plugs factory-new cameras into the PoE switch and waits: OADM's DHCP server gives each one an address, the
active **profile**, a flow of nodes, runs on it, and a few minutes later the camera has its password, AXIS OS,
configuration, time, certificate and fixed address. The same canvas is the editor of a profile and, for one camera, the
result: every node green, yellow or red with its message.

Core plugin `plugins/Oadm.Plugins.Provisioning` (+ `.Client`), id `oadm.provisioning`, rail page **Provisioning** (group
Devices), **free, on by default**. On by default changes nothing by itself: a camera starts a flow only while
provisioning is started on the page and a profile is active. Every node uses the existing task of its area (with that task's
checks); provisioning adds the order, the per-camera result and the fixed address pool. It never changes the DHCP
server's settings.

## Rules that never break

1. **Only factory-new cameras from OADM's DHCP.** A flow starts only for an Axis MAC that got its address from OADM's DHCP
   server **and** has no password yet (status Password not set). Any other camera that gets a lease is listed as "Not new,
   left as it is" and never changed. No Discovery fallback.
2. **Set password first, and it stops.** Every profile starts with the fixed nodes Set password and Add to OADM. If Set
   password fails, nothing after it runs for that camera. Every other node continues on error by default.
3. **Never the same address twice.** The Set IP node takes and reserves the next free address of its pool under a short
   lock (taking only, not the write), so two cameras never get the same address; the writes run in parallel.
4. **No lock-out.** The fixed IP must answer within 2 minutes, else the node is red with both addresses and OADM keeps the
   camera at the address that answers. OADM's own account on the camera is never removed or demoted.
5. **Device safety** of task plugins for every node (CLAUDE.md): APIs checked first, input validated before the first
   write, secrets only in the master-key store, never logged.
6. **Administrator only** for profiles and Start/Stop (provisioning writes to every new camera). The
   device group rules apply to the devices it adds.

## The page

- **DHCP card** (top): the DHCP server's state, interface and range, with Turn on / Edit opening the DHCP server's own
  form. Provisioning never changes the DHCP server; without a running DHCP server it cannot start ("Turn on the DHCP
  server first.").
- **Start provisioning / Stop provisioning** and the **active profile** (several profiles, one active). While started,
  the page says "Provisioning started" with the start time; a session lasts until Stop, also across a server restart.
  Start warns (Start stays possible) when the Set IP pool overlaps the DHCP range.
- **Cameras**: virtualized table, one row per camera that started a flow: MAC, model, serial, address, profile, state
  (Queued, Running node, Done, Done with warnings, Failed), the node it is on. Clicking a row opens the profile's canvas
  with that camera's result per node. **Run failed again** re-runs only the red nodes of the chosen cameras, in flow
  order. Rows can be removed (the camera stays in OADM).
- Up to **8 cameras at once**; more wait as Queued.

## Profiles: the flow

A profile is a name and a **straight chain of nodes** on the top-to-bottom canvas of Vision Center (no branches, no
loops). Nodes are added from a list, moved up and down, removed (except the two fixed ones) and configured in the side
panel. The profile is checked before it can be active (one error per node, the same check in the editor).

Every node ends **Success**, **Warning** or **Error** with a plain message ("AXIS OS 12.6.95 installed.", "No file for
this model.", "The camera did not answer at 10.0.0.120 within 2 minutes."). A node that does not apply (no SD card, no
remote focus) is Success with a grey "Not needed: ..." line. Each node has **Continue on error** (on by default; off for
Set password).

## Nodes

1. **Set password** (fixed, first): the root password, from OADM's credential list or typed (stored encrypted with the
   master key, added to the credential list, never shown again).
2. **Add to OADM** (fixed, second): adds the camera with that login; optional **tags**, so it lands in the right device
   group. OADM has no names of its own: a device shows its host name or address.
3. **Upgrade AXIS OS online**: AXIS OS from axis.com with the MyAxis sign-in of the administrator who started
   provisioning (expired or no sign-in: Error "Sign in to MyAxis."; after a server restart Start again). Target: the latest of the camera's current track, the latest LTS, or
   the latest active track; optional **only if older than** a version (else Success "Already 12.6.95."). The Upgrade
   AXIS OS task's checks apply.
4. **Upgrade AXIS OS from files**: several uploaded AXIS OS files; each camera gets the file for its model. No file for
   the model: **Warning**. A downgrade only with Allow downgrade.
5. **Network settings**: host name from a pattern (`{model}`, `{serial}`, `{mac}`, `{n}` with a start number), DNS
   servers and search domain.
6. **Date and time**: NTP servers (NTS where the camera has it) or OADM's NTP server (its address filled in), and the
   time zone.
7. **Configuration**: a configuration file of Device configuration **or** a template camera read at run time; the areas
   to apply (parameters, action rules, schedules, stream profiles). Device-specific values (address, host name,
   certificates, passwords) are never copied; settings the model lacks: Warning listing them.
8. **Users**: more accounts (name, role, password from the credential list or typed); several Users nodes allowed;
   OADM's account never touched.
9. **Certificate**: install a certificate by a PKI issuing policy (clock check first), **or** set up EST enrollment (the
   EST server's rules, PRO) so the camera enrolls and renews by itself.
10. **802.1X**: turns on 802.1X with the certificate and CA of PKI's 802.1X policies (no settings of its own). Placed
    anywhere; the switch is the installer's business.
11. **SD card**: format (ext4), encrypt (passphrase from the credential list or typed), cleanup settings (max age,
    level). No card: Not needed.
12. **Autofocus**: starts the autofocus and waits until it is done (at most 2 minutes). No remote focus: Not needed.
13. **VAPIX commands**: commands of the VAPIX Commander library with their values (its checks and API requirements); a
    command the model lacks: Warning.
14. **Wait for approval**: the camera waits here until a person presses Continue on its row (for example before the fixed
    IP).
15. **Set IP**: an address **pool** of its own (first and last address, for example 192.168.15.10 to 192.168.15.50),
    subnet mask, gateway and DNS servers; it should not overlap the DHCP range (a warning in the node and at Start,
    never an error). Under the lock (rule 3) it takes the next address of the pool, starting at the first, that is free:
    not reserved in this session, no OADM device has it, no answer to ping and TCP 80/443. A taken address is skipped and
    the node says so ("192.168.15.12 is in use, 192.168.15.13 assigned."). It writes the fixed IP, mask, gateway and DNS,
    waits until the camera answers there (OADM follows the moved device). The session's reserved addresses are kept
    until Stop; a new session starts at the first address again (the addresses given before are OADM devices by then and
    skipped). Pool used up: Error "No free address left in the pool."

Left out on purpose (2026-11-18): install applications, action rules (the Configuration node copies them), hardening
check, VPN, restart, snapshot, notify, capture mode / rotation.

## Sites

Not at sites in the first version: the DHCP server and the flows run on the server's own network only.

## Tests

- Only Password not set cameras with an OADM DHCP lease start a flow; a configured camera is never changed.
- Set password failing stops the flow; any other failing node continues unless Continue on error is off; Warning always
  continues; Run failed again runs only the red nodes, in order.
- Set IP: parallel cameras never get the same address (gates); the next free address skips session reservations, OADM
  devices and answering addresses and says which it skipped; reservations survive a restart until Stop; the DHCP
  server's settings never change; overlap with the DHCP range is a warning; a used-up pool is an Error; no answer at the new
  address within 2 minutes is an Error and the camera stays reachable.
- At most 8 cameras run at once (gates, no timing); more are Queued.
- Every node with its task's fake device: Success, Warning, Error and Not needed with their messages.
- The profile check: one error per node, the same in the editor and before activation.
- 100,000 devices in OADM: the free-address check and the camera table stay fast.
