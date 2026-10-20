# Sites

Status: built on the branch `feature/sites` (2026-10-19 / 20), not on main until the maintainer accepts it; the manual
test plan on hardware (node README) is still to do. First agreed 2026-10-15 as "Agents" (branch `feature/agents`,
`docs/agents.md`); re-evaluated against main (v3.6.0-rc.1) and renamed on 2026-10-19; after a security review the same
day the node connects out to the server, binding needs a pairing code and the node opens only allowed ports; an
architecture, SDK and security review added the rest of that day's decisions. While building: TLS 1.2 is allowed next to
1.3 (Windows Server 2019), and a review of the built code replaced the first SRP-6a pairing with J-PAKE, added the limits
on strangers and moved the Windows node service to its own account (see Binding, Server listener, Security review). On
2026-10-20 the inbound mode ("Wait for the server") was removed: the node always connects to the server, the node app
became simpler and the node reaches all network cards by default. Defaults marked **(default)** were not asked and may
change.

## Purpose

One OADM server manages devices in networks it cannot reach directly: a closed camera network behind a recording
server's second network card, another building, a remote site. Instead of a second OADM server (ADM's multi-server
model) a small **OADM Site Node** runs in that network and is OADM's foot in it.

The site node is a pipe. It keeps no database, no credentials, no tasks and no plugins. Everything stays on the one
server: devices, credentials, tasks, event rules, compliance, plugins, the audit log.

```
Client ──► OADM server ◄──TLS 5081── OADM Site Node, site "Building B" ──► cameras 192.168.0.x
           (everything)   (the node     (pipe, no data,                     (allowed ports only)
                           connects)     no inbound port)
```

## Decisions

| Topic | Decision |
|---|---|
| Name (2026-10-19) | **Site** in the UI: rail page **Sites**, device column **Site**, status and event **Site offline**. The program is **OADM Site Node**. One site = one remote network with exactly one node in v1. "Agent" was dropped (reads as AI), "network" clashes with Network settings. |
| Model (2026-10-19) | Kept: one server, nodes are pipes. The parked multi-server "nodes" draft (full OADM servers as relays, 2026-10-13) stays dropped. |
| Direction (2026-10-19, security review; 2026-10-20) | **The node always connects to the server; the node never listens** (no inbound port at the site, nothing to scan there). The server listens for site nodes on its own port, TCP 5081 **(default)**, apart from the clients' 5080, **only after an administrator turns on "Accept site nodes"** on the Sites page (off by default: a server without sites opens no extra port). The inbound mode of 2026-10-19 (the node waiting on 5081, the server dialing it, mDNS announce, Search and Add manually on the Sites page) was removed on 2026-10-20: one direction, one code path. |
| Finding | The node app takes the server's address; once the node connects, it shows on the Sites page as **Waiting to be bound**. |
| Binding (2026-10-19, security review) | **Pairing code**: the node app shows an 8-character code while binding is open; the administrator types it in OADM at **Bind**. The code proves both sides to each other through J-PAKE, a balanced PAKE (see Binding), so neither another server nor anything in between can bind, and nobody (not even an impostor node or server taking part) can test codes offline. Binding is open for 24 hours of run time **(default)** after install or Reset, then closed until **Allow binding** in the node app. A bound node refuses every other server. |
| Removing | From OADM (rare), with "Keep devices" or not. Locally with **Reset** in the node app when the server is gone. |
| Reach (2026-10-20) | The node reaches the devices on **all network cards (default)** or on the **cards chosen in the node app** (one choice: All cards, or ticked cards; ticking a card unticks All cards, ticking All cards clears the choice; no chosen card counts as all cards). Restrict to chosen cards when the machine also sits in an office network. Optional extra routed subnets (camera VLANs) in the app. The server cannot widen the reach. |
| Ports (2026-10-19, security review) | The node uses **only allowed ports**, in two lists: **Connect to** (devices; TCP 80, 443 and 554 by default) and **Listen on** (relays; empty by default), so a port allowed for a camera never opens a listener at the site. Further ports (a plugin's ports, a camera on 8443, a per-device RTSP port, the NTP relay's UDP 123) are **requested by the server and allowed in the node app** ("Building B asks to connect to TCP 8443: camera 192.168.0.30. Allow"); nothing is allowed by itself. A dial to another port is a site error "Port 8443 is not allowed at Building B. Allow it in the OADM Site Node app." |
| Local UI | A small desktop app (Avalonia, OADM look) next to the headless service. Most installers do not use command lines. |
| Installers (2026-10-19) | **Windows MSI and Linux .deb only** (amd64 and arm64). No portable ZIP, no macOS node in v1. |
| Scope v1 | All device traffic, see "Scope". |
| Site offline (2026-10-19) | A **new device status `SiteOffline`, computed, never stored**: the stored status stays the last real one, so a node that comes back restores every device without writes. No polls, new tasks and plugin calls refused before any request. |
| Event (2026-10-19) | **Server scope, no new event scope**: one stateful server event per site "Site offline: &lt;site&gt;" and **Any site offline** (`count`, `{site}` names), like the "Any device" twins. Devices of an offline site fire no "Device unreachable". |
| Compliance (2026-10-19) | Devices of an offline site are **Not checked: Site offline** in every check (Reachable and Login works included); no compliance event turns on because of it. |
| Limits (2026-10-19) | **Permanent streams and short connections are counted apart** (see Transport). |
| Plugins (2026-10-19) | Declared per package in `plugin.json` (`siteSupport`, `sitePorts`, written by the plugin project SDK); a DIM on `ITaskPlugin` / `IComplianceCheckType` only where one item differs. `IPlugin` stays unchanged. The plugin project SDK always writes `siteSupport` (default `full`); a manifest without it counts as `none` (no third-party plugins exist yet, so there is no "not tested" state). |
| Notifications (2026-10-19) | Send HTTP request, Send email and Send TCP message stay on the server's own network in v1 (a "Send through site" option can come later). |
| Roles (2026-10-19) | New permission **`sites.view`** "See sites" (group Devices, Operator and Viewer yes). Bind, rename, replace and remove: **Administrator only**, never grantable. Adding devices at a site needs `devices.addRemove` ("Add and remove devices"), as every add mode, the waiting list and Import from ADM today. |
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
  small library without EF Core first, else self-contained single-file. Runs **without root**: outbound connections to the
  server only (it never listens for the server; mDNS browse of `_axis-video._tcp` only), ping through the OS without raw sockets (Windows `IcmpSendEcho`, Linux `ping_group_range`, else a TCP
  check of 80 / 443). A relay of a port below 1024 (NTP 123) needs the right to bind it (Linux `CAP_NET_BIND_SERVICE` on
  the unit; the Windows service account may bind it).
- **App** "OADM Site Node" (Avalonia). The theme (`Oadm.Client/Themes`: `Colors.Dark.axaml`, `Colors.Light.axaml`,
  `OadmTheme.axaml`, `Icons.axaml`, `OadmColors`) moves to a shared assembly the client and the app both use; the app
  takes the OS's dark or light setting (it has no client settings). It may use `Oadm.Sdk.Client` controls (`StatusChip`,
  `MessageWindow`, `PasswordBox`, `FormField`). First start: the terms of use like the client (`TermsWindow`, the
  accepted SHA-256 in the app's own settings). One window laid out like a service control window (redesigned again 2026-10-20 after the
  AXIS Device Manager Service Control, and simplified the same day): about 440 x 780, fitted to small screens
  (`WindowFit`), the custom title bar of the client (`Border.titleBar`,
  `Oadm.Theme.Shell.TitleBarLayout.ContentMarginWithoutMaximize`: the app name left, minimize and close right, no
  maximize), **the tabs directly on the window background** (no card: the window is narrow; `ui:PageTab` without a close
  command, `Count` badge), label / field rows (`ui:FormField`); below the tabs **Node status** (simplified 2026-10-20:
  every state line lives here, each name once): the status chip "Connected to SERVER" ("Not connected: Cannot reach
  10.0.0.5:5081", "Not connected" while binding is closed, "No server address yet", "Service not running"; then the
  window says "Start the OADM Site Node service." and shows no tabs), "Site SITE" once bound, the binding line ("Bound
  since 2026-10-20 07:44", "Not bound. Binding open for 23 more hours.", "Binding is closed."), small and grey "Node ID
  1457a0b5… · Version 1.0.0" (the whole ID as tooltip) and, while ports wait, "The server asks for N ports." with
  **Review** (opens Ports); the footer **Save** (the one accent
  button: enabled while the address has unsaved changes) and **Close**, both on the right. No start / stop buttons. Tabs:
  - **General**, top to bottom: **Server address** (`oadm.example.com` or `10.0.0.5`; hint "Port 5081 unless you type
    another.", while no address is saved "Enter the address of the OADM server." first, so a new node shows what to do;
    the connection problem below the field); then, once an address is saved and while not bound (or a new server
    identity was accepted), **Pairing code**: the code in large letters with "Type this code at Bind on the Sites page of
    OADM.", the wrong codes, and the button the state needs: **Show pairing code** or **Allow binding** (another 24
    hours, a new code). **Save** of the address runs as an administrator already, so its answer carries the current code
    while not bound and the app shows it right away (the code never enters the state everyone reads); Show pairing code
    is for an app opened later. Then a new identity of the server ("SERVER presents a new identity (fingerprint).", see
    Binding) with **Accept new server identity**, and **Reset** at the bottom (red, confirmed: "The node forgets SERVER
    and gets a new identity. Bind it again in OADM."; it tells the server first, Binding 7). Binding, site, node ID and
    version are in Node status. No mode switch: the node always connects to the server;
  - **Networks**: **Reach devices on**: one choice, **All cards** (default) or chosen cards (a check list of the cards
    with their addresses, "Default router" on the office side; ticking a card unticks All cards, ticking All cards clears
    the choice; unticking the last card is All cards again), and **Extra networks** (routed camera VLANs: add, remove);
  - **Ports** (the tab shows the number of requests): the ports the server asks for first, each with the list and why
    ("Connect to TCP 8443: camera 192.168.0.30", "Listen on UDP 123: NTP server") and **Allow** / **Dismiss**, then two
    lists, **Connect to** (TCP 80, 443, 554 by default) and **Listen on** (empty by default), each with remove and add;
  - **Activity**: the connections now (short and permanent, see Limits), the relays open now ("NTP, UDP 123, for
    SERVER") and their problems ("UDP 123 is used by another program"), and the **Connection log**: the last
    connections through the node (time, server, target address and port, short or permanent, result), newest 10,000
    **(default)**, also in a daily file kept 30 days and at most 1 GB together (`logs/`; the same line within a minute
    once with "(N more)"); never contents.

  It talks to the service over a local channel (Windows named pipe, Linux Unix socket): everyone may read the state
  except the pairing code; showing the code and every change need administrator rights (UAC on Windows, `pkexec` on
  Linux; the code is **Show pairing code**, so a local user without those rights cannot bind a server). Headless machines use `oadm-site-node
  status`, `pairing-code` (administrators), `server <address>`, `allow-binding`, `networks`, `ports`, `accept-server`,
  `reset`.
- **Data** (`%ProgramData%\OADM Site Node`, `/var/lib/oadm-site-node`; service account only): `node.json` {id, server
  address (with its port), all cards or the chosen cards, extra subnets, allowed ports (connect, listen), requested ports,
  binding run time left, bound servers (a list with at most one entry in v1: name, identity fingerprint, bound time)}, the node's TLS key (Windows a CNG key like the server's, else encrypted with a
  local key file). No device data, no passwords, ever.
- **Identity**: a random id and a new key at first start and at every Reset (a copied VM keeps the original's identity
  until Reset; see Binding). The node's certificate is self-signed and valid for 100 years; both sides check
  fingerprints only and ignore certificate dates (a Raspberry Pi has no clock and a closed network often no NTP).

### States

| State | The node |
|---|---|
| Unbound, binding open | Connects to the server; answers `Hello`, and `Bind` with the right code; the app shows the code |
| Unbound, binding closed | Does not connect; the app says "Binding is closed. Click Allow binding." |
| Bound | Connects to the bound server only and checks its identity |

The binding window counts the service's run time (saved every minute), not the wall clock.

## Binding

1. In the node app the installer types the server's address. The node connects; its check of the server's key waits for
   the pairing (the key is not known yet). The Sites page lists it as **Waiting to be bound** (node name = its host name,
   its address as the server sees it, version).
2. **Bind** (Administrator): dialog with the site name (default the node's host name) and **Pairing code** (the 8
   characters the node app shows; Crockford base32, no look-alikes, case and dashes ignored, 40 bits). Both sides have
   seen each other's key fingerprint in the TLS handshake. **J-PAKE** (a balanced password-authenticated key exchange,
   NIST 3072-bit prime order group, SHA-256, BouncyCastle `JPakeParticipant`; decision 2026-10-19: a plain HMAC of the
   code sent first would let someone in between test all 2^40 codes offline; SRP-6a, chosen first, is augmented: the node
   holds the verifier, so an impostor node waiting to be bound, or anything in between replacing salt and B, received the
   server's A and proof after the administrator typed the code and could test every code offline. In J-PAKE neither side
   holds anything a guess can be checked against; each attempt is one online guess). `PairStart` carries the server's
   round 1 and returns the node's round 1 and round 2 (the node makes a fresh participant for every PairStart); `Bind`
   carries the server's round 2 and `HMAC-SHA256(K, "bind" | node fingerprint | server identity fingerprint)` with the
   session key K = SHA-256("oadm-site-pairing" | keying material); the node checks it and answers with the same over
   "bound" | both fingerprints, which the server checks. A failed proof is a wrong code. Anything in between with its own
   key fails both proofs even with the right code. Then the node stores the server identity's fingerprint and the server
   the node's. Audited "Bound site". (`Oadm.Sites.Binding`: `PairingCode`, `Pairing`, `NodePairing`, `ServerPairing`.)
   Binds run one at a time on the server (name and count checks cannot both pass for two binds); a site that cannot be
   stored after the node bound is undone with `Unbind`. Waiting nodes are kept by their key fingerprint, never by the id
   a node reports (a stranger reusing a real node's id gets its own row), and Bind names the node by its fingerprint
   (`BindSiteRequest.node_fingerprint`; the dialog shows its short form).
3. **Wrong codes** (a failed proof at `Bind`): after 5 wrong codes the node makes a new code and refuses `Bind` for 10 minutes **(default)** ("Too
   many wrong pairing codes. Wait 10 minutes and use the new code."); the app shows the attempts and from where. The
   server shows "The pairing code is wrong." below the field. Proofs are checked one at a time with the lock checked in
   the same step, so parallel links never get more than 5 guesses. `PairStart` is limited to 10 a minute per address
   (IPv6 per /64) and 30 a minute in all **(default)**; a `Bind` needs a `PairStart` of its own link.
4. From then on every connection is mutual TLS: the server presents its site identity, the node its own key; each side
   checks the pinned fingerprint.
5. The server refuses to bind an id or a fingerprint it already knows: "This site node is a copy of Building B. Click
   Reset on the copy." (cloned VMs).
6. **Remove site** (Administrator, red, confirmed): "Remove site Building B?" with **Keep devices** (they stay as Site
   offline, to be moved by Replace or the automatic move) or not (removed with their credentials and tags, one
   transaction like `RemoveManyAsync`). The server sends `Unbind`; a node that does not answer is forgotten anyway ("The
   site node did not answer. Reset it in the OADM Site Node app."). Audited "Removed site". With Keep devices the site
   leaves a tombstone (table `RemovedSites` {Id, Name, RemovedUtc}, migration `RemovedSitesAndHeldWaiting`; its own table,
   so a new site may take the name and the reset node may bind again): its devices show Site offline with "Building B
   (removed)", their status events go off (the site never comes back), the re-find looks for them at once, and the Sites
   page lists it as **Removed** with **Replace with...** and **Remove devices** until its last device left (then the
   tombstone is dropped). The waiting entries of a removed site are removed; Replace moves them to the new site.
7. **Reset** in the node app (or `oadm-site-node reset`): new id, key and pairing code, the server forgotten, binding
   open for 24 hours. **The node tells the server first** (decision 2026-10-20): while the bound server watches it, its
   `Watch` sends a last `NodeStatus` with `reset = 13`; the server marks the site **Not bound** (`SiteTexts.NotBound`,
   stored), audits "Site node was reset" (user system) and ends the session; the node waits for that at most 3 s
   (`NodeOptions.ResetWaitLimit`, `ResetNotice`), then resets anyway. Without a session (server not reachable) it resets
   at once and the old site stays Offline. The node connects again with its new identity and waits to be bound; the old
   site's devices show Site offline until it is replaced (bind the node again, then Replace with) or removed.

**Server listener.** Off by default. **Accept site nodes** on the Sites page (Administrator, audited "Turned on site
nodes" / "Turned off site nodes", setting `Sites.Accept`) opens it without a restart; turning it off closes it and every
node connection (every site goes offline: the server never dials a node). The server accepts site nodes on
`Sites.ListenUrl` (default `https://0.0.0.0:5081`, its own Kestrel endpoint with the site identity certificate, never
the clients' endpoint, TLS 1.3 or 1.2, see Transport). A node that is not bound may only `Hello`, `PairStart` and
wait for `Bind`: at most 50 waiting nodes and 10 new connections a minute per source address **(default)**; a waiting node
not bound within 24 hours is dropped. Before any TLS work the listener counts new connections per source address (IPv6
per /64): 120 a minute **(default)**, wider than the 10 for unknown keys because bound nodes reconnect with a control and
up to 8 worker links; at most 64 handshakes at once, at most 64 links whose Hello has not answered (5 s for it). Until a
link carries a bound site's key the server reads at most 16 KB per answer (`NodeLink.Trust`). A Hello is checked before
anything of it is stored, logged, audited or shown (`HelloRules`: node id 8..64 hex digits or a GUID, host name at most 253
characters, names at most 64, version at most 32 printable ASCII, no control or format characters); one that breaks a rule
is refused whole. The per-address tables drop entries by age and are bounded (`AddressRateLimiter`). Bound nodes
reconnect with backoff (1, 2, 4 ... 60 s). **Server addresses**: once bound, the server sends the
node its DNS name and every address of its network cards (`Watch` of the control connection, again after every change);
the node keeps them and tries the typed address first, then the others in order. A server that moved is found again as
long as one of them still answers; otherwise change the address in each node app (`oadm-site-node server <address>`);
the identity stays, so no new bind. The user docs recommend typing a DNS name.

**Server identity.** The server binds with its own **site identity key** (ECDSA P-256, setting `Sites.Identity`,
encrypted with the master key, purpose `oadm:site-identity`), not with its TLS certificate. So
`--Oadm:RegenerateTlsCertificate`, a new TLS certificate after a master key replacement and a server that moved to another
address keep their sites. It is in the backup (`oadm.db` + `master.key`), so a backup restored on a new server takes its
sites along, like `Server.Id` (plugin licenses). `RotateIdentity(new fingerprint)`, signed with the old key, replaces it.
If the key is lost (master key replaced: add it to the `Security.KeyReplaced` notice and the error log's list), the node
app shows "SERVER at ADDRESS presents a new identity (fingerprint). Accept" (administrator), naming where it came from.
**Accept does not bind it**: it lets that identity, and only it, pair like a server at an open node, and the app shows the
pairing code; the new server binds with Bind and the code (the Sites page lists the node "accepted this server's new
identity", Bind binds the existing site again: `HelloReply.new_identity_accepted`). Anyone at one of the server's
addresses can raise the request, so it never binds without the code. A request waiting in the app is not replaced by
another identity for an hour, and never once accepted (`BindingState.NewIdentityHold`). A node serves **one server session at a
time**: a second connection with the same identity is refused ("Another OADM server with this identity is connected from
10.0.0.5"), so a restored test copy of the server cannot drive the production nodes alongside the real one. The node
opens every link itself, to the one address it chose, so its links are always one session; the server refuses a second
control link of the same identity while the first one answers.

**Security review (2026-10-19).** The node holds no passwords, but whoever binds it gets a pipe into the networks it
reaches (factory-default cameras, default passwords, old firmware), and whoever controls the OADM server controls every
site. So: the server opens its site port only after Accept site nodes; the node connects out and never listens (no
inbound port at the site); TLS 1.3 (1.2 where the OS has no 1.3) with pinned keys on both sides; binding needs the pairing code from the node
app (an administrator of the node's machine), open 24 hours of run time only, proved with J-PAKE so no side can test codes
offline; strangers learn nothing about a bound node (its Hello to them carries the state, version and protocol version
only: no id, host name, mode, features or owner); what strangers send is limited before TLS, before Hello and before
binding (see Server listener and Binding); the node opens only the networks and
ports allowed in its app (connect and listen apart), so a compromised server cannot use it as a general pivot; the node keeps a connection log of
its own; every bind is audited; a bound node never accepts another server. Remaining: plain HTTP and RTSP cross the node
readable (see Roles, audit and secrets); updates are manual (the Sites page shows "Update available").

## Transport and protocol

gRPC over HTTP/2 + TLS, `site_node.proto` (`SiteNodeService`, hosted by the node, called by the server):

- **Who opens the TCP connection.** Always the node: it opens TCP + TLS to the server's site port; after TLS the roles
  turn: the server is the HTTP/2 client on that socket (`SocketsHttpHandler.ConnectCallback` hands it the accepted
  connection), the node the HTTP/2 server (Kestrel on a connection it opened, through its own
  `IConnectionListenerFactory`). **Spike passed (2026-10-19)**: Kestrel serves HTTP/2 on a connection the node opened itself
  (`Oadm.Sites.Transport.AcceptedStreamListener`: an `IConnectionListenerFactory` for `AcceptedStreamEndPoint`, the stream
  pumped through two pipes like Kestrel's socket transport, so the end of the stream is the end of Kestrel's input; Unbind
  ends the accept loop so Kestrel stops), and the server's `SocketsHttpHandler.ConnectCallback` returns the accepted TLS
  stream (`tests/Oadm.Sites.Tests/ReversedConnectionTests`: Hello, a 4 MB `Dial` echo with half-close, 200 dials over the
  one connection, a dropped link failing at once, an unpinned key refused). The fallback (the node as gRPC client) is not
  needed.

- **Two connections per node**: **control** (`Hello`, `Watch`, `Ping`, `ProbeTcp`, `Resolve`, `ReverseLookup`, `Browse`,
  `LocalAddressFor`; the heartbeat) and **worker** (every `Dial` and `Relay`: polls, tasks, uploads, live view,
  WebSockets). A firmware upload or a live view never delays the heartbeat, so it never causes a false Site offline. The
  node's Kestrel allows 4,096 streams per connection **(default)**; the server sets `EnableMultipleHttp2Connections`, so
  the worker side opens further connections by itself when one is full (10,000 trigger WebSockets need at least three).
  The node keeps the control connection and opens worker connections when the server asks (`OpenConnections(n)`
  on the control connection, at most 8 **(default)**); the server hands them to the handler's pool.
  A separate connection for large transfers can be added later inside `ISiteTransport` if the perf test shows polls
  waiting behind uploads on slow links.
- **Online check** on the control connection only: HTTP/2 keepalive pings plus `Watch`. Offline after 30 s without an
  answer, online again after 60 s stable **(defaults)** (no flapping).
- **`ISiteTransport.OpenStreamAsync(site, host, port, kind, deadline) -> Stream`** is the only way the server opens a
  connection through a node (`kind` = Short or Permanent, see Limits).
- **Capabilities**: `Hello` returns `protocol_version` and `features` (`dial`, `browse`, `resolve`, `relay.udp`,
  `relay.tcp`, `local-address`, ...). The server refuses a node below its minimum version ("Site node too old. Update
  it."); a missing feature turns off only that feature ("Update Building B to serve NTP").
- Calls:
  - `Hello` -> id, version, protocol version, features, state; the bound server's name only to that server. A bound node
    answers anyone else with state, version and protocol version only.
  - `PairStart(server round 1)` -> the node's rounds 1 and 2; `Bind(site name, server round 2, proof)` -> the node's proof (see Binding); `Unbind`, `Rename`,
    `RotateIdentity(new certificate, signed by the old key)`; `SetServerAddresses` (see Server listener);
    `OpenConnections(n)` (worker connections).
  - `RequestPorts([{direction connect | listen, protocol, port, reason}])`: the server asks for ports (a plugin's `sitePorts` while it is on, a device
    or address with another port); the node lists them in its app until allowed or dismissed. `Watch` carries both allowed
    lists, so the server knows them before it dials or opens a relay.
  - `Watch` (server stream): state, network cards, reached cards and subnets, relays and their problems, connection
    counts, now and after every change.
  - `Dial` (bidirectional stream): first message host or address, port, kind, deadline; then bytes both ways; an end flag
    per direction is a TCP half-close; cancelling the call closes the socket; the node stops connecting when the deadline
    passes. The device's own socket error (refused, timed out, unreachable, reset) comes back as that error; node
    problems ("outside its networks", "port not allowed", "busy", "offline") are site errors (see Server). Every dial is a
    line in the node's connection log.
  - `Resolve(name)` and `ReverseLookup(address)`: DNS on the node's side (typed host names, devices added by name, "Use
    DNS names" with the same rules as `SystemDnsNameLookup`: a full name, not ".local" or ".arpa", resolving back to the
    same IP).
  - `Ping(address)`, `ProbeTcp(address, port)`: the address-in-use check of Network settings and Assign IP address (the
    add page never pings).
  - `LocalAddressFor(address)`: the node's own address towards a device ("Use OADM as NTP server").
  - `Browse(duration)` (server stream): mDNS `_axis-video._tcp` results on the reached cards.
  - `RangeScan(from, to, parallelism, timeout)` (server stream): the first contact of a range scan on the node (thousands
    of short connects stay local); the server then probes and logs in through `Dial`.
  - `Relay(protocol, port)` (bidirectional stream; the port must be on the Listen on list): the node listens on that port on the
    reached cards and passes every
    datagram or connection to the server; answers go back to the sender. The relay lives as long as the stream. Problems
    ("UDP 123 is used by another program", "No permission to use UDP 123") come back like `NtpStatusTexts`.
- **Limits per node** **(defaults)**:
  - **Short** dials (polls, tasks, page reads, scans, probes): 1,024 open, 256 connecting at once; over it "Site busy",
    retried, never a device error.
  - **Permanent** dials (Device trigger WebSockets, RTSP live view, metadata and event streams, Web View WebSockets): no
    own cap below the device limit (30,000 per server; Device triggers watch at most 10,000), at most 64 connecting at
    once. The node app and the Sites page show both counts. The outgoing port range of the node's OS bounds them (Windows
    16,384 by default): the user docs say so, like Device triggers today.
  - **Probes** (ProbeTcp, the ping fallback, every address of a range scan): 64 at once, the rest wait; at most 4 range
    scans at once. **Relays**: a UDP sender is answered for 2 minutes after its last datagram, a TCP relay holds 256
    connections; only senders inside the reached networks are relayed. Never reached: the node's own addresses,
    loopback, link-local, multicast; extra networks at least /8.
- **Large transfers** **(default)**: at most 2 per site at once (streamed request bodies: firmware and ACAP uploads,
  configuration imports; streamed answers: system reports, recording export), so a slow site link keeps room for polls,
  live view and the heartbeat. The route handler holds a per-site gate for requests that stream; a task step waiting for
  it says "Waiting for the site link". The Sites page shows the transfers running.
- **IPv4** and global / unique-local **IPv6**; link-local addresses with a zone id are refused in v1.
- **TLS 1.3, or 1.2** on the link between server and node (decision 2026-10-19: Windows Server 2019 and
  Windows 10 1809 / LTSC 2019 have no TLS 1.3, and many recording servers run them). 1.3 whenever both sides have it;
  forward-secret AEAD suites only where the OS lets an application choose (Linux). Both sides present a self-signed
  ECDSA P-256 certificate (100 years) and check the other's fingerprint only (`Oadm.Sites.Transport.SiteTls`). Every
  handshake has a timeout (`SiteTls.HandshakeTimeout`, 5 s): `SslStream` has none, and a peer without a common protocol
  was seen to leave the accepting side waiting forever.

## Server

- **Data model** (new migration `Sites`; migrations are never deleted): table `Sites` {Id, Name (1..64, unique
  case-insensitive), Mode and Port (kept for the migrations, not used since the inbound mode went: Mode is always
  Outbound), Address (where the node last connected from),
  NodeKeyFingerprint, NodeId, BoundUtc, LastSeenUtc (written at most once a minute),
  Version, NotBound (bool)}; online / offline lives in memory only. `Device.SiteId` (nullable = the server's own
  network), `WaitingDevice.SiteId` (nullable = try everywhere; set by the import's Site column). At most 100 sites
  **(default)**.
- **Proto**: `sites.proto` `SiteService` for the client (`ListSites`, `WatchSites`, `SetAccept`, `BindSite`,
  `RenameSite`, `ReplaceSite`, `RemoveSite`; `BindSite` carries the pairing code; `Site.mode` and `node_port` are
  reserved), every method in `AccessPolicy` (`sites.view` or Admin;
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
  `SiteException` (offline, busy, outside its networks, port not allowed) never changes a device's status, never counts as a failed poll
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
  move, also not between routes. **Only a device with a pinned HTTPS certificate moves to another route**, verified
  against that pin there: over HTTP a compromised node could answer the authenticated basicdeviceinfo itself and capture
  the device's traffic, so a device stored with HTTP moves only within its own route (logged once). The DHCP server's
  `IDeviceAutoAdd.FollowAsync` moves a device to the server's own network under the same rule. Logged and audited "Device moved" (user "system", "from Building B 192.168.0.20 to This server 10.0.0.20").
- **Ports.** Before a dial the server checks the site's allowed ports (from `Watch`): a device whose port is not allowed
  is not contacted and keeps its last status; the Sites page row says "1 port waiting for approval in the node app"; the
  add page row says "Port 8443 is not allowed at Building B. Allow it in the OADM Site Node app." The server sends
  `RequestPorts` once per new port.
- **Replace.** Nodes report their address themselves (where they connect from). **Replace with...** moves every device
  of a site to another bound site after checking each device's
  serial at the same address through the new one (a dead Raspberry Pi replaced by a new one; devices that do not answer
  stay and are listed).
- **Timeouts.** VAPIX keeps 15 s per request and the status poll 3 s (`StatusPollTimeout`). A new connection through a
  site costs about 4 round trips (open the stream, connect, TLS, request), so every timeout through a site gets 4 x the
  measured round trip on top (3 s poll over a 600 ms satellite link = 5.4 s); kept connections need only one. The Sites page shows the round trip so a slow link
  is visible.

## Plugins and SDK

- Plugins that reach devices only through `IVapixClient`, `ICorePluginContext.EventStreams` and host services work at
  sites without changes (verified on main: Device triggers, Action Rule Editor SOAP, Edge storage, Dashboards,
  compliance types and the shared `plugins/Shared` code open no sockets of their own). **Rule for plugin authors**
  (plugins/README.md, dev docs): never open your own sockets to devices; the banned-API analyzer shows how the host does
  it. The plugin project SDK (`Oadm.Plugin.Sdk`) ships the same analyzer and `BannedSymbols.txt` as **warnings** for
  every plugin, so third-party developers see it too.
- **Testing** (`Oadm.Sdk.Testing`): `FakeSite` (online / offline, allowed ports), `FakeDeviceNetwork` (ping, probe,
  resolve, local address) and an in-memory relay for UDP and TCP, so a plugin tests its site behavior without a node.
- `IDeviceInfo.SiteId` and `SiteName` (DIM null): the device's route; plugins may refine `CanRun` / `NotSupportedReason`
  with it. `ActionPlaceholders.Fixed` gets `{site}` (the device's site name, empty on the server's own network).
- `IDeviceNetwork` (`ITaskQueryContext.Network`, `ITaskExecutionContext.Network`, `ICorePluginContext.Network`; DIM):
  `PingAsync`, `ProbeTcpAsync`, `ResolveAsync`, `LocalAddressTowardsAsync(address)`, all routed like the device. The
  Network plugin's address-in-use check (`AddressProbe`) and the NTP plugin (address towards the device, the device's
  host name) use it; Assign IP address refuses a new address outside the site's reached networks in the dialog.
- **Declaration** in `plugin.json` for the package (`PluginManifest`, like `license` and `minSdkVersion`):
  `siteSupport` (`full`, `none`; missing = not declared) and `sitePorts` (`[{protocol: udp|tcp, port, reason}]`), written
  by the plugin project SDK from the properties `OadmSiteSupport` / `OadmSitePorts`. Optional DIMs
  `ITaskPlugin.SiteSupport` and `IComplianceCheckType.SiteSupport` override the package value for one item. A task with
  `none` is greyed for devices at a site: "Not available for devices at a site" (`TaskSupportReasons`); a compliance type
  with `none` is Not checked there; a page or action of such a package refuses devices at a site with the same text. A
  manifest without `siteSupport` counts as `none`. The Plugins page shows the site support and ports next to the License column; the
  node app lists the ports. New SDK members get doc comments (ApiDocs tests), samples and templates stay valid.
- **Relay** (`ICorePluginContext.Sites`, DIM null): the bound sites (`ISiteInfo` {Id, Name, IsOnline}) and
  `SitesChanged`; a plugin that offers a server to
  devices (they connect to it) opens one relay per site, UDP and TCP from the start (decision 2026-10-19: the stack is
  built once):
  - **UDP**: `OpenUdpRelayAsync(site, port, reason)` -> `IUdpRelay` : `IDatagramEndpoint` (receive with the sender's
    address, reply).
  - **TCP**: `OpenTcpRelayAsync(site, port, reason)` -> `ITcpRelay` : `IStreamListener` (`AcceptAsync` ->
    `IIncomingConnection` {`Stream`, remote address, site}; a half-close and the end of the stream pass through like
    `Dial`).
  - The port is given at runtime (a syslog plugin's port is a setting): opening a relay requests the port itself
    (Listen on, with the reason) when the node has not allowed it; `sitePorts` in `plugin.json` lists the ports known in
    advance (Plugins page, docs).
  - Every relay has a **state** (`RelayState`: Running, WaitingForApproval, PortInUse, NoPermission, SiteOffline,
    NotSupported (node without `relay.tcp` / `relay.udp`)) with a plain text and `StateChanged`, so a page can say "NTP at
    Building B: waiting for approval in the OADM Site Node app".
  - The host serves both interfaces for the server's own network too (`ICorePluginContext.Network.ListenUdpAsync` /
    `ListenTcpAsync`, bound and allowed by the host), so a plugin writes its server once and runs it locally and at every
    site. An HTTP helper on top of `IStreamListener` (Kestrel over the accepted streams) lets a plugin serve HTTP endpoints
    at sites as it does locally.
  - The plugin opens its relays when its feature turns on; the host closes every relay of a plugin that turns off or stops
    and reopens them after a node reconnects. The port must be in `sitePorts` and allowed in the node app.
  - **What a relay cannot do** (plugin docs say so; such a plugin declares `siteSupport: none`): broadcast and multicast
    (DHCP, SSDP), protocols that carry addresses or ports inside (FTP passive mode, SIP, RTP to dynamic ports), port
    ranges, logic on the node. While the site link is down the server is not there for the site's devices (a plugin like a
    RADIUS server says so).
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

- Rail page **Sites** (group Servers **(default)**, icon `server`; shown with `sites.view`): **Accept site nodes**
  (Administrator; off and nothing listed: an empty page with what sites are and **Accept site nodes**; on: **Turn off
  site nodes** in the toolbar, red, confirmed; on and empty: "Enter 10.0.0.5:5081 as the server address in the OADM Site
  Node app."); one grid: state icon, Name, Node address (where it connected from),
  Devices (count), **Response time**, **Connections** (now open), **Waiting for approval** (ports to allow in the node app),
  **Uploads and downloads** (running now), Version ("Update available"), Last seen. The State column has room for the
  reason ("Offline: The site node does not answer.", the whole text as its tooltip). Row actions (Administrator): Rename,
  Replace with... (tooltip "Replace with another site"), **Remove site** (red, confirmed, Keep devices); a removed site
  with kept devices: Replace with... and **Remove devices**. Nodes that connected and wait appear in the same list as
  "Waiting to be bound" rows with **Bind** (dialog: Site name, Pairing code); bound to another server or binding closed
  greyed with the reason. Errors below the list. Fake mode: two
  sample sites, one offline.
- **Site columns, one rule**: the device grid, the add page, the waiting list window and the Plugins page show their
  Site / Sites column while the server has sites (a bound, not bound or removed site; the client watches the site list
  with "See sites", else the devices decide) and hide it otherwise; the device grid's column chooser greys Site out
  meanwhile ("Bind a site first (Sites page)").
- Device grid: column **Site** (empty for the server's own network), in the search and the export. The address link and
  **Open web interface** of a device at a site open it in **Web View** (the user's PC usually cannot reach the site;
  Web View goes through the server and the node); with Web View off both are disabled: "Turn on Web View to open devices
  at a site"; with Web View on but a role that may not show it or open devices: "Your role does not allow opening
  devices." (the Web View page's own text). Status **Site
  offline** (amber device icon). Group by site may come later with Group by tag; `PageDeviceChoice` and the device
  picker may offer sites later like tags.
- Add page: a column **Site** in every mode (empty for the server's own network); nothing to choose. While the server has
  sites, Discovery says "Axis devices found on this server's network and at the sites." "No Axis device
  answered at X." means: not through the server and not through any site that reaches X. The DNS name check box looks up
  each device on its own route.
- Import from file: an optional column **Site** (by name), also written by the device export. "Listed before in line N"
  compares (site, address). A line without Site that reaches different devices through several sites lists all of them,
  not selected, with "Answers through Building A and Building B." Import from ADM keeps its expected MAC address per
  line.
- Waiting list window: column **Site**, in its export; "Already waiting" compares (site, address). Entries of a site that
  is offline are not tried (nothing written) until it is back. An entry without a site that answers through several sites
  adds the one device OADM does not manage yet; several unmanaged devices hold it ("Answers through Building A and
  Building B. Waits for Try now.", `WaitingDevice.Held`), not tried again by itself.
- Plugins page: column **Sites** ("Works at sites", "Only this server's network", the ports such as "UDP 123" that
  installers need for firewalls).

## Roles, audit and secrets

- `sites.view` "See sites" (Operator and Viewer yes): the Sites page. Administrator only, never grantable: Accept site nodes,
  Bind (with the pairing code), Rename, Replace, Remove site. Adding devices at a site: `devices.addRemove`.
- Audited: "Turned on site nodes" / "Turned off site nodes", "Bound site", "Wrong pairing code" (the node, from where), "Renamed site", "Replaced site", "Removed site", and (user "system",
  after the hysteresis) "Site offline" / "Site online", "Site node was reset"; "Device moved" and "Device added automatically" as today.
- What the node's machine sees: HTTPS and RTSP over TLS stay encrypted end to end. Plain HTTP devices and plain RTSP
  cross it readable (Digest never sends the password itself). **Basic over plain HTTP is never sent through a site**
  (the add page's `AllowBasicOverHttp` exception is off for site routes). User docs say so.
- **Backup**: the `Sites` table and `Sites.Identity` are in `oadm.db`; no format change. `manifest.json` counts sites and
  the restore report says "Sites: 3 restored". The one-session-per-identity rule keeps a restored copy from driving the
  live nodes.

## Packaging

- **Windows**: `OADM-SiteNode-<version>-win-x64.msi` (WiX 6 like the main MSI, `packaging/windows/`): `Program Files\OADM
  Site Node`, service "OADM Site Node" (`OadmSiteNode`, automatic, virtual account `NT SERVICE\OadmSiteNode`, restart on failure and after a stop with an error), Start menu shortcut to the
  app; no firewall rule (the node only connects out). A silent install takes `SERVER=<address>`.
  Built (stream 7): data folder for SYSTEM and the service SID only, native files extracted to its `runtime`, the folder on
  the system PATH; `SERVER` runs `oadm-site-node server <address> --timeout 60` after the service started (a refused
  address fails the install).
- **Linux**: `oadm-site-node_<version>_<amd64|arm64>.deb` (service, own system user `oadm-site-node`,
  `/opt/oadm/site-node`, `/var/lib/oadm-site-node` 0700) and `oadm-site-node-app_<version>_<arch>.deb` (the app, Depends
  `oadm-site-node`, desktop file). Raspberry Pi 4 and 5 are a target. Pre-release versions `1.2.0~rc.1`.
  Built (stream 7): `/usr/bin/oadm-site-node` (command line), app in `/opt/oadm/site-node-app` with
  `/usr/bin/oadm-site-node-app` and its own icon name `oadm-site-node`; the unit runs as `oadm-site-node` with
  `AmbientCapabilities=CAP_NET_BIND_SERVICE`, `RuntimeDirectory=oadm-site-node`, `StateDirectory` and `CacheDirectory`
  0700; data and user kept on purge. Details: `packaging/README.md`.
- Each package ships `LICENSE.txt`, `NOTICE.txt`, `LGPL-2.1.txt` (only if FFmpeg or other LGPL parts are in it),
  `TERMS.md` and its own `THIRD-PARTY-NOTICES.txt` (`tools/Oadm.Notices` reads the node's deps.json too; Linux in
  `/usr/share/doc/oadm-site-node*/`).
- **Server side**: the main MSI's firewall rule covers TCP 5081 too (Domain, Private) for the site listener (nothing
  listens there until Accept site nodes is on); the .deb and
  .pkg change no firewall today and stay so.
- `manage package site-node [--rid] [--version]` (both manage scripts and `scripts/manage-help.txt`).
- Release workflow (`release.yml`): job `installers` builds the node packages next to the others (the arm64 .deb on
  `ubuntu-24.04-arm`), installs, checks (service up, `Hello` answers unbound) and removes the .deb; `smoke-windows`
  (`smoke.ps1`) does the same for the node MSI; `release` uploads them and lists them in `SHA256SUMS.txt`. No portable
  ZIP. Pre-release tags behave as today.
- Website: the download area gets **Site node** (Windows, Linux menu x64 / ARM64 like the main downloads). Docs: a page
  "Sites" (install, bind, networks, reset, the outgoing port range).

## Tests

Three layers (decision 2026-10-19): unit tests and in-process tests run on all three systems in every CI run; real
networks are checked by hand on hardware before a release. **No Docker tests in v1**: what only Docker would prove (real
isolation, a real subnet, a pulled link) the manual test plan covers; Docker comes later only if real bugs show up there.

- **Unit** (no network): the node's logic with fakes (fake clock, fake list of network cards, in-memory connections).
- **In-process** (server and node in one test process, real gRPC and TLS over loopback): the node gets a test
  dialer that maps addresses to fake cameras (`FakeCamera` on random loopback ports), so two sites can both have
  192.168.0.20 pointing to different cameras; a few tests let the node dial real loopback ports (a closed port = refused,
  a port that never answers = timed out) so device socket errors and site errors are told apart on real sockets.
- **Manual test plan** (node `README.md`, like the DHCP plugin's): a Raspberry Pi and a Windows machine as nodes with the
  development camera behind them; through NAT, two network cards (all cards, then only the camera card), pull the
  cable (Site offline, back online after the waiting time), mDNS and range scan in the real subnet, NTP relay, a port not
  allowed. Read-only towards the camera; opt-in hardware tests (`Category=Hardware`) for what can be automated there.

What the layers check:

- Node: state machine with a fake clock (unbound open, closed after 24 h of run time, Allow binding, bound, reset with a
  new identity and code), pairing (right code, wrong code, 5 wrong = new code and 10 minutes closed, a server in between
  with its own key fails both proofs), the connection to the server (reconnect with backoff, worker connections on
  request), all cards by default, chosen cards and extra subnets (allowed and refused addresses), ports (both lists:
  allowed, refused, requested, allowed in the app, dismissed; a Connect to port never allows a relay), the connection log, `Dial` (bytes, half-close, cancel, deadline,
  device socket errors vs site errors, short and permanent limits), `Relay` (UDP and TCP: open, accept, half-close, close with the stream, port
  in use), `RangeScan`, capabilities.
- Server with an in-process node: Accept site nodes (off = nothing listens, on without restart, off
  closes the node connections), waiting nodes (limits, dropped after 24 hours), the server addresses a node falls back to,
  at most 2 large transfers per site (the third waits, polls go on), timeouts with the round trip on top, a manifest
  without `siteSupport` = none, the Web View link for site devices, bind with
  the code / remove / keep devices / not bound, a port not allowed (not contacted, the request sent once), identity rotation and "new
  identity" acceptance, one session per identity, copy detection, routing of VAPIX, RTSP (with a per-device RTSP port)
  and WebSockets with TLS pinning end to end, the client cache dropping a moved device, overlapping addresses on two
  sites (discovery, imports, the waiting list, the address conflict check), Site offline (computed status not stored,
  every status switch, refused new tasks and plugin calls, Log in / Set password refused, a running task across a short
  dropout, the site events, no "Device unreachable", hysteresis, compliance Not checked), moving devices (sticky route,
  1 hour rule, names never move, DHCP follow), replace, DNS lookups per route, a backup restored on a
  new server keeps its sites.
- The banned-API analyzer in the build (Send TCP message, NTP upstream still allowed); a test that every
  `VapixConnectionOptions` comes from the one helper.
- Perf (`Category=Perf`): 30,000 devices behind 10 sites polled through the nodes while a firmware upload and live views
  run through the same node (polls stay within their interval), 10,000 Device trigger WebSockets through 3 nodes, the
  waiting list with 30,000 entries tried through sites, a site going offline and online with 3,000 devices (one batch,
  no writes).
- Tests follow "tests that never flake": fake clocks for the binding window, the hysteresis and the 1 hour rule.

## Build order

1. Foundations: the spike of the reversed connection (Kestrel over a connection the node opened); `VapixClient` on
   `SocketsHttpHandler` (rebase the branch refactor, fix `RtspClient`), `DeviceRoute`
   through the one options helper, the factory, the probe and RTSP, the banned-API analyzer; `SiteOffline` in the SDK
   enum and every status switch; node service and app (identity, the connection to the server, pairing code, Hello / Bind / Unbind,
   Dial, networks, ports and requests, connection log, terms, theme assembly); server identity key, the site listener,
   `ISiteTransport` (control and worker connections, short and permanent limits), Sites page, `sites.view`, `Device.SiteId`, Add manually through the sites, polling, tasks, Site offline.
2. Discovery at sites (every add mode, mDNS, `RangeScan`, Resolve, DNS names), imports and Import from ADM with Site,
   the waiting list per (site, address), moving devices, Replace.
3. RTSP route in use (live view, PTZ, metadata, events), Device triggers, Web View, recording export, compliance.
4. SDK: `IDeviceNetwork`, `siteSupport` / `sitePorts`, relays for UDP (`IDatagramEndpoint`) and TCP (`IStreamListener`,
   the HTTP helper), local listeners through the same interfaces, NTP at sites, DHCP message, the site events.
5. Installers, release workflow, notices, website and user docs.

## Work streams

For building with several agents in parallel.

**Branches (decision 2026-10-19): main stays at the 3.6.0 state until Sites is accepted.** All work happens on the
branch `feature/sites`, made from the tag `v3.6.0` (not the old `feature/agents`, which is outdated; its refactor
commit 2cefed9 is picked over in stream 1). Each stream works on its own branch `feature/sites-<n>-<name>` from
`feature/sites` (an agent in its own worktree) and merges back into `feature/sites` when its tests are green. Nothing of
Sites is merged into or pushed to main, and no release tag is made from these branches, until the maintainer accepts the
whole feature; fixes on main meanwhile are merged into `feature/sites`, never the other way. Dropping Sites = deleting the
branches; main is untouched.

**Stream 0 comes first and alone**; afterwards the streams below touch
different files and meet only at the contracts of stream 0. Each stream ends with its tests green and the full unit run
(`manage test unit`) unchanged; a stream never changes a contract of stream 0 alone (a change goes back to stream 0 and
every stream that uses it).

0. **Contracts and spike** (one agent, merged before anything else): the spike of the reversed connection (decides
   Kestrel over the node's connection or the fallback); `site_node.proto`, `sites.proto`, the `site_id` fields and
   `SITE_OFFLINE = 6`; `ISiteTransport`, `DeviceRoute`, `SiteException` and its kinds; SDK surface as stubs with doc
   comments (`DeviceStatus.SiteOffline`, `IDeviceInfo.SiteId` / `SiteName`, `IDeviceNetwork`, `ISiteInfo`, the relay
   interfaces and `RelayState`, `siteSupport` / `sitePorts` in `PluginManifest`); the migration `Sites`; an in-memory
   `ISiteTransport` fake for the other streams' tests.
1. **Routing refactor** (Core): `VapixClient` on `SocketsHttpHandler` with the routed `ConnectCallback`, the one options
   helper `DeviceConnections.For`, `VapixClientFactory` cache key, `VapixDeviceProbe`, `RtspClient`, routed DNS, the
   banned-API analyzer (repo and plugin project SDK). With no site, OADM behaves exactly as before.
2. **Site offline everywhere**: the computed status, every status switch of the list in "Server" (host, plugins, client,
   fake mode), the site events, compliance Not checked, "Site errors are not device errors".
3. **Node program**: service and app (identity, the connection to the server, pairing, states, networks, both port lists and requests,
   connection log, `Dial`, `Relay`, `Browse`, `RangeScan`, `Resolve`, `Ping`, terms, theme assembly), its tests and
   manual test plan. Needs only stream 0.
4. **Server side of sites**: the site listener and Accept site nodes, `ISiteTransport` over the real node (the
   control and worker connections, limits, large transfers, timeouts), binding, `SiteService`, the Sites page and Bind
   dialog, roles and audit, backup. Joins streams 1 and 3 in the in-process tests.
5. **Discovery and devices at sites**: every add mode, imports and Import from ADM with Site, the waiting list per
   (site, address), overlapping networks, moving devices, Replace, the Site column, the Web View link.
   After streams 1 and 4.
6. **SDK and plugins**: `IDeviceNetwork` in Network settings and the NTP task, relays and local listeners, the HTTP
   helper, NTP at sites, DHCP message, `siteSupport` in every bundled plugin and the templates, `Oadm.Sdk.Testing` fakes,
   SDK docs and samples. After stream 0; the relays need stream 4 for the end-to-end tests.
7. **Packaging and docs**: node MSI and .deb, server firewall rule, `manage package site-node`, release workflow,
   notices, website downloads and the user page "Sites". After stream 3.

**Stream 0 result (2026-10-19)**, what the other streams build on:

- Contracts: `src/Oadm.Contracts/Protos/site_node.proto` (`SiteNodeService` incl. `PairStart`, `SetServerAddresses`,
  `OpenConnections`; `DialMessage` with `DialResult` and `DialError`: `DEVICE_*` are the device's socket errors, `SITE_*`
  the node's problems), `sites.proto` (`SiteService` incl. `SetAccept`), `DEVICE_STATUS_SITE_OFFLINE = 6`,
  `Device.site_id = 30`, `DiscoveredDevice.site_id = 21`, `WaitingDevice.site_id = 14`, `WaitingLine.site_id = 6`.
- `src/Oadm.Sites` (shared by server and node, no EF): `AcceptedStreamListener`, `SiteTls`, `PairingCode`, `NodePairing`,
  `ServerPairing`; tests `tests/Oadm.Sites.Tests`.
- Core `Oadm.Core.Sites`: `DeviceRoute`, `ISiteTransport.OpenStreamAsync` (device errors stay `SocketException` with the
  device's `SocketError`, so `DeviceStatusClassifier` keeps working; site problems are `SiteException` with `SiteProblem`
  and the standard texts of `SiteException.For`), `ISiteStates` (names, online, round trip, `Changed`), the `Site`
  entity; migration `Sites` (table `Sites`, `Devices.SiteId` with an index, `WaitingDevices.SiteId`; the unique index of
  `WaitingDevices.NormalizedAddress` stays: stream 5 puts the route into `WaitingDeviceStore.Normalize`).
  `Device.SiteName` is filled by reads like `CredentialUserName` (not a column).
- SDK (`Oadm.Sdk.Sites`, stubs with doc comments; behavior in streams 2 and 6): `DeviceStatus.SiteOffline`,
  `IDeviceInfo.SiteId` / `SiteName`, `SiteSupport` / `SitePort` / `SiteProtocol` (JSON as "full" / "udp"),
  `PluginManifest.SiteSupport` / `SitePorts`, `ITaskPlugin.SiteSupport`, `IComplianceCheckType.SiteSupport`,
  `IDeviceNetwork` (`ITaskExecutionContext.Network`, `ITaskQueryContext.Network`), `IPluginNetwork`
  (`ICorePluginContext.Network`: `ForDevice`, `ForSite`, `ListenUdpAsync`, `ListenTcpAsync`), `ISites`
  (`ICorePluginContext.Sites`), `ISiteInfo`, `IRelay`, `IUdpRelay`, `ITcpRelay`, `IDatagramEndpoint`, `IStreamListener`,
  `IIncomingConnection`, `Datagram`, `RelayState`, `SiteTexts`. `SdkInfo.Version` is raised by stream 6 together with
  `dev/compatibility.md`.
- Test fake: `tests/Shared/FakeSiteTransport.cs` (`ISiteTransport` + `ISiteStates`; sites with online, not bound, busy,
  allowed ports, networks and (host, port) -> loopback endpoint maps), linked into the Core and Server tests.

Streams 1, 2 and 3 run in parallel; 4 after 1 and 3; 5 and 6 in parallel after 4; 7 any time after 3. Shared files that
several streams touch (CLAUDE.md, `CHANGELOG.md`, `plugins/README.md`, `AccessPolicy`) are changed only at the end of
each stream, one stream at a time.
