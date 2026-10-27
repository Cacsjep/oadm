# Zero-touch provisioning

Status: **spec, not built**.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Factory-new cameras are plugged in and set up by themselves: OADM's DHCP server gives them a staging address, OADM sets
the password, applies the profile (configuration files, applications, name, tags, certificate, 802.1X) and lists them
in a table; as the last step the user applies the planned fixed IP addresses. Then provisioning mode ends and the DHCP
server is back as it was.

OADM PRO plugin `plugins/Oadm.Plugins.Provisioning` (+ `.Client`), id `oadm.provisioning`, rail page **Provisioning**
(group Devices), `"pro": true`, off by default; runs only while OADM PRO (or the trial) is active
([licensing.md](licensing.md)). The single steps it uses (DHCP server, configuration files, applications, PKI) stay free.

## Rules that never break

1. **Only factory-new cameras.** OADM provisions only Axis devices whose status is Password not set. A configured camera
   that gets a staging address is listed as "Not new, left as it is" and never changed.
2. **The DHCP server always goes back.** Before provisioning the DHCP server's settings are saved to disk; ending
   provisioning, Cancel, turning the plugin off and the next server start after a stop during provisioning restore them
   exactly (a server that was off is off again). Static leases are never touched.
3. **No lock-out.** The fixed IP is written only after the user's Apply network; the camera must answer at the new
   address within 2 minutes, else the row shows the problem with both addresses. A planned address that is taken (another
   OADM device, another row, an answer on the network) is refused before writing.
4. **Device safety** of task plugins applies to every step (CLAUDE.md): APIs checked first, input validated before the
   first write, no secret logged or persisted outside the master-key store.
5. **Administrator only.** Profiles and provisioning mode need the Administrator role (provisioning takes over the
   server's DHCP); the device group rules apply to the devices it adds.

## Profiles

A profile has:

- **Name.**
- **Staging range**: first and last address, subnet mask, gateway for the time of provisioning (validated like the DHCP
  server's range: same subnet as one of the server's interfaces, no overlap with static leases).
- **Password** of the new cameras' root account (stored encrypted with the master key, like device credentials; never
  shown again, only replaced). Also stored as the devices' credential.
- **Configuration files**: one or more files of the configuration plugin (DeviceConfig "Create configuration file"),
  applied in their order; a file whose settings a model lacks is applied partly and the row says which settings were
  left out (the plugin's existing rules).
- **Applications**: ACAPs (and their license keys) from the Applications plugin, installed after the configuration.
- **Name pattern and tags**: the device name from a pattern (`{site}`, `{model}`, `{n}` with a start number) and tags,
  so new cameras land in the right device group.
- **Certificate**: an issuing PKI policy for HTTPS and, if wanted, 802.1X ([pki.md](pki.md)); the PKI rules decide which
  CA signs.
- **Fixed addresses**: the target range (first, last), subnet mask, gateway, DNS servers. Each new camera gets the next
  free address as its proposal: free = no OADM device has it, no row has it, no answer to ping and TCP 80 / 443.

Several profiles can exist; one is active while provisioning runs.

## Provisioning mode

- **Start** (page toolbar, profile choice): checks that no other DHCP server answers in the staging network (one DHCP
  discover; another answer is a warning in the start dialog, Start stays possible) and that the staging range is valid,
  saves the DHCP server's settings, sets the staging range (the server's normal range is paused, static leases stay),
  turns the DHCP server on, opens its firewall rule like the DHCP plugin does. The start dialog says the normal range is
  paused until provisioning ends.
- **New camera**: a DHCP lease from the staging range to an Axis MAC (the DHCP plugin's lease events), then the Axis
  check and the login state as on the add page. Password not set: the steps run at once, one camera after the other in
  parallel up to 8: set the password, add the device to OADM (name, tags), configuration files, applications,
  certificate and 802.1X. Each row shows the running step and the result per step (`ui:StatusChip`).
- **Table**: virtualized, one row per camera: name, model, serial, MAC, staging IP, planned fixed IP (editable, checked
  below the field: format, inside the target subnet, not taken), status per step, problem text. Rows can be removed
  (the camera stays in OADM).
- **Apply network**: for the chosen rows (default every finished row): writes the fixed IP, mask, gateway and DNS
  (Network plugin's checks and its API), waits until the camera answers at the new address (OADM follows the moved device,
  [discovery.md](discovery.md)), then the row is Done.
- **End provisioning**: refused while a step runs (Cancel first); restores the DHCP server, closes the firewall rule if
  it was closed before. Rows stay readable until the next start.

## Sites

Not at sites in the first version: the staging DHCP runs on the server's own network only.

## Tests

- DHCP snapshot: start, end, Cancel, plugin off and server restart during provisioning restore the exact settings (fake
  DHCP host); static leases untouched.
- Only Password not set cameras are changed (FakeCamera with a set password stays unchanged).
- Steps in order per row, a failed step stops that row only; parallelism limit with gates.
- Proposals: next free address skips OADM devices, rows and answering addresses; duplicates in the table refused.
- Apply network: written only on Apply, unreachable at the new address shows the problem, the device moves.
- PRO: the page and the steps run only with an active license or trial.
- 30,000 devices in OADM: proposals and the duplicate check stay fast.
