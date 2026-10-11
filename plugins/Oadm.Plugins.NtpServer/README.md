# NTP server (`oadm.ntp-server`)

Core plugin (+ `.Client`), rail page **NTP server** (group Servers, icon `clock`). Own RFC 5905 server-mode
implementation (SNTPv4 compatible), no library.

- Time source: without an upstream OADM is a fully valid NTP server on its own clock, like chrony's
  `local stratum 10`: LI 0, stratum 10, reference id `LOCL`, reference timestamp = now, root delay 0, root dispersion
  10 ms; never LI 3 / stratum 16, no OS clock-sync detection. With an upstream (one host name or IP, optional
  `host:port`): stratum = upstream + 1, reference id = upstream IPv4 address (IPv6: first 4 bytes of its MD5), root delay
  and dispersion accumulated (+15 ppm per second since the last sync). The OS clock is served, never set.
- Upstream (`UpstreamMonitor`, own loop, fully decoupled: requests are always answered at once from the current
  `TimeSourceState`, an immutable record the responder reads without locks): one query in flight, hard timeout 2 s per
  query, DNS with its own 3 s timeout (also when the resolver ignores the token) cached 5 min and kept stale on DNS
  failure; a fresh socket per query; answers must come from the queried address and port and carry our transmit
  timestamp as originate (16 random low bits), else ignored; sanity checks: mode 4, stratum 1..15, LI != 3, non-zero
  timestamps, round trip < timeout; Kiss-o'-Death (stratum 0) rejected. Good answer: poll 64 s, doubling to 1024 s after 4
  good answers in a row. Failure: retry after 2, 4, 8 ... s up to the poll interval; after 3 failures in a row the
  upstream is not reachable: local mode (stratum 10) + warning, back automatically on the next good answer; while 1-2
  failures the last good upstream state is still served. KoD RATE doubles the poll interval, DENY/RSTR wait 1024 s.
- Protocol: UDP 123 (`NtpServerOptions.Port`, tests use 0 = random port on 127.0.0.1), IPv4 and IPv6 in separate sockets.
  Mode 3 requests of version 3 and 4 with a transmit timestamp get one 48-byte mode 4 answer (VN and poll echoed,
  precision from the Stopwatch resolution, originate = request transmit, receive taken when the datagram is read,
  transmit set right before sending); everything else (modes 1/2/5/6/7, other versions, < 48 bytes) is dropped silently.
  NTP era handling (2036) in `NtpTimestamp`. The receive loop reuses one buffer, one answer buffer and one
  `SocketAddress`; the client key is read from the sockaddr bytes (no allocation per packet). Windows: SIO_UDP_CONNRESET off.
- Abuse protection (`NtpRateLimiter`): token bucket per client IP, burst 8, refill 1 per 2 s; over it: drop, one
  Kiss-o'-Death RATE per client per minute, one "Rate limited" log entry per client per minute. Global cap 2,000
  requests/s (dropped, status "Too many requests, dropping" for 10 s). Client table LRU, at most 10,000 entries, idle
  entries expire after 10 min. No allow list, no NTS in v1.
- Settings (plugin setting `config`: enabled, interfaceId, interfaceName, upstream) stored server side and restored on
  server start. Interfaces from the SDK `IServerNetworkInterfaces` (every up, non-loopback interface with an address;
  "All interfaces" binds 0.0.0.0 and [::]; an interface binds all its addresses except IPv6 link-local). A failed bind
  (port in use, interface gone) is retried every 30 s while enabled.
- Status line (one at a time, `ui:StatusChip`, detail as tooltip): `Running on 10.0.0.17:123` / `Running on all
  interfaces, port 123` (ok), `Stopped` (neutral), `Port 123 is in use by another program` (error; Windows +
  " (Windows Time service)" when `sc query W32Time` says RUNNING, tooltip: net stop w32time; Windows has no privileged
  ports, so AccessDenied there also means in use), `Insufficient permission to use port 123` (error; Linux tooltip: run
  as root or `sudo setcap 'cap_net_bind_service=+ep' <server exe>`; macOS: run with sudo or choose All interfaces),
  `Interface <name> is not available` (error; AddressNotAvailable or the interface is gone), then warnings in this
  order: `Too many requests, dropping`, `Upstream <host> does not answer, using this computer's time`, `Server clock differs
  from upstream by 3.2 s` (> 1 s). Mapping in `NtpStatusTexts.ForBindError` (SocketError x OS).
- Request log: last 40 in memory (`RequestLog` ring buffer of value entries, no table): time, client, offset (client
  transmit minus server receive; "-" beyond one day, e.g. clients that randomize the transmit timestamp), result Answered
  / Rate limited. Pushed live (see `ICorePluginContext.Events`, batched every 500 ms); the page resolves the device column
  ("P3265-V (10.0.0.48)") from the client's device list (O(n) per device change).
- Page methods (`NtpServerMethods`): `getState` -> `NtpState` (config, status, interfaces, requests, upstream, stratum,
  port); `save` ({enabled, interfaceId, upstream}) validates the upstream with one query (same timeouts; the running
  server keeps serving) and replies {saved, upstreamError, upstreamResult, state}; an upstream that does not answer is
  not saved (error under the field). Events: `state` (status/upstream changed), `requests` (new entries).
- Page (one card in the host card, subtitle in the host page header, status chip left of the Save button); Enable NTP server;
  Listen on (select, refreshed when the page opens); Upstream server (optional, checked while typing, field errors under
  the field, "Checking <host>" accent chip while Save runs, "Answered: 3 ms off, 12 ms round trip" after
  it, a line "Serving stratum 3 from ..." / "Serving the server clock ..."); Save (primary); "Last requests" DataGrid
  (Time, Client, Device, Offset, Result chip). The view model watches the events while the view is attached; when the
  stream ends or fails (or the host has none: fake mode) it re-reads the state every 2 s.
- Task "Use OADM as NTP server" (contributed, id `oadm.ntp-server.use`, group Maintenance, no dialog, `CanRun`: ntp 1.x
  or param.cgi): the address is the selected interface's address of the device's family, with "All interfaces" the
  local address the server routes to the device with (UDP connect, no packet). Steps: Check compatibility (fails "The
  OADM NTP server is not running ... Nothing was changed." when it is off), Read NTP settings, Set NTP server <addr>
  (Skipped "Already uses <addr>"), Verify NTP settings (Warning on a mismatch). Name "Use OADM NTP server 10.0.0.17"
  ("Use OADM NTP server" with All interfaces). The VAPIX requests are the Date and time plugin's (`TimePlanner`,
  `TimeClient`; its `Model/` and `Vapix/` sources are compiled into the NTP assembly, because a project reference would
  put `Oadm.Plugins.DateTime.Server.dll` into this plugin folder, where the loader would register Date and time twice).
- Per OS: Windows needs nothing but a free port 123 (W32Time holds it on many machines: stop it, then OADM serves; the
  firewall must allow inbound UDP 123); Linux needs root or `cap_net_bind_service` (chronyd/ntpd must not hold 123);
  macOS needs sudo for a single interface (wildcard binds below 1024 are allowed without root since 10.14).
- Tests: `tests/Oadm.Plugins.NtpServer.Tests` (codec round trip, era wrap, invalid input; responses incl. local mode and
  ignored modes; rate limiter burst/refill/LRU/global; status per SocketError and OS; interface listing; upstream
  client against `FakeUpstream`: timeout, slow and late answers, wrong origin, other port, KoD RATE/DENY,
  unsynchronized, DNS failure/hang/cache; monitor fallback and recovery; in-process loopback integration: valid mode 4
  answer a client accepts, rate limiting with one KoD, answers within 100 ms while the upstream hangs, upstream
  validation on Save, persistence across restart, port in use retried, missing interface; the task against the Date and
  time fake camera; page view model; headless screenshots `ntp-server-page.png`, `-errors.png`,
  `-upstream-warning.png`, `-checking.png`). A hardware test where the development camera queries OADM is a device write and is not
  written/run without explicit user approval.
