# 802.1X server (OADM PRO, planned)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-23; in work: the protocol layer is built ("Code (protocol)"), the rest follows the stages at the end.

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
    **Minimum TLS version** (one select: TLS 1.0, TLS 1.1, **TLS 1.2** (default), TLS 1.3; for 1.0 or 1.1 the hint below
    the field says "Only for old devices: TLS 1.0 and 1.1 are no longer considered secure."), **Cipher suites** (check
    boxes, the secure default set checked, **Reset to default**: TLS 1.3 suites and TLS 1.2 ECDHE suites with AES-GCM or
    ChaCha20-Poly1305; with a minimum of 1.0 or 1.1 the list also shows the old ECDHE suites with AES-CBC and SHA-1, which
    those versions need, checked when the minimum is lowered; never RC4, 3DES, NULL, export or RSA key exchange; at least
    one usable suite for the chosen versions must stay checked), **Largest fragment** (bytes, 500..1400, default 1000; the switch's `Framed-MTU` makes it smaller). The
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
    certificate** (the client certificate of the row, the shared `ui:CertificateWindow`, built with stage 1), **Delete
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
  TLS 1.2 and 1.3 by default; TLS 1.0 and 1.1 only when Advanced lowers the minimum (RFC 8996 deprecates them; SSL never).
- **Fragmentation** (RFC 5216 2.1.5), both directions: the first fragment carries the L flag and the total length, M on
  every fragment but the last, each fragment acknowledged by an empty EAP-TLS message. Fragment size = **Largest
  fragment** (Advanced), at most the switch's `Framed-MTU` minus the EAP and RADIUS overhead. Incoming total at most 64 KB (refused above, "Message
  too large"), at most 60 round trips per session.
- **Keys:** the MSK from the TLS key material (TLS 1.2: PRF "client EAP encryption"; TLS 1.3: exporter
  "EXPORTER_EAP_TLS_Key_Material"), sent as MS-MPPE-Recv-Key / MS-MPPE-Send-Key (RFC 2548) in the Access-Accept, encrypted
  with the shared secret.
- **VLAN** (RFC 3580): Tunnel-Type = VLAN (13), Tunnel-Medium-Type = 802 (6), Tunnel-Private-Group-ID = the VLAN ID.
- **TLS library** (see Libraries): BouncyCastle TLS in its **non-blocking mode**: one `TlsServerProtocol` per
  session without a stream; the bytes of each incoming EAP-TLS message go in with `OfferInput`, the bytes to send come
  out with `ReadOutput` and are cut into fragments; the handshake lives in memory only, no socket. `TlsServer` overrides
  give the minimum version and the cipher suites of Advanced, `NotifyAlertReceived` / `NotifyAlertRaised` the alert
  numbers in both directions, `TlsContext.ExportKeyingMaterial` the MSK. `SslStream` offers neither the key export nor the
  alert numbers on all three systems. Stage 1 starts with a proof: a full EAP-TLS exchange with a BouncyCastle client in a
  unit test.
- **Server certificate:** issued by the OADM CA (PKI) for the server, names = the listen addresses and the server's host
  name, renewed automatically 30 days before it expires; the cameras trust it through the PKI task "IEEE 802.1X".

## Libraries (research 2026-10-23)

Nothing is written from scratch that a maintained library or the SDK already does well:

| Part | Use | Why |
|---|---|---|
| UDP socket, firewall, Listen on | the SDK: `ctx.Network.ListenUdpAsync` (`IUdpRelay`), like NTP and DHCP | plugins may not open sockets (banned APIs); the host binds, opens the firewall and later relays sites the same way |
| TLS handshake in memory | **BouncyCastle** (`BouncyCastle.Cryptography` 2.7.0, already shipped): `TlsServerProtocol()` non-blocking, `OfferInput` / `ReadOutput` / `GetAvailableOutputBytes`, `TlsServer.GetSupportedVersions` / `GetSupportedCipherSuites`, `NotifyClientCertificate`, `NotifyAlertRaised` / `NotifyAlertReceived`, `TlsContext.ExportKeyingMaterial` (all present in 2.7.0) | the only .NET TLS stack with the key export and alert numbers on all three systems |
| RADIUS packets | **own codec** (decision 2026-10-23), from RFC 2865 / 2869 / 3579 / 5080 / 8044 | the whole stack under our control for the session state, the log and the strict parsing rules; packets keep every attribute in order with its raw bytes (Proxy-State, fixtures, View details), malformed packets are a result value, never an exception. `Flexinets.Radius.Core` (MIT) was looked at and not taken |
| EAP and EAP-TLS framing | own code (RFC 3748, 5216, 9190) | no .NET library exists; the format is a few header bytes, the logic is the session state |
| MS-MPPE key encryption | own code (RFC 2548 2.4.2) with the RFC's construction and test vectors | a few lines; not in the RADIUS library |
| Certificate checks | .NET `X509Chain` and the existing PKI / EST checks (OADM CA, trusted roots, Axis device ID roots, `axis-<serial>.local`) | already tested in EST |
| MAC address parsing | own code | small, vendor formats listed below |

No RADIUS server with EAP-TLS exists for .NET (FreeRADIUS is C, ToughRADIUS Go; neither fits a self-contained
cross-platform app), so the RADIUS, EAP and EAP-TLS layers are ours; BouncyCastle carries the TLS and the SDK the sockets.

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

## Certificates and revocation (PKI)

Decision 2026-10-23: **PKI owns the list of issued certificates and the revocation list**; EST and the 802.1X server read
it. One list, so a revoked certificate can neither authenticate nor renew. Stage 1, built 2026-10-24 (code:
`plugins/Oadm.Plugins.Pki/README.md` "Issued certificates and revocation").

- **PKI page, tab "Certificates"** (fixed `ui:PageTab`s "Certificate authority" and "Certificates"; nothing is read
  before the tab is opened): every certificate the OADM CA issued, by a PKI task (Enable HTTPS, IEEE 802.1X, Renew) or by
  EST: device (its address; a device OADM no longer manages shows its serial with "(removed)"), use (HTTPS, IEEE 802.1X,
  MQTT, RTSPS), issued by (Task, EST), valid from, valid until, serial number, state chip (Valid, Expires soon (the
  settings' warning days, 30 by default), Expired, Replaced (a newer one of the same device and use exists), Revoked
  (tooltip: date, reason, user)). Filters (state, use, issued by) and search (device address, host name, device serial,
  certificate serial in any notation); newest first, 500 rows per page from the server (Previous / Next, "1-500 of
  12,345"), virtualized grid, `RangeObservableCollection`, overlay on the first read and a progress row on later ones;
  **View certificate** (toolbar, row menu, double click: the shared `ui:CertificateWindow`, a revoked one says "Certificate
  revoked on ..."). An event `certificates` (at most one per second) makes every open tab read its page again.
- **Revoke** (red toolbar button and row menu; one or several rows, at most 5,000 at once; the confirmation
  `ui:MessageWindow` with the choice **Reason**: Key compromised, Device replaced, Device removed, Other; red Revoke):
  the serials go into the revocation list with time, reason and user; **Revoke all of this device** (row menu, last):
  every certificate of the row's device that is still valid. Expired and unknown serials are skipped and counted, an
  existing revocation is never changed. No undo (the device gets a new certificate by Enable IEEE 802.1X or Certificate
  enrollment). Methods `revoke` and `revokeDevice`: Admin with "Manage the certificate authority", `RequiresAllDevices`,
  audited by the host (Admin methods), and logged with the count, reason and user.
- **Device groups:** a user with a group sees the tab with the certificates of the group's devices only (none of removed
  devices); View certificate of another device's certificate answers like an unknown one ("This certificate is not in
  the list."); Revoke is hidden (and refused by the host).
- **Kept** (plugin records, one row each, only changed rows written, new certificates batched every 2 s, a revocation
  written before the call returns): `issued` (serial, device id, device serial from the `axis-<serial>.local` name, use,
  source, CA id, valid from, valid until, issued, alias), `issuedDer` (the DER, read one at a time) and `revoked`
  (serial, time, reason, user, device, valid until). A certificate leaves the list a year after it ended; a revocation
  when its certificate ended (it can no longer be used). Checked at the start and every hour. The plugin setting `issued`
  of OADM 4.1 moves into the records at the first start and is deleted (those entries have no DER: View certificate says
  "OADM keeps no copy of this certificate."). A certificate a task deletes from the device stays listed (as Replaced):
  a copy could still be in use.
- **Existing certificates** (decision 2026-10-24, the cheapest robust way without a new hook in the host's full refresh):
  (1) whenever PKI reads a device's certificate list (View / Delete certificates, Renew certificates), every certificate
  there that one of the OADM CAs (active or previous) signed (checked by a chain build to that CA) and that names this
  device's `axis-<serial>.local` is added once (use: IEEE 802.1X for clientAuth, else HTTPS; issued by EST for the
  `oadm_` / `oadm2_` EST profile names, else Task; issued = its valid-from); (2) once per server, 5 minutes after the
  start, one background pass reads the certificate list (REST `cert` v1, read only) of every device that answers and has
  AXIS OS 11.11 or later, 4 at a time (plugin setting `existingImported` when it ran to its end; a stop runs it again at
  the next start). Devices that did not answer come in by (1) or when renewed.
- **EST:** `simpleenroll` and `simplereenroll` with a revoked client certificate are refused (403, "Certificate revoked
  on 2026-11-02 (Key compromised)"), listed in Enrollments; the camera's Axis device ID is never revoked, so Set up still
  works. A revoked certificate is never flagged "Not renewed".
- **802.1X server:** a revoked client certificate fails "Certificate revoked on 2026-11-02 (Key compromised)"
  (`CertificateRevocation.Describe()`); the check is a hash lookup of the serial (`IDeviceCertificateAuthority.IsRevoked`:
  an immutable set in memory, replaced when PKI changes it).
- **SDK 0.4.0** (`src/Oadm.Sdk/Certificates`), default members of `IDeviceCertificateAuthority` (the offer of PKI):
  `IsRevoked(serial)`, `FindRevocation(serial)`, `Revocations`, event `RevocationChanged` (subscribe again when `Find`
  returns another instance), `ListIssuedAsync(IssuedCertificateQuery)` (filters, search, `DeviceIds`, paging; rows
  `IssuedCertificateInfo` with `IssuedCertificateState` and the revocation), `GetIssuedCertificateAsync(serial)` (DER),
  `RevokeAsync(serials, RevocationReason, userName)` (`RevokeResult`); `DeviceCertificateRequest.Purpose`
  (`DeviceCertificatePurposes`: https, dot1x, mqtt, rtsps); `IssuedCertificateSources` (Task, EST); `CertificateSerials.Normalize`
  (any notation: upper hex, no separators, no leading zeros; .NET keeps a leading zero byte, BouncyCastle drops it);
  `CertificateDescriber` / `CertificateDescription` for the shared certificate window.
- **802.1X page, tab "Revoked":** the revoked certificates (from PKI's list) and **Revoke**, which opens the PKI
  certificate list as a picker (search, filters) and revokes the chosen ones. A second door to the same list.
- **Later:** PKI publishes a CRL (and maybe OCSP) for RADIUS servers of other vendors.

## Who passes

- The client certificate: a chain to the OADM CA or a root of the PKI trusted root store, valid now, extended key usage
  client authentication (or none), and it names **exactly one managed device**: the DNS name `axis-<serial>.local` the
  OADM CA puts into every device certificate, or the serial of the factory **Axis device ID** (checked against the Axis
  device ID roots EST ships). At a site: a device of that site.
- Not revoked (PKI's revocation list, above); removing a device from OADM refuses it at its next authentication.
- The EAP identity (sent before TLS, unprotected) is shown and logged, never trusted.

## Limits and load

- 30,000 devices may authenticate after a power cut: TLS handshakes run with bounded parallelism (64 at a time, the rest
  wait in a queue; the switch retransmits and gets the cached or next answer), the session table and the log are O(1) per
  change, pushes to the page go through one batched event per 500 ms.
- Per switch at most 1,000 open sessions; a session costs at most 64 KB.

## Sites

Stage 4. A switch at a site sends its requests to the **site node**, which relays the datagrams to the server over its
outgoing connection (`ctx.Sites.OpenUdpRelayAsync(site, 1812)`, the relay NTP uses; no new SDK). The RADIUS, EAP and TLS
logic runs only on the server; the node never sees a secret or a key.

- **Switch rows get a Site** ("This server" or a site). A request is matched by site + the switch address as the node
  saw it, so the same private address at two sites is two switches. The switch is configured with the **node's address**
  as its RADIUS server; the switch dialog shows it ("RADIUS server for this switch: 10.1.0.5, port 1812").
- **The relay opens** for a site when its first switch is added and closes with its last; the node app asks to allow
  UDP 1812 like NTP's port; the page shows sites whose node has not allowed it yet (like EST).
- **Who passes at a site:** the device the certificate names must be a device of that site (as "Who passes" says).
- **Node offline:** the switch gets no answer and applies its own fallback (switch configuration, not OADM); the switch
  row shows "Site offline". Sessions of that site end as Timed out.
- **Round trips:** EAP-TLS needs about 6..12 of them; each adds the node's latency. The defaults (30 s per message, 60 s
  per session) leave room; switches usually wait 5 s per request with retries, which the duplicate cache answers.

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

## Code (protocol)

`plugins/Oadm.Plugins.Radius/Protocol/` (namespace `Oadm.Plugins.Radius.Protocol.*`): the protocol without sockets, host or
UI; the server part feeds it datagrams. Tests: `tests/Oadm.Plugins.Radius.Tests`.

- **Datagram flow** (`Sessions/RadiusProcessor.Process(datagram, RadiusClient, IPEndPoint source)`, thread-safe, never
  throws): `RadiusPacket.Parse` (framing rules above) → only Access-Request and Status-Server → the Message-Authenticator
  (`RadiusAuthenticators.VerifyRequest`, before anything else; missing or wrong = drop `WrongSecret`) → Status-Server
  answered (Access-Accept, not cached) → `AccessRequest.Read` (attribute rules) → `RadiusDuplicateCache` (key: switch,
  source address and port, Identifier; same authenticator = cached answer or drop while in work; a new authenticator
  replaces the entry) → the session of the State attribute or a new one → the answer by `RadiusPacketWriter.Response`
  (Message-Authenticator first, Response Authenticator, Proxy-State last in order). The result: `Answered` /
  `AnsweredFromCache` with the bytes to send to the source, or `Dropped` with a `RadiusDropReason` (Malformed,
  NotARequest, WrongSecret, InProgress, StaleEapResponse, AnswerTooLarge, InternalError) and a short detail for the debug
  log; the session snapshot after the step and the reason of an Access-Reject.
- **The switch** is a `RadiusClient(Key, Secret)` the server part finds by the source address (site + address at a site);
  the processor never looks at NAS-Identifier or NAS-IP-Address for that. `EndSessions(key)` ends a removed switch's
  sessions (Abandoned, "The switch was removed") and forgets its cached answers.
- **Settings**: `RadiusProtocolOptions` (record, replaced as a whole through `Options` while running): `Tls`
  (`TlsSettings`: `MinimumVersion`, `CipherSuites` of `TlsCipherSuites`, `Validate()` for the page, `DefaultFor(min)` for
  Reset to default), `LargestFragment` (500..1400), `ServerIdentity` (`TlsServerIdentity.Create(chain, pkcs8)` or
  `FromCertificate(x509, issuers)`, read when a handshake starts, so a renewal applies at once), the timeouts (30 s / 60 s),
  `MaxRoundTrips` 60, `MaxMessageLength` 64 KB, `MaxSessions` 10,000, `MaxSessionsPerClient` 1,000.
- **Who passes**: `IClientCertificateValidator.Validate(ClientCertificateCheck)` (switch key, session id, the chain DER leaf
  first, TLS version, EAP identity and MAC shown only) returns `ClientCertificateVerdict.Accept(vlanId, warning, tag)` or
  `Refuse(reason, alert)`. Called synchronously inside the TLS handshake (in-memory lookups only); an exception refuses
  (alert 80). The refusal's reason is the session's reason; its alert goes to the device.
- **Sessions** (`Sessions/EapTlsSession`, internal): key switch + State (16 random bytes, the same in every
  Access-Challenge); a new session (no State) for the same switch + port + MAC abandons the old one (several MACs on one
  port stay separate). `SessionChanged` (event, one snapshot per state change, raised under the session's lock) and
  `ExpireSessions()` (call every second) give the server part the Sessions tab and the log; `EapSessionSnapshot` (states,
  fragment n of m, reason, alert, port, port type, MAC, EAP identity, NAS identity, TLS version and suite, client
  certificates, VLAN, warning, tag; never keys) and `EapSessionTexts` (the plain words and the refusal texts).
- **When each state shows**: Receiving client hello = the device's fragments before TLS started; Sending server
  certificate = OADM's last (or only) fragment of its flight is out; Waiting for acknowledgement = an earlier fragment is
  out; Receiving client certificate = the device's fragments after that; Checking client certificate = the validator runs;
  Waiting for client finished = the certificate passed and TLS needs more of the device's flight (it came in parts);
  Sending result = OADM's last flight (TLS 1.2 Finished, TLS 1.3 commitment) or its refusal alert is out.
- **First message**: EAP-Start (empty EAP-Message) → Identity request; Identity → EAP-TLS Start; a NAK without EAP-TLS fails;
  anything else the switch started → Identity request (RFC 3579 2.1, Appendix A). An EAP-Request from the switch: Access-
  Reject with EAP-Response/Nak (RFC 3579 2.6.2). An unreadable EAP packet: Access-Reject with EAP-Failure (RFC 3579 2.2).
- **EAP Identifier** (RFC 3748 4.1): a response with the Identifier of the request before is a retransmission (the
  outstanding request again); any other Identifier is dropped. EAP-Success and EAP-Failure carry the response's Identifier.
- **Fragments**: OADM's TLS data per EAP-TLS message = min(Largest fragment, Framed-MTU − 4 − 10), an 802.11 port at most
  1,496 EAP octets (RFC 3580 3.10); L and the total only on the first fragment, never on an unfragmented message (RFC 9190
  2.1.9). The device's fragments: L required on the first of a fragmented message (at most 64 KB), a repeated L only with
  the same value, the fragments add up exactly; else "Message could not be read".
- **Results**: an alert OADM raises is sent in an EAP-TLS request and the session waits for the device's answer before
  EAP-Failure ("Sending result" with the reason; a timeout then ends Failed with that reason); an alert from the device
  ends at once with EAP-Failure. TLS 1.3: the commitment message (application data 0x00) after the handshake.
- **BouncyCastle** (2.7.0, compiled against, the host's copy at run time): `TlsServerProtocol()` without stream;
  `OfferInput` / `ReadOutput`; `EapTlsServer : DefaultTlsServer` gives versions, suites (only those of the server key:
  ECDSA or RSA), the certificate request, the credentials (signature scheme from the client's list) and records the
  alerts. BouncyCastle exports keying material only inside `NotifyHandshakeComplete`, so the MSK is derived there.
- **Keys and attributes**: `MppeKeys` (MS-MPPE-Recv-Key = MSK 0..31, Send-Key = MSK 32..63, RFC 2548 2.4.2 with two
  distinct salts), `VlanAttributes` (tag 0), the Access-Accept also echoes the request's User-Name (RFC 3579 3).
- **Text**: `MacAddress.Parse` (also HPE `accc8e-123456`; an EUI-64 or extra digits are not a suffix), `RadiusText`
  (UTF-8, control and format characters removed), `NasPortTypes`, `TlsAlertTexts` (the table above; the OADM-sent form of
  a certificate alert says "Client certificate …").
- **RFC vectors**: RFC 2865 7 and RFC 5997 6 (Message-Authenticator). The second request of RFC 2865 7.3 is printed with
  a State length of 0x10 for 8 octets and is malformed as printed (a test shows the parser drops it).

## Stages

1. **Built 2026-10-24:** PKI: the list of issued certificates and the revocation list (Certificates tab, Revoke, the SDK
   members), device groups filtering of the Certificates tab; EST refuses revoked certificates.
2. RADIUS and EAP-TLS core (own RADIUS codec, BouncyCastle TLS proof first), fragmentation, the server certificate,
   switches (changed while running), Sessions and Log, the TLS alert texts, Advanced, the Revoked tab.
3. VLAN rules (off by default), event rules, device groups filtering (the PKI Certificates tab was filtered in stage 1).
4. Switches at sites through the site node.

The switches, VLAN rules, settings, the issued certificates and the revocation list are part of the encrypted backup
(plugin settings and records, `docs/spec/settings-backup.md`); a restore brings back the revocation list with the CA.
