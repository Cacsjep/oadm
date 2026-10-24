# 802.1X server (OADM PRO)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-23; built 2026-10-24: the protocol layer ("Code (protocol)") and the server part ("Code (server)"); the
page is the client part.

An OADM PRO plugin (`oadm.radius`, `"pro": true`, its own source-available license like EST). OADM answers the switches
of the network as their **RADIUS server** and authenticates the cameras on the switch ports with **EAP-TLS** (IEEE
802.1X). It completes the certificate suite: PKI issues the camera's client certificate and trusts the OADM CA (task
"IEEE 802.1X"), EST renews it, the 802.1X server checks it when the camera plugs in. A clean-room implementation from
the RFCs in `docs/rfc/` (verbatim copies; their README says which part each one covers).

## Decisions (2026-10-23)

- **OADM PRO**, locked without a license or trial like EST (`docs/spec/licensing.md`).
- **Only managed devices pass:** the client certificate must be issued by the OADM CA itself (the current or a previous
  one; a root above an OADM CA imported as an intermediate anchors the chain, but a certificate of another CA under that
  root never passes) **and** name a device OADM manages. A genuine certificate of a device OADM does not manage is refused.
  The PKI trusted root store is not used (decision 2026-10-24, review round 1: safer than the first wording).
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
    one usable suite for the chosen versions must stay checked; a suite the server certificate cannot use, ECDHE_RSA with
    its ECDSA key, says "Not usable with the server certificate." below its name and does not count: Save stays off with
    the reason as its tooltip), **Largest fragment** (bytes, 500..1400, default 1000; the switch's `Framed-MTU` makes it smaller). The
    status left of Save: "Running on port 1812", "OADM PRO is not active.", "The PKI has no
    certificate authority.", "Port 1812 is used by another program", "Interface Ethernet 2 is not available".
  - **Switches** (card, right): name, address (IP or host name), state chip (Answered, No requests yet, Wrong secret),
    last request, the requests without 802.1X (a MAC address or password check of the switch: not answered, counted apart,
    never Wrong secret); **Add switch**, Edit, Delete (red). The dialog: Name, Address, **Shared secret** (`ui:PasswordBox`,
    16 characters or more, **Generate** fills a random one and shows it once to copy), validation below the fields.
    Changes apply to the running server at once (decision above). At most 500 switches.
  - **VLANs** (card, below; collapsed while off): **Assign VLANs** (off by default); rules in order, first match wins:
    a **site** or a **tag** → **VLAN ID** (1..4094); **Default VLAN** (optional; empty = no VLAN for devices no rule
    matches). Applies to the next authentication of each device. The tags of the rules are protected: only users with all
    devices (also an Operator with "Tag devices") add them to devices or remove them, also while the 802.1X server is
    stopped or off ("The tag Floor 3 sets a VLAN. Only users with all devices can add or remove it. Nothing was
    changed."); while the 802.1X server runs nobody renames or deletes them ("The tag Floor 3 sets a VLAN. Change the VLAN
    rule first. Nothing was changed."). While it is stopped or off (also PRO lapsed) they can be renamed or deleted: the
    rule then matches no device (docs/spec/tags.md "Protected tags").
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
  snapshot that a change replaces atomically (decision: switches change while the server runs).
- **Duplicates:** a retransmission (same switch, port, identifier, authenticator) gets the cached answer, never a second
  step (RFC 5080 2.2.2). At most 20,000 cached answers (twice the open sessions allowed) and 32 MB, 30 s each, the oldest
  answered ones go first and a request still in work is never evicted. The final answer of every ended session
  (Access-Accept or Access-Reject) is kept apart for 30 s by switch and State (at most 65,536), so the retransmission of
  the last request gets it again even after the busy cache dropped it (no "Session not found" Reject for a camera that
  passed).
- **EAP-TLS** (RFC 5216, TLS 1.2; RFC 9190, TLS 1.3 with the one-byte commitment message, no resumption, no tickets).
  TLS 1.2 and 1.3 by default; TLS 1.0 and 1.1 only when Advanced lowers the minimum (RFC 8996 deprecates them; SSL never).
- **Fragmentation** (RFC 5216 2.1.5), both directions: the first fragment carries the L flag and the total length, M on
  every fragment but the last, each fragment acknowledged by an empty EAP-TLS message. Fragment size = **Largest
  fragment** (Advanced), at most the smallest `Framed-MTU` the switch sent in the session minus the EAP and RADIUS
  overhead (many switches send it in the first request only, RFC 3579 2.4). Incoming total at most 64 KB (refused above, "Message
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
  every certificate of the row's device that is still valid; a device that answers has its certificate list read first
  (read only, at most 20 s), so OADM certificates on it from before the list are revoked too. While either runs the
  list's progress row says "Revoking the certificates" and both are off (a write runs to its end: no Cancel). Expired serials (and text
  that is no serial number) are skipped and counted, an existing revocation is never changed. A serial the list does not
  hold (decision 2026-10-24: a certificate of the OADM CA from before the list that no read found) is revoked too, without
  a device, and kept until the latest end of the OADM CAs (the 802.1X Revoked tab shows "Unknown device"). No undo (the device gets a new certificate by Enable IEEE 802.1X or Certificate
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
  of OADM 4.1 moves into the records at the first start and is deleted (those entries have no DER, `noCopy`: View
  certificate says "OADM keeps no copy of this certificate." until a read of the device's certificate list finds the
  certificate, which stores its copy). A certificate a task deletes from the device stays listed as Replaced
  (`removedFromDeviceUtc`): a copy could still be in use. The PKI page's device counts (and the Replace CA question) count
  only certificates still in use: not ended, not revoked, not deleted.
- **Existing certificates** (decision 2026-10-24, the cheapest robust way without a new hook in the host's full refresh):
  (1) whenever PKI reads a device's certificate list (View / Delete certificates, Renew certificates), every certificate
  there that one of the OADM CAs (active or previous) signed (checked by a chain build to that CA) and that names this
  device's `axis-<serial>.local` is added once (use: the one the EST profile name behind `oadm_` / `oadm2_` names when it
  fits the certificate (`oadm_mqtt`, `oadm2_mqtt_cameras` -> MQTT, `oadm_rtsps` -> RTSPS; MQTT and IEEE 802.1X only for
  clientAuth, HTTPS and RTSPS only without), else IEEE 802.1X for clientAuth and HTTPS for the others,
  `IssuedRegistry.PurposeOfFound`; issued by EST for the `oadm_` / `oadm2_` EST profile names, else Task; issued = its
  valid-from); (2) once per server, 5 minutes after the
  start, one background pass reads the certificate list (REST `cert` v1, read only) of every device that answers and has
  AXIS OS 11.11 or later, 4 at a time (plugin setting `existingImported` when a round reached the devices: one device
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
- **802.1X page, tab "Revoked":** the revoked certificates (from PKI's list) and **Revoke**, which opens the PKI
  certificate list as a picker (search, filters) and revokes the chosen ones. A second door to the same list.
- **Later:** PKI publishes a CRL (and maybe OCSP) for RADIUS servers of other vendors.

## Who passes

- The client certificate: issued by the OADM CA itself (the current or a previous one; the chain may end in a root above
  it, a certificate of another CA under that root never passes), valid now, extended key usage client authentication (or
  none), and it names **exactly one managed device**: the DNS name `axis-<serial>.local` the
  OADM CA puts into every device certificate, or the serial of the factory **Axis device ID** (checked against the Axis
  device ID roots EST ships). At a site: a device of that site.
- A certificate of the OADM CA that PKI lists with another use (MQTT, HTTPS, RTSPS) is refused (alert 42, "Certificate is
  not an IEEE 802.1X certificate"; review round 2): an MQTT client certificate has client authentication and the
  `axis-<serial>.local` name too, but the 802.1X page cannot revoke it. One PKI does not list (from before the list)
  passes as before.
- PKI runs with a CA: without it nothing passes, an Axis device ID neither (alert 80, "The PKI has no certificate
  authority"): without PKI there is no revocation list.
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
page with the Sessions and Log rows of their devices; rows without a managed device only users with all devices. They get
no server-wide data (settings, the server's interfaces and certificate, sites, senders that are no switch, the VLAN
settings: `getVlans` answers empty settings, no rules, no default and no tag choices) and only the switches of the routes
their group names (none when it names none). The tags of the VLAN rules are protected (review round 2): a user who may tag
devices but has a group could otherwise choose a device's VLAN. The plugin hands the rules' tag names to the host
(`ctx.ProtectedTags.Protect`, SDK 0.4.0) at its start and with every save (the old and the new names while the save runs);
`DeviceTagStore` refuses them for a group user in the tagging transaction. On a host without `ProtectedTags` the tag rules
match no device (fail closed; site rules and the default VLAN apply). The host keeps the protected names while the plugin
is installed (round 3): a stopped or turned-off 802.1X server (PRO lapsed, a restart with it off) still protects the tags
of its stored rules, so a tag a group user adds meanwhile never sets a VLAN when the server starts again; the names are
forgotten only when the plugin is removed (docs/spec/tags.md "Protected tags"). While the 802.1X server runs, a rename or
delete of such a tag is refused for every user (`Protect(names, RadiusTexts.VlanTag, RadiusTexts.VlanTagChange)`): the
rules keep tags by name. While it is stopped, off or faulted (a lapsed PRO license turns the package off) nobody can edit
the rules, so the rename or delete is allowed (decision 2026-10-24, final review): the rule's old name then matches no
device (fail closed), and a tag made again under that name is protected again. A change of case only is no rename. A save
of the rules waits for group users' tag writes in flight (`Protect` returns after them). Users with all devices, also an Operator
with all devices and "Tag devices", add and remove the VLAN-rule tags: they manage VLAN membership (decision
2026-10-24).

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
  (`TlsSettings`: `MinimumVersion`, `CipherSuites` of `TlsCipherSuites`, `Validate()` for the page, `DefaultFor(min)` for
  Reset to default), `LargestFragment` (500..1400), `ServerIdentity` (`TlsServerIdentity.Create(chain, pkcs8)` or
  `FromCertificate(x509, issuers)`, read when a handshake starts, so a renewal applies at once), the timeouts (30 s / 60 s),
  `MaxRoundTrips` 60, `MaxMessageLength` 64 KB, `MaxSessions` 10,000, `MaxSessionsPerClient` 1,000.
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
- **Fragments**: OADM's TLS data per EAP-TLS message = min(Largest fragment, Framed-MTU − 4 − 10), an 802.11 port at most
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

`plugins/Oadm.Plugins.Radius/Server/` (namespace `Oadm.Plugins.Radius.Server`), the page contract in `Api/RadiusApi.cs`
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
  addresses stay missing the 30 s retry does, so an address a card never takes does not reopen the listener every 5 s. One relay per bound site with switches (`ctx.Sites.OpenUdpRelayAsync`,
  released with the site's last switch or a new port; `sitesNotAllowed` = relays waiting for approval).
- **Datagram path**: the receive loop finds the switch in the immutable `SwitchTable` by site + source address (IPv4-mapped
  addresses as IPv4; unknown senders dropped, counted in `UnknownRequests`, debug log for the first 20 and every 1,000th),
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
  certificate is made for; unlisted ones pass as before); giving the one serial of its
  `axis-<serial>.local` names (none or two: "Client certificate names no
  device"), else the Axis device ID roots (`plugins/Shared/DeviceId/AxisDeviceId`, shared with EST) giving the subject's
  serial, else alert 48; then the revocation list (44, `FindRevocation(...).Describe()`), the managed device by serial
  (`DeviceIndex`: the full device list read every 10 s and after a miss, at most every 2 s: a miss right after a read waits
  for the rest of the 2 s, never for the next round; 49 "Certificate of a device OADM does not
  manage (...)"), the switch's site (49 "The device belongs to another site"), the MAC warning and the VLAN
  (`VlanPlan`). The verdict's tag `CheckedCertificate` (device, serial, certificate serial) goes with the session.
- **Server certificate** (`ServerIdentityKeeper`): ECDSA P-256 from the OADM CA for the IPv4 listen addresses (with All
  interfaces those of every card that is up, without self-assigned 169.254 addresses) and the host name, 365 days, kept in
  the setting `serverCertificate` (key encrypted with `radius:server-key`), made again with a new CA, a new name or 30 days
  before its end (checked at the start and every minute; a round asked for while one runs, e.g. a CA change, runs right
  after it); names that only went away (a card or cable down, virtual adapters that come and go) keep the certificate, so
  it does not change with every link; a new one goes into the processor's options at once. Without a CA none: handshakes are refused, the status
  says "The PKI has no certificate authority."
- **Sessions tab** (`SessionBoard`): `SessionChanged` only queues; every 500 ms the tick drains it (rows keyed by session,
  the device the certificate named or a MAC guess), ends timed-out sessions (every second), takes rows ended 10 s ago off
  (`removed`), and publishes the changed rows as `sessions` with `PluginEventAudience.DeviceRows` (500 rows per event;
  rows without a device: `Guid.Empty`, users with all devices only).
- **Log** (`RadiusLog`): one row per ended session (also Timed out and Abandoned), records `log` (key = id, only new and
  removed rows, every 2 s) and `logCertificates` (the leaf DER once per fingerprint, deleted with its last row; certificates
  no row points to are deleted at the start); kept per "Log entries kept"; `listLog` searches every column in one pass, the
  time also as the page shows it (`LogQuery.TimeZoneId`, the page's `TimeZoneInfo.Local.Id`; unknown: UTC only);
  `deleteLog` clears both collections and publishes `logCleared` (a write that failed during Delete all never brings its
  rows back). Event rules: `oadm.radius.authenticated` (device, switch, port, VLAN) and `oadm.radius.failed` (the device
  the certificate named or none; switch, port, reason) for Failed and Timed out, never for Abandoned or a MAC warning.
- **Page methods** (`RadiusMethods`, roles in `RequiredRole`, `RequiresAllDevices` for every save, `generateSecret`,
  `deleteSwitch`, `deleteLog` and `revoke`): `revoke` also needs PKI's "Manage the certificate authority"
  (`oadm.pki:manage`, checked with the call: "Your role does not allow: PKI: Manage the certificate authority.") and takes
  only IEEE 802.1X certificates (PKI's use dot1x, or a serial PKI's list does not hold whose client certificates in this
  server's log were all issued by an OADM CA, current or previous (the logged DER chained like a handshake, within its own
  validity); an Axis device ID serial never: revoking it would lock the camera out of 802.1X and of EST Set up for good;
  anything else refuses the whole request "Only IEEE 802.1X certificates can be revoked here.", nothing revoked; one pass:
  `PurposeOf` per serial and one pass over the log, no search of PKI's list per serial); `listRevoked` takes `RevokedQuery`
  (`offset`, `limit` 1..500, `search` over device, serial, reason and user) and answers one page (`RevokedPage`: `rows`, `total`); rows of sessions, the log,
  `getLogCertificate`, `listRevoked`,
  `listCertificates` (PKI's `ListIssuedAsync` with the caller's `DeviceIds`) and `getIssuedCertificate` follow the caller's
  devices (`ViewedSet`); a row or certificate of another device answers like an unknown one; `listSites` the group's sites;
  `getVlans` answers a group user empty VLAN settings (no rules, no default, no tag choices); `getState` gives a group user the status only
  (no settings, interfaces, server certificate, sites or unknown senders) and `listSwitches` the switches of the routes
  the group names (`CoversRoute`: its sites, This server only when the group has it). `state` goes to users with all
  devices and to each group that read the page (`ViewerScopes`, the status only), `switches` to users with all devices
  only, `logCleared` and `revoked` (PKI's `RevocationChanged`) to every page.
- **Tests**: `tests/Oadm.Plugins.Radius.Tests` `ServerTests` (end to end through the in-memory listener and relays with a
  BouncyCastle camera: accept TLS 1.2 and 1.3 and the device ID, every refusal, removed device, MAC warning, sites,
  switches added and removed while sessions run, wrong secret, VLANs, no PRO, no CA, server certificate kept and renewed,
  previous CA, secrets, input rules, host names, Advanced, Listen on, the log), `HardeningTests` (review round 1: a sibling
  CA under the enterprise root, PKI gone, revoke permission and uses, the revoked pages, a group user's state and switches,
  requests without EAP, the device list after a miss, a CA change during maintenance, stable server certificate names, one
  socket per address, the log search in local time, Delete all during a failed write, orphan certificates; review round 2:
  nothing passes without PKI, a listed MQTT certificate refused, no CA reads per handshake and a changed CA trusted at
  once, an Axis device ID serial from the log never revoked, the revoke check without a search per serial, empty VLAN
  settings for a group user, the VLAN tags protected and tag rules without protection, loopback and a new address within
  5 s), `PluginTests`
  (manifest, the canary over every
  method, rows of a group user), `PluginPerfTests` (Category=Perf: 30,000 authentications through the plugin, the log at
  50,000 kept). Support: `Support/PluginHarness.cs` (`TestAuthority`, `TestDeviceIds`, `PluginSwitch`).
- **Not built yet**: recorded conversations of real switches (no hardware here; the protocol tests replay the RFC
  examples), accounting.

## Code (page)

`plugins/Oadm.Plugins.Radius.Client` (namespace `Oadm.Plugins.Radius.Client`, `RadiusPage`, `HasOwnCards`); tests in
`tests/Oadm.Plugins.Radius.Tests/Client` against a fake server (`FakeRadiusBackend`: every method of the contract with demo
data, also for the screenshots; review round 2 in `ReviewRound2PageTests` and `ReviewRound2HeadlessTests`). Built 2026-10-24.

- **Fixed tabs** (`ui:PageTab`): **Server** (General with Advanced left, Switches right, VLANs below), **Sessions**, **Log**,
  **Revoked**. Nothing of a tab is read before it opens (the Server tab reads `getState`, `listSwitches`, `getVlans` each
  time it opens, the overlay over the cards; the lists the first time, then the events keep them).
- **One Save** at the end of the VLANs card saves General with Advanced (`saveSettings`) and then the VLANs (`saveVlans`),
  the status chip and the server certificate's end directly left of it, the sites that did not allow the port and a save
  error without a field below it (one accent button per view). Both parts are checked with `RadiusRules` before the first
  call (an error below its field, else below Save), so a refusal leaves nothing half saved; a `saveVlans` refused for
  another reason still shows the saved General. While Assign VLANs is off the collapsed card blocks nothing: Save sends
  the card when it is valid, else the saved rules with Assign VLANs off. Switches apply in their dialog at once.
- **Advanced** is a chevron (`ToggleButton.sectionExpander`) and starts collapsed; the old CBC suites are listed and checked
  only while the minimum is below TLS 1.2; the page sends only the listed checked suites; the error "Check at least one
  cipher suite." below the list. A suite with `usable: false` (the server's key cannot use it) shows "Not usable with
  the server certificate." below its name and never counts as the checked one (review round 2).
- **Switch dialog**: Name (unique), Address (IPv4 with four parts, IPv6 or a host name; unique per site), Site (only when
  the server has sites), Shared secret (`ui:PasswordBox`, 16..128 characters; **Generate** asks `generateSecret` and shows
  the secret in plain text once with "Copy it now into the switch: OADM does not show it again."; on Edit empty keeps it),
  the line "RADIUS server for this switch: <address>, port <port>" (the row's `radiusServerAddress`, else the Listen on
  address, the site node's address for a site). A refusal of the server goes below the field it names, else into a message.
- **VLANs**: Assign VLANs (check box), the rules (Match Site or Tag, the site or tag, VLAN ID 1..4094, move up and down,
  Remove), Default VLAN (optional). Tags: the server's list (`tagChoices`) plus the tags of the devices this client knows
  and of the saved rules. A user limited to a group (the client's `HasAllDevices` or the state's `hasAllDevices` false,
  fail closed) reads no VLANs: no `getVlans`, the card's content hidden and emptied (no rules, no tag or site choices);
  rules read before a role change leave at once. Empty settings from the server show an empty card (review round 2).
- **Reads and events**: a list read while the page was away is stale (`MarkStale` on `Activate`) and read again when its
  tab opens (the progress row, the rows stay). Events that arrive during a read wait and apply after it: session batches
  in order (an update older than the read row, a shorter duration, is left out), log rows once each.
- **Sessions**: rows by id in a dictionary, a `sessions` batch updates rows in place and adds new ones on top in one step;
  an ended row leaves 10 s after it ended (one step per second, the page's one-second tick also moves the durations of the
  open rows); the device in italics while it is a guess from the MAC (`TextBlock.guess`); the state chip running (violet),
  ok or error with the text of the server ("Sending server certificate: fragment 2 of 4").
- **Log**: pages of 500 from the server with the search (300 ms after the last key), live `log` rows on the first page when
  they match the search (the chosen row stays chosen when they push the full page), the reason or the warning in the Result chip (a warning is amber), View certificate describes the
  DER of `getLogCertificate` with the SDK's `CertificateDescriber` in the client, Delete all red and confirmed. Every
  read sends `timeZoneId` (`TimeZoneInfo.Local.Id`) so the search finds the times as shown; `logCleared` during a read
  drops that read and the live rows that waited for it (review round 2).
- **Revoked**: pages of 500 from the server: `listRevoked` with `{offset, limit (at most 500), search}` (search over device,
  serial, reason and user, 300 ms after the last key) -> `{total, rows}` newest first, Previous / Next and "1-500 of
  1,202" below the table; a server part that answers only `{rows}` gets the search and the pages in the client. A row
  without a device id says "Unknown device", a removed device "Device (removed)". **Revoke** opens the shared certificate
  list (`ui:IssuedCertificateList`, started on the IEEE 802.1X certificates) as a choice, then the reason
  (`IssuedCertificateList.ConfirmRevokeAsync`), then `revoke`, with "Revoking the certificates" in the progress row and
  Revoke off until the server answered (no Cancel: a write runs to its end); "Nothing was revoked ..." when every chosen
  one was revoked already. View certificate in the choice shows the row's revocation and "ends soon" as the row's state says (the list
  sends no warning days; PKI's own tab uses the days of its answer).
- **Device groups**: a user with a group sees only Sessions and Log (the server filters the rows); users without "manage"
  or without all devices see no Save, Add switch, Edit, Delete, Delete all or Revoke, and the inputs are read only.
- **Contract**: the server part's `plugins/Oadm.Plugins.Radius/Api/RadiusApi.cs` (methods, `RadiusTopics`, DTOs and
  `RadiusRules`, whose texts and checks the page uses for its errors below the fields); the client project references the
  server project like EST (a linked copy would give the tests every type twice). A session event row with `removed` leaves
  the tab at once; the page also drops ended rows 10 s after their end. **Page additions** for the server part
  (`RadiusPageMethods` in `Api/RadiusApi.cs`): `listSites` (-> `{sites: [{siteId, siteName}]}`, every site
  of the server; without it the page takes the sites of the devices and switches it knows), `listCertificates` (PKI's
  list through `IDeviceCertificateAuthority.ListIssuedAsync`: `{state, purpose, source, search, offset, limit}` ->
  `{rows: [IssuedCertificateInfo], total, offset}`, rows of the caller's devices) and `getIssuedCertificate`
  (`{serialNumber}` -> `{der}`, empty when OADM kept no copy). VLAN tag choices: the server's `tagChoices`, the tags of the
  devices the client knows and those of the saved rules (users with all devices only).

## Stages

1. **Built 2026-10-24:** PKI: the list of issued certificates and the revocation list (Certificates tab, Revoke, the SDK
   members), device groups filtering of the Certificates tab; EST refuses revoked certificates.
2. **Built 2026-10-24 (server part):** RADIUS and EAP-TLS core (own RADIUS codec, BouncyCastle TLS proof first), fragmentation, the server certificate,
   switches (changed while running), Sessions and Log, the TLS alert texts, Advanced, the Revoked tab.
3. **Built 2026-10-24 (server part):** VLAN rules (off by default), event rules, device groups filtering (the PKI Certificates tab was filtered in stage 1).
4. **Built 2026-10-24 (server part):** Switches at sites through the site node.

The switches, VLAN rules, settings, the issued certificates and the revocation list are part of the encrypted backup
(plugin settings and records, `docs/spec/settings-backup.md`); a restore brings back the revocation list with the CA.
