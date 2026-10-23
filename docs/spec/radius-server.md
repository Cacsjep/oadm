# 802.1X server (OADM PRO, planned)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-23, not built yet (stages at the end).

An OADM PRO plugin (`oadm.radius`, `"pro": true`, its own source-available license like EST). OADM answers the switches
of the network as their **RADIUS server** and authenticates the cameras on the switch ports with **EAP-TLS** (IEEE
802.1X). It completes the certificate suite: PKI issues the camera's client certificate and trusts the OADM CA (task
"IEEE 802.1X"), EST renews it, the 802.1X server checks it when the camera plugs in. A clean-room implementation from
the RFCs in `docs/rfc/` (verbatim copies; their README says which part each one covers).

## Decisions (2026-10-23)

- **OADM PRO**, locked without a license or trial like EST (`docs/spec/licensing.md`).
- **Only managed devices pass:** the client certificate must chain to the OADM CA (or a root of the PKI trusted root
  store) **and** name a device OADM manages. A genuine certificate of a device OADM does not manage is refused.
- **Result:** Access-Accept or Access-Reject; **VLAN assignment** per rule, **off by default**.
- **Switches change while the server runs:** adding, editing or removing a switch applies at once to the running
  server, without a restart and without dropping the sessions of the other switches. A removed switch's open sessions end
  (its next request is dropped as from an unknown switch).

## What the user sees

- **Plugins page:** "802.1X server" with the chip **OADM PRO**; off by default; without an active license the same lock
  and **Activate** as EST. When the license ends, the server stops answering (switches fall back to their own rule).
- **Page "802.1X server"** (rail, group Security, after EST server); subtitle: "Switches ask OADM which cameras may use
  their ports."
  - **General** (card, left): **Listen on** (all interfaces or one, like EST), **Port** (UDP 1812), **Log entries
    kept** (1,000..50,000, default 5,000). **Advanced** (collapsed, the defaults fit almost every network):
    **Minimum TLS version** (TLS 1.2 default, or TLS 1.3; 1.0 and 1.1 are never offered, RFC 8996), **Cipher suites**
    (check boxes, the secure default set checked, **Reset to default**; TLS 1.3 suites and TLS 1.2 ECDHE suites with
    AES-GCM or ChaCha20-Poly1305 only, nothing with CBC, RSA key exchange, RC4, 3DES or SHA-1 MACs; at least one must stay
    checked), **Largest fragment** (bytes, 500..1400, default 1000; the switch's `Framed-MTU` makes it smaller). The
    status left of Save: "Running on port 1812", "OADM PRO is not active.", "The PKI has no
    certificate authority.", "Port 1812 is used by another program", "Interface Ethernet 2 is not available".
  - **Switches** (card, right): name, address (IP or host name), state chip (Answered, No requests yet, Wrong secret),
    last request; **Add switch**, Edit, Delete (red). The dialog: Name, Address, **Shared secret** (`ui:PasswordBox`,
    16 characters or more, **Generate** fills a random one and shows it once to copy), validation below the fields.
    Changes apply to the running server at once (decision above). At most 500 switches.
  - **VLANs** (card, below; collapsed while off): **Assign VLANs** (off by default); rules in order, first match wins:
    a **site** or a **tag** → **VLAN ID** (1..4094); **Default VLAN** (optional; empty = no VLAN for devices no rule
    matches). Applies to the next authentication of each device.
  - **Sessions** (tab): the authentications in progress, live: started, switch, port, MAC address, EAP identity, device,
    **IP address** (below), **state** (below), duration. Virtualized; at most 10,000 open sessions.
  - **Log** (tab, like the EST enrollments): time, switch, port, MAC address, EAP identity, device, IP address, result (status chip
    Authenticated / Failed), reason, VLAN; **search** over every column (one pass, one grid reset), **View
    certificate** (the client certificate of the row, `EstCertificateWindow` moved to a shared control first), **Delete
    all** (red, confirmed, users with all devices). Kept entries per the setting, newest first.
- **Event rules:** "Device authenticated" (device, switch, port, VLAN), "Authentication failed" (device or unknown,
  switch, port, reason).
- **PKI task "IEEE 802.1X"** (exists): its dialog names this server ("Uses the OADM 802.1X server" when it is on) and the
  EAP identity it sets on the camera (`axis-<serial>`, the default).

## MAC address, device and IP address

- **MAC address** from the switch's `Calling-Station-Id` (RFC 3580), normalized to `AC:CC:8E:12:34:56` whatever the
  vendor's format (`AC-CC-8E-…`, `accc.8e12.3456`, …); empty when the switch sends none.
- **Device** before the certificate is checked: the managed device whose serial equals the MAC (an Axis serial is its
  MAC address), shown in italics as a guess; after the check: the device the certificate names. Only the certificate
  decides, never the MAC (it can be faked).
- **MAC does not match:** when the certificate names device A but the MAC is another device's or no managed device's,
  the session still passes, the log row gets the warning "MAC address AC:CC:8E:65:43:21 does not match the certificate
  of ACCC8E123456" (a copied certificate shows this way) and the event rule "Authentication failed" is not raised; a
  later setting may refuse it.
- **IP address:** the device's address from the device list (its last known address; at authentication time the port is
  not open yet, so the device may get or change its address afterwards, e.g. in a new VLAN). Empty for an unknown device.

## Session states

A session is one EAP conversation of one port (key: switch + the RADIUS `State` attribute OADM sends). Its state is
shown in plain words, as deep as the protocol allows:

1. **Waiting for identity**: the switch sent the first request.
2. **Starting TLS**: identity received, EAP-TLS Start sent.
3. **Receiving client hello**, fragment 1 of 2 when fragmented.
4. **Sending server certificate: fragment 2 of 4** (server hello, certificate, key exchange, request for the client
   certificate).
5. **Waiting for acknowledgement of fragment 2 of 4**.
6. **Receiving client certificate: fragment 3 of 5** (the total from the length the camera sends first).
7. **Checking client certificate**: chain, dates, use, the managed device.
8. **Waiting for client finished**.
9. **Sending result**.
10. **Authenticated** (with VLAN 20).
11. **Failed** with the reason, e.g. "The device does not trust the server certificate (TLS alert 48, unknown CA)",
    "Certificate of a device OADM does not manage (ACCC8E123456)", "Client certificate expired on 2026-09-30".
12. **Timed out** (no answer within 30 s of the last message, 60 s in all) or **Abandoned** (the switch started a new
    session for the port).

Ended sessions leave the Sessions tab after 10 s and are in the log.

## TLS alerts

Every failure caused by a TLS alert names the alert, its number and who sent it, from one table in the plugin
(`TlsAlertTexts`), never the raw enum name alone:

- Sent by the device (it refused OADM): "The device does not trust the server certificate (TLS alert 48, unknown CA)".
- Sent by OADM (it refused the device): "Client certificate not issued by a trusted CA (TLS alert 48, unknown CA)".

| Alert | Text (device sent / OADM sent) |
|---|---|
| 10 unexpected message | Unexpected message |
| 20 bad record MAC | Message damaged on the way |
| 40 handshake failure | No common security settings |
| 42 bad certificate | Certificate rejected |
| 43 unsupported certificate | Certificate type not supported |
| 44 certificate revoked | Certificate revoked |
| 45 certificate expired | Certificate expired |
| 46 certificate unknown | Certificate not accepted |
| 47 illegal parameter | Invalid value in the handshake |
| 48 unknown CA | Certificate not issued by a trusted CA |
| 49 access denied | Access denied |
| 50 decode error | Message could not be read |
| 51 decrypt error | Signature check failed |
| 70 protocol version | TLS version not supported |
| 71 insufficient security | Security settings too weak |
| 80 internal error | Internal error |
| 86 inappropriate fallback | Fallback refused |
| 90 user canceled | Canceled |
| 109 missing extension | Required extension missing |
| 110 unsupported extension | Extension not supported |
| 112 unrecognized name | Server name not known |
| 116 certificate required | No client certificate sent |
| other | "TLS alert <n>" |

The device-sent form starts with "The device …" where it reads better (48: "The device does not trust the server
certificate").

## Protocol

- **RADIUS** (RFC 2865) on UDP 1812 (setting), Access-Request / Access-Challenge / Access-Accept / Access-Reject;
  **EAP over RADIUS** (RFC 3579): EAP-Message split into 253-byte attributes, the `State` attribute ties the round trips.
  **Status-Server** (RFC 5997) answered. No accounting (1813) in the first version.
- **Message-Authenticator** (RFC 3579, BlastRADIUS hardening): required on every Access-Request (a request without it
  or with a wrong one is dropped and counted as "Wrong secret" on the switch), sent first in every answer.
- **Clients:** a request is answered only from a configured switch address (host names resolved when saved and every
  10 min); anything else is dropped (debug log, counted). The switch table is read for every request from an in-memory
  snapshot that a change replaces atomically (decision: switches change while the server runs).
- **Duplicates:** a retransmission (same switch, port, identifier, authenticator) gets the cached answer, never a second
  step. At most 4,096 cached answers, 30 s each.
- **EAP-TLS** (RFC 5216, TLS 1.2; RFC 9190, TLS 1.3 with the one-byte commitment message, no resumption, no tickets).
  TLS 1.2 and 1.3 by default (Minimum TLS version in Advanced); TLS 1.0/1.1 never.
- **Fragmentation** (RFC 5216 2.1.5), both directions: the first fragment carries the L flag and the total length, M on
  every fragment but the last, each fragment acknowledged by an empty EAP-TLS message. Fragment size = **Largest
  fragment** (Advanced), at most the switch's `Framed-MTU` minus the EAP and RADIUS overhead. Incoming total at most 64 KB (refused above, "Message
  too large"), at most 60 round trips per session.
- **Keys:** the MSK from the TLS key material (TLS 1.2: PRF "client EAP encryption"; TLS 1.3: exporter
  "EXPORTER_EAP_TLS_Key_Material"), sent as MS-MPPE-Recv-Key / MS-MPPE-Send-Key (RFC 2548) in the Access-Accept, encrypted
  with the shared secret.
- **VLAN** (RFC 3580): Tunnel-Type = VLAN (13), Tunnel-Medium-Type = 802 (6), Tunnel-Private-Group-ID = the VLAN ID.
- **TLS library:** BouncyCastle TLS (already a dependency) in its **non-blocking mode**: one `TlsServerProtocol` per
  session without a stream; the bytes of each incoming EAP-TLS message go in with `OfferInput`, the bytes to send come
  out with `ReadOutput` and are cut into fragments; the handshake lives in memory only, no socket. `TlsServer` overrides
  give the minimum version and the cipher suites of Advanced, `NotifyAlertReceived` / `NotifyAlertRaised` the alert
  numbers in both directions, `TlsContext.ExportKeyingMaterial` the MSK. `SslStream` offers neither the key export nor the
  alert numbers on all three systems. Stage 1 starts with a proof: a full EAP-TLS exchange with a BouncyCastle client in a
  unit test.
- **Server certificate:** issued by the OADM CA (PKI) for the server, names = the listen addresses and the server's host
  name, renewed automatically 30 days before it expires; the cameras trust it through the PKI task "IEEE 802.1X".

## Parsing and RFC compliance

Switches differ; the server assumes nothing a switch may leave out and is strict about what the RFCs require.

- **RADIUS packets (RFC 2865):** 20..4,096 bytes; the length field must equal the datagram (extra bytes after it are
  ignored as the RFC says, a shorter datagram is dropped); every attribute length 2..255 and within the packet; unknown
  attributes and vendor attributes (type 26) are skipped, never an error; a malformed packet is **silently dropped**
  (RFC 2865 3), logged at debug with the switch and the reason, counted on the switch row. Attributes may come in any
  order; repeated ones are kept in order.
- **Required in each Access-Request:** EAP-Message (else not ours: Access-Reject "Only EAP-TLS is supported", never a
  password check), Message-Authenticator (RFC 3579 3.2, checked before anything else). User-Name, NAS-IP-Address /
  NAS-IPv6-Address / NAS-Identifier, NAS-Port, NAS-Port-Id, NAS-Port-Type, Calling-Station-Id, Called-Station-Id,
  Framed-MTU and State are **optional**: every column that needs one shows it empty when missing.
- **Answers:** the Response Authenticator (RFC 2865 3), Message-Authenticator first; **Proxy-State** attributes copied
  unchanged and in order into the answer (RFC 2865 5.33); State sent in every Access-Challenge and checked on the next
  request (unknown State: Access-Reject, "Session not found or expired").
- **EAP (RFC 3748):** EAP-Message attributes concatenated in order (RFC 3579 3.1); the EAP length must match the joined
  bytes; Identifier tracked per session (a repeated Identifier is a retransmission); a NAK (type 3) to EAP-TLS ends the
  session: "The device does not support EAP-TLS (it asked for <method>)"; EAP types other than Identity, NAK and EAP-TLS
  (13) are refused.
- **EAP-TLS (RFC 5216 / 9190):** flags L, M, S checked; the L length only on the first fragment and at most 64 KB; the
  fragments must add up to it; an acknowledgement is an EAP-TLS message with no data; reserved flag bits ignored.
- **Port:** NAS-Port-Id (text, e.g. "GigabitEthernet1/0/5") when sent, else NAS-Port (number), else empty;
  NAS-Port-Type shown as Ethernet, Wireless and so on when sent.
- **MAC address** (Calling-Station-Id, RFC 3580 3.21 recommends `AC-CC-8E-12-34-56`): accepted with `-`, `:`, `.` or no
  separators, upper or lower case, in groups of 2 or 4 (`accc.8e12.3456`), with surrounding spaces or quotes; a suffix
  after the 12 digits (`…:SSID`, wireless) ignored; anything that is not 12 hex digits after that is shown as sent, never
  matched to a device.
- **Switch name and address:** the switch row is found by the packet's source address, never by NAS-Identifier or
  NAS-IP-Address (both are shown in the log, not trusted).
- **Text attributes** decoded as UTF-8; invalid bytes shown as replacement characters; control characters removed before
  they reach the UI or a log.
- **Fixtures:** recorded conversations of real switches (Cisco IOS / IOS-XE, HPE Aruba, Juniper, Netgear, Ubiquiti,
  Zyxel, MikroTik as available) under `tests/.../Fixtures/Radius`, each replayed in a test; new switch quirks become a
  fixture first.

## Who passes

- The client certificate: a chain to the OADM CA or a root of the PKI trusted root store, valid now, extended key usage
  client authentication (or none), and it names **exactly one managed device**: the DNS name `axis-<serial>.local` the
  OADM CA puts into every device certificate, or the serial of the factory **Axis device ID** (checked against the Axis
  device ID roots EST ships). At a site: a device of that site.
- No revocation list yet (a later feature of PKI); removing a device from OADM refuses it at its next authentication.
- The EAP identity (sent before TLS, unprotected) is shown and logged, never trusted.

## Limits and load

- 30,000 devices may authenticate after a power cut: TLS handshakes run with bounded parallelism (64 at a time, the rest
  wait in a queue; the switch retransmits and gets the cached or next answer), the session table and the log are O(1) per
  change, pushes to the page go through one batched event per 500 ms.
- Per switch at most 1,000 open sessions; a session costs at most 64 KB.

## Sites

The first version answers switches on the server's own network. Later stage: switches at a site reach OADM through the
site node (a UDP relay of the node, like the TCP relay of EST; the node app asks to allow UDP 1812).

## Device groups

A server-wide service: General, Switches and VLANs are changed only by users with all devices. Users with a group see the
page with the Sessions and Log rows of their devices; rows without a managed device only users with all devices.

## Secrets and logs

Shared secrets are stored encrypted with the master key (`docs/spec/security.md`), never logged, never sent to a client
after saving (Edit shows them empty, "Leave empty to keep the current secret"). Logs name the switch, port, MAC, device
and reason, never secrets or key material.

## Tests

- RADIUS packets and the authenticators from the RFC test vectors; Message-Authenticator required; duplicates;
  Proxy-State echoed; every optional attribute missing; every malformed case of "Parsing and RFC compliance" dropped.
- Fuzz tests of the RADIUS, EAP and EAP-TLS parsers (random and mutated packets from the fixtures): never an exception
  outside the parser, never a hang, never more memory than the limits.
- The recorded switch fixtures replayed; the MAC formats of every vendor normalized.
- Full EAP-TLS conversations with a BouncyCastle TLS client as the camera through an in-memory switch: TLS 1.2 and 1.3,
  fragment sizes 500..1400 and small `Framed-MTU`, every state of the table, every refusal (unknown CA, expired, an
  unmanaged device, another site), alerts in both directions with their texts, timeouts with a `FakeTimeProvider`.
- Switches added, changed and removed while sessions run (the decision); Advanced: minimum version 1.3 refuses a 1.2
  client with the alert text, an unchecked cipher suite is not offered.
- Perf (`Category=Perf`): 30,000 authentications, the session table and the log at their limits.
- Hardware (opt-in, `Category=HardwareWrite`, never automatic): a camera with an OADM CA certificate on a real switch.

## Stages

1. RADIUS and EAP-TLS core, fragmentation, the server certificate, switches (changed while running), Sessions and Log,
   the TLS alert texts.
2. VLAN rules (off by default), event rules, device groups filtering.
3. Switches at sites through the site node.
