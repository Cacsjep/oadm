# EST server (OADM PRO)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-22; stages 1 to 3 built (stages at the end). Code details: `plugins/Oadm.Plugins.Est/README.md`.

The first OADM PRO plugin (`oadm.est`). OADM runs an EST server (RFC 7030) and sets up the cameras' own EST client, so
every camera gets its certificates (HTTPS, IEEE 802.1X, MQTT, RTSPS) from the OADM CA and **renews them by itself**
(at sites: stage 4, planned; until then `plugin.json` says `"siteSupport": "none"` and the tasks refuse devices at a site). A clean-room implementation from RFC 7030 and the Axis camera APIs.

## What the user sees

- **Plugins page:** "EST server" is listed for everyone with the chip **OADM PRO** (`plugin.json` `"pro": true`): without
  an active OADM PRO license or trial the check box is locked, the server refuses to turn it on
  (`ProLicenseTexts.NeedsPro` "Needs an OADM PRO license or trial.") and **Activate** opens Settings > License. When the
  license ends or is removed, the host stops it: it stops serving, enrolled cameras keep their certificates until they
  expire, renewals are refused while it is off. The same lock for every later PRO plugin (`docs/spec/plugins.md`).
- **Page "EST server"** (rail, group Security, next to PKI):
  - Port and enrollments kept left, the profiles right; the status left of Save: "Running on port 9454", "OADM PRO is not
    active.", "The PKI has no certificate authority.", or the listener's state ("... is used by another program").
  - **Profiles** (at most 2 per camera, the camera's limit; a use belongs to one profile): name, use (HTTPS, IEEE 802.1X,
    MQTT, RTSPS), validity (days), key (RSA 2048+ / ECC P-256+). Two ready-made: **HTTPS** (server certificates) and
    **IEEE 802.1X** (client certificates).
  - **Enrollments**: time, device, profile, new or renewal, result, reason (500 newest, setting).
  - **Certificates** issued by EST: device, profile, valid until, **Revoke** (red, confirmed). The camera keeps a revoked
    certificate until it ends, but gets no new one for that profile until it is set up again (the camera signs in with
    its device ID, so the device and profile are blocked, not the one certificate).
- **Task "Set up certificate enrollment"** (device context menu, Security; one task per device): which profile serves
  which use; Nothing was changed on cameras that cannot do it. **Task "Remove certificate enrollment"**.
- **Event rules**: "Certificate enrolled", "Enrollment refused" (device, profile, reason).

## EST server

- Operations: `GET /.well-known/est/<profile>/cacerts`, `POST .../simpleenroll`, `POST .../simplereenroll`. No csrattrs,
  serverkeygen, fullcmc (404). Unknown or disabled profile: the same 404 everywhere (no probing).
- **Port TCP 9454** (setting; above 1024, so no rights needed on Linux servers and nodes). TLS 1.2+, own listener (not the
  gRPC Kestrel): `ctx.Network.ListenTcpAsync` on the server's own network and `ctx.Sites.OpenTcpRelayAsync(site, port)`
  per site the cameras of which are set up (the node app asks to allow the port, like NTP's). TLS and a small HTTP/1.1
  parser on the raw stream in the plugin (the SDK has no HTTPS helper on `IStreamListener` yet; built here, moved to the
  SDK when a second plugin needs it).
- **Who may enroll:** mutual TLS with the camera's factory **Axis device ID** certificate (IEEE 802.1AR, AXIS Edge Vault),
  checked against the two Axis device ID root CAs (RSA, ECC; valid to 2060) shipped with the plugin (the camera sends the
  intermediates). No passwords, nothing to hand out. **And** the serial in the device ID certificate must be a device of
  the device list (at a site: a device of that site): a genuine Axis camera OADM does not manage gets nothing.
- **Renewal** (`simplereenroll`): the client certificate must be one EST issued (or the device ID), not revoked, same
  device.
- **Not from strangers:** a request without a valid identity (401) is not listed in Enrollments and logged at debug level
  only; a 401 or 403 closes the connection; at most 64 connections, 4 per address, 2 minutes each.
- **Names:** host names reported by the camera go into a certificate only when they are plain (letters, digits, `-`, dots;
  no `*`); EC keys only on P-256, P-384 or P-521.
- **Issuing:** the OADM CA of the PKI plugin signs (one CA for everything), through the SDK's plugin services: PKI offers
  "sign a device certificate" and "CA chain", EST uses them. Like PKI's `DeviceCertificateIssuer`, OADM sets subject and
  SANs from what it knows (address, host name, FQDN, `axis-<serial>.local`), never from the CSR; EKU from the profile; key
  policy RSA >= 2048 / EC >= 256; SHA-1/MD5 CSRs refused. The answer carries the leaf **and the chain** (Axis refuses a
  leaf only), PKCS#7 certs-only, base64 wrapped at 76 characters. Every certificate goes into PKI's issued registry.
- **The server's TLS certificate:** issued by the OADM CA, SANs = the server's addresses and host name; **one per site**
  with the node's addresses toward the cameras (cameras dial an IP, so no SNI: the plugin picks the certificate by the
  site the connection came through). Re-issued when the addresses change (node network cards, DHCP).
- Revocation: internal only (no CRL, no OCSP), permanent.
- Logs: one line per request (no keys, no CSR bodies).
- Limits: 64 connections at once, 20 requests and 30 s idle per connection, headers 16 KB, body 64 KB. Answers: 401 no
  or no valid identity, 403 not managed / other site / revoked, 400 request unreadable or refused, 404 unknown, 503 no
  OADM PRO or no CA (Retry-After 600).
- The server's TLS certificate: ECC P-256 key made in memory (never stored), 90 days, made again with a third left or
  when the addresses or the CA change.

## Certificate renewals and pinning

A camera that renews its HTTPS certificate by EST presents a new certificate every period. OADM accepts a new device
certificate **without** Certificate changed when an OADM CA issued it **directly** (the PKI's active or a previous CA,
`ITrustAnchors.SetRenewalIssuers`; never the roots above an imported CA, never another plugin's anchors), it is valid now,
has serverAuth and names the same device (`axis-<serial>.local`), and pins it again. Any other change still shows **Certificate changed**. Applies to all OADM-CA certificates
(also PKI's own HTTPS task).

## Setting up a camera (the deploy task)

Built in stage 3 (`Tasks/EstTasks.cs`), tested on 10.0.0.48. Device context menu, Security: **Set up certificate
enrollment** and **Remove certificate enrollment**, each with a confirmation (no options: the profiles come from the EST
server page; the confirmation says that an IEEE 802.1X profile replaces the certificate for network access). Not for
devices at a site until stage 4.

Compatibility first (CLAUDE.md device safety): AXIS OS **12.10+** (`CanRun`), and before the first write a fresh
`config/discover` with `cert` v1 and `cert-est` v1, an Axis device ID certificate of this camera's serial on the camera
(found by its chain, not its alias), and at least one enabled profile whose service the camera has. Others: Nothing was
changed. Checked on 10.0.0.48 (P3265-V, 12.11.77): device ID certificates ECC P-256, RSA 2048 and RSA 4096, subject
`serialNumber` = the serial in lower case, issued by "Axis device ID Intermediate CA ECC 2" / "RSA 2" (in the shipped
bundles), valid to 9999; the camera starts with no EST profiles.

1. Check compatibility; the profiles: the enabled ones of the EST server whose service the camera has, at most 2.
   `supportedServices` lists only the services no profile uses yet, so the services of the camera's profiles count too.
2. Read the camera's certificates: the device ID of the profile's key (ECC or RSA, the smallest key).
3. Install the CA certificates: the OADM CA chain with `cert` v1, matched by fingerprint (an existing alias is reused),
   new ones as "OADM CA <8 hex>" like PKI's; never deleting.
   With IEEE 802.1X on (`IDeviceInfo.Dot1xEnabled`) the IEEE 802.1X profile is skipped with a warning (no lock-out),
   unless the payload says `replaceDot1x` (`SetupPayload`): the dialog (`EstSetupWindow`) shows, only when selected
   cameras have IEEE 802.1X on, the check box "Also replace the network access certificate of N cameras with IEEE 802.1X
   on" with the hint that the RADIUS server must trust the OADM CA (off by default); the task then logs that it was
   chosen. HTTPS
   comes last (its certificate restarts the web server); the reads while waiting are retried during that restart.
4. Same set as on the camera (id, server, CA aliases, device ID, services, SANs; `subject` is not compared): nothing is
   written. Otherwise remove OADM's profiles (`oadm_...`) and the camera's profiles that serve the same services (others
   stay), and add the new ones **one at a time**, each waited for:
   `POST /config/rest/cert-est/v1/profiles` with `id` = `oadm_<profile>` (the camera refuses `-` in ids, so profile names
   are letters, digits and `_`), `server` = `<address>:9454/.well-known/est/<profile>` (no scheme; the server's address
   toward the camera, `LocalAddressTowardsAsync`), `caCertificates` (the aliases of step 3), `certificate` = the device
   ID alias, `services` (`WEBSERVER` for HTTPS, `NETAUTH` for IEEE 802.1X, `MQTT`, `RTSPS`), `subject.cn` (the device's
   address) and `subjectAlternativeNames` (`IP:` the address, `DNS:` host name and `axis-<serial>.local`).
5. Wait until the camera reports **ENROLLED** (poll every 3 s, up to 2 minutes; a refusal the EST server recorded for this
   device and profile ends the wait at once with its reason). Seen on 10.0.0.48: ENROLLED within about 3 s; the
   certificate is stored under the profile id (`oadm_https`); the camera also installs the CA from the answer by itself
   (alias = the CA's common name). With HTTPS on (`HttpsEnabled`): the web server switches to the new certificate a few
   seconds later and restarts (connections drop); the task pins it (`UpdateDeviceTlsAsync`, again for up to 60 s while
   the web server restarts; a warning when it does not happen). Only when OADM talks HTTPS to the camera: the task never
   changes how OADM connects.
6. Not built yet: removing unused certificates OADM put there (never device ID certificates, their CAs, factory or
   public CAs).

Both tasks need the task permission "Set up certificate enrollment" (`enroll`, not for operators by default): setting up
again also lifts a revoke. Remove certificate enrollment removes only the profiles named `oadm_...`; the certificates stay until they end. No
rollback exists on the camera; the set-up is safe to run again (idempotent). Hardware test:
`tests/Oadm.Plugins.Est.Tests/EstHardwareWriteTests.cs` (`OADM_EST_HARDWARE_WRITE=1`, `OADM_EST_SERVER_ADDRESS`,
`OADM_DEV_CAMERAS`): a real EST server on this computer with a test CA, the real task on 10.0.0.48, then the camera back
as before (profile removed, previous web server certificate, test certificates deleted).

**The camera's own EST errors** (not built yet): the camera sends the event `tnsaxis:CertificateManagement/EST/Error` (profile, error code
0..7: startup, test connection, connection, enrollment, re-enrollment; internal or external, and a message such as
"SSL peer certificate ... was not OK") on its event stream. The plugin listens for it on the set-up cameras (the
device events connection of OADM, like Device triggers) and shows it in **Enrollments** as "Camera: <message>", so a
renewal that never reaches the server is visible too. Reference: VAPIX "Enrollment over secure transport API"
(`cert-est.v1`).

## License of the code

`plugins/Oadm.Plugins.Est*` carry their own `LICENSE` (source-available: the source can be read, using the plugin needs
an OADM PRO license; changing or removing the license check is not allowed). The rest of OADM stays Apache 2.0. The
THIRD-PARTY notices list the plugin's license.

## Stages

1. **Host and SDK** (built): `"pro": true` in plugin.json, the Plugins page lock and badge, turning PRO plugins off when
   the license ends (`docs/spec/plugins.md` "OADM PRO plugins"); plugin services, PKI offers `IDeviceCertificateAuthority`
   ("Plugin services"); accepting OADM-CA renewals without Certificate changed (`docs/spec/vapix.md`).
2. **EST server** (built): listener, TLS, HTTP/1.1, the three operations, device ID check (the Axis device ID CA bundles
   from axis.com, checksums checked), managed-device check, issuing through PKI, enrollments and certificates lists,
   revoke, events, the page; tests over TLS with a test device ID hierarchy. Recorded camera requests follow with stage 3.
3. **Deploy task** and remove task (built) with the camera's `cert` / `cert-est` APIs; fixtures from a real camera, the
   write test on 10.0.0.48 passed. Open: the camera's own EST error events in Enrollments, removing unused certificates.
4. **Sites:** TCP relay per site, the node's certificate, the node app's port request.
5. **Docs:** user docs page, plugin README, website PRO section.
