# OADM Site Node

The node of a site (`docs/sites.md`): OADM's foot in a network the server cannot reach. A pipe: no database, no
credentials, no tasks, no plugins. Two programs:

- `oadm-site-node` (this project): the headless service (Windows service "OADM Site Node" `OadmSiteNode` under its own
  virtual account `NT SERVICE\OadmSiteNode`, systemd `Type=notify` as the user `oadm-site-node`), runs without root.
  A background part that fails stops the node with exit code 1 (`NodeFailure`; the SCM's recovery runs for stops with
  an error, systemd `Restart=on-failure`); the loops themselves log and retry their own errors (a tick that cannot be
  saved, a local channel that cannot be opened). The same exe is the command line.
- `oadm-site-node-app` (`src/Oadm.SiteNode.App`): the window, Avalonia with the shared OADM theme (`src/Oadm.Theme`).

Shared: `src/Oadm.SiteNode.Admin` (the local channel, texts, server address and subnet rules), `src/Oadm.Sites` (TLS
of the link, `AcceptedStreamListener`, SRP pairing), `site_node.proto`.

## How it works

- **Data folder** `%ProgramData%\OADM Site Node` or `/var/lib/oadm-site-node` (`--data-dir`, `OADM_SITE_NODE_DATA_DIR`):
  `node.json` (`Config/NodeConfig`: id, mode, server address, cards, all cards, extra subnets, both port lists, requests,
  binding run time left, bound server, the server's own addresses, a new server identity waiting for Accept),
  `identity.bin` (the TLS identity, PKCS#12 under AES-256-GCM) with `local.key` (32 bytes, 0600 on Linux), `logs/`
  (`site-node-*.log` rolling daily or 20 MB, 30 files, 14 days; `connections-yyyyMMdd.log` 30 days and at most 1 GB
  together, the oldest first; the same line within a minute is written once with "(N more)"; at most 10,000 lines wait
  for the disk, more are dropped and counted). Never device data or passwords. Only absent files mean a first start:
  `node.json`, `identity.bin` or `local.key` that exist but cannot be read (5 tries) fail the start, damaged content is
  kept as `.unreadable`; writes go through a temporary file flushed to the disk (`Config/DataFiles`).
- **Links** (`Links/`): every TLS connection to a server is a `SiteLink`; Kestrel serves `SiteNodeService` on it through
  `AcceptedStreamListener` (no socket endpoint of its own). Mode Connect (default): `OutboundConnector` connects to the
  typed address (port 5081 unless typed), then the server's own addresses (`SetServerAddresses`), backoff 1, 2, 4 ... 60
  s; unbound any server key (the pairing proves it), bound only the pinned key (another one is noted for **Accept**),
  binding closed: no connection. `OpenConnections` adds worker links (at most 8). Mode Wait: `InboundListener` listens on
  5081 and does TLS itself with the same options, so everything above the socket is one code path; `NodeAnnouncer`
  answers mDNS `_oadm-site._tcp` (TXT id, version, state) on every card. Every handshake ends within
  `SiteTls.HandshakeTimeout`.
- **Calls** (`Service/SiteNodeGrpcService`): Hello to anyone (the bound server's and the site's name only to that
  server); PairStart and Bind while not bound; everything else only to the bound identity, one session at a time
  (`LinkRegistry`: a second connection with that identity from another address is refused "Another OADM server with this
  identity is connected from ..."). Refusals: FAILED_PRECONDITION with the text; a wrong code PERMISSION_DENIED; too many
  wrong codes RESOURCE_EXHAUSTED.
- **States** (`State/BindingState`): binding open for 24 hours of run time (`BindingClock` ticks every minute and saves),
  then closed until Allow binding; the pairing code lives in memory (new at start, Allow binding, Reset, Unbind and after
  5 wrong codes, which also refuse Bind for 10 minutes); Reset = new id, key and code, the server forgotten.
- **Reach** (`Networks/NetworkScope`): the reached cards' networks and the extra networks (at least /8, IPv6 /16), never
  special addresses (0.0.0.0/8, loopback, 169.254/16, multicast, 240/4; IPv6 loopback, link-local, multicast) and never
  the node's own card addresses. `CardWatcher` reads the cards every 10 s and on the OS's address change; a change goes
  to the server's Watch and the app at once and relays bind again.
- **Dial** (`Devices/Dialer`, `DialPipe`): the port must be on Connect to, the address (after DNS on the node) on a
  reached card or extra subnet; short dials at most 1,024 open and 256 connecting, permanent ones at most 64 connecting;
  the socket and its place are released on every path; device socket errors come back as `DEVICE_*`, the node's
  problems as `SITE_*`. Every dial is a line of the connection log. Probes (ProbeTcp, the ping fallback, each address of a
  range scan) wait for one of 64 places; at most 4 range scans at once (RESOURCE_EXHAUSTED).
- **Relay** (`Relays/RelayHost`): UDP or TCP on the reached cards for as long as the call lives, only for ports on Listen
  on; a port that is not is requested with its reason and the relay waits; a port in use or without permission is
  reported and tried again every 30 s and after every change of the app or the cards. Only senders inside the reached
  networks are relayed and answered; a UDP sender is answered for 2 minutes after its last datagram (`UdpSenders`, at
  most 100,000); a TCP relay holds at most 256 connections.
- **Network checks and discovery** (only for reached addresses): Resolve (only the reached addresses of a name),
  ReverseLookup (a full name, not `.local` / `.arpa`, that resolves back to the address; empty outside the networks),
  Ping (the OS, else TCP 80 / 443 when on Connect to), ProbeTcp, LocalAddressFor (empty outside the networks), Browse (`_axis-video._tcp` on the reached cards),
  RangeScan (open 443 / 80 on the reached part of a range, only ports on Connect to).
- **Local channel** (`AdminChannel/`): named pipe `oadm-site-node` (Windows) or `/run/oadm-site-node/admin.sock` (Linux;
  the unit needs `RuntimeDirectory=oadm-site-node`, mode 0755); a node with its own data folder uses names derived from
  it. Every local user reads the state and the connection log; the pairing code and every change need an administrator
  (Windows: SYSTEM or an elevated token of Administrators, never the service account; Linux: root or the service user).
  A channel that cannot be opened is logged and tried again every 30 s; the node runs on without it. The app runs a change through an elevated copy of
  itself (`--admin-request ... --out ...`, UAC or pkexec).

## Command line

```
oadm-site-node                          run the service on a console
oadm-site-node status | pairing-code | allow-binding | accept-server | reset
oadm-site-node server <address> | server --wait
oadm-site-node networks [card <name> on|off | all on|off | add|remove <network>]
oadm-site-node ports [allow|remove|dismiss connect|listen tcp|udp <port>]
oadm-site-node log [count]
```

`--timeout <seconds>` waits that long for the service to answer (the MSI's `SERVER=<address>` runs `server <address>
--timeout 60` right after starting the service).

**Firewall (Windows)**: the MSI opens no port. "Wait for the server" adds the inbound rule "OADM Site Node (TCP 5081)"
(Domain and Private, for `oadm-site-node.exe`), "Connect to the server" removes it (`Admin/NodeFirewall`). The service
runs as its own unprivileged account and cannot change the firewall, so the administrator's process that sent the change runs netsh: the
elevated copy of the app, the app running as administrator, or the command line. Uninstalling removes the rule. Linux:
no firewall change.

## Tests

`tests/Oadm.SiteNode.Tests`: unit tests with a fake clock and fake cards (states, pairing, networks, ports, the
connection log, dial limits with gates, the admin commands, DNS and mDNS records, the range scan) and in-process tests:
the real node (Kestrel, links) against a test server over loopback in both modes, pairing end to end, dial on real
loopback sockets (refused vs site errors), relays UDP and TCP, one session at a time, Reset, Accept of a new identity.
`tests/Oadm.SiteNode.App.Tests`: the app's view model and headless windows (screenshots with `OADM_SCREENSHOT_DIR`).

## Manual test plan

Before a release, with a Raspberry Pi 4 or 5 (Debian, arm64) and a Windows machine as nodes, the development camera
behind each. Read-only towards the camera.

1. **Install and first start**: install the node packages; the app asks for the terms; the service runs as its own user
   (`systemctl status oadm-site-node`, Services on Windows) and `oadm-site-node status` says "Not bound. Binding open for
   24 more hours.".
2. **Outbound through NAT**: server in the office network, node behind a NAT router. Turn on Accept site nodes on the
   Sites page, type the server's address in the app: "Connected to ...". Show pairing code (UAC / pkexec prompt), Bind
   on the Sites page with it. A wrong code once: "The pairing code is wrong." on the server, the app lists the attempt.
   Five wrong codes: a new code, Bind refused for 10 minutes.
3. **Inbound**: switch the node to Wait for the server; the Sites page's Search finds it (state unbound), Add manually by
   address works; bind; a second OADM server is refused ("This site node is bound to another OADM server.").
4. **Two network cards**: the office card (default router) is not reached; a camera on the office network is "outside
   the networks"; turning on the card or All cards reaches it; an extra network (a routed camera VLAN) is reached.
5. **Ports**: add a camera on 8443: the server asks, the app lists "Connect to TCP 8443: camera ..."; Allow; the camera
   is reached. Dismiss: the server asks again later. A port not allowed is never dialed (connection log: "Port not
   allowed").
6. **Pull the cable**: unplug the node's uplink: Site offline on the server after the waiting time; plug it in: online
   again after the stable time, the node reconnects with backoff (log), no device turns Unreachable.
7. **mDNS and range scan**: Discovery and Network range on the add page find the camera behind the node.
8. **NTP relay**: turn on NTP at sites; the app asks for Listen on UDP 123; Allow; the camera set to "Use OADM as NTP
   server" syncs; on Linux without `CAP_NET_BIND_SERVICE` the relay says "No permission to use UDP 123"; another program
   on UDP 123: "UDP 123 is used by another program".
9. **Reset**: Reset in the app (confirmed); the Sites page shows Not bound; bind again with the new code.
10. **Server identity lost**: replace the server's key (test server only); the app shows "... presents a new identity";
    Accept; the node connects again.
11. **Restart and clock**: restart the service and the machine; the binding time left only counts run time; a node
    without a clock (Raspberry Pi without network time) still connects (dates of the certificates are ignored).

## What should move to a shared library

The node has its own small copies of logic the server also has (stream 3 did not touch `Oadm.Core`):
`Discovery/DnsWire` + `AxisAggregator` (the server's `Oadm.Core.Discovery.Mdns`), `Devices/DnsNameRules` (the server's
`SystemDnsNameLookup.Normalize` and the verified reverse lookup), `Discovery/RangeScanner.TryParse` (`Ipv4Range`), and
the TCP first contact of a range scan. They belong in one library without EF Core that the server and the node share.
