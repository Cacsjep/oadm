# EST server (OADM PRO)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-22; not built yet (stages at the end).

The first OADM PRO plugin (`oadm.est`). OADM runs an EST server (RFC 7030) and sets up the cameras' own EST client, so
every camera gets its certificates (HTTPS, IEEE 802.1X, MQTT, RTSPS) from the OADM CA and **renews them by itself**, also
at sites. A clean-room implementation from RFC 7030 and the Axis camera APIs.

## What the user sees

- **Plugins page:** "EST server" is listed for everyone with a **PRO** badge. The plugin's `plugin.json` has `"pro": true`:
  the host refuses to turn it on without an active OADM PRO license or trial ("Needs OADM PRO. Start a 30-day trial." with
  the link of Settings > License). When the license ends or is removed, the host turns it off: it stops serving, enrolled
  cameras keep their certificates until they expire, renewals are refused while it is off. The same lock for every later
  PRO plugin.
- **Page "EST server"** (rail, group Security, next to PKI):
  - Status next to Save: "Running on port 9454", "Port in use", ...; the sites it serves.
  - **Profiles** (at most 2 per camera, the camera's limit; a use belongs to one profile): name, use (HTTPS, IEEE 802.1X,
    MQTT, RTSPS), validity (days), key (RSA 2048+ / ECC P-256+). Two ready-made: **HTTPS** (server certificates) and
    **IEEE 802.1X** (client certificates).
  - **Enrollments**: time, device, profile, new or renewal, result, reason (500 newest, setting).
  - **Certificates** issued by EST: device, profile, valid until, **Revoke** (red; a revoked certificate is never renewed).
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

## Certificate renewals and pinning

A camera that renews its HTTPS certificate by EST presents a new certificate every period. OADM accepts a new device
certificate **without** Certificate changed when it chains to an OADM CA (PKI's trust anchors) and names the same device
(serial), and pins it again. Any other change still shows **Certificate changed**. Applies to all OADM-CA certificates
(also PKI's own HTTPS task).

## Setting up a camera (the deploy task)

Compatibility first (CLAUDE.md device safety): AXIS OS **12.10+**, the REST APIs `cert` v1 and `cert-est` v1 (checked
fresh before the first write), an Axis device ID certificate on the camera (found by its chain, not its alias). Others:
Nothing was changed.

1. Read the camera's EST profiles and certificates.
2. Upload the CA chain (EST server certificate's issuer and the profile's issuer) with `cert` v1, never deleting; reuse
   an alias with the same content; aliases at most 32 characters.
3. If a profile serves IEEE 802.1X: set the 802.1X CA trust (as PKI does) before the profile.
4. Same set as planned: nothing written. Otherwise remove the camera's EST profiles and write the new ones **one at a
   time** (writing a profile restarts the camera's EST client): `POST /config/rest/cert-est/v1/profiles` with `server` =
   `https://<address>:9454/.well-known/est/<profile>`, `caCertificates`, `certificate` = the device ID alias (RSA or ECC to
   match the profile; without it the camera answers 500), `services`, subject CN and SANs. The address: the server's
   address toward the camera, or at a site the node's (`LocalAddressTowardsAsync`).
5. Wait until the camera reports **ENROLLED** (poll, up to 2 minutes); with HTTPS: check it answers with the new
   certificate and pin it.
6. Remove only unused certificates OADM put there. Never device ID certificates, their CAs, factory or public CAs.

No rollback exists on the camera; the task is safe to run again (idempotent).

## License of the code

`plugins/Oadm.Plugins.Est*` carry their own `LICENSE` (source-available: the source can be read, using the plugin needs
an OADM PRO license; changing or removing the license check is not allowed). The rest of OADM stays Apache 2.0. The
THIRD-PARTY notices list the plugin's license.

## Stages

1. **Host and SDK:** `"pro": true` in plugin.json, the Plugins page lock and badge, turning PRO plugins off when the license
   ends; plugin services (PKI offers sign and chain); accepting OADM-CA renewals without Certificate changed.
2. **EST server:** listener, TLS with per-site certificates, HTTP/1.1, the three operations, device ID check, managed-device
   check, issuing through PKI, enrollments and certificates lists, revoke, events; tests with recorded camera requests.
3. **Deploy task** and remove task with the camera's `cert` / `cert-est` APIs; fixtures from a real camera (read-only
   first), the write tests only on the developer's explicit go.
4. **Sites:** TCP relay per site, the node's certificate, the node app's port request.
5. **Docs:** user docs page, plugin README, website PRO section.
