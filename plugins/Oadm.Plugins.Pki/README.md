# PKI (`oadm.pki`)

Core plugin (+ `.Client`), rail page **PKI** (group Security, icon `shield`), always on. Since OADM 4.5 the package also
holds the core plugins **EST server** (`oadm.est`, `Est/`, README there) and **802.1X server** (`oadm.radius`, `Radius/`,
README there), whose pages are the tabs **802.1X server** and **EST server** of the PKI page (`TabOf`), each with its
own source-available `LICENSE`; the rest stays Apache 2.0
(`docs/spec/pki.md` "One plugin"). One server assembly (`Oadm.Plugins.Pki.Server.dll`, namespaces `Oadm.Plugins.Pki`,
`Oadm.Plugins.Est`, `Oadm.Plugins.Radius`), one client assembly, one `plugin.json`. Only the two servers need OADM PRO
(`Servers/ServerSwitch.cs`: Turn on / Turn off, the stored choice `serverOn`, started and stopped with the license); PKI's
own parts never do. The core plugins keep their 4.1 ids, so their settings, records, permissions and events keep their
keys; a 4.1 folder `oadm.est` or `oadm.radius` is skipped ("Merged into PKI") and its on/off choice taken over once. Device API research:
`tests/Oadm.Plugins.Pki.Tests/Fixtures/`. Part 1: the CAs, their page and the server's trust;
part 2: the contributed Security tasks (below), and PKI's CA service (`PkiCertificateAuthority`, offered as
`IDeviceCertificateAuthority` through `ICorePluginContext.Services` while the plugin runs; the EST server signs with it),
the only writers of the list of issued certificates (see "Issued certificates and revocation" below).
- CA store (plugin settings): `ca` (the first-start root CA: id = SHA-256 of the certificate, certificate and chain PEM,
  key as PKCS#8 PEM encrypted with `ICorePluginContext.Secrets`, purpose `pki:ca:<id>`), `caDeleted` ("true" once that CA
  was deleted: no start makes it again), `importedCas` (created and imported CAs, below), `deletedAxisCas` (SHA-256 of the
  built-in Axis device ID CAs the user deleted), `previousCas` (public parts of CAs replaced before OADM 4.5; ended ones
  dropped, nothing adds to it any more), `config`, `existingImported` (the one pass over the devices ran; below); `issued`
  of OADM 4.1 moves into the plugin records at the first start (below). Without `Secrets` the plugin keeps no CA ("CA
  cannot be stored on this server"); a key that cannot be decrypted (other master key) shows "Key cannot be read" in the
  CA's row, the CA cannot issue, nothing is overwritten.
- No OADM CA (decision 2026-10-26, `docs/spec/pki.md` "Certificate authorities"): every CA is an ordinary root or
  intermediate CA. First start: the root CA "OADM Root CA <machine name>" in the background (progress row "Creating the
  certificate authority"; RSA 4096, 10 years), because the built-in policies need an issuer; it keeps the id `oadm`.
  `CaGenerator` (`CertificateRequest` only): a root or an intermediate signed by a CA with its key; RSA 2048 / 3072 / 4096
  (SHA-256 PKCS#1 v1.5) or ECC P-256 (ECDSA SHA-256), 16 random positive serial bytes, NotBefore now - 5 min,
  BasicConstraints CA (critical, no path length limit), KeyUsage keyCertSign + cRLSign (critical), SKI + AKI (the issuer's
  key for an intermediate, which ends at the latest with it and keeps its chain), 1..30 years (default 10), off the
  request thread; `CannotSignCa` refuses an issuer that is no CA or has a path length of 0.
- Create CA (`createAuthority`, `CreateCaRequest`: name, intermediate, issuer id, key `CaKeys`, years; field errors
  `CommonName`, `Issuer`, `Key`, `ValidityYears`): stored in `importedCas` with `Created` (its key never left OADM, so it
  may be an EST sign-in CA); a name another CA has is refused.
- Import (`CaImporter`): PKCS#12 + password (also the Back up file) or PEM certificate(s) + key (in the file or a separate
  PEM: PKCS#8, PKCS#1, SEC1, encrypted PKCS#8), at most 1 MB, password never stored or logged. Checks with field errors
  (`File`, `Password`, `KeyFile`, `KeyPassword`): readable / password right, exactly one certificate with a matching key,
  a CA (keyCertSign when KeyUsage is present), RSA >= 2048 or ECC P-256 / P-384, valid now and NotAfter >= now +
  device validity + 1 year ("This CA expires on <date>, before certificates it would issue."). The other certificates on
  the issuer path become the chain.
- Import (`import`, decision 2026-10-25) adds the CA with its key to `importedCas` (see "Certificate authorities"); it
  never replaces another CA and asks nothing.
- Delete (`deleteAuthority`, `DeleteAuthorityRequest` with the row id) of every CA; see "Certificate authorities".
- Trust anchors: the first-start CA and its chain, the previous CAs (+ chains) and every created or imported CA with its
  key (+ chain) go to `ctx.TrustAnchors`, so device certificates they issued are **Trusted** in the device grid without an
  OS change (lazy, next full refresh); the renewal issuers (`SetRenewalIssuers`) are the CAs that issue and the previous
  OADM CAs.
- Export `.crt` (PEM, with the chain) / `.cer` (DER) of every CA row; Back up (`backup`, `BackupRequest.Id`: any CA with
  its key, none: the first-start CA) = PKCS#12 with key and chain, AES-256-CBC + SHA-256, 100,000 iterations, password
  >= 8 (twice in the dialog), warning popup, logged "CA backup exported".
- OS trust store (`TrustStore/`, `TrustStoreInstallers.ForServer` / `.ForClient`, one class per OS behind
  `IProcessRunner` and `IMachineRootStore`, 30 s per tool, output to the log): Windows LocalMachine Root (client without
  admin: `certutil -addstore Root` with runas), Debian/Ubuntu `/usr/local/share/ca-certificates/oadm-<fp16>.crt` +
  `update-ca-certificates`, RHEL/Fedora `/etc/pki/ca-trust/source/anchors` + `update-ca-trust extract`, SUSE
  `/etc/pki/trust/anchors` + `update-ca-certificates` (client without root: one `pkexec /bin/sh -c` prompt, without pkexec
  the error shows the exact `sudo` command), macOS System keychain `security add-trusted-cert -d -r trustRoot` (client:
  `osascript ... with administrator privileges`), check `find-certificate -Z -a`, remove `delete-certificate -Z <sha1>`.
  Texts: "Permission denied: the server must run as administrator / root", "No supported certificate tool found
  (update-ca-certificates or update-ca-trust)", "<tool> failed: <first line>", "The installation was cancelled.".
  Every root CA of the list can be installed (decision 2026-10-26: `installServerTrust` with `InstallTrustRequest`,
  the state's `RootTrust` per root CA). Tests use fakes only; nothing ever touches a real trust store.
- Settings of OADM before 4.5 (`config`, read only by the conversions since stage 4: they became the issuing policies
  "HTTPS" and "IEEE 802.1X"; `PkiValidation` keeps the field rules the policies use): `deviceCertValidityDays` 365 (1..3650), `expiryWarningDays` 30 (1..365; since OADM 4.5 only read once: it becomes the Expiry
  warning policy "All devices"), `dot1x.eapolVersion` 1|2, `dot1x.identity`
  mac|hostName|custom, `dot1x.customIdentity` (1..64 printable, placeholders `{serial}` `{hostName}` only),
  `dot1x.radiusCa` oadm|imported + `radiusCaPem` (read once by the conversion of 2026-10-25: the RADIUS server CA of the
  IEEE 802.1X policy becomes a CA of the list, `PkiService.ConvertRadiusCasAsync`).
- Page methods (`PkiMethods`, camelCase, errors as `{errors: {Field: message}}` or `{error}`): `getState` (status, ca,
  previousCas, config, `rootTrust` (every root CA and the server's root store), counts, radiusCa, `simulated` in fake
  mode), `createAuthority`, `import`, `exportPublic`, `exportPrevious`, `backup`, `installServerTrust`,
  `importAuthority`, `deleteAuthority`, `getCertificates`, `getCertificate` (below); event `state` after every change,
  `certificates` (payload `{}`) when the list of issued certificates or the list of certificates no longer accepted
  changed. Gone since 2026-10-26: `generate`, `previewReplace`, `removePrevious`, `removeAuthority`, `revoke`,
  `revokeDevice`.
- Page (stage 7 of `docs/spec/pki.md`, `HasOwnCards`, subtitle "Certificates, their policies, and the 802.1X and EST
  servers."): fixed tabs Certificates | Policies | 802.1X server | EST server (`PkiViewModel.Tabs`, the server pages from
  `ICorePluginClientContext.Tabs`, `OpenTabRequested`; Edit policies: `PkiTabRequest`, with the policies to highlight).
  Policies: one grouped table and a side panel (see below). Certificates: two cards that share the tab's height with a
  splitter between them: **Certificate authorities** (Create CA, Import with "CA certificate" and "CA with its key", both
  add a row; the search; below it only the first-start CA being made or a server that cannot keep a CA) and
  **Certificates** (Create, Sign request and Import as plain command buttons; Group by, the state and issuer filters and
  the search).
  The CAs are rows (decision 2026-10-26): Type only "Root CA" or "Intermediate CA" (a root CA adds where it is installed:
  "OS root store: server", "OS root store: client"); Key the key type, "No key" or "Key cannot be read"; the menu has
  View, Export (PEM / DER), Install in trusted root store (every root CA), Back up (every CA with its key) and Delete
  (red, confirmed); a CA that cannot be read is a row "Unknown CA" / "Cannot be read" with Delete only; the column **Used
  in** (only the CA list, `IssuedCertificateListViewModel.ShowsUsedIn`): the names of the policies that use the CA, the
  first as a badge and "+N", every name as tooltip (`PkiService.UsedIn`). Key and the date of Valid until are dimmed in
  both cards. No Save: the settings of device certificates and IEEE 802.1X are issuing policies.
  Policies: see "Policies". Dialogs `CreateCaWindow`, `ImportCaWindow` (FileRow, key
  FileRow only for a certificate without key), `BackupWindow`: `ValidatingViewModel`, errors under the fields, the
  confirmation popups owned by the dialog. Last export folder per client in
  `LocalApplicationData/Oadm/plugins/oadm.pki/client.json`. Fake mode (`FakeOadmApi.Pki.cs`): in-memory CAs with Create
  CA and Delete CA, server store simulated, this computer never touched.
- Tests: `tests/Oadm.Plugins.Pki.Tests` (generator extensions, importer formats and every check, service with real secret
  protector, event hub and anchor registry: default CA, restart, other master key, no secrets, Create CA, Delete CA,
  export, backup restore, settings, RADIUS CA, status texts; trust store installers per OS with the fake runner; page
  view model incl. dialogs; fake mode; headless screenshots `pki-a-certificates-tab.png`, `pki-a-ca-list-end.png`,
  `pki-a-create-ca-root.png`, `pki-a-create-ca-intermediate.png`, `pki-a-delete-ca.png`, `pki-import-dialog-errors.png`,
  `pki-backup-dialog.png`; the list of issued certificates: `IssuedRegistryTests`, `ExistingCertificateImportTests`,
  `CertificatesTabTests` (+ `pki-certificates.png`, `pki-certificate-window.png`), `BackupTests` (a restore on another
  server over the real database), Perf `IssuedScaleTests` (30,000 devices x 4 certificates));
  `tests/Oadm.Core.Tests/Vapix/TrustAnchorTests.cs` (anchored leaf / intermediate Trusted, self-signed stays, registry,
  lazy re-rating in `CertificatePinning`, host context).

## Certificate authorities (docs/spec/pki.md)

- `PkiService.Authorities()` (the CA service's `Authorities`): the first-start root CA (`oadm`, `Issues` while its key is
  readable), the previous OADM CAs not ended (`oadm-previous:<sha256>`, CA certificate then its chain), the Axis device ID
  (`axis-device-id`, the PEMs of `plugins/Shared/DeviceId` embedded, roots first, without the deleted ones) and the
  created and imported CAs (`imported:<sha256>`, oldest first). Built once per change of the lists (reference check, and
  again when a previous CA ends). `CaChanged` (the service's `Changed`) after Create CA, Import CA and Delete.
- `SetUsedBy(serviceId, serviceName, ids)`: in memory per service (each service tells it again after a start), an empty
  list clears it; a change publishes the state (no `Changed`). Shown as "Used by" (names in order, "-" for none).
- Created and imported CAs (decision 2026-10-25: several issuing CAs): setting `importedCas` (at most 50 together, part of
  the backup): the certificate PEM, the chain (`chainPem`: an intermediate's issuers), `created` for Create CA and, for a
  CA with its key, `keyProtected` (PKCS#8 PEM encrypted with `ICorePluginContext.Secrets`, purpose `pki:ca:<id>`; never
  logged, never exported on its own). Loaded at the start (`ReadImported`): a key the master key cannot read leaves the
  CA listed but unable to issue ("Key cannot be read", logged once); a certificate that cannot be read is a row that says
  so. `importAuthority` (PEM or DER, one certificate; errors under the file `AuthorityFile`: not a certificate, larger than
  1 MB, contains a private key, not a CA, expired, already in the list), `import` (a CA with its key, `CaImporter`; the
  same CA imported before without its key gets it; one with its key already listed: "This CA is already in the list.").
  All Admin ("manage", all devices, audited).
- Delete (`deleteAuthority`, `PkiService.DeleteAuthorityAsync`, decision 2026-10-26): every CA row, also the first-start
  CA, a previous one, each Axis device ID certificate and a row that cannot be read (`ca-unreadable:<authority id>`: its
  stored value is first kept in `ca-unreadable-<UTC time>.json` in the data folder). Refused while a policy names it
  (`PolicyStore.NamingCa`: "Used by the policy Contoso. Change or delete the policy first."), a service uses it, or it
  signed a CA that is still listed ("It signed <name>. Delete <name> first."). Its certificates stay listed.
- Issuing: `PkiService.IssuingCa(caId)` (the first-start CA for null or `oadm`, else a created or imported CA with its
  key; null: it cannot issue), `IssuingCas()`; `PolicyStore.IssuingCaChoices()` (Issued by), `NamingCa(id)` (the
  policies that keep a CA). Every signing path takes the CA of its policy: the tasks
  (`CertificateDeployment.RequireCa(service, policy)`), the CA service (`DeviceCertificateRequest.IssuerId`: EST profiles,
  the 802.1X and EST servers' certificates through `Ca/IssuerChains`), Create and Sign request. The chain sent with a
  certificate is the issuing CA's; the tasks install a certificate of an intermediate CA with its chain
  (`CertificateDeployment.IssuerChainPems`, `DeviceCertificates.InstallSignedAsync`: the certificate alone when the
  device refuses the chain).
- Page: the CA rows of the Certificates list (see "Page" above; **Import** > "CA certificate" opens
  `ImportAuthorityWindow`, "CA with its key" `ImportCaWindow`; **Create CA** `CreateCaWindow`). Tests: `AuthorityTests`,
  `SeveralCasTests`, `StoredCaUnreadableTests`, `PreviousCaConversionTests`, `ChainUploadTests`, `CaListHeadlessTests`,
  `PolicyStoreTests`.

## Policies (docs/spec/pki.md "Policies", "Code (stage 3)", "Code (stage 4)")

- `Policies/`: `PolicyModels` (contract), `PolicyRules`, `PolicyMatcher`, `PolicyStore` (setting `policies`, part of the
  backup), `ExpiryEvents`; `PkiService.Policies.cs` (the methods, PKI's own conversions, the expiry days handed to the
  host; no revocations since 2026-10-26: Block only refuses). Kinds (decision 2026-10-25):
  HTTPS certificate, IEEE 802.1X certificate (issuing, no devices), Expiry warning, 802.1X server (one only, its
  certificate and TLS settings), 802.1X trusted CA (stored id `allowCa`), VLAN, Block (a stored `alsoRevoke` is no longer read), EST server (one
  only, its certificate), Enrollment, EST sign-in CA (stored id `signInCa`). Defaults "HTTPS", "IEEE 802.1X", "EST server"
  (`PkiService.ConvertIssuing`, owner `oadm.pki:issuing`); the 802.1X server policy comes from `RadiusPolicies`. The kinds
  of before 2026-10-25 (`serverCertificate`, `revoke`) are converted once on start (`PolicyStore.ConvertKinds` through
  `RewriteOnceAsync`, owner `oadm.pki:kinds-2026-10-25`; never on a store that cannot be read). The previous OADM CAs get
  their own 802.1X trusted CA policies once (`PolicyStore.ConvertPreviousCasOnceAsync`, owner
  `oadm.pki:previous-cas-2026-10-26`, after the 802.1X server's conversion; decision 2026-10-26: no CA follows another). The 802.1X server inside
  PKI reads the store in process (`IPluginServices`); `Radius/Server/RadiusPolicies.cs` converts its settings once.
- Page: tab **Policies** (`PoliciesViewModel`, decision 2026-10-25, like Intune): **Create** and **Search policies** above
  one virtualized table grouped by `PolicyTableGroups` (Issuing policies, Server policies, 802.1X access policies, EST
  enrollment policies, Expiry warnings; a group without rows shows no header, the PRO groups hide without OADM PRO; the
  header shows the group's icon, `PolicyTableGroups.KeyOf` gives a `ui:GridGroup`):
  Name (the **Built-in** badge `Border.badge.system`, the red problem dot), Type, Summary. A click on a row or Create opens
  the side panel (`Border.sidePanel`, 560 px, pushes the table left): Create first lists the types ("Create a policy",
  Next), then the form; Delete (red, not for built-in policies), "Not saved", Cancel, Save; "Discard changes" before
  unsaved changes are lost. The form (`PolicyEditorViewModel`, `PolicyFormView`): the kind's description, Name, VLAN ID,
  Days before the end, Devices ("All devices" or the chosen ones
  with Choose devices: the shared `DevicePickerWindow` in its group mode, made only on that click), 802.1X trusted CA's CA and "Only devices OADM manages", the issuing kinds' Issued by
  (`PolicyList.IssuingCas`), Valid (days), Key, for IEEE 802.1X EAPOL version, EAP identity (MAC address, Host name, IP
  address, Last octet of the IP address, Custom), Custom identity and RADIUS server CA (a CA of the list, the hint names the
  issuer of the 802.1X server's certificate), the server kinds' Certificate (one of the Certificates list) and the 802.1X
  server's Minimum TLS version (1.0 to 1.3), Largest EAP packet (500 to 1,400), Reauthentication (Switch decides or 10 to
  1,440 minutes). A save sends the policy's revision; one over a later change is refused "Changed by someone else.
  Reload.". A jump from a server tab's "N policies applied" highlights that server's policies for 3 s
  (`PoliciesViewModel.Highlight`, `PolicyHighlight.Dot1x` / `Est`, `PkiView.FollowHighlight`: the theme's
  `DataGrid.highlightRows` / `DataGridRow.highlight`).
- Events "Certificate expires soon", "Certificate expired" and their Any device twins (`ExpiryEvents`, group
  "Certificates", the ids of OADM 4.1's built-in events).
- Server status without a usable certificate (`PolicyTexts.ServerNoCertificate`, `ServerCertificateUnusable`): "The
  802.1X server has no certificate. Choose one in its policy." / "The EST server cannot use its certificate: it has
  ended."
- Tests: `PolicyStoreTests`, `ExpiryEventTests`, `PolicyPageTests` (the table, the side panel, Create/Edit/Delete, a group
  user; + headless `pki-page-1700.png`, `pki-page-1000.png`,
  `pki-policy-vlan.png`, `pki-policy-allow-ca.png`, `pki-policy-server.png`, `pki-policy-expiry.png`, `pki-policy-block.png`),
  `IssuingPolicyTests`, `IssuingTaskTests`, `Dot1xPolicyChoiceTaskTests`, `IssueDialogTests` (+ headless
  `pki-enable-https.png`, `pki-enable-dot1x.png`, `pki-renew.png`, `pki-install-dot1x.png`, `pki-policy-https.png`, `pki-policy-dot1x-certificate.png`,
  `pki-policy-est-server.png`, `pki-policy-dot1x-server.png`), `PolicyTabHeadlessTests` (`pki-policies-1700.png`,
  `pki-policies-1000.png` and their `-end` captures: nothing clipped, no sideways scroll), `SeveralCasTests` (the
  conversion of the kinds, Used in), Perf `PolicyPerfTests` (30,000 devices).

## PKI Security tasks (part 2, contributed by `oadm.pki`)

Clock check (docs/spec/pki.md; `plugins/Shared/DeviceClock/CameraClock.cs`, linked into PKI and EST, `DeviceClock` uses it):
the Enable HTTPS, Enable IEEE 802.1X and Renew certificates dialogs (`ClockCheck`, after the main button of `IssueCertificateWindow`) read the camera clocks
through the read-only query `readClock` (8 at a time, 10 s each, progress window with Cancel; unreadable cameras left out)
and ask with `ui:MessageWindow` Yes / No when a camera is more than 5 s behind the certificate's start (5 min back); Yes
puts `{"clockConfirmed":true}` in the payload. Step "Check device clock" (802.1X, renew, manual install): ahead is fine;
behind beyond the start fails "The device's time is 9 min 12 s behind. The certificate becomes valid on the device only in
4 min 12 s. Set the device to use NTP. Nothing was changed." unless confirmed (then a Warning log line). Tests: `ClockTests`.

Context menu group **Security** (icon key), no toolbar, `CanRun` on cached data only: AXIS OS >= 6.53 from the firmware
version (all tasks, 802.1X too). Compatibility (docs/spec/pki.md "Older AXIS OS"), decided in `Tasks/PkiCompatibility`
fresh in every Check compatibility (`RequireAsync` -> `PkiDeviceSupport`: certificate API, AXIS OS version, the web server
roles before 7.10, the highest EAPOL version) and cached 10 min for queries (`CachedAsync`). Device APIs (`Device/`):
`DeviceCertificates` (one facade, `CertificateApiKind` Rest / Soap) over REST `cert` v1 (`CertApi`, when `config/discover`
lists `cert.v1` released) or SOAP (`CertSoapApi`: `tds` Get/Load/Delete certificates and CA certificates,
`acertificates:CreateCertificate2` / `GetPkcs10Request2` / `LoadPkcs12` / `Get/SetCertSet`; AXIS OS 6.53+ by the version
read fresh, unknown refuses "The device's AXIS OS version cannot be read. Nothing was changed.", older "Needs AXIS OS 6.53
or later. Nothing was changed."), `DeviceSoap` (the envelope, `DeviceXml` limits, faults as the device's text), SOAP web
server TLS (`WebServerTls`: `aweb:Get/SetWebServerTlsConfiguration`, ciphers sent back as read, Operator and Viewer
policies too before 7.10), 802.1X (`Dot1xSettings` with `Dot1xApiKind`: network_settings.cgi alone where `getNetworkInfo`
reports the certificates (1.38, 12.11), else `SetCertSet EAPTLS_WIRED` binds them first, with param.cgi
`Network.Interface.I0.dot1x` where there is no network settings API; `NetworkInfoApi`: getNetworkInfo,
setWired8021XConfiguration), time.cgi getDateTimeInfo (`DeviceClock`, HTTP Date header as fallback). EAPOL version 3 needs
10.1 ("EAPOL version 3 needs AXIS OS 10.1 or later. Choose a policy with EAPOL version 1 or 2. Nothing was changed."); SOAP
keys are RSA 2048 (an ECC policy: "The device cannot create an ECC key. Choose a policy with RSA. Nothing was changed.").
- Issuing (`CertificateDeployment.IssueAsync`, `DeviceCertificateIssuer`): key on the device (`create_certificate` with the issuing policy's key, EC-P256 or RSA-2048, in
  the default keystore, after the keystore's `key_types` were checked) -> `get_csr` -> OADM signs (its own subject `CN=<OADM address>`, SAN IP addresses, host name, FQDN,
  `axis-<serial>.local`, the reported ones only for a device no group covers; never the CSR's; CSR key RSA >= 2048 or EC; EKU serverAuth / clientAuth; KeyUsage digitalSignature +
  keyEncipherment; AKI = CA SKI; 16 random serial bytes; validity of the issuing policy from now - 5 min, capped at the
  CA) -> `PATCH certificates/<alias>` (SOAP: `CreateCertificate2` under "<alias> key", `GetPkcs10Request2`, `LoadCertificates`
  under the alias, then the placeholder is deleted); a certificate of an intermediate CA goes with its chain (REST: the
  PEMs one after the other; SOAP: one DER sequence, read back), alone when the device refuses it or keeps anything else
  (`SignedInstall`, a Warning in the task log; decision 2026-10-26); the entry goes into `issued`. A failure after the key was created deletes the
  unfinished key again. Aliases `OADM HTTPS|802.1X <yyyyMMdd-HHmmss>`, CA `OADM CA <8 hex>`, RADIUS CA `OADM RADIUS CA <8
  hex>`, manual installs `OADM import <ts>`, percent-encoded in URLs. CA certificates are matched by fingerprint (an
  existing alias is reused; an alias another CA holds becomes `<alias>-2`). "Remove previous OADM certificate" deletes older
  OADM certificates of the purpose that nothing uses (alias prefix + registry serial or the CA as issuer), never others; a
  refused delete is a warning. A failure after the HTTPS switch request was sent drops the new certificate as unused only
  when the web server settings name another one (`HttpsNotVerifiedAsync`).
- **Enable HTTPS** (`oadm.pki.https-enable`, dialog `IssueCertificateWindow`: the HTTPS certificate policy (the first preselected, read
  with the query `listIssuingPolicies`) with its summary, then the clock check; the run uses the policy's stored values; payload `IssuePayload`; name
  "Enable HTTPS"): Check compatibility (with the policy and the device's key types), Check device clock, Read web server
  settings, Read network settings (Skipped without network-settings), Install CA certificate (Skipped "Already
  installed"), Create key on the device, Get certificate request, Sign certificate, Install certificate, Switch web server
  to the new certificate (policy kept; HTTP only becomes HTTP and HTTPS), Verify HTTPS (`ctx.UpdateDeviceTlsAsync("https",
  fingerprint)`, retried up to 60 s while the web server restarts, step text "Waiting for the web server to restart": OADM
  pins the new certificate, scheme https; the failure says what was changed), Remove previous OADM certificate.
- **Disable HTTPS** (`oadm.pki.https-disable`, confirmation "Video systems that use HTTPS lose the connection to these
  devices.", name "Disable HTTPS"): Check compatibility (with `Network.HTTP.AuthenticationPolicy=basic` it fails unless the
  server setting Allow Basic login over HTTP is on, `IVapixClient.AllowsBasicOverHttp`; the refusal names the setting), Read
  web server settings, Set HTTP only (policy `Http`, certificates stay; Skipped "Already HTTP only"), Verify
  (`UpdateDeviceTlsAsync("http")`, then HTTP must keep answering for 10 s while the web server restarts, at most 60 s;
  docs/spec/pki.md "Web server restart and Basic login").
- **Enable IEEE 802.1X** (`oadm.pki.dot1x-enable`, dialog `IssueCertificateWindow` with the warning "Devices on ports
  that enforce 802.1X become unreachable if authentication fails.", the IEEE 802.1X certificate policy with its summary,
  name "Enable IEEE 802.1X"): Check compatibility (CA chain must end in a root, the policy and its RADIUS server CA, never
  compared with the 802.1X server's certificate, the
  device's key types, the certificate API, the policy's EAPOL version, wired 802.1X by `Dot1xSettings`, identity 1..128), Check device clock (below), Install CA certificates (RADIUS
  server CA + the issuing CA's chain), Create key / Get request / Sign / Install certificate, Set 802.1X configuration (enabled,
  EAP-TLS, identity MAC = serial / host name / IP address / last octet of the IP address / custom with `{serial}` `{hostName}`, EAPoLv<n>, certClient, certsCA = RADIUS
  CA aliases; below network settings 1.38 the certificates by `SetCertSet` first), Verify 802.1X settings (read back, from
  `GetCertSet` there; mismatch = Warning), Remove previous OADM certificate.
- **Disable IEEE 802.1X** (`oadm.pki.dot1x-disable`, name "Disable IEEE 802.1X"): Check compatibility, Set 802.1X off
  (Skipped "Already off"), Verify; certificates stay.
- **Renew certificates** (`oadm.pki.renew`, name "Renew certificates", dialog `IssueCertificateWindow` via
  `IssueCertificateViewModel.ForRenew`: the 802.1X warning and the IEEE 802.1X policy (`IssuingPolicyField`, the first
  preselected), then the clock check; payload `RenewPayload` {`dot1xPolicyId`, `clockConfirmed`}): Check compatibility (a
  chosen policy that no longer exists fails here, nothing written), Read certificates, Read web server settings, Read
  network settings, then per purpose whose current certificate is OADM's the enable flow (HTTPS: the first HTTPS
  certificate policy; IEEE 802.1X: the chosen one, without one the first) with
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
  in use, not factory; a web server or 802.1X read that fails refuses everything, the dialog shows In use "Unknown"),
  Delete certificate <alias> (one each), Verify. Names "Delete certificate <alias>" / "Delete N
  certificates" (distinct aliases).
- **Install certificates** (`oadm.pki.install`): `InstallCertificatesWindow`: Use for (HTTPS / IEEE 802.1X / CA
  certificates only), .pfx / .p12 files (one password for all, `CertificateFiles`: wrong password under the field), table
  File, Certificate, Device, Status chip; a file must match exactly one selected device by MAC (serial in any notation, also
  `axis-<serial>`), IP or host name / FQDN in CN or SAN, one file per device; problems block Install. Install confirms,
  uploads (`UploadAsync`) and sends `{purpose, password, files[{deviceId, fileId, fileName}], policyId}` (CA only: deviceId
  empty = every device). IEEE 802.1X shows the IEEE 802.1X policy below Use for (`IssuingPolicyField`, read once through the
  task's query `listIssuingPolicies` when the purpose is first chosen, `ui:LoadingOverlay` over the card); the run sets that
  policy's identity, EAPOL version and RADIUS server CA (without `policyId` the first policy; a deleted one fails in Check
  compatibility, nothing written). Steps: Check compatibility, Read certificate file, then HTTPS: Read web server settings, Install
  certificate (`install_from_pkcs12`), Switch web server, Verify HTTPS; 802.1X: Check device clock, Install CA
  certificates, Install certificate, Set 802.1X configuration, Verify 802.1X settings; CA only: Install CA certificates.
  A device without a file ends Done with warnings, nothing sent. Name "Install certificate <file>" / "Install N
  certificates".
- **Install CA certificates** (`oadm.pki.install-ca`, icon `upload`, install one or more CA
  certificates on many devices, no CA of PKI needed): `InstallCaCertificatesWindow` (`InstallCaCertificatesViewModel`):
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
- Not done: the device grid's "Trusted (<CA name>)" / "Issued by a previous CA" texts (OADM certificates already rate
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
  Older AXIS OS: `LegacyFirmwareTests.cs` (parsers and request bodies against `Fixtures/Legacy/<version>` of 6.53, 8.40,
  9.80, 10.12, 11.9, 11.11; the compatibility matrix; every task per family on `FakeCamera.Legacy`).
  `HardwareWriteTests` is an opt-in skeleton (`OADM_PKI_HARDWARE_WRITE=1`, `Category=HardwareWrite`), not written yet.

## Issued certificates and revocation

Spec: `docs/spec/pki.md` "Current and pending" and `docs/spec/radius-server.md`. EST and the 802.1X server read it through
the CA service. Decision 2026-10-26: the UI never revokes; the `revoked` records are the internal list of certificates
no longer accepted (Delete, a confirmed replacement, an unused certificate), refused as "Certificate deleted in OADM" /
"Certificate replaced by a newer certificate" (`Ca/NotAcceptedTexts`) and never listed.
- `Ca/IssuedRegistry` (`PkiService.Issued`): the store of device certificates PKI's CAs issued (per camera and use the
  current one and at most one pending one, `docs/spec/pki.md` "Current and pending"), in memory and in the plugin
  records `issued` (one row per serial: `IssuedCertificate` without DER), `issuedDer` (`{"der": base64}`, read one at a
  time by `GetDerAsync`) and `revoked` (`CertificateRevocation`). Indexes by normalized serial
  (`CertificateSerials.Normalize`), by device id, by camera (the serial, else the device id) and the current and pending
  one per camera and use; an ordered list for the pages (newest first, one pass per query with filters and search; keys
  that left the store are skipped and dropped at the next query). `Query(..., currentOnly: true)` (the page) lists the
  current ones of managed cameras by serial; the CA service lists every stored one. Transitions: `AddPendingAsync` (an
  older pending one is revoked, Superseded, except one a task delivered), `ConfirmAsync` (the previous current one is
  revoked, Superseded, user `OadmUser` "OADM", and leaves the store), `DiscardAsync` (an unused pending one, reason Other),
  `MarkDelivered` (a task ran "Set 802.1X configuration", EST wrote an IEEE 802.1X answer), `ReplacedOnCameraAsync`
  (Install certificates). `RevokeAsync` removes what it revokes from the store; older entries of that camera and use become
  `NotCurrent`. `ICertificateDelivery` (`PkiCertificateAuthority`): EST's `DeliveredAsync` / `NotDeliveredAsync` and the
  802.1X server's `SignedInAsync`. New entries are written every `FlushDelay` (2 s)
  and on stop, only changed rows, in batches of 2,000; `RevokeAsync` writes before it returns and raises
  `RevocationChanged` once; `IsRevoked` / `FindRevocation` read an immutable dictionary replaced on change (no lock).
  `PruneAsync` at the start and every hour: certificates ended more than `KeptAfterEnd` (30 days) ago, revocations of ended
  certificates, EST certificates pending longer than `PendingTimeout` (1 hour) without their answer (revoked as unused).
  `LoadAsync` (in `PkiService.StartAsync`, before anything is answered) moves the settings value `issued` of OADM 4.1
  (entries marked `NoCopy`; `ImportFoundAsync` stores their DER, start and device serial when a read finds them).
  `RevokeAsync` also revokes a serial the list does not hold (hex, at most 20 bytes; no device; kept until
  `UnknownNotAfter` = `PkiService.LatestCaEnd`, the latest end of PKI's CAs). `MarkRemovedFromDevice`
  (the Delete certificates task, after the delete) sets `RemovedFromDeviceUtc`: no longer listed, still revocable.
  `IssuedCounts.From` (the page's counts) skips ended, revoked, deleted, pending and not current certificates.
  `ImportFoundAsync` never adds a revoked serial.
- Writers: `CertificateDeployment.IssueAsync` (the tasks: pending after Sign certificate, with DER and the device's serial;
  `CertificateDeployment.ConfirmAsync` after the verify step, `NotUsedAsync` when the install or verify failed) and
  `PkiCertificateAuthority.SignAsync` (the service: use from `DeviceCertificateRequest.Purpose`, DER, the serial of the
  `axis-<serial>.local` name; pending for EST, current at once for other callers); certificates a task deletes from the
  device stay in the store, not listed. `ImportFoundAsync` adds OADM
  certificates found in a device's certificate list (signed by the first-start CA, a previous or a created CA, checked by a chain build;
  naming this device): `CertificateInventory.ReadAsync` with the device (View / Delete certificates), Renew certificates,
  and `Ca/ExistingCertificateImport` (once per server, 5 minutes after the start, every device that answers with AXIS OS
  6.53+, REST or SOAP, 4 at a time, read only; setting `existingImported` only when a round reached the devices: one read, no devices,
  or none with the certificate API; else again after `PkiOptions.ExistingImportRetry`, 1 hour).
- Methods: `getCertificates` (`CertificatesRequest`: state, purpose, source, search, offset, limit 500 ->
  `CertificatesReply`; the caller's devices from `call.Devices`; a user with a group: only those, else every row and
  removed devices with `DeviceManaged` false; an offset past the end answers the last page), `getCertificate`
  (`CertificateDetailsRequest` -> `CertificateDetailsReply` with the SDK's `CertificateDescription` and the revocation;
  not listed or another group's: "This certificate is not in the list."; no DER: "OADM keeps no copy of this
  certificate."); `revoke` and `revokeDevice` are gone (2026-10-26). Event `certificates` at most once per
  `PkiOptions.CertificatesEventDelay` (1 s) to `AllDevices` and to each group that read the page.
- Client (`IssuedCertificatesViewModel`, in `PkiView.axaml`): the client SDK's shared list of issued certificates
  (`ui:IssuedCertificateList` with `IssuedCertificateListViewModel`, also the 802.1X server's certificate choice; PKI reads
  through `PkiCertificateSource`): filters (state, issued by; no use filter since 2026-10-25), search (300 ms after the last key), Previous / Next,
  `RangeObservableCollection` rows (`IssuedCertificateRow`), the selection handed over in one step and kept over a new
  page, View certificate (`ui:CertificateWindow`, a double click too; its "ends soon" follows the row's state: each device
  has the days of its Expiry warning policy, `CertificatesReply.WarningDays` is null); the page adds Delete (red, also for
  several rows), hidden for a user with a group or without "manage"; while it runs the list's progress row shows it
  (`WorkText`; a write runs to its end, no Cancel). The card reads with the page; every `certificates` event reads the shown page again.
- **Certificates card (stage 5, docs/spec/pki.md "Code (stage 5)")**: `PkiService.CertificateList` pages the CAs, the
  server certificates, the local certificates (`Certificates/LocalCertificates`, plugin records `localCertificates`, keys
  encrypted with the master key, purpose `pki:certificate:<id>`) and the registry in one list; `PkiService.CertificateActions`
  holds `createCertificate`, `signRequest`, `importCertificate`, `deleteCertificates` and the download `downloadCertificate`
  (`CertificateMethods`). Client: `CertificateDialogViewModels` with `CreateCertificateWindow`, `SignRequestWindow`,
  `ImportCertificateWindow`, `ExportKeyWindow`; row buttons in `PkiView.axaml`. Create and Sign request refuse a camera's
  `axis-<12 hex>` name (`CertificateRules.IsCameraName`); `PkiCertificateAuthority.PurposeOf` answers `NotDevicePurpose` for
  every local certificate, so EST and the 802.1X server never take one for a camera's. Delete takes every device
  certificate and the created and signed certificates no server policy shows; a still valid one OADM issued goes on the
  list of certificates no longer accepted (`DeleteCertificatesAsync`); one a server policy shows is refused ("Used by the policy 802.1X server. Pick another certificate first.").
- Fake mode (`FakeOadmApi.PkiCertificates.cs`): 24 devices with HTTPS and some IEEE 802.1X certificates in every state;
  View certificate describes a certificate the fake CA signs; Delete changes the rows.

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
method, Create CA, Delete CA and the Certificates card's Create, Sign request, Import, Delete and Export too, except `installServerTrust`) and `:trustServer` "Trust it on the server" (`installServerTrust`). The page hides the CA actions
and locks the settings without "manage", and hides "Install in trusted root store" without "trustServer". The Security
tasks have one key each (`task:oadm.pki....`). Operator default: show and the tasks.
