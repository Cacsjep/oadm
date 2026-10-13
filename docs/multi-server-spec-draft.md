# Multi-server (nodes): spec draft

Status: **parked (2026-10-13).** The design is complete; it is built only when many users ask for it. Decisions are in
"Decisions"; they move to CLAUDE.md once the feature is built.

## Goal

One OADM system that manages devices in several separated networks (VLANs, buildings, sites behind routers or NAT). The
user installs an OADM server in each network. The main server adopts it as a **node** from its **Nodes** page. After
that, the user still logs in to one server and sees one device list, one task list and one set of event rules. They
work as if every device were local.

What must hold:

- **Frictionless.** Installing a node means running the server installer; nothing is configured on the node machine.
  Adopting it is search (or address) → confirm → done.
- **One system, one brain.** All logic and all data live on the main: one database, one login, one device list, one task
  pane, one audit log, one set of roles and rules. A node is the main's hand in another network, not a second server.
- **Nodes hold no secrets.** No device passwords, no credential list, no users on a node.
- **No change for single-server users.** A server without nodes behaves exactly like today.
- **Plugins keep working without changes.** A plugin written against today's SDK works with devices behind a node.
- **Future-proof.** Target 50 nodes and 5,000 devices in total, nodes behind NAT, a **failover main** later for the case
  that the main fails, users restricted to nodes later.

Not a goal (first version): a cloud service, nodes that work on their own while cut off from the main.

## Terms

| Term | Meaning |
|---|---|
| **Main server** | The server users log in to. Has all logic and data and its own local network access. |
| **Node** | An OADM server adopted by a main. Relays the main's connections into its network and runs the network services there (NTP, DHCP). No database, users or pages of its own. |
| **Route** | The node a device is reached through (`Device.NodeId`; empty = the main's own network). |
| **Adopt** | Pair a node with a main (search or address, then confirm). **Release** undoes it. |
| **Failover main** | A later phase: a standby main with a live copy of the main's data that takes over when the main fails. |

## References

- **ADM 5 (classic):** the client connects to several independent servers; users, settings and rules exist once per
  server. Not one system.
- **ADM Extend:** a cloud organization with **sites**; an **edge host** per site dials out and works locally.
- **Milestone XProtect:** **Management Server** (configuration, users) with failover; **Recording Servers** near the
  cameras; the **Event Server** central. Recording must never stop, so Milestone puts work near the cameras.

OADM is mostly interactive management; its continuous duties are status polling, Device triggers' WebSockets, RTSP event
streams and the NTP and DHCP servers. Only NTP and DHCP must keep answering in their network whatever happens; they run on
the node. The rest runs on the main, and the main's own failure is covered by the failover main.

## Architecture: relay nodes (decided)

A federated design (every node a full OADM server owning its devices, shared setup copied down, node logins during
outages) was worked out first and dropped: it spreads users and passwords over every node and needs ownership,
catch-up and conflict rules. Relay nodes keep one brain and add resilience through a failover main instead.

### What a node does

1. **Connection relay.** The main asks the node to open a TCP connection to an address and port in the node's network.
   The bytes of that connection travel inside a gRPC stream of the node link (see "Node link protocol"). Everything the
   main does with devices runs over such relayed connections:
   - HTTP(S) to devices: `VapixClient`'s `SocketsHttpHandler.ConnectCallback` opens the connection through the device's
     node. **TLS stays end to end** between the main and the device with the pinned certificate; the node only sees
     encrypted bytes. For devices on plain HTTP the node sees the HTTP exchange (Digest, never the password).
   - WebSockets (Device triggers, Web View): the same callback (`ClientWebSocket` with the handler).
   - RTSP (live view, event streams, Metadata Monitor): `RtspClient` takes its stream from the same connector.
   - So polling, tasks, every plugin page, event rule actions, live view and Web View work unchanged; only the connector
     knows about nodes (`IDeviceConnector`: local sockets for the main's own network, the relay for a node).
2. **Searches in its network.** mDNS browse and the range scan's first contact (thousands of short connects) run on the
   node, which reports what answered. The main then probes and logs in through the relay, with its credential list.
   The address probe of the Network plugins (ping, TCP 80 / 443) runs on the node too.
3. **Network services.** NTP server and DHCP server run on the node for its network: the configuration comes from the
   main (the plugin pages get a node select), the state that must survive a cut link stays on the node (DHCP leases).
   The DHCP automatic add and following devices ask the main over the link.
Uploads (firmware, apps) go through the relay like everything else: the file crosses the node link once per device
(Q14).

A node has no database: `node.json` (the main's addresses and certificate, its own certificate) plus the network
services' state. It is the same installer and executable started in node mode.

### What stays on the main

Everything else: users, roles, sessions, audit, settings, credential list, tags, event rules, plugins, the device table
with passwords, tasks and their history, polling, the task engine, live view and the Web View relay, every plugin page.

### When something fails

| Failure | Effect |
|---|---|
| A node or its link is down | Its devices show **Node offline** (not Unreachable) with their last known state; tasks for them fail with "Node Building A is offline. Nothing was changed."; Device triggers raise "connection lost" after 30 s as today. NTP and DHCP keep answering in that network. Everything reconnects by itself. |
| The main is down | v1: nothing but NTP and DHCP on the nodes. With the failover main (later phase): the standby takes over, nodes and clients reconnect to it. |

## Pairing (adopt) flow

1. The user installs the OADM server on the node machine ("Server only", Q6). A fresh server without users and without a
   main is **adoptable** for 24 hours after it starts: it announces itself in its network (mDNS `_oadm._tcp`, TXT:
   version, adoptable=yes).
2. On the main: **Nodes** page → **Search** (mDNS in the main's networks) or **Add node** (address). The list shows found
   servers with name, address, version, "Ready to adopt" / "Already a node of another main" / "Has users (a standalone
   server)".
3. **Adopt**: the main connects, shows the node's certificate fingerprint in the shared confirmation window ("Trust this
   server?"), the admin confirms (Q3). The main issues the node a certificate from an OADM-internal CA (separate from the
   PKI plugin's device CA); the node pins the main. From then on each side accepts only the other (mutual TLS) and the
   **node dials the main** (Q2).
4. The node shows on the Nodes page as Online with its networks (interfaces, subnets), version and device count.
5. **Release** (Admin, confirmed): asks which route its devices take now (the main or another node) or removes them from
   OADM (Q8b). The node forgets the main and becomes a fresh, adoptable server. A node whose main is gone forever is reset
   locally (`Oadm.Server --release-node`), never over the network.

## What changes where

| Area | With nodes |
|---|---|
| Device list | New grid column **Node** (shown when nodes exist), filter, Group by node like Group by tag. Serial unique system-wide as today. |
| Device status | **Node offline** for devices of an unreachable node. |
| Add devices | Discovery / Network range / Add manually / Import get **From node** (one select at the top, default the main's network; hidden without nodes). The search runs on that node, probes and logins through it. Import: optional "Node" column. |
| Following moved devices | The re-find searches on every node; a device found through another node moves its route (Q8). |
| Polling, tasks, plugin pages, event rules, Web View, live view | Unchanged logic on the main; connections through the device's node. |
| Parallelism | Per node: at most 16 device connections being opened at a time and 256 open streams per node link (tunable); the task setting stays per plugin. |
| NTP / DHCP pages | A node select at the top; settings and status per node (see "Walkthrough"). |
| Credentials, PKI | Unchanged on the main. |
| Roles | Unchanged in v1; "role → nodes" stored for later (Q7). |
| Audit | Unchanged; node actions (adopt, release, routes) audited. |
| Nodes page | Admin only, rail bottom group. Columns: name, address, status (Online, Offline since, Version too old, Waiting for adoption), version, devices, networks, last seen. Actions: Search, Add node, Adopt, Rename, Release, Upgrade (later, Q5). |
| Upgrades | Main first; the main talks to nodes one minor version older. A node is small and changes rarely. |
| Packaging | Same server package; "Server only" in every installer (Q6). Nodes need no open port (they dial the main). |
| Fake mode | Two fake nodes with devices, one offline. |
| Scale | 50 nodes x 100 devices and 5 nodes x 1,000 devices; polling 5,000 devices through nodes within the 60 s round. |

## Walkthrough: everything tied to the server machine

Today one server is both "the brain" and "the network". Some code and settings describe the machine, not the system
(found in the code, 2026-10-13). Each of them needs a home with nodes.

### Plugins that run where the network is (NTP server, DHCP server)

Today: one instance per server; its settings are stored in the database (`Plugin:oadm.dhcp-server:config`, `:leases`,
`Plugin:oadm.ntp-server:config`) but hold this machine's interface id (Windows GUID, Linux name), subnet, router, DNS and
leases; the page's "Listen on" list comes from the server's own interfaces (`getState`, `InterfaceSelection`).

With nodes:

- **Node-capable core plugins.** A core plugin declares that it runs on every node (`ICorePlugin.Placement =
  EveryNode`, DIM default `Main`). The main starts one instance per node: on the node itself (the node has the bundled
  plugins), plus its own instance for the main's network.
- **Settings per node.** The main stores them per node (`Plugin:<id>:<key>@<node id>`, the main's own without suffix),
  sends them to the node over the control stream, and the node keeps a copy so it serves after a restart while the link is
  down. The node never changes them itself.
- **State per node.** What must survive a cut link (DHCP leases) lives on the node (`plugin-data/<id>/` on its disk) and
  is mirrored to the main for display and for moving a node.
- **Page.** The page of a node-capable plugin gets a **Node** select at the top (shared control, shown only when nodes
  exist; "Main server", then the nodes by name). Every page call carries the node (`InvokeAsync` routed by the host to that
  node's instance). So the DHCP page for node Building A: choose **Building A** → "Listen on" lists **that node's**
  interfaces (the node answers `getState`) → the range is checked against that interface's subnet, router / DNS / domain
  are that node's → **Save** stores the config for Building A on the main and the node binds UDP 67 there. The status line
  next to Save ("Running on Ethernet - 10.20.0.5", "Port 67 is in use by ...") is the node's.
- **Node offline.** The page shows the last known settings and status read-only: "Node Building A is offline. Its DHCP
  server keeps running with these settings."
- **SDK context on a node.** Settings (from the main, read-only), Logger and DataDirectory (local), Firewall (the node's
  own Windows rules), Events (to the page, through the main), AutoAdd and Follow (forwarded to the main, which adds the
  device routed through this node), the address probe (local). No device passwords, no `Devices` repository with secrets.
- **"Use OADM as NTP server" task:** writes the address of the NTP server **on the device's node** (that node computes it,
  as `ServerAddressFor` does today), not the main's.
- **Firewall:** a Windows node in service mode opens UDP 67 / 123 on its own machine (`FirewallRuleKeeper` runs on the
  node).

### Searches and probes in the node's network

| Today on the server | With nodes |
|---|---|
| mDNS browse on every interface (`MdnsBrowser`) | Runs on the chosen node ("From node" on the add page); results stream to the main. |
| Range scan with the anonymous probe (`RangeScanner`, `VapixDeviceProbe`: no credentials) | Runs completely on the node (anonymous, no secrets); the main then logs in through the relay. `Scan.Parallelism` and `Scan.TimeoutMs` stay system-wide and apply per node. |
| "Add manually" host name lookup (`Dns.GetHostAddressesAsync`) | Resolved by the chosen node's DNS (the node's network may have its own DNS). Relayed connections to a host name are resolved on the node too. |
| Re-find of moved devices (`DeviceRelocationHostedService`, mDNS every 5 min) | Runs on the main's network and on every node; a device found through another node changes route (Q8). |
| Address probe before a new static address (`IAddressProbe`: ping + TCP 80 / 443; Network settings, Assign IP address, DHCP) | Runs on the device's node. The Network plugins get it from a new host service (`ctx.AddressProbe`, routed by device), instead of their own `NetworkAddressProbe`. The only plugin change outside NTP / DHCP. |
| `Discovery.ZeroConfSeconds` | System-wide, applies to every search. |

### Clock and time zone

- "Date and time" with **Sync with server time** sends the main's UTC time and, for a camera behind a node, **that node's
  time zone** (reported in its hello; Q16). The dialog says "Server time (Building A: Europe/London)" when the selection
  is behind one node, "Each node's time zone" for several.
- The NTP server on a node serves **the node's clock** with its own upstream (the node must reach the upstream; without
  one it serves its clock at stratum 10 as today).

### Server identity

- `Server.Name` is stamped on every device as `Device.ServerName` (polling, add); the client shows it nowhere. With nodes
  it becomes the name of the device's node (the main's name for the main's devices); the visible part is the Node column.
- The Settings page's **Server name** and **Listen URL** are the main's. A node's name is set on the Nodes page (Rename);
  its listen address stays local (it dials out) and has no page.
- Each node keeps its own TLS certificate (adoption, mutual TLS) and `Server.ListenUrl`; neither has a page on the main.

### Files and logs

- Uploads, the Web View spool, System report jobs, the database and the master key stay on the main's disk.
- A node writes its own server log (`<data>/logs`) and keeps its network services' state; nothing else.
- Today no server log can be read from the client at all. Reading a node's log from the main comes later (Q18); in v1 it
  is read on the node machine.

### Event rule actions without devices

- **Send email** and **Send HTTP request** get **Send from**: "Main server" (default) or a node (Q19). With a node the
  action's connection goes through that node's relay like a device connection: TLS (HTTPS, SMTP with STARTTLS / TLS)
  still runs between the main and the target, the node sees no content. Notifications uses the host's connector for the
  chosen node (`SocketsHttpHandler.ConnectCallback`, MailKit `ConnectAsync(Stream, host, port, ...)`); the choice is a
  DynamicChoice field listing the nodes. A node offline: the run fails "Node Building A is offline." (event log). Send test
  sends from the chosen node too.

### What needs no change

Polling, tasks (except the address probe), live view and RTSP event streams, Web View, Device triggers, Metadata Monitor,
I/O Monitor, Snapshot report, VAPIX Commander, Hardening scan, Image Health, System report, Edge Recording Export, PKI
(the CA and "Install in trusted root store" are the main's), `Devices.RtspPorts` and `Devices.UseHostName` (both per device,
resolved on the device's node), uploads and their limits.

## Node link protocol

The node link is **gRPC** like everything else in OADM (`nodes.proto`, service `NodeLink` on the main, HTTP/2 with mutual
TLS, dialed by the node):

- `Control` (one bidirectional stream per node): hello with versions, capabilities and networks; heartbeat every 10 s;
  commands from the main (search mDNS, scan a range, probe addresses, apply NTP / DHCP configuration) and their results.
- `Relay` (one bidirectional stream per relayed connection): the first message names the target {host, port, timeout},
  then both sides send `bytes` chunks (at most 64 KB each), and the end is close or an error ("Connection refused",
  "Timed out"). Because the node dials the main, the main cannot call the node directly: the main asks for a relay on
  the control stream and the node opens the `Relay` call (reverse tunnel), or the node keeps a small pool of idle relay
  calls open.
- Why bytes in gRPC streams and not one gRPC message per VAPIX request: one mechanism carries HTTP, HTTPS, WebSocket and
  RTSP; TLS stays end to end between main and device, so no password or decrypted answer reaches the node; the node needs
  no VAPIX, RTSP or plugin logic and almost never changes; the main's code (and every plugin) keeps its normal HTTP and
  socket clients.
- Protocol version in the hello; the main refuses a node it cannot talk to with "Version 2.9 is too old. Upgrade the node to
  3.0 or later." The hello lists the node's capabilities, so later phases (direct video, upgrade) switch on per
  node.
- Backpressure per stream (HTTP/2 flow control): one slow camera never stalls the others.

## Failover main (later phase)

- A second OADM server adopted as **standby main**: it receives a continuous copy of the main's database (SQLite online
  backup or WAL shipping, seconds behind), the master key and the node CA.
- Nodes and clients know both addresses (the client's server list keeps both).
- Takeover: manual ("Make this the main server") first; automatic later with a tie-breaker so two mains never act at once:
  a main acts only while it reaches more than half of its nodes.
- Spec of its own before it is built.

## Phases (each one ships)

1. **Nodes and relay**: Nodes page, adopt / release, node link, connection relay, routes, Node column and filter, Add
   devices from a node, Node offline, **Send from** a node for email and HTTP actions (Q19). Everything device-related
   works through nodes from here on.
2. **Network services on nodes**: NTP and DHCP per node, the address probe on the node.
3. **Direct video** from the node (Q4).
4. **Failover main**.
5. **Pushed upgrades** (Q5), **roles per node** (Q7), **node logs from the main** (Q18).

## Development branch

All multi-server work happens on its own branch, **`feature/multi-server`**, created from `main`; nothing of it is committed to
`main` directly. This spec is the exception: it sits on `main` as a parked draft.

- `main` is merged into the branch regularly (at least after every release), so the branch never drifts far.
- The branch builds and passes the unit tests at every commit; each phase is a series of small commits on it.
- It is merged into `main` through a pull request (the check "tests ubuntu-latest") only when the maintainer decides, at
  the earliest after phase 1 works end to end; releases from `main` meanwhile contain no node code.
- Hardware tests with real nodes run from the branch, opt-in like every hardware test.

## Open questions

(none)

## Decisions

- **Q1 / Q10 (2026-10-13, revised): relay nodes.** All logic and data on the main; a node relays TCP connections into its
  network inside gRPC streams (TLS end to end between main and device), runs searches and the network services (NTP, DHCP)
  there, and holds no secrets. The main's failure is covered by a failover main in a later phase. Replaces the federated
  design (nodes as full servers with node logins), which was dropped together with its questions (Q12 rules on nodes, Q13
  node logins).
- **Q2 (2026-10-13): the node dials the main.** After adoption the node keeps one outgoing connection (mutual TLS) and
  reconnects by itself (1, 2, 4 ... 30 s). Only the main's port must be reachable. Adopting from the main's Search / Add
  node needs the main to reach the node once; for a site behind NAT, `Oadm.Server --join <main address>` on the node makes
  it show up on the Nodes page as "Waiting for adoption".
- **Q3 (2026-10-13): fingerprint confirmation, no code.** The main shows the node's certificate fingerprint in the shared
  confirmation window; the admin confirms. With relay nodes a node holds no secrets, so a confirmed fake node gets no
  passwords; it could see the traffic of devices on plain HTTP and drop connections. Mitigations: a fresh node is
  adoptable only for 24 hours after it starts (then a restart or `--join`); the main warns when a node with the same name
  or address was adopted before.
- **Q4 (2026-10-13): video and Web View through the main in v1, direct from the node later.** Direct needs the node to run
  the live view relay with a short-lived ticket from the main (phase 3); the client falls back to the main automatically.
  No password reaches the client.
- **Q5 (2026-10-13): upgrades pushed from the main, in a later phase.** v1 by hand; the Nodes page shows nodes that are
  behind ("Node runs 2.6.0, main 2.7.0"). Later: **Upgrade** sends the installer for the node's platform over the node link;
  the node checks its SHA-256 against the release's `SHA256SUMS.txt`, installs itself and reconnects.
- **Q6 (2026-10-13): "Server only" in every installer.** The Windows MSI and the macOS .pkg get a third choice "Server only"
  (today the client is always installed there; Linux already has `oadm-server` alone). No node-specific choice: any fresh
  server is adoptable.
- **Q7 (2026-10-13): roles per node later, model ready now.** v1 roles are system-wide; "role → nodes" (empty = all) is
  stored so a later version can limit lists, tasks, live view and events to some nodes.
- **Q8 (2026-10-13): a device keeps the route it was added through.** Another node finding it shows "Already added (Node
  A)". The route moves automatically only when the device is unreachable through its node and answers through another one
  with the same serial and login (audited "Device moved from Node A to Node B"). Context menu **OADM > Node** changes the
  route by hand.
- **Q8b (2026-10-13): Release asks.** "Route its 120 devices through: [main / another node]" or "Remove them from OADM".
  Works for a dead node too.
- **Q9 (2026-10-13): the UI name is "Node".** Rail page "Nodes", column "Node", status "Node offline", the main is "Main
  server".
- **Q11 (2026-10-13, revised): a standalone server can be adopted.** Adopt shows "Has users and 240 devices" and asks to
  confirm: its devices are added to the main with their passwords and tags, routed through the new node; its own users,
  settings, rules and database are archived in its data folder and it continues as a node.
- **Q14 (2026-10-13): uploads always through the relay, no file cache on nodes.** A node never gets a password, also not
  in memory. A cache would need the node to send the upload itself (it cannot write into the main's end-to-end TLS) and so
  the camera's password (Basic over HTTPS on AXIS OS 12). Cost: the file crosses the node link once per device (200
  cameras x 80 MB = 16 GB; about 25 minutes of a full 100 Mbit link, a few minutes on 1 Gbit; the firmware task runs 4
  cameras at a time anyway).
- **Q15 (2026-10-13): failover is decided when it is built.** Takeover (manual, automatic with the node majority) gets its
  own spec. What v1 keeps open for it: `node.json` holds a list of main addresses, the client's server list can hold
  two, and the node CA and the master key can be copied to a standby.
- **Q20 (2026-10-13): a Node select at the top of node-capable plugin pages.** One page; a shared Node select right of the
  page title ("Main server", then the nodes by name; hidden without nodes; the last choice remembered per client).
  Everything below is the chosen node's: interfaces, range, router / DNS, leases, status.
- **Q16 (2026-10-13): "Sync with server time" uses the time zone of the camera's node.** UTC comes from the main; the zone
  from the node (in its hello). The dialog names it: "Server time (Building A: Europe/London)", or "Each node's time
  zone" for a selection across nodes.
- **Q17 (2026-10-13): `Device.ServerName` = the name of the device's node.** It is shown nowhere today; the Node column is
  the visible part.
- **Q18 (2026-10-13): node logs from the main come later.** v1: the log is read on the node machine
  (`<data>/logs`). Later: Download log on the Nodes page (and the main's on the Logs page).
- **Q19 (2026-10-13): email and HTTP actions can be sent from a node, in phase 1.** "Send from: Main server / <node>"; the
  connection goes through the node's relay (TLS from the main, the node sees no content).
