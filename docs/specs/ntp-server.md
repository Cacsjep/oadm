# NTP server core plugin (spec)

Status: decided with the user on 2026-10-07. Plugin id `oadm.ntp-server`, core plugin with a rail page,
contributes the task "Use OADM as NTP server". Own implementation (no .NET NTP server library exists;
GuerrillaNtp etc. are clients only). Simplicity first: the user enables it, picks an interface, saves.

## Page (one card, nothing else)

```
NTP server                                   [● Running on 10.0.0.17:123]   <- status line (StatusChip)

  [x] Enable NTP server
  Listen on        [ Ethernet - 10.0.0.17 (Intel I219)        v ]   all interfaces with IPv4/IPv6, plus "All interfaces"
  Upstream server  [ pool.ntp.org                                ]   optional, empty = server clock only
                                                        [ Save ]

  Last requests                                                     <- last 40 requests, newest first
  Time       Client        Device             Offset    Result
  18:02:11   10.0.0.48     P3265-V (10.0.0.48) +2 ms    Answered
  18:02:09   10.0.0.99     -                  -         Rate limited
```

- Interface list: every up interface with its addresses (cross-platform via NetworkInterface), refreshed
  when the page opens; "All interfaces" binds 0.0.0.0 and [::].
- Upstream: one optional host name or IP. Save validates it (resolves and queries it once) and shows the
  result next to the field (field error rules: under the field).
- Status line, always one of (ui:StatusChip, plain text, no chips):
  `Running on <addr>:123` (ok), `Stopped` (neutral), `Port 123 is in use by another program` (error;
  on Windows mention the Windows Time service when it holds the port), `Insufficient permission to use
  port 123` (error; Linux: run as root or grant CAP_NET_BIND_SERVICE, exact command in the tooltip/docs;
  macOS: run with sudo), `Interface <name> is not available` (error), `Upstream <host> not reachable,
  serving the server clock` (warning).
- Request log: last 40 entries in memory (ring buffer), pushed live to the page; device column resolves
  the client IP against managed devices. No database table.

## Time source: server clock plus upstream

- The OS clock is the time base (it is normally synced by Windows Time, chrony, systemd-timesyncd).
- With an upstream configured, the plugin queries it every 64..1024 s (exponential backoff on failure),
  using its own client on the same packet codec, and reports stratum = upstream stratum + 1 and
  reference id = upstream address; the measured offset between server clock and upstream is shown as a
  warning when > 1 s ("Server clock differs from upstream by 3.2 s"); the plugin does NOT set the OS clock.
- Without upstream (user decision): OADM is a fully valid NTP server on its own clock, like chrony's
  `local stratum 10` / ntpd's local clock: leap indicator 0 (synchronized), stratum 10, reference id
  "LOCL", reference timestamp = now, small root dispersion. Clients (Axis cameras) accept and sync.
  There is NO "server clock not synchronized" status and the server never answers as unsynchronized
  (LI 3 / stratum 16) in this mode. If an upstream is configured but unreachable, it falls back to this
  local mode and the status says "Upstream <host> not reachable, serving the server clock" (warning).

## Protocol (RFC 5905 server mode, SNTPv4 compatible, RFC 4330)

- UDP, IPv4 and IPv6, port 123. Answer mode 3 (client) requests with mode 4 (server) responses; versions
  3 and 4; ignore everything else (control mode 6/7, broadcast, symmetric) silently: no amplification.
- Response: LI 0, VN = request VN, mode 4, stratum, poll = request poll,
  precision from Stopwatch resolution, root delay/dispersion, reference id, reference timestamp (last
  upstream sync or now), originate = request transmit timestamp, receive timestamp taken immediately
  when the datagram is read, transmit timestamp set right before sending. NTP era handling (2036) in
  the codec.
- Responses are never larger than requests (48 bytes, no extensions); malformed or short packets dropped.

## Security / abuse protection

- Per-client rate limit: token bucket per source IP (e.g. burst 8, refill 1 request / 2 s); over the
  limit: drop silently, optionally send one Kiss-o'-Death "RATE" per client per minute. Log as
  "Rate limited" (log entries themselves rate limited).
- Global limit: max 2,000 requests / s total; beyond that drop and show a warning status
  ("Too many requests, dropping").
- Bounded memory: the per-client table is an LRU with a cap (e.g. 10,000 entries), entries expire after
  10 min idle. Receive loop never allocates per packet beyond a pooled buffer.
- Only the selected interface is bound. Optional allow list is NOT in v1 (simplicity).
- No NTS in v1 (RFC 8915 needs a TLS key-exchange server + AEAD extensions; later goal).

## Task contributed: "Use OADM as NTP server"

Group Maintenance (or Time with Date and time). Points the selected cameras' NTP configuration at the
OADM server address on the interface they reach (reuse the Date and time plugin's NTP request code via a
shared library or call its logic; do not duplicate VAPIX code). Steps: Check compatibility, Read NTP
settings, Set NTP server <addr>, Verify NTP settings, Completed. Task name "Use OADM NTP server 10.0.0.17".

## Cross-platform and tests

- Windows, Linux, macOS. Error detection maps SocketError.AddressAlreadyInUse / AccessDenied /
  AddressNotAvailable to the status texts above.
- Tests: packet codec (round trip, era wrap, invalid input), server-mode response building, rate limiter
  (burst, refill, LRU cap, global cap), interface listing abstraction, status mapping per SocketError,
  upstream client against a fake server, an in-process integration test on a random high port (bind
  port is injectable for tests) using GuerrillaNtp or our client as the querying side, page view model,
  headless screenshots. A hardware test (opt-in Category=HardwareWrite, never automatic) where 10.0.0.48
  queries the OADM NTP server is NOT run without explicit user approval.
