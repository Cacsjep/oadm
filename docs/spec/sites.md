# Sites (OADM Site Node)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Released with 4.0.0 (decisions 2026-10-17 to 2026-10-21): the contracts and the link (`Oadm.Sites`), routing
(see "Device Communication"), Site offline (see "Data Model"), the node program (`Oadm.SiteNode*`) and its installers, and
the server side. **Outbound only** (decision 2026-10-20): the site node always connects to the server and never listens;
the inbound mode ("Wait for the server", the node's listener and mDNS announce, the server dialing nodes, Search / Add
manually / Change address on the Sites page, `FindNodes`, `ChangeSiteAddress`, `SiteMode`, the node firewall rule) is
gone; the columns `Sites.Mode` / `Port` stay for the migrations, unused. The node reaches **all network cards by default**
(`NodeConfig.AllCards`; chosen cards restrict it, no chosen card = all cards, `NetworkScope.ReachesAllCards`). Node app
(`Oadm.SiteNode.App`, 800 x 900, fixed size, one page, no tabs, decision 2026-10-21): cards Node status (a picture: the OADM logo
`Oadm.Theme.Controls.OadmMark` (the splash logo without animation, shared with `SplashLogo`) with the server's name, a line
with the status below it (grey not linked yet, purple "Ready to bind" (connected, not bound), green connected and bound, red not connected /
lost / error, `LinkKind`), a hexagon
with the sites icon and the site or computer name; the binding line and node ID with version centered below the status;
then requests and relays that do not run), Server (two columns, labels above: Server address left; while not bound
the **Pairing code** right in a read-only field like the address with a copy button, the hint "... Valid for N more
minutes." below it, or Allow binding), Networks (Reach devices on = a
select with check boxes: All cards, then each card with its addresses on a second line; extra networks only on the command
line), Ports (**Add** right of the title opens
the dialog `AddPortWindow` (Direction Out / In, Protocol, Port); one table: Direction (Out = the node connects to devices,
In = it listens at the site), Protocol, Port, State; requests first with Allow / Dismiss, allowed ports with Remove); footer
Reset left (red), Save and Close right. No connection log in the app (the node's log files and `oadm-site-node log`). The
pairing code is part of the state every local user reads while the node is not bound (decision 2026-10-21: a code lasts
10 minutes, `BindingState.CodeLifetime`; the app counts the last minute in seconds, `oadm-site-node pairing-code` prints
"Valid until"; a pairing started with a code still binds for a minute after the code's scheduled end; reading it with the
command line is logged); a bound node that accepted a new server identity gives its code to administrators only (Show
pairing code in the app). Add port in the app never marks a port as the server's (`PortPolicy.Allow(fromRequest: false)`).
- The node connects out with a backoff of 1, 2, 4 and 8 s, then every 10 s while the server cannot be reached
  (`NodeOptions.MaxBackoff`, decision 2026-10-22).
- `Oadm.Server.Sites`: `SiteManager` (`ISiteStates`, `ISiteTransport`, `ISiteLargeTransfers`; `SiteOptions`: offline after
  30 s without an answer, online after 60 s stable, after a start "starting" until the node settles or 2 minutes, while
  starting no site events, compliance results stay and devices' status events start from `Device.StoredStatus`),
  `SiteListener` (TCP `Sites.ListenUrl`, default 0.0.0.0:5081, only while `Sites.Accept`; Windows service: the rule
  "OADM Server (Site nodes, TCP 5081)" through `FirewallRuleKeeper` while it is on, the first sync at start always
  applies, removed when off or stopped; 120 new connections a minute per address before TLS (IPv6 per /64,
  `Oadm.Sites.Transport.AddressRateLimiter`: full = the oldest address makes room), handshake timeout, 64 handshakes at
  once and 8 per address, closing waits for every handshake, 64 links of unknown keys before Hello (5 s; a bound site's
  key is not capped), 16 KB answers until a link carries a bound site's key (`NodeLink.Trust`), Hello checked by
  `HelloRules`, 10 new unknown connections a minute per address, 50 waiting nodes kept by key fingerprint, 3 per address,
  full = the one waiting longest makes room, dropped after 24 h; binding with J-PAKE (`Oadm.Sites.Binding`), binds one at
  a time, the node's Bind runs to its end whatever the caller does, Unbind when the site cannot be stored or Bind's answer
  was lost, a node that accepted a new server identity binds its site again with the code; links become sessions or
  waiting nodes only while Accept is on and the server runs, Remove takes the site and its session in one step, a session
  whose site is gone ends; the waiting times run on `TimeProvider.GetTimestamp`, never the wall clock; the tick ends the
  session of a node silent for 30 s; online / offline audits are written after the lock; the Sites page reads the device
  counts only after the device feed shows a device that changed its site),
  `SiteSession` (control link with Watch and a 10 s Hello heartbeat and the round trip, up to 8 worker links for Dial,
  a new worker when every one carries 2,000 dials), `SiteIdentityStore` (`Sites.Identity`, purpose `oadm:site-identity`;
  dropped with a replaced master key and named in the notice), `SiteAdmin` (Remove with or without devices, Replace),
  `SiteGrpcService`. At most 2 large transfers per site (streamed bodies over 1 MB or of unknown size, streamed answers
  except the Web View relay's (`VapixClient.RelayedOption`); a task waits as long as it takes, its device timeout starts
  after the wait and the tasks pane says "<step> - Waiting for the site link" (`SiteLinkWait`); anyone else waits inside
  the request's timeout, then "Site ... is busy"; a dropped streamed answer frees its slot when collected). A dial gets the
  site's verdict before the caller's timeout (node timeout less a margin, worker wait and node answer as Busy); a dial whose
  link or session ends is an `IOException` around "Site offline: ..." (`DialStream`), a cancelled one an
  `OperationCanceledException`; a poll that gets no answer while the node does not answer a Hello either
  (`ISiteStates.LinkAnswersAsync`) is a site problem. One control link attaches per site at a time; a port request follows
  the caller's token and is asked again when it did not arrive and after every reconnect.
- Permission `sites.view` "See sites" (Devices, Operator and Viewer yes); every SiteService change is Administrator only.
  Audit: Turned on / off site nodes, Bound site, Wrong pairing code, Renamed site, Replaced site,
  Removed site, Site offline / online, Site node was reset (system). Backup: "Sites: N restored".
- Client: rail page **Sites** (right below Compliance, else Devices, no group; `HostPages.Sites`, icon `server`, `Sites/SitesViewModel`, one dialog
  `SiteFormWindow` for Bind / Rename / Replace / Remove; Bind starts with the node's host name, "acs (2)" when a site has
  it, or the site's own name when its node binds it again; Replace with lists only other online sites), device column **Site**
  (`Device.site_name = 31`, search and export); fake mode Building B (online), Warehouse (offline) and a waiting node.
- Plugins at sites (stream 6): `IDeviceNetwork` (`Oadm.Core.Sites.DeviceNetworks`: `LocalDeviceNetwork` on the server's
  own network, `SiteDeviceNetwork` over `ISiteNetwork`) in `ITaskExecutionContext` / `ITaskQueryContext.Network` and
  `ICorePluginContext.Network` (`PluginNetworkScope`: listeners bound by the host with the Windows firewall rule, a port in
  use retried every 30 s). `ICorePluginContext.Sites` = relays over the node's Relay call on worker links (`SiteRelay`,
  states with `SiteTexts.RelayStateText`, reopened after a reconnect); the host disposes a plugin's listeners and relays
  when it stops or is turned off. `siteSupport` (manifest; missing = none; `ITaskPlugin` / `IComplianceCheckType.SiteSupport`
  override) is enforced by the host: tasks greyed "Not available for devices at a site", runs and queries refused,
  compliance Not checked, VAPIX clients and event streams of core plugins (`SiteSupportGuard`) and actions refuse site
  devices. The plugin project SDK writes `siteSupport` (`OadmSiteSupport`, default full) and `sitePorts` (`OadmSitePorts`
  "udp:123:NTP server"); bundled: DHCP none, NTP full + UDP 123, the others full. NTP "Serve at sites" (a select with check boxes:
  All sites, chosen sites or none, `NtpConfig.ServeSites`; one relay per served site, limits per site and client; a site no
  longer served gets its relay released (`IRelay.ReleaseAsync`, proto `RelayMessage.release = 4`): its node drops the
  request and the allowed port it allowed from the server's request (`PortEntry.ByServer`), a port the node's administrator
  added stays; a lost link, a server stop or the plugin turned off only dispose and keep the port); DHCP page "The DHCP server works only on this server's own network."; Assign IP address
  refuses addresses outside the site's networks (`IDeviceNetwork.Reaches`). The Plugins page has a Sites column
  (`PluginPackageInfo.site_support = 12`, `site_ports = 13`). `Oadm.Sdk.Testing`: `FakeDeviceNetwork`, `FakeSites`,
  `FakePluginNetwork`, `InMemoryUdpRelay` / `InMemoryTcpRelay`. SDK 0.2.0. Developer page `dev/sites.md`.
- Discovery at sites (stream 5): every add mode asks the server's own network and every online site (`ISiteNetwork`
  over the control link, `SiteManager.Network`; `Reaches` from the node's Watch). Results carry their route
  (`DiscoveredDevice.site_id = 21`, `site_name = 22`, `other_sites = 23`, `problem = 24`); the same MAC address through
  several routes is one row through the site, the others in the Site tooltip; different devices at one address through
  different routes are separate rows. Commit stores `Device.SiteId`. Import from file reads a Site column (by name); a line
  without a site that reaches different devices lists all, unchecked, with "Answers through Building A and Building B.";
  an ADM line only uses the route where its MAC answers. The waiting list is per (site, address)
  (`WaitingDeviceStore.Normalize(address, siteId)`, `WaitingDevice.site_name = 15`, column Site). Moves: Unreachable
  devices and devices of a site offline for more than an hour are looked for through every route and moved with route and
  address together (audited "Device moved", "from Building B 192.168.0.20 to This server 10.0.0.20"); a device answering
  on its route never moves, names never move, a DHCP lease moves to the server's network; to another route only a device
  with a pinned HTTPS certificate, verified against that pin there (over HTTP a node could answer the login itself: such a
  device moves only within its route, `DeviceAddressChangeResult.NotPinned`, logged once). The address conflict check of
  Network settings / Assign IP address lists only devices on the same route. The address link and Open web interface of a
  device at a site open it in Web View (`ICorePluginClientContext.OpenDeviceRequested`), disabled with "Turn on Web View to
  open devices at a site" while it is off. Texts `Oadm.Contracts.Sites.SiteDiscoveryTexts`.
- Known residual risk (as on the server's own network, see "Automatic login on the add page"): a device at a site that
  imitates the anonymous Axis answer, or a compromised site node pretending to be one, gets the credential list's
  passwords tried on it; the user docs say so (Sites > Overview).
- User docs: a top-level website section **Sites** (`website/src/content/docs/sites/`: Overview, Set up a site, Sites page,
  OADM Site Node app; screenshots from the headless tests; `/admin/sites` redirects to it); developer page `dev/sites.md`.
- Not built yet: identity rotation over gRPC, the HTTP helper on `IStreamListener`.
