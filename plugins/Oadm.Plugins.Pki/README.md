# PKI (`oadm.pki`)

Core plugin (+ `.Client`), rail page **PKI** (group Security, icon `shield`), always on. Since OADM 4.5 the package also
holds the core plugins **EST server** (`oadm.est`, `Est/`, README there) and **802.1X server** (`oadm.radius`, `Radius/`,
README there), each with its rail page and its own source-available `LICENSE`; the rest stays Apache 2.0
(`docs/spec/pki.md` "One plugin"). One server assembly (`Oadm.Plugins.Pki.Server.dll`, namespaces `Oadm.Plugins.Pki`,
`Oadm.Plugins.Est`, `Oadm.Plugins.Radius`), one client assembly, one `plugin.json`. Only the two servers need OADM PRO
(`Servers/ServerSwitch.cs`: Turn on / Turn off, the stored choice `serverOn`, started and stopped with the license); PKI's
own parts never do. The core plugins keep their 4.1 ids, so their settings, records, permissions and events keep their
keys; a 4.1 folder `oadm.est` or `oadm.radius` is skipped ("Merged into PKI") and its on/off choice taken over once. Device API research:
`tests/Oadm.Plugins.Pki.Tests/Fixtures/`. Part 1: the CA, its page and the server's trust;
part 2: the contributed Security tasks (below), and the OADM CA service (`PkiCertificateAuthority`, offered as
`IDeviceCertificateAuthority` through `ICorePluginContext.Services` while the plugin runs; the EST server signs with it),
the only writers of the list of issued certificates (see "Issued certificates and revocation" below).
- CA store (plugin settings): `ca` (id = SHA-256 of the certificate, source generated/imported, certificate and chain PEM,
  key as PKCS#8 PEM encrypted with `ICorePluginContext.Secrets`, purpose `pki:ca:<id>`), `previousCas` (public parts, at
  most 10, newest first, expired ones dropped), `config`, `existingImported` (the one pass over the devices ran; below);
  `issued` of OADM 4.1 moves into the plugin records at the first start (below). Without `Secrets` the plugin keeps no CA
  ("CA cannot be stored on this server"); a key that cannot be decrypted (other master key) is the error state "CA key
  cannot be read" with Generate / Import only, nothing is overwritten.
- First start: the default CA "OADM Root CA <machine name>" in the background (status "Creating the certificate
  authority"). Generate (`CaGenerator`, `CertificateRequest` only): RSA 4096, SHA-256 PKCS#1 v1.5, 16 random positive
  serial bytes, NotBefore now - 5 min, BasicConstraints CA (critical), KeyUsage keyCertSign + cRLSign (critical), SKI +
  AKI, 1..30 years (default 10), off the request thread.
- Import (`CaImporter`): PKCS#12 + password (also the Back up file) or PEM certificate(s) + key (in the file or a separate
  PEM: PKCS#8, PKCS#1, SEC1, encrypted PKCS#8), at most 1 MB, password never stored or logged. Checks with field errors
  (`File`, `Password`, `KeyFile`, `KeyPassword`): readable / password right, exactly one certificate with a matching key,
  a CA (keyCertSign when KeyUsage is present), RSA >= 2048 or ECDSA P-256 / P-384, valid now and NotAfter >= now +
  device validity + 1 year ("This CA expires on <date>, before certificates it would issue."). The other certificates on
  the issuer path become the chain.
- Replace (generate or import): without `confirmed` and with an existing CA the reply is `needsConfirmation` with
  `devicesWithCurrentCa` (also for 0 devices, and only after all checks passed, so the page asks after the field
  checks); the page asks "N devices have certificates from the current CA. They keep working until they are renewed.
  Replace the CA?" (only "Replace the CA?" for 0). The old CA moves to
  `previousCas`, the anchors are updated, a `state` event is published.
- Trust anchors: the active CA, its chain and the previous CAs (+ chains) go to `ctx.TrustAnchors`, so device
  certificates they issued are **Trusted** in the device grid without an OS change (lazy, next full refresh).
- Export `.crt` (PEM, with the chain) / `.cer` (DER, CA only), previous CAs as well; Back up = PKCS#12 with key and chain,
  AES-256-CBC + SHA-256, 100,000 iterations, password >= 8 (twice in the dialog), warning popup, logged "CA backup exported".
- OS trust store (`TrustStore/`, `TrustStoreInstallers.ForServer` / `.ForClient`, one class per OS behind
  `IProcessRunner` and `IMachineRootStore`, 30 s per tool, output to the log): Windows LocalMachine Root (client without
  admin: `certutil -addstore Root` with runas), Debian/Ubuntu `/usr/local/share/ca-certificates/oadm-<fp16>.crt` +
  `update-ca-certificates`, RHEL/Fedora `/etc/pki/ca-trust/source/anchors` + `update-ca-trust extract`, SUSE
  `/etc/pki/trust/anchors` + `update-ca-certificates` (client without root: one `pkexec /bin/sh -c` prompt, without pkexec
  the error shows the exact `sudo` command), macOS System keychain `security add-trusted-cert -d -r trustRoot` (client:
  `osascript ... with administrator privileges`), check `find-certificate -Z -a`, remove `delete-certificate -Z <sha1>`.
  Texts: "Permission denied: the server must run as administrator / root", "No supported certificate tool found
  (update-ca-certificates or update-ca-trust)", "<tool> failed: <first line>", "The installation was cancelled.".
  Tests use fakes only; nothing ever touches a real trust store.
- Settings (`config`, `PkiValidation` shared by server and page): `deviceCertValidityDays` 365 (1..3650, shortened to the
  CA's end when issuing, logged), `expiryWarningDays` 30 (1..365; since OADM 4.5 only read once: it becomes the Expiry
  warning policy "All devices"), `dot1x.eapolVersion` 1|2, `dot1x.identity`
  mac|hostName|custom, `dot1x.customIdentity` (1..64 printable, placeholders `{serial}` `{hostName}` only),
  `dot1x.radiusCa` oadm|imported + `radiusCaPem` (`importRadiusCa` checks a PEM/DER CA certificate, Save stores it).
- Page methods (`PkiMethods`, camelCase, errors as `{errors: {Field: message}}` or `{error}`): `getState` (status, ca,
  previousCas, config, serverTrustInstalled (checked every call), counts, radiusCa, `simulated` in fake mode), `generate`,
  `import`, `previewReplace`, `exportPublic`, `exportPrevious`, `removePrevious`, `backup`, `installServerTrust`,
  `saveSettings`, `importRadiusCa`, `getCertificates`, `getCertificate`, `revoke`, `revokeDevice` (below); event `state`
  after every change, `certificates` (payload `{}`) when the list of issued certificates or the revocation list changed.
- Page (since stage 3 of `docs/spec/pki.md`, one page without tabs, `HasOwnCards`, subtitle "Issues device certificates
  for HTTPS and IEEE 802.1X."): Certificate authorities | Policies side by side from 1,280 px of page width (stacked
  below), then Certificates (below), then Device certificates (the validity) and IEEE 802.1X (EAPOL version, EAP identity
  + custom field, RADIUS server CA with Import / View, one Save for both cards at the end of the IEEE 802.1X card, inputs
  460 px wide). Certificate authorities: Generate new CA and Import CA (a menu: "Own CA with its key" replaces the OADM CA,
  "CA certificate only" adds a row) in the header; the OADM CA's chips (status "CA valid until <date>" ok / "CA expires in
  N days" warning (within the longest Expiry warning) / "CA expired", "CA key cannot be read" error; the trusted root store
  of the server and of this computer); the list (Name, Kind, Valid until, Used by: the Allow CA policies "802.1X: OADM CA
  (only devices OADM manages)" and the services; View for every row, Export for a previous CA, Remove red for imported and previous
  CAs, confirmed); Install in trusted root store, Export public certificate (PEM / DER menu) and Back up below it.
  Policies: see "Policies". Dialogs `GenerateCaWindow`, `ImportCaWindow` (FileRow, key
  FileRow only for a certificate without key), `BackupWindow`: `ValidatingViewModel`, errors under the fields, the
  confirmation popups owned by the dialog. Last export folder per client in
  `LocalApplicationData/Oadm/plugins/oadm.pki/client.json`. Fake mode (`FakeOadmApi.Pki.cs`): in-memory CA + one previous
  CA, server store simulated, this computer never touched.
- Tests: `tests/Oadm.Plugins.Pki.Tests` (generator extensions, importer formats and every check, service with real secret
  protector, event hub and anchor registry: default CA, restart, other master key, no secrets, replace + confirmation,
  previous CAs cap/expiry/remove, export, backup restore, settings, RADIUS CA, status texts; trust store installers per
  OS with the fake runner; page view model incl. dialogs; fake mode; headless screenshots `pki-page.png`,
  `pki-page-imported-intermediate.png`, `pki-generate-dialog.png`, `pki-import-dialog-errors.png`, `pki-backup-dialog.png`;
  the list of issued certificates and revocation: `IssuedRegistryTests`, `RevocationTests`, `ExistingCertificateImportTests`,
  `CertificatesTabTests` (+ `pki-certificates.png`, `pki-revoke-confirm.png`, `pki-certificate-window.png`), `BackupTests`
  (a restore on another server over the real database), Perf `IssuedScaleTests` (30,000 devices x 4 certificates));
  `tests/Oadm.Core.Tests/Vapix/TrustAnchorTests.cs` (anchored leaf / intermediate Trusted, self-signed stays, registry,
  lazy re-rating in `CertificatePinning`, host context).

## Certificate authorities (docs/spec/pki.md)

- `PkiService.Authorities()` (the CA service's `Authorities`): the OADM CA (`oadm`, `Issues` while its key is readable),
  the previous OADM CAs not ended (`oadm-previous:<sha256>`, CA certificate then its chain), the Axis device ID (`axis-device-id`,
  the PEMs of `plugins/Shared/DeviceId` embedded, roots first) and the imported CAs (`imported:<sha256>`, oldest first).
  Built once per change of the lists (reference check, and again when a previous CA ends). `CaChanged` (the service's
  `Changed`) after a replace, a removed previous CA, Import CA and Remove.
- `SetUsedBy(serviceId, serviceName, ids)`: in memory per service (each service tells it again after a start), an empty
  list clears it; a change publishes the state (no `Changed`). Shown as "Used by" (names in order, "-" for none).
- Imported CAs: setting `importedCas` (public PEM only, at most 50, part of the backup). `importAuthority` (PEM or DER,
  one certificate; errors under the file `AuthorityFile`: not a certificate, larger than 1 MB, contains a private key, not
  a CA, expired, already in the list) and `removeAuthority` (imported only; refused while used: "Used by the 802.1X
  server. Remove it there first."). Both Admin ("manage", all devices, audited).
- Page: the card **Certificate authorities** (see "Page" above; **Import CA** > "CA certificate only" opens
  `ImportAuthorityWindow`). `removeAuthority` is also refused while a policy names the CA ("Used by the policy Contoso.
  Change or delete the policy first."). Tests: `AuthorityTests`, `PolicyStoreTests`.

## Policies (docs/spec/pki.md "Policies", "Code (stage 3)")

- `Policies/`: `PolicyModels` (contract), `PolicyRules`, `PolicyMatcher`, `PolicyStore` (setting `policies`, part of the
  backup), `ExpiryEvents`; `PkiService.Policies.cs` (the methods, PKI's own conversion, Revoke on save and every minute,
  the expiry days handed to the host). Kinds of stage 3: 802.1X server (one only), Allow CA, VLAN, Block, Revoke, Expiry
  warning. The 802.1X server inside PKI reads the store in process (`IPluginServices`); `Radius/Server/RadiusPolicies.cs`
  converts its settings once.
- Page: card **Policies** (`PoliciesViewModel`): the filter (All, Certificates, 802.1X, Revoke) as a segmented switch, rows
  (Name, Kind, Summary with the warning of a later overlap, Devices, Edit and Delete icon buttons), **Add policy**; the
  editor (`PolicyEditorViewModel`, `PolicyFormView` on the shared `DevicePickerWindow`; the device part only for kinds with
  devices and not "All devices"): Name, Kind (a new policy only) with VLAN ID or Days on its row, Devices ("All devices" or
  "Chosen devices"; not for Revoke), Allow CA's CA and "Only devices OADM manages",
  the 802.1X server's Minimum TLS version, Cipher suites (Reset to default), Largest EAP packet, Reauthentication. A Revoke
  policy asks first with `countRevocations`.
- Events "Certificate expires soon", "Certificate expired" and their Any device twins (`ExpiryEvents`, group
  "Certificates", the ids of OADM 4.1's built-in events).
- Tests: `PolicyStoreTests`, `ExpiryEventTests`, `PolicyPageTests` (+ headless `pki-page-1700.png`, `pki-page-1000.png`,
  `pki-policy-vlan.png`, `pki-policy-allow-ca.png`, `pki-policy-server.png`, `pki-policy-expiry.png`,
  `pki-policy-revoke-question.png`), Perf `PolicyPerfTests` (30,000 devices).

## PKI Security tasks (part 2, contributed by `oadm.pki`)

Clock check (docs/spec/pki.md; `plugins/Shared/DeviceClock/CameraClock.cs`, linked into PKI and EST, `DeviceClock` uses it):
the Enable HTTPS and Enable IEEE 802.1X dialogs (`ClockCheck`, after the 802.1X confirmation) read the camera clocks
through the read-only query `readClock` (8 at a time, 10 s each, progress window with Cancel; unreadable cameras left out)
and ask with `ui:MessageWindow` Yes / No when a camera is more than 5 s behind the certificate's start (5 min back); Yes
puts `{"clockConfirmed":true}` in the payload. Step "Check device clock" (802.1X, renew, manual install): ahead is fine;
behind beyond the start fails "The device's time is 9 min 12 s behind. The certificate becomes valid on the device only in
4 min 12 s. Set the device to use NTP. Nothing was changed." unless confirmed (then a Warning log line). Tests: `ClockTests`.

Context menu group **Security** (icon key), no toolbar, `CanRun` on cached data only: AXIS OS >= 11.11 from the firmware
version (+ network-settings 1.x in `Apis` for 802.1X). Device APIs (`Device/`): REST `cert` v1 (`CertApi`: `config/discover`
must list `cert.v1` released, read fresh before the first write, cached 10 min for queries; else "Needs AXIS OS 11.11 or
later. Nothing was changed."), SOAP web server TLS (`WebServerTls`: `aweb:Get/SetWebServerTlsConfiguration` at
`/vapix/services`, ciphers sent back as read), network_settings.cgi (`NetworkInfoApi`: getNetworkInfo, setWired8021X
Configuration), time.cgi getDateTimeInfo (`DeviceClock`, HTTP Date header as fallback).
- Issuing (`CertificateDeployment.IssueAsync`, `DeviceCertificateIssuer`): key on the device (`create_certificate` RSA-2048 in
  the default keystore) -> `get_csr` -> OADM signs (its own subject `CN=<OADM address>`, SAN IP addresses, host name, FQDN,
  `axis-<serial>.local`, the reported ones only for a device no group covers; never the CSR's; CSR key RSA >= 2048 or EC; EKU serverAuth / clientAuth; KeyUsage digitalSignature +
  keyEncipherment; AKI = CA SKI; 16 random serial bytes; validity `deviceCertValidityDays` from now - 5 min, capped at the
  CA) -> `PATCH certificates/<alias>`; the entry goes into `issued`. A failure after the key was created deletes the
  unfinished key again. Aliases `OADM HTTPS|802.1X <yyyyMMdd-HHmmss>`, CA `OADM CA <8 hex>`, RADIUS CA `OADM RADIUS CA <8
  hex>`, manual installs `OADM import <ts>`, percent-encoded in URLs. CA certificates are matched by fingerprint (an
  existing alias is reused). "Remove previous OADM certificate" deletes older OADM certificates of the purpose that
  nothing uses (alias prefix + registry serial or the CA as issuer), never others.
- **Enable HTTPS** (`oadm.pki.https-enable`, dialog = the clock check only, name "Enable HTTPS"): Check compatibility, Check device clock, Read web server
  settings, Read network settings (Skipped without network-settings), Install CA certificate (Skipped "Already
  installed"), Create key on the device, Get certificate request, Sign certificate, Install certificate, Switch web server
  to the new certificate (policy kept; HTTP only becomes HTTP and HTTPS), Verify HTTPS (`ctx.UpdateDeviceTlsAsync("https",
  fingerprint)`, retried up to 60 s while the web server restarts: OADM pins the new certificate, scheme https), Remove
  previous OADM certificate.
- **Disable HTTPS** (`oadm.pki.https-disable`, confirmation "Video systems that use HTTPS lose the connection to these
  devices.", name "Disable HTTPS"): Check compatibility (fails when `Network.HTTP.AuthenticationPolicy=basic`: OADM sends no
  Basic over HTTP), Read web server settings, Set HTTP only (policy `Http`, certificates stay; Skipped "Already HTTP only"),
  Verify (`UpdateDeviceTlsAsync("http")`).
- **Enable IEEE 802.1X** (`oadm.pki.dot1x-enable`, confirmation "Devices on ports that enforce 802.1X become
  unreachable if authentication fails.", name "Enable IEEE 802.1X"): Check compatibility (CA chain must end in a root, the
  RADIUS CA of the page, cert v1, network-settings with `wired.8021X`, identity 1..128), Check device clock (below), Install CA certificates (RADIUS
  server CA + the OADM CA chain), Create key / Get request / Sign / Install certificate, Set 802.1X configuration (enabled,
  EAP-TLS, identity MAC = serial / host name / custom with `{serial}` `{hostName}`, EAPoLv<n>, certClient, certsCA = RADIUS
  CA aliases), Verify 802.1X settings (read back, mismatch = Warning), Remove previous OADM certificate.
- **Disable IEEE 802.1X** (`oadm.pki.dot1x-disable`, name "Disable IEEE 802.1X"): Check compatibility, Set 802.1X off
  (Skipped "Already off"), Verify; certificates stay.
- **Renew certificates** (`oadm.pki.renew`, name "Renew certificates"): Check compatibility, Read certificates, Read web
  server settings, Read network settings, then per purpose whose current certificate is OADM's the enable flow with
  prefixed steps ("HTTPS: Create key on the device", "IEEE 802.1X: ..."; 802.1X adds Check device clock and runs only
  while 802.1X is on); otherwise one Skipped step "Renew HTTPS certificate" / "Renew IEEE 802.1X certificate" with the
  reason ("No OADM HTTPS certificate on this device").
- **View certificates** (`oadm.pki.view`, dialog only, never a task) and **Delete certificates**
  (`oadm.pki.delete`): `CertificatesWindow` (virtualized DataGrid grouped Client / Server / CA / devices that could not be
  read; MAC address, Address, Name, Issued by, Issued to, Valid to, In use (HTTPS, 802.1X), Source OADM / Other;
  SearchBox; Refresh), read through the query `listCertificates` (read-only, at most 4 devices at a time, progress row,
  rows appear progressively, list replaced as a whole at most every 300 ms). At most 5,000 devices per window
  (`CertificatesViewModel.MaxDevices`: every device is read, 4 at a time, about 190,000 rows at 5,000; 30,000 would take
  hours): a larger selection gets "Choose at most 5,000 devices." before anything is read. Delete mode: check boxes; in use and Axis
  factory (802.1AR) certificates greyed with a tooltip; Select all / none (O(n)); confirmation popup; payload = aliases per
  device. Task steps: Check compatibility, Read certificates (validates every choice before the first delete: exists, not
  in use, not factory), Delete certificate <alias> (one each), Verify. Names "Delete certificate <alias>" / "Delete N
  certificates" (distinct aliases).
- **Install certificates** (`oadm.pki.install`): `InstallCertificatesWindow`: Use for (HTTPS / IEEE 802.1X / CA
  certificates only), .pfx / .p12 files (one password for all, `CertificateFiles`: wrong password under the field), table
  File, Certificate, Device, Status chip; a file must match exactly one selected device by MAC (serial in any notation, also
  `axis-<serial>`), IP or host name / FQDN in CN or SAN, one file per device; problems block Install. Install confirms,
  uploads (`UploadAsync`) and sends `{purpose, password, files[{deviceId, fileId, fileName}]}` (CA only: deviceId empty =
  every device). Steps: Check compatibility, Read certificate file, then HTTPS: Read web server settings, Install
  certificate (`install_from_pkcs12`), Switch web server, Verify HTTPS; 802.1X: Check device clock, Install CA
  certificates, Install certificate, Set 802.1X configuration, Verify 802.1X settings; CA only: Install CA certificates.
  A device without a file ends Done with warnings, nothing sent. Name "Install certificate <file>" / "Install N
  certificates".
- **Install CA certificates** (`oadm.pki.install-ca`, icon `upload`, install one or more CA
  certificates on many devices, no OADM CA needed): `InstallCaCertificatesWindow` (`InstallCaCertificatesViewModel`):
  **Add files** (several at once, again for more; PEM .crt / .pem / .cer incl. bundles with several certificates, DER
  .cer / .crt / .der, at most 1 MB each, read by the shared `CaCertificateFiles`), a virtualized table with one row per
  certificate (Name = subject CN, Issued by, Valid to, SHA-256 (first 8 bytes, tooltip the whole one), File, Status chip,
  Remove icon): the same certificate in several files is one row with both file names; a row's problem stays in its Status
  column ("Not a CA certificate": no BasicConstraints CA or no keyCertSign when KeyUsage is present, "Expired", "Not valid
  yet", "Cannot be read" / "No certificate" for a file). Rows with a problem are left out; Install is disabled with the
  reason as tooltip while no usable certificate is listed or more than 150 (`CaCertificateFiles.MaxCertificates`, one
  step each within the 200 steps of a task; the reason also directly below the table), confirms in `ui:MessageWindow`
  ("Install 3 CA certificates on 12 devices? ...") and returns `InstallCaPayload` {certificates [{name, pem}]} (public
  data in memory, no upload). Steps: Check compatibility (the payload is checked again first: every certificate a CA,
  valid now, duplicates merged, else Failed "<name>: <reason> Nothing was changed." without a request; then cert v1 from a
  fresh `config/discover`), Read installed CA certificates, Install CA certificate <name> (one each, "(2)" for an equal
  name; Skipped "Already installed" when the device has the fingerprint under any alias; alias "OADM CA <8 hex>" like
  the enable flows, "-2" when another certificate holds that alias), Verify CA certificates (every fingerprint listed,
  else Warning). Name "Install CA certificate <name>" / "Install N CA certificates".
- Not done: the device grid's "Trusted (OADM CA)" / "Issued by a previous CA" texts (OADM certificates already rate
  **Trusted** through the trust anchors). The Certificate expires column follows the Expiry warning policies since stage 3. Task names do not
  distinguish Enable from Update or one renewed purpose (the name is fixed per run, before the device is read).
- Tests (`tests/Oadm.Plugins.Pki.Tests`): parsers and request bodies against the recorded answers of the development camera (linked from
  `tests/Oadm.Plugins.Pki.Tests/Fixtures`), every task against `FakeCamera` (stateful REST cert v1 with real keys and CSRs, SOAP web
  server, network_settings.cgi, time.cgi) incl. compatibility failures with no writes, clock, chain, cleanup, renew,
  delete refusals, manual install, Install CA certificates (`InstallCaTests`: PEM bundles, DER, non-CA, expired,
  garbage, installs / skips existing / no writes on an old device or an invalid payload); registry batching; dialog view
  models (the certificate window at its 5,000 devices: unit check plus a Perf test with 190,000 certificates; Install CA
  certificates with 30,000 devices); headless screenshots `pki-view-certificates.png`, `pki-delete-certificates.png`,
  `pki-install-manual.png`, `pki-install-ca.png`, `pki-dot1x-confirm.png`; `tests/Oadm.Client.Tests/SecurityMenuTests` (`pki-security-menu.png`);
  `tests/Oadm.Server.Tests/DeviceTlsTests` (UpdateDeviceTlsAsync with the fake network presenting real certificates).
  `HardwareWriteTests` is an opt-in skeleton (`OADM_PKI_HARDWARE_WRITE=1`, `Category=HardwareWrite`), not written yet.

## Issued certificates and revocation

Spec: `docs/spec/radius-server.md` "Certificates and revocation (PKI)". EST and the coming 802.1X server read it through
the CA service.
- `Ca/IssuedRegistry` (`PkiService.Issued`): every device certificate the OADM CA issued, in memory and in the plugin
  records `issued` (one row per serial: `IssuedCertificate` without DER), `issuedDer` (`{"der": base64}`, read one at a
  time by `GetDerAsync`) and `revoked` (`CertificateRevocation`). Indexes by normalized serial
  (`CertificateSerials.Normalize`), by device and the newest per device and use (state Replaced); an ordered list for the
  pages (newest first, one pass per query with filters and search). New entries are written every `FlushDelay` (2 s)
  and on stop, only changed rows, in batches of 2,000; `RevokeAsync` writes before it returns and raises
  `RevocationChanged` once; `IsRevoked` / `FindRevocation` read an immutable dictionary replaced on change (no lock).
  `PruneAsync` at the start and every hour: certificates ended more than a year ago, revocations of ended certificates.
  `LoadAsync` (in `PkiService.StartAsync`, before anything is answered) moves the settings value `issued` of OADM 4.1
  (entries marked `NoCopy`; `ImportFoundAsync` stores their DER, start and device serial when a read finds them).
  `RevokeAsync` also revokes a serial the list does not hold (hex, at most 20 bytes; no device; kept until
  `UnknownNotAfter` = `PkiService.LatestCaEnd`, the latest end of the active and previous CAs). `MarkRemovedFromDevice`
  (the Delete certificates task, after the delete) sets `RemovedFromDeviceUtc`: state Replaced, still revocable.
  `IssuedCounts.From` (the page's counts) skips ended, revoked and deleted certificates.
- Writers: `CertificateDeployment.IssueAsync` (the tasks, with DER and the device's serial) and
  `PkiCertificateAuthority.SignAsync` (the service: use from `DeviceCertificateRequest.Purpose`, DER, the serial of the
  `axis-<serial>.local` name); certificates a task deletes from the device stay listed. `ImportFoundAsync` adds OADM
  certificates found in a device's certificate list (signed by the active or a previous CA, checked by a chain build;
  naming this device): `CertificateInventory.ReadAsync` with the device (View / Delete certificates), Renew certificates,
  and `Ca/ExistingCertificateImport` (once per server, 5 minutes after the start, every device that answers with AXIS OS
  11.11+, 4 at a time, read only; setting `existingImported` only when a round reached the devices: one read, no devices,
  or none with the certificate API; else again after `PkiOptions.ExistingImportRetry`, 1 hour).
- Methods: `getCertificates` (`CertificatesRequest`: state, purpose, source, search, offset, limit 500 ->
  `CertificatesReply`; the caller's devices from `call.Devices`; a user with a group: only those, else every row and
  removed devices with `DeviceManaged` false; an offset past the end answers the last page), `getCertificate`
  (`CertificateDetailsRequest` -> `CertificateDetailsReply` with the SDK's `CertificateDescription` and the revocation;
  not listed or another group's: "This certificate is not in the list."; no DER: "OADM keeps no copy of this
  certificate."), `revoke` (`RevokeRequest`: serials, at most 5,000, and `RevocationReason`) and `revokeDevice`
  (`RevokeDeviceRequest`: every certificate of the device that is still valid, after `PkiService.DeviceCertificateReader`
  read the device's list for at most `PkiOptions.DeviceReadTimeout`, 20 s; a device that does not answer revokes what the
  list holds) -> `RevokeReply` (revoked, already revoked, expired or no serial); the revoking user is the caller. Event `certificates` at most once per
  `PkiOptions.CertificatesEventDelay` (1 s) to `AllDevices` and to each group that read the page.
- Client (`IssuedCertificatesViewModel`, in `PkiView.axaml`): the client SDK's shared list of issued certificates
  (`ui:IssuedCertificateList` with `IssuedCertificateListViewModel`, also the 802.1X server's certificate choice; PKI reads
  through `PkiCertificateSource`): filters (state, use, issued by), search (300 ms after the last key), Previous / Next,
  `RangeObservableCollection` rows (`IssuedCertificateRow`), the selection handed over in one step and kept over a new
  page, View certificate (`ui:CertificateWindow`, a double click too; its "ends soon" follows the row's state: each device
  has the days of its Expiry warning policy, `CertificatesReply.WarningDays` is null); the page adds Revoke and Revoke all of
  this device with `MessageWindow.ConfirmWithChoiceAsync` (Reason, red Revoke), hidden for a user with a group or without
  "manage"; while either runs (Revoke all of this device reads the device first, up to 20 s) the list's progress row says
  "Revoking the certificates" and both are off (`WorkText`; a write runs to its end, no Cancel). The card reads with the page; every `certificates` event reads the shown page again.
- Fake mode (`FakeOadmApi.PkiCertificates.cs`): 24 devices with HTTPS and some IEEE 802.1X certificates in every state;
  View certificate describes a certificate the fake CA signs; Revoke changes the rows.

## Device groups (SDK 0.4.0)

`minSdkVersion` 0.4.0. `InvokeAsync(ICallContext, ...)`: `getState` for everyone who may see the page, with the counts of
issued certificates (`issuedDevices`, `devicesWithCurrentCa`, `devicesWithPreviousCa`, `expiringSoon`, per previous CA)
only of the caller's devices (`ViewedSet` of `plugins/Shared/ViewerScopes.cs`); `getCertificates` and `getCertificate`
only of the caller's devices (above); every other method `RequiresAllDevices`
(the CA, its trust and the settings are server-wide). The `state` event goes to `AllDevices` with every device and to
`DeviceScope(key)` of each group that read the page with that group's counts. The page hides the CA actions, the trust
button and Save for a user with a group (`HasAllDevices`). The tasks get their device from the host;
`CertificateDeployment.NamesFor(device, network, inAnyGroup)` passes `IDeviceInfo.AddedByAllDevices` to
`CertificateNames.For(..., trustNames)`: false leaves the host name, the FQDN and a name address out (only IP addresses
and `axis-<serial>.local`, CN the OADM address when it is an IP address, else the first IP address or the axis name).
`inAnyGroup` (`PkiService.InAnyGroup`, `IDeviceScopes.InAnyGroup`; no scopes: true) leaves out everything the camera
reports (host name, FQDN, further IP addresses): a group user could change them on the camera. Tests: `DeviceGroupTests` (the `PluginCanary`,
counts and events per group, names, Enable HTTPS on a device a group user added, one a group covers and one no group covers, the page,
the Certificates tab of a group user).

## Permissions

`oadm.pki:show` (`getState`, `getCertificates`, `getCertificate`), `:manage` "Manage the certificate authority" (every other
method, Revoke too, except `installServerTrust`) and `:trustServer` "Trust it on the server" (`installServerTrust`). The page hides the CA actions
and locks the settings without "manage", and hides "Install in trusted root store" without "trustServer". The Security
tasks have one key each (`task:oadm.pki....`). Operator default: show and the tasks.
