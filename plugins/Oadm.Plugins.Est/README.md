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
    are up) and host name; at a site the node's addresses the cameras were given.
  - `Serving/EstEndpoint`: `GET /.well-known/est/<profile>/cacerts` (the CA chain), `POST .../simpleenroll`,
    `POST .../simplereenroll`; everything else and unknown or disabled profiles 404. Who may enroll: the camera's Axis
    device ID (or a certificate EST issued it that is still kept, chaining to the OADM CA) whose serial is a managed device of
    the same site. Answers: PKCS#7 certs-only, base64 at 76 characters, leaf and chain. Refusals: 401 (no or no valid
    identity), 403 (not managed, other site, certificate of another camera), 400 (request unreadable or refused by the CA), 503 (no PRO, no CA).
  - `DeviceId/AxisDeviceId`: the Axis device ID CAs (RSA and ECC bundles, roots and intermediates as Axis publishes
    them, embedded); the serial is the subject's X.520 serialNumber (12 hex digits).
  - `Protocol/EstHttp`: HTTP/1.1 (16 KB headers, 64 KB body, Content-Length or chunked, `Expect: 100-continue`,
    keep-alive); `Protocol/EstMessages`: PKCS#7 certs-only and PKCS#10 in base64.
  - `Serving/EstStore`: plugin settings `config` (port, profiles, enrollments kept 100..5000), `enrollments` (newest
    first), `sites`; plugin records `certificates` (`ctx.Records`, one row per certificate: per device and profile the
    newest and the one before it; dropped 30 days after they end or when the device is gone; only changed rows are
    written); written at most every 2 s.
  - Signing: `IDeviceCertificateAuthority` of PKI (`ctx.Services`), names from OADM (CN = the device's address; SAN =
    its address, the address it connected from on the server's network, host name, `axis-<serial>.local`), validity from
    the profile, source "EST".
  - Events: "Certificate enrolled" (`oadm.est.enrolled`: profile, kind) and "Enrollment refused" (`oadm.est.refused`:
    profile, reason), only for managed devices; "Certificate not renewed" (`oadm.est.notrenewed`: profile, validUntil)
    from the hourly `CheckRenewalsAsync` (newest certificate per device and profile, halfway through its lifetime plus
    `NotRenewedGrace`: Axis cameras renew halfway and retry daily; removing an `oadm_` profile stops the check).
  - Methods: `getState`, `getEnrollments`, `getNotRenewed` (500 at most) for everyone; `save` and `clearEnrollments`
    Admin (permission `oadm.est:manage`).
- Task **Certificate enrollment** (`Tasks/EstTasks.cs`, Security; the dialog `EstSetupWindow` like the Applications
  dialog: the chosen camera's profiles through the read-only query `listProfiles`, Remove with an inline confirmation, Set
  up with the IEEE 802.1X choice): set up
  (AXIS OS 12.10+, cert v1 and cert-est v1 checked fresh; the camera's profiles `oadm_<profile>` with the device ID, the OADM
  CA chain, this server as EST server; waits for ENROLLED; pins a new HTTPS certificate) or remove the chosen profiles (any
  profile; payload `SetupPayload.Action`). At a site the camera gets the node's address; `EstService.ServeSiteAsync`
  stores it (setting `sites`) and opens the site's relay (`ISites.OpenTcpRelayAsync`), whose connections get the site's own
  server certificate. `Device/CameraEstApi.cs`: the camera's REST calls.
- Client part (`Oadm.Plugins.Est.Client.dll`): General (listen on, port, enrollments kept) and Profiles (name, use, days,
  key, on; at most one per use), Save with the status left of it; Enrollments (live, search, Delete all); Not renewed (only while there are
  any). No Revoke and no list of every certificate (docs/spec/est-server.md).
- Tests: `tests/Oadm.Plugins.Est.Tests` (protocol, device ID, TLS end to end over in-memory connections with a test device
  ID hierarchy and a fake CA, the page with screenshots).
