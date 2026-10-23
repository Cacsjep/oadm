# 802.1X server (oadm.radius)

An OADM PRO plugin, in work. Spec: `docs/spec/radius-server.md`. License: `LICENSE` in this folder (source-available, not
Apache 2.0).

- Core plugin "802.1X server" (group Security, icon `lock`), off by default, `"pro": true`. OADM answers the switches as
  their RADIUS server and authenticates the cameras on the switch ports with EAP-TLS. So far the plugin starts nothing and
  has no page; the server part (listener, switches, sessions and log, certificate checks) and the page come next.
- Server part (`Oadm.Plugins.Radius.Server.dll`), protocol layer `Protocol/` (no sockets, no host, no UI; code map in the
  spec, "Code (protocol)"):
  - `Radius/`: the own RADIUS codec (RFC 2865, 2869, 3579, 5080, 5997, 8044): packets with their attributes in order and
    raw, malformed packets as a result value, the Access-Request reader, the Response Authenticator and the
    Message-Authenticator (always the first attribute of an answer), the duplicate cache, MS-MPPE keys (RFC 2548) and VLAN
    attributes (RFC 3580).
  - `Eap/`: EAP packets (RFC 3748) and the EAP-TLS header with the L, M and S flags (RFC 5216).
  - `Tls/`: the BouncyCastle TLS server in non-blocking mode (versions and cipher suites from the settings, client
    certificate required and handed to `IClientCertificateValidator`, alerts in both directions), `TlsAlertTexts`, the
    cipher suite catalogue and `TlsServerIdentity`.
  - `Sessions/`: `RadiusProcessor` (one datagram in, at most one answer out; the session table, timeouts) and the
    session state machine with the spec's states.
  - `Text/`: MAC address normalizer, NAS-Port-Type names, text sanitizing.
- BouncyCastle (`BouncyCastle.Cryptography` 2.7.0) is compiled against and never copied into the plugin folder: the
  server ships the same version and the plugin load context falls back to it.
- Tests: `tests/Oadm.Plugins.Radius.Tests` (a BouncyCastle client as the camera through an in-memory switch, RFC vectors,
  parsing rules, fuzzing, fragmentation, alerts, timeouts; `Category=Perf` for many parallel sessions).
