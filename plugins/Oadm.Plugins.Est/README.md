# EST server (oadm.est)

The first OADM PRO plugin. Spec: `docs/spec/est-server.md`. License: `LICENSE` in this folder (source-available, not
Apache 2.0).

- Core plugin, rail page **EST server** (group Security, icon `lock`), off by default, `"pro": true`: the host runs it only
  while OADM PRO is active; every request checks the license again (503 "OADM PRO is not active.").
- Server part (`Oadm.Plugins.Est.Server.dll`):
  - `Serving/EstService`: listens on TCP 9454 (setting `config.port`, 1025..65535) on the server's network through
    `ICorePluginContext.Network.ListenTcpAsync`; TLS 1.2+ (`SslStream`, client certificate requested, checked per request);
    at most 64 connections, 20 requests and 30 s idle per connection.
  - `Serving/ServerCertificates`: the server's TLS certificate from the OADM CA (ECC P-256 key made in memory, 90 days,
    made again with a third left, when the addresses or the CA change); SAN = the server's addresses (network cards that
    are up) and host name; one per site (stage 4).
  - `Serving/EstEndpoint`: `GET /.well-known/est/<profile>/cacerts` (the CA chain), `POST .../simpleenroll`,
    `POST .../simplereenroll`; everything else and unknown or disabled profiles 404. Who may enroll: the camera's Axis
    device ID (or a certificate EST issued it, not revoked, chaining to the OADM CA) whose serial is a managed device of
    the same site. Answers: PKCS#7 certs-only, base64 at 76 characters, leaf and chain. Refusals: 401 (no or no valid
    identity), 403 (not managed, other site, revoked), 400 (request unreadable or refused by the CA), 503 (no PRO, no CA).
  - `DeviceId/AxisDeviceId`: the Axis device ID CAs (RSA and ECC bundles, roots and intermediates as Axis publishes
    them, embedded); the serial is the subject's X.520 serialNumber (12 hex digits).
  - `Protocol/EstHttp`: HTTP/1.1 (16 KB headers, 64 KB body, Content-Length or chunked, `Expect: 100-continue`,
    keep-alive); `Protocol/EstMessages`: PKCS#7 certs-only and PKCS#10 in base64.
  - `Serving/EstStore`: plugin settings `config` (port, profiles, enrollments kept 100..5000), `enrollments` (newest
    first), `certificates` (dropped 30 days after they end), `revoked` (device and profile pairs); written at most every
    2 s.
  - Signing: `IDeviceCertificateAuthority` of PKI (`ctx.Services`), names from OADM (CN = the device's address; SAN =
    its address, the address it connected from on the server's network, host name, `axis-<serial>.local`), validity from
    the profile, source "EST".
  - Events: "Certificate enrolled" (`oadm.est.enrolled`: profile, kind) and "Enrollment refused" (`oadm.est.refused`:
    profile, reason), only for managed devices.
  - Methods: `getState`, `getEnrollments`, `getCertificates` (search, 500 a page) for everyone; `save`, `revoke` Admin
    (permission `oadm.est:manage`).
- Tasks (`Tasks/EstTasks.cs`, Security, confirmation popups in `EstTaskDialogs.cs`): **Set up certificate enrollment**
  (AXIS OS 12.10+, cert v1 and cert-est v1 checked fresh; the camera's profiles `oadm_<profile>` with the device ID, the OADM
  CA chain, this server as EST server; waits for ENROLLED; pins a new HTTPS certificate) and **Remove certificate
  enrollment** (only `oadm_...` profiles). Not at sites yet. `Device/CameraEstApi.cs`: the camera's REST calls.
- Client part (`Oadm.Plugins.Est.Client.dll`): port, enrollments kept, profiles (name, use, days, key, on; at most one per
  use), Save with the status left of it; Enrollments (live); Certificates with search and **Revoke** (red, confirmed).
- Revoke: the camera keeps the certificate until it ends, but the device gets nothing more for that profile until it is
  set up again (the set-up task clears it, stage 3).
- Tests: `tests/Oadm.Plugins.Est.Tests` (protocol, device ID, TLS end to end over in-memory connections with a test device
  ID hierarchy and a fake CA, the page with screenshots).
