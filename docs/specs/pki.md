# PKI core plugin (spec)

Status: decided with the user on 2026-10-07. Plugin id `oadm.pki`, core plugin with a rail page **PKI**
(icon `key`), packages `plugins/Oadm.Plugins.Pki` (+ `.Client`). A simple PKI: one active certificate
authority that issues device certificates for HTTPS and IEEE 802.1X. The task plugins that deploy them are
contributed by this core plugin (part 2, below). Simplicity first: by default everything works without the
user touching the page.

Research on ADM and the device APIs: `docs/specs/pki-research/README.md`.

## Decisions (user)

| Topic | Decision |
|---|---|
| CA private key | Encrypted with the server master key (`ICorePluginContext.Secrets`, AES-256-GCM). No passphrase, no prompts. Never leaves the server except in an explicit backup. |
| Default CA | Generated automatically on first start: RSA 4096, SHA-256, 10 years. |
| Regenerate | Allowed, with a user-chosen validity (default 10 years). |
| Replace (regenerate or import) | "Replace and warn": a confirmation names how many devices carry certificates of the current CA; afterwards those show "Issued by a previous CA" and can be renewed in one step. The old CA's public certificate stays exportable and trusted by the server during a transition. |
| Customer CA import | PKCS#12 (`.pfx` / `.p12` + password) or PEM certificate + PEM key (encrypted or not). Root or intermediate; the chain from the file goes to the devices. |
| Export | Public certificate as `.crt` (PEM) or `.cer` (DER). Backup with private key as password-protected `.pfx`. |
| OS trust store | "Install in trusted root store": the server installs the CA into its own machine root store (it runs elevated anyway: administrator / root), and the client installs it into its computer's machine root store (OS elevation prompt when the client is not elevated). Both results are shown separately. |
| Server's own trust check | Certificates issued by the active CA (and previous CAs in transition) count as **Trusted** in the device grid's Certificate column, without any OS change. |
| Device certificates | 365 days (setting), alternative names: IP address and host name always (when known), plus FQDN and `axis-<serial>.local`; common name = the address OADM uses. |
| Renewal | **No automatic renewal.** Warning 30 days before expiry (setting); the user starts "Renew certificates now". |
| 802.1X settings | On the PKI page, like ADM (EAPOL version, EAP identity, RADIUS server CA). Enable/Update runs without a dialog. |
| HTTPS Enable/Update | Installs the certificate and turns HTTPS on; the HTTP setting stays as it is. |
| Security submenu | HTTPS: Enable/Update, Disable; IEEE 802.1X: Enable/Update, Disable; View installed certificates; Delete certificates; Install certificates manually; Renew certificates now. |

## Part 1: core plugin (server)

### CA store

- Plugin setting `ca` (JSON): `{ id, source: "generated" | "imported", certificatePem, chainPem[] (issuers above
  the CA for an intermediate, may be empty), keyProtected (Secrets.Protect(PKCS#8 PEM, "pki:ca:<id>")),
  createdUtc }`. `id` = SHA-256 fingerprint of the CA certificate (upper hex).
- Plugin setting `previousCas` (JSON list, at most 10, newest first): `{ id, certificatePem, chainPem[],
  replacedUtc }`, public parts only (no key). Kept until the user removes one or its certificate expires.
- Plugin setting `issued` (JSON list, the registry of device certificates OADM issued, written by the task
  plugins in part 2; part 1 only reads it): `{ serialNumber, deviceId, purpose: "https" | "dot1x", caId,
  notAfterUtc, issuedUtc }`. Used for the replace warning count and the "previous CA" state.
- Without `Secrets` (host offers no protector) the plugin does not start and its status says so.
- First start without a stored CA: generate the default CA (see below) and log it. A stored CA whose key
  cannot be decrypted (other master key) is an error state on the page with "Generate new CA" / "Import"
  only; nothing is overwritten automatically.

### Generate

- RSA 4096, SHA-256 with PKCS#1 v1.5 signatures, serial 16 random bytes (positive), NotBefore = now - 5 min.
- Subject: `CN=<common name>, O=<organization>` (organization optional). Defaults: common name
  `OADM Root CA <server name>`, organization empty.
- Extensions: BasicConstraints CA=true critical (no path length limit), KeyUsage keyCertSign + cRLSign
  critical, SubjectKeyIdentifier, AuthorityKeyIdentifier (= SKI).
- Validity in years, 1..30, default 10. Generation runs off the request thread (RSA 4096 takes up to a few
  seconds) and the page shows a progress state.
- Only `System.Security.Cryptography` (`CertificateRequest`), no BouncyCastle. Cross-platform.

### Import customer CA

- Input: PKCS#12 bytes + password, or certificate PEM (may contain the chain) + key PEM (PKCS#8, PKCS#1, SEC1,
  encrypted PKCS#8 with password). Bytes travel client -> server inside the Invoke payload (base64, at most
  1 MB); the password is never stored or logged.
- Checks before anything is stored (field errors under the matching field of the import dialog):
  - The file can be read / the password is right ("The password is wrong or the file is damaged.").
  - Exactly one certificate with a private key, and the key matches it.
  - It is a CA: BasicConstraints CA=true and, when KeyUsage is present, keyCertSign.
  - It is valid now and at least 1 year beyond the device certificate validity ("This CA expires on
    <date>, before certificates it would issue."), else a warning field error that blocks import.
  - Key type RSA >= 2048 or ECDSA P-256 / P-384 ("RSA 1024 is too weak.").
- The remaining certificates of the file become `chainPem` (ordered leaf-issuer upwards); a self-signed root
  among them is kept.

### Replace (regenerate or import)

- `previewReplace` returns `{ devicesWithCurrentCa }` (distinct device ids in `issued` with the current
  `caId`). The page asks with the shared confirmation popup:
  "N devices have certificates from the current CA. They keep working, but show 'Issued by a previous CA'
  until they are renewed. Replace the CA?" (no count line when 0).
- On confirm the current CA moves to `previousCas` (public part only), the new one becomes `ca`, the
  server's trust anchors are updated (see below), a `state` event is published, and it is logged.

### Export and backup

- `exportPublic` ({ format: "pem" | "der" }) -> base64 bytes + file name `<cn>.crt` / `<cn>.cer` (PEM includes the
  chain for an imported intermediate, DER is the CA certificate only). Previous CAs: `exportPrevious` ({ id, format }).
- `backup` ({ password }) -> PKCS#12 (certificate, chain, key) encrypted with AES-256-CBC + SHA-256 PBE,
  100,000 iterations; file name `<cn>.pfx`. The password: at least 8 characters, entered twice in the client;
  never stored or logged. The page confirms first: "Whoever has this file can issue certificates that your
  devices and browsers trust. Keep it safe." Logged as "CA backup exported" (without password).
- Import accepts this backup unchanged (restore on another server).

### Trust anchors for the server's own check (SDK + Core change)

- SDK: `ICorePluginContext.TrustAnchors` (DIM null) of a new `ITrustAnchors { void Set(IReadOnlyList<byte[]>
  derCertificates); }`, per plugin (the host keeps one set per plugin id; Set replaces the plugin's set).
- Core: a singleton `TrustAnchorRegistry` (union of all plugin sets, versioned). `CertificateTrustEvaluator`
  gets the registry: when the system chain build fails only because of an untrusted root (or a partial chain)
  and a chain built with `TrustMode = CustomRootTrust` over the anchors succeeds, the result is **Trusted**.
  Self-signed device certificates stay SelfSigned. The registry change marks every device for a full refresh
  only lazily (the next refresh shows Trusted); no immediate poll storm.
- The PKI plugin sets the active CA + previous CAs (+ the root of an imported intermediate) on start and on
  every change.

### OS trust store (server side)

`TrustStoreInstaller` (cross-platform, one implementation per OS, chosen at runtime):

| OS | Install | Check installed | Remove |
|---|---|---|---|
| Windows | `X509Store(StoreName.Root, StoreLocation.LocalMachine)` Add | Find by thumbprint | Remove |
| Linux (Debian/Ubuntu) | write `/usr/local/share/ca-certificates/oadm-<fp16>.crt`, run `update-ca-certificates` | file exists | delete file + update |
| Linux (RHEL/Fedora/SUSE) | write `/etc/pki/ca-trust/source/anchors/oadm-<fp16>.crt`, run `update-ca-trust extract` (SUSE: `/etc/pki/trust/anchors` + `update-ca-certificates`) | file exists | delete file + update |
| macOS | `security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain <file>` | `security find-certificate -Z -a /Library/Keychains/System.keychain` contains the SHA-256 | `security delete-certificate -Z <sha1> /Library/Keychains/System.keychain` |

`<fp16>` = first 16 hex characters of the SHA-256 fingerprint. Commands run with a 30 s timeout, output goes to the
server log. Failures map to plain texts: "Permission denied: the server must run as administrator / root",
"No supported certificate tool found (update-ca-certificates or update-ca-trust)", "<tool> failed: <first line>".
Page method `installServerTrust` -> `{ installed, error }`; `getState` reports `serverTrustInstalled` for the
active CA (checked on every getState, cheap file/store lookup). Firefox and Java keep their own stores: noted in
the page tooltip, not handled.

### Settings (plugin setting `config`)

| Key | Default | Range | Page label |
|---|---|---|---|
| `deviceCertValidityDays` | 365 | 1..3650 (not beyond the CA's NotAfter: shortened, logged) | Device certificate validity (days) |
| `expiryWarningDays` | 30 | 1..365 | Warn before expiry (days) |
| `dot1x.eapolVersion` | 1 | 1, 2 | EAPOL version |
| `dot1x.identity` | `mac` | `mac`, `hostName`, `custom` | EAP identity: MAC address / Host name / Custom |
| `dot1x.customIdentity` | empty | 1..64 printable, required when custom; `{serial}` and `{hostName}` placeholders allowed | (field under the select) |
| `dot1x.radiusCa` | `oadm` | `oadm` = this CA, `imported` | RADIUS server CA |
| `dot1x.radiusCaPem` | empty | one CA certificate (PEM/DER `.crt` / `.cer` import), public only | Import... / View |

`saveSettings` validates (field errors per field, `ValidatingViewModel` on the client mirrors the rules).

### Page methods (`PkiMethods`, camelCase JSON) and events

`getState` -> `{ status, ca { id, subject, commonName, organization, source, keyType, notBeforeUtc, notAfterUtc,
fingerprint, isIntermediate, chainSubjects[] }, previousCas[], config, serverTrustInstalled, devicesWithCurrentCa,
devicesWithPreviousCa, expiringSoon }`; `generate` ({ commonName, organization, validityYears, confirmed });
`import` ({ fileBase64, fileName, password, keyFileBase64?, keyPassword?, confirmed }); `previewReplace`;
`exportPublic`; `exportPrevious`; `removePrevious` ({ id }); `backup` ({ password }); `installServerTrust`;
`saveSettings` ({ config }); `importRadiusCa` ({ fileBase64 }). Replace methods without `confirmed` and with
devices affected return `{ needsConfirmation, devicesWithCurrentCa }`. User-level errors come back as
`{ errors: { field: message } }` or `{ error }`, not as gRPC errors. Event `state` after every change.

## Part 1: page (client)

`HasOwnCards`, `ui:PageHeader.Subtitle` "Issues device certificates for HTTPS and IEEE 802.1X." and
`ui:PageHeader.Trailing` status chip: "CA valid until 2036-10-07" (ok), "CA expires in 20 days" (warning), "CA
expired" / "CA key cannot be read" (error).

```
Certificate authority                                                           (card)
  Name            OADM Root CA SERVER01                     Generated / Imported
  Valid           2026-10-07 to 2036-10-07
  Key             RSA 4096
  Fingerprint     3F:A2:...  (SHA-256, selectable)
  Chain           (only for an imported intermediate: "Issued by Acme Root CA")
  Trusted root store   ✓ Installed on the server   ○ Not installed on this computer
  [Install in trusted root store]  [Export public certificate v]  [Back up...]  [Generate new CA...]  [Import CA...]

Device certificates                                                             (card)
  Device certificate validity (days)   [ 365 ]
  Warn before expiry (days)            [ 30 ]
  Issued: 120 devices · 3 expire within 30 days · 4 from a previous CA          (summary line, from `issued`)

IEEE 802.1X                                                                     (card)
  EAPOL version      [ 1 v ]
  EAP identity       [ MAC address v ]   (Custom: text field below, hint "{serial} and {hostName} are replaced")
  RADIUS server CA   [ This OADM CA v ]  [Import...] [View]   (imported: subject + valid until line)
                                                                    [ Save ]

Previous certificate authorities  (card, only when there are any)
  Name | Valid until | Replaced | [Export] [Remove]
```

- "Install in trusted root store": calls `installServerTrust`, then installs on the client computer
  (`ClientTrustStoreInstaller`, same table; Windows not elevated: `certutil -addstore Root <file>` started with
  `runas` (UAC prompt); macOS: `osascript ... with administrator privileges` running `security add-trusted-cert`;
  Linux not root: `pkexec` running the install, when `pkexec` is missing the error shows the exact `sudo` command).
  Result as two StatusChips in the "Trusted root store" row: server and this computer, each Installed / Not
  installed / Failed (tooltip = error). Fake mode: server part simulated, client part disabled.
- When client and server run on the same computer, both installs target the same machine store; the second
  sees "already installed" and skips.
- Generate dialog (`ValidatingViewModel`): Common name (required, 1..64), Organization (optional, max 64),
  Validity (years, 1..30, default 10); RSA 4096 shown as a fixed info line. Then the replace confirmation.
- Import dialog: shared `FileRow` ("Choose file..." `.pfx`, `.p12`, `.pem`, `.crt`, `.key`), password
  (`ui:PasswordBox`); for a PEM certificate without a key a second `FileRow` "Private key" + key password. Server
  field errors under the fields. Then the replace confirmation.
- Export: menu button PEM (.crt) / DER (.cer), save dialog, last folder remembered per client.
- Back up: password + confirm (`ui:PasswordBox`, at least 8, must match), confirmation popup, save dialog.
- Shared controls only (CardHeader, FormField, StatusChip, FileRow, PasswordBox, MessageWindow, DialogTitleBar,
  DialogFooter); field errors under the fields; no jargon beyond the established terms (certificate, CA, 802.1X).

## Part 2: task plugins (contributed, group Security)

Device API research, request examples and the 10.0.0.48 fixtures: `docs/specs/pki-research/`.

### Decisions (user)

| Topic | Decision |
|---|---|
| Key generation | On the device: `create_certificate` -> `get_csr` -> OADM signs -> `PATCH` the signed certificate. The private key never leaves the device. |
| Key type | RSA 2048. Keystore: the device's default keystore (secure element SE0 / TEE0 / TPM0 when present, else software). |
| Firmware | REST certificate API `cert` v1 only (AXIS OS 11.11 and later). Older devices fail "Needs AXIS OS 11.11 or later. Nothing was changed." No SOAP fallback for keys and CSRs. |
| 802.1X safety | Checks + confirmation: the task fails before any write when the device clock is more than 5 minutes off the server clock or the chain is incomplete; the client asks with `ui:MessageWindow` "Devices on ports that enforce 802.1X become unreachable if authentication fails." (Cancel / Enable). |
| HTTPS: Disable | Connection policy HTTP only (like ADM), certificates stay; OADM switches its own connection to HTTP. Confirmation: "Video systems that use HTTPS lose the connection to these devices." |
| Renewal | Manual only ("Renew certificates now"). |

### Compatibility (`PkiCompatibility`)

- `getApiList` does not list the certificate API: the task reads `GET /config/discover` (cached per device for 10 min in
  the plugin, re-read fresh in `ExecuteAsync` before the first write) and requires `cert` v1 (>= 1.0).
- HTTPS: additionally SOAP `aweb:GetWebServerTlsConfiguration` must answer (web server TLS service).
- 802.1X: `network-settings` 1.x with the `wired.8021X` object in `getNetworkInfo`.
- `CanRun` (cached data only): AXIS OS >= 11.11 from `FirmwareVersion`, `network-settings` in `Apis` for 802.1X; the
  exact check happens in the task ("Check compatibility" step).

### Aliases and the issued registry

- Device certificate alias: `OADM HTTPS <yyyyMMdd-HHmmss>` / `OADM 802.1X <yyyyMMdd-HHmmss>`; CA certificate alias
  `OADM CA <first 8 hex of fingerprint>` (RADIUS CA: `OADM RADIUS CA <8 hex>`). Aliases are percent-encoded in URLs.
- Every signed certificate is appended to the plugin's `issued` registry (serial, deviceId, purpose, caId, notAfter,
  alias). Older OADM certificates of the same purpose on the device are deleted after the switch succeeded (step
  "Remove previous OADM certificate"); never certificates OADM did not issue.

### Certificate content

Subject `CN=<address OADM uses>`; SAN `IP:<IPv4/IPv6 address>` always, `DNS:<host name>` and `DNS:<FQDN>` when known
(from the device's network info), `DNS:axis-<serial lowercase>.local`; validity `deviceCertValidityDays` from now - 5 min,
capped at the CA's NotAfter; EKU serverAuth (HTTPS) / clientAuth (802.1X); KeyUsage digitalSignature + keyEncipherment;
AKI = CA SKI. The CSR's public key is checked (RSA >= 2048 or EC) and OADM puts in its own subject and SAN, never the
CSR's. The chain (CA + intermediates of an imported CA) goes to the device as CA certificates.

### Tasks (one per device, steps, names)

1. **HTTPS: Enable/Update** (id `oadm.pki.https-enable`). Steps: Check compatibility, Read web server settings, Install
   CA certificate (Skipped "Already installed"), Create key on the device, Get certificate request, Sign certificate,
   Install certificate, Switch web server to the new certificate (`SetWebServerTlsConfiguration`: the new alias, policy
   unchanged; when the policy is HTTP only it becomes HTTP and HTTPS, because Enable must enable), Verify HTTPS (new TLS
   handshake: served fingerprint = the new certificate; OADM pins it and switches its scheme to https through a host
   API, so no CertificateChanged status), Remove previous OADM certificate. Name "Enable HTTPS" / "Update HTTPS
   certificate" (device already serving an OADM certificate).
2. **HTTPS: Disable** (`oadm.pki.https-disable`, confirmation). Steps: Check compatibility, Read web server settings,
   Set HTTP only, Verify (HTTP answers; OADM's device record switches to http). Name "Disable HTTPS".
3. **IEEE 802.1X: Enable/Update** (`oadm.pki.dot1x-enable`, confirmation). Steps: Check compatibility, Check device clock
   (fails beyond 5 min: "The device clock is 12 min off. Set the date and time first. Nothing was changed."), Install CA
   certificates (RADIUS server CA from the PKI page + chain), Create key on the device, Get certificate request, Sign
   certificate, Install certificate, Set 802.1X configuration (`setWired8021XConfiguration`: enabled, EAP-TLS,
   identity from the page setting (MAC = serial, host name, custom with `{serial}` / `{hostName}`), EAPOL version,
   certClient = new alias, certsCA), Verify 802.1X settings (read back), Remove previous OADM certificate. Name "Enable
   IEEE 802.1X" / "Update IEEE 802.1X certificate".
4. **IEEE 802.1X: Disable** (`oadm.pki.dot1x-disable`). Steps: Check compatibility, Set 802.1X off, Verify. Certificates
   stay. Name "Disable IEEE 802.1X".
5. **Renew certificates now** (`oadm.pki.renew`). Renews what OADM issued on the device (from `issued` + the device's
   certificate list): per purpose a new key + CSR on a new alias, sign, install, switch (web server / 802.1X), verify,
   remove the old one. Skipped purposes say why ("No OADM 802.1X certificate on this device"). Name "Renew certificates"
   ("Renew HTTPS certificate" / "Renew IEEE 802.1X certificate" when only one applies).
6. **View installed certificates** (`oadm.pki.view`, dialog, read-only, no task): a window like ADM's "Installed
   Certificates": grouped Client / Server / CA certificates (expanders with counts), columns MAC address, Address, Issued
   by, Issued to, Valid to, In use (HTTPS, 802.1X), Source (OADM / other); SearchBox; Refresh. Reads through
   `ITaskPluginQuery` (`listCertificates`, at most 4 devices at a time, progress row). Virtualized; with many devices it
   loads progressively.
7. **Delete certificates** (`oadm.pki.delete`, dialog): the same list with check boxes; certificates in use by the web
   server or 802.1X, and the Axis factory device ID certificates, are greyed with a tooltip and cannot be selected.
   Payload: aliases per device (the server re-checks "in use" before deleting). Steps: Check compatibility, Read
   certificates, Delete certificate <alias> (one step each), Verify. Name "Delete certificate <alias>" / "Delete N
   certificates".
8. **Install certificates manually** (`oadm.pki.install`, dialog): choose `.pfx` / `.p12` files (one password for all,
   like ADM) and the purpose (HTTPS / 802.1X / CA only); files are matched to devices by MAC address, IP address or FQDN
   in the subject CN or SAN (match must be unique; a table shows file -> device, unmatched rows red in the Status
   column). Upload via `ITaskDialogContext.UploadAsync`; the task installs with `install_from_pkcs12` (base64, password
   in memory only), then switches HTTPS or 802.1X like 1 / 3 when that purpose was chosen. Name "Install certificate
   <file>".

Context menu: group **Security**; display names "HTTPS: Enable/Update", "HTTPS: Disable", "IEEE 802.1X: Enable/Update",
"IEEE 802.1X: Disable", "View installed certificates", "Delete certificates", "Install certificates manually", "Renew
certificates now" (all at most 32 characters).

### Device grid

- The Certificate column shows "Trusted (OADM CA)" for certificates of the active CA and "Issued by a previous CA"
  (warning) for previous CAs; the Certificate expires column warns `expiryWarningDays` before expiry (setting of the PKI
  page; default 30, replacing the fixed 30).

### Open points (need an approved hardware write test on 10.0.0.48)

- Whether `PATCH certificates/<alias>` accepts a PEM chain (otherwise the chain goes only as CA certificates).
- `SetWebServerTlsConfiguration` as a write on 12.11.
- 802.1X verification against a real RADIUS server and switch is out of scope; the read-back of the settings is the
  verification.
