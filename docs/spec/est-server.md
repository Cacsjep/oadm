# EST server (OADM PRO)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-22; stages 1 to 4 and 6 built (stages at the end). Code details: `plugins/Oadm.Plugins.Pki/Est/README.md`.

The first OADM PRO feature: since OADM 4.5 a PRO part of the PKI package (core plugin `oadm.est`, `plugins/Oadm.Plugins.Pki/Est`;
OADM 4.1 shipped it as the plugin `oadm.est`). OADM runs an EST server (RFC 7030) and sets up the cameras' own EST client, so
every camera gets its certificates (HTTPS, IEEE 802.1X, MQTT, RTSPS) from the OADM CA and **renews them by itself**
also at sites (through a relay of the site node). A clean-room implementation from RFC 7030 and the Axis camera APIs.

## What the user sees

- **Turn on / Turn off** (since OADM 4.5 the EST server is a PRO part of PKI, `docs/spec/pki.md` "One plugin"; OADM 4.1 had
  it as its own plugin with the Plugins page check box): off by default; **Turn on** in the tab's off state (`ui:OffState`, "The EST server is off") stores the
  choice and starts the server (TCP 9454 opens), **Turn off** (left of Save, confirmed) stops it. It runs only while it is on
  and an OADM PRO license or trial is active: without one the off state reads "The EST server needs OADM PRO" with
  `ProLicenseTexts.NeedsPro` ("Needs an OADM PRO license or trial.") and no Turn on, while the trial was never used
  **Start 30-day trial** for administrators (`docs/spec/licensing.md` "Trial"); when the license ends or is
  removed the server stops (enrolled cameras keep their certificates until they expire, renewals are refused while it is
  off) and a new license starts it again with the kept choice. A 4.1 server whose plugin was on keeps it on (the choice is
  taken over once, its settings and records stay). The task Certificate enrollment fails while the server does not run
  ("The EST server is off. Turn it on on the EST server page. Nothing was changed.", or "OADM PRO is not active.").
- **Page "EST server"** (the tab EST server of the PKI page; two columns, decision 2026-10-25, docs/spec/pki.md):
  - **Server** above **Profiles** left (Listen on, port, enrollments kept; below it, in the warning style, why
    no camera can enroll), **Enrollments** right. The Save row of Server: the link "4 policies applied" left (the server's
    certificate, who may enroll and which CAs sign in are PKI's EST server, Enrollment and EST sign-in CA policies,
    docs/spec/pki.md "Code (stage 6)" and "Several CAs and the policy cleanup"; the
    tooltip lists them, it opens the Policies tab), the status left of Save: "Running on port 9454", "OADM PRO is not
    active.", "The PKI has no certificate authority.", "The EST server has no certificate: Corp CA cannot issue." (the CA
    of the EST server policy lost its key, docs/spec/pki.md "Policies as a list"), or the listener's state ("... is used by
    another program").
  - **Profiles** (at most 2 per camera, the camera's limit: a third add is refused, "No more profiles can be added",
    seen on 10.0.0.48; a use belongs to one profile): name, use (HTTPS, IEEE 802.1X,
    MQTT, RTSPS), issuing policy (PKI's HTTPS certificate policies for HTTPS and RTSPS, IEEE 802.1X certificate policies for
    IEEE 802.1X and MQTT: their valid days and key; docs/spec/pki.md "Code (stage 4)"; by default the built-in one of its
    kind, decision 2026-10-25: one issuing policy per kind, no "EST HTTPS" policies), on. The profile name stays what
    the camera uses in its URL. Two ready-made: **HTTPS** (server certificates, policy "HTTPS") and **IEEE 802.1X** (client
    certificates, policy "IEEE 802.1X"). A profile whose policy cannot be read issues nothing (503 "The issuing policy of
    this profile cannot be read.").
  - **Enrollments**: time, device, profile, new or renewal, result, reason (500 newest, setting 100..5,000). **View
    certificate** (toolbar, or a double click on a row; decision 2026-10-23) shows the certificate an enrollment issued
    in OADM's own window (the shared `ui:CertificateWindow` of the client SDK, also used by PKI and the 802.1X server): validity with a status chip, issued to and issued by, key and
    signature, version, serial number, SHA-256 fingerprint, uses, the names in it; values selectable. The server keeps the
    certificate (DER) of every listed enrollment in the records collection `issued` by serial number and deletes it when
    its enrollment leaves the list or on Delete all (`Enrollment.SerialNumber`, `EstStore.IssuedAsync`, read one at a time
    through `IPluginRecords.GetAsync`); method `getCertificate` (everyone who sees the page) describes it in plain names
    (`CertificateDescriber`); older entries have none ("OADM no longer keeps this certificate."). Search in
    the page (device, profile, kind, result, reason; one pass and one grid reset over at most 5,000 rows, new rows
    under the search as they come); **Delete all** (red toolbar button like the event log, confirmed, Admin, every
    client's list empties: topic `enrollmentsCleared`).
  - **Not renewed** (only while there are any): device, profile, valid until, state (Not renewed, Expired), 500 at most
    with "Showing 500 of ...". No list of every certificate: the certificates are on the cameras (PKI page, device
    details; every certificate the OADM CA issued: PKI page, tab Certificates). No Revoke here: revoking is PKI's (tab
    Certificates, `docs/spec/radius-server.md` "Certificates and revocation", built 2026-10-24); a revoked certificate
    renews nothing (below) and is never flagged Not renewed. **Remove** in the dialog still stops renewals.
- **Task "Certificate enrollment"** (device context menu, Security; one task per device), its dialog like the
  Applications (ACAP) dialog: the chosen camera's enrollment profiles (read-only query `listProfiles`: profile, use, set
  up by OADM or Other, EST server, status Enrolled / No certificate / Server not reached), **Remove** (red, inline
  confirmation; any profile, also of another EST server, decision 2026-10-22) and **Set up**; runs are named "Set up
  certificate enrollment" or "Remove enrollment profile <id>". **The dialog stays open** (decision 2026-10-23: it closed
  and the user did not see what happened): Set up and Remove start the task from the dialog (`RunTaskAsync`), the run line
  below the list shows the step ("Set up: Step 5/7 · Wait for the https certificate"; several cameras "3 of 12 cameras
  done · 1 failed"), then the result as a status chip (done, done with warnings, failed with the camera's message;
  several cameras name up to 5 by address, "+N more in the tasks pane"), and the profiles are read again; the footer says
  Close. The runs also show in the tasks pane.
- **Event rules**: "Certificate enrolled", "Enrollment refused" (device, profile, reason), "Certificate not renewed"
  (device, profile, valid until).
- **Users with a device group** (docs/spec/device-scope.md "Server-wide services"): the page read only (Save and Delete
  all hidden; the server refuses them, `RequiresAllDevices`); Enrollments, Not renewed, View certificate, the certificate
  count and the listeners only of the group's devices (a listener of this server when the group has a device without a
  site, of a site when it has a device there); refusals of cameras OADM does not manage only for users with all devices.
  Live updates: a new enrollment goes to the pages that see its device; state, Delete all and Not renewed changes to
  users with all devices and to each group whose users read the page (with that group's rows).

## EST server

- Operations: `GET /.well-known/est/<profile>/cacerts`, `POST .../simpleenroll`, `POST .../simplereenroll`. No csrattrs,
  serverkeygen, fullcmc (404). Unknown or disabled profile: the same 404 everywhere (no probing).
- **Port TCP 9454** (setting; above 1024, so no rights needed on Linux servers and nodes). TLS 1.2+, own listener (not the
  gRPC Kestrel): `ctx.Network.ListenTcpAsync` on the server's own network (every address, or with **Listen on** one interface: the
  overload with its addresses, no IPv6 link-local; the set-up gives local cameras that interface's address of their
  family, else "has no IPv4 address for this camera. Nothing was changed."; a gone interface: status "Interface Ethernet 2 is not
  available", tried again every 30 s; a server certificate made on Turn on names that interface's addresses) and `ctx.Sites.OpenTcpRelayAsync(site, port)`
  per site the cameras of which are set up (the node app asks to allow the port, like NTP's). TLS and a small HTTP/1.1
  parser on the raw stream in the plugin (the SDK has no HTTPS helper on `IStreamListener` yet; built here, moved to the
  SDK when a second plugin needs it).
- **Who may enroll:** mutual TLS with the camera's factory **Axis device ID** certificate (IEEE 802.1AR, AXIS Edge Vault),
  checked against the two Axis device ID root CAs (RSA, ECC; valid to 2060) shipped with the plugin (the camera sends the
  intermediates). No passwords, nothing to hand out. **And** the serial in the device ID certificate must be a device of
  the device list (at a site: a device of that site): a genuine Axis camera OADM does not manage gets nothing. **And**
  (since stage 6 of docs/spec/pki.md) the CA must be named by an **EST sign-in CA** policy of PKI (default: "Axis device ID"
  for new certificates, which renews too, and "OADM CA" for renewals; none, no PKI or policies that cannot be read: nothing
  signs in), and an **Enrollment** policy must hold the device and allow the profile (default "All devices", every
  profile). None holds it: 403 "No enrollment policy for this device."; a profile not allowed: the 404 of an unknown
  profile (no probing), listed in Enrollments with the policy that refused it.
- **Renewal with the current certificate** (maintainer decision 2026-10-25): for `simplereenroll` a camera also signs in
  with its current certificate when OADM issued exactly that certificate, whatever CA of PKI issued it (OADM CA, previous
  OADM CA, an imported CA with its key): valid now, its serial is the camera's **current** certificate in PKI's store (a
  pending one does not count), the same DER, not on the revocation list, not one of the Certificates list made here
  (`ICurrentCertificates.CurrentCameraAsync`, O(1) by serial). The camera comes from the store entry (serial -> camera),
  never from a name in the certificate; a certificate of the same CA issued outside OADM stays refused (401). Order in
  `EstEndpoint.SerialOfAsync`: the Axis device ID (an EST sign-in CA policy names it), then this rule (renewals only), then
  the EST sign-in CA policies' OADM CAs by the axis name; then, as before, the managed device, its site, the Enrollment
  policies, the revocation list and the record's device. New enrollments keep the EST sign-in CA policies alone.
- **Renewal** (`simplereenroll`): the client certificate must be the device ID or a valid certificate the OADM CA
  itself issued (chain element 1 is the OADM CA, never another CA below a root above an imported OADM CA) whose DNS
  names hold exactly one `axis-<serial>.local` serial, a managed device, and that PKI does not mark as not a device's
  (`PurposeOf` = `PkiCertificateAuthority.NotDevicePurpose`: created, signed or imported on PKI's Certificates card; 401
  like no identity); with a record of it, the record's device must
  be that device (a record of a device OADM no longer manages is forgotten: a camera removed and added again). A record
  is not needed: a camera whose answers got lost renews with an older certificate than the two the store keeps. A
  certificate on PKI's revocation list (`IDeviceCertificateAuthority.IsRevoked`, one lookup) is refused, for
  `simpleenroll` too: 403 "Certificate revoked on 2026-11-02 (Key compromised)" (`CertificateRevocation.Describe()`), listed
  in Enrollments; the Axis device ID is never revoked, so Set up gives the camera a new certificate. EST and
  PKI never take a reported host name or FQDN `axis-<12 hex>...` of another serial into a certificate, so a camera
  cannot name another camera (`EstEndpoint.NamesOtherCamera`, `CertificateNames.NamesOtherCamera`).
- **Not from strangers:** a request without a valid identity (401) is not listed in Enrollments and logged at debug level
  only; a 401 or 403 closes the connection, and so does every answer to a connection without a client certificate (one
  `cacerts`, never a slot held); at most 64 connections, 4 per address (IPv6 per /64), 2 minutes each, and at most 16 that
  have not shown a client certificate yet, with 10 s for the handshake and their one request; the client's chain is collected
  without downloads (`DisableCertificateDownloads`).
- **Names:** host names reported by the camera go into a certificate only when they are plain (letters, digits, `-`, dots;
  no `*`), only when a user with all devices added the device (`IDeviceInfo.AddedByAllDevices`) and only when no device
  group covers it (`IDeviceScopes.InAnyGroup`; docs/spec/device-scope.md "Host names in certificates"). For a device a
  user with a group added, the certificate and the camera's profile name only its stored IP address and
  `axis-<serial>.local` (CN = that address, or the axis name for a name address); for a covered device a user with all
  devices added, also the name address it was added with. EC keys only on P-256, P-384 or P-521.
- **Issuing:** the OADM CA of the PKI plugin signs (one CA for everything), through the SDK's plugin services: PKI offers
  "sign a device certificate" and "CA chain", EST uses them. Like PKI's `DeviceCertificateIssuer`, OADM sets subject and
  SANs from what it knows (the stored address, host name, FQDN, `axis-<serial>.local`; never the address a connection
  came from, never another camera's axis name), never from the CSR; EKU from the profile; key
  policy RSA >= 2048 / EC >= 256; SHA-1/MD5 CSRs refused. The answer carries the leaf **and the chain** (Axis refuses a
  leaf only), PKCS#7 certs-only, base64 wrapped at 76 characters. Every certificate goes into PKI's list of issued
  certificates with its use (`DeviceCertificateRequest.Purpose`: HTTPS, IEEE 802.1X, MQTT, RTSPS) and source EST, pending
  until the camera has it (decision 2026-10-25, `docs/spec/pki.md` "Current and pending"): once the 200 answer is written
  (`EstService` after `EstHttp.WriteAsync`, `ICertificateDelivery.DeliveredAsync`) HTTPS, RTSPS and MQTT certificates
  become current and the previous one of the camera and use is revoked; an IEEE 802.1X one only when the camera signs in
  to OADM's 802.1X server with it. An answer that cannot be written revokes the new certificate as unused. EST's own
  store takes the certificate only once the answer is written (an unsent one never pushes out the camera's older one), and
  a failure of PKI's delivered call is logged without losing the enrollment record (fix of 2026-10-25).
- **The server's TLS certificate** (decision 2026-10-25, docs/spec/pki.md "Server certificates"): the certificate PKI's
  EST server policy picks from the Certificates list, **one for this server and every site** (cameras dial an IP, there is
  no SNI): it names the server's addresses and host name and the site nodes' addresses toward the cameras. There is no
  Create in the policy: Turn on without one makes the shared one (10 years, ECC); another one comes from the Certificates
  tab (Create) and is chosen in the EST server policy. Never renewed or re-issued by itself; the EST tab shows no
  certificate row (its policy does) but warns
  when a site's node address or one of this server's addresses is not in it ("Site Parking is not in the certificate of
  the EST server. Create a certificate with its address on the Certificates tab and choose it in the EST server
  policy.", `PolicyTexts.AddressNotInCertificate` for an own address). A revoked one is never shown again, also not from
  the copy read before (`ServerCertificates` checks the revocation and the end by the server clock).
- Revocation: PKI's revocation list (above); no CRL, no OCSP yet. **Remove** in the dialog stops renewals.
- Logs: one line per request (no keys, no CSR bodies).
- Limits (`EstConnectionSlots`): 64 connections at once, 4 per address (per site: the same address at two sites is two
  cameras; IPv6 per /64, IPv4 mapped into IPv6 as IPv4), at most 16 of them before the handshake showed a client certificate
  (10 s for the handshake, and for the one request without a certificate); 20 requests and 30 s idle per connection,
  2 minutes per connection, headers 16 KB, body 64 KB. Answers: 401 no
  or no valid identity (also a CA no EST sign-in CA policy names for the operation), 403 not managed / other site / no
  enrollment policy / certificate of another camera, 400 request unreadable or refused, 404 unknown (also a profile the
  device's Enrollment policy does not allow), 503 no OADM PRO or no CA (Retry-After 600; "The CA of this profile cannot
  issue certificates." when the CA of the profile's issuing policy cannot issue).
- Issuing (decision 2026-10-25): a profile's certificates come from the CA its issuing policy names ("Issued by": the
  OADM CA or an imported CA with its key; `DeviceCertificateRequest.IssuerId`); `cacerts` and the answer carry that CA's
  chain. The EST sign-in CAs stay as they are: only the Axis device ID and OADM CAs (the current and previous ones) can be
  EST sign-in CAs, because EST reads a camera's serial from a certificate's name only where OADM vouches for it; an
  imported CA's key may also be used outside OADM, so a name in its certificate proves nothing. A camera with a certificate
  of an imported CA renews with that certificate when it is its current one in PKI's store ("Renewal with the current
  certificate"), else with its Axis device ID.
- The server's TLS certificate (`Serving/ServerCertificates`): the EST server policy's certificate with its key, loaded
  once and again when the policies change (PKCS#12 in memory, so Windows TLS can use the key); the TLS chain is its
  issuer's chain (`cacerts` answers the chain of the profile's issuing CA, above).

## Certificate renewals and pinning

A camera that renews its HTTPS certificate by EST presents a new certificate every period. OADM accepts a new device
certificate **without** Certificate changed when a CA of PKI issued it **directly** (the OADM CA, a previous OADM CA or an
imported CA with its key, `ITrustAnchors.SetRenewalIssuers`; never the roots above an imported CA, never another plugin's
anchors), it is valid now,
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
  Ended more than 30 days ago: dropped; a device OADM no longer manages: forgotten when the check finds its certificate
  due, or at once when its camera signs in again with a new device id (removed and added again). Stored as plugin records
  (`ctx.Records`, collection `certificates`, one row per certificate by serial number): a flush writes only the changed
  certificates, never the whole list. 4.1.0-rc.2 kept every certificate in one settings value `certificates`: loaded
  once, cut down, moved to the records and the settings value deleted.
- **Not renewed**: the newest certificate, **halfway through its lifetime plus `NotRenewedGrace` (2 days)**, or a tenth of the
  lifetime when that is shorter (1 year: day 184.5), without a newer one. Checked 1 minute after the start and
  then every hour; flagged once (`NotRenewedUtc`, persisted): the event "Certificate not renewed", a warning in the
  server log, the page reads its list again. A later renewal replaces the record and ends it.
- Never flagged (`RenewalStoppedUtc`): certificates of an `oadm_` profile removed with **Remove** in the dialog or by
  Set up (a renamed or turned-off profile), and certificates revoked in 4.1.0-rc.2 (`revokedUtc` when loaded; the
  setting `revoked` is deleted, so those cameras renew again).
- Set up counts the profiles that stay on the camera (another EST server's for other uses, kept network access
  profiles): it picks the most of the EST server's profiles that fit in two, preferring those that replace a profile
  (`EstSetupTask.Fitting`), else "leave no room ... Nothing was changed" before any write.
- **Two names per profile** (seen on 10.0.0.48, AXIS OS 12.11.77, 2026-10-23): the camera stores the certificate under
  the profile's id and **never over an older certificate of that name** ("simple enroll failed: Certificate is in use"
  in its log while the EST server answered 200), not after a change in place either (`PATCH` reported ENROLLED and kept
  the old one). A certificate stays bound to its use (web server, network access, MQTT) after its profile is removed;
  deleting it is refused ("Certificate is in use") until a certificate of another name takes the use, then it can be
  deleted. So OADM's profile is `oadm_<profile>` or `oadm2_<profile>` (`EstTaskIds.IsOadm`, `IdsOf`; the prefix
  `oadm2_` never comes from a profile name). Set up keeps the name of OADM's profile that is identical; one that differs
  is removed and added again, like a profile set up after **Remove**: `FreeNameAsync` takes the first name without a
  certificate (an older one no use holds is deleted first), else the other one; both held: that profile fails "keeps no
  name free", the others are set up. Once the new certificate is in, every `oadm_` / `oadm2_` certificate no profile
  uses is deleted when the camera lets it go (`DeleteFreedCertificatesAsync`; PKI names its certificates "OADM HTTPS
  ..."). `EstHardwareWriteTests` covers it (set up, again, another host name, Remove and set up again).
- Set up changes per profile: an identical profile stays (no new certificate, no web server restart); a written one is
  done only once a new certificate was issued for it **and the camera holds it** (the certificate under the profile's
  name has its fingerprint: the camera may report ENROLLED with the older one; "The EST server issued the certificate,
  but the camera did not store it." otherwise); an add whose answer got
  lost is checked and tried once more. IEEE 802.1X: its profiles stay untouched unless this run writes OADM's IEEE 802.1X
  profile (replace chosen and the profile fits). "Nothing to remove" is a warning (Done with warnings).
- Sites: a site that leaves the bound ones (removed, or its node reset and waiting to be replaced) loses its relay; its
  addresses stay until the hourly check finds no managed device there. A chosen interface that goes away while
  listening closes the listener ("Interface ... is not available"); failures of the hourly check and of the 30 s retry
  are logged.
- Robustness: a failed write of the store is logged and tried again; stopping never opens a listener or relay any more;
  a move of 4.1.0-rc.2 data cut short keeps the records' newer state.
- Scale: a per-device index (`_profilesOf`) keeps Stop and Forget O(1) per device (`RenewalTests`, Perf: 30,000 cameras
  in about 0.5 s); one read of the device list per check; at most 20 log lines per check, then one summary line.

## Setting up a camera (the deploy task)

Built in stage 3 (`Tasks/EstTasks.cs`), tested on 10.0.0.48. Device context menu, Security: **Certificate enrollment**
(`EstSetupTask`, one task id `oadm.est.setup`; the payload `SetupPayload` sets up, with the IEEE 802.1X choice below, or
removes the chosen profile ids); the profiles come from the EST server page. Devices at a site too (stage 4).

Compatibility first (CLAUDE.md device safety): AXIS OS **12.10+** (`CanRun`), and before the first write a fresh
`config/discover` with `cert` v1 and `cert-est` v1, an Axis device ID certificate of this camera's serial on the camera
(found by its chain, not its alias), and at least one profile whose service the camera has and that an
Enrollment policy allows the camera (none holds it: "No enrollment policy for this device. Add one on the PKI page.";
a profile not allowed is skipped with a warning). Others: Nothing was changed. Checked on 10.0.0.48 (P3265-V, 12.11.77): device ID certificates ECC P-256, RSA 2048 and RSA 4096, subject
`serialNumber` = the serial in lower case, issued by "Axis device ID Intermediate CA ECC 2" / "RSA 2" (in the shipped
bundles), valid to 9999; the camera starts with no EST profiles.

1. Check compatibility; the profiles: those of the EST server whose service the camera has, at most 2.
   `supportedServices` lists only the services no profile uses yet, so the services of the camera's profiles count too.
2. Read the camera's certificates: the device ID of the profile's key (ECC or RSA, the smallest key).
3. Install the CA certificates: the issuer chain of the certificate the EST server policy picks (fix of 2026-10-25: it
   may be a previous OADM CA or an imported CA, never simply the current OADM CA) with `cert` v1, matched by fingerprint
   (an existing alias is reused), new ones as "OADM CA <8 hex>" like PKI's; never deleting. The check step refuses when
   the EST server has no usable certificate (its status text), when PKI does not know its issuer ("The CA that issued
   the EST server's certificate is not in the CA list. Choose another certificate in the EST server policy.") and when
   the address the camera gets is not in it ("10.0.0.17 is not in the EST server's certificate. Create a certificate with
   it on the Certificates tab and choose it in the EST server policy."); Nothing was changed.
   With IEEE 802.1X on (read fresh in the check step, `Network.Interface.I0.dot1x.Enabled`; unknown counts as on; the
   dialog counts cameras not read yet too) the IEEE 802.1X profile is skipped with a warning (no lock-out): every
   profile on the camera that serves IEEE 802.1X, also one serving other uses too, stays untouched, and a wanted use such
   a profile serves is skipped with a warning,
   unless the payload says `replaceDot1x` (`SetupPayload`): the dialog (`EstSetupWindow`) shows, only when selected
   cameras have IEEE 802.1X on, the check box "Also replace the network access certificate of N cameras with IEEE 802.1X
   on" with the hint naming the CAs of the dot1x profile's IEEE 802.1X policy ("These cameras will check the RADIUS server
   with OADM Root CA. Only when your RADIUS server trusts OADM Root CA. Otherwise these cameras drop off the network.",
   `CameraProfileList.RadiusServerCa` / `NetworkAccessCa` of the read-only query; without them the general hint) (off by
   default); the task then logs that it was chosen. HTTPS
   comes last (its certificate restarts the web server); the reads while waiting are retried during that restart.
4. Same set as on the camera (id, server, CA aliases, device ID, services, SANs; `subject` is not compared): nothing is
   written. Otherwise remove those of OADM's profiles (`oadm_...`) and of the camera's profiles serving the same services
   that are not identical to a wanted one (others stay), and add the missing ones **one at a time**, each waited for:
   `POST /config/rest/cert-est/v1/profiles` with `id` = `oadm_<profile>` or `oadm2_<profile>` (see "Two names per
   profile"; the camera refuses `-` in ids, so profile names are letters, digits and `_`), `server` = `<address>:9454/.well-known/est/<profile>` (no scheme; the server's address
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
6. With the dot1x profile (decision 2026-10-25): **Install the RADIUS server CA** and **Set the RADIUS server CA**, so the
   camera checks the RADIUS server with the RADIUS server CA of the profile's IEEE 802.1X policy, as Enable IEEE 802.1X
   sets it (else a new OADM CA leaves the camera trusting the old one: TLS alert 48, seen 2026-10-25). The CA is found in
   the check step, only with IEEE 802.1X on (missing from the CA list: nothing is
   changed; with IEEE 802.1X off it is not needed). Run before the HTTPS profile is written (its certificate restarts the
   web server), else after the profiles. Only the CA list of the camera's EAP-TLS settings is written
   (`setWired8021XConfiguration` with on/off, identity, EAPOL version and client certificate as the camera has them,
   `NetworkInfoApi.SetCaCertificatesAsync`; network settings before 1.38, also on 12.10, do not bind certificates: there
   the CA certificates of the SOAP certificate set `EAPTLS_WIRED` change and the client certificate stays,
   `Dot1xSettings.SetCaCertificatesAsync`, docs/spec/pki.md "Older AXIS OS"); skipped without the network settings API, with IEEE 802.1X off or without a
   client certificate, "Already set" when nothing differs. Also when the profiles were already set up.
7. Not built yet: removing unused certificates OADM put there (never device ID certificates, their CAs, factory or
   public CAs).

Setting up and removing need the task permission "Set up certificate enrollment" (`enroll`, not for operators by
default). Remove removes the chosen profiles that the camera has (others are skipped, "Nothing was changed" when none);
the certificates stay until they end. No
rollback exists on the camera; the set-up is safe to run again (idempotent). Hardware test:
`tests/Oadm.Plugins.Est.Tests/EstHardwareWriteTests.cs` (`OADM_EST_HARDWARE_WRITE=1`, `OADM_EST_SERVER_ADDRESS`,
`OADM_DEV_CAMERAS`): a real EST server on this computer with a test CA, the real task on 10.0.0.48, then the camera back
as before (profile removed, previous web server certificate, test certificates deleted).

**The camera's own EST errors**: VAPIX documents an event `tnsaxis:CertificateManagement/EST/Error`, but 10.0.0.48
(AXIS OS 12.11.77) neither declares it nor sends it: probed 2026-10-22 with a profile whose server does not answer
(status stays `UNCONNECTED`) and with an EST server that refuses the camera (status `CONNECTED`); the event stream
carried only the audit log line of the profile change (`EstEventProbeTests`, HardwareWrite). So the set-up reads the
profile status instead, and OADM keeps no event stream open to the cameras for EST.

**Accepted as they are** (decision 2026-10-22): host names and FQDNs a camera reports go into its certificates whatever
their domain (only for devices a user with all devices added and no device group covers; only another camera's
`axis-<serial>` name is refused); PKI takes the IP addresses a device reports, only for devices no group covers; Set up
removes a profile of another EST server whole when it serves a use OADM sets up.

## License of the code

`plugins/Oadm.Plugins.Pki/Est` and `plugins/Oadm.Plugins.Pki.Client/Est` carry their own `LICENSE` (source-available: the source
can be read, using the EST server needs an OADM PRO license; changing or removing the license check is not allowed). The rest
of OADM, PKI included, stays Apache 2.0. The THIRD-PARTY notices name the part ("Except the part Est of the plugin oadm.pki").

## Stages

1. **Host and SDK** (built): `"pro": true` in plugin.json, the Plugins page lock and badge, turning PRO plugins off when
   the license ends (`docs/spec/plugins.md` "OADM PRO plugins"); plugin services, PKI offers `IDeviceCertificateAuthority`
   ("Plugin services"); accepting OADM-CA renewals without Certificate changed (`docs/spec/vapix.md`).
2. **EST server** (built): listener, TLS, HTTP/1.1, the three operations, device ID check (the Axis device ID CA bundles
   from axis.com, checksums checked), managed-device check, issuing through PKI, enrollments, events, the page; tests over TLS with a test device ID hierarchy. Recorded camera requests follow with stage 3.
3. **Deploy task** (built; since 2026-10-22 one task, Certificate enrollment, that sets up or removes) with the camera's `cert` / `cert-est` APIs; fixtures from a real camera, the
   write test on 10.0.0.48 passed. Open: removing unused certificates.
4. **Sites** (built): `ISites.OpenTcpRelayAsync` per bound site with set-up cameras (`EstService.ServeSiteAsync`, called
   by the set-up task with the node's address towards the camera, `LocalAddressTowardsAsync` on the device's route); the
   addresses per site are stored (plugin setting `sites`; decision 2026-10-25: the one certificate of the EST server
   policy names them, no certificate per site, the tab warns when a site is missing from it); a removed site's relay is released and its addresses dropped; a port change releases and
   reopens every relay; `plugin.json` `"siteSupport": "full"`, `sitePorts` TCP 9454 "EST server". The page lists the relays
   that do not run below Save ("EST server at <site>: waiting for approval in the OADM Site Node app"); the set-up's wait
   ends with that text when the relay does not run.
5. **Docs:** user docs page, plugin README, website PRO section.
6. **Policies** (built 2026-10-24, docs/spec/pki.md "Code (stage 6)"): Enrollment and EST sign-in CA policies of PKI replace
   "Sign in with" and the rule that every managed device may use every profile.
