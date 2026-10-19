# Sites (planned)

Status: design. First agreed 2026-10-15 as "Agents" (branch `feature/agents`, `docs/agents.md`); re-evaluated against
main (v3.6.0-rc.1) and renamed on 2026-10-19. Nothing is built yet except the first refactor on that branch
(`IDeviceConnector`). Defaults marked **(default)** were not asked and may change.

## Purpose

One OADM server manages devices in networks it cannot reach directly: a closed camera network behind a recording
server's second network card, another building, a remote site. Instead of a second OADM server (ADM's multi-server
model) a small **OADM Site Node** runs in that network and is OADM's foot in it.

The site node is a pipe. It keeps no database, no credentials, no tasks and no plugins. Everything stays on the one
server: devices, credentials, tasks, event rules, compliance, plugins, the audit log.

```
Client ──► OADM server ──TLS 5081──► OADM Site Node, site "Building B" ──► cameras 192.168.0.x
           (everything)               (pipe, no data)
```

## Decisions

| Topic | Decision |
|---|---|
| Name (2026-10-19) | **Site** in the UI: rail page **Sites**, device column **Site**, status and event **Site offline**. The program is **OADM Site Node**. One site = one remote network with exactly one node in v1. "Agent" was dropped (reads as AI), "network" clashes with Network settings. |
| Model (2026-10-19) | Kept: one server, nodes are pipes. The parked multi-server "nodes" draft (full OADM servers as relays, 2026-10-13) stays dropped. |
| Direction | The **server connects to the node** (TLS, port 5081 **(default)**). Nodes the server cannot reach (behind NAT) are a later mode where the node opens the connection; the protocol stays the same (see Transport). |
| Finding | An unbound node announces itself over mDNS. The Sites page finds it with **Search** (mDNS) or **Add manually** (address). mDNS reaches only the server's own segment, so Add manually must be just as easy. |
| Binding | **First server wins**: no pairing code. The first OADM server that binds a node owns it; the node then refuses every other server. Binding is open for 24 hours of run time **(default)** after install or Reset, then closed until **Allow binding** in the node app. |
| Removing | From OADM (rare), with "Keep devices" or not. Locally with **Reset** in the node app when the server is gone. |
| Reach | The node reaches the devices on the **network cards chosen in the node app** ("Reach devices on this network", or all cards). Default: every card except the one with the default router (the office side). Any port on those networks; no port lists, no approvals. Optional extra routed subnets (camera VLANs) in the app. The server cannot widen the reach. |
| Local UI | A small desktop app (Avalonia, OADM look) next to the headless service. Most installers do not use command lines. |
| Installers (2026-10-19) | **Windows MSI and Linux .deb only** (amd64 and arm64). No portable ZIP, no macOS node in v1. |
| Scope v1 | All device traffic, see "Scope". |
| Site offline (2026-10-19) | A **new device status `SiteOffline`, computed, never stored**: the stored status stays the last real one, so a node that comes back restores every device without writes. No polls, new tasks and plugin calls refused before any request. |
| Event (2026-10-19) | **Server scope, no new event scope**: one stateful server event per site "Site offline: &lt;site&gt;" and **Any site offline** (`count`, `{site}` names), like the "Any device" twins. Devices of an offline site fire no "Device unreachable". |
| Compliance (2026-10-19) | Devices of an offline site are **Not checked: Site offline** in every check (Reachable and Login works included); no compliance event turns on because of it. |
| Limits (2026-10-19) | **Permanent streams and short connections are counted apart** (see Transport). |
| Plugins (2026-10-19) | Declared per package in `plugin.json` (`siteSupport`, `sitePorts`, written by the plugin project SDK); a DIM on `ITaskPlugin` / `IComplianceCheckType` only where one item differs. `IPlugin` stays unchanged. Not declared = "Not tested at sites", still runs. |
| Notifications (2026-10-19) | Send HTTP request, Send email and Send TCP message stay on the server's own network in v1 (a "Send through site" option can come later). |
| Roles (2026-10-19) | New permission **`sites.view`** "See sites" (group Devices, Operator and Viewer yes). Bind, rename, change address, replace and remove: **Administrator only**, never grantable. Adding devices at a site needs `devices.addRemove` ("Add and remove devices"), as every add mode, the waiting list and Import from ADM today. |
| Node app (2026-10-19) | Asks for the terms of use (`TERMS.md`) on its first start like the client; follows the OS's dark or light setting. |
| Updates | Manual, with the node's installer. OADM shows "Update Building B" for a missing feature and refuses only a node below its minimum version. No push updates in v1. |
| Moving devices | **Automatic**: a device found by its MAC address through another route moves after the same checks as today's re-find, under the rules in "Moving devices". |
| Add page | **Every mode checks everywhere by itself**: Discovery, Network range, Add manually, Import from file, Import from ADM and the waiting list try the server's own network and every online site that reaches the address. No choice to make; a column Site shows where each device answered. A device answering through a site and the server's own network is added **through the site**. |

## Scope

Everything that talks to a device goes through its route. Verified against main on 2026-10-19 (connection inventory):

- VAPIX (`IVapixClient`): polling and full refresh, every task plugin, every core plugin page (Snapshot report, VAPIX
  Commander, Hardening scan, Image Health Dashboard, I/O Monitor, Edge storage, Action Rule Editor over SOAP, System report
  streaming, Edge Recording Export downloads), event rule actions (Set output, Run VAPIX command), compliance checks that
  read devices (Application running, Matches configuration file, SD card), device Log in and Set password
  (`DeviceLoginService`, `DeviceFirstPasswordService`), the add page's login (`DiscoveryAuthenticator`, `DeviceAdder`),
  the automatic add (`DeviceAutoAddService`), the waiting list (`WaitingDeviceRunner`), following moved devices.
- WebSockets (`ClientWebSocket` with the routed handler): Device triggers, the Web View relay.
- RTSP (`RtspClient`, with the per-device RTSP port of `Devices.RtspPorts`): live view, PTZ and zoom / focus live view,
  Metadata Monitor, event streams.
- Discovery: zero-conf, range scans, typed addresses and host names, DNS names ("Use DNS names instead of IP
  addresses"), imports.
- Network checks: the address-in-use probe of Network settings and Assign IP address (`AddressProbe`: ping + TCP 80 / 443),
  "my address towards the device" and the device's host name of "Use OADM as NTP server".

## Site node program

Two parts, one installer:

- **Service** `oadm-site-node` (headless; Windows service "OADM Site Node", systemd unit). No plugins, so Native AOT is
  allowed (CLAUDE.md: helpers without plugin loading); the mDNS, probe and DNS code it shares with the server moves to a
  small library without EF Core first, else self-contained single-file. Runs **without root**: port 5081, mDNS 5353 with
  address reuse, ping through the OS without raw sockets (Windows `IcmpSendEcho`, Linux `ping_group_range`, else a TCP
  check of 80 / 443). A relay of a port below 1024 (NTP 123) needs the right to bind it (Linux `CAP_NET_BIND_SERVICE` on
  the unit; the Windows service account may bind it).
- **App** "OADM Site Node" (Avalonia). The theme (`Oadm.Client/Themes`: `Colors.Dark.axaml`, `Colors.Light.axaml`,
  `OadmTheme.axaml`, `Icons.axaml`, `OadmColors`) moves to a shared assembly the client and the app both use; the app
  takes the OS's dark or light setting (it has no client settings). It may use `Oadm.Sdk.Client` controls (`StatusChip`,
  `MessageWindow`, `PasswordBox`, `FormField`). First start: the terms of use like the client (`TermsWindow`, the
  accepted SHA-256 in the app's own settings). One window:
  - state: "Not bound. Binding open for 23 more hours." / "Binding closed." with **Allow binding** (another 24 hours) /
    "Bound to SERVER (address) since ...";
  - the site name (from the server once bound) and the node's id;
  - **Networks**: each network card with its addresses and a switch "Reach devices on this network" (default: on for
    every card except the one with the default router), **All cards** as a shortcut, and **Extra subnets** (routed camera
    VLANs: add, remove);
  - the relays open now ("NTP, UDP 123, for SERVER") and their problems ("UDP 123 is used by another program");
  - the connections now (short and permanent, see Limits) so a busy node is visible;
  - **Reset** (red, confirmed: "The node forgets SERVER and gets a new identity. Bind it again in OADM.");
  - an identity change of the server: "SERVER presents a new identity (fingerprint). **Accept**" (see Binding);
  - version.

  It talks to the service over a local channel (Windows named pipe, Linux Unix socket): everyone may read the state;
  every change needs administrator rights (UAC on Windows, `pkexec` on Linux). Headless machines use `oadm-site-node
  status`, `allow-binding`, `networks`, `accept-server`, `reset`.
- **Data** (`%ProgramData%\OADM Site Node`, `/var/lib/oadm-site-node`; service account only): `node.json` {id, port,
  reached cards, all cards, extra subnets, binding run time left, bound servers (a list with at most one entry in v1:
  name, identity fingerprint, bound time)}, the node's TLS key (Windows a CNG key like the server's, else encrypted with a
  local key file). No device data, no passwords, ever.
- **Identity**: a random id and a new key at first start and at every Reset (a copied VM keeps the original's identity
  until Reset; see Binding). The node's certificate is self-signed and valid for 100 years; both sides check
  fingerprints only and ignore certificate dates (a Raspberry Pi has no clock and a closed network often no NTP).

### States

| State | Answers | mDNS |
|---|---|---|
| Unbound, binding open | `Hello`, `Bind` | `_oadm-site._tcp`, TXT `id`, `version`, `state=unbound` |
| Unbound, binding closed | `Hello`; `Bind` refused: "Binding is closed on this site node. Click Allow binding in the OADM Site Node app." | TXT `state=closed` (Search shows it greyed with that text) |
| Bound | `Hello` to anyone; everything else only to the bound server's identity, one session at a time | TXT `state=bound`, `server=<name>` |

The binding window counts the service's run time (saved every minute), not the wall clock.

## Binding

1. The Sites page finds the node (Search lists unbound nodes, greys out bound and closed ones; Add manually asks for an
   address and optional port).
2. **Bind** (Administrator; the site name, default the node's host name): the server opens TLS to the node, records the
   node's fingerprint and sends `Bind` signed with its **site identity key**. The node stores that key's fingerprint and
   is bound. Audited "Bound site".
3. From then on every connection is mutual TLS: the server presents its site identity, the node its own key; each side
   checks the pinned fingerprint.
4. The server refuses to bind an id or a fingerprint it already knows: "This site node is a copy of Building B. Click
   Reset on the copy." (cloned VMs).
5. **Remove site** (Administrator, red, confirmed): "Remove site Building B?" with **Keep devices** (they stay as Site
   offline, to be moved by Replace or the automatic move) or not (removed with their credentials and tags, one
   transaction like `RemoveManyAsync`). The server sends `Unbind`; a node that does not answer is forgotten anyway ("The
   site node did not answer. Reset it in the OADM Site Node app."). Audited "Removed site".
6. **Reset** in the node app: new id and key, the server forgotten, binding open for 24 hours. OADM shows the site as
   **Not bound** (its devices Site offline) until it is replaced or removed.

**Server identity.** The server binds with its own **site identity key** (ECDSA P-256, setting `Sites.Identity`,
encrypted with the master key, purpose `oadm:site-identity`), not with its TLS certificate. So
`--Oadm:RegenerateTlsCertificate`, a new TLS certificate after a master key replacement and a server that moved to another
address keep their sites. It is in the backup (`oadm.db` + `master.key`), so a backup restored on a new server takes its
sites along, like `Server.Id` (plugin licenses). `RotateIdentity(new fingerprint)`, signed with the old key, replaces it.
If the key is lost (master key replaced: add it to the `Security.KeyReplaced` notice and the error log's list), the node
app shows "SERVER presents a new identity (fingerprint). Accept" (administrator). A node serves **one server session at a
time**: a second connection with the same identity is refused ("Another OADM server with this identity is connected from
10.0.0.5"), so a restored test copy of the server cannot drive the production nodes alongside the real one.

**Known risk of "first server wins"**: the node holds no passwords, but whoever binds it gets the pipe into the networks
it reaches (factory-default cameras, default passwords, old firmware). Limits: only the network cards chosen in the app
(by default not the office side); binding open for 24 hours of run time only; the installer's own bind fails at once
("Bound to another server"); the app shows which server owns the node; every bind is audited; a bound node never accepts
another server. A pairing code can be added later without changing the protocol.

## Transport and protocol

gRPC over HTTP/2 + TLS on the node's port, `site_node.proto` (`SiteNodeService`, hosted by the node, called by the
server).

- **Two connections per node**: **control** (`Hello`, `Watch`, `Ping`, `ProbeTcp`, `Resolve`, `ReverseLookup`, `Browse`,
  `LocalAddressFor`; the heartbeat) and **worker** (every `Dial` and `Relay`: polls, tasks, uploads, live view,
  WebSockets). A firmware upload or a live view never delays the heartbeat, so it never causes a false Site offline. The
  node's Kestrel allows 4,096 streams per connection **(default)**; the server sets `EnableMultipleHttp2Connections`, so
  the worker side opens further connections by itself when one is full (10,000 trigger WebSockets need at least three).
  A separate connection for large transfers can be added later inside `ISiteTransport` if the perf test shows polls
  waiting behind uploads on slow links.
- **Online check** on the control connection only: HTTP/2 keepalive pings plus `Watch`. Offline after 30 s without an
  answer, online again after 60 s stable **(defaults)** (no flapping).
- **`ISiteTransport.OpenStreamAsync(site, host, port, kind, deadline) -> Stream`** is the only way the server opens a
  connection through a node (`kind` = Short or Permanent, see Limits). The later NAT mode (the node opens TCP + TLS to
  the server, the server runs the same `SiteNodeService` client over that socket) changes only the transport, not
  `site_node.proto` or any caller.
- **Capabilities**: `Hello` returns `protocol_version` and `features` (`dial`, `browse`, `resolve`, `relay.udp`,
  `relay.tcp`, `local-address`, ...). The server refuses a node below its minimum version ("Site node too old. Update
  it."); a missing feature turns off only that feature ("Update Building B to serve NTP").
- Calls:
  - `Hello` -> id, version, protocol version, features, state, bound server name.
  - `Bind(site name, signed by the server identity)`, `Unbind`, `Rename`, `RotateIdentity(new fingerprint, signed by the
    old key)`.
  - `Watch` (server stream): state, network cards, reached cards and subnets, relays and their problems, connection
    counts, now and after every change.
  - `Dial` (bidirectional stream): first message host or address, port, kind, deadline; then bytes both ways; an end flag
    per direction is a TCP half-close; cancelling the call closes the socket; the node stops connecting when the deadline
    passes. The device's own socket error (refused, timed out, unreachable, reset) comes back as that error; node
    problems ("outside its networks", "busy", "offline") are site errors (see Server).
  - `Resolve(name)` and `ReverseLookup(address)`: DNS on the node's side (typed host names, devices added by name, "Use
    DNS names" with the same rules as `SystemDnsNameLookup`: a full name, not ".local" or ".arpa", resolving back to the
    same IP).
  - `Ping(address)`, `ProbeTcp(address, port)`: the address-in-use check of Network settings and Assign IP address (the
    add page never pings).
  - `LocalAddressFor(address)`: the node's own address towards a device ("Use OADM as NTP server").
  - `Browse(duration)` (server stream): mDNS `_axis-video._tcp` results on the reached cards.
  - `RangeScan(from, to, parallelism, timeout)` (server stream): the first contact of a range scan on the node (thousands
    of short connects stay local); the server then probes and logs in through `Dial`.
  - `Relay(protocol, port)` (bidirectional stream): the node listens on that port on the reached cards and passes every
    datagram or connection to the server; answers go back to the sender. The relay lives as long as the stream. Problems
    ("UDP 123 is used by another program", "No permission to use UDP 123") come back like `NtpStatusTexts`.
- **Limits per node** **(defaults)**:
  - **Short** dials (polls, tasks, page reads, scans, probes): 1,024 open, 256 connecting at once; over it "Site busy",
    retried, never a device error.
  - **Permanent** dials (Device trigger WebSockets, RTSP live view, metadata and event streams, Web View WebSockets): no
    own cap below the device limit (30,000 per server; Device triggers watch at most 10,000), at most 64 connecting at
    once. The node app and the Sites page show both counts. The outgoing port range of the node's OS bounds them (Windows
    16,384 by default): the user docs say so, like Device triggers today.
- **IPv4** and global / unique-local **IPv6**; link-local addresses with a zone id are refused in v1.

## Server

- **Data model** (new migration `Sites`; migrations are never deleted): table `Sites` {Id, Name (1..64, unique
  case-insensitive), Address, Port, NodeKeyFingerprint, NodeId, BoundUtc, LastSeenUtc (written at most once a minute),
  Version, NotBound (bool)}; online / offline lives in memory only. `Device.SiteId` (nullable = the server's own
  network), `WaitingDevice.SiteId` (nullable = try everywhere; set by the import's Site column). At most 100 sites
  **(default)**.
- **Proto**: `sites.proto` `SiteService` for the client (`ListSites`, `WatchSites`, `FindNodes`, `BindSite`,
  `RenameSite`, `ChangeSiteAddress`, `ReplaceSite`, `RemoveSite`), every method in `AccessPolicy` (`sites.view` or Admin;
  the contract test fails on a missing rule); `DeviceInfo.site_id`, `DiscoveredDevice.site_id`, `WaitingDevice.site_id`;
  `DeviceStatus.SITE_OFFLINE = 6` in `common.proto`.
- **Status `SiteOffline`** (SDK `Oadm.Sdk.Devices.DeviceStatus.SiteOffline = 6`, `SdkInfo.Version` raised,
  `dev/compatibility.md`). Computed when a device is read or published (`DeviceRepository` reads, `DeviceChangeFeed`):
  the stored status stays the last real one; a site going offline or online publishes one Updated change per device of
  it in one batch. **No status switch in the code is exhaustive today (all end in `_ =>`)**, so step 1 adds the case
  everywhere a status decides whether a device is contacted or how it is shown, with a test per place:
  - mapping and texts: `Oadm.Server/Mapping/Mappers.cs`, `Oadm.Client/Devices/DeviceStatusInfo.cs`,
    `Oadm.Sdk/Devices/DeviceMessages.cs` (`StatusText`, `Refusal`: "Site offline: Building B" so plugins never contact),
    `Oadm.Sdk/Plugins/TaskSupportReasons.cs`, `Oadm.Sdk.Client/Controls/DeviceIcon.cs` (amber, both themes);
  - server: `DevicePollingService`, `DeviceStatusClassifier`, `DeviceWebGrpcService`, `ActionRunner`, `RuleEngine`,
    `WaitingDeviceRunner`, `DeviceAutoAddService`, `DiscoveryAuthenticator`, `DeviceRelocationHostedService`,
    `DeviceAddressService`, `DeviceLoginService`, `DeviceFirstPasswordService`, `BuiltInComplianceTypes`, the compliance
    engine, `TaskPluginRunnableCache`, the live view and RTSP port services;
  - plugins: Edge Recording Export, I/O Monitor, Metadata Monitor, System report, Snapshot report, Hardening scan,
    Dashboards, VAPIX Commander, Edge storage, Image Health Dashboard, Action Rule Editor, Device triggers;
  - every `FakeOadmApi` switch.
  Third-party plugins fall into their own `_` default, so the host refuses before any plugin call (task runs, page
  methods with device ids through `DeviceMessages.Refusal`, actions, compliance types).
- **Routing.** A route is a value: `DeviceRoute` = the site id or none. Everything that opens a connection to a device
  takes a route:
  - `VapixClient` moves from `HttpClientHandler` to `SocketsHttpHandler` (still `HttpClientHandler` on main; the branch
    refactor `IDeviceConnector` does this and applies cleanly except `RtspClient`, whose `ConnectAsync` got a
    `TimeProvider` since). The pinning callback and `VapixProbe`'s handler factory move with it. Its `ConnectCallback`
    asks `ISiteTransport` for a stream each time it connects (never a stream or channel kept from creation), so a
    reconnected node is used at once. TLS, pinning and login stay in the server, end to end to the device. Covers
    HTTP(S), WebSockets (`ClientWebSocket` with the same handler), the Web View relay, firmware and ACAP uploads,
    recording export, system reports.
  - **One place builds connection options.** Today `VapixClientFactory`, `DiscoveryAuthenticator`, `DeviceAdder` and
    `DeviceFirstPasswordService` each build `VapixConnectionOptions`; they take the route from one helper
    (`DeviceConnections.For(device | discovered)`), and a test checks that no options are built elsewhere (the analyzer
    cannot see a missing route).
  - `VapixClientFactory`: the route is part of the cache key; a device whose route or address changes drops its cached
    client. At most 2 connections per device, 30 s idle.
  - `VapixDeviceProbe` (discovery) is one singleton with one handler today: its `ConnectCallback` reads the route from
    the request (`HttpRequestOptions`), so one handler serves every route. `RtspClient.ConnectAsync` (live view,
    metadata, event streams) takes the route and keeps the per-device RTSP port.
  - Names resolve on the route: `IDnsNameLookup` and `CoreDiscoveryBackend`'s typed host names take the route (today they
    call `Dns` on the server before the probe); a device added by host name at a site is resolved by the node on every
    connect; "Use OADM as NTP server" resolves the device's host name through `IDeviceNetwork.ResolveAsync` (today `Dns`
    in `UseOadmNtpServerTask`).
  - **Banned APIs**: `Microsoft.CodeAnalysis.BannedApiAnalyzers` with a `BannedSymbols.txt` banning `Socket`,
    `TcpClient`, `UdpClient`, `Ping`, `Dns`, `HttpClientHandler`, `SocketsHttpHandler` and `ClientWebSocket.ConnectAsync`
    in `src/` and `plugins/`, except (each with a justification in a suppression): `ISiteTransport` and the route handler,
    `VapixClient` (the routed `ClientWebSocket.ConnectAsync`), `OadmChannel`, `MdnsBrowser`, the NTP server's
    `NtpUdpServer`, `UpstreamClient` and `UpstreamResolver`, the DHCP server's `DhcpSockets`, the Notifications actions
    (`SendHttpAction`, `SendEmailAction`, `SendTcpAction`: the user's own servers), the client's `DeviceWebRelay`,
    `ServerSettings` and `ServerTlsCertificate` (`Dns.GetHostName`). `tools/` stays outside the analyzer.
- **Site errors are not device errors.** `DeviceStatusClassifier` maps a device's own socket errors as today; a
  `SiteException` (offline, busy, outside its networks) never changes a device's status, never counts as a failed poll
  (nor towards the outage share of a poll round), never fires "Device unreachable", never fails a compliance check.
- **Overlapping networks.** Two sites may both use 192.168.0.x. Everything keyed or compared by address becomes
  (route, address): discovery deduplication, "Listed before in line N", the waiting list (`WaitingDeviceStore.Normalize`
  per route; the runner's `ListAddressesAsync` drop check per route), the address conflict check of Network settings and
  Assign IP address, the address-in-use probe, relocation. The MAC address stays unique across all routes.
- **Site offline.** Devices of an offline or not bound site show Site offline: no polls, no full refresh, new tasks,
  plugin calls, device Log in, Set password and RTSP port changes refused before any request ("Site offline: Building
  B"), live view and Web View show the same text; compliance shows Not checked; the waiting list runner skips entries
  only that site reaches. Running tasks treat a short dropout like their own wait for a restarted device; a task fails
  only when the site stays offline longer than that step's wait. Back online: a full refresh per device only after more
  than 5 minutes offline, spread like after a server start (`RefreshParallelism`, 64).
- **Discovery, everywhere by itself.** Every mode of the add page asks the server's own network and every online site: a
  zero-conf scan runs on all of them at once; a range scan's first contact runs on each node that reaches the range
  (`RangeScan`), a typed address and every import line go to the server and to each online site that reaches the
  address, in parallel; a typed host name goes to all of them, each resolving it itself. Each result carries its route.
  The same MAC address through several routes is one row, **through the site** whose networks include the address (the
  server's own route only when no site reaches it; the tooltip names the other routes); different devices at the same
  address through different routes are separate rows. `Commit` stores the route. An ADM import line keeps its expected
  MAC address, so only the route where that device answers is used. The waiting list tries its entries the same way.
- **Moving devices.** The re-find (`DeviceRelocationHostedService`) looks for Unreachable devices, and for devices of a
  site that has been offline for more than 1 hour **(default)**, on the server's network and through every online site.
  A device found by its MAC address through another route is verified there as today (serial, pinned certificate,
  stored credentials) and moved: route and address change together, the cached client is dropped. A device that answers
  on its current route never moves (no ping-pong between two routes that both reach it). Devices addressed by name never
  move, also not between routes. The DHCP server's `IDeviceAutoAdd.FollowAsync` moves a device to the server's own
  network. Logged and audited "Device moved" (user "system", "from Building B 192.168.0.20 to This server 10.0.0.20").
- **Replace and change address.** **Change address** points the site to a new node address (verified by the node's
  fingerprint). **Replace with...** moves every device of a site to another bound site after checking each device's
  serial at the same address through the new one (a dead Raspberry Pi replaced by a new one; devices that do not answer
  stay and are listed).
- **Timeouts.** VAPIX keeps 15 s per request and the status poll 3 s (`StatusPollTimeout`); the site link adds its round
  trip, so polls through a site get the measured round trip on top. The Sites page shows the round trip so a slow link
  is visible.

## Plugins and SDK

- Plugins that reach devices only through `IVapixClient`, `ICorePluginContext.EventStreams` and host services work at
  sites without changes (verified on main: Device triggers, Action Rule Editor SOAP, Edge storage, Dashboards,
  compliance types and the shared `plugins/Shared` code open no sockets of their own). **Rule for plugin authors**
  (plugins/README.md, dev docs): never open your own sockets to devices; the banned-API analyzer shows how the host does
  it.
- `IDeviceInfo.SiteId` (DIM null): the device's route; plugins may refine `CanRun` / `NotSupportedReason` with it.
- `IDeviceNetwork` (`ITaskQueryContext.Network`, `ITaskExecutionContext.Network`, `ICorePluginContext.Network`; DIM):
  `PingAsync`, `ProbeTcpAsync`, `ResolveAsync`, `LocalAddressTowardsAsync(address)`, all routed like the device. The
  Network plugin's address-in-use check (`AddressProbe`) and the NTP plugin (address towards the device, the device's
  host name) use it; Assign IP address refuses a new address outside the site's reached networks in the dialog.
- **Declaration** in `plugin.json` for the package (`PluginManifest`, like `license` and `minSdkVersion`):
  `siteSupport` (`full`, `none`; missing = not declared) and `sitePorts` (`[{protocol: udp|tcp, port, reason}]`), written
  by the plugin project SDK from the properties `OadmSiteSupport` / `OadmSitePorts`. Optional DIMs
  `ITaskPlugin.SiteSupport` and `IComplianceCheckType.SiteSupport` override the package value for one item. A task with
  `none` is greyed for devices at a site: "Not available for devices at a site" (`TaskSupportReasons`); a compliance type
  with `none` is Not checked there; a package that declares nothing (older third-party plugins) shows "Not tested at
  sites" on the Plugins page but runs. The Plugins page shows the site support and ports next to the License column; the
  node app lists the ports. New SDK members get doc comments (ApiDocs tests), samples and templates stay valid.
- **Relay** (`ICorePluginContext.Sites`, DIM null): the bound sites and their state, `OpenRelayAsync(site, Udp|Tcp,
  port)`. SDK `IDatagramEndpoint` (receive, reply) is served by local sockets and by relays alike, so a plugin handles both
  the same way. The plugin opens a relay when its feature turns on; the host closes every relay of a plugin that turns off
  or stops and reopens them after a node reconnects.
- **NTP server** (`sitePorts` UDP 123): its page gets "Serve at sites" (each online site with the `relay.udp` feature
  relays UDP 123); "Use OADM as NTP server" gives a device at a site the node's address in that network
  (`LocalAddressFor`). Requests are answered from the server's time source; the round trip is part of the NTP delay
  calculation. The rate limits are kept per (route, client address).
- **DHCP server**: `siteSupport: none`. Its page: "The DHCP server works only on this server's own network."
- **Notifications**: unchanged, server network only (see Decisions).
- **Event rules**: built-in server events (`BuiltInEvents`, stateful, server scope, no new `EventScope`): one per site
  "Site offline: &lt;site&gt;" (types follow the sites like Device triggers' dynamic types, `{site}`, `{address}`) and
  **Any site offline** (`count`, `{site}` = the names of the offline sites); after the hysteresis above. "Device
  unreachable" and **Any device unreachable** never count Site offline devices. Dashboards subscribe to them like every
  other event.
- **Compliance**: every check is Not checked "Site offline" for devices of an offline site; types that contact devices run
  through the route; a `SiteException` is "Not checked", never Failed.

## Client

- Rail page **Sites** (group Servers **(default)**, icon to be chosen; shown with `sites.view`): toolbar **Search** and
  **Add manually** (Administrator); one grid: state icon, Name, Node address, Devices (count), Round trip, Connections
  (short / permanent), Version, Last seen. Row actions (Administrator): Rename, Change address, Replace with...,
  **Remove site** (red, confirmed, Keep devices). Search results appear in the same list as "Not bound" rows with
  **Bind**; bound to another server or binding closed greyed with the reason. Errors below the list. Fake mode: two
  sample sites, one offline.
- Device grid: column **Site** (empty for the server's own network), in the search and the export. Status **Site
  offline** (amber device icon). Group by site may come later with Group by tag; `PageDeviceChoice` and the device
  picker may offer sites later like tags.
- Add page: a column **Site** in every mode (empty for the server's own network); nothing to choose. "No Axis device
  answered at X." means: not through the server and not through any site that reaches X. The DNS name check box looks up
  each device on its own route.
- Import from file: an optional column **Site** (by name), also written by the device export. "Listed before in line N"
  compares (site, address). A line without Site that reaches different devices through several sites lists all of them,
  not selected, with "Answers through Building A and Building B." Import from ADM keeps its expected MAC address per
  line.
- Waiting list window: column **Site**, in its export; "Already waiting" compares (site, address).

## Roles, audit and secrets

- `sites.view` "See sites" (Operator and Viewer yes): the Sites page. Administrator only, never grantable: Search / Add
  manually / Bind, Rename, Change address, Replace, Remove site. Adding devices at a site: `devices.addRemove`.
- Audited: "Bound site", "Renamed site", "Changed site address", "Replaced site", "Removed site", and (user "system",
  after the hysteresis) "Site offline" / "Site online"; "Device moved" and "Device added automatically" as today.
- What the node's machine sees: HTTPS and RTSP over TLS stay encrypted end to end. Plain HTTP devices and plain RTSP
  cross it readable (Digest never sends the password itself). **Basic over plain HTTP is never sent through a site**
  (the add page's `AllowBasicOverHttp` exception is off for site routes). User docs say so.
- **Backup**: the `Sites` table and `Sites.Identity` are in `oadm.db`; no format change. `manifest.json` counts sites and
  the restore report says "Sites: 3 restored". The one-session-per-identity rule keeps a restored copy from driving the
  live nodes.

## Packaging

- **Windows**: `OADM-SiteNode-<version>-win-x64.msi` (WiX 6 like the main MSI, `packaging/windows/`): `Program Files\OADM
  Site Node`, service "OADM Site Node" (`OadmSiteNode`, automatic, LocalService, restart on failure), firewall rule TCP
  5081 (Domain, Private), Start menu shortcut to the app.
- **Linux**: `oadm-site-node_<version>_<amd64|arm64>.deb` (service, own system user `oadm-site-node`,
  `/opt/oadm/site-node`, `/var/lib/oadm-site-node` 0700) and `oadm-site-node-app_<version>_<arch>.deb` (the app, Depends
  `oadm-site-node`, desktop file). Raspberry Pi 4 and 5 are a target. Pre-release versions `1.2.0~rc.1`.
- Each package ships `LICENSE.txt`, `NOTICE.txt`, `LGPL-2.1.txt` (only if FFmpeg or other LGPL parts are in it),
  `TERMS.md` and its own `THIRD-PARTY-NOTICES.txt` (`tools/Oadm.Notices` reads the node's deps.json too; Linux in
  `/usr/share/doc/oadm-site-node*/`).
- `manage package site-node [--rid] [--version]` (both manage scripts and `scripts/manage-help.txt`).
- Release workflow (`release.yml`): job `installers` builds the node packages next to the others (the arm64 .deb on
  `ubuntu-24.04-arm`), installs, checks (service up, `Hello` answers unbound) and removes the .deb; `smoke-windows`
  (`smoke.ps1`) does the same for the node MSI; `release` uploads them and lists them in `SHA256SUMS.txt`. No portable
  ZIP. Pre-release tags behave as today.
- Website: the download area gets **Site node** (Windows, Linux menu x64 / ARM64 like the main downloads). Docs: a page
  "Sites" (install, bind, networks, reset, the outgoing port range).

## Tests

- Node: state machine with a fake clock (unbound open, closed after 24 h of run time, Allow binding, bound, reset with a
  new identity), reached cards and extra subnets (allowed and refused addresses), `Dial` (bytes, half-close, cancel,
  deadline, device socket errors vs site errors, short and permanent limits), `Relay` (open, close with the stream, port
  in use), mDNS announce, `RangeScan`, capabilities.
- Server with an in-process node on loopback: bind / remove / keep devices / not bound, identity rotation and "new
  identity" acceptance, one session per identity, copy detection, routing of VAPIX, RTSP (with a per-device RTSP port)
  and WebSockets with TLS pinning end to end, the client cache dropping a moved device, overlapping addresses on two
  sites (discovery, imports, the waiting list, the address conflict check), Site offline (computed status not stored,
  every status switch, refused new tasks and plugin calls, Log in / Set password refused, a running task across a short
  dropout, the site events, no "Device unreachable", hysteresis, compliance Not checked), moving devices (sticky route,
  1 hour rule, names never move, DHCP follow), replace and change address, DNS lookups per route, a backup restored on a
  new server keeps its sites.
- The banned-API analyzer in the build (Send TCP message, NTP upstream still allowed); a test that every
  `VapixConnectionOptions` comes from the one helper.
- Perf (`Category=Perf`): 30,000 devices behind 10 sites polled through the nodes while a firmware upload and live views
  run through the same node (polls stay within their interval), 10,000 Device trigger WebSockets through 3 nodes, the
  waiting list with 30,000 entries tried through sites, a site going offline and online with 3,000 devices (one batch,
  no writes).
- Tests follow "tests that never flake": fake clocks for the binding window, the hysteresis and the 1 hour rule.

## Build order

1. Foundations: `VapixClient` on `SocketsHttpHandler` (rebase the branch refactor, fix `RtspClient`), `DeviceRoute`
   through the one options helper, the factory, the probe and RTSP, the banned-API analyzer; `SiteOffline` in the SDK
   enum and every status switch; node service and app (identity, announce, Hello / Bind / Unbind, Dial, networks, terms,
   theme assembly); server identity key, `ISiteTransport` (control and worker connections, short and permanent
   limits), Sites page, `sites.view`, `Device.SiteId`, Add manually through the sites, polling, tasks, Site offline.
2. Discovery at sites (every add mode, mDNS, `RangeScan`, Resolve, DNS names), imports and Import from ADM with Site,
   the waiting list per (site, address), moving devices, Replace, Change address.
3. RTSP route in use (live view, PTZ, metadata, events), Device triggers, Web View, recording export, compliance.
4. SDK: `IDeviceNetwork`, `siteSupport` / `sitePorts`, relays with `IDatagramEndpoint`, NTP at sites, DHCP message,
   the site events.
5. Installers, release workflow, notices, website and user docs.
