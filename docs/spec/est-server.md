# EST server (OADM PRO)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-22; stages 1 to 4 built (stages at the end). Code details: `plugins/Oadm.Plugins.Est/README.md`.

The first OADM PRO plugin (`oadm.est`). OADM runs an EST server (RFC 7030) and sets up the cameras' own EST client, so
every camera gets its certificates (HTTPS, IEEE 802.1X, MQTT, RTSPS) from the OADM CA and **renews them by itself**
also at sites (through a relay of the site node). A clean-room implementation from RFC 7030 and the Axis camera APIs.

## What the user sees

- **Plugins page:** "EST server" is listed for everyone with the chip **OADM PRO** (`plugin.json` `"pro": true`): without
  an active OADM PRO license or trial the check box is locked, the server refuses to turn it on
  (`ProLicenseTexts.NeedsPro` "Needs an OADM PRO license or trial.") and **Activate** opens Settings > License. When the
  license ends or is removed, the host stops it: it stops serving, enrolled cameras keep their certificates until they
  expire, renewals are refused while it is off. The same lock for every later PRO plugin (`docs/spec/plugins.md`).
- **Page "EST server"** (rail, group Security, next to PKI):
  - **General** left (Listen on, port, enrollments kept), **Profiles** right; the status left of Save: "Running on port 9454", "OADM PRO is not
    active.", "The PKI has no certificate authority.", or the listener's state ("... is used by another program").
  - **Profiles** (at most 2 per camera, the camera's limit; a use belongs to one profile): name, use (HTTPS, IEEE 802.1X,
    MQTT, RTSPS), validity (days), key (RSA 2048+ / ECC P-256+). Two ready-made: **HTTPS** (server certificates) and
    **IEEE 802.1X** (client certificates).
  - **Enrollments**: time, device, profile, new or renewal, result, reason (500 newest, setting 100..5,000). Search in
    the page (device, profile, kind, result, reason; one pass and one grid reset over at most 5,000 rows, new rows
    under the search as they come); **Delete all** (red toolbar button like the event log, confirmed, Admin, every
    client's list empties: topic `enrollmentsCleared`).
  - **Not renewed** (only while there are any): device, profile, valid until, state (Not renewed, Expired), 500 at most
    with "Showing 500 of ...". No list of every certificate: the certificates are on the cameras (PKI page, device
    details). No Revoke (decision 2026-10-22): without a revocation list it only stopped renewals, which Remove
    certificate enrollment and removing the device already do; real revocation (a CRL the RADIUS servers of IEEE 802.1X
    check) is a later feature of PKI.
- **Task "Set up certificate enrollment"** (device context menu, Security; one task per device): which profile serves
  which use; Nothing was changed on cameras that cannot do it. **Task "Remove certificate enrollment"**.
- **Event rules**: "Certificate enrolled", "Enrollment refused" (device, profile, reason), "Certificate not renewed"
  (device, profile, valid until).

## EST server

- Operations: `GET /.well-known/est/<profile>/cacerts`, `POST .../simpleenroll`, `POST .../simplereenroll`. No csrattrs,
  serverkeygen, fullcmc (404). Unknown or disabled profile: the same 404 everywhere (no probing).
- **Port TCP 9454** (setting; above 1024, so no rights needed on Linux servers and nodes). TLS 1.2+, own listener (not the
  gRPC Kestrel): `ctx.Network.ListenTcpAsync` on the server's own network (every address, or with **Listen on** one interface: the
  overload with its addresses, no IPv6 link-local; the set-up gives local cameras that interface's address of their
  family, else "has no IPv4 address for this camera. Nothing was changed."; a gone interface: status "Ethernet 2 is not
  available", tried again every 30 s; the server certificate names that interface's addresses) and `ctx.Sites.OpenTcpRelayAsync(site, port)`
  per site the cameras of which are set up (the node app asks to allow the port, like NTP's). TLS and a small HTTP/1.1
  parser on the raw stream in the plugin (the SDK has no HTTPS helper on `IStreamListener` yet; built here, moved to the
  SDK when a second plugin needs it).
- **Who may enroll:** mutual TLS with the camera's factory **Axis device ID** certificate (IEEE 802.1AR, AXIS Edge Vault),
  checked against the two Axis device ID root CAs (RSA, ECC; valid to 2060) shipped with the plugin (the camera sends the
  intermediates). No passwords, nothing to hand out. **And** the serial in the device ID certificate must be a device of
  the device list (at a site: a device of that site): a genuine Axis camera OADM does not manage gets nothing.
- **Renewal** (`simplereenroll`): the client certificate must be one EST issued and still kept (the newest of its device
  and profile or the one before it) or the device ID, same device.
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
  or no valid identity, 403 not managed / other site / certificate of another camera, 400 request unreadable or refused, 404 unknown, 503 no
  OADM PRO or no CA (Retry-After 600).
- The server's TLS certificate: ECC P-256 key made in memory (never stored), 90 days, made again with a third left or
  when the addresses or the CA change.

## Certificate renewals and pinning

A camera that renews its HTTPS certificate by EST presents a new certificate every period. OADM accepts a new device
certificate **without** Certificate changed when an OADM CA issued it **directly** (the PKI's active or a previous CA,
`ITrustAnchors.SetRenewalIssuers`; never the roots above an imported CA, never another plugin's anchors), it is valid now,
has serverAuth and names the same device (`axis-<serial>.local`), and pins it again. Any other change still shows **Certificate changed**. Applies to all OADM-CA certificates
(also PKI's own HTTPS task).

## Renewals and Not renewed

How Axis cameras renew (Axis training material): **halfway through the validity** the camera asks for a renewal with
its current certificate (`simplereenroll`, it may reuse its key); when that fails it tries again **daily** until it
succeeds. 10.0.0.48 did not renew a certificate of 10 minutes within 30 minutes: the camera checks about daily, so
certificates of a few days or less are not useful.

The camera sends no event when a renewal fails (see "The camera's own EST errors"), so the server watches its own
records (`EstStore`, `EstService.CheckRenewalsAsync`):

- **Records**: per device and profile the newest certificate and the one before it (a camera whose answer got lost
  renews with the previous one); a renewal or a new set-up pushes out the older ones; at most 2 x devices x profiles.
  Ended more than 30 days ago: dropped; a device OADM no longer manages: forgotten by the check. Records of 4.1.0-rc.2
  (every certificate) are cut down when loaded.
- **Not renewed**: the newest certificate, **halfway through its lifetime plus `NotRenewedGrace` (2 days)** (short
  ones: plus a tenth of the lifetime; 1 year: day 184.5), without a newer one. Checked 1 minute after the start and
  then every hour; flagged once (`NotRenewedUtc`, persisted): the event "Certificate not renewed", a warning in the
  server log, the page reads its list again. A later renewal replaces the record and ends it.
- Never flagged (`RenewalStoppedUtc`): the cameras of "Remove certificate enrollment".

## Setting up a camera (the deploy task)

Built in stage 3 (`Tasks/EstTasks.cs`), tested on 10.0.0.48. Device context menu, Security: **Set up certificate
enrollment** (its dialog: the IEEE 802.1X choice below) and **Remove certificate enrollment** (a confirmation); the
profiles come from the EST server page. Devices at a site too (stage 4).

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
   device and profile ends the wait at once with its reason; after 2 minutes the profile's status says why:
   `UNCONNECTED` = the camera did not reach the server ("could not reach the EST server ... TCP <port>"), `CONNECTED` =
   it reached the server but got no certificate). Seen on 10.0.0.48: ENROLLED within about 3 s; the
   certificate is stored under the profile id (`oadm_https`); the camera also installs the CA from the answer by itself
   (alias = the CA's common name). With HTTPS on (`HttpsEnabled`): the web server switches to the new certificate a few
   seconds later and restarts (connections drop); the task pins it (`UpdateDeviceTlsAsync`, again for up to 60 s while
   the web server restarts; a warning when it does not happen). Only when OADM talks HTTPS to the camera: the task never
   changes how OADM connects.
6. Not built yet: removing unused certificates OADM put there (never device ID certificates, their CAs, factory or
   public CAs).

Both tasks need the task permission "Set up certificate enrollment" (`enroll`, not for operators by default). Remove
certificate enrollment removes only the profiles named `oadm_...`; the certificates stay until they end. No
rollback exists on the camera; the set-up is safe to run again (idempotent). Hardware test:
`tests/Oadm.Plugins.Est.Tests/EstHardwareWriteTests.cs` (`OADM_EST_HARDWARE_WRITE=1`, `OADM_EST_SERVER_ADDRESS`,
`OADM_DEV_CAMERAS`): a real EST server on this computer with a test CA, the real task on 10.0.0.48, then the camera back
as before (profile removed, previous web server certificate, test certificates deleted).

**The camera's own EST errors**: VAPIX documents an event `tnsaxis:CertificateManagement/EST/Error`, but 10.0.0.48
(AXIS OS 12.11.77) neither declares it nor sends it: probed 2026-10-22 with a profile whose server does not answer
(status stays `UNCONNECTED`) and with an EST server that refuses the camera (status `CONNECTED`); the event stream
carried only the audit log line of the profile change (`EstEventProbeTests`, HardwareWrite). So the set-up reads the
profile status instead, and OADM keeps no event stream open to the cameras for EST.

## License of the code

`plugins/Oadm.Plugins.Est*` carry their own `LICENSE` (source-available: the source can be read, using the plugin needs
an OADM PRO license; changing or removing the license check is not allowed). The rest of OADM stays Apache 2.0. The
THIRD-PARTY notices list the plugin's license.

## Stages

1. **Host and SDK** (built): `"pro": true` in plugin.json, the Plugins page lock and badge, turning PRO plugins off when
   the license ends (`docs/spec/plugins.md` "OADM PRO plugins"); plugin services, PKI offers `IDeviceCertificateAuthority`
   ("Plugin services"); accepting OADM-CA renewals without Certificate changed (`docs/spec/vapix.md`).
2. **EST server** (built): listener, TLS, HTTP/1.1, the three operations, device ID check (the Axis device ID CA bundles
   from axis.com, checksums checked), managed-device check, issuing through PKI, enrollments, events, the page; tests over TLS with a test device ID hierarchy. Recorded camera requests follow with stage 3.
3. **Deploy task** and remove task (built) with the camera's `cert` / `cert-est` APIs; fixtures from a real camera, the
   write test on 10.0.0.48 passed. Open: removing unused certificates.
4. **Sites** (built): `ISites.OpenTcpRelayAsync` per bound site with set-up cameras (`EstService.ServeSiteAsync`, called
   by the set-up task with the node's address towards the camera, `LocalAddressTowardsAsync` on the device's route); the
   addresses per site are stored (plugin setting `sites`) and form the site's own server certificate (picked by the
   connection's site: no SNI); a removed site's relay is released and its addresses dropped; a port change releases and
   reopens every relay; `plugin.json` `"siteSupport": "full"`, `sitePorts` TCP 9454 "EST server". The page lists the relays
   that do not run below Save ("EST server at <site>: waiting for approval in the OADM Site Node app"); the set-up's wait
   ends with that text when the relay does not run.
5. **Docs:** user docs page, plugin README, website PRO section.
