# DHCP server (core plugin `oadm.dhcp-server`)

Summary in `docs/spec/bundled-plugins.md` "DHCP server". Own RFC 2131 / RFC 2132 implementation
(no library), IPv4 only, one interface, starts disabled.

## Sites

`siteSupport: none` (plugin.json): DHCP is broadcast and a relay cannot carry it. The server works only on this server's
own network; the page says "The DHCP server works only on this server's own network." while devices are at a site. The
address-in-use probe before an offer runs on the server's own network (`ICorePluginContext.Network.ForSite(null)`,
`NetworkAddressProbe`), never with sockets of the plugin. A lease to a managed device's MAC address moves that device to
the server's own network (`IDeviceAutoAdd.FollowAsync`).

## How it works

- Settings (plugin setting `config`: enabled, interfaceId, interfaceName, rangeStart, rangeEnd, autoAddAxisDevices) restored
  on server start; the plugin starts disabled. Leases (plugin setting `leases`, a JSON list of static and bound/expired/released leases,
  never offers) are written at most every 2 s when changed and on stop; they survive restarts.
- Derived, never asked: mask from the interface prefix, router = the interface's IPv4 gateway when it is in the subnet,
  DNS = the interface's IPv4 DNS servers, domain = its DNS suffix, lease 24 h (T1 50 %, T2 87.5 %), server id = the
  interface address. Shown as one line: "Clients get mask 255.255.255.0, router 10.0.0.138, DNS 10.0.0.138, lease 24 h"
  ("no router" / "no DNS", ", domain x" when there is one). Server, router and DNS addresses inside the range are skipped.
- Protocol (`DhcpEngine`): DISCOVER -> OFFER (static lease first, then the client's previous lease, the requested
  address, the next free address from a rotating cursor, then the oldest expired/released lease); REQUEST selecting
  (another server id: our offer is withdrawn), init-reboot (NAK for a wrong address or network, silent without a
  record), renewing/rebinding (NAK without a lease); DECLINE and in-use probe hits mark the address as a conflict for 1 h;
  RELEASE keeps the record (the device gets the same address again); INFORM -> ACK without lease time. Replies go to
  ciaddr when set, else broadcast (also NAKs and clients without the broadcast flag: no portable ARP injection).
  Parameter request list honored for 1, 3, 6, 15 (51/53/54/58/59 always); host name (12) echoed and stored.
- Before a new address is offered the Network plugin's probe (ping + TCP 80/443, `AddressProbe.cs` compiled in) runs
  with 2.5 s timeout, at most 32 probes at once (busy: no answer, the client retries), up to 3 addresses per request.
  Static leases are never probed.
- Abuse protection: per MAC token bucket (burst 10, 1 per 3 s, one log line per device per minute), global 500
  messages/s, table LRU 10,000 MACs with 10 min idle expiry (shared `KeyedRateLimiter`); at most 64 messages in
  flight; one pending offer per MAC (re-offered without a new probe), offers expire after 60 s, pending offers capped at
  half the pool (min 16) so a flood of new MACs cannot take the pool. Receive buffer reused, reply buffers pooled.
- Sockets per OS (`UdpDhcpSocketFactory`): Windows binds the interface address (broadcasts of the interface arrive);
  Linux 0.0.0.0 + `SO_BINDTODEVICE` (root or `cap_net_raw` before kernel 5.7); macOS 0.0.0.0 + `IP_BOUND_IF`; packets
  of other interfaces are dropped via IP_PKTINFO. A loopback binding never sends broadcasts (tests). Injectable
  `IDhcpSocketFactory`; tests use `FakeDhcpNetwork` (in memory) and loopback with random ports, never port 67 on a real
  interface.
- Other DHCP servers (`OtherServerCheck`): a discover with the broadcast flag and a random locally administered MAC from
  UDP 68 (address reuse), offers collected for 3 s (tests end it at the first offer: `OtherServerStopAfter`), the own
  address and the own server (it ignores the probe MAC) not counted. Runs on Save when enabling (not when already serving on that interface), every 10 min while running and at
  server start. Found on Save: nothing saved, the page asks with the shared confirmation popup "Another DHCP server
  (10.0.0.1) answers on this network. Running two DHCP servers causes address conflicts. Enable anyway?" and saves again
  with the servers confirmed; confirmed servers are stored per interface (`config.acceptedOtherServers`) and never warn
  again: after "Enable anyway" the status says `Running on ...`. Only a server that was not confirmed turns it into a warning.
- Status line (`DhcpStatusTexts`, `ui:StatusChip` left of the Save button, detail as tooltip; plain language, protocol details
  only in the server log): `Running on Ethernet (10.0.0.17/24)`, `Stopped`, `Port 67 is in use by another program`
  (Windows tooltip names the running DHCP Server role or Internet Connection Sharing, via `sc query DHCPServer` /
  `SharedAccess`; AccessDenied on Windows also means in use; Linux: dnsmasq, isc-dhcp-server, NetworkManager + `ss`;
  macOS: bootpd/Internet Sharing + `lsof`), `Insufficient permission to use port 67` (Linux: root or `setcap
  'cap_net_bind_service,cap_net_raw=+ep'`; macOS: sudo), `Interface <name> is not available`, `Range is not inside the
  interface subnet` (the interface address changed; re-checked every 30 s like a failed bind), `Another DHCP server
  answers on this network (<ip>)` (warning), `Address pool exhausted` (warning, 5 min after a request found no address
  or while no address is free).
- Page methods (`DhcpServerMethods`): `getState` (config, status, interfaces with an IPv4 address, per interface the
  derived values, all leases, lease version), `save` (field errors per field; `otherServers` when a confirmation is
  needed), `saveStatic` (add/edit, field errors "Mac", "Address", "Name"), `makeStatic`, `deleteStatic`, `release`
  (forgets a dynamic lease). Events: `state` (status changed), `leases` (changed + removed leases with the lease version,
  batched every 500 ms, split above 2,000 entries; the page ignores versions it already has).
- Page (`DhcpServerView`, subtitle in the host page header via `ui:PageHeader`, status chip left of Save, no card title;
  `ui:LoadingOverlay` "Reading the DHCP server" until the first `getState` answered, later reads update in place): Enable
  DHCP server; Listen on (`InterfaceSelection`, IPv4 interfaces only, "Ethernet - 10.0.0.17/24 (Intel I219)"); Start
  address / End address (`ui:FormField`, errors under the fields while typing: "Must be inside the subnet 10.0.0.0/24.",
  "Must be after the start address.", network/broadcast/server address, at most 65,536 addresses); the derived
  line directly under End address, the auto-add check box below it aligned with the inputs; Save (disabled with the reason as tooltip; status "Checking for other DHCP servers" while saving). Leases:
  summary "6 leases, 2 static", shared `SearchBox`, "+ Static lease"; one virtualized DataGrid (MAC address, IP address
  sorted numerically, Host name / device, Type, Expires "in 23 h" / "Expired" / "Released" / "-", row actions as link
  buttons: Edit, Delete for static; Make static, Release for dynamic; Delete and Release are red and confirmed). Managed devices
  by MAC = serial number (else by address) show "P3265-V (managed)". Rows update in place; search and structural changes
  rebuild the list with one reset (O(n), 5,000 leases tested).
- Static lease dialog (`StaticLeaseWindow`, `ValidatingViewModel`): MAC address, IP address (inside the subnet, may be
  outside the range, not the server address, not reserved or actively leased by another device), optional name (max 63);
  errors under the fields while typing, the server's answer under the field too.
- Tests: `tests/Oadm.Plugins.DhcpServer.Tests` (codec round trip with all options, overload, concatenation, malformed
  input and random garbage; state machine incl. selecting/other server, init-reboot, renew/rebind, decline, release,
  inform, probe conflicts, offer expiry, pending offer cap, probe concurrency; lease store: allocation, static priority,
  collisions, expiry, reclaim, persistence, 5,000 leases; rate limits; status texts per OS; the whole service on the
  in-memory network: full exchange, unicast renew, live events, restart persistence, page actions, other server +
  confirmation, port in use retried, Windows service named, interface address change, pool exhausted, flood, malformed
  datagrams; real socket on loopback; page view model incl. 5,000 leases; `AutoAddTests` with a fake `IDeviceAutoAdd`:
  Axis blocks only, once per lease change, setting off adds nothing but follows, managed devices never added, one retry,
  4 at a time, engine never blocked, the whole server on the in-memory network; headless screenshots `dhcp-server-page.png`,
  `dhcp-server-auto-add.png`,
  `-page-errors.png`, `-page-other-server.png`, `dhcp-server-other-server-confirm.png`, `dhcp-server-static-lease-dialog.png`).

## Layout

| Folder | What |
|---|---|
| `Protocol/` | `DhcpMessage` codec (header, magic cookie, options 1, 3, 6, 12, 15, 50-59, 61; overload 52; RFC 3396 concatenation; unknown options ignored; malformed input rejected), `MacAddress`, `Ip4` |
| `Leases/` | `LeaseStore`: leases by MAC and by address, conflicts, pending offers, static priority, versioned changes for the page, stored form (`StoredLease`) |
| `Serving/` | `DhcpEngine` (state machine, `Leased` callback after every ACK), `DhcpListener` (receive loop, rate limits, pooled buffers), `DhcpSockets` (per-OS UDP binding, injectable `IDhcpSocketFactory`), `OtherServerCheck`, `AxisOui` (the Axis MAC blocks), `DhcpDeviceAutoAdd` (queue to the server's `IDeviceAutoAdd`) |
| `Status/` | `DhcpStatusTexts` (status line, bind error per OS) |
| `Shared/` | page contract (`DhcpServerMethods`, `DhcpState`, requests/replies) and `DhcpValidation` (shared with the page) |
| `../Oadm.Plugins.Network/AddressProbe.cs`, `Model/Ipv4.cs` | compiled in (the Network plugin's in-use probe and IPv4 math; a project reference would register the Network tasks twice) |

## Automatically add Axis devices

Check box "Automatically add Axis devices that get an address" (config `autoAddAxisDevices`, default off, saved with
Save). After every ACK the engine hands MAC and address to `DhcpDeviceAutoAdd`, which only queues (the receive loop never
waits). Only MAC addresses of the Axis blocks in `AxisOui` (00:40:8C, AC:CC:8E, B8:A4:4F, E8:27:25, from the IEEE
registry) are handled, once per MAC and lease change, 4 at a time:

1. `ctx.AutoAdd.FollowAsync(serial = MAC, address)`: a managed device at another address is verified there by the server
   (serial with the stored credentials) and its record moved, whether the check box is on or not; devices reached by
   host name keep it.
2. Unmanaged and the check box on: `ctx.AutoAdd.AddAsync(address, serial, "DHCP server")` runs the add page's pipeline
   on the server. Anonymous Axis check, then: factory default -> added as "Password not set" (context menu "Set
   password"); credential list login works -> added as Ok; no credential fits -> added as "Credentials required"
   (context menu "Log in"). Not an Axis device, another serial or no answer -> not added (server log).
3. No answer yet (cameras answer HTTP a few seconds after DHCP) or a moved device not verified yet: one retry after 30 s,
   dropped when the lease changed meanwhile.

The server writes the audit entries ("Device added automatically", "Device moved", user "system").

## Port 67 per OS

| OS | What OADM does | What you need |
|---|---|---|
| Windows | binds UDP 67 on the interface address (Windows delivers broadcasts of that interface to it) | nothing but a free port 67: stop the **DHCP Server** role (`net stop DHCPServer`) or **Internet Connection Sharing** (`net stop SharedAccess`) if they run; allow inbound UDP 67 in the firewall |
| Linux | binds 0.0.0.0:67 with `SO_BINDTODEVICE` to the interface | root, or once `sudo setcap 'cap_net_bind_service,cap_net_raw=+ep' <Oadm.Server>` (`cap_net_raw` for the device binding on kernels before 5.7); dnsmasq / isc-dhcp-server / NetworkManager shared connections must not hold port 67 (`sudo ss -ulpn 'sport = :67'`) |
| macOS | binds 0.0.0.0:67 with `IP_BOUND_IF` to the interface index | run the server with sudo; Internet Sharing (bootpd) must be off (`sudo lsof -nP -iUDP:67`) |

When bound to 0.0.0.0, every OS also checks the interface each packet arrived on. The check for other DHCP servers uses
UDP 68 with address reuse (the OS DHCP client often holds it). Replies to clients without an address are broadcast,
because unicast to a MAC without an ARP entry is not portable (known limitation; clients accept broadcasts).

## Manual test plan (real network, only on an isolated test network)

Never run these on a production network: a second DHCP server hands out wrong addresses.

1. **Setup**: a test switch with the OADM server PC (static address, e.g. 10.0.0.17/24, gateway 10.0.0.138) and one or
   two AXIS cameras set to DHCP (factory default or Network settings "DHCP"). No other DHCP server on the switch.
2. **Start disabled**: open the DHCP server page: status "Stopped", the interface list shows only interfaces with an
   IPv4 address, the line "Clients get mask 255.255.255.0, router 10.0.0.138, DNS ..., lease 24 h".
3. **Field errors**: type 10.0.1.100 as start address: "Must be inside the subnet 10.0.0.0/24." under the field, Save
   disabled with the reason in its tooltip.
4. **Enable**: range 10.0.0.100 - 10.0.0.199, Enable, Save: status "Running on Ethernet (10.0.0.17/24)".
5. **Leases**: restart a camera: it appears as Dynamic with "in 23 h"; when the camera is managed in OADM the host name
   column shows "P3265-V (managed)". The camera's web page is reachable at the leased address.
6. **Make static** on that lease, restart the camera: same address, Type Static, Expires "-".
7. **+ Static lease** with the MAC of the second camera and 10.0.0.48 (outside the range): restart the camera: it gets
   10.0.0.48. Try the same address for another MAC: "Already reserved for ..." under the IP address.
8. **Conflict probe**: give a laptop the static address 10.0.0.100 (answers ping), release the cameras' leases, restart
   a camera: it does not get 10.0.0.100 (server log: "answers the in-use probe").
9. **Release**: Release a dynamic lease: the row disappears; the camera gets a new answer at its next renewal or reboot.
10. **Other DHCP server**: connect a router with its DHCP server on, disable and enable OADM's server: the popup "Another
    DHCP server (10.0.0.138) answers on this network. ... Enable anyway?" appears; Cancel keeps it off, Enable anyway
    starts it with the warning status.
11. **Port in use**: on Windows start Internet Connection Sharing (or on Linux `dnsmasq --port=0 --dhcp-range=...`), save:
    "Port 67 is in use by another program" with the hint in the tooltip; stop the other program: OADM starts within 30 s.
12. **Restart**: restart the OADM server: it comes back enabled with the same leases; the cameras keep their addresses
    at the next renewal.
13. **Automatic add**: add the cameras' credential on the Credentials page, check "Automatically add Axis devices that
    get an address", Save. Restart a camera that OADM does not manage: within a minute it appears on the Devices page
    (status Ok) and the audit log shows "Device added automatically". A factory-default camera appears as "Password not
    set"; "Set password" in its context menu sets the first password. Remove a managed camera's static lease so it gets
    a new address from the range: OADM follows it ("Device moved" in the audit log).
14. **Interface change**: change the server's address to another subnet: status "Range is not inside the interface
    subnet"; pull the cable: "Interface Ethernet is not available".

Automated tests (`tests/Oadm.Plugins.DhcpServer.Tests`) cover all of this on an in-memory network (`FakeDhcpNetwork`) and
on loopback with random ports. They never send DHCP traffic on a real interface.

## Device groups

SDK 0.4.0. The DHCP server serves the whole network, so `save`, `saveStatic`, `deleteStatic`, `makeStatic` and `release`
are for users with all devices (`RequiresAllDevices`; the page shows without changes for the others, `MayManage`). Users
with a device group see the page with the leases of their devices only, by MAC address (an Axis serial number is its MAC
address; `Serving/DhcpGroupView`). Never by address: the lease of another device at an address a group device had would
show to the group. Their pages get the state and the lease
changes of their devices through `PluginEventAudience.DeviceScope(key)` for the groups whose users opened the page
(`plugins/Shared/ViewerScopes.cs`, filtered in the background loop; a removed lease only by its MAC address). A publish
round never reads the device table: each group's `DhcpGroupView` is kept (`ViewerScopes.ViewAsync`) and built again on
`IDeviceScopes.Changed` for the group or after 5 minutes. A group's `getState` answers `Renew`: its page reads the state
again every 5 minutes while shown (`DhcpServerViewModel.RenewInterval`), and a group not read for 15 minutes is forgotten.
Lease changes go in one event (changes and removals together); only beyond 2,000 leases in parts that share the version
(`DhcpServerService.Parts`), and the page applies every event whose version is not older than its own. Tests: `CanaryTests`
(the canary, filtered leases and live changes), `PageViewModelTests` (no changes for a group), `GroupScaleTests` (5 groups
at 30,000 devices: no read per round, + Perf; a change and a removal of one batch, the parts, `Renew`, the page's renewal).

## Permissions

`oadm.dhcp-server:show` (the page, `getState`) and `:manage` "Change the DHCP server" (`save`, `saveStatic`,
`deleteStatic`, `makeStatic`, `release`). Without "manage" the form is read-only and Save, Static lease and the lease
actions are hidden. Operator default: show only.
