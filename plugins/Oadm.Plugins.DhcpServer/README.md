# DHCP server (core plugin `oadm.dhcp-server`)

Spec: `docs/specs/dhcp-server.md`, summary in `CLAUDE.md` "DHCP server plugin". Own RFC 2131 / RFC 2132 implementation
(no library), IPv4 only, one interface, starts disabled.

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
waits). Only MAC addresses of the Axis blocks in `AxisOui` (00:40:8C, AC:CC:8E, B8:A4:4F, E8:27:25, IEEE registry
2026-10-08) are handled, once per MAC and lease change, 4 at a time:

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
