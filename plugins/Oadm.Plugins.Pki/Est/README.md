# EST server (oadm.est)

A PRO part of the PKI plugin (`../README.md`, `docs/spec/pki.md` "One plugin"). Spec: `docs/spec/est-server.md`. License:
`LICENSE` in this folder (source-available, not Apache-2.0), also for `plugins/Oadm.Plugins.Pki.Client/Est`.

- Core plugin `oadm.est` of the PKI package, its page the tab **EST server** of the PKI page (`EstPage.TabOf`). The core
  plugin always runs; the server (`EstService`) runs only while it is turned on (`ServerSwitch`, plugin setting `serverOn`,
  off by default) and OADM PRO is active; every request checks the license again (503 "OADM PRO is not active."). Page
  methods `getServer`, `turnOn`, `turnOff` (`../Servers/ServerSwitch.cs`) besides `EstMethods`.
- Server part (in `Oadm.Plugins.Pki.Server.dll`, namespace `Oadm.Plugins.Est`):
  - `Serving/EstService`: listens on TCP 9454 (setting `config.port`, 1025 to 65535) on the server's network through
    `ICorePluginContext.Network.ListenTcpAsync`; TLS 1.2 or later (`SslStream`, client certificate requested, checked per
    request); at most 64 connections (`Serving/EstConnectionSlots`: 4 per address, IPv6 per /64, 16 without a client
    certificate with 10 s for the handshake), 20 requests and 30 s idle per connection.
  - `Serving/ServerCertificates`: the server's TLS certificate is the one PKI's EST server policy picks from the
    Certificates list (`docs/spec/pki.md` "Server certificates": by default the shared "802.1X and EST server", ECC P-256, 10
    years), loaded with its key and again when the policies change; one certificate for this server and every site, never
    renewed by itself; the TLS chain is its issuer's chain; `cacerts` and the answers carry the chain of the CA of the
    profile's issuing policy (`Ca/IssuerChains`, `DeviceCertificateRequest.IssuerId`).
  - `Serving/EstPolicies`: a profile's issuing policy (`EstProfile.PolicyId`; HTTPS certificate for HTTPS and RTSPS, IEEE
    802.1X certificate for IEEE 802.1X and MQTT): its valid days for enrollments and renewals, its key for Set up; none
    readable: 503 "The issuing policy of this profile cannot be read."; the profiles' uses PKI checks before deleting a
    policy (`ProfileUses`) and the profile names for the editor (`ProfileNames`); the conversions of the EST settings
    (`ConvertAsync`, `ConvertRulesAsync`, `CleanUpAsync`, `RemoveOffProfilesAsync`; owners in `docs/spec/pki.md` "Converting
    existing settings"). SAN of the shared server certificate = the server's addresses (network cards that are up) and host
    name; at a site the node's addresses the cameras were given.
  - `Serving/EstEndpoint`: `GET /.well-known/est/<profile>/cacerts` (the CA chain), `POST .../simpleenroll`,
    `POST .../simplereenroll`; everything else and unknown profiles 404. Who may enroll (`SerialOfAsync`): the camera's
    Axis device ID, or a valid certificate of a CA of an EST sign-in CA policy, or to renew its current certificate from
    OADM (whatever CA of PKI issued it), whose serial is a managed device of the same site. Answers: PKCS#7 certs-only,
    base64 at 76 characters, leaf and chain. Refusals: 401 (no or no valid identity), 403 (not managed, other site, no
    enrollment policy, certificate of another camera, a certificate PKI no longer accepts: `IDeviceCertificateAuthority.IsRevoked`,
    `Ca/NotAcceptedTexts`), 400 (request unreadable or refused by the CA), 404 (unknown or not allowed profile), 503 (no
    PRO, no CA). Every request names its use (`DeviceCertificateRequest.Purpose`) for PKI's list.
  - `Serving/EstRules`: PKI's EST policies, built from PKI's `PolicyStore` again when its version moves or PKI started again:
    EST sign-in CA policies name the CAs a camera may sign in with (`SignInCas(kind)`: for new certificates also renews, for
    renewals only `simplereenroll`; only those PKI still lists count; no store, an unreadable one or none: nothing signs in);
    Enrollment policies hold the devices and their profiles (`Check(device, profile)`: none holds it 403 "No enrollment
    policy for this device.", a profile not allowed the 404 of an unknown profile, listed with the policy).
    `EstState.PoliciesApply` and `PolicyLines` (the link "N policies applied" with its tooltip), `PolicyProblem` (null for
    group users) and the warning when a site's node address or one of this server's addresses is not in the server
    certificate. EST reports `SetUsedBy("oadm.est", "EST server", sign-in CAs ∩ listed + oadm)` at the start, on a policy
    change and on PKI's `Changed`, and clears it when it stops.
  - `AxisDeviceId` (`plugins/Shared/DeviceId`, linked, shared with the 802.1X server): the Axis device ID CAs (RSA and ECC
    bundles, roots and intermediates as Axis publishes them, embedded); the serial is the subject's X.520 serialNumber (12
    hex digits).
  - `Protocol/EstHttp`: HTTP/1.1 (16 KB headers, 64 KB body, Content-Length or chunked, `Expect: 100-continue`,
    keep-alive); `Protocol/EstMessages`: PKCS#7 certs-only and PKCS#10 in base64.
  - `Serving/EstStore`: plugin settings `config` (interface, port, profiles, enrollments kept 100 to 5,000; `signInWith` of
    4.1 is read only by the conversion and kept by a save), `enrollments` (newest first), `sites`; plugin records
    `certificates` (`ctx.Records`, one row per certificate: per device and profile the newest and the one before it;
    dropped 30 days after they end or when the device is gone; only changed rows are written) and `issued` (the DER of each
    listed enrollment); written at most every 2 s.
  - Signing: `IDeviceCertificateAuthority` of PKI (`ctx.Services`), names from OADM (CN = the device's address; SAN = its
    stored address, host name, `axis-<serial>.local`; never the address a connection came from), validity from the
    profile's issuing policy, source "EST".
  - Events: "Certificate enrolled" (`oadm.est.enrolled`: profile, kind) and "Enrollment refused" (`oadm.est.refused`:
    profile, reason), only for managed devices; "Certificate not renewed" (`oadm.est.notrenewed`: profile, validUntil) from
    the hourly `CheckRenewalsAsync` (newest certificate per device and profile, halfway through its lifetime plus
    `NotRenewedGrace`; removing an `oadm_` profile stops the check).
  - Methods: `getState`, `getEnrollments`, `getNotRenewed` (500 at most), `getCertificate` (the certificate of a listed
    enrollment, described by the SDK's `CertificateDescriber`, shown in the shared `ui:CertificateWindow`) for everyone;
    `save` and `clearEnrollments` Admin (permission `oadm.est:manage`).
  - Device groups (`minSdkVersion` 0.4.0): `InvokeAsync(ICallContext, ...)`; `save` and `clearEnrollments` are
    `RequiresAllDevices`; for a user with a group the reads are filtered by `ViewedSet` (`plugins/Shared/ViewerScopes.cs`,
    the ids and sites of `caller.Devices`): enrollments with a device of the view (none without a device), Not renewed and
    the certificate count of its devices, listeners of its sites (and of this server when it has a local device),
    `getCertificate` only for a listed enrollment of its devices (else `CertificateGone`). Events: `enrollments` with
    `PluginEventAudience.Device` (no device: `AllDevices`); `state`, `enrollmentsCleared` and `certificates` to
    `AllDevices` and to `DeviceScope(key)` of each group that read the page (`ViewerScopes`; the state per group with its
    own rows). Names (`EstEndpoint.CommonName`, `DnsNames`): host names only when `AddedByAllDevices`, the camera's reported
    host name only when no group covers the device (`EstService.InAnyGroup`, no scopes: covered). Background work (the
    listener, the renewal check) keeps `ctx.Devices`; the set-up task gets its device from the host.
- Task **EST - Certificate Enrollment** (`Tasks/EstTasks.cs`, Security; the dialog `EstSetupWindow` like the Applications
  dialog: the chosen camera's profiles through the read-only query `listProfiles`, Remove with an inline confirmation, Set up
  with the IEEE 802.1X choice; the camera clocks first through the query `readClock`, `CameraClock`, Yes sets
  `SetupPayload.ClockConfirmed`, logged): set up (AXIS OS 12.10 or later, cert v1 and cert-est v1 checked fresh; the camera's
  profiles `oadm_<profile>` or `oadm2_<profile>` with the device ID, the issuer chain of the EST server's certificate, this
  server as EST server; with the dot1x profile also the RADIUS server CA of the profile's IEEE 802.1X policy, installed and
  set, written with 802.1X off, then on; waits until the camera holds the new certificate; pins a new HTTPS certificate;
  deletes OADM's older certificates no use holds any more) or remove the chosen profiles (any profile; payload
  `SetupPayload.Action`). The camera never stores a certificate over an older one of the same name and keeps a removed
  profile's certificate bound to its use, so a changed profile or one set up again after Remove takes the other name (spec
  "Two names per profile"). At a site the camera gets the node's address; `EstService.ServeSiteAsync` stores it (setting
  `sites`) and opens the site's relay (`ISites.OpenTcpRelayAsync`); the relay's connections get the same server
  certificate. `Device/CameraEstApi.cs`: the camera's REST calls.
- Client part (in `Oadm.Plugins.Pki.Client.dll`, `Est/`, namespace `Oadm.Plugins.Est.Client`): while the server does not run
  only the off state (`Servers/ServerOffStateView`: Turn on, Start 30-day trial, the needs-PRO title); else Server (Listen
  on, Port, Enrollments kept; Save with the status and Turn off left of it, the "N policies applied" link) above Profiles
  (name, use, issuing policy; at most one per use; Add profile) in the left column, Enrollments right (live, search, View
  certificate, Delete all; `EstPage.HasOwnCards`, one column on a narrow page); Not renewed (only while there are any). No
  list of every certificate here: that is PKI's Certificates tab (`docs/spec/pki.md` "Certificates").
- Tests: `tests/Oadm.Plugins.Est.Tests` (protocol, device ID, TLS end to end over in-memory connections with a test device ID
  hierarchy and a fake CA, the page with screenshots; `ServerTests`, `PolicyTests`, `EnrollmentPolicyTests`, `TaskTests`,
  `PageTests`, `RenewalTests`, `ListenTests`, `ServerCertificateTrustTests`; `DeviceGroupTests`: the `PluginCanary`, the
  filtered lists and certificate, the events per group, the names of a device a group user added, the page for a group
  user; `EstHardwareWriteTests` and `EstEventProbeTests`, HardwareWrite).
