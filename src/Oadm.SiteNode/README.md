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
of the link, `AcceptedStreamListener`, J-PAKE pairing, `AddressRateLimiter`, `HelloRules`), `site_node.proto`.

## How it works

- **Data folder** `%ProgramData%\OADM Site Node` or `/var/lib/oadm-site-node` (`--data-dir`, `OADM_SITE_NODE_DATA_DIR`):
  `node.json` (`Config/NodeConfig`: id, server address, all cards or the chosen cards, extra subnets, both port lists, requests,
  binding run time left, bound server, the server's own addresses, a new server identity waiting for Accept),
  `identity.bin` (the TLS identity, PKCS#12 under AES-256-GCM) with `local.key` (32 bytes, 0600 on Linux), `logs/`
  (`site-node-*.log` rolling daily or 20 MB, 30 files, 14 days; `connections-yyyyMMdd.log` 30 days and at most 1 GB
  together, the oldest first; the same line within a minute is written once with "(N more)"; at most 10,000 lines wait
  for the disk, more are dropped and counted). Never device data or passwords. Only absent files mean a first start:
  `node.json`, `identity.bin` or `local.key` that exist but cannot be read (5 tries) fail the start, damaged content is
  kept as `.unreadable`; writes go through a temporary file flushed to the disk (`Config/DataFiles`).
- **Links** (`Links/`): the node always connects to the server and never listens. Every TLS connection it opens is a
  `SiteLink`; Kestrel serves `SiteNodeService` on it through `AcceptedStreamListener` (no socket endpoint of its own).
  `OutboundConnector` connects to the
  typed address (port 5081 unless typed), then the server's own addresses (`SetServerAddresses`), backoff 1, 2, 4 ... 60
  s, started over only after a session that lasted 30 s or served the bound server (a server that accepts TLS and
  closes at once is not tried every second); dual mode where the OS has IPv6, else IPv4. Unbound: any server key (the
  pairing proves it); bound: only the pinned key, another one is noted with where it came from (a waiting one is not
  replaced for an hour, never once accepted); **Accept** lets that identity pair with the code, it binds nothing by
  itself; binding closed: no connection. `OpenConnections` adds worker links to the same address (at most 8). Every
  handshake ends within `SiteTls.HandshakeTimeout`.
- **Calls** (`Service/SiteNodeGrpcService`): Hello to anyone; a bound node tells strangers its state, version and
  protocol version only (the bound server also gets its name and the site's). PairStart and Bind while not bound (or for
  an accepted new server identity), J-PAKE: PairStart answers with a fresh participant's rounds 1 and 2, Bind checks the
  server's round 2 and proof; PairStart 10 a minute per address and 30 in all. Calls of servers the node is not bound to
  may send at most 64 KB (`NodeHost.LinkLimits`). Everything else only to the bound identity, checked on every call
  against the binding of that moment. All links go to the one address the node chose, so they are one server session (the
  server refuses a second control link of the same identity). Kestrel keeps idle links (no
  keep-alive timeout: the HTTP/2 pings keep them). Refusals: FAILED_PRECONDITION with the text; a wrong code PERMISSION_DENIED; too many
  wrong codes RESOURCE_EXHAUSTED.
- **States** (`State/BindingState`): binding open for 24 hours of run time (`BindingClock` ticks every minute and saves),
  then closed until Allow binding; the pairing code lives in memory (new at start, Allow binding, Reset, Unbind and after
  5 wrong codes, which also refuse Bind for 10 minutes; proofs are checked one at a time, so parallel links get no more
than 5 guesses); Reset = new id, key and code, the server forgotten.
- **Reach** (`Networks/NetworkScope`): all cards (default) or the chosen cards (no chosen card = all cards; a machine that
  also sits in an office network chooses the camera cards), and the extra networks (at least /8, IPv6 /16), never
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
  itself (`--admin-request ... --out ...`, UAC or pkexec). The copy hands the answer back over its standard output on
  Linux (`--out -`, a line `OADM-ANSWER <base64>`; never a file in /tmp), in a file in the user's own temp folder on
  Windows. A dismissed prompt (UAC 1223, pkexec 126 / 127) shows nothing; no pkexec or a copy without an answer shows
  why in the app.

## Development

`manage run node` builds and runs the node on the console and `manage run node-app` its app, both on the data folder
`artifacts/dev-node` (never the installed node's; `--data` for another one). With
`manage run server`: turn on Accept site nodes on the Sites page, enter `localhost` as the server in the node app, then
Bind with the code. Changes in the app need an administrator (UAC).

## Command line

```
oadm-site-node                          run the service on a console
oadm-site-node status | pairing-code | allow-binding | accept-server | reset
oadm-site-node server <address>
oadm-site-node networks [card <name> on|off | all on | add|remove <network>]
oadm-site-node ports [allow|remove|dismiss connect|listen tcp|udp <port>]
oadm-site-node log [count]
```

`--timeout <seconds>` waits that long for the service to answer (the MSI's `SERVER=<address>` runs `server <address>
--timeout 60` right after starting the service).

**Firewall**: none. The node only connects out to the server (TCP 5081 unless the address names another port); the
installers open no port, on Windows and Linux.

## Tests

`tests/Oadm.SiteNode.Tests`: unit tests with a fake clock and fake cards (states, pairing, networks, ports, the
connection log, dial limits with gates, the admin commands, DNS and mDNS records, the range scan) and in-process tests:
the real node (Kestrel, links) against a test server over loopback, pairing end to end, dial on real loopback sockets
(refused vs site errors), relays UDP and TCP, reconnects with backoff, worker links, Reset, Accept of a new identity.
`tests/Oadm.SiteNode.App.Tests`: the app's view model and headless windows (screenshots with `OADM_SCREENSHOT_DIR`).

## Manual test plan

Before a release, with a Raspberry Pi 4 or 5 (Debian, arm64) and a Windows machine as nodes, the development camera
behind each. Read-only towards the camera.

1. **Install and first start**: install the node packages; the app asks for the terms; the service runs as its own user
   (`systemctl status oadm-site-node`, Services on Windows) and `oadm-site-node status` says "Not bound. Binding open for
   24 more hours.".
2. **Outbound through NAT**: server in the office network, node behind a NAT router. Turn on Accept site nodes on the
   Sites page, type the server's address in the app and Save (UAC / pkexec prompt): the pairing code shows below the
   address at once, Node status says "Connected to ...". Close and open the app: Show pairing code (another prompt).
   Bind on the Sites page with the code; Node status shows "Connected to SERVER", "Site NAME", "Bound since ...". A wrong code once: "The pairing code is wrong." on the server, the app lists the attempt.
   Five wrong codes: a new code, Bind refused for 10 minutes.
3. **Another server**: point the bound node at a second OADM server; it refuses the connection (the pinned key) and the
   app shows the new identity with **Accept**; nothing binds without the pairing code.
4. **Two network cards**: All cards (default) reaches both; choose only the camera card: a camera on the office network
   is "outside the networks"; All cards again reaches it; an extra network (a routed camera VLAN) is reached.
5. **Ports**: add a camera on 8443: the server asks, the app lists "Connect to TCP 8443: camera ..."; Allow; the camera
   is reached. Dismiss: the server asks again later. A port not allowed is never dialed (connection log: "Port not
   allowed").
6. **Pull the cable**: unplug the node's uplink: Site offline on the server after the waiting time; plug it in: online
   again after the stable time, the node reconnects with backoff (log), no device turns Unreachable.
7. **mDNS and range scan**: Discovery and Network range on the add page find the camera behind the node.
8. **NTP relay**: turn on NTP at sites; the app asks for Listen on UDP 123; Allow; the camera set to "Use OADM as NTP
   server" syncs; on Linux without `CAP_NET_BIND_SERVICE` the relay says "No permission to use UDP 123"; another program
   on UDP 123: "UDP 123 is used by another program".
9. **Reset**: Reset in the app (confirmed) while the node is connected: within a few seconds the old site shows **Not
   bound** on the Sites page (audit "Site node was reset", user system), its devices Site offline; the node connects
   again with a new identity and waits to be bound; bind it again with the new code and Replace the old site with it.
   Reset again with the server stopped: the node resets at once (no hang), the old site stays Offline. Then stop the
   service (and Ctrl+C a console run): it ends within seconds, exit code 0, no crash dump.
10. **Server identity lost**: replace the server's key (test server only); the app shows "... presents a new identity";
    Accept; the node connects again.
11. **Restart and clock**: restart the service and the machine; the binding time left only counts run time; a node
    without a clock (Raspberry Pi without network time) still connects (dates of the certificates are ignored).

## What should move to a shared library

The node has its own small copies of logic the server also has (stream 3 did not touch `Oadm.Core`):
`Discovery/DnsWire` + `AxisAggregator` (the server's `Oadm.Core.Discovery.Mdns`), `Devices/DnsNameRules` (the server's
`SystemDnsNameLookup.Normalize` and the verified reverse lookup), `Discovery/RangeScanner.TryParse` (`Ipv4Range`), and
the TCP first contact of a range scan. They belong in one library without EF Core that the server and the node share.
