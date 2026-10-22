# PKI (`oadm.pki`)

Core plugin (+ `.Client`), rail page **PKI** (group Security, icon `shield`), always on. Device API research:
`tests/Oadm.Plugins.Pki.Tests/Fixtures/`. Part 1: the CA, its page and the server's trust;
part 2: the contributed Security tasks (below), and the OADM CA service (`PkiCertificateAuthority`, offered as
`IDeviceCertificateAuthority` through `ICorePluginContext.Services` while the plugin runs; the EST server signs with it),
the only writers of the `issued` registry.
- CA store (plugin settings): `ca` (id = SHA-256 of the certificate, source generated/imported, certificate and chain PEM,
  key as PKCS#8 PEM encrypted with `ICorePluginContext.Secrets`, purpose `pki:ca:<id>`), `previousCas` (public parts, at
  most 10, newest first, expired ones dropped), `issued` (`{serialNumber, deviceId, purpose https|dot1x, caId, notAfterUtc,
  issuedUtc, alias, source}`, source "EST" for certificates of the CA service, empty for PKI's tasks; per device and purpose the newest counts; `IssuedRegistry` keeps changes in memory and writes at
  most every 2 s and on stop, entries ended more than 30 days ago are dropped), `config`. Without `Secrets` the plugin keeps no CA
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
  CA's end when issuing, logged), `expiryWarningDays` 30 (1..365), `dot1x.eapolVersion` 1|2, `dot1x.identity`
  mac|hostName|custom, `dot1x.customIdentity` (1..64 printable, placeholders `{serial}` `{hostName}` only),
  `dot1x.radiusCa` oadm|imported + `radiusCaPem` (`importRadiusCa` checks a PEM/DER CA certificate, Save stores it).
- Page methods (`PkiMethods`, camelCase, errors as `{errors: {Field: message}}` or `{error}`): `getState` (status, ca,
  previousCas, config, serverTrustInstalled (checked every call), counts, radiusCa, `simulated` in fake mode), `generate`,
  `import`, `previewReplace`, `exportPublic`, `exportPrevious`, `removePrevious`, `backup`, `installServerTrust`,
  `saveSettings`, `importRadiusCa`; event `state` after every change.
- Page (`HasOwnCards`, subtitle "Issues device certificates for HTTPS and IEEE 802.1X.", header chip "CA valid until
  <date>" ok / "CA expires in N days" warning (within `expiryWarningDays`) / "CA expired", "CA key cannot be read" error):
  cards Certificate authority (name, validity, key, fingerprint selectable, chain of an intermediate, "Trusted root store"
  chips for the server and this computer, the source (Generated / Imported) as a "Source" row, buttons Install in trusted
  root store, Export public certificate (PEM / DER menu), Back up, a separator, then Generate new CA, Import CA), Device certificates (the two fields; no issued-devices summary line), IEEE 802.1X (EAPOL version, EAP identity + custom field, RADIUS server CA
  with Import... / View, one Save for both cards at the end of the IEEE 802.1X card, inputs 460 px wide), Previous certificate authorities (only when there are any: Name, Valid
  until, Replaced, Export / Remove links, Remove red and confirmed). Dialogs `GenerateCaWindow`, `ImportCaWindow` (FileRow, key
  FileRow only for a certificate without key), `BackupWindow`: `ValidatingViewModel`, errors under the fields, the
  confirmation popups owned by the dialog. Last export folder per client in
  `LocalApplicationData/Oadm/plugins/oadm.pki/client.json`. Fake mode (`FakeOadmApi.Pki.cs`): in-memory CA + one previous
  CA, server store simulated, this computer never touched.
- Tests: `tests/Oadm.Plugins.Pki.Tests` (generator extensions, importer formats and every check, service with real secret
  protector, event hub and anchor registry: default CA, restart, other master key, no secrets, replace + confirmation,
  previous CAs cap/expiry/remove, export, backup restore, settings, RADIUS CA, status texts; trust store installers per
  OS with the fake runner; page view model incl. dialogs; fake mode; headless screenshots `pki-page.png`,
  `pki-page-imported-intermediate.png`, `pki-generate-dialog.png`, `pki-import-dialog-errors.png`, `pki-backup-dialog.png`);
  `tests/Oadm.Core.Tests/Vapix/TrustAnchorTests.cs` (anchored leaf / intermediate Trusted, self-signed stays, registry,
  lazy re-rating in `CertificatePinning`, host context).

## PKI Security tasks (part 2, contributed by `oadm.pki`)

Context menu group **Security** (icon key), no toolbar, `CanRun` on cached data only: AXIS OS >= 11.11 from the firmware
version (+ network-settings 1.x in `Apis` for 802.1X). Device APIs (`Device/`): REST `cert` v1 (`CertApi`: `config/discover`
must list `cert.v1` released, read fresh before the first write, cached 10 min for queries; else "Needs AXIS OS 11.11 or
later. Nothing was changed."), SOAP web server TLS (`WebServerTls`: `aweb:Get/SetWebServerTlsConfiguration` at
`/vapix/services`, ciphers sent back as read), network_settings.cgi (`NetworkInfoApi`: getNetworkInfo, setWired8021X
Configuration), time.cgi getDateTimeInfo (`DeviceClock`, HTTP Date header as fallback).
- Issuing (`CertificateDeployment.IssueAsync`, `DeviceCertificateIssuer`): key on the device (`create_certificate` RSA-2048 in
  the default keystore) -> `get_csr` -> OADM signs (its own subject `CN=<OADM address>`, SAN IP addresses, host name, FQDN,
  `axis-<serial>.local`; never the CSR's; CSR key RSA >= 2048 or EC; EKU serverAuth / clientAuth; KeyUsage digitalSignature +
  keyEncipherment; AKI = CA SKI; 16 random serial bytes; validity `deviceCertValidityDays` from now - 5 min, capped at the
  CA) -> `PATCH certificates/<alias>`; the entry goes into `issued`. A failure after the key was created deletes the
  unfinished key again. Aliases `OADM HTTPS|802.1X <yyyyMMdd-HHmmss>`, CA `OADM CA <8 hex>`, RADIUS CA `OADM RADIUS CA <8
  hex>`, manual installs `OADM import <ts>`, percent-encoded in URLs. CA certificates are matched by fingerprint (an
  existing alias is reused). "Remove previous OADM certificate" deletes older OADM certificates of the purpose that
  nothing uses (alias prefix + registry serial or the CA as issuer), never others.
- **Enable HTTPS** (`oadm.pki.https-enable`, no dialog, name "Enable HTTPS"): Check compatibility, Read web server
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
  RADIUS CA of the page, cert v1, network-settings with `wired.8021X`, identity 1..128), Check device clock (> 5 min off:
  "The device clock is 12 min off. Set the date and time first. Nothing was changed."), Install CA certificates (RADIUS
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
- Not done: the device grid's "Trusted (OADM CA)" / "Issued by a previous CA" texts and `expiryWarningDays` in the
  Certificate expires column (OADM certificates already rate **Trusted** through the trust anchors). Task names do not
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

## Permissions

`oadm.pki:show` (`getState`), `:manage` "Manage the certificate authority" (every other method except
`installServerTrust`) and `:trustServer` "Trust it on the server" (`installServerTrust`). The page hides the CA actions
and locks the settings without "manage", and hides "Install in trusted root store" without "trustServer". The Security
tasks have one key each (`task:oadm.pki....`). Operator default: show and the tasks.
