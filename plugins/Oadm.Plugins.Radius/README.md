# 802.1X server (oadm.radius)

An OADM PRO plugin. Spec: `docs/spec/radius-server.md`. License: `LICENSE` in this folder (source-available, not
Apache 2.0).

- Core plugin "802.1X server" (group Security, icon `lock`), off by default, `"pro": true`. OADM answers the switches as
  their RADIUS server and authenticates the cameras on the switch ports with EAP-TLS. Page methods and events:
  `Api/RadiusApi.cs` (shared with the page); events for the event rules "Device authenticated" and "Authentication
  failed".
- Server part `Server/` (code map in the spec, "Code (server)"): `RadiusService` (listener with Listen on, a relay per site
  with switches, 64 workers, the tick of the Sessions tab and the log), `SwitchTable` (immutable, swapped on every change),
  `DeviceCertificateValidator` and `CaTrust` (who passes), `DeviceIndex` (the managed devices in memory),
  `ServerIdentityKeeper` (the server certificate from the OADM CA), `SessionBoard`, `RadiusLog`, `VlanPlan`, `RadiusStore`.
- Settings: `config`, `switches` (secrets encrypted with the master key), `vlans`, `serverCertificate` (key encrypted);
  records: `log`, `logCertificates`. All in the encrypted backup.
- The Axis device ID CAs are shared with EST (`plugins/Shared/DeviceId`, linked with their PEM files).
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
  parsing rules, fuzzing, fragmentation, alerts, timeouts; the plugin end to end through its in-memory UDP listener and
  site relays, `ServerTests`; the rules of the first review, `HardeningTests`; the device-group canary and filtering,
  `PluginTests`; `Category=Perf` for many parallel
  sessions and 30,000 authentications through the plugin).
- Not built yet: recorded conversations of real switches (Cisco, Aruba, ...): none could be recorded here; the tests
  replay the RFC examples. A new switch quirk becomes a fixture first.
- Client part (`Oadm.Plugins.Radius.Client.dll`, `plugins/Oadm.Plugins.Radius.Client`): the page "802.1X server" with the
  fixed tabs Server (General with Advanced, Switches with the switch dialog, VLANs, one Save), Sessions (live), Log (search,
  View certificate, Delete all) and Revoked (Revoke through the shared `ui:IssuedCertificateList` of the client SDK); code
  map in the spec, "Code (page)". The page uses the server part's contract `Api/RadiusApi.cs` (project reference, like EST).
  Tests: `tests/Oadm.Plugins.Radius.Tests/Client` against `FakeRadiusBackend`
  (view models, headless rendering and screenshots with `OADM_SCREENSHOT_DIR`, `Category=Perf` for 10,000 sessions and
  30,000 devices).
