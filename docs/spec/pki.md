# PKI, the certificate control center

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
The plugin's details: `docs/spec/bundled-plugins.md` (PKI) and `plugins/Oadm.Plugins.Pki/README.md`. The two servers of the
package have their own specs: `docs/spec/est-server.md` and `docs/spec/radius-server.md`.

PKI is the one place for everything about certificates: one page with the **policies** (every rule: which certificate a
device gets, what a certificate lets it do) and the **certificates** (every certificate OADM knows, the CAs among them),
plus the 802.1X and EST servers as tabs. Services that use certificates (802.1X server, EST server, the PKI tasks) keep no
certificate rules of their own: they apply PKI's policies and show only how they run (server state, switches, sessions,
logs, enrollments).

## One plugin: PKI with the 802.1X and EST servers

- **One package, three core plugins, one rail page:** PKI (`oadm.pki`), 802.1X server (`oadm.radius`) and EST server
  (`oadm.est`) in one package (`docs/spec/plugins.md` "Merged packages"). One settings store, one backup, one set of
  permissions, one policy list in PKI's own code; no contract between plugins. One folder `plugins/Oadm.Plugins.Pki`
  (+ `.Client`); the EST and 802.1X code keeps its own folders inside it (`Est/`, `Radius/`), each with its source-available
  `LICENSE` (`docs/spec/licensing.md`); the rest of PKI is Apache-2.0. THIRD-PARTY-NOTICES and the plugin's license notice
  name both.
- **On and off:** PKI is always on. The 802.1X and EST servers are off by default and are turned on on their own tab
  ("Turn on" in the tab's off state, the stored choice `Plugin:<id>:serverOn`). They open their ports (UDP 1812, TCP 9454)
  only while they run.
- **OADM PRO:** the two servers run only with an active OADM PRO license or trial (`PluginRegistry.ProActive`). Without it
  the off state reads "The EST server needs OADM PRO" / "The 802.1X server needs OADM PRO" with `ProLicenseTexts.NeedsPro`
  and offers no Turn on (Start 30-day trial while the trial is available, `docs/spec/licensing.md` "Trial"); a license that
  ends stops them, a new one starts them again with the kept choice. PKI's own parts (CAs, issuing, Certificates) never need
  a key: OADM itself is never limited. The Plugins page's PKI row says "802.1X and EST servers need OADM PRO" in its
  description, no PRO chip.
- **Ids stay:** the core plugins keep the ids `oadm.est` and `oadm.radius`, and the host keys settings (`Plugin:<id>:*`),
  records, the data folder (`plugin-data/<id>`), events, permissions (`<id>:show`, `<id>:manage`,
  `task:oadm.est.setup:enroll`), tasks, audit names and services by core plugin id, so stored rules and roles keep working.
- **From OADM 4.1, once:** a plugin folder `oadm.est` or `oadm.radius` left from 4.1 is not loaded (the loader skips it with
  one log line "Merged into PKI"; the installers remove it). The only choice stored per package, `Plugins.Enabled`, is taken
  over once (`MergedPlugins.TakeOverAsync`: an entry of `oadm.est` / `oadm.radius` becomes `Plugin:<id>:serverOn`, a choice
  made since wins, the entries leave `Plugins.Enabled`). The policies are converted once too ("Converting existing
  settings"). Details: `docs/spec/plugins.md` "Merged packages" and "PRO parts of PKI".
- **Sites:** the link to the site node carries no plugin id (a relay is opened by protocol, port and reason, "EST server"
  on TCP 9454), so nodes of 4.1 and later relay unchanged; the relay opens only while the EST server runs.

## The PKI page

One rail entry **PKI** (group Security) with fixed tabs (`ui:PageTab` without X): **Certificates** | **Policies** |
**802.1X server** | **EST server** (`PkiViewModel.Tabs`). Subtitle: "Certificates, their policies, and the 802.1X and EST
servers."

- **Certificates** (see "Certificate authorities" and "Certificates"): two cards that share the tab's height, a
  `paneSplitter` between them: **Certificate authorities** (300 px at first, at least 250; `PkiListPart.Authorities`) and
  **Certificates** (at least 300 px; `PkiListPart.Certificates`, `CertificatesRequest.NoAuthorities`).
- **Policies** (only for users with all devices): one grouped table and a side panel (see "Policies").
- **802.1X server** and **EST server**: the pages of the core plugins `oadm.radius` and `oadm.est` (their content:
  `docs/spec/radius-server.md`, `docs/spec/est-server.md`). They have no rail entry: their client pages say
  `ICorePluginPage.TabOf` = `oadm.pki` and the host hands their views to the PKI page (`docs/spec/plugins.md` "Pages shown
  as tabs"). The server parts keep their pages (`HasPage`), so the "show" and "manage" permissions and start page choices
  stay. While a server does not run its tab shows the off state; a server that is on but does not run (the license ended,
  a start that failed: "The EST server did not start") offers Turn off there too. Turn on and Turn off show a progress row
  ("Starting the EST server") while they run, Start 30-day trial "Starting the trial".
- A server's tab shows only for users whose role has that core plugin's "show" permission (the server lists only the pages
  the role may show, the host follows role changes). With only one tab the tab row is left out. A role that shows a server
  but not PKI gets that server's page in the rail (there is no PKI page to hold the tab).
- Only the open tab's view is in the window: a server tab reads its state when it is opened (like a page). A click on the
  rail entry keeps the tab that was open.
- Everything that opens `plugin:oadm.radius` or `plugin:oadm.est` (`OpenAsync`, a role's start page, links of the Plugins
  page) opens the PKI page on that tab; `plugin:oadm.pki` opens it on Certificates.
- The link **"N policies applied"** on a server tab (left in its Save row) opens the Policies tab; its tooltip lists each of
  those policies in two lines, the name bold and the summary sentence below it (`Servers/PolicyLinesTip`, the state's
  `PolicyLines` from `PolicyStore.LinesOf`). The Policies tab then highlights that server's policies for 3 seconds
  (`PkiTabRequest.Set(tab, PolicyHighlight.Dot1x / Est)`, `PoliciesViewModel.Highlight`, `HighlightTime`; the rows get the
  theme class `DataGridRow.highlight` of a `DataGrid.highlightRows` grid, a tint that fades in and out; the first one
  scrolls into view; a search that would hide one is cleared; a jump before the first read waits for the policies).
- Managing needs "Manage the certificate authority" and all devices. Users without it see the page read only (no toolbar
  buttons, no row actions but View). Users limited to a device group see only the device certificates of their devices (no
  Policies tab, no actions); their camera's certificate is found by the camera's serial like the list finds it (also one
  issued while the camera had another device id), and Used by of the CAs reaches them without policy names (the policies
  are server-wide).

## Certificate authorities

There is no special OADM CA: every CA is an ordinary **root CA** (self-signed) or **intermediate CA**, whatever made it.

| Where it comes from | Example | Issues certificates | Stored as |
|---|---|---|---|
| First start of a new server | "OADM Root CA SERVER01" | yes | setting `ca`, id `oadm` (the built-in policies name it) |
| **Create CA** | "Contoso Issuing CA" | yes | `importedCas` with `Created` |
| **Import** > **CA with its key** (PKCS#12 or PEM + key) | "Contoso Root" | yes | `importedCas` with the key |
| **Import** > **CA certificate** (PEM or DER, no key) | "Corp RADIUS CA" | no | `importedCas` without a key |
| Built in | "Axis device ID" | no | the RSA and ECC roots and intermediates of `plugins/Shared/DeviceId` |
| OADM before 4.5 | "OADM CA 2025" | no | `previousCas` (a CA replaced before 4.5; pruned once it ended) |

- **First start:** a new server makes one root CA "OADM Root CA <server name>" (RSA 4096, 10 years) in the background
  (progress "Creating the certificate authority"): the built-in HTTPS policy and the server policies need an issuer. From
  then on it is an ordinary root CA: it can be deleted when nothing uses it, and once deleted it is never made again
  (setting `caDeleted`, written before `ca` is removed). An update keeps the stored CA as it is, with its name. While it is
  being made the issuing and EST sign-in choices offer it (`PolicyStore.DefaultCaPending`, named "OADM Root CA"); a server
  without secret protection offers it too.
- **Card:** **Create CA** and **Import** (menu: **CA certificate** and **CA with its key**, both add a row) left, the search
  right; no groups or filters (the list's use filter CA). Empty: "No certificate authorities yet. Use Create CA or Import."
- **Rows:** Name, Type (only **Root CA** or **Intermediate CA**; a root CA adds its OS root store badges, see
  "Certificates"), Issuer ("Self-signed" for a root CA), **Key** (a CA with its key: its key type, "RSA 4096", "ECC P-256";
  a CA certificate only: **No key**; a key the master key cannot read: **Key cannot be read**; `CaKeyTexts`), Valid until,
  **Used in** (the names of the policies that use the CA, the first as a neutral badge and "+N", every name as tooltip,
  nothing for a CA no policy uses; only the CA list: `IssuedCertificateListViewModel.ShowsUsedIn`), ⋯.
- **Row menu:** **View** (`ui:CertificateWindow`), **Export** (PEM, DER: the public certificate), **Install in trusted root
  store** (every root CA, permission `oadm.pki:trustServer`: the server's machine root store, then this computer's;
  confirmed with the SHA-256 fingerprint; `InstallTrustRequest.Fingerprint`, `PkiState.RootTrust`), **Back up** (every CA
  with its key: PKCS#12 with its chain, `BackupRequest.Id`), then a separator and **Delete** (red). Several chosen CAs get
  no menu: each CA is deleted on its own.
- **Create CA** (`createAuthority`, `CreateCaRequest`, `CreateCaWindow`: one form, no card): **Name** (hint "The name
  devices and browsers show for this CA."; unique among the CAs: "Another CA has this name."), **Type** (Root CA
  (self-signed), Intermediate CA), **Signed by** (only for an intermediate: every CA with its key, intermediates too;
  "Choose a CA with its key.", "This CA has expired.", "This CA cannot sign other CAs.", "This CA may not sign other CAs."
  for a path length of 0; none listed: "No CA with its key yet. Create a root CA first."), **Key** (RSA 4096 preset; ECC
  P-256, RSA 2048, RSA 3072; `CaKeys`), **Valid (years)** (1 to 30, 10 preset). `CaGenerator`: SHA-256, NotBefore 5 minutes
  early, BasicConstraints CA without a path length limit, keyCertSign + cRLSign; an intermediate ends at the latest with
  the CA that signs it and keeps that CA's chain. Progress row: "Creating the CA. This takes a few seconds." The new CA
  joins the list like an imported CA with its key (key encrypted with the master key, never logged) and can issue at once.
  Its key never left OADM, so it may be an EST sign-in CA (`StoredImportedCa.Created`, `PolicyStore.CreatedCas`).
- **Import:** `import` (`CaImporter`) adds a CA with its key, `importAuthority` a certificate only; nothing replaces another
  CA and nothing asks. The same CA imported before without its key, or with a key the master key cannot read, gets the
  key; one with its key already listed (also the first-start CA or a previous one): "This CA is already in the list.". A CA
  backup of another OADM server imported here is such a CA. `importAuthority` accepts a CA that is only inside the Axis
  device ID entry, so it can become a CA of its own. Created and imported CAs together: at most 50 ("At most 50 CAs can be
  created or imported. Delete one first.").
- **Delete** (`deleteAuthority`, `DeleteAuthorityRequest`; confirmation "Delete the CA <name>?" with "Its certificates stay
  in the list. This cannot be undone.", red Delete) takes **every** CA: the first-start root CA, created, imported and
  previous CAs, each Axis device ID root and intermediate (stays deleted after an update: setting `deletedAxisCas`, their
  SHA-256) and a row that cannot be read. Refused with the reason in a message while a policy names it ("Used by the
  policy <name>. Change or delete the policy first."; issuing, 802.1X trusted CA, EST sign-in CA, RADIUS server CA;
  `PolicyStore.NamingCa`), while a service uses it ("Used by the 802.1X server. Remove it there first.") and while it signed
  a CA that is still listed ("It signed <name>. Delete <name> first."). Its certificates stay in the list; nothing else is
  deleted or blocked. The 802.1X and EST servers stop trusting it at once (`CaChanged`).
- **A row that cannot be read** (the stored first-start CA is damaged, or a created or imported CA whose certificate cannot
  be read; `PkiService.UnreadableRows`, `IssuedCertificateInfo.Unreadable`): name "Unknown CA", no badges, Issuer and Valid
  until "-", the red state "Cannot be read", only **Delete** in its menu, refused while a policy names it like any CA. The
  stored value is never overwritten by itself (one error in the log; a backup restores it). Delete first writes it as it is
  to `ca-unreadable-<UTC yyyyMMdd-HHmmss-fff>.json` in the PKI plugin's data folder (`ICorePluginContext.DataDirectory`, part
  of the backup) for support, never logged; without that file nothing changes ("The stored CA cannot be kept for support.
  Nothing was changed.").
- **The PKI status** (`ui:StatusChip` with its detail, below the CA card's toolbar row) says only that the server cannot keep
  a CA ("CA cannot be stored on this server") or that the first-start CA is being made or could not be made. A key that
  cannot be read, an expired or expiring CA show in the CA's own row.
- **Keys:** a CA key is never exported on its own; it leaves OADM only in Back up and the server backup. The trusted root
  store of the server stays as it is (what OADM trusts when it connects to devices).
- **Several issuing CAs:** any CA with its key can issue (`CertificateAuthorityInfo.Issues`; `PkiService.IssuingCa(id)`,
  `IssuingCas()`). Issuing policies pick it as **Issued by** (`PolicyStore.IssuingCaChoices()`); every signing path signs
  with the CA of its policy: the tasks (`CertificateDeployment.RequireCa(service, policy)`, Renew per use), Create and Sign
  request, the CA service (`DeviceCertificateRequest.IssuerId`: EST profiles through their issuing policy). The validity is
  cut to the issuing CA's end.
- **Chains:** a certificate of an intermediate CA goes out with its chain (the intermediates from its issuer up, never the
  root; `CertificateDeployment.IssuerChainPems`, `Ca/IssuerChains`): to the camera for HTTPS and the IEEE 802.1X client
  certificate, in EST answers and `cacerts`, in the servers' TLS chains. REST `cert` v1 gets the certificate followed by the
  chain; SOAP loads both as one DER sequence and reads the entry back. A camera that refuses the chain, or keeps anything
  but the certificate itself, gets the certificate alone (`DeviceCertificates.InstallSignedAsync`, `SignedInstall`); the
  task log says which ("Certificate "<alias>" installed with its chain (1 CA certificate)." or the Warning "The device
  refused the certificate with its chain (...); the certificate was installed alone."). A root CA's certificates go alone.
  The 802.1X server builds the path through PKI's intermediates (`docs/spec/radius-server.md` "Who passes").
- **Trust:** the first-start CA, the previous CAs and every created or imported CA with its key (each with its chain) go to
  the server's trust anchors (`ctx.TrustAnchors`), so device certificates they issued are Trusted in the device grid. The
  renewal issuers (`ITrustAnchors.SetRenewalIssuers`) are the CAs that issue and the previous OADM CAs, so EST renewals pin
  without "Certificate changed" (`docs/spec/est-server.md` "Certificate renewals and pinning"). An imported CA without its
  key keeps none of PKI's lists (its certificates come from elsewhere; `TrustedIssuer.KeepsLists`).
- A CA that leaves the list (deleted, a previous one that ended) drops out of every policy at once (fail closed).
- **Used by** (View of a CA) has one group per use: "802.1X: ..." (802.1X trusted CA policies), "Issuing: ..." (issuing
  policies), "RADIUS server CA: IEEE 802.1X" (the IEEE 802.1X certificate policies whose cameras check the RADIUS server
  with it), then the services (`SetUsedBy`).

## Policies

### The table and the side panel

The Policies tab is one card and a side panel, like Intune (`PkiView` `PoliciesPage`, `PoliciesViewModel`):

- **Table:** **Create** (the tab's accent button; off with the reason at 200 policies) and **Search policies** (name, type,
  summary) in the toolbar row; the policies in groups with a collapsible header each (`PolicyTableGroups`,
  `DataGridCollectionView`; the header shows the chevron, the group's icon and its name, the group key a `ui:GridGroup`
  with `PolicyTableGroups.IconOf` / `KeyOf`: key, server, network, download, bell; a group without policies shows no
  header), in this order: **Issuing policies** (HTTPS certificate, IEEE 802.1X certificate), **Server policies** (802.1X
  server, EST server), **802.1X access policies** (802.1X trusted CA, VLAN, Block), **EST enrollment policies**
  (Enrollment, EST sign-in CA), **Expiry warnings**; built-in policies first in their group, then the others in age order.
  Columns: **Name** (a red dot `Ellipse.problemDot.error` with the reason as tooltip for a later overlap or a CA that can
  no longer issue, also when no CA can issue at all; the **Built-in** badge `Border.badge.system` for a system policy),
  **Type** ("VLAN", "802.1X trusted CA"), **Summary** (the sentence, dimmed). At most 200 policies
  (`PolicyLimits.MaxPolicies`, "Use at most 200 policies.") in a virtualized DataGrid. A search that matches nothing says so
  in one line.
- **Without OADM PRO** the table shows only the HTTPS certificate, IEEE 802.1X certificate and Expiry warning policies
  (`PolicyKinds.IsPro`); stored PRO policies stay and come back with a license.
- **Side panel** (theme `Border.sidePanel` with `Border.sidePanelContent`, 560 px): it slides in at the right of the tab
  (0.2 s) and pushes the table to the left; nothing covers the table. A click on a row opens that policy's form; **Create**
  opens "Create a policy" with every type and what it does in one line (never a second 802.1X server or EST server policy),
  **Next** shows the chosen type's form. The title is the policy's name (with the Built-in badge) or "New VLAN policy", the
  type below it; the close button top right. At the bottom: **Delete** apart on the left (red, not for a built-in policy),
  the status ("Not saved"), **Cancel**, **Save** (Next while the types show). Save stores the policy and closes the panel;
  Cancel and the close button ask first when the form has changes ("Discard changes": Discard / Cancel), so does a click on
  another row or Create.
- **Form** (`PolicyFormView`, `PolicyEditorViewModel`): the kind's description (`PolicyKinds.Description`), the name (not
  for a system policy), the kind's own fields, and for kinds with devices **Devices**: All devices, or the chosen sites, tags
  and devices in one line with **Choose devices** (the shared `DevicePickerWindow` in its group mode over a copy of the
  choice, made only on that click, never one over 30,000 devices per click on a row; Cancel changes nothing). The Devices
  field is a field of the form's validator (none chosen, the server's overlap refusal until the choice changes), so it
  blocks Save like any other error. A system policy keeps its name and devices (a system Expiry warning for chosen devices
  names them below the days). Save is off without a change ("No changes to save."); "changed" is worked out once per edit.
  A save locks the form and its Delete; a refusal of a field the form does not show (the kind) or of the whole policy goes
  into a message.
- **Concurrent changes:** a reload (another user's change) keeps a form with changes not saved, refreshes an unchanged one
  and closes the panel when its policy is gone; a form whose dialog is open (Choose devices, a question) stays as it is, and
  a click on a row opens the row as it is after the question. Every stored policy has a `Revision` (PKI counts its saves
  and the system's certificate write); a save sends the revision it read and PKI refuses one over a later change ("Changed
  by someone else. Reload.", `PolicyFields.Policy`), so no save overwrites another user's change unseen.
- **Read only** (inputs disabled, no Create, Save or Delete) without "Manage the certificate authority". A role change keeps
  the open form and what was typed in it; only a change of what the user may do here acts: losing Manage makes the form of
  a stored policy read only and closes a new one, a device group that hides the tab closes it
  (`PoliciesViewModel.NotifyRole`).

### Kinds

| Group | Kind | Fields | Devices | Policy there from the start |
|---|---|---|---|---|
| Issuing policies | **HTTPS certificate** | Issued by (a CA with its key), Valid (days, 1 to 3,650), Key (RSA 2048, ECC P-256: the key the device makes) | no | Built-in "HTTPS" (the first-start root CA, 365 days, ECC P-256; RSA 2048 where an older OADM ran before) |
| Issuing policies | **IEEE 802.1X certificate** | Issued by, Valid (days), Key, EAPOL version (1, 2 or 3), EAP identity (MAC address, Host name, IP address, Last octet of the IP address, Custom), RADIUS server CA (a CA of the list) | no | Built-in "IEEE 802.1X" (the first-start root CA, 365 days, ECC P-256, RADIUS server CA the first-start root CA; a new installation EAPOL version 2 and the IP address as identity; one used before keeps its identity, EAPOL version and RSA 2048) |
| Server policies | **802.1X server** (one only, PRO) | Certificate (see "Server certificates"; ECC only), Minimum TLS version (1.0, 1.1, 1.2, 1.3; 1.0 and 1.1 with the hint "Only for old devices: TLS 1.0 and 1.1 are no longer considered secure."; the cipher suites follow it, no selection), Largest EAP packet (500 to 1,400 bytes), Reauthentication (Switch decides, or every 10 to 1,440 minutes) | no | Built-in "802.1X server" (the shared certificate "802.1X and EST server", TLS 1.2, 1,020 bytes as RFC 3748 3.1, Switch decides) |
| Server policies | **EST server** (one only, PRO) | Certificate (see "Server certificates") | no | Built-in "EST server" (the shared certificate "802.1X and EST server") |
| 802.1X access policies (PRO) | **802.1X trusted CA** (stored `allowCa`) | CA; Only devices OADM manages (on) or Any device with a valid certificate | no | "OADM CA" (the first-start root CA) and "Axis device ID", both only managed |
| 802.1X access policies (PRO) | **VLAN** | VLAN ID (1 to 4094) | yes | none |
| 802.1X access policies (PRO) | **Block** | none (the devices only) | yes | none |
| EST enrollment policies (PRO) | **Enrollment** | Profiles: All profiles, Chosen profiles (a check box per EST profile, also one a policy names that the server no longer has), No enrollment | yes | Built-in "All devices" (every device OADM manages, all profiles) |
| EST enrollment policies (PRO) | **EST sign-in CA** (stored `signInCa`) | CA; For: new certificates (hint "Cameras may renew with it too.") or renewals | no | "Axis device ID" (new certificates) and the first-start root CA under its real name, e.g. "OADM Root CA SERVER01" (renewals) |
| Expiry warnings | **Expiry warning** | Days before the end (1 to 365) | yes | Built-in "All devices" (30 days) |

A new server thus counts 3 policies on the 802.1X tab (802.1X server, two 802.1X trusted CA) and 4 on the EST tab (EST
server, Enrollment, two EST sign-in CA). The 802.1X tab counts 802.1X server, 802.1X trusted CA, VLAN and Block; the EST
tab EST server, Enrollment and EST sign-in CA.

- **System policies** (`PolicyStore.IsSystem`, the Built-in badge): the first (oldest) policy of HTTPS certificate, IEEE
  802.1X certificate, 802.1X server, EST server, Expiry warning and Enrollment. OADM needs them, so they have no Delete (the
  server refuses it: "OADM needs this policy. Change its values instead."); their values stay editable, also the devices of
  the Expiry warning and Enrollment ones (else their "All devices" would overlap every policy added next to them). Users add
  their own policies: further issuing policies (picked in the task dialogs and the EST profiles), 802.1X trusted CA, VLAN,
  Block, EST sign-in CA, and Expiry warning or Enrollment for some devices.
- **Summary:** one plain sentence that says what the policy does with its values (one builder per kind,
  `PolicyStore.Summary`; "these devices" stands for the Devices column; up to two lines in a rule list, the whole sentence as
  tooltip). Examples: "Certificates with 30 days or less left show a warning and raise Certificate expires soon.", "Enable
  HTTPS issues certificates from OADM Root CA SERVER01, valid 365 days, with an ECC P-256 key.", the IEEE 802.1X one adds
  "Cameras sign in with their IP address." and "Cameras check the RADIUS server with OADM Root CA SERVER01.", "The 802.1X
  server shows the certificate 802.1X and EST server (OADM Root CA SERVER01), until 2035-10-25. It accepts TLS 1.2 or later
  with secure cipher suites, sends EAP packets of up to 1,020 bytes and lets the switch decide when cameras sign in again."
  ("... asks cameras to sign in again every 60 minutes."), "The EST server has no certificate yet.", "The 802.1X server lets
  in devices OADM manages that have a certificate from ..." / "... lets in any device with a valid certificate from Contoso
  Root.", "The 802.1X server puts these devices in VLAN 30.", "The 802.1X server refuses these devices.", "These cameras may
  get certificates from the EST server for every profile.", "Cameras sign in to the EST server with their Axis device ID for
  new certificates and renewals.", "Cameras with a certificate from ... may sign in to renew it.". No preview line in the
  editor.

### Rules

- **Issuing policies have no devices.** The task dialogs (Enable HTTPS, Enable IEEE 802.1X, Renew certificates, Install
  certificates for IEEE 802.1X) let the user pick the issuing policy of their kind; the first one is preselected and its
  summary sentence shows below the field. The run uses the policy's values as stored (no values of its own in the dialog).
  EST issues by profile: an EST profile names its issuing policy (HTTPS certificate for HTTPS and RTSPS, IEEE 802.1X
  certificate for IEEE 802.1X and MQTT). Users add more issuing policies, e.g. "HTTPS 2 years", or an IEEE 802.1X one with
  RSA for devices before AXIS OS 12 (see "Older AXIS OS").
- **No overlaps:** saving is refused when a device is in two policies of the same kind ("10.0.0.48 is in Building B and
  PTZ.", "... and 3 other devices are in ..."), checked against the devices of now, one pass over the devices per other
  policy of the kind, only over the devices the policy holds. Only an overlap the save adds refuses; one stored before (e.g.
  by the conversion) never blocks an edit of something else. The same CA in two 802.1X trusted CA ("Another 802.1X trusted
  CA policy has this CA.") or two EST sign-in CA policies ("Another EST sign-in CA policy has this CA.") is refused too.
- **Later overlaps** (a tag or site change): both rows show a warning naming the one that applies (`PolicyStore.Rows`): the
  safer one. Block over VLAN; of two VLAN policies the older; of two Expiry warnings the longer; of two Enrollment policies
  a profile must be allowed by both (the warning names the narrower policy when one is within the other). Block and VLAN may
  hold the same device (different kinds, no warning): Block refuses first. Overlaps of Block policies change nothing.
- **All devices:** the device selector offers "All devices" next to "Chosen devices" (every kind with devices). It holds
  every device, also those of sites added later and, for the 802.1X kinds, devices OADM does not manage (a certificate of an
  802.1X trusted CA for any device: an "All devices" VLAN gives its VLAN, an "All devices" Block refuses it); for Enrollment
  every device OADM manages (EST never serves another). It overlaps every other policy of its kind: saving is refused as for
  any overlap ("All devices includes every device, also those of PTZ."); converted data keeps the overlap with its warning.
- **Other checks on save** (`PolicyRules`, the same on the page and in the store): the name (1 to 64 characters, unique per
  kind: "Another VLAN policy has this name.", so "All devices" can be an Expiry warning and a VLAN policy), the kind's fields,
  one policy only of the server kinds ("Only one 802.1X server policy is allowed."), a CA PKI does not list ("This
  certificate authority is not in the PKI."), an issuing CA without its key ("This CA cannot issue certificates. Choose a CA
  with its key."), sites that no longer exist ("This site no longer exists."), an EST sign-in CA that is not a choice
  ("Cameras sign in with the Axis device ID or a CA created in OADM."), a chosen profile the EST server does not have ("The
  EST server has no profile web."; the EST core plugin hands the store `ProfileNames`, also while the server is off), at
  most 200 policies. A policy keeps its kind ("A policy keeps its kind. Add a new policy instead."; the editor offers the kind
  for a new policy only).
- **Deleting is refused** for a system policy and for an issuing policy an EST profile names ("Used by the EST profile
  https."; the EST core plugin hands the store `ProfileUses`, read from its settings, also while the server is off).
- A device that no Enrollment policy matches is refused by the EST server ("No enrollment policy for this device."), as a
  device OADM does not manage is.
- A device that no VLAN policy matches gets no VLAN from the server (the switch port keeps its own). There is no default
  VLAN.
- **Block only refuses:** the 802.1X server refuses its devices, nothing is revoked or deleted. A stored `AlsoRevoke` is no
  longer read and is dropped on the next save (`PolicyRules.Normalize`).
- **802.1X trusted CA:** each CA passes the 802.1X server only with a policy that names it; no CA follows another
  (`docs/spec/radius-server.md` "Who passes"). The choices are every CA of the list, previous OADM CAs too.
- **EST sign-in CA:** the choices (`PolicyStore.SignInCaChoices`) are the Axis device ID, the first-start root CA, the
  previous OADM CAs and the CAs created in OADM, never an imported CA: EST reads a camera's serial from a name in the
  certificate only where OADM vouches for that name, and an imported CA's key may be used outside OADM. A deleted Axis device
  ID stays a choice; its policy then lets nothing in (fail closed). One for new certificates lets cameras sign in for
  `simpleenroll` and `simplereenroll`, one for renewals only `simplereenroll`.
- **RADIUS server CA** of the IEEE 802.1X certificate policy (`PkiPolicy.RadiusCaId`): a CA of the list (the choices of the
  Certificate authorities card, the first-start root CA first and by default); a CA the cameras need for another RADIUS
  server is imported on the Certificates tab (Import > CA certificate). The hint below the field names the CA that issued
  the 802.1X server policy's certificate ("The 802.1X server's certificate is issued by OADM Root CA SERVER01."). Enable
  IEEE 802.1X installs that CA and the certificates above it PKI knows; the issue dialog shows its name. Such a CA counts as
  used (Used in, cannot be deleted). The tasks never compare it with the 802.1X server's certificate: Enable, Renew and
  Install certificates (IEEE 802.1X) and EST's Set up always install the policy's RADIUS server CA; turning 802.1X off on
  the switch first is the user's job. A new CA never changes the servers' certificates: they keep the one their policy
  picks. An Axis device ID root or intermediate becomes a CA of its own here, so the camera trusts exactly that CA, never
  every Axis root and intermediate.
- **Expiry warning** applies to every device certificate, whoever issued it (the HTTPS certificate polling reads). The days
  of a device are the longest of the Expiry warning policies that hold it; a device no such policy holds keeps 30 days. A
  check that covers no single device (a new Compliance check) gets the longest days of the policies, else 30. The events
  **Certificate expires soon** and **Certificate expired** and their Any device twins are PKI's (`ExpiryEvents`, PKI is an
  `IEventSource`, group "Certificates"; ids `oadm.device.certificate-expires-soon`, `oadm.device.certificate-expired`, kept
  from the host's former built-in events, `BuiltInEvents` keeps them as constants, so stored event rules keep working): the
  full report at the start, a round every minute and after a change of the policies. The device grid's "Certificate
  expires" chip and the Compliance check's default days follow the policies too (a Compliance check with its own days keeps
  them): PKI hands the host its days when the policies changed (`ITrustAnchors.SetExpiryWarning(daysFor, defaultDays)`, only
  PKI's count); the host sends every device row again in one batch (`ExpiryWarningPublisher`, `Device.cert_warning_days`).
  The PKI page's own list uses each device's days (`CertificatesReply.WarningDays` is null).
- **Tags:** the tags of every policy are protected under PKI (`ctx.ProtectedTags`, "The tag Floor 3 is used by a PKI
  policy. Only users with all devices can add or remove it. Nothing was changed."): a user who may tag devices but has a
  group could otherwise choose a device's VLAN or take it out of a Block policy. Without the host's protection the tag parts
  of the kinds that give something (VLAN, Enrollment: `PolicyKinds.GrantsByTag`) match no device (fail closed), those of
  Block and Expiry warning still do. The 802.1X server releases the tags it protected itself before 4.5.
- **Applying:** every change is audited by the host; the 802.1X and EST servers apply a change at once (the next handshake
  or request uses the new rules, never half of a change). Kinds of a server that is off stay listed and editable (they
  apply once it runs).
- **A stored list that cannot be read** (`PolicyStore.IsUnreadable`): one error in the log per load, nothing converted or
  written (the stored value is never overwritten), every save and delete refused, the 802.1X server refuses every device
  ("The PKI policies cannot be read", alert 80), EST lets nothing sign in, the Policies tab says "The policies cannot be read.
  Restore a backup." below the table and is read only.
- The policy kinds live in PKI's code (no SDK): one store, one editor, the 802.1X and EST servers read it in process
  (`IPluginServices.Offer<PolicyStore>`, found each time). A server never stores rules of its own.

## Server certificates

The 802.1X server and the EST server show one certificate each, picked by their server policy from the Certificates list,
like any TLS server. The server certificate names the server; the cameras' own certificates come from the issuing policies
and are checked against the 802.1X trusted CA and EST sign-in CA policies. The two are independent.

- **Certificate** (`PkiPolicy.CertificateId`, the id of a row of the Certificates list): a certificate with a key OADM holds
  that a TLS server can use (no CA, server authentication or no key usage limit, not ended, not on the list of certificates
  no longer accepted); the 802.1X server only one with an ECC key ("The 802.1X server needs a certificate with an ECC key.";
  its cipher suites follow it). The choices (`PolicyList.ServerCertificates`, `PolicyCertificateChoice`, "EST server (OADM
  Root CA SERVER01), until 2035-10-25") come from `PkiService.ServerCertificateChoices`, cached per list, day and change of
  the not-accepted list; the store checks the pick on save (`PolicyStore.CertificateRefusal`). The field shows the names
  the certificate holds below it. A certificate a server policy shows cannot be deleted ("Used by the policy 802.1X server.
  Pick another certificate first.") and shows the server under Used by.
- **No Create in the policy:** other certificates come from the Certificates tab (Create, Sign request, Import); the field
  lists them.
- **One certificate for both servers:** when OADM PRO becomes active, on every start with OADM PRO and on Turn on, a server
  policy without a certificate gets the shared one (`PkiService.EnsureServerCertificatesAsync`, a system write
  `PolicyStore.SetServerCertificateAsync`, only where none is set): "802.1X and EST server", from the CA a server policy
  names when it can issue (the first-start root CA by default), else the first CA that can issue
  (`PkiService.ServerIssuingCa`), ECC P-256, 10 years (never past the CA's end), server authentication, kept in the
  Certificates list with its key (created, use Server), naming this computer's addresses and name, the names the running
  servers give and, while the EST server runs, the site nodes' addresses. Exception: the 802.1X server's stored certificate
  of before 4.5 is imported with its key for its policy (cameras keep working). Nothing while no CA can issue (tried again at
  the next start, minute or connection). Start order: PKI's own start and license change make it only once both server
  plugins started (`PolicyStore.ServerPluginStarted`, also while their servers are off; the EST plugin hands the site nodes'
  addresses from its stored `sites` then) and neither server is turned on; a server that is on makes it at its own start
  with its names, the 802.1X server after it took over its kept certificate (`PkiPlugin.MakesServerCertificates`).
- **No automatic renewal:** nothing makes a server certificate again. The Expiry warning and the server's status remind; the
  user picks another one in the policy.
- **The servers** load the picked certificate with its key (`PkiCertificateAuthority.ServerCertificate`;
  `ServerIdentityKeeper` for 802.1X, `Est/Serving/ServerCertificates` for EST) and again when the policies change. The TLS
  chain, and the CA certificates EST's Set up gives the cameras, are the issuer chain of the picked certificate
  (`PkiService.ServerIssuerChain`).
- **Status** of a server without a usable certificate (`PolicyTexts.ServerNoCertificate`, `ServerCertificateUnusable`): "The
  802.1X server has no certificate. Choose one in its policy.", or "The EST server cannot use its certificate: it has
  ended." (also "it is no longer in the Certificates list.", "it is no longer accepted.", "its key cannot be read."). Such a
  server refuses every camera (fail closed).

## Certificates

Every certificate OADM uses now (no history, see "Current and pending"), paged on the server, so 30,000 devices with two
certificates each stay fast: the CAs, each managed camera's current certificate per use (tasks and EST), OADM's server
certificates and the certificates created, signed or imported here. Two cards of the shared `ui:IssuedCertificateList`
(virtualized, pages of 500) on the Certificates tab.

- **Order** (`PkiService.CertificateList`, page method `getCertificates`): the CAs, the server certificates, the ones
  created, signed or imported here (newest first), then the device certificates (newest first, the issued registry). This
  fixed order makes paging one count plus one registry pass; the rows before the device certificates are at most a few
  hundred and built per request. Search over every column (also a serial in any notation; View shows serial and Valid from),
  filters State (Valid, Expires soon, Expired) and Issued by (the CAs of the CA card and "Other CA"); no use filter: the Type
  badges say the use. Sorting by a column is not offered (the order is the server's).
- **Columns** (one line per row where it fits): **Name** with a second line (the key and the issuer: "RSA 4096 · issued by
  OADM Root CA SERVER01", a device certificate "Issued by OADM Root CA SERVER01", a root CA its key only); **Type** as
  neutral badges (`Border.badge.neutral`): Root CA or Intermediate CA for a CA; HTTPS, IEEE 802.1X, MQTT, RTSPS or Server;
  Signed or Imported for the certificates signed or imported here (a created one has no badge of its own); a root CA adds
  where it is installed as tinted badges (`Border.badge.tinted`): "OS root store: server", "OS root store: client"
  (tooltips "Installed in the operating system's trusted root store of the computer running the OADM server." / "... of
  this computer."), "Failed on ..." with the reason, the progress of an install; **Key** (dimmed); **Valid until**: the date
  (dimmed, the `secondary` text class), and below it a status (`ui:StatusChip`, one line) only when the state needs
  attention: "Expires in 12 days" (amber, the Expiry warning policy's days), "Expired" (red), "Cannot be read" (red, a CA
  row that cannot be read); the CA card's **Used in**; one narrow fixed column with a ⋯ button. No State or Used by column:
  what uses a certificate ("802.1X: OADM CA (only devices OADM manages)", "Issuing: HTTPS, IEEE 802.1X", a server
  certificate "802.1X server", a device certificate its device) is the section "Used by" of View.
- **Group by** (a select in the Certificates card's toolbar, kept on this computer:
  `SetLocalSetting("certificates.groupBy")`): **Type** (default): Certificate authorities, Server certificates, Created,
  signed and imported, Device certificates; **Issuer**: one group per CA in the order of the CA list, then Other CA; **None**.
  The server orders and counts the groups (`CertificatesRequest.GroupBy`, `CertificatesReply.Groups`), so a page of 500
  cuts across groups and each header names the group's count over every page ("Device certificates · 60,000"). The group
  headers are the shared `DataGrid.groups` style (collapsible).
- **Toolbars:** the Certificates card has **Create**, **Sign request** and **Import** as command buttons left, and Group by,
  the state and issuer filters and the search right-aligned in the same row; on a card too narrow for both the filters go
  below as a whole (`ToolbarRow`), never cut.
- **Row actions:** one ⋯ button per row (`Button.toolbar.iconOnly`, `Icon.more`); its menu is the same as the row's
  right-click menu (one source, `IssuedCertificateListViewModel.MenuOf`): the everyday actions, then a separator and the red
  ones last (`MenuItem.danger`). A double click is View.
  - A CA: see "Certificate authorities".
  - Every other row: **View**, **Export** (PEM, DER; **With its key** where OADM holds it), then the red **Delete** where it
    applies.
  - Several chosen rows: the menu of any of them offers **Delete N certificates** (red; at most 5,000 at once); none in the
    CA list.
  - Users who may not manage the CA get View only.
  - There is no revoke anywhere: Delete is the only action that ends a certificate.
- **Create** (`createCertificate`): use HTTPS or IEEE 802.1X, an issuing policy of that kind (its days and key: ECC P-256 or
  RSA 2048), Name (1 to 64 characters), addresses (host names and IP addresses, at most 20; at least one for HTTPS). OADM
  makes the key; the dialog downloads a PKCS#12 with a password (at least 8 characters). The key is kept encrypted with the
  master key (plugin records `localCertificates`, purpose `pki:certificate:<id>`) and is part of the backup. Server policies
  are no choice.
- **Sign request** (`signRequest`): a PKCS#10 request (PEM or DER, up to 1 MB) from another system, its key never leaves it.
  Its signature is checked, SHA-1 / MD5 refused, RSA 2048 or more or EC P-256 / P-384 / P-521, and the key kind of the chosen
  policy (an RSA request with an ECC policy is refused). Names: the request's CN and its host names and IP addresses (plain
  host names only); an HTTPS policy needs one. Nothing else of the request's subject is copied (the certificate's subject is
  its CN only), so no serial number attribute reaches the certificate.
- **Camera names:** Create and Sign request refuse a name or address like an Axis camera's (`axis-<12 hex>`, any domain, any
  case: `CertificateRules.IsCameraName`) below its field ("This name belongs to an Axis camera."): EST and the 802.1X server
  take a camera's serial from such a name.
- **Never a device's certificate:** a certificate of the Certificates list (created, signed or imported) is never a camera's,
  whatever it names. `PkiCertificateAuthority.PurposeOf` answers `NotDevicePurpose` for its serial (checked before the issued
  registry, a hash lookup in `LocalCertificates.HasSerial`); EST then finds no camera in it (401 "The camera's certificate is
  not a valid Axis device ID or an EST certificate.") and the 802.1X server refuses it (alert 42, "Certificate is not a
  device certificate"). Fail closed: also when such a certificate was found on a device and listed in the registry.
- **Import** (`importCertificate`): one PEM or DER certificate, or a PKCS#12 file with exactly one certificate with its key
  (RSA or ECC; the chain in it is not kept; the key kept like a created one). Refused: CA certificates ("Use Import > CA
  certificate"), expired ones, a PEM with a private key, several certificates in one PEM, one already listed. A wrong
  password shows below Password only for a PKCS#12 file (its outer structure read without the password, `LooksLikePkcs12`);
  any other file "The file cannot be read." below File. The use comes from the extended key usage: server -> HTTPS, client
  -> IEEE 802.1X, else Other.
- **Export** (`downloadCertificate`, audited with "Export certificate <name> (<format>)"): PEM or DER of the certificate
  only (no chain); PKCS#12 with a password only where OADM holds the key (created, imported with a key, the server
  certificates among them), with the issuing CA, AES-256, 100,000 iterations. Device certificates have no key in OADM (the
  camera made it). Never a CA key.
- **Delete** (`deleteCertificates`; confirmation "It leaves the list. This cannot be undone." / "They leave the list. ...",
  red Delete; all or none, the first refusal says why): every device certificate, created and signed ones no server policy
  shows, imported ones nothing uses or that ended, ended server certificates. Refused for a certificate a server policy
  shows ("Used by the policy 802.1X server. Pick another certificate first.") and for a CA ("Delete CAs in Certificate
  authorities."). A still valid one OADM issued (a device certificate, a created or signed one) goes on PKI's list of
  certificates **no longer accepted** (see "Current and pending"), so a copy left on a camera or elsewhere is refused by the
  802.1X and EST servers ("Certificate deleted in OADM") until it ends; the server log says "PKI: N still valid certificates
  deleted by <user> are no longer accepted until they end". That list is never shown.
- **Limits:** at most 1,000 certificates created, signed or imported; files up to 1 MB. A file goes through `InvokeAsync` as
  base64 (core plugins have no upload path), within the server's 16 MB message limit.
- **Who:** every action needs "Manage the certificate authority" and all devices, audited by the host; a user limited to a
  device group sees only the device certificates of the group's devices and no actions.
- An event `certificates` (at most one per second) makes every open tab read its page again.

## Current and pending

The Certificates list shows what is in use now, not the history. Code: `Ca/IssuedRegistry` (`plugins/Oadm.Plugins.Pki/README.md`
"Issued certificates and revocation").

- **The store** (plugin records `issued`, `issuedDer`): per camera and use (HTTPS, IEEE 802.1X, MQTT, RTSPS) the
  **current** certificate (the one confirmed on the camera) and at most one **pending** one (issued, not confirmed yet).
  Cameras are matched by their **serial** (`deviceSerial`, from `axis-<serial>.local`), never only by the device entry's
  id: a camera removed and added again gets a new id. An entry without a serial (OADM before 4.5) counts by its device id;
  the list then hides it when the camera has a newer current one under its serial. Every caller that keys by device finds a
  camera added again: `ForDevice(IDeviceInfo)` and `RevocableOf(IDeviceInfo)` look up the id and the serial. Created,
  signed and imported certificates and the servers' certificates are kept apart (`Certificates/LocalCertificates`).
- **Kept:** one row each, only changed rows written, new certificates batched every 2 s, an entry of the not-accepted list
  written before the call returns. `issued` (serial, device id, device serial, use, source Task or EST, CA id, valid from,
  valid until, issued, alias), `issuedDer` (the DER, read one at a time) and `revoked` (the not-accepted list). A stored
  certificate leaves 30 days after it ended (`KeptAfterEnd`, also a hidden current one of a camera OADM no longer manages;
  server certificates the same), an entry of the not-accepted list when its certificate ended. Checked at the start and
  every hour. The plugin setting `issued` of OADM 4.1 moves into the records at the first start and is deleted (those
  entries have no DER: View says "OADM keeps no copy of this certificate." until a read of the device's certificate list
  finds the certificate and stores its copy).
- **The list** (`IssuedRegistry.Query`, `currentOnly`): the current certificate of each use of each camera OADM manages, not
  deleted from the camera by a task and not on the list of certificates no longer accepted. A current one stays listed when
  it is expired (the camera needs attention). Created and signed certificates on that list are not listed either, unless a
  server policy still shows one. Not listed, but kept for the 802.1X checks and EST: pending ones, older entries of OADM
  before 4.5, certificates a task deleted from the device (`removedFromDeviceUtc`: a copy could still be in use, so it can
  still be refused until it ends) and the current ones of cameras OADM no longer manages (they stay valid, nothing is refused
  on removal; a camera added again gets its rows back). The CA service's `ListIssuedAsync` still lists every stored
  certificate; `IssuedCertificateState.Replaced` stays in the SDK, never answered. The page's device counts count only
  certificates still in use: not ended, not on the not-accepted list, not deleted.
- **Confirmed** (the pending one becomes current; the previous current one goes on the not-accepted list with the reason
  `RevocationReason.Superseded`, user "OADM", and leaves the store; the task log: "The previous certificate (serial ...) was
  replaced by "<alias>" and is no longer accepted."):
  - Enable HTTPS, Renew certificates (HTTPS): after Verify HTTPS passed.
  - Enable IEEE 802.1X, Renew certificates (IEEE 802.1X): after Verify 802.1X settings read back 802.1X on with the new
    certificate and its CA certificates.
  - Install certificates (a file OADM did not issue): after its verify step the camera's current OADM certificate of that
    use is no longer accepted the same way (the file's certificate is not stored).
  - EST, HTTPS, RTSPS and MQTT profiles: when `EstHttp.WriteAsync` of the 200 answer returned (the answer is handed to TLS;
    the camera is not asked to acknowledge).
  - EST, IEEE 802.1X profile: only when the camera next signs in to OADM's 802.1X server with the new certificate. With the
    802.1X server off or another RADIUS server it stays pending; the previous one stays valid.
  - Any other caller of the CA service (`SignAsync` with a device, a source other than EST): current at once.
- **Unused** (no longer accepted, reason Other, user "OADM", and removed from the store; the task log: "The new certificate
  (serial ...) is not in use and is no longer accepted."): an install that failed before the certificate was in use (HTTPS:
  any failure before the switch request was sent, or after it when a read of the web server settings shows another
  certificate), an EST answer that could not be written, an EST certificate still pending after
  `IssuedRegistry.PendingTimeout` (1 hour) without its answer (the server stopped in between). A newer pending one of the
  same camera and use replaces an older one (Superseded).
- **Device safety (IEEE 802.1X tasks):** once "Set 802.1X configuration" ran, the new certificate is `Delivered` and never
  dropped as unused, unless the read-back names another client certificate. A newer pending one does not drop it either (it
  stays in the store, not current). While the read-back does not confirm the new certificate the task deletes no previous
  OADM certificate from the camera (Remove previous OADM certificate: Skipped).
- **Device safety (HTTPS tasks):** once the switch request of "Switch web server to the new certificate" was sent, the new
  certificate is `Delivered` (the web server may present it after a slow restart, or the device applied it and the answer
  got lost). A failure from there on (the switch answer, Verify HTTPS) reads the web server settings
  (`CertificateDeployment.HttpsNotVerifiedAsync`): another certificate named there drops it as unused; the new one named
  there or a read that fails keeps it pending, the previous one stays current and valid ("stays pending" in the log).
- **Remove previous OADM certificate** after a confirmed switch: a delete the device refuses is a Warning log line and a
  step warning (Done with warnings, "delete it with Delete certificates"), never a failed task; the others are still
  removed.
- **The list of certificates no longer accepted** (records `revoked`; never shown on the page): serial, time, reason, user,
  device, valid until; kept until the certificate ends. Only Delete, a confirmed replacement and an unused certificate add
  to it (`IssuedRegistry.RevokeAsync`; the SDK's `IDeviceCertificateAuthority.RevokeAsync` stays, no plugin of OADM calls
  it). The refusal names the cause (`Ca/NotAcceptedTexts`): "Certificate replaced by a newer certificate" (reason
  Superseded), else "Certificate deleted in OADM" (also a revoke of OADM before 4.5); never "revoked". The 802.1X server
  (alert 44) and EST (403) check it: an OADM certificate passes unless it is on the list (certificates missing from the
  store pass, so an update, a backup restore and `ImportFoundAsync` stay safe). The check is a hash lookup of the serial
  (`IsRevoked`: an immutable set in memory, replaced when PKI changes it).
- **Existing certificates** (found on the cameras): (1) whenever PKI reads a device's certificate list (View / Delete
  certificates, Renew certificates), every certificate there that a CA of PKI issues with signed (checked by a chain build
  to that CA) and that names this device's `axis-<serial>.local` is added once (`ImportFoundAsync`; use: the one the EST
  profile name behind `oadm_` / `oadm2_` names when it fits the certificate (`oadm_mqtt`, `oadm2_mqtt_cameras` -> MQTT,
  `oadm_rtsps` -> RTSPS; MQTT and IEEE 802.1X only for clientAuth, HTTPS and RTSPS only without), else IEEE 802.1X for
  clientAuth and HTTPS for the others, `IssuedRegistry.PurposeOfFound`; source EST for the `oadm_` / `oadm2_` EST profile
  names, else Task; issued = its valid-from); (2) once per server, 5 minutes after the start, one background pass reads the
  certificate list (REST `cert` v1 or the SOAP certificate service, read only, see "Older AXIS OS") of every device that
  answers and has AXIS OS 6.53 or later, 4 at a time (plugin setting `existingImported` once a round reached the devices: one
  device read, or nothing to read; a round that read no device runs again an hour later, a stop at the next start). Devices
  that did not answer come in by (1) or when renewed. A serial on the not-accepted list is never added.
- **Existing data:** no conversion. Entries of OADM before 4.5 stay; the newest of each camera and use counts as current,
  the others are hidden. The update adds nothing to the not-accepted list.
- **Audit:** what goes on the list goes to the server log ("PKI: revoked N certificates (Replaced by a newer certificate) for
  OADM", the internal wording) and to the task's log; plugins have no audit API.
- **Backup:** the issued certificates and the not-accepted list are plugin records, part of the encrypted backup; a restore
  brings them back with the CAs (`docs/spec/settings-backup.md`).

## Converting existing settings

On the first start after the update, once per owner (`PolicyStore.ConvertOnceAsync`, the owner noted in the stored list),
without asking, never on settings or a store that cannot be read (the next start tries again), nothing the user set lost.
The old settings stay where they are and are no longer read. Overlaps found while converting are kept and show their
warning; nothing is dropped. A conversion that runs after policies exist merges: no second policy of a CA (only devices
OADM manages, or renewals only, when either was: the stricter), no second 802.1X server policy, no second "All devices" VLAN
policy, no "All devices" Enrollment when an Enrollment policy exists. The conversion is checked on copies of real data (a
4.1 server's backup), not only on test data.

| Owner | Where | What |
|---|---|---|
| `oadm.pki` | `PkiService.ConvertOwnSettings` | PKI's warn before expiry (`config.expiryWarningDays`) becomes the Expiry warning policy "All devices" |
| `oadm.pki:issuing` | `PkiService.ConvertIssuing` | the Device certificates and IEEE 802.1X cards (validity, EAPOL version, EAP identity, RADIUS server CA) become the issuing policies "HTTPS" and "IEEE 802.1X"; the EST server policy is added. The key: ECC on a new server, RSA where an OADM ran before (a stored CA or page settings), because the tasks made RSA 2048 keys on the devices; what the devices get does not change with the update |
| `oadm.radius` | `RadiusPolicies.ConvertAsync`, at the 802.1X core plugin's start | the settings of development builds of the 802.1X server (Advanced in `config`, `trustedCas`, `policies`, `vlans`; it never shipped before 4.5): the 802.1X server policy (minimum TLS, largest EAP packet), one 802.1X trusted CA policy per ticked CA with its "Only devices OADM manages", VLAN and Block policies (Revoke: Block) with the same names, devices and order, a Default VLAN as a last VLAN policy "All devices" (so no device changes its VLAN); a server without them gets the default policies of the 802.1X kinds. PKI starts first (the core plugins of a package start in name order); without the store the next start converts |
| `oadm.est:profiles`, `oadm.est` | `EstPolicies.ConvertAsync`, `ConvertRulesAsync`, at the EST core plugin's start | each EST profile names the built-in issuing policy of its kind (its old validity and key are no longer read); the Enrollment policy "All devices" (every managed device, every profile); one EST sign-in CA policy per CA of 4.1's "Sign in with" (`config.signInWith`, kept by a save; 4.1 had none set: the Axis device ID and the first-start CA): "Axis device ID" (new certificates), the first-start root CA under its real name (renewals), a previous CA by its name (renewals); a previous CA PKI no longer lists is not converted. A renamed or removed EST profile is not followed into the policies (the policy keeps the name, which then allows nothing; the editor shows it to uncheck) |
| `oadm.est:issuing-2026-10-25` | `EstPolicies.CleanUpAsync` | a profile that names a policy an earlier conversion made ("EST HTTPS", "EST 802.1X", "EST " + the profile's name, also "EST https" / "EST dot1x") names the built-in policy of its kind; those policies are deleted where no other profile names them |
| `oadm.est:off-profiles-2026-10-25` | `EstPolicies.RemoveOffProfilesAsync` | EST profiles that were off are removed (`docs/spec/est-server.md` "Profiles") |
| `oadm.pki:kinds-2026-10-25` | `PolicyStore.ConvertKinds` | the Server certificate policy of the 802.1X server goes into the 802.1X server policy (without one, one with the TLS defaults takes it), the one of the EST server becomes the EST server policy (id, name and values kept); a Revoke policy becomes a Block policy; names made unique per kind ("Stolen (2)"); without an EST server policy one with the defaults is added. `LegacyServerCertificate` and `LegacyRevoke` exist only for this (`PolicyKinds.IsKnown` refuses them) |
| `oadm.pki:radius-ca-2026-10-25` | `PkiService.ConvertRadiusCasAsync`, at the end of the start | the RADIUS server CA of each IEEE 802.1X policy becomes a CA of the list: "OADM 802.1X server" the CA that issued the 802.1X server's certificate (else the first-start CA); a CA imported into the policy goes into the CA list (or the listed CA with its fingerprint) and is named; the Axis device ID entry never matches; one that cannot be read stays without a CA (logged; the form and the task ask for one, never a silent switch to another CA). `RadiusCa` and `RadiusCaPem` are read only here |
| `oadm.pki:previous-cas-2026-10-26` | `PolicyStore.ConvertPreviousCasOnceAsync` | an 802.1X trusted CA policy of the first-start CA used to trust every previous OADM CA too; each previous OADM CA still listed gets its own 802.1X trusted CA policy with that policy's settings, named after the CA (made unique) and placed right after it, so every camera keeps passing. A previous CA a policy names already, or a list without a policy of the first-start CA, stays as it is. Runs only after the `oadm.radius` conversion: at PKI's start when that ran before, else right after it (`RadiusPlugin`) |

- **Server certificates** (see "Server certificates"): the 802.1X server's own certificate of before 4.5 (setting
  `serverCertificate`, key encrypted with `radius:server-key`, no longer written) is imported into the Certificates list once
  and picked by the 802.1X server policy, so cameras keep working; the EST server policy gets the shared certificate.

## Clock check before installing a certificate

A camera whose clock is behind sees a new certificate as "not valid yet".

- Every certificate a CA of PKI issues to a device starts **5 minutes in the past** (`NotBefore = now - 5 min`), which covers
  small drifts silently (PKI tasks and EST).
- The dialogs of **Enable HTTPS**, **Enable IEEE 802.1X**, **Renew certificates** and EST's **Set up** read the camera's clock
  first (read-only query `readClock`, `time.cgi` or the HTTP Date header; `plugins/Shared/DeviceClock/CameraClock.cs`). If the
  camera is still more than 5 seconds behind the certificate's start, they ask before starting: "The camera's time is 9 min
  12 s behind. The certificate becomes valid on the camera only in 4 min 12 s. Set the camera to use NTP. Continue?" **Yes**
  / **No**. Several cameras: one question with how many and the largest drift. The task gets the answer and does not ask
  again; a camera that is ahead is fine. The dialogs read at most the first 100 selected cameras (3 seconds each); the tasks
  check every camera.
- A run without the answer (no dialog) still refuses a camera behind the certificate's start ("Nothing was changed").
  Renew certificates checks the clock once before it renews the first purpose (HTTPS too), with the answer of its dialog.

## Tasks

The Security tasks (Enable / Disable HTTPS, Enable / Disable IEEE 802.1X, Renew certificates, View / Delete certificates,
Install certificates, Install CA certificates) with their steps: `plugins/Oadm.Plugins.Pki/README.md` "PKI Security tasks".
Rules:

- The task payload carries only the policy id (and the clock answer); the run uses the policy's stored values and checks
  them like a saved policy before the first write. Without a policy id (older clients) the first of the kind. A policy
  deleted after the dialog fails in Check compatibility: "The chosen policy no longer exists. Start the task again. Nothing
  was changed." (Renew: before HTTPS too). Renew certificates and Install certificates (IEEE 802.1X) always ask for the IEEE
  802.1X certificate policy (the first one could rewrite a camera's EAP identity, EAPOL version or RADIUS server CA that
  another policy set); HTTPS of Renew keeps the first HTTPS certificate policy. The preset is the first policy: PKI does not
  keep which policy issued a certificate (`IssuedCertificate` has no policy id).
- Check compatibility reads the key types of the device's default keystore (`GET cert/v1`: `settings.keystore`,
  `keystores[].key_types`). ECC is EC-P256 on the device, RSA is RSA-2048; a device that does not list its key types gets
  RSA only.
- Names in device certificates: `CertificateNames.For` (`plugins/Oadm.Plugins.Pki/README.md` "Device groups"); only for a
  device a user with all devices added the host name and FQDN, only for a device no group covers what the camera reports.

## Older AXIS OS

Certificates, HTTPS and IEEE 802.1X run on **AXIS OS 6.53 and later** (researched on hardware on 6.53, 8.40, 9.80, 10.12,
11.9, 11.11 and 12.11; the recorded answers, sanitized, in `tests/Oadm.Plugins.Pki.Tests/Fixtures/Legacy/<version>/`). EST
needs 12.10 (`docs/spec/est-server.md`). One decision per device, `Tasks/PkiCompatibility` (`Decide`, `Dot1xApiOf`), made
fresh in Check compatibility before the first write; the menus use the cached firmware version only (6.53 or later).

| | Certificates | HTTPS web server | IEEE 802.1X on/off, identity, EAPOL | 802.1X certificates | Keys | EAPOL |
|---|---|---|---|---|---|---|
| 12.11 and later (network settings 1.38) | REST cert v1 | SOAP, Admin policy | network settings | network settings | keystore's types (ECC, RSA) | 1-3 |
| 11.11.135 to 12.10 | REST cert v1 | SOAP, Admin policy | network settings | SOAP certificate set | keystore's types (11.11: RSA) | 1-3 |
| 10.1 to 11.11.134 | SOAP | SOAP, Admin policy | network settings | SOAP certificate set | RSA 2048 | 1-3 |
| 8.50 to 10.0 (network settings listed) | SOAP | SOAP, Admin policy | network settings | SOAP certificate set | RSA 2048 | 1-2 |
| 7.10 to 8.40 (no API list) | SOAP | SOAP, Admin policy | parameters | SOAP certificate set | RSA 2048 | 1-2 |
| 6.53 to 7.0 | SOAP | SOAP, Admin + Operator + Viewer | parameters | SOAP certificate set | RSA 2048 | 1-2 |

- **Certificate API:** REST `cert` v1 only when `config/discover` lists it **released** (11.11.135 and later; beta on 11.9 is
  not used); else the SOAP certificate service at `/vapix/services` (`Device/CertSoapApi`): `tds:GetCertificates`,
  `GetCACertificates`, `LoadCertificates`, `LoadCACertificates`, `DeleteCertificates` (certificates and CA certificates, DER
  in base64), `acertificates:CreateCertificate2`, `GetPkcs10Request2`, `LoadPkcs12`, `GetCertSet` / `SetCertSet`.
  `config/discover` answering 404, 400, 405 or 501 means not listed. The AXIS OS version for the SOAP path comes fresh from
  `basicdeviceinfo.cgi`, else `Properties.Firmware.Version`; an unknown or unreadable version, or one before 6.53, refuses
  ("The device's AXIS OS version cannot be read. Nothing was changed." / "Needs AXIS OS 6.53 or later. Nothing was
  changed."): never "unknown = allowed" for writes.
- **Keys on SOAP:** `CreateCertificate2` takes no key type (10.12 refuses one with `ter:TagMismatch`): RSA 2048 only, an ECC
  policy fails in Check compatibility with "The device cannot create an ECC key. Choose a policy with RSA. Nothing was
  changed." (6.53 also refuses an ECC PKCS#12). The key is created under the placeholder "&lt;alias&gt; key" (a self-signed
  certificate), the request comes from it, the signed certificate is loaded under the alias (the device finds the key; the
  placeholder's own id is refused as existing) and the placeholder is deleted after that (the key stays; a failed delete is
  a Warning log line). A failure before the load deletes the placeholder again.
- **802.1X certificates:** network settings before 1.38 (9.80 1.6, 10.12 1.15, 11.9 1.29, 11.11.232 1.33; 12.10 too) answer
  success to `certClient` / `certsCA` but do not bind them and never report them in `getNetworkInfo`. OADM uses network
  settings alone where `getNetworkInfo` names them (`Wired8021X.ReportsCertificates`; 12.11 lists 1.37 but reports them) or
  the list says 1.38 or later; else the certificates go through `SetCertSet EAPTLS_WIRED` (client certificate, CA
  certificates, trusted ones as read) **before** 802.1X is switched on, and the read-back of Verify 802.1X settings takes
  them from `GetCertSet` (`Device/Dot1xSettings`). Also for Disable IEEE 802.1X (certificates stay bound), Renew, Install
  certificates for 802.1X, View / Delete certificates (in use by 802.1X) and the EST step "Set the RADIUS server CA" (only
  the CA certificates of the set change).
- **802.1X off, then on:** with network settings, every write of the 802.1X configuration that leaves 802.1X on writes the
  whole configuration with `enabled` false and then the same with true (`NetworkInfoApi.SetOffThenOnAsync`): Set 802.1X
  configuration of Enable IEEE 802.1X, Renew certificates and Install certificates (`EnableEapTlsAsync`), and the CA
  certificate install of EST's "Set the RADIUS server CA" while 802.1X is on (`SetCaCertificatesAsync`). AXIS OS 12.11
  authenticates again only on the change from off to on, not when certificates change while it is on. The parameter way (no
  network settings) writes on once.
- **Without network settings** (no API list before 8.50, or no `network-settings` in it): on/off, identity and EAPOL
  version through `param.cgi action=update` `Network.Interface.I0.dot1x.Enabled`, `.EAPOLVersion`, `.EAPTLS.Identity` (form
  body), read with `param.cgi list`; the certificates through the certificate set as above. Host names for the HTTPS
  certificate are then not read (Read network settings Skipped, the OADM address and `axis-<serial>.local`).
- **HTTPS before 7.10:** `SetWebServerTlsConfiguration` needs Operator and Viewer next to Admin (6.53: Admin alone is
  `ter:InvalidArgs` "occurrence violation"): OADM sends all three when the device reported them or the version is before
  7.10; Enable switches each HTTP-only role to HTTP and HTTPS, Disable sets all to HTTP.
- **EAPOL version 3** needs AXIS OS 10.1 (IEEE 802.1X-2010; 6.53 breaks with it, 9.80 refuses it with error 4004): an IEEE
  802.1X policy with EAPOL 3 fails Check compatibility below 10.1 with "EAPOL version 3 needs AXIS OS 10.1 or later. Choose a
  policy with EAPOL version 1 or 2. Nothing was changed." (Renew: the purpose is Skipped with that reason). The default
  policy of a new installation uses EAPOL 2; its ECC key still needs AXIS OS 12, so older devices need a second policy with
  RSA.
- Reads in the background (the one pass for existing certificates) take the API from the cached `config/discover` and the
  device's cached AXIS OS version (`PkiCompatibility.CachedAsync`).
- AXIS OS 9.80 refuses connections for a few seconds after a connection policy change (the web server restarts). 6.53 has
  neither `basicdeviceinfo.cgi` nor the API list (version from the parameter), 8.40 has `basicdeviceinfo.cgi` only.
- **Hardware** (`LegacyHardwareWriteTests`, category HardwareWrite, one test per device, the device's state saved first and
  restored and compared at the end): Enable HTTPS, Renew and Disable HTTPS on 6.53, 8.40, 9.80, 10.12 and 11.9; Enable,
  Renew and Disable IEEE 802.1X with EAPOL 2 and 3 on 10.12 and 11.9; on 6.53, 8.40 and 9.80 everything up to switching
  802.1X on; ECC and EAPOL 3 below 10.1 refused with no write; chains (`Chain_Local_200_1012`, `Chain_Local_12_119`):
  HTTPS with ECC P-256 and RSA 4096 root CAs, then HTTPS and IEEE 802.1X with a certificate of an RSA 4096 intermediate CA.
  Not verified yet: the REST path of 11.11.135 to 12.10 with the certificate set; the network settings between 8.50 and 9.70
  (assumed: `getNetworkInfo` when listed; 9.80 lists 1.6 and it works).

## Web server restart and Basic login

- **Verify waits for the web server restart.** After the web server changes, Verify HTTPS (Enable HTTPS, Renew's HTTPS part,
  Install certificates for HTTPS) and Verify (Disable HTTPS) wait at most 60 s (`CertificateDeployment.SwitchTimeout`, a try
  every 3 s on the PKI clock, no fixed sleeps) with the step text "Waiting for the web server to restart". HTTPS: the new
  certificate in the handshake shows the restart happened. HTTP: AXIS OS 9.80 answers right after the change and refuses
  connections a few seconds later while its web server restarts, so the device must keep answering for 10 s
  (`PkiOptions.WebServerSettle`, a parameter read every 3 s; a refused read starts the wait again).
- A device that does not come back fails the step with what was changed and how OADM connects: "The device did not come back
  with the new certificate within 1 minute. The web server was switched to the certificate "OADM HTTPS ...". OADM still
  connects as before.", "The device did not answer over HTTP within 1 minute. The device was set to HTTP only; its
  certificates stay. OADM still connects as before.", or, when it answered once and then stopped, "The device stopped
  answering when its web server restarted and did not come back within 1 minute. ... OADM connects over HTTP."
- **Disable HTTPS on a device with Basic login only** (`Network.HTTP.AuthenticationPolicy=basic`): runs when the server
  setting **Allow Basic login over HTTP** is on (`IVapixClient.AllowsBasicOverHttp`; Check compatibility says "Basic login
  over HTTP allowed"); with it off it refuses before any write: "The device accepts only Basic login over HTTP. To disable
  HTTPS on it, turn on Allow Basic login over HTTP in Settings > Security. Nothing was changed."
  (`HttpsDisableTask.BasicOnlyRefusal`).

## Delete and install safety

- **Delete certificates** deletes nothing while what uses the certificates is unknown: a failed read of the web server
  settings or of the 802.1X settings (REST and SOAP) fails "Read certificates" with "What uses the certificates cannot be
  read (...). Nothing was changed." (a hidden 802.1X client certificate or RADIUS server CA would take the camera off the
  network). The dialog's query then marks every certificate `UseUnknown`: In use "Unknown", not selectable ("What uses it
  cannot be read: it cannot be deleted."). A device without 802.1X (a read that says so) is not unknown.
- **SOAP answers:** an HTTP error is never a success; a body with a SOAP fault throws with the device's reason, any other
  non-2xx answer with the HTTP status (`DeviceSoap.SendAsync`).
- **CA certificate aliases:** every install of CA certificates (Install CA certificates, the CA steps of Enable, Renew and
  Install certificates) takes a free alias when another CA certificate holds "OADM CA &lt;8 hex&gt;" already
  ("&lt;alias&gt;-2", `CertificateDeployment.FreeAlias`).
- **Install certificates, CA certificates only:** a file with a certificate that is not a CA certificate is refused, in the
  dialog's row ("Cannot be used") and again in "Read certificate file" ("&lt;file&gt;: &lt;name&gt; is not a CA
  certificate. Choose a file with CA certificates only. Nothing was changed.", `CertificateFiles.NotCaProblem`).
- **Install certificates, keys over HTTP** (agreed with the maintainer; the CodeQL alert "sensitive data transmission" is
  used as intended): HTTPS and IEEE 802.1X send each file with its private key and password to its device. When OADM reaches
  some of the devices that get a file over HTTP (`IDeviceInfo.ConnectsOverHttps` false), the dialog shows one line ("2 of
  the 3 devices that get a file are reached over HTTP: their private keys cross the network unencrypted.") and the check box
  **Send the keys anyway**; Install stays disabled until it is ticked (tooltip "Tick Send the keys anyway."). Never one
  question per device; counted once over the files (O(files)); nothing extra without an HTTP device or for CA certificates
  only. The task logs a Warning line per device it sent a key to over HTTP ("cam48.pfx was sent with its private key over
  HTTP: unencrypted on the network.", never the password or the key).

## Code

The detail lives in `plugins/Oadm.Plugins.Pki/README.md`; the map:

- **CAs:** `PkiService.cs`, `PkiService.Authorities.cs` (`CreateAuthorityAsync`, `DeleteAuthorityAsync` per kind),
  `Ca/CaGenerator`, `Ca/CaImporter`, `Ca/PkiStore` (`ca`, `caDeleted`, `previousCas`), setting `importedCas`
  (`StoredImportedCa`: certificate, `chainPem`, `created`, `keyProtected` = `ISecretProtector.Protect(PKCS#8 PEM,
  "pki:ca:<id>")`). The first-start root CA keeps the id `oadm` and `CertificateAuthorityKind.Oadm` in the SDK (stored
  policies name it); nothing else treats it apart. Page methods `createAuthority`, `import`, `importAuthority`,
  `deleteAuthority`, `installServerTrust` (`InstallTrustRequest`), `backup` (`BackupRequest.Id`). State `PkiState`:
  `RootTrust` (not for users limited to a device group, no root store lookups for them), `CaUnreadable`;
  `AuthorityRow.Issues` feeds Create CA's Signed by.
- **Policies:** `Policies/PolicyModels.cs` (the contract shared with the page: `PolicyKinds`, `PolicyGroups`, `PkiPolicy`,
  `PolicyDevices`, `PolicyList`, `PolicyMethods`, `PolicyTexts`), `PolicyRules`, `PolicyMatcher` (hash sets of sites, tags
  and serials, O(1) per device), `PolicyStore` (setting `policies`: the list in age order and the converted owners; an
  immutable snapshot with a version, `Changed` after the write), `ExpiryEvents`; `PkiService.Policies.cs`. Page methods
  `getPolicies` (rows with summary and warning, the CA and certificate choices, the sites, the tags, `mayManage`; a user
  limited to a device group gets an empty list), `savePolicy` (`PolicySaveReply` with the field error or the list),
  `deletePolicy`; event `policies` after every change.
- **Certificates:** `PkiService.CertificateList.cs`, `PkiService.CertificateActions.cs` (`CertificateMethods`:
  `createCertificate`, `signRequest`, `importCertificate`, `deleteCertificates`, `downloadCertificate`),
  `Certificates/LocalCertificates`, `Ca/IssuedRegistry`, `Ca/NotAcceptedTexts`, `Ca/ExistingCertificateImport`.
- **Server certificates:** `PkiService.ServerCertificates.cs`, `Radius/Server/ServerIdentityKeeper`,
  `Est/Serving/ServerCertificates`.
- **SDK** (`src/Oadm.Sdk/Certificates`, default members of `IDeviceCertificateAuthority`, the offer of PKI): `IsRevoked`,
  `FindRevocation`, `Revocations`, event `RevocationChanged` (subscribe again when `Find` returns another instance),
  `ListIssuedAsync(IssuedCertificateQuery)` (filters, search, `DeviceIds`, paging, `GroupBy`; rows `IssuedCertificateInfo`),
  `GetIssuedCertificateAsync(serial)` (DER), `RevokeAsync`, `PurposeOf(serial)`; `DeviceCertificateRequest.Purpose`
  (`DeviceCertificatePurposes`: https, dot1x, mqtt, rtsps) and `IssuerId`; `IssuedCertificateSources` (Task, EST);
  `CertificateSerials.Normalize` (any notation: upper hex, no separators, no leading zeros); `CertificateDescriber` /
  `CertificateDescription` for the shared certificate window. The SDK names stay (revocation) although the UI never says
  revoke.
- **Client:** `PkiViewModel` (+ `.Tabs.cs`), `PkiView` (`Arrange`, `FollowHighlight`), `PoliciesViewModel`,
  `PolicyEditorViewModel`, `PolicyFormView`, `CreateCaViewModel` / `CreateCaWindow`, `IssuedCertificatesViewModel`; the client
  SDK's `IssuedCertificateList` / `IssuedCertificateListViewModel` (`MenuOf`, `ActionsOf`, `SelectionActions`, `MoreStatesOf`,
  `Toolbar`, `Banner`); theme `Border.badge.neutral`, `Border.badge.tinted`, `MenuItem.danger`. Fake mode:
  `FakeOadmApi.Pki.cs`, `FakeOadmApi.PkiCertificates.cs`.
- **Tests** (`tests/Oadm.Plugins.Pki.Tests` unless named): `AuthorityTests`, `SeveralCasTests`, `StoredCaUnreadableTests`,
  `PreviousCaConversionTests`, `ChainUploadTests`, `CaListHeadlessTests`, `PolicyStoreTests`, `PolicyPageTests`,
  `PolicyTabHeadlessTests`, `PolicySentenceTests`, `EstPolicyPageTests`, `ExpiryEventTests`, `IssuingPolicyTests` (fixtures of
  4.1 settings), `IssuingTaskTests`, `Dot1xPolicyChoiceTaskTests`, `IssueDialogTests`, `CertificateListTests`,
  `CertificateGroupTests`, `CertificatesTabTests`, `CertificateCardHeadlessTests`, `PkiTabsTests`, `BackupTests`,
  `CurrentCertificateTests`, `CertificateConfirmationTaskTests`, `IssuedRegistryTests`, `ExistingCertificateImportTests`,
  `LegacyDeviceApiTests`, `CompatibilityMatrixTests`, `LegacyTaskTests`, `WebServerRestartTests`, `HttpsTaskTests`,
  `TaskSafetyFixesTests`, `DeviceSoapErrorTests`, `KeysOverHttpTests`, `FakeModeTests`; Perf `PolicyPerfTests`,
  `CertificateListPerfTests`, `IssuedScaleTests` (30,000 devices); `Oadm.Plugins.Est.Tests` `EnrollmentPolicyTests`,
  `PolicyTests`, `ServerTests`; `Oadm.Plugins.Radius.Tests` `PolicyTests`, `AllowCaTests`, `ConversionTests` (fixtures of
  every stored format in `Fixtures/Conversion`), `PkiTabHeadlessTests`; `Oadm.Client.Tests` `PluginTabTests`;
  `Oadm.Core.Tests` `ExpiryWarningTests`.

## Not planned

- A limit of devices per switch port: OADM sees no logoff without RADIUS accounting; set the limit on the switch. (The
  802.1X server only caps its open sign-in sessions at 8 per switch port, `docs/spec/radius-server.md` "Limits and load".)
- Exporting a CA key on its own.
- Later: a CRL (and maybe OCSP) for RADIUS servers of other vendors; it would need its own list of removed serials.
