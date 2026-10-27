# EST server (OADM PRO)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Code details: `plugins/Oadm.Plugins.Pki/Est/README.md`.

A PRO part of the PKI package (core plugin `oadm.est`, `plugins/Oadm.Plugins.Pki/Est`; `docs/spec/pki.md` "One plugin"). OADM
runs an EST server (RFC 7030) and sets up the cameras' own EST client, so every camera gets its certificates (HTTPS, IEEE
802.1X, MQTT, RTSPS) from a CA of PKI and **renews them by itself**, also at sites (through a relay of the site node). A
clean-room implementation from RFC 7030 and the Axis camera APIs. Who may enroll, which CA issues and the server's own
certificate are PKI's policies (EST server, Enrollment, EST sign-in CA and the issuing policies; `docs/spec/pki.md`
"Policies"); the server keeps no rules of its own.

## What the user sees

- **Turn on / Turn off** (`docs/spec/pki.md` "One plugin"): off by default; **Turn on** in the tab's off state
  (`ui:OffState`, "The EST server is off") stores the choice and starts the server (TCP 9454 opens), **Turn off** (left of
  Save, confirmed) stops it. Without OADM PRO the off state reads "The EST server needs OADM PRO" with
  `ProLicenseTexts.NeedsPro` ("Needs an OADM PRO license or trial.") and no Turn on. When the license ends the server stops:
  enrolled cameras keep their certificates until they expire, renewals are refused while it is off. The task EST -
  Certificate Enrollment fails while the server does not run ("The EST server is off. Turn it on on the EST server page.
  Nothing was changed." or "OADM PRO is not active. Nothing was changed.").
- **Tab "EST server"** of the PKI page: **Server** above **Profiles** in a left column (680 px, the same as the 802.1X tab:
  `ServerPageLayout`), **Enrollments** with Not renewed right, as high as the page; below 1,400 px of page width one column,
  Enrollments in a 480 px row below (`EstView.Arrange`).
  - **Server:** Listen on, Port, Enrollments kept; below it, in the warning style, why no camera can enroll ("Cameras cannot
    sign in: there is no EST sign-in CA policy.", "No camera can enroll: there is no Enrollment policy.", the store's own
    texts), the warning when an address is not in the server certificate (see "Server certificate") and the relays that do
    not run ("EST server at <site>: waiting for approval in the OADM Site Node app"). The Save row: the link "4 policies
    applied" (on a new server) left, the status left of Save ("Running on port 9454", "OADM PRO is not active.", "The PKI has
    no certificate authority.", the server certificate's problem from `docs/spec/pki.md` "Server certificates", or the
    listener's state, e.g. "Interface Ethernet 2 is not available").
  - **Profiles** (Add profile a flat toolbar button): name, use (HTTPS, IEEE 802.1X, MQTT, RTSPS), issuing policy (HTTPS
    certificate policies for HTTPS and RTSPS, IEEE 802.1X certificate policies for IEEE 802.1X and MQTT; by default the
    built-in one of its kind). At most one profile per use; every listed profile is on, a profile not wanted is removed
    (asked first, red Remove). The profile name stays what the camera uses in its URL (letters, digits and `_`: the camera
    refuses `-` in ids). Two ready-made: **HTTPS** (server certificates, policy "HTTPS") and **IEEE 802.1X** (client
    certificates, policy "IEEE 802.1X"). A profile whose policy cannot be read issues nothing (503 "The issuing policy of
    this profile cannot be read."). Without PKI's store the profile's own values count. A save keeps each profile's stored
    values (`ProfileRowViewModel.ToProfile`). Profiles that were off in OADM 4.1 are removed once by the update
    (`EstPolicies.RemoveOffProfilesAsync`, owner `oadm.est:off-profiles-2026-10-25` in PKI's converted list, so it also runs
    on a server already on 4.5.0-rc.1; one Information line per removed profile, its name only); their enrollments and
    certificates stay in the lists.
  - **Enrollments:** time (dimmed, `DataGridCell.dimmed`), device, profile, new or renewal, result, reason (500 newest,
    setting 100 to 5,000). **View certificate** (toolbar, or a double click on a row) shows the certificate an enrollment
    issued in the shared `ui:CertificateWindow`. The server keeps the certificate (DER) of every listed enrollment in the
    records collection `issued` by serial number and deletes it when its enrollment leaves the list or on Delete all
    (`Enrollment.SerialNumber`, `EstStore.IssuedAsync`, read one at a time through `IPluginRecords.GetAsync`); method
    `getCertificate` (everyone who sees the page) describes it (`CertificateDescriber`); older entries have none ("OADM no
    longer keeps this certificate."). Search in the page (device, profile, kind, result, reason; one pass and one grid reset
    over at most 5,000 rows, new rows under the search as they come); **Delete all** (red toolbar button, confirmed, Admin;
    every client's list empties: topic `enrollmentsCleared`).
  - **Not renewed** (only while there are any): device, profile, valid until, state (Not renewed, Expired), at most 500 with
    "Showing 500 of 1,204". No list of every certificate here: that is PKI's Certificates tab, which also deletes
    (`docs/spec/pki.md` "Certificates"). A certificate PKI no longer accepts renews nothing and is never flagged Not renewed.
    **Remove** in the task dialog stops renewals.
- **Task "EST - Certificate Enrollment"** (device context menu, Security; one task per device), its dialog like the
  Applications (ACAP) dialog: the chosen camera's enrollment profiles (read-only query `listProfiles`: profile, use, set up
  by OADM or Other, EST server, status Enrolled / No certificate / Server not reached), **Remove** (red, inline confirmation;
  any profile, also of another EST server) and **Set up**; runs are named "Set up certificate enrollment" or "Remove
  enrollment profile <id>". **The dialog stays open:** Set up and Remove start the task from the dialog (`RunTaskAsync`), the
  run line below the list shows the step ("Set up: Step 5/7 · Wait for the https certificate"; several cameras "3 of 12
  cameras done · 1 failed"), then the result as a status chip (done, done with warnings, failed with the camera's message;
  several cameras name up to 5 by address, "+N more in the tasks pane"), and the profiles are read again; the footer says
  Close. The runs also show in the tasks pane. Set up reads the camera clocks first (`docs/spec/pki.md` "Clock check before
  installing a certificate").
- **Event rules:** "Certificate enrolled", "Enrollment refused" (device, profile, reason), "Certificate not renewed" (device,
  profile, valid until).
- **Users with a device group** (`docs/spec/device-scope.md` "Server-wide services"): the page read only (Save and Delete all
  hidden; the server refuses them, `RequiresAllDevices`); Enrollments, Not renewed, View certificate, the certificate count
  and the listeners only of the group's devices (a listener of this server when the group has a device without a site, of a
  site when it has a device there); no policy count or policy problem; refusals of cameras OADM does not manage only for
  users with all devices. Live updates: a new enrollment goes to the pages that see its device; state, Delete all and Not
  renewed changes to users with all devices and to each group whose users read the page (with that group's rows).

## EST server

- **Operations:** `GET /.well-known/est/<profile>/cacerts`, `POST .../simpleenroll`, `POST .../simplereenroll`. No csrattrs,
  serverkeygen, fullcmc (404). An unknown profile: the same 404 everywhere (no probing). `cacerts` stays open to every
  profile that exists (no device is known then).
- **Port TCP 9454** (setting, 1025 to 65535, so no rights needed on Linux servers and nodes). TLS 1.2 or later, own listener
  (not the gRPC Kestrel): `ctx.Network.ListenTcpAsync` on the server's own network (every address, or with **Listen on** one
  interface: the overload with its addresses, no IPv6 link-local; the set-up gives local cameras that interface's address of
  their family, else "The EST server listens on Ethernet 2, which has no IPv4 address for this camera. Nothing was
  changed."; a gone interface: status "Interface Ethernet 2 is not available", tried again every 30 s) and
  `ctx.Sites.OpenTcpRelayAsync(site, port)` per site whose cameras are set up (the node app asks to allow the port, like
  NTP's). TLS and a small HTTP/1.1 parser on the raw stream in the plugin (the SDK has no HTTPS helper on `IStreamListener`;
  moved to the SDK when a second plugin needs it).
- **Who may enroll:** mutual TLS, and every check must pass:
  - The CA of the client certificate is named by an **EST sign-in CA** policy for the operation (one for new certificates
    allows `simpleenroll` and `simplereenroll`, one for renewals only `simplereenroll`). By default "Axis device ID" for new
    certificates and the first-start root CA under its real name for renewals. No PKI, policies that cannot be read or no
    such policy: nothing signs in (401, fail closed). The choices are PKI's (`docs/spec/pki.md` "Rules", EST sign-in CA).
  - With the camera's factory **Axis device ID** certificate (IEEE 802.1AR, AXIS Edge Vault): a chain to the Axis device ID
    root CAs (RSA, ECC; valid to 2060) shipped with the plugin (`plugins/Shared/DeviceId`; the camera sends the
    intermediates); the serial is the subject's serialNumber. No passwords, nothing to hand out.
  - With a certificate of a CA of an EST sign-in CA policy: valid now, issued by that CA itself (chain element 1 is that CA,
    never another CA below a root above it), its DNS names hold exactly one `axis-<serial>.local` serial, and PKI does not
    mark it as not a device's (`PurposeOf` = `PkiCertificateAuthority.NotDevicePurpose`: created, signed or imported on
    PKI's Certificates tab; 401 like no identity).
  - The serial is a device of the device list (at a site: a device of that site): a genuine Axis camera OADM does not manage
    gets nothing (403). With a record of the certificate, the record's device must be that device (a record of a device OADM
    no longer manages is forgotten: a camera removed and added again). A record is not needed: a camera whose answers got
    lost renews with an older certificate than the two the store keeps.
  - An **Enrollment** policy holds the device and allows the profile (default "All devices", every profile). None holds it:
    403 "No enrollment policy for this device."; a profile not allowed (or "No enrollment"): the 404 of an unknown profile,
    listed in Enrollments with "The enrollment policy PTZ does not allow the profile https." / "... allows no enrollment."
    (checked after the device and site, before the not-accepted list).
  - Not on PKI's list of certificates no longer accepted (`IDeviceCertificateAuthority.IsRevoked`, one lookup), also for
    `simpleenroll`: 403 "Certificate deleted in OADM" or "Certificate replaced by a newer certificate"
    (`Ca/NotAcceptedTexts`), listed in Enrollments. The Axis device ID is never on it, so Set up gives the camera a new
    certificate.
- **Renewal with the current certificate:** for `simplereenroll` a camera also signs in with its current certificate when
  OADM issued exactly that certificate, whatever CA of PKI issued it (the first-start root CA, a previous OADM CA, a created
  CA, an imported CA with its key): valid now, its serial is the camera's **current** certificate in PKI's store (a pending
  one does not count), the same DER, not on the not-accepted list, not one of the Certificates list made here
  (`ICurrentCertificates.CurrentCameraAsync`, O(1) by serial). The camera comes from the store entry (serial -> camera),
  never from a name in the certificate; a certificate of the same CA issued outside OADM stays refused (401). Order in
  `EstEndpoint.SerialOfAsync`: the Axis device ID (an EST sign-in CA policy names it), then this rule (renewals only), then
  the CAs of the EST sign-in CA policies by the axis name; then the managed device, its site, the Enrollment policies, the
  not-accepted list and the record's device. New enrollments keep the EST sign-in CA policies alone. A camera with a
  certificate of an imported CA thus renews with that certificate when it is its current one, else with its Axis device ID.
- **Not from strangers:** a request without a valid identity (401) is not listed in Enrollments and logged at debug level
  only; a 401 or 403 closes the connection, and so does every answer to a connection without a client certificate (one
  `cacerts`, never a slot held). The client's chain is collected without downloads (`DisableCertificateDownloads`).
- **Names:** host names reported by the camera go into a certificate only when they are plain (letters, digits, `-`, dots;
  no `*`), only when a user with all devices added the device (`IDeviceInfo.AddedByAllDevices`) and only when no device group
  covers it (`IDeviceScopes.InAnyGroup`; `docs/spec/device-scope.md` "Host names in certificates"). For a device a user with a
  group added, the certificate and the camera's profile name only its stored IP address and `axis-<serial>.local` (CN = that
  address, or the axis name for a name address); for a covered device a user with all devices added, also the name address
  it was added with. EST and PKI never take a reported host name or FQDN `axis-<12 hex>...` of another serial into a
  certificate, so a camera cannot name another camera (`EstEndpoint.NamesOtherCamera`, `CertificateNames.NamesOtherCamera`).
  EC keys only on P-256, P-384 or P-521.
- **Issuing:** the CA the profile's issuing policy names signs ("Issued by": the first-start root CA, a created CA or an
  imported CA with its key; `DeviceCertificateRequest.IssuerId`), with the policy's valid days; Set up asks the camera for
  the policy's key. EST uses PKI's CA service through the SDK's plugin services (`IDeviceCertificateAuthority`). Like PKI's
  `DeviceCertificateIssuer`, OADM sets subject and SANs from what it knows (the stored address, host name, FQDN,
  `axis-<serial>.local`; never the address a connection came from, never another camera's axis name), never from the CSR;
  EKU from the profile; key policy RSA 2048 or more / EC 256 or more; SHA-1 / MD5 CSRs refused. The answer carries the leaf
  **and the chain** of that CA (Axis refuses a leaf only), PKCS#7 certs-only, base64 wrapped at 76 characters; `cacerts`
  answers the same chain.
- **PKI's list:** every certificate goes into PKI's list of issued certificates with its use
  (`DeviceCertificateRequest.Purpose`: HTTPS, IEEE 802.1X, MQTT, RTSPS) and source EST, pending until the camera has it
  (`docs/spec/pki.md` "Current and pending"): once the 200 answer is written (`EstService` after `EstHttp.WriteAsync`,
  `ICertificateDelivery.DeliveredAsync`) HTTPS, RTSPS and MQTT certificates become current; an IEEE 802.1X one only when the
  camera signs in to OADM's 802.1X server with it. An answer that cannot be written makes the new certificate not accepted
  as unused. EST's own store takes the certificate only once the answer is written (an unsent one never pushes out the
  camera's older one); a failure of PKI's delivered call is logged without losing the enrollment record.
- **Used by:** EST reports the CAs of its EST sign-in CA policies that PKI still lists, plus the first-start CA, to PKI
  (`SetUsedBy`) at the start, on a policy change and on PKI's `Changed`, and clears it when it stops.
- Logs: one line per request (no keys, no CSR bodies).
- **Limits** (`EstConnectionSlots`): 64 connections at once, 4 per address (per site: the same address at two sites is two
  cameras; IPv6 per /64, IPv4 mapped into IPv6 as IPv4), at most 16 of them before the handshake showed a client
  certificate (10 s for the handshake, and for the one request without a certificate; `EstHost.HandshakeTimeout`, tests with
  real TLS on the system clock set 30 s); 20 requests and 30 s idle per connection (`EstHost.IdleTimeout`), 2 minutes per
  connection, headers 16 KB, body 64 KB, at most 32 trailer lines after a chunked body.
- **Answers:** 401 no or no valid identity (also a CA no EST sign-in CA policy names for the operation), 403 not managed /
  other site / no enrollment policy / certificate of another camera / no longer accepted, 400 request unreadable or refused,
  404 unknown (also a profile the device's Enrollment policy does not allow), 503 no OADM PRO ("OADM PRO is not active.") or
  no CA (Retry-After 600; "The CA of this profile cannot issue certificates." when the CA of the profile's issuing policy
  cannot issue).

## Server certificate

The certificate PKI's EST server policy picks from the Certificates list (`docs/spec/pki.md` "Server certificates"), **one
for this server and every site** (cameras dial an IP, there is no SNI): it names the server's addresses and host name and
the site nodes' addresses toward the cameras. Turn on without one makes the shared one (10 years, ECC); another one comes
from the Certificates tab and is chosen in the EST server policy. Never renewed or made again by itself.

- `Serving/ServerCertificates` loads it with its key once and again when the policies change (PKCS#12 in memory, so Windows
  TLS can use the key); the TLS chain is its issuer's chain. One on the not-accepted list is never shown again, also not from
  the copy read before (it checks that list and the end by the server clock).
- The tab shows no certificate row (the policy does) but warns when a site's node address or one of this server's
  addresses is not in it ("Site Parking is not in the certificate of the EST server. Create a certificate with its address
  on the Certificates tab and choose it in the EST server policy.", `PolicyTexts.AddressNotInCertificate` for an own
  address). After a Save with another **Listen on** interface the warning follows that interface's addresses at once.

## Certificate renewals and pinning

A camera that renews its HTTPS certificate by EST presents a new certificate every period. OADM accepts a new device
certificate **without** Certificate changed when a CA of PKI issued it **directly** (the first-start root CA, a previous
OADM CA, a created CA or an imported CA with its key, `ITrustAnchors.SetRenewalIssuers`; never the roots above an imported
CA, never another plugin's anchors), it is valid now, has serverAuth and names the same device (`axis-<serial>.local`), and
pins it again (`docs/spec/vapix.md`). Any other change still shows **Certificate changed**. This applies to every
certificate of PKI's CAs, also PKI's own HTTPS task.

## Renewals and Not renewed

How Axis cameras renew: **halfway through the validity** the camera asks for a renewal with its current certificate
(`simplereenroll`, it may reuse its key); when that fails it tries again **daily** until it succeeds. A camera checks about
daily, so certificates of a few days or less are not useful.

The camera sends no event when a renewal fails (see "The camera's own EST errors"), so the server watches its own records
(`EstStore`, `EstService.CheckRenewalsAsync`):

- **Records:** per device and profile the newest certificate and the one before it (a camera whose answer got lost renews
  with the previous one); a renewal or a new set-up pushes out the older ones; at most 2 x devices x profiles. Ended more
  than 30 days ago: dropped; a device OADM no longer manages: forgotten when the check finds its certificate due, or at once
  when its camera signs in again with a new device id (removed and added again). Stored as plugin records (`ctx.Records`,
  collection `certificates`, one row per certificate by serial number): a flush writes only the changed certificates, never
  the whole list. A settings value `certificates` (4.1.0-rc.2) is loaded once, cut down, moved to the records and deleted.
- **Not renewed:** the newest certificate, **halfway through its lifetime plus `NotRenewedGrace` (2 days)**, or a tenth of
  the lifetime when that is shorter (1 year: day 184.5), without a newer one. Checked 1 minute after the start and then
  every hour; flagged once (`NotRenewedUtc`, persisted): the event "Certificate not renewed", a warning in the server log, the
  page reads its list again. A later renewal replaces the record and ends it.
- Never flagged (`RenewalStoppedUtc`): certificates of an `oadm_` profile removed with **Remove** in the dialog or by Set up
  (a renamed or removed profile), and certificates revoked in 4.1.0-rc.2 (`revokedUtc` when loaded; the setting `revoked` is
  deleted, so those cameras renew again).
- Sites: a site that leaves the bound ones (removed, or its node reset and waiting to be replaced) loses its relay; its
  addresses stay until the hourly check finds no managed device there. A chosen interface that goes away while listening
  closes the listener ("Interface ... is not available"); failures of the hourly check and of the 30 s retry are logged.
- Robustness: a failed write of the store is logged and tried again; stopping never opens a listener or relay any more; a
  move of 4.1.0-rc.2 data cut short keeps the records' newer state.
- Scale: a per-device index (`_profilesOf`) keeps Stop and Forget O(1) per device (`RenewalTests`, Perf: 30,000 cameras in
  about 0.5 s); one read of the device list per check; at most 20 log lines per check, then one summary line.

## Setting up a camera

Device context menu, Security: **EST - Certificate Enrollment** (`Tasks/EstTasks.cs`, `EstSetupTask`, one task id
`oadm.est.setup`; the payload `SetupPayload` sets up, with the IEEE 802.1X choice below, or removes the chosen profile ids);
the profiles come from the EST server tab. Devices at a site too: the camera gets the node's address toward it
(`LocalAddressTowardsAsync` on the device's route), `EstService.ServeSiteAsync` stores it (plugin setting `sites`) and opens
the site's relay; a removed site's relay is released and its addresses dropped; a port change releases and reopens every
relay; the set-up's wait ends with the relay's text when the relay does not run. `plugin.json` `"siteSupport": "full"`,
`sitePorts` TCP 9454 "EST server".

Compatibility first (CLAUDE.md device safety): AXIS OS **12.10 or later** (`CanRun`), and before the first write a fresh
`config/discover` with `cert` v1 and `cert-est` v1, an Axis device ID certificate of this camera's serial on the camera
(found by its chain, not its alias), and at least one profile whose service the camera has and that an Enrollment policy
allows the camera (none holds it: "No enrollment policy for this device. Add one on the PKI page. Nothing was changed."; a
profile not allowed is skipped with the policy's reason as a warning; none left: "The enrollment policies allow none of the
profiles the camera supports. Nothing was changed."). Recorded on 10.0.0.48 (P3265-V, 12.11.77): device ID certificates ECC
P-256, RSA 2048 and RSA 4096, subject `serialNumber` = the serial in lower case, issued by "Axis device ID Intermediate CA
ECC 2" / "RSA 2" (in the shipped bundles), valid to 9999; the camera starts with no EST profiles.

1. **Check compatibility**; the profiles: those of the EST server whose service the camera has, at most 2 (the camera's
   limit: a third add is refused, "No more profiles can be added"). `supportedServices` lists only the services no profile
   uses yet, so the services of the camera's profiles count too. Set up counts the profiles that stay on the camera
   (another EST server's for other uses, kept network access profiles): it picks the most of the EST server's profiles that
   fit in two, preferring those that replace a profile (`EstSetupTask.Fitting`), else "The camera's other enrollment profiles
   leave no room (two at most). Remove one in Certificate enrollment first. Nothing was changed." before any write.
2. **Read the camera's certificates:** the device ID of the profile's key (ECC or RSA, the smallest key).
3. **Install the CA certificates:** the issuer chain of the certificate the EST server policy picks (any CA of the list) with
   `cert` v1, matched by fingerprint (an existing alias is reused), new ones as "OADM CA <8 hex>" like PKI's; never deleting.
   The check step refuses when the EST server has no usable certificate (its status text), when PKI does not know its issuer
   ("The CA that issued the EST server's certificate is not in the CA list. Choose another certificate in the EST server
   policy. Nothing was changed.") and when the address the camera gets is not in it ("10.0.0.17 is not in the EST server's
   certificate. Create a certificate with it on the Certificates tab and choose it in the EST server policy."). With IEEE
   802.1X on (read fresh in the check step, `Network.Interface.I0.dot1x.Enabled`; unknown counts as on; the dialog counts
   cameras not read yet too) the IEEE 802.1X profile is skipped with a warning (no lock-out): every profile on the camera
   that serves IEEE 802.1X, also one serving other uses too, stays untouched, and a wanted use such a profile serves is
   skipped with a warning, unless the payload says `replaceDot1x`: the dialog (`EstSetupWindow`) shows, only when selected
   cameras have IEEE 802.1X on, the check box "Also replace the network access certificate of N cameras with IEEE 802.1X on"
   (off by default, no hint below it); the task then logs that it was chosen. HTTPS comes last (its certificate restarts the
   web server); the reads while waiting are retried during that restart.
4. **Remove the camera's enrollment profiles** that differ, then add: the same set as on the camera (id, server, CA aliases,
   device ID, services, SANs; `subject` is not compared) writes nothing. Otherwise remove those of OADM's profiles and of the
   camera's profiles serving the same services that are not identical to a wanted one (others stay), and add the missing
   ones **one at a time**, each waited for: `POST /config/rest/cert-est/v1/profiles` with `id` = `oadm_<profile>` or
   `oadm2_<profile>` (see "Two names per profile"), `server` = `<address>:9454/.well-known/est/<profile>` (no scheme; the
   server's address toward the camera, `LocalAddressTowardsAsync`), `caCertificates` (the aliases of step 3), `certificate`
   = the device ID alias, `services` (`WEBSERVER` for HTTPS, `NETAUTH` for IEEE 802.1X, `MQTT`, `RTSPS`), `subject.cn` (the
   device's address) and `subjectAlternativeNames` (`IP:` the address, `DNS:` host name and `axis-<serial>.local`). An
   identical profile stays (no new certificate, no web server restart); an add whose answer got lost is checked and tried
   once more. IEEE 802.1X: its profiles stay untouched unless this run writes OADM's IEEE 802.1X profile (replace chosen and
   the profile fits). "Nothing to remove" is a warning (Done with warnings).
5. **Wait for the certificate:** until the camera reports **ENROLLED** (poll every 3 s, up to 2 minutes; a refusal the EST
   server recorded for this device and profile ends the wait at once with its reason; after 2 minutes the profile's status
   says why: `UNCONNECTED` = the camera did not reach the server ("could not reach the EST server ... TCP <port>"),
   `CONNECTED` = it reached the server but got no certificate). A written profile is done only once a new certificate was
   issued for it **and the camera holds it** (the certificate under the profile's name has its fingerprint: the camera may
   report ENROLLED with the older one; else "The EST server issued the certificate, but the camera did not store it. The
   previous certificate stays in use."). The camera stores the certificate under the profile id (`oadm_https`) and installs
   the CA from the answer by itself (alias = the CA's common name). **Pin the new HTTPS certificate:** with HTTPS on
   (`HttpsEnabled`) the web server switches to the new certificate a few seconds later and restarts (connections drop); the
   task pins it (`UpdateDeviceTlsAsync`, again for up to 60 s while the web server restarts; a warning when it does not
   happen). Only when OADM talks HTTPS to the camera: the task never changes how OADM connects.
6. **Install the RADIUS server CA** and **Set the RADIUS server CA** (with the dot1x profile), so the camera checks the
   RADIUS server with the RADIUS server CA of the profile's IEEE 802.1X policy, as Enable IEEE 802.1X sets it (else a camera
   whose RADIUS server certificate now comes from another CA keeps trusting the old one: TLS alert 48). The CA is found in
   the check step, only with IEEE 802.1X on (missing from the CA list: nothing is changed; with IEEE 802.1X off it is not
   needed). Run before the HTTPS profile is written (its certificate restarts the web server), else after the profiles. Only
   the CA list of the camera's EAP-TLS settings is written: `setWired8021XConfiguration` with identity, EAPOL version and
   client certificate as the camera has them, off and then on again so the camera authenticates anew
   (`NetworkInfoApi.SetCaCertificatesAsync`, `docs/spec/pki.md` "802.1X off, then on"); network settings before 1.38, also
   on 12.10, do not bind certificates: there the CA certificates of the SOAP certificate set `EAPTLS_WIRED` change and the
   client certificate stays (`Dot1xSettings.SetCaCertificatesAsync`, `docs/spec/pki.md` "Older AXIS OS"). Skipped without
   the network settings API, with IEEE 802.1X off or without a client certificate; "Already set" when nothing differs. Also
   when the profiles were already set up.
7. Not built yet: removing unused certificates OADM put there (never device ID certificates, their CAs, factory or public
   CAs).

Setting up and removing need the task permission "Set up certificate enrollment" (`enroll`, not for operators by default).
Remove removes the chosen profiles that the camera has (others are skipped, "Nothing was changed" when none); the
certificates stay until they end. No rollback exists on the camera; the set-up is safe to run again (idempotent).

**Two names per profile** (seen on 10.0.0.48, AXIS OS 12.11.77): the camera stores the certificate under the profile's id
and **never over an older certificate of that name** ("simple enroll failed: Certificate is in use" in its log while the
EST server answered 200), not after a change in place either (`PATCH` reported ENROLLED and kept the old one). A
certificate stays bound to its use (web server, network access, MQTT) after its profile is removed; deleting it is refused
("Certificate is in use") until a certificate of another name takes the use. So OADM's profile is `oadm_<profile>` or
`oadm2_<profile>` (`EstTaskIds.IsOadm`, `IdsOf`; the prefix `oadm2_` never comes from a profile name). Set up keeps the name
of OADM's profile that is identical; one that differs is removed and added again, like a profile set up after **Remove**:
`FreeNameAsync` takes the first name without a certificate (an older one no use holds is deleted first), else the other
one; both held: that profile fails "The camera still uses its older certificates of the https profile and keeps no name
free for a new one. The other profiles were set up.". Once the new certificate is in, every `oadm_` / `oadm2_` certificate
no profile uses is deleted when the camera lets it go (`DeleteFreedCertificatesAsync`; PKI names its certificates "OADM
HTTPS ...").

**The camera's own EST errors:** VAPIX documents an event `tnsaxis:CertificateManagement/EST/Error`, but 10.0.0.48 (AXIS OS
12.11.77) neither declares it nor sends it (probed with a profile whose server does not answer and with an EST server that
refuses the camera; `EstEventProbeTests`, HardwareWrite). So the set-up reads the profile status instead, and OADM keeps no
event stream open to the cameras for EST.

**Accepted as they are:** host names and FQDNs a camera reports go into its certificates whatever their domain (only for
devices a user with all devices added and no device group covers; only another camera's `axis-<serial>` name is refused);
PKI takes the IP addresses a device reports, only for devices no group covers; Set up removes a profile of another EST
server whole when it serves a use OADM sets up.

**Hardware test:** `tests/Oadm.Plugins.Est.Tests/EstHardwareWriteTests.cs` (`OADM_EST_HARDWARE_WRITE=1`,
`OADM_EST_SERVER_ADDRESS`, `OADM_DEV_CAMERAS`): a real EST server on this computer with a test CA, the real task on
10.0.0.48 (set up, again, another host name, Remove and set up again), then the camera back as before (profile removed,
previous web server certificate, test certificates deleted).

## License of the code

`plugins/Oadm.Plugins.Pki/Est` and `plugins/Oadm.Plugins.Pki.Client/Est` carry their own `LICENSE` (source-available: the
source can be read, using the EST server needs an OADM PRO license; changing or removing the license check is not
allowed). The rest of OADM, PKI included, stays Apache-2.0. The THIRD-PARTY notices name the part ("Except the part Est of
the plugin oadm.pki").
