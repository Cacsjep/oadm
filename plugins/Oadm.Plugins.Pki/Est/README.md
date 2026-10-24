# EST server (oadm.est)

A PRO part of the PKI plugin (`../README.md`, `docs/spec/pki.md` "One plugin"; OADM 4.1 shipped it as its own plugin).
Spec: `docs/spec/est-server.md`. License: `LICENSE` in this folder (source-available, not Apache 2.0), also for
`plugins/Oadm.Plugins.Pki.Client/Est`.

- Core plugin `oadm.est` of the PKI package, rail page **EST server** (group Security, icon `lock`). The core plugin always
  runs; the server (`EstService`) runs only while it is turned on (`ServerSwitch`, plugin setting `serverOn`, off by
  default) and OADM PRO is active; every request checks the license again (503 "OADM PRO is not active.").
  Page methods `getServer`, `turnOn`, `turnOff` (`../Servers/ServerSwitch.cs`) besides `EstMethods`.
- Server part (in `Oadm.Plugins.Pki.Server.dll`, namespace `Oadm.Plugins.Est`):
  - `Serving/EstService`: listens on TCP 9454 (setting `config.port`, 1025..65535) on the server's network through
    `ICorePluginContext.Network.ListenTcpAsync`; TLS 1.2+ (`SslStream`, client certificate requested, checked per request);
    at most 64 connections, 20 requests and 30 s idle per connection.
  - `Serving/ServerCertificates`: the server's TLS certificate from the OADM CA (key made in memory with the days and key
    of PKI's Server certificate policy "EST server", 90 days ECC P-256 by default; made again 30 days, at most a third,
    before its end, when the addresses, the CA or the policy's key change);
  - `Serving/EstPolicies`: a profile's issuing policy (`EstProfile.PolicyId`; HTTPS certificate for HTTPS and RTSPS, IEEE
    802.1X certificate for IEEE 802.1X and MQTT): its valid days for enrollments and renewals, its key for Set up; none
    readable: 503 "The issuing policy of this profile cannot be read."; the once-only conversion of the profiles of 4.1
    (owner `oadm.est:profiles`) and the profiles' uses PKI checks before deleting a policy; SAN = the server's addresses (network cards that
    are up) and host name; at a site the node's addresses the cameras were given.
  - `Serving/EstEndpoint`: `GET /.well-known/est/<profile>/cacerts` (the CA chain), `POST .../simpleenroll`,
    `POST .../simplereenroll`; everything else and unknown or disabled profiles 404. Who may enroll: the camera's Axis
    device ID (or a valid certificate of the OADM CA for it, with or without a record) whose serial is a managed device of
    the same site. Answers: PKCS#7 certs-only, base64 at 76 characters, leaf and chain. Refusals: 401 (no or no valid
    identity), 403 (not managed, other site, certificate of another camera, a certificate on PKI's revocation list:
    `IDeviceCertificateAuthority.IsRevoked`, "Certificate revoked on ... (reason)"), 400 (request unreadable or refused by
    the CA), 503 (no PRO, no CA). Every request names its use (`DeviceCertificateRequest.Purpose`) for PKI's list.
  - `AxisDeviceId` (`plugins/Shared/DeviceId`, linked, shared with the 802.1X server): the Axis device ID CAs (RSA and ECC bundles, roots and intermediates as Axis publishes
    them, embedded); the serial is the subject's X.520 serialNumber (12 hex digits).
  - PKI's EST policies (docs/spec/pki.md "Code (stage 6)"; `Serving/EstRules`, built from PKI's `PolicyStore` again when
    its version moves): Sign-in CA policies name the CAs a camera may sign in with (`SignInCas(kind)`: for new
    certificates also renews, for renewals only `simplereenroll`; only those PKI still lists count; no store, an unreadable
    one or none: nothing signs in); Enrollment policies hold the devices and their profiles (`Check(device, profile)`: none
    holds it 403 "No enrollment policy for this device.", a profile not allowed the 404 of an unknown profile, listed with
    the policy). `EstState.PoliciesApply` ("3 policies apply", Edit in PKI) and `PolicyProblem` (null for group users);
    "Issued by" (the OADM CA by its name in PKI, else the name in its certificate). EST reports
    `SetUsedBy("oadm.est", "EST server", sign-in CAs ∩ listed + oadm)` at the start, on a policy change and on PKI's
    `Changed`, and clears it when it stops. `config.signInWith` ("Sign in with" of before) is read only by the conversion
    (`EstPolicies.ConvertRulesAsync`, owner `oadm.est`) and kept by a save; the store gets `ProfileNames` for the editor.
  - Set up reads the camera clocks first (`CameraClock`, query `readClock` of the Set up task; the run line shows the
    reading) and asks Yes / No when one is behind the certificate's start; Yes sets `SetupPayload.ClockConfirmed` (logged).
  - `Protocol/EstHttp`: HTTP/1.1 (16 KB headers, 64 KB body, Content-Length or chunked, `Expect: 100-continue`,
    keep-alive); `Protocol/EstMessages`: PKCS#7 certs-only and PKCS#10 in base64.
  - `Serving/EstStore`: plugin settings `config` (interface, port, profiles, enrollments kept 100..5000), `enrollments` (newest
    first), `sites`; plugin records `certificates` (`ctx.Records`, one row per certificate: per device and profile the
    newest and the one before it; dropped 30 days after they end or when the device is gone; only changed rows are
    written); written at most every 2 s.
  - Signing: `IDeviceCertificateAuthority` of PKI (`ctx.Services`), names from OADM (CN = the device's address; SAN =
    its stored address, host name, `axis-<serial>.local`; never the address a connection came from), validity from
    the profile, source "EST".
  - Events: "Certificate enrolled" (`oadm.est.enrolled`: profile, kind) and "Enrollment refused" (`oadm.est.refused`:
    profile, reason), only for managed devices; "Certificate not renewed" (`oadm.est.notrenewed`: profile, validUntil)
    from the hourly `CheckRenewalsAsync` (newest certificate per device and profile, halfway through its lifetime plus
    `NotRenewedGrace`: Axis cameras renew halfway and retry daily; removing an `oadm_` profile stops the check).
  - Methods: `getState`, `getEnrollments`, `getNotRenewed` (500 at most), `getCertificate` (the certificate of a listed
    enrollment, records collection `issued`, described by the SDK's `CertificateDescriber`, shown in the shared `ui:CertificateWindow`) for everyone; `save` and `clearEnrollments`
    Admin (permission `oadm.est:manage`).
  - Device groups (SDK 0.4.0, `minSdkVersion` 0.4.0): `InvokeAsync(ICallContext, ...)`; `save` and `clearEnrollments` are
    `RequiresAllDevices`; for a user with a group the reads are filtered by `ViewedSet` (`plugins/Shared/ViewerScopes.cs`,
    the ids and sites of `caller.Devices`): enrollments with a device of the view (none without a device), Not renewed
    and the certificate count of its devices, listeners of its sites (and of this server when it has a local device),
    `getCertificate` only for a listed enrollment of its devices (else `CertificateGone`). Events: `enrollments` with
    `PluginEventAudience.Device` (no device: `AllDevices`); `state`, `enrollmentsCleared` and `certificates` to
    `AllDevices` and to `DeviceScope(key)` of each group that read the page (`ViewerScopes`; the state per group with its
    own rows). Names (`EstEndpoint.CommonName`, `DnsNames`): host names only when `AddedByAllDevices`, the camera's
    reported host name only when no group covers the device (`EstService.InAnyGroup`, no scopes: covered). Background work
    (the listener, the renewal check) keeps `ctx.Devices`; the set-up task gets its device from the host.
- Task **Certificate enrollment** (`Tasks/EstTasks.cs`, Security; the dialog `EstSetupWindow` like the Applications
  dialog: the chosen camera's profiles through the read-only query `listProfiles`, Remove with an inline confirmation, Set
  up with the IEEE 802.1X choice): set up
  (AXIS OS 12.10+, cert v1 and cert-est v1 checked fresh; the camera's profiles `oadm_<profile>` or `oadm2_<profile>` with
  the device ID, the OADM CA chain, this server as EST server; waits until the camera holds the new certificate; pins a new
  HTTPS certificate; deletes OADM's older certificates no use holds any more). The camera never stores a certificate over
  an older one of the same name and keeps a removed profile's certificate bound to its use, so a changed profile or one
  set up again after Remove takes the other name (spec "Two names per profile") or remove the chosen profiles (any
  profile; payload `SetupPayload.Action`). At a site the camera gets the node's address; `EstService.ServeSiteAsync`
  stores it (setting `sites`) and opens the site's relay (`ISites.OpenTcpRelayAsync`), whose connections get the site's own
  server certificate. `Device/CameraEstApi.cs`: the camera's REST calls.
- Client part (in `Oadm.Plugins.Pki.Client.dll`, `Est/`, namespace `Oadm.Plugins.Est.Client`): while the server does not run
  only General with its status and Turn on (`Servers/ServerSwitchView`); else General (listen on, port, enrollments kept) and Profiles (name, use, days,
  key, on; at most one per use) in one card (side by side when wide, `WrapPanel`), Save with the status and Turn off left
  of it; Enrollments in its own card (live, search, Delete all; `EstPage.HasOwnCards`); Not renewed (only while there
  are any; a revoked certificate is never flagged). No Revoke and no list of every certificate here: both are PKI's (card
  Certificates, docs/spec/radius-server.md "Certificates and revocation").
- Tests: `tests/Oadm.Plugins.Est.Tests` (protocol, device ID, TLS end to end over in-memory connections with a test device
  ID hierarchy and a fake CA, the page with screenshots; `DeviceGroupTests`: the `PluginCanary`, the filtered lists and
  certificate, the events per group, the names of a device a group user added, the page for a group user).
