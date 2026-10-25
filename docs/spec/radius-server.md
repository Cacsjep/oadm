# 802.1X server (OADM PRO)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-23; one-page redesign, trusted CAs from PKI and policies agreed 2026-10-24; built 2026-10-24: the protocol
layer ("Code (protocol)") and the server part ("Code (server)"); the page is the client part. Since stage 3 of
`docs/spec/pki.md` (2026-10-24) every rule of the server is a policy of PKI: 802.1X server (TLS, packet size,
reauthentication), Allow CA, VLAN, Block and Revoke; the server keeps no rules of its own. Since decision 2026-10-25 the
kinds are 802.1X server (also its certificate: Issued by, days, key), 802.1X trusted CA (the former Allow CA), VLAN and
Block (with "Also revoke their IEEE 802.1X certificates", the former Revoke); `docs/spec/pki.md` "Several CAs and the
policy cleanup".

A PRO part of the PKI package since OADM 4.5 (core plugin `oadm.radius`, `plugins/Oadm.Plugins.Pki/Radius`, its own
source-available license like EST; `docs/spec/pki.md` "One plugin"). Off by default: **Turn on** in the tab's off state
stores the choice and starts it (UDP 1812 opens), **Turn off** (left of Save, confirmed) stops it; it runs only while it is
on and OADM PRO is active (without it the off state reads "The 802.1X server needs OADM PRO" with "Needs an OADM PRO
license or trial." and no Turn on; a license that ends stops it, a new one starts it with the kept choice). OADM answers the switches
of the network as their **RADIUS server** and authenticates the cameras on the switch ports with **EAP-TLS** (IEEE
802.1X). It completes the certificate suite: PKI issues the camera's client certificate and installs the RADIUS server CA of
its IEEE 802.1X certificate policy (task "Enable IEEE 802.1X"), EST renews it, the 802.1X server checks it when the camera plugs in. A clean-room implementation from
the RFCs in `docs/rfc/` (verbatim copies; their README says which part each one covers).

## Decisions (2026-10-23)

- **OADM PRO**, locked without a license or trial like EST (`docs/spec/licensing.md`).
- **Only managed devices pass:** the client certificate must be issued by the OADM CA itself (the current or a previous
  one; a root above an OADM CA imported as an intermediate anchors the chain, but a certificate of another CA under that
  root never passes) **and** name a device OADM manages. A genuine certificate of a device OADM does not manage is refused.
  The PKI trusted root store is not used (decision 2026-10-24, review round 1: safer than the first wording).
- **Result:** Access-Accept or Access-Reject; a **VLAN** only from a VLAN policy of PKI (none: the switch port keeps its
  own; there is no default VLAN).
- **Switches change while the server runs:** adding, editing or removing a switch applies at once to the running
  server, without a restart and without dropping the sessions of the other switches. A removed switch's open sessions end
  (its next request is dropped as from an unknown switch).

## What the user sees

- **Turn on / Turn off** (since OADM 4.5, before on the Plugins page): off by default, Turn on in the tab's off state
  (`ui:OffState`; no Turn on without an active license, the off state says "Needs an OADM PRO license or trial." and,
  while the trial was never used, administrators get **Start 30-day trial**, `docs/spec/licensing.md` "Trial"); while it
  does not run the tab shows only the off state (Turn off too when it is on but does not run) and reads nothing else. When the license ends, the server stops answering (switches fall back to
  their own rule); a development build before 4.5 whose plugin was on keeps it on (the 802.1X server never shipped before 4.5).
- **Tab "802.1X server" of the PKI page** (since stage 7 of `docs/spec/pki.md`; no rail entry of its own): **Server**
  above **Switches** in a left column (680 px), **Sessions** right, as high as the page (one column on a narrow page;
  `RadiusView.Arrange`). The off state's text: "Switch ports let only cameras with a trusted certificate into the
  network." The rules
  are PKI's policies (`docs/spec/pki.md` "Policies"); the page has no Trusted CAs, Policies or Advanced (decision
  2026-10-24, stage 3). Revocation is PKI's (its Certificates list).
  - **Card headers like the Permissions page:** the title and its buttons in one row ("Switches [Add switch]").
  - **Server** (card): **Listen on**, **Port** (UDP 1812), **Log entries kept** (1,000..50,000, default 5,000), the link
    "N policies applied" (802.1X server, 802.1X trusted CA, VLAN and Block policies, 3 on a new server; left in the Save row,
    opens PKI's Policies tab), the status chip ("Running on port 1812", "The PKI has no certificate authority.", "The
    802.1X server has no certificate. Choose one in its policy.", "The 802.1X server cannot use its certificate: it has
    ended." (docs/spec/pki.md "Server certificates"), "Port 1812 is used by another program", "Interface Ethernet 2 is not
    available") and Turn off directly left of **Save**.
  - **Switches** (card): name (flexes; a switch that needs attention gets a dot before its name, red for "Wrong secret" and "Site
    offline", amber for "No requests yet", the state as its tooltip), address, site, last request (no state column,
    decision 2026-10-25), each
    column as wide as its longest text; Edit and Delete as row icon buttons; the dialog with the shared secret and Generate.
  - **Sessions** (one table, live: the sign-ins in progress on top, then the finished ones of the log; View certificate and
    Delete all in its header, pages of 500 with Previous / Next): every column as
    wide as its content (the full "2026-10-24 07:21:58", the port, MAC address, EAP identity, device, IP address, the VLAN
    with its policy "VLAN 30 by Building B"), the state or result flexes, Duration right-aligned; on a narrow page the grids
    scroll sideways instead of cutting columns.
- **Event rules:** "Device authenticated" (device, switch, port, VLAN), "Authentication failed" (device or unknown,
  switch, port, reason).
- **PKI task "Enable IEEE 802.1X"**: its dialog picks the IEEE 802.1X certificate policy and shows its summary (the EAP
  identity, on a new installation the IP address, and the RADIUS server CA the camera checks this server with). The task
  never compares that CA with this server's certificate (decision 2026-10-25): the user turns 802.1X off on the switch
  first when they do not match.

## MAC address, device and IP address

- **MAC address** from the switch's `Calling-Station-Id` (RFC 3580), normalized to `AC:CC:8E:12:34:56` whatever the
  vendor's format (`AC-CC-8E-…`, `accc.8e12.3456`, …); empty when the switch sends none.
- **Device** before the certificate is checked: the managed device whose serial equals the MAC (an Axis serial is its
  MAC address), shown in italics as a guess; after the check: the device the certificate names. Only the certificate
  decides, never the MAC (it can be faked): a row's device id (who may see it, the log) comes only from the checked
  certificate, a guess is only shown (fix of 2026-10-25).
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
10. **Authenticated** ("Authenticated with VLAN 20 by Building B": the VLAN and the VLAN policy that gave it).
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

A device that sends no certificate is refused with the alert of its version, and the reason names that alert: TLS 1.3
"No client certificate sent (TLS alert 116, certificate required)" (RFC 8446 4.4.2.4), TLS 1.2 and older "No client
certificate sent (TLS alert 40, handshake failure)" (RFC 5246 7.4.6).

The device-sent form starts with "The device …" where it reads better (48: "The device does not trust the server
certificate").

## Protocol

- **RADIUS** (RFC 2865) on UDP 1812 (setting), Access-Request / Access-Challenge / Access-Accept / Access-Reject;
  **EAP over RADIUS** (RFC 3579): EAP-Message split into 253-byte attributes, the `State` attribute ties the round trips.
  **Status-Server** (RFC 5997) answered while the server can authenticate; without a server certificate (no CA) or
  without OADM PRO it is silently discarded, so the switch fails over (RFC 5997 4.2). No accounting (1813) in the first
  version.
- **Message-Authenticator** (RFC 3579, BlastRADIUS hardening): required on every Access-Request (a request without it
  or with a wrong one is dropped and counted as "Wrong secret" on the switch), sent first in every answer. An
  Access-Request with neither EAP-Message nor Message-Authenticator is a MAC address or password check, not 802.1X (RFC
  3579 3.2 requires the attribute only with EAP-Message): dropped and counted apart on the switch row, never "Wrong
  secret".
- **Clients:** a request is answered only from a configured switch address (host names resolved when saved and every
  10 min); anything else is dropped (debug log, counted). The switch table is read for every request from an in-memory
  snapshot that a change replaces atomically (decision: switches change while the server runs). Two switches of one site
  never share a resolved address (a host name and its IP address): Save refuses it below Address ("Another switch has this
  address.", fix of 2026-10-25).
- **Duplicates:** a retransmission (same switch, port, identifier, authenticator) gets the cached answer, never a second
  step (RFC 5080 2.2.2). At most 20,000 cached answers (twice the open sessions allowed) and 32 MB, 30 s each, the oldest
  answered ones go first and a request still in work is never evicted. The final answer of every ended session
  (Access-Accept or Access-Reject) is kept apart for 30 s by switch and State (at most 65,536), so the retransmission of
  the last request gets it again even after the busy cache dropped it (no "Session not found" Reject for a camera that
  passed).
- **EAP-TLS** (RFC 5216, TLS 1.2; RFC 9190, TLS 1.3 with the one-byte commitment message, no resumption, no tickets).
  TLS 1.2 and 1.3 by default; TLS 1.0 and 1.1 only when the 802.1X server policy lowers the minimum (RFC 8996 deprecates
  them; SSL never).
- **Fragmentation** (RFC 5216 2.1.5), both directions: the first fragment carries the L flag and the total length, M on
  every fragment but the last, each fragment acknowledged by an empty EAP-TLS message. Fragment size = **Largest EAP
  packet** (the 802.1X server policy), at most the smallest `Framed-MTU` the switch sent in the session minus the EAP and RADIUS
  overhead (many switches send it in the first request only, RFC 3579 2.4). Incoming total at most 64 KB (refused above, "Message
  too large"), at most 60 round trips per session.
- **Keys:** the MSK from the TLS key material (TLS 1.2: PRF "client EAP encryption"; TLS 1.3: exporter
  "EXPORTER_EAP_TLS_Key_Material"), sent as MS-MPPE-Recv-Key / MS-MPPE-Send-Key (RFC 2548) in the Access-Accept, encrypted
  with the shared secret.
- **VLAN** (RFC 3580): Tunnel-Type = VLAN (13), Tunnel-Medium-Type = 802 (6), Tunnel-Private-Group-ID = the VLAN ID.
- **Reauthentication** (the 802.1X server policy: "Switch decides", or every N minutes, 10 to 1,440): every Access-Accept
  carries Session-Timeout = N × 60 seconds and Termination-Action = RADIUS-Request (1), so the switch authenticates the port
  again then instead of ending the session (RFC 3580 3.17, 3.19). "Switch decides" sends neither.
- **TLS library** (see Libraries): BouncyCastle TLS in its **non-blocking mode**: one `TlsServerProtocol` per
  session without a stream; the bytes of each incoming EAP-TLS message go in with `OfferInput`, the bytes to send come
  out with `ReadOutput` and are cut into fragments; the handshake lives in memory only, no socket. `TlsServer` overrides
  give the minimum version of the 802.1X server policy and the cipher suites that follow it (decision 2026-10-25: no
  selection), `NotifyAlertReceived` / `NotifyAlertRaised` the alert
  numbers in both directions, `TlsContext.ExportKeyingMaterial` the MSK. `SslStream` offers neither the key export nor the
  alert numbers on all three systems. Stage 1 starts with a proof: a full EAP-TLS exchange with a BouncyCastle client in a
  unit test.
- **Server certificate** (decision 2026-10-25, docs/spec/pki.md "Server certificates"): the certificate PKI's 802.1X
  server policy picks from the Certificates list (a key OADM holds, ECC only: the cipher suites follow the key and the
  minimum TLS version). Turn on without one: the certificate the server kept before is imported with its key and picked,
  else the one OADM makes for both servers (docs/spec/pki.md "Server certificates"). Never renewed by itself (the Expiry
  warning and the status remind). The cameras trust it through the PKI task "Enable IEEE 802.1X": the RADIUS server CA
  of the IEEE 802.1X policy is a CA of PKI's list (decision 2026-10-25), and the policy's hint names the CA that issued
  this certificate. Without a
  usable certificate the server refuses handshakes and the status says why ("The 802.1X server has no certificate. Choose
  one in its policy.").

## Libraries (research 2026-10-23)

Nothing is written from scratch that a maintained library or the SDK already does well:

| Part | Use | Why |
|---|---|---|
| UDP socket, firewall, Listen on | the SDK: `ctx.Network.ListenUdpAsync` (`IUdpRelay`), like NTP and DHCP | plugins may not open sockets (banned APIs); the host binds, opens the firewall and later relays sites the same way |
| TLS handshake in memory | **BouncyCastle** (`BouncyCastle.Cryptography` 2.7.0, already shipped): `TlsServerProtocol()` non-blocking, `OfferInput` / `ReadOutput` / `GetAvailableOutputBytes`, `TlsServer.GetSupportedVersions` / `GetSupportedCipherSuites`, `NotifyClientCertificate`, `NotifyAlertRaised` / `NotifyAlertReceived`, `TlsContext.ExportKeyingMaterial` (all present in 2.7.0) | the only .NET TLS stack with the key export and alert numbers on all three systems |
| RADIUS packets | **own codec** (decision 2026-10-23), from RFC 2865 / 2869 / 3579 / 5080 / 8044 | the whole stack under our control for the session state, the log and the strict parsing rules; packets keep every attribute in order with its raw bytes (Proxy-State, fixtures, View details), malformed packets are a result value, never an exception. `Flexinets.Radius.Core` (MIT) was looked at and not taken |
| EAP and EAP-TLS framing | own code (RFC 3748, 5216, 9190) | no .NET library exists; the format is a few header bytes, the logic is the session state |
| MS-MPPE key encryption | own code (RFC 2548 2.4.2) with the RFC's construction and test vectors | a few lines; not in the RADIUS library |
| Certificate checks | .NET `X509Chain` with custom root trust, offline: the OADM CAs (current and previous, issuer of the leaf itself) and the Axis device ID roots of EST (`plugins/Shared/DeviceId`), the `axis-<serial>.local` rule of PKI; never the trusted roots of the server | the chain rules of EST, one set built per CA change |
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

- **PKI page, tab "Certificates"** (stage 1; today the tab of `docs/spec/pki.md` "Certificates" with its two cards
  Certificate authorities and Certificates; nothing is read before the tab is opened): every certificate the OADM CA issued, by a PKI task (Enable HTTPS, IEEE 802.1X, Renew) or by
  EST: device (its address; a device OADM no longer manages shows its serial with "(removed)"), use (HTTPS, IEEE 802.1X,
  MQTT, RTSPS), issued by (the issuing CA since stage 5), valid until, state chip (Valid, Expires soon (the
  settings' warning days, 30 by default), Expired, Revoked (tooltip: date, reason, user)); since 2026-10-25 only each
  camera's current certificate per use (`docs/spec/pki.md` "Current and pending"), no Replaced. Filters (state, issued by; the use filter is gone since 2026-10-25) and search (device address, host name, device serial,
  certificate serial in any notation); newest first, 500 rows per page from the server (Previous / Next, "1-500 of
  12,345"), virtualized grid, `RangeObservableCollection`, overlay on the first read and a progress row on later ones;
  **View certificate** (toolbar, row menu, double click: the shared `ui:CertificateWindow`, a revoked one says "Certificate
  revoked on ..."). An event `certificates` (at most one per second) makes every open tab read its page again.
- **Revoke** (red toolbar button and row menu; one or several rows, at most 5,000 at once; the confirmation
  `ui:MessageWindow` with the choice **Reason**: Key compromised, Device replaced, Device removed, Other; red Revoke):
  the serials go into the revocation list with time, reason and user and leave the store (decision 2026-10-25); **Revoke all of this device** (row menu, last):
  every certificate of the row's device that is still valid; a device that answers has its certificate list read first
  (read only, at most 20 s), so OADM certificates on it from before the list are revoked too. While either runs the
  list's progress row says "Revoking the certificates" and both are off (a write runs to its end: no Cancel). Expired serials (and text
  that is no serial number) are skipped and counted, an existing revocation is never changed. A serial the list does not
  hold (decision 2026-10-24: a certificate of the OADM CA from before the list that no read found) is revoked too, without
  a device, and kept until the latest end of the OADM CAs. No undo (the device gets a new certificate by Enable IEEE 802.1X or Certificate
  enrollment). Methods `revoke` and `revokeDevice`: Admin with "Manage the certificate authority", `RequiresAllDevices`,
  audited by the host (Admin methods), and logged with the count, reason and user.
- **Device groups:** a user with a group sees the tab with the certificates of the group's devices only (none of removed
  devices); View certificate of another device's certificate answers like an unknown one ("This certificate is not in
  the list."); Revoke is hidden (and refused by the host).
- **Kept** (plugin records, one row each, only changed rows written, new certificates batched every 2 s, a revocation
  written before the call returns): `issued` (serial, device id, device serial from the `axis-<serial>.local` name, use,
  source, CA id, valid from, valid until, issued, alias), `issuedDer` (the DER, read one at a time) and `revoked`
  (serial, time, reason, user, device, valid until). A certificate leaves the store 30 days after it ended (decision
  2026-10-25, before a year); a revocation when its certificate ended (it can no longer be used). Checked at the start and every hour. The plugin setting `issued`
  of OADM 4.1 moves into the records at the first start and is deleted (those entries have no DER, `noCopy`: View
  certificate says "OADM keeps no copy of this certificate." until a read of the device's certificate list finds the
  certificate, which stores its copy). A certificate a task deletes from the device stays in the store, not
  listed (`removedFromDeviceUtc`): a copy could still be in use, Revoke can take it. The PKI page's device counts (and the Replace CA question) count
  only certificates still in use: not ended, not revoked, not deleted.
- **Existing certificates** (decision 2026-10-24, the cheapest robust way without a new hook in the host's full refresh):
  (1) whenever PKI reads a device's certificate list (View / Delete certificates, Renew certificates), every certificate
  there that one of the OADM CAs (active or previous) signed (checked by a chain build to that CA) and that names this
  device's `axis-<serial>.local` is added once (use: the one the EST profile name behind `oadm_` / `oadm2_` names when it
  fits the certificate (`oadm_mqtt`, `oadm2_mqtt_cameras` -> MQTT, `oadm_rtsps` -> RTSPS; MQTT and IEEE 802.1X only for
  clientAuth, HTTPS and RTSPS only without), else IEEE 802.1X for clientAuth and HTTPS for the others,
  `IssuedRegistry.PurposeOfFound`; issued by EST for the `oadm_` / `oadm2_` EST profile names, else Task; issued = its
  valid-from); (2) once per server, 5 minutes after the
  start, one background pass reads the certificate list (REST `cert` v1 or the SOAP certificate service, read only, `docs/spec/pki.md` "Older AXIS
  OS") of every device that answers and has AXIS OS 6.53 or later, 4 at a time (plugin setting `existingImported` when a round reached the devices: one device
  read, or nothing to read; a round that read no device runs again an hour later, a stop at the next start). Devices that
  did not answer come in by (1) or when renewed.
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
  (`DeviceCertificatePurposes`: https, dot1x, mqtt, rtsps); `IssuedCertificateSources` (Task, EST); `PurposeOf(serial)` (the use of a listed certificate, a lookup in memory, review round 2); `CertificateSerials.Normalize`
  (any notation: upper hex, no separators, no leading zeros; .NET keeps a leading zero byte, BouncyCastle drops it);
  `CertificateDescriber` / `CertificateDescription` for the shared certificate window.
- **802.1X page:** no Revoked tab (decision 2026-10-24): revocation is PKI's Certificates tab; a **revoke policy** of
  the 802.1X page revokes through `RevokeAsync` (Policies).
- **Later:** PKI publishes a CRL (and maybe OCSP) for RADIUS servers of other vendors.

## Who passes

- The client certificate: issued directly by a CA of an **802.1X trusted CA** policy of PKI (decision 2026-10-24; a certificate of
  another CA under the same root never passes, also when that root is imported and allowed), or for the Axis device ID a
  chain to its roots; valid now, extended key usage client authentication (or none). Previous OADM CAs follow the OADM
  CA's policy (no policies of their own), so replacing the CA never locks the cameras out. A CA PKI no longer lists drops
  out at once (fail closed); without PKI's policies nothing passes.
- For a CA with **Only devices OADM manages** (the default) it must name **exactly one managed device**: the DNS name
  `axis-<serial>.local` the OADM CA puts into every device certificate, or the serial of an **Axis device ID** (an imported
  CA: the name, else a serial in the subject). At a site: a device of that site. For a CA without it, any valid
  certificate of that CA passes; one that names a managed device counts as that device (site, policies, VLAN), any other
  passes without a device and without a VLAN, and the Device column of Sessions and Log shows its subject.
- No **Block** policy holds the device ("Blocked by Storage room", alert 49); one with **Also revoke** says "Certificate
  revoked by Stolen" (alert 44; a certificate of a CA OADM issues with is then revoked in PKI in the background). Block
  wins over VLAN.
- A CA OADM issues with (the OADM CA, a previous OADM CA, an imported CA with its key; decision 2026-10-25) keeps PKI's
  lists: its certificates listed with another use are refused and those on the revocation list too
  (`TrustedIssuer.KeepsLists`). An imported CA without its key: OADM's lists do not apply (its certificates come from
  elsewhere).
- A certificate of an OADM CA that PKI lists with another use (MQTT, HTTPS, RTSPS) is refused (alert 42, "Certificate is
  not an IEEE 802.1X certificate"; review round 2). One created, signed or imported on PKI's Certificates card is refused
  too (alert 42, "Certificate is not a device certificate"), whatever it names. One PKI does not list (from before the list)
  passes as before.
- PKI runs with a CA: without it nothing passes, an Axis device ID neither (alert 80, "The PKI has no certificate
  authority"): without PKI there is no revocation list.
- Not revoked (PKI's revocation list, above; checked for certificates of the OADM CAs); removing a device from OADM
  refuses it at its next authentication.
- The EAP identity (sent before TLS, unprotected) is shown and logged, never trusted.

## Limits and load

- 30,000 devices may authenticate after a power cut: TLS handshakes run with bounded parallelism (64 at a time, the rest
  wait in a queue; the switch retransmits and gets the cached or next answer), the session table and the log are O(1) per
  change, pushes to the page go through one batched event per 500 ms.
- Per switch at most 1,000 open sessions, per switch port at most 8 (fix of 2026-10-25: a new one on a full port ends the
  oldest there, "Too many sessions on the port", so spoofed MAC addresses on one port never use up the switch). A new
  session at the switch's or the server's limit is silently dropped (`RadiusDropReason.TooManySessions`; the switch tries
  again), never an Access-Reject. A session costs at most 64 KB; the buffer of a fragmented message grows with the
  fragments that arrived (never the announced length up front).

## Sites

Stage 4. A switch at a site sends its requests to the **site node**, which relays the datagrams to the server over its
outgoing connection (`ctx.Sites.OpenUdpRelayAsync(site, 1812)`, the relay NTP uses; no new SDK). The RADIUS, EAP and TLS
logic runs only on the server; the node never sees a secret or a key.

- **Switch rows get a Site** ("This server" or a site). A request is matched by site + the switch address as the node
  saw it, so the same private address at two sites is two switches. The switch is configured with the **node's address**
  as its RADIUS server (the switch dialog no longer names it, decision 2026-10-24).
- **The relay opens** for a site when its first switch is added and closes with its last; the node app asks to allow
  UDP 1812 like NTP's port; the page shows sites whose node has not allowed it yet (like EST).
- **Who passes at a site:** the device the certificate names must be a device of that site (as "Who passes" says).
- **Node offline:** the switch gets no answer and applies its own fallback (switch configuration, not OADM); the switch
  row shows "Site offline". Sessions of that site end as Timed out.
- **Round trips:** EAP-TLS needs about 6..12 of them; each adds the node's latency. The defaults (30 s per message, 60 s
  per session) leave room; switches usually wait 5 s per request with retries, which the duplicate cache answers.

## Device groups

A server-wide service: the settings and Switches are changed only by users with all devices; the policies are PKI's
(server-wide too, `docs/spec/pki.md`). Users with a group see the page with the Sessions and Log rows of their devices;
rows without a managed device only users with all devices. They get no server-wide data (settings, the server's
interfaces and certificate, sites, senders that are no switch, how many policies apply) and only the switches of the routes
their group names (none when it names none). The tags the policies use are protected by PKI (`docs/spec/pki.md` "Code
(stage 3)"): a user who may tag devices but has a group could otherwise choose a device's VLAN or take it out of a Block
policy. Until OADM 4.5 the 802.1X server protected them itself; its core plugin releases that set at its start. On a host
without `ProtectedTags` the tag parts of VLAN policies match no device (fail closed), those of Block policies still match,
Block policies with Also revoke never match by tag.

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
- Switches added, changed and removed while sessions run (the decision); the 802.1X server policy: minimum version 1.3 refuses a 1.2
  client with the alert text, an old CBC suite is offered only with minimum 1.0 or 1.1.
- Perf (`Category=Perf`): 30,000 authentications, the session table and the log at their limits.
- Hardware (opt-in, `Category=HardwareWrite`, never automatic): a camera with an OADM CA certificate on a real switch.

## Code (protocol)

`plugins/Oadm.Plugins.Pki/Radius/Protocol/` (namespace `Oadm.Plugins.Radius.Protocol.*`): the protocol without sockets, host or
UI; the server part feeds it datagrams. Tests: `tests/Oadm.Plugins.Radius.Tests`.

- **Datagram flow** (`Sessions/RadiusProcessor.Process(datagram, RadiusClient, IPEndPoint source)`, thread-safe, never
  throws): `RadiusPacket.Parse` (framing rules above) → only Access-Request and Status-Server → the Message-Authenticator
  (`RadiusAuthenticators.VerifyRequest`, before anything else; missing or wrong = drop `WrongSecret`, but no EAP-Message
  and no Message-Authenticator = drop `NotEap`) → Status-Server answered (Access-Accept, not cached; without a server
  certificate dropped `NotReady`) → `AccessRequest.Read` (attribute rules) → `RadiusDuplicateCache` (key: switch,
  source address and port, Identifier; same authenticator = cached answer or drop while in work; a new authenticator
  replaces the entry) → the session of the State attribute or a new one → the answer by `RadiusPacketWriter.Response`
  (Message-Authenticator first, Response Authenticator, Proxy-State last in order). The result: `Answered` /
  `AnsweredFromCache` with the bytes to send to the source, or `Dropped` with a `RadiusDropReason` (Malformed,
  NotARequest, WrongSecret, InProgress, StaleEapResponse, AnswerTooLarge, InternalError, NotEap: no EAP-Message and no
  Message-Authenticator, NotReady: Status-Server without a server certificate) and a short detail for the debug
  log; the session snapshot after the step and the reason of an Access-Reject.
- **The switch** is a `RadiusClient(Key, Secret)` the server part finds by the source address (site + address at a site);
  the processor never looks at NAS-Identifier or NAS-IP-Address for that. `EndSessions(key)` ends a removed switch's
  sessions (Abandoned, "The switch was removed") and forgets its cached answers.
- **Settings**: `RadiusProtocolOptions` (record, replaced as a whole through `Options` while running): `Tls`
  (`TlsSettings`: `MinimumVersion`, `CipherSuites` of `TlsCipherSuites`, `Validate()` for the policy, `DefaultFor(min)` for
  Reset to default), `LargestFragment` (500..1400), `Reauthentication` (null: none; else Session-Timeout and
  Termination-Action RADIUS-Request in every Access-Accept, `ReauthenticationAttributes`), `ServerIdentity` (`TlsServerIdentity.Create(chain, pkcs8)` or
  `FromCertificate(x509, issuers)`, read when a handshake starts, so a renewal applies at once), the timeouts (30 s / 60 s),
  `MaxRoundTrips` 60, `MaxMessageLength` 64 KB, `MaxSessions` 10,000, `MaxSessionsPerClient` 1,000, `MaxSessionsPerPort` 8.
- **TLS versions, suites, signatures**: only the versions with a checked suite the server key can use are offered
  (`TlsSettings.VersionsFor(key)`; TLS 1.3 without a checked TLS 1.3 suite is not offered), `Validate(key)` refuses a set
  the key cannot use (only ECDHE_RSA suites with the ECDSA server certificate). The signature schemes OADM accepts and
  uses (`EapTlsServer.AcceptedSignatures`): ECDSA, EdDSA, RSA-PSS and RSA PKCS#1 with SHA-256 or stronger, never
  SHA-224; SHA-1 only when the minimum is TLS 1.0 or 1.1 (RFC 9155 2), then also the RFC 5246 default for a TLS 1.2
  client without a list.
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
- **EAP Identifier** (RFC 3748 4.1): OADM's first request after the switch's own (its Identity request) uses that
  Identifier + 1, never the same one; a response with the Identifier of the request before is a retransmission (the
  outstanding request again); any other Identifier is dropped. EAP-Success and EAP-Failure carry the response's Identifier.
- **Fragments**: OADM's TLS data per EAP-TLS message = min(Largest EAP packet − 10, Framed-MTU − 4 − 10), an 802.11 port at most
  1,496 EAP octets (RFC 3580 3.10); L and the total only on the first fragment, never on an unfragmented message (RFC 9190
  2.1.9). The device's fragments: L required on the first of a fragmented message (at most 64 KB), a repeated L only with
  the same value, the fragments add up exactly; else "Message could not be read".
- **Accept**: EAP-Success only with an accepting verdict of the validator and the keys; the session is Authenticated only
  once the Access-Accept is built (`CompleteAccept`): an answer above 4,096 bytes ends it Failed "Answer too large" (no
  Accept is sent, the log never says Authenticated).
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

## Code (server)

`plugins/Oadm.Plugins.Pki/Radius/Server/` (namespace `Oadm.Plugins.Radius.Server`), the page contract in `Api/RadiusApi.cs`
(methods `RadiusMethods`, topics `RadiusTopics`, DTOs, the input rules `RadiusRules` shared with the page, `RadiusJson`
camelCase with enums as camelCase strings; linked into the client part). Built 2026-10-24.

- **Wiring** (`RadiusPlugin`, `RadiusHost`, `RadiusService`): PKI's CA through `ctx.Services.Find<IDeviceCertificateAuthority>()`
  each time, OADM PRO through `ctx.Pro` on every datagram (none answered without it). Listener `ctx.Network.ListenUdpAsync(port,
  "802.1X server", addresses)` (SDK 0.4.0: the addresses of the Listen on interface; with All interfaces the addresses of
  every card that is up, each on its own socket, so the answer leaves from the address the switch sent to, as switches
  check (RFC 2865 3, RFC 5080 2.2.1), plus the loopback addresses (a test on the server itself); no card with an address:
  every address). Every 5 s a cheap check of the addresses (the list of the interfaces; the host passes on no address change
  event of the OS) listens on a new set at once (a card or an address came or went; one interface: its addresses moved or
  it is gone); a listener that could not open is tried again every 30 s. The host leaves out an address that is not ready
  yet (an IPv6 address still checked) while the others serve; the listener's `IRelay.BoundAddresses` (SDK 0.4.0; null from
  an older host = the requested ones) shows it missing: the next 5 s check listens again once, and while the same
  addresses stay missing the 30 s retry does, so an address a card never takes does not reopen the listener every 5 s.
  After three tries for the same missing addresses (the 5 s check and two 30 s retries) the retries stop until the
  addresses of the cards or the missing ones change (logged once). One relay per bound site with switches (`ctx.Sites.OpenUdpRelayAsync`,
  released with the site's last switch or a new port; `sitesNotAllowed` = relays waiting for approval).
- **Datagram path**: the receive loop finds the switch in the immutable `SwitchTable` by site + source address (IPv4-mapped
  addresses as IPv4; unknown senders dropped, counted in `UnknownRequests`, debug log for the first 20 and every 1,000th,
  and counted in `DroppedRequests`),
  a bounded queue (8,192; full = dropped, the switch retransmits), 64 workers (`RadiusHost.Workers`, the bound on parallel
  handshakes) call `RadiusProcessor.Process` with the switch of now (a switch removed meanwhile: its sessions end, nothing
  sent). The switch row's state: Site offline (site or relay offline), Wrong secret (the last request had a missing or wrong
  Message-Authenticator), Answered, No requests yet; dropped counts malformed, wrong secret and too large answers;
  `macOnlyCount` the requests without EAP (MAC address or password checks).
- **Switches** (setting `switches`, at most 500): secret `ctx.Secrets.Protect(secret, "radius:switch:<id>")` (no protector:
  saving refused "This server cannot keep shared secrets."; an unreadable secret takes the switch out with a warning), never
  logged or returned (Edit with an empty secret keeps it). A save or delete builds a new table and swaps it; a changed
  address or site, or a delete, ends the switch's sessions (`EndSessions`). Host names resolved on the switch's route
  (`ForSite(siteId).ResolveAsync`) when saved (none: "The server cannot find this host name.") and every 10 minutes;
  `radiusServerAddress` = the Listen on interface's IPv4, else `LocalAddressTowardsAsync` (at a site the node's).
  `generateSecret`: 24 letters and digits (no look-alikes), nothing stored.
- **PKI's policies** (`RadiusPolicies`, `RadiusRuleSet`, `PolicyPlan`; `docs/spec/pki.md` "Code (stage 3)"): the store is
  found each time (`RadiusHost.Policies`); `RulesNow()` builds the rules again before a handshake when the store's version
  moved or PKI started again (a new store) and puts the server policy's TLS, largest EAP packet and reauthentication into the
  processor's options; a change of the store also publishes the state ("6 policies apply"). `CaTrust.Build` takes PKI's
  `Authorities` and the CAs of the 802.1X trusted CA policies (`TrustedCaSettings`, rebuilt with the rules: the trusted set compares it
  by reference): previous OADM CAs follow the OADM CA ("oadm-previous:<sha256>"), each issuing certificate's hash maps to its
  CA (`TrustedIssuer`: kind, only managed; the Axis device ID through `AxisDeviceId.Verify` with PKI's roots and
  intermediates). `PolicyPlan`: the oldest VLAN policy that holds the device gives its VLAN and its name, any Block policy
  refuses (one with Also revoke never by tag, VLAN tags only while the host protects them). The settings of the
  development builds before 4.5 (Advanced in `config`, `trustedCas`, `policies`, `vlans`) are read only once, by
  `RadiusPolicies.ConvertAsync` when the core plugin starts (PKI notes it), and are left as they are; their Revoke policies
  become Block policies with Also revoke, the 802.1X server policy gets its certificate's defaults (OADM CA, 365 days, ECC).
- **Block policies with Also revoke**: PKI revokes on save and every minute; a handshake that meets a certificate such a policy holds queues
  it (deduplicated, at most 10,000 waiting) for a background `RevokeAsync` by the user who saved the policy; a serial
  queued while that round ends starts another, and a failed round keeps its serials for the minute round (fix of
  2026-10-25). The subject of a certificate of a device OADM does not manage is shown without control or format characters.
- **Certificate check** (`DeviceCertificateValidator`, in memory): PKI's CA offer (none: alert 80 "The PKI has no
  certificate authority" for every certificate, the Axis device ID too), dates (alert 45, "Client certificate expired on ..."),
  extended key usage client authentication or none (42), a chain (`X509Chain`, custom root trust, offline) to `CaTrust`
  (the CA's `Chain` and `PreviousChains`, SDK 0.4.0: self-signed ones are the anchors) whose direct issuer is one of the
  OADM CAs themselves (`CaTrust.Issuers`: the first certificate of each chain, by SHA-256 hash; another CA under the same
  root never passes); a handshake compares only the CA offer and a counter of its `Changed` events with those the set was
  built from (`CaTrust.Authority`, `CaTrust.Version`; review round 2: no hashing per handshake) and builds the set again at
  once for a new offer (PKI restarted) or after `Changed`; the minute round also compares the hashes of the chains
  (`CaTrust.Key`). A set of an offer that is gone refuses with alert 80. PKI reads its previous CAs from PEM once per change
  (`PkiService.PreviousChains`, cached per list) and raises `Changed` when a previous CA is removed. A certificate of the
  OADM CA that PKI lists with another use than dot1x (`IDeviceCertificateAuthority.PurposeOf`, SDK 0.4.0, a lookup in
  memory) refuses with alert 42 "Certificate is not an IEEE 802.1X certificate" (42 like the other refusals of what a
  certificate is made for; unlisted ones pass as before), and one PKI marks as not a device's
  (`PkiCertificateAuthority.NotDevicePurpose`: created, signed or imported on the Certificates card) with "Certificate is
  not a device certificate"; giving the one serial of its
  `axis-<serial>.local` names (none or two: "Client certificate names no
  device"), else the Axis device ID roots (`plugins/Shared/DeviceId/AxisDeviceId`, shared with EST) giving the subject's
  serial, else alert 48; then the revocation list (44, `FindRevocation(...).Describe()`), the managed device by serial
  (`DeviceIndex`: the full device list read every 10 s and after a miss, at most every 2 s: a miss right after a read waits
  for the rest of the 2 s, never for the next round; 49 "Certificate of a device OADM does not
  manage (...)"), the switch's site (49 "The device belongs to another site"), the policies, the MAC
  warning and the VLAN (`PolicyPlan`). The verdict's tag `CheckedCertificate` (device, serial, certificate serial, the VLAN
  policy) goes with the session; Sessions and Log carry it as `VlanPolicy`.
- **Server certificate** (`ServerIdentityKeeper`, decision 2026-10-25): loads the certificate of the 802.1X server policy
  with its key (`PkiCertificateAuthority.ServerCertificate`) at the start, every minute and when the policies change; a new
  pick goes into the processor's options at once. The first time without one it calls
  `EnsureServerCertificateAsync` with the IPv4 listen addresses (with All interfaces those of every card that is up,
  without self-assigned 169.254 addresses) and the host name, handing over the certificate of the setting
  `serverCertificate` (key encrypted with `radius:server-key`, no longer written) to be imported. Without a usable one
  handshakes are refused and the status says why (`ServerIdentityKeeper.Problem`); without a CA "The PKI has no
  certificate authority."
- **Sessions tab** (`SessionBoard`): `SessionChanged` only queues; every 500 ms the tick drains it (rows keyed by session,
  the device the certificate named or a MAC guess), ends timed-out sessions (every second), takes rows ended 10 s ago off
  (`removed`), and publishes the changed rows as `sessions` with `PluginEventAudience.DeviceRows` (500 rows per event;
  rows without a device: `Guid.Empty`, users with all devices only).
- **Dropped requests** (`DroppedRequests`): a sender that is no switch (by site + address) and a `WrongSecret` drop of the
  processor (by switch) are counted per sender and reason in O(1); the 500 ms tick drains the due ones (no row in the
  last minute) into the log as `LogRow`s with `Requests` (the count), `Sender` and `SenderSiteId` (only for a sender that
  is no switch) and no device id, published like session rows (audience `Guid.Empty`: users with all devices). A sender
  without requests for a minute is forgotten; at most 64 senders that are no switch are followed at once (spoofed
  addresses cannot grow it or flood the log; the others are only counted), senders with a wrong secret are added
  switches (at most 500). Texts: `DroppedRequestTexts` in `Api/RadiusApi.cs`.
- **Log** (`RadiusLog`): one row per ended session (also Timed out and Abandoned) and the rows of dropped requests, records `log` (key = id, only new and
  removed rows, every 2 s) and `logCertificates` (the leaf DER once per fingerprint, deleted with its last row; certificates
  no row points to are deleted at the start); kept per "Log entries kept"; `listLog` searches every column in one pass, the
  time also as the page shows it (`LogQuery.TimeZoneId`, the page's `TimeZoneInfo.Local.Id`; unknown: UTC only);
  `deleteLog` clears both collections and publishes `logCleared` (a write that failed during Delete all never brings its
  rows back). Event rules: `oadm.radius.authenticated` (device, switch, port, VLAN) and `oadm.radius.failed` (the device
  the certificate named or none; switch, port, reason) for Failed and Timed out, never for Abandoned or a MAC warning.
- **Page methods** (`RadiusMethods`, roles in `RequiredRole`, `RequiresAllDevices` for every save, `generateSecret`,
  `deleteSwitch` and `deleteLog`; Admin methods audited except `generateSecret`). The methods `listRevoked`, `revoke`,
  `listCertificates`, `getIssuedCertificate` (decision 2026-10-24: revocation is PKI's) and `getTrustedCas`,
  `saveTrustedCas`, `getPolicies`, `savePolicies`, `countRevocations` (stage 3: the rules are PKI's policies) are gone.
  Rows of sessions, the log and `getLogCertificate` follow the caller's devices (`ViewedSet`); a row of another
  device answers like an unknown one; `listSites` the group's sites; `getState` gives a group user the status only
  (no settings, interfaces, server certificate, sites or unknown senders) and `listSwitches` the switches of the routes
  the group names (`CoversRoute`: its sites, This server only when the group has it). `state` goes to users with all
  devices and to each group that read the page (`ViewerScopes`, the status only), `switches` to users with all devices
  only, `logCleared` and `revoked` (PKI's `RevocationChanged`) to every page.
- **Tests**: `tests/Oadm.Plugins.Radius.Tests` `ServerTests` (end to end through the in-memory listener and relays with a
  BouncyCastle camera: accept TLS 1.2 and 1.3 and the device ID, every refusal, removed device, MAC warning, sites,
  switches added and removed while sessions run, wrong secret, no PRO, no CA, server certificate kept and renewed,
  previous CA, secrets, input rules, host names, the server policy's minimum version, Listen on, the log), `HardeningTests` (review round 1: a sibling
  CA under the enterprise root, PKI gone, a group user's state and switches,
  requests without EAP, the device list after a miss, a CA change during maintenance, stable server certificate names, one
  socket per address, the log search in local time, Delete all during a failed write, orphan certificates; review round 2:
  nothing passes without PKI, a listed MQTT certificate refused, no CA reads per handshake and a changed CA trusted at
  once, loopback and a new address within 5 s), `DroppedRequestTests` (rows of a sender that is no switch and of a wrong secret, one per minute with the count,
  never for group users, the cap of followed senders), `AllowCaTests` (the defaults, a CA without a policy or removed by
  PKI, an imported CA for any device with its subject in the log, an imported enterprise root and a sibling CA, a policy
  deleted while a handshake rebuilds the set), `PolicyTests` (VLAN with the policy in the log, the older VLAN and Block over
  VLAN, Block, Revoke of a new certificate, nothing without PKI's policies, reauthentication attributes, the defaults, tags
  only with protection), `ConversionTests` (fixtures of every stored format, once, nothing dropped), `PluginTests`
  (manifest, the canary over every
  method, rows of a group user), `PluginPerfTests` (Category=Perf: 30,000 authentications through the plugin, the log at
  50,000 kept). Support: `Support/PluginHarness.cs` (`TestAuthority`, `TestDeviceIds`, `PluginSwitch`).
- **Not built yet**: recorded conversations of real switches (no hardware here; the protocol tests replay the RFC
  examples), accounting.

## Code (page)

`plugins/Oadm.Plugins.Pki.Client/Radius` (namespace `Oadm.Plugins.Radius.Client`, `RadiusPage`, `HasOwnCards`); tests in
`tests/Oadm.Plugins.Radius.Tests/Client` against a fake server (`FakeRadiusBackend`: every method of the contract with demo
data, also for the screenshots; the four, two and one column layout and the rows of dropped requests in
`ServerLayoutTests`). One page since 2026-10-24.

- **One page, no tabs, two columns** (`RadiusView`, decision 2026-10-25): Server above Switches left, the **Sessions** card
  right (the left column 680 px, the same as the EST tab: `ServerPageLayout`), the cards as high as the page (at least 480 px; `RadiusView.FitSessions`), so the grids stay bounded
  and virtualized. Below 1,400 px of page width one column (`RadiusView.Arrange` in `MeasureOverride`, before the cards are
  measured): Server, Switches, then Sessions in a 480 px row, the page scrolls. Users limited to a device group see Sessions
  over the whole width. The Sessions card holds one table
  (`AuthenticationsViewModel`, rows `IAuthenticationRow`) with the authentications in progress on top (ended ones leave
  it, the log holds them) and the log's page below them, newest first; the log's search applies to both, View
  certificate and Delete all are the log's, the pages (Previous, Next) page the log; a read of either list or a new search builds the table again (one reset), a
  batch of the live events applies in place (updated rows change themselves, a new one is inserted on top, an ended one
  leaves), so scroll and choice stay every 500 ms (fix of 2026-10-25). `Activate` reads everything once (`LoadAllAsync`: `getState`, `listSwitches`, `listSites` with the overlay
  over the cards, then `listSessions` and `listLog`); the events keep it.
- **Card headers**: `TextBlock.cardTitle` and the card's buttons in one `WrapPanel` row (like the Permissions page).
- **Server**: Listen on, Port, Log entries kept; the Save row: the link "6 policies applied" left (`RadiusState.PoliciesApply`,
  `RadiusViewModel.PoliciesApplyText`; the tooltip lists the policies, `RadiusState.PolicyLines`; it opens PKI's Policies
  tab through `PkiTabRequest` and `ICorePluginClientContext.OpenAsync("plugin:oadm.pki")`), the status chip and Turn off
  directly left of **Save** (the one accent button of the page), the server certificate's end,
  the sites that did not allow the port and a save error without a field below it.
- **Switches**: as before; the switch dialog: Name (unique), Address (IPv4 with four parts, IPv6 or a host name; unique per
  site), Site (only with sites), Shared secret (8..128, **Generate** shows it once). A refusal goes below the field it
  names, else into a message. Columns: Name flexes, the others fixed at the width of their longest text (`MinWidth` =
  `Width`, never squeezed).
- **Sessions columns** (decision 2026-10-25: no MAC address, Duration, Device or IP address): fixed widths for their
  content (time, switch, port 70 px, EAP identity, the VLAN number only (80 px) with "VLAN 30 by Building B" as tooltip), Status
  flexes; the grid
  scrolls sideways on narrow pages. The search still finds the MAC address, the device and the IP address.
- **Reads and events**: a list read while the page was away is stale (`MarkStale` on `Activate`) and read again (the
  progress row, the rows stay). Events during a read wait and apply after it: session batches in order, log rows once each.
- **Sessions**: rows by id, a `sessions` batch updates rows in place and adds new ones on top in one step; an ended row
  leaves 10 s after it ended; the device in italics while it is a guess from the MAC; the state chip running, ok or error.
- **Log**: View certificate, Delete all (red, confirmed), the search (300 ms after the last key), pages of 500, live rows on
  the first page when they match (the chosen row stays chosen), `timeZoneId` with every read; `logCleared` during a read
  drops that read. No Add switch (decision 2026-10-24).
- **Device groups**: a user with a group (the client's `HasAllDevices` or the state's `hasAllDevices` false, fail closed)
  sees only Sessions and Log; nothing server-wide is read, and what was read before a role change leaves at once. Users
  without "manage" see no Save, Add, Edit, Delete or Delete all, and the inputs are read only.
- **Contract**: `plugins/Oadm.Plugins.Pki/Radius/Api/RadiusApi.cs` (methods, topics, DTOs and `RadiusRules`, whose texts and
  checks the page uses for its errors below the fields); the client project references the server project like EST.
  `listSites` -> every site of the server (without it the page takes the sites of the devices and switches it knows).

## Stages

1. **Built 2026-10-24:** PKI: the list of issued certificates and the revocation list (Certificates tab, Revoke, the SDK
   members), device groups filtering of the Certificates tab; EST refuses revoked certificates.
2. **Built 2026-10-24 (server part):** RADIUS and EAP-TLS core (own RADIUS codec, BouncyCastle TLS proof first), fragmentation, the server certificate,
   switches (changed while running), Sessions and Log, the TLS alert texts, Advanced, the Revoked tab.
3. **Built 2026-10-24 (server part):** VLAN rules (off by default), event rules, device groups filtering (the PKI Certificates tab was filtered in stage 1).
4. **Built 2026-10-24 (server part):** Switches at sites through the site node.
5. **Built 2026-10-24:** one page without tabs, Trusted CAs from PKI, policies (VLAN, block, revoke) instead of the VLAN
   rules and the Revoked tab, Advanced as a dialog, Largest EAP packet 1,020 by default.
6. **Built 2026-10-24 (stage 3 of `docs/spec/pki.md`):** the rules are PKI's policies (802.1X server with reauthentication,
   Allow CA, VLAN, Block, Revoke), converted once from the stored settings; the page: Server | Switches, Sessions and Log
   with a splitter, "N policies apply" with Edit policies, the shared secret from 8 characters.

The switches, settings, PKI's policies, the issued certificates and the revocation list are part of the encrypted backup
(plugin settings and records, `docs/spec/settings-backup.md`); a restore brings back the revocation list with the CA.
