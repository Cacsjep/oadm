# DHCP server core plugin (spec)

Status: decided with the user on 2026-10-07. Plugin id `oadm.dhcp-server`, core plugin with a rail page.
Own implementation (no maintained .NET DHCP server library; full control needed for reservations).
Same simplicity as the NTP server: enable, interface, address range, save; lease list; static leases.

## Page (one card + lease list)

```
DHCP server                                  [● Running on Ethernet (10.0.0.17/24)]   <- status (StatusChip)

  [x] Enable DHCP server
  Listen on      [ Ethernet - 10.0.0.17/24 (Intel I219)      v ]   interfaces with an IPv4 address (no "all")
  Start address  [ 10.0.0.100 ]     End address [ 10.0.0.199 ]     must be inside the interface subnet
                                                         [ Save ]

  Leases                                    [ + Static lease ]        <- one virtualized grid
  MAC address         IP address    Host name / device       Type     Expires
  B8:A4:4F:63:13:39   10.0.0.48     P3265-V (managed)        Static   -               [edit] [delete]
  AC:CC:8E:5F:60:71   10.0.0.101    axis-accc8e5f6071        Dynamic  in 23 h         [make static] [release]
```

- Everything else is derived from the selected interface, not asked: subnet mask (interface prefix),
  router (the interface's gateway if it has one in the subnet, else none), DNS servers (the interface's
  DNS servers), lease time 24 h, server identifier = interface address. Shown read-only in one small
  secondary line under the range ("Clients get mask 255.255.255.0, router 10.0.0.138, DNS 10.0.0.138,
  lease 24 h"). No advanced section in v1.
- Static lease dialog: MAC address, IP address (inside the subnet, may be outside the dynamic range,
  must not collide), optional name; field errors under the fields. "Make static" turns a dynamic lease
  into a static one in one click. Leases of managed devices show the device name.
- Lease list is virtualized, sortable, searchable (shared SearchBox), scale to thousands.

## Status line (always one of, cross-platform)

`Running on <interface> (<addr>/<prefix>)` (ok), `Stopped` (neutral), `Port 67 is in use by another
program` (error; Windows: mention the Windows DHCP Server role / ICS; Linux: dnsmasq/isc-dhcp/
NetworkManager), `Insufficient permission to use port 67` (error; Linux: root or CAP_NET_BIND_SERVICE +
CAP_NET_RAW if raw sockets are needed; macOS: sudo), `Interface <name> is not available`, `Range is not
inside the interface subnet`, `Another DHCP server answers on this network (<ip>)` (warning, see below),
`Address pool exhausted` (warning).

## Protocol (RFC 2131 / RFC 2132)

- IPv4 only in v1. Listen UDP 67 on the selected interface, reply to 68. Messages: DISCOVER -> OFFER,
  REQUEST -> ACK/NAK (selecting, init-reboot, renewing, rebinding), DECLINE (mark address bad for a
  while), RELEASE, INFORM -> ACK. Broadcast vs unicast replies per the broadcast flag/ciaddr rules;
  where the OS cannot unicast to a client without an address, broadcast (document the limitation).
- Options: 1 subnet mask, 3 router, 6 DNS, 12 host name (echo/store), 15 domain name if the interface
  has one, 51 lease time, 53 message type, 54 server id, 55 parameter request list honored, 58/59
  renewal/rebinding (50%/87.5%), 61 client id, 50 requested address. Unknown options ignored.
- Before offering an address: check it is not answered by ping/ARP-free TCP probe (reuse the Network
  plugin's address probe) and not leased; conflicts are marked and skipped.
- Leases persisted (small EF table or plugin storage) so they survive restarts; static leases always
  win; expired leases reclaimed.

## Safety

- Rogue DHCP is dangerous: when enabling (and every 10 min while running) the plugin sends a DHCPDISCOVER
  probe on the interface and listens for OFFERs from other servers; if one answers, show the warning
  status and refuse to enable until the user confirms in a popup ("Another DHCP server (10.0.0.1)
  answers on this network. Running two DHCP servers causes address conflicts. Enable anyway?").
- Rate limits: per-MAC and global message rate limits (token buckets, LRU-capped tables) so floods of
  DISCOVERs cannot exhaust the pool or CPU; per-MAC at most one pending offer; offers expire after 60 s.
- Bounded memory and pooled buffers, same as the NTP server.

## Cross-platform and tests

- Windows, Linux, macOS (binding to port 67 on a specific interface differs per OS: use SO_BINDTODEVICE
  on Linux where available, bind to the interface address + broadcast reception elsewhere; document).
- Tests: packet codec (all options, malformed input), state machine (DISCOVER/OFFER/REQUEST/ACK/NAK/
  DECLINE/RELEASE/INFORM, renew/rebind), lease allocation (range, static priority, exhaustion,
  conflicts, expiry), rate limits, rogue server detection with a fake second server, status mapping,
  persistence, page view model with 5,000 leases, headless screenshots. No test ever runs a DHCP server
  on the user's real network automatically (in-process tests on loopback/fake transport only); a manual
  test plan goes into the plugin README.
