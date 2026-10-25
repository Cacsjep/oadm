# PKI, the certificate control center

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
The plugin's details: `docs/spec/bundled-plugins.md` (PKI) and `plugins/Oadm.Plugins.Pki/README.md`.

**Release (decision 2026-10-24):** all stages below ship together in **OADM 4.5** (with device groups and the 802.1X
server; there is no 4.2 release). Main may hold a stage in between; nothing is released before stage 6.

**Goal (decisions 2026-10-24):** PKI is the one place for everything about certificates. One page with the
**policies** (every rule: which certificate a device gets, what a certificate lets it do) and the **certificates** (every
certificate OADM knows, the CAs among them), plus the 802.1X and EST servers as tabs of the same page. Services that use certificates (802.1X server, EST server, the PKI tasks)
keep no certificate rules of their own: they apply PKI's policies and show only how they run (server state, switches,
sessions, logs, enrollments).

## One plugin: PKI with the 802.1X and EST servers

Decision 2026-10-24: the EST server (`oadm.est`) and the 802.1X server (`oadm.radius`) become part of the PKI plugin
package (`oadm.pki`). No contract between plugins is needed any more: one settings store, one backup, one set of
permissions, one policy list in PKI's own code.

- **One package, three core plugins, one rail page:** PKI, 802.1X server, EST server (a package may hold several core
  plugins, `docs/spec/plugins.md`); the pages of the two servers are tabs of the PKI page (see "The PKI page"). One folder `plugins/Oadm.Plugins.Pki` (+ `.Client`); the EST and 802.1X code keeps its
  own folders inside it (`Est/`, `Radius/`) with its source-available `LICENSE` (`docs/spec/licensing.md`); the rest of PKI
  stays Apache-2.0. THIRD-PARTY-NOTICES and the plugin's license notice name both.
- **On and off:** PKI is always on (as today). The 802.1X and EST servers are off by default and switched on their own
  tab ("Turn on" in the tab's off state, the stored choice kept like a plugin's on/off). They open their ports (UDP 1812,
  TCP 9454) only while on.
- **OADM PRO:** the 802.1X and EST servers start only with an active OADM PRO license (`PluginRegistry.ProActive`); without
  it their tab's off state reads "The EST server needs OADM PRO" with `ProLicenseTexts.NeedsPro` and offers no Turn on
  (Start 30-day trial while the trial is available, `docs/spec/licensing.md` "Trial"); a license that runs out stops them,
  a new one starts them again with the kept choice. PKI's own parts (CAs, issuing, Revoke, Certificates) never need a key:
  OADM itself is never limited. `licensing.md` changes from "only PRO plugins read it" to "PRO plugins and the PRO parts
  of PKI read it". Plugins page: the PKI row says "802.1X and EST servers need OADM PRO" in its description; no PRO chip
  (PKI itself is free).
- **Taking over 4.1 data, once, on the first start of 4.5:** the settings, records (enrollments, sessions, log, issued
  lists) and the on/off choice stored under `oadm.est` and `oadm.radius` move to PKI (a server on when its plugin was on).
  The old ids stay readable for this step only. A plugin folder `oadm.est` or `oadm.radius` left from 4.1 is not loaded
  (the loader skips it with one log line "Merged into PKI"); the installers remove it. Event rule events and actions,
  tasks, permissions and audit names keep their ids so stored rules and roles keep working (the permissions of 802.1X and
  EST keep their keys, now declared by PKI).
  Built (stage 2): the core plugins keep their ids `oadm.est` and `oadm.radius`, and the host keys settings
  (`Plugin:<id>:*`), records (PluginRecords), the data folder (`plugin-data/<id>`), events, permissions (`<id>:show`,
  `<id>:manage`, `task:oadm.est.setup:enroll`) and services by core plugin id: all of it stays where 4.1 stored it and is
  PKI's without a copy. The only choice stored per package, `Plugins.Enabled`, is taken over once
  (`MergedPlugins.TakeOverAsync`: `oadm.est` / `oadm.radius` become `Plugin:<id>:serverOn`, a choice made since wins, the
  entries leave `Plugins.Enabled`). Details: `docs/spec/plugins.md` "Merged packages" and "PRO parts of PKI".
- **Sites:** the site node's EST relay follows PKI (the relay asks the server by the new owner, still answers the old id
  for site nodes of 4.1 until they update). Built: the link to the node carries no plugin id (a relay is opened by protocol,
  port and reason, "EST server" on TCP 9454 as in 4.1), so nodes of 4.1 and 4.5 relay unchanged; the relay opens only while
  the EST server runs.

## The PKI page

Decisions 2026-10-24 (stage 7): one rail entry **PKI** (group Security) with fixed tabs (`ui:PageTab` without X):
**Certificates** | **Policies** | **802.1X server** | **EST server** (decision 2026-10-25: Policies has its own tab).

- **Policies** (PKI's own, only for users with all devices): one grouped table of the policies and a side panel to
  create and edit them (decision 2026-10-25, see "Policies"). Edit policies of a server tab opens it (`PkiTabRequest`).
- **Certificates** (PKI's own; decision 2026-10-25: two tables that share the tab's height, a `paneSplitter` between
  them): the **Certificate authorities** card (300 px at first, at least 250: every CA with its actions in the row menu,
  New OADM CA and Import (CA certificate, CA with its key: both add a CA, decision 2026-10-25) in its toolbar, the OADM
  CA's notice below that row; search, no groups or filters: `PkiListPart.Authorities`, the list's use filter CA), then the
  **Certificates** card, every other certificate (`PkiListPart.Certificates`: `CertificatesRequest.NoAuthorities`) with
  Create, Sign request and Import (at least 300 px; filters State and Issued by, no use filter: the Type badges say the
  use). One line per row: Name, Type (badges; an imported CA with its key "Can issue"; the OADM CA's trust as tinted badges
  (`Border.badge.tinted`) "OS root store: server", "OS root store: client" (decision 2026-10-25; tooltips "Installed in
  the operating system's trusted root store of the computer running the OADM server." / "... of this computer.", a
  failure its own reason)), Issuer, Key, Valid until (a state badge next to the date when it needs attention), the CA
  list's **Used in** (decision 2026-10-25: the names of the policies that use the CA, the first as a neutral badge and
  "+N", every name as tooltip, nothing for a CA no policy uses; only the CA list: `IssuedCertificateListViewModel.ShowsUsedIn`),
  ⋯; no Used by column. The OADM server's own TLS certificate is a read-only row of the Servers group ("OADM server", self-signed,
  View and Export only; `ITrustAnchors.ServerCertificate` from the host; never issued by a policy, the clients pin it).
  See "Certificates" for the actions.
- **802.1X server** and **EST server**: the pages of the core plugins `oadm.radius` and `oadm.est`, unchanged in content
  (Turn on / Turn off, the OADM PRO texts, their cards, permissions, events, tasks and audit names). They have no rail
  entry: their client pages say `ICorePluginPage.TabOf` = `oadm.pki` and the host hands their views to the PKI page
  (`docs/spec/plugins.md` "Pages shown as tabs"). The server parts keep their pages (`HasPage`), so the "show" and
  "manage" permissions stay as they are. While a server does not run its tab shows the off state; a server that is on
  but does not run (the license ended, a start that failed) offers Turn off there too, and Turn on and Turn off show a
  progress row ("Starting the EST server") while they run (fix of 2026-10-25), Start 30-day trial "Starting the trial".
- A server's tab shows only for users whose role has that core plugin's "show" permission (the server lists only the
  pages the role may show, the host follows role changes). With no server tab the tab row is left out.
- The link **"N policies applied"** on a server tab (decision 2026-10-25: left in its Save row) opens the Policies tab;
  its tooltip lists each of those policies in two lines, the name bold and the summary sentence below it
  (`Servers/PolicyLinesTip`, the state's `PolicyLines` from `PolicyStore.LinesOf`). The time column of the 802.1X sessions
  and of the EST enrollments is dimmed (`DataGridCell.dimmed`). Removing an EST profile asks first (red Remove). Everything that opens `plugin:oadm.radius` or
  `plugin:oadm.est` (`OpenAsync`, a role's start page, links of the Plugins page) opens the PKI page on that tab;
  `plugin:oadm.pki` opens it on Certificates. A click on the rail entry keeps the tab that was open.
- Only the open tab's view is in the window: a server tab reads its state when it is opened (like a page).
- Decision of stage 7: a role that shows a server but not PKI gets that server's page in the rail as before (there is no
  PKI page to hold the tab).
- Managing needs "Manage the certificate authority" and all devices (like Revoke today). Users without it see the page
  read only (no toolbar buttons, no row buttons but View); users limited to a device group see only the device
  certificates of their devices (no Policies tab, no actions). Their camera's certificate is found by the camera's serial
  like the list finds it (also one issued while the camera had another device id), and Used by of the CAs reaches them
  without policy names (fix of 2026-10-25: the policies are server-wide).

## Certificate authorities

| Kind | Example | Issues certificates | Source |
|---|---|---|---|
| OADM CA | "OADM CA" | yes | generated (**New OADM CA** replaces it; an import never does since 2026-10-25) |
| Previous OADM CA | "OADM CA 2025" | no (kept while its certificates live) | a replaced OADM CA |
| Axis device ID | "Axis device ID" | no | built in (the RSA and ECC roots and intermediates EST ships, `plugins/Shared/DeviceId`) |
| Imported CA | "Contoso Root" | with its key: yes ("Can issue") | **Import** > **CA certificate** (a root or intermediate certificate, PEM or DER; no key) or **CA with its key** (PKCS#12 or PEM + key; decision 2026-10-25) |

- The CAs are rows of the Certificate authorities card of the Certificates tab: name, kind, valid until, **Used in** (the
  policies that name it; the full list, e.g. "802.1X: OADM CA (only devices OADM manages)", is the tooltip and the
  section "Used by" of View); **View** (`ui:CertificateWindow`),
  **Remove** (imported and previous CAs; refused while a policy names it: "Used by the policy <name>. Change or delete the
  policy first.").
- The CA key is never exported on its own; it leaves OADM only in the encrypted CA backup (Back up) and the server
  backup. The trusted root store of the server stays as it is (what OADM trusts when it connects to devices).
- **Several issuing CAs** (decisions 2026-10-25): every import adds a CA; nothing replaces the OADM CA but New OADM CA.
  Any CA with its key (the OADM CA, an imported CA with its key) can issue: issuing policies and the 802.1X and EST server
  policies pick it as **Issued by**; every signing path signs with the CA of its policy. The key of an imported CA is kept
  like the OADM CA's (master key, never logged, never exported on its own, in the server backup). A CA a policy names
  (issuing, 802.1X trusted CA, EST sign-in CA) cannot be removed. Details: "Several CAs and the policy cleanup".
- A CA that leaves the list (expired previous CA) drops out of every policy at once (fail closed).
- **Used by** of a CA has one group per use: "802.1X: ..." (802.1X trusted CA), "Issuing: ..." (issuing policies),
  "RADIUS server CA: IEEE 802.1X" (the IEEE 802.1X certificate policies whose cameras check the RADIUS server with it; fix of
  2026-10-25), then the services.
- **A stored OADM CA that cannot be read** (the setting `ca` exists but is damaged; fix of 2026-10-25,
  `PkiStore.LoadCaStateAsync`): one error in the log, the value is never overwritten (no default CA over it, New OADM CA
  refused with "The OADM CA cannot be read. Restore a backup."), the page status says "OADM CA cannot be read" (detail
  "Restore a backup."). A backup restores it.

## Policies

**One grouped table and a side panel, like Intune (decision 2026-10-25; replaces the cards by area and Check a device,
which are gone):** the cards made the tab about two screens long. The Policies tab is one card and a side panel (`PkiView`
`PoliciesPage`, `PoliciesViewModel`):

- **Table**: **Create** (the tab's accent button; off with the reason at 200 policies) and **Search policies** (name, type,
  summary) in the toolbar row; the policies in groups with a collapsible header each (`PolicyTableGroups`, like the
  Plugins page: `DataGridCollectionView`; a group without policies shows no header, e.g. the PRO groups without OADM
  PRO), in this order: **Issuing policies** (HTTPS certificate,
  IEEE 802.1X certificate), **Server policies** (802.1X server, EST server), **802.1X access policies** (802.1X trusted
  CA, VLAN, Block), **EST enrollment policies** (Enrollment, EST sign-in CA), **Expiry warnings**; built-in policies
  first in their group, then the others in age order. Columns: **Name** (a red dot `Ellipse.problemDot.error` with the
  reason as tooltip for a later overlap or a CA that can no longer issue; the **Built-in** badge for a system policy),
  **Type** ("VLAN", "802.1X trusted CA"), **Summary** (the sentence, dimmed). Scale: at most 200 policies
  (`PolicyLimits.MaxPolicies`) in a virtualized DataGrid. A search that matches nothing says so in one line.
- **Side panel** (theme `Border.sidePanel` with `Border.sidePanelContent`, 560 px): it slides in at the right of the tab
  (0.2 s) and pushes the table to the left, like a classic side navigation; nothing covers the table. A click on a row
  opens that policy's form; **Create** opens "Create a policy" with every type and what it does in one line (never a
  second 802.1X server or EST server policy), **Next** shows the chosen type's form. The title is the policy's name (the
  Built-in badge) or "New VLAN policy", the type below it; the close button top right. At the bottom: **Delete** apart on
  the left (red, not for a built-in policy), the status ("Not saved"), **Cancel**, **Save** (Next while the types show).
  Save stores the policy and closes the panel; Cancel and the close button ask first when the form has changes ("Discard
  changes": Discard / Cancel), so does a click on another row or Create.
- **Form** (`PolicyFormView`, `PolicyEditorViewModel`): what the type does, the name (not for a system policy), the type's
  own fields, and for types with devices **Devices**: All devices, or the chosen sites, tags and devices in one line with
  **Choose devices** (the shared `DevicePickerWindow` in its group mode over a copy of the choice; Cancel changes nothing),
  the devices' error below that field. A system policy (`IsInPlace`) keeps its name and devices (a system Expiry warning
  for chosen devices names them below the days). Save is off without a change ("No changes to save."). Errors directly
  below each field. The policy dialog is gone. Fixes of 2026-10-25: the Devices field is a field of the form's validator
  (none chosen, tags of a Block that also revokes, the server's overlap refusal until the choice changes), so it blocks
  Save like any other error; the form keeps only the choice and makes the device picker only for Choose devices (never
  one over 30,000 devices per click on a row); it works out "changed" once per edit; a save locks the form and its
  Delete; a refusal of a field the form does not show (the kind) or of the whole policy goes into a message. An issuing
  policy whose CA cannot issue has its row's dot also when no CA can issue at all.
- A reload (another user's change) keeps a form with changes not saved, refreshes an unchanged one and closes the panel
  when its policy is gone; a form whose dialog is open (Choose devices, a question) stays as it is, and a click on a row
  opens the row as it is after the question. Every stored policy has a `Revision` (PKI counts its saves and the system's
  certificate write); a save sends the revision it read and PKI refuses one over a later change ("Changed by someone else.
  Reload.", `PolicyFields.Policy`), so no save overwrites another user's change unseen (fix of 2026-10-25). Read only
  (inputs disabled, no Create, Save or Delete) without "Manage the certificate authority". A role change keeps the open
  form and what was typed in it; only a change of what the user may do here acts: losing Manage makes the form of a stored
  policy read only and closes a new one, a device group that hides the tab closes it (`PoliciesViewModel.NotifyRole`).

System policies (decision 2026-10-25, `PolicyStore.IsSystem`): OADM needs them, so they have no Delete (the server refuses
it: "OADM needs this policy. Change its values instead."); their values stay editable. They are the first (oldest) policy
of HTTPS certificate, IEEE 802.1X certificate, 802.1X server, EST server, Expiry warning and Enrollment. Add never
offers a second 802.1X server or EST server policy. Users add their own (User) policies: further issuing policies (picked
in the task dialogs and the EST profiles), 802.1X trusted CA, VLAN, Block, EST sign-in CA and Expiry warning or
Enrollment for some devices. The summary (decision 2026-10-24) is one plain sentence that says what the
policy does with its values (one builder per kind, `PolicyStore.Summary`, no preview line in the editor (decision 2026-10-25);
"these devices" stands for the Devices column; up to two lines in a rule list, the whole sentence as tooltip): "Certificates
with 30 days or less left show a warning and raise Certificate expires soon.", "Enable HTTPS issues certificates from OADM
Root CA acs, valid 365 days, with an ECC key.", the IEEE 802.1X one adds "Cameras sign in with their MAC address." (host
name, or "as <identity>"), "The 802.1X server shows the certificate 802.1X server (OADM CA), until 2035-10-25. It accepts
TLS 1.2 or later, sends EAP packets of up to 1,020 bytes and lets the switch decide when cameras sign in again." ("... asks
cameras to sign in again every 60 minutes."), "The EST server shows the certificate EST server (OADM CA), until
2035-10-25." ("... has no certificate yet."), "The 802.1X server lets in devices
OADM manages that have a certificate from ..." / "... lets in any device with a valid certificate from Contoso Root.",
"The 802.1X server puts these devices in VLAN 30.", "The 802.1X server refuses these devices." ("... Their IEEE 802.1X
certificates are revoked, new ones too." with Also revoke), "These cameras may get certificates from the EST server for every profile." ("... for
the profiles https and dot1x.", "These cameras may not get certificates from the EST server."), "Cameras sign in to the EST
server with their Axis device ID for new certificates and renewals.", "Cameras with a certificate from ... may sign in to
renew it.". The editor is the form of the side panel (see above): the kind's description, name, the kind's fields (each
kind its own form, e.g. the 802.1X server policy: Certificate, Minimum TLS version, Largest EAP packet, Reauthentication),
the Devices field where the kind has devices.

Without OADM PRO the table shows only the HTTPS certificate, IEEE 802.1X certificate and Expiry warning policies
(`PolicyKinds.IsPro`; stored PRO policies stay and come back with a license).

| Group | Kind | Fields | Devices | Policy there from the start (Built-in: editable, never deleted) |
|---|---|---|---|---|
| Issuing policies | **HTTPS certificate** | Issued by (a CA with its key), valid (days, 1 to 3,650), key (RSA, ECC) | no | Built-in "HTTPS" (OADM CA, 365 days, ECC; RSA where an older OADM ran before) |
| Issuing policies | **IEEE 802.1X certificate** | Issued by, valid (days), key, EAPOL version (1, 2 or 3), EAP identity (MAC address, host name, IP address, last octet of the IP address, custom; the two IP modes decision 2026-10-25), RADIUS server CA (a CA of the list; decision 2026-10-25, see "RADIUS server CA") | no | Built-in "IEEE 802.1X" (OADM CA, 365 days, ECC, RADIUS server CA the OADM CA; a new installation EAPOL version 2 (works from AXIS OS 6.53; 3 needs 10.1) and the IP address as identity, decision 2026-10-25; one used before keeps its identity, EAPOL version and RSA) |
| Server policies | **802.1X server** (one only, PRO) | Certificate (one of the Certificates list with a key OADM holds, ECC; see "Server certificates"); Minimum TLS version (1.0, 1.1, 1.2, 1.3; 1.0 and 1.1 with the hint "Only for old devices"; the cipher suites follow it, no selection, decision 2026-10-25), largest EAP packet (500 to 1,400 bytes), reauthentication (Switch decides, or every 10 to 1,440 minutes: sent to the switch as the session timeout) | no | Built-in "802.1X server" (the shared certificate "802.1X and EST server", see "Server certificates"; TLS 1.2, 1,020 bytes as RFC 3748 3.1, Switch decides) |
| Server policies | **EST server** (one only, PRO) | Certificate (one of the Certificates list with a key OADM holds; no Create in the policy; see "Server certificates") | no | Built-in "EST server" (the shared certificate "802.1X and EST server") |
| 802.1X access policies (PRO) | **802.1X trusted CA** (stored `allowCa`; "Allow CA" before 2026-10-25) | CA, Only devices OADM manages (on) or any device with a valid certificate of it | no | "OADM CA" (with its previous CAs) and "Axis device ID", both only managed |
| 802.1X access policies (PRO) | **VLAN** | VLAN ID (1..4094) | yes | none |
| 802.1X access policies (PRO) | **Block** | Also revoke their IEEE 802.1X certificates (off; on: devices and sites only, never tags or All devices) | yes | none |
| EST enrollment policies (PRO) | **Enrollment** | profiles the devices may use (all, or chosen ones), or no enrollment | yes | Built-in "All devices" (every device OADM manages, all profiles) |
| EST enrollment policies (PRO) | **EST sign-in CA** (stored `signInCa`; "Sign-in CA" before 2026-10-25) | CA, for new certificates or for renewals | no | "Axis device ID" (new), "OADM CA" (renewals) |
| Expiry warnings | **Expiry warning** | days before the end (1 to 365) | yes | Built-in "All devices" (30 days) |

A new server thus counts 3 policies on the 802.1X tab (802.1X server, two 802.1X trusted CA) and 4 on the EST tab (EST
server, Enrollment, two EST sign-in CA).

- **Issuing policies have no devices.** The task dialogs (Enable HTTPS, Enable IEEE 802.1X; Renew certificates and
  Install certificates for IEEE 802.1X since 2026-10-25) let the user pick the issuing policy of their kind; the first one
  is preselected and its summary sentence shows below the field. The run uses the policy's values as stored (decision
  2026-10-25: no values of their own in the dialog, the policy decides). EST issues by profile: an EST profile names its
  issuing policy (the profile name stays what the camera uses in its URL). Users add more issuing policies, e.g. "HTTPS 2
  years", or an IEEE 802.1X one with RSA for devices before AXIS OS 12 (the built-in one already uses EAPOL version 2; see
  "Older AXIS OS").
- **No overlaps** (decision 2026-10-24): saving is refused when a device is in two policies of the same kind
  ("10.0.0.48 is in Building B and PTZ."), checked against the devices of now; the same CA in two 802.1X trusted CA or two
  EST sign-in CA policies is refused too. When a tag or site change creates an overlap later, both policies show a warning
  in their row and the safer one applies: Block over VLAN; of two VLAN policies, the older one; of two Block policies the
  one that also revokes.
- A device that no Enrollment policy matches is refused by the EST server ("No enrollment policy for this device"),
  as a device OADM does not manage is today.
- A device that no VLAN policy matches gets no VLAN from the server (the switch port keeps its own). There is no default
  VLAN.
- **All devices** (decision 2026-10-24): the device selector offers "All devices" next to "Chosen devices" (every kind with
  devices except a Block that also revokes). It holds every device, also those of sites added later and, for the 802.1X kinds, devices OADM
  does not manage (a certificate of an 802.1X trusted CA for any device: an "All devices" VLAN gives its VLAN, an "All devices"
  Block refuses it). It overlaps every other policy of its kind: saving is refused as for any overlap ("All devices includes
  every device, also those of PTZ."); converted data keeps the overlap with its warning.
- **Expiry warning** (decision 2026-10-24) applies to every device certificate, whoever issued it (the HTTPS certificate
  polling reads, as today). The events **Certificate expires soon** and **Certificate expired** move from the built-in
  events (fixed 30 days, `docs/spec/event-rules.md`) to PKI's event group "Certificates" and use the policy's days; they
  keep their ids and their Any device twins, so stored event rules keep working. The device grid's "Certificate
  expires" chip and the Compliance check's default days follow the policy too (a Compliance check with its own days keeps
  them).
- **Block with Also revoke** (decision 2026-10-25: the former Revoke): on save it revokes the IEEE 802.1X certificates of
  its devices, then every new certificate of them while it exists (the minute round); no tags and no All devices while the
  box is checked (a tag could revoke every device that gets it later, without undo). Without the box nothing is revoked.
- Every change is audited; the 802.1X and EST servers apply a change at once (fail closed while a change is applied).
- The policy kinds live in PKI's code (no SDK): one store, one editor, the 802.1X and EST servers read it in process; a
  server never stores rules of its own. Kinds of a server that is off stay listed and editable (they apply once it runs).

## Certificates

Every certificate OADM uses now (decision 2026-10-25: no history, see "Current and pending"), paged on the server (search
over every column, filters: issued by, state; no use filter since 2026-10-25), so 30,000 devices with two certificates
each stay fast. The full management view of PKI: two cards of the shared `ui:IssuedCertificateList` (virtualized, pages of
500) on the Certificates tab, the Certificate authorities card and the Certificates card (see "The PKI page").

- Includes the CAs, each managed camera's current certificate per use (tasks and EST), OADM's server certificates and the
  certificates created, signed or imported here.
- **Columns** (decisions 2026-10-24, one line per row where it fits): **Name** with a second line (the key and the issuer:
  "RSA 4096 · issued by OADM Root CA SERVER01", a device certificate "Issued by OADM Root CA SERVER01", a root CA its key
  only); **Type** as badges (neutral pills, `Border.badge.neutral`): Root CA or Intermediate CA plus OADM CA, Previous OADM
  CA, Axis device ID or Imported for a CA (and "Can issue" for an imported CA with its key); HTTPS, IEEE 802.1X, MQTT,
  RTSPS or Server; Signed or Imported for the certificates signed or imported here (a created one has no badge of its own, decision 2026-10-25); the OADM CA row adds next to its badges where
  it is installed ("OS root store: server", "OS root store: client" since 2026-10-25, "Failed on ..." with the reason, the
  progress of an install); **Valid until**: the date, and below it a status (`ui:StatusChip`, one line) only when the state needs
  attention: "Expires in 12 days" (amber, the Expiry warning policy's days), "Expired" and "Revoked" (red, the revocation
  as tooltip); a valid certificate shows its date only (there is no State column; the state filter stays);
  the CA card's **Used in** (decision 2026-10-25, replaces the Used by column: the first policy name as a neutral badge and
  "+N", every name as tooltip); what uses a certificate ("802.1X: OADM CA (only devices OADM manages)", "Issuing: HTTPS,
  IEEE 802.1X, ...", a server certificate "802.1X server", a device certificate its device) stays the section "Used by" of
  View; one narrow fixed column with a ⋯ button (the row's actions menu).
- **Group by** (a select in the Certificates card's toolbar, not in the CA card, decision 2026-10-24): **Type** (default): Certificate authorities, Server
  certificates, Created, signed and imported, Device certificates; **Issuer**: one group per CA in the order of the CAs
  (OADM CA, previous OADM CAs, Axis device ID, imported CAs, Other CA); **None**. The server orders and counts the groups
  (`CertificatesRequest.GroupBy`, `CertificatesReply.Groups`), so a page of 500 cuts across groups and each header names the
  group's count over every page ("Device certificates · 60,000"). The grid's group headers are the shared `DataGrid.groups`
  style (collapsible). The choice is kept on this computer.
- **Toolbars** (decision 2026-10-25: two cards, no Create menu): the Certificate authorities card has **New OADM CA** and
  **Import** (menu: **CA certificate** and **CA with its key**, both add a row) left and the search right; the Certificates
  card has **Create**, **Sign request** and **Import** as plain command buttons left, and Group by, the state and issuer
  filters and the search right-aligned in the same row; on a card too narrow for both the filters go below as a whole
  (`ToolbarRow`), never cut.
- **Row actions** (decision 2026-10-24): one ⋯ button per row (`Button.toolbar.iconOnly`, `Icon.more`); its menu is the
  same as the row's right-click menu (one source, `IssuedCertificateListViewModel.MenuOf`): the everyday actions, then a
  separator and the red ones last (`MenuItem.danger`). A double click is View.
  - OADM CA: **View**, **Export** (PEM, DER: the public certificate, as Export public certificate before), **Install in
    trusted root store** (the server and this computer, `oadm.pki:trustServer`), **Back up** (the CA with its key).
  - Previous OADM CA and imported CA: **View**, **Export** (PEM, DER), then **Remove** (red; refused while a policy names it).
  - Axis device ID: **View**, **Export** (PEM, DER).
  - Every other row: **View**, **Export** (PEM, DER; **With its key** where OADM holds it), then the red ones that apply:
    **Revoke** (device, created and signed certificates that are still valid; never a server's own certificate, fix of
    2026-10-25), **Revoke all of this device** (device certificates), **Delete**.
  - Several chosen rows: the menu of any of them offers **Revoke N certificates** and **Delete N certificates** (red).
  - Users who may not manage the CA get View only.
- No OADM CA, a CA being created, a key that cannot be read or a server that cannot keep a CA: the PKI status
  (`ui:StatusChip` with its detail) stands below the toolbar row, above the list; nothing when the OADM CA is fine (its row says it).
- Actions:
  - **Create**: OADM makes the key and the certificate from an issuing policy (name, addresses, use); download as
    PKCS#12 with a password. The key is kept, encrypted with the master key like the CA key, and is part of the backup.
  - **Sign request**: upload a request (PKCS#10, PEM or DER) from another system; its key never leaves it.
  - **Import**: a certificate (PEM, DER) or a PKCS#12 with its password (the key is kept like a created one).
  - **Export**: PEM, DER; PKCS#12 only where OADM holds the key (created, imported with key, server certificates), with
    a password. Device certificates have no key in OADM (the camera made it). Every export with a key is audited.
  - **View** (`ui:CertificateWindow`), **Revoke** (as today), **Delete** (only certificates nothing uses; revoked or
    expired ones, imported ones no longer needed, and created or signed ones). **Revoke and delete** (decision 2026-10-25:
    users could not delete what they created): Delete is always offered for created and signed certificates; a still valid
    one is revoked first (reason Other, by the user, audited with the delete), all or none. The confirmation says so ("Lab
    cert is still valid. It is revoked first, so no device can use it any more."; several: "3 of them are still valid. They
    are revoked first, ...") and its red button reads **Revoke and delete**. Refused for a certificate a server policy shows
    ("Used by the policy 802.1X server. Pick another certificate first.").
  - Create and Sign request never name an Axis camera (`axis-<12 hex>`, any domain): only device certificates do.

## Current and pending (decisions 2026-10-25)

The Certificates list shows what is in use now, not the history. Code: `Ca/IssuedRegistry` (`plugins/Oadm.Plugins.Pki/README.md`
"Issued certificates and revocation").

- **The store** (plugin records `issued`, `issuedDer`): per camera and use (HTTPS, IEEE 802.1X, MQTT, RTSPS) the
  **current** certificate (the one confirmed on the camera) and at most one **pending** one (issued, not confirmed yet).
  Cameras are matched by their **serial** (`deviceSerial`, from `axis-<serial>.local`), never only by the device entry's id:
  a camera removed and added again gets a new id. An entry without a serial (OADM before 4.5) counts by its device id;
  the list then hides it when the camera has a newer current one under its serial. Every caller that keys by device finds
  a camera added again: `ForDevice(IDeviceInfo)` and `RevocableOf(IDeviceInfo)` look up the id and the serial (the tasks'
  reads, Revoke all of this device, the Revoke policies). Created, signed and imported certificates and the servers'
  certificates are kept as before.
- **The list** (`IssuedRegistry.Query`, `currentOnly`): the current certificate of each use of each camera OADM manages,
  not deleted from the camera by a task. A current one stays listed when it is expired or revoked from before (the camera
  needs attention). Not listed, but kept for the 802.1X checks, EST and Revoke: pending ones, older entries of OADM before
  4.5 and the current ones of cameras OADM no longer manages (they stay valid, no revocation on removal; a camera added
  again gets its rows back). The state Replaced is gone from the list and its filter (`IssuedCertificateState.Replaced`
  stays in the SDK, never answered). The CA service's `ListIssuedAsync` still lists every stored certificate.
- **Confirmed** (the pending one becomes current; the previous current one goes on the revocation list with the reason
  `RevocationReason.Superseded` (shown "Replaced by a newer certificate"), user "OADM", and leaves the store):
  - Enable HTTPS, Renew certificates (HTTPS): after Verify HTTPS passed.
  - Enable IEEE 802.1X, Renew certificates (IEEE 802.1X): after Verify 802.1X settings read back 802.1X on with the new
    certificate and its CA certificates.
  - Install certificates (a file OADM did not issue): after its verify step the camera's current OADM certificate of that
    use is revoked the same way (the file's certificate is not stored).
  - EST, HTTPS, RTSPS and MQTT profiles: when `EstHttp.WriteAsync` of the 200 answer returned (the answer is handed to
    TLS; the camera is not asked to acknowledge).
  - EST, IEEE 802.1X profile: only when the camera next signs in to OADM's 802.1X server with the new certificate. With the
    802.1X server off or another RADIUS server it stays pending; the previous one stays valid.
  - Any other caller of the CA service (`SignAsync` with a device, a source other than EST): current at once.
- **Unused** (revoked with the reason Other, user "OADM", and removed from the store): an install that failed before the
  certificate was in use (HTTPS: any failure before the switch request was sent, or after it when a read of the web server
  settings shows another certificate), an EST answer that could not be written, an EST
  certificate still pending after `IssuedRegistry.PendingTimeout` (1 hour) without its answer (the server stopped in
  between). A newer pending one of the same camera and use replaces an older one (Superseded).
- **Device safety (IEEE 802.1X tasks):** once "Set 802.1X configuration" ran, the new certificate is `Delivered` and never
  revoked as unused, unless the read-back names another client certificate. A newer pending one does not revoke it either
  (it stays in the store, not current). While the read-back does not confirm the new certificate the task deletes no
  previous OADM certificate from the camera (Remove previous OADM certificate: Skipped).
- **Device safety (HTTPS tasks, fix of 2026-10-25):** once the switch request of "Switch web server to the new certificate"
  was sent, the new certificate is `Delivered` (the web server may present it after a slow restart, or the device applied
  it and the answer got lost). A failure from there on (the switch answer, Verify HTTPS) reads the web server settings
  (`CertificateDeployment.HttpsNotVerifiedAsync`): another certificate named there revokes it as unused; the new one named
  there or a read that fails keeps it pending, the previous one stays current and valid ("stays pending" in the log).
- **Remove previous OADM certificate** after a confirmed switch: a delete the device refuses is a Warning log line and a
  step warning (Done with warnings, "delete it with Delete certificates"), never a failed task; the others are still removed.
- **Revoke** (the page, Revoke all of this device, the Revoke policies, the 802.1X server's policy revokes): the
  certificate goes on the revocation list and leaves the store; its camera has no row until it gets a new certificate. The
  revoke dialog and its reasons are unchanged; Delete stays for what ended or was revoked before 4.5, and for created,
  signed and imported certificates.
- **The revocation list** (`revoked`): serial, time, reason, user, device, valid until; kept until the certificate ends.
  The 802.1X server and EST sign-in check it as before: an OADM certificate passes unless it is on the list (certificates
  missing from the store pass, so an update, a backup restore and `ImportFoundAsync` stay safe). `ImportFoundAsync` never
  adds a serial that is on the list.
- **Existing data:** no conversion. Entries of OADM before 4.5 stay; the newest of each camera and use counts as current,
  the others are hidden. Nothing is revoked by the update.
- **Pruning:** a stored certificate leaves `KeptAfterEnd` (30 days, before 365) after its end, also a hidden current one of a
  camera OADM no longer manages; server certificates the same.
- **Audit:** automatic revocations go to the server log ("PKI: revoked N certificates (Replaced by a newer certificate)
  for OADM") and to the task's log; plugins have no audit API.
- **Later:** a CRL for other vendors' RADIUS servers would need its own list of removed serials.
- **Tests:** `Oadm.Plugins.Pki.Tests` `CurrentCertificateTests`, `CertificateConfirmationTaskTests`, `IssuedRegistryTests`,
  `IssuedScaleTests` (Category=Perf); `Oadm.Plugins.Est.Tests` `ServerTests` (the delivery after the answer);
  `Oadm.Plugins.Radius.Tests` `ServerTests` (the sign-in).

## Converting existing settings

On the first start after the update, once, without asking:

- **The kinds of 2026-10-25** (`PolicyStore.ConvertKinds`, owner `oadm.pki:kinds-2026-10-25`, once, never on a store that
  cannot be read, nothing the user set lost): the Server certificate policy of the 802.1X server goes into the 802.1X
  server policy (its CA, days and key; without an 802.1X server policy one with the TLS defaults takes them), the one of
  the EST server becomes the EST server policy in its place (id, name and values kept); a Revoke policy becomes a Block
  policy with Also revoke, a Block policy keeps it off; names stay, made unique per kind ("Stolen (2)"); without an EST
  server policy one with the defaults is added. A server converted from 4.1 gets the new kinds directly (the issuing
  conversion makes the EST server policy, the 802.1X server's conversion the 802.1X server policy with its certificate's
  defaults and Block policies with Also revoke).
- PKI's Device certificates card (validity; its warn before expiry becomes the Expiry warning policy "All devices") and
  IEEE 802.1X card (EAPOL version, EAP identity, RADIUS server CA): the
  default issuing policies "HTTPS" and "IEEE 802.1X" with these values.
- 802.1X Advanced (minimum TLS version, cipher suites, largest EAP packet): the 802.1X server policy.
- 802.1X Trusted CAs: one 802.1X trusted CA policy per ticked CA, with its "Only devices OADM manages".
- 802.1X policies: VLAN and Block policies (Revoke: Block with Also revoke) with the same names, devices and order. A Default VLAN of a development
  build before 4.5 (the 802.1X server never shipped before 4.5) becomes a last VLAN policy "All devices" (every device, also of sites added later and devices
  OADM does not manage), so no device changes its VLAN.
- EST: the Enrollment policy "All devices" (as today: every managed device, every profile). Sign in with: one EST sign-in CA
  policy per ticked CA. EST profiles keep their names and name the built-in issuing policy of their kind (decision
  2026-10-25: one issuing policy per kind by default; the profile's old validity and key are no longer read; a profile may
  still pick a custom HTTPS or IEEE 802.1X policy).
- **EST issuing policies of before 2026-10-25** (`EstPolicies.CleanUpAsync`, owner `oadm.est:issuing-2026-10-25`, once,
  never on settings or a store that cannot be read): a profile that names a policy the EST conversion made ("EST HTTPS",
  "EST 802.1X", "EST " + the profile's name, also "EST https" / "EST dot1x") names the built-in policy of its kind; those
  policies are then deleted where no other profile names them (task dialogs and created certificates only record them as
  history). The rename of 2026-10-24 ("EST https" → "EST HTTPS") is gone.
- **Server certificates** (decision 2026-10-25, see "Server certificates"): the 802.1X server's own certificate (kept with
  its key by the 802.1X server) is imported into the Certificates list once and picked by the 802.1X server policy, so
  cameras keep working; the EST server policy gets a new certificate from the OADM CA (10 years, ECC) on its first start.
- Overlaps found while converting are kept and show their warning; nothing is dropped.

## Clock check before installing a certificate

Decision 2026-10-24: a camera whose clock is behind sees a new certificate as "not valid yet".

- Every certificate the OADM CA issues to a device starts **5 minutes in the past** (`NotBefore = now - 5 min`), which
  covers small drifts silently (PKI tasks and EST).
- The dialogs of **Enable HTTPS**, **Enable IEEE 802.1X**, **Renew certificates** and EST's **Set up** read the camera's clock first (read-only
  query, `time.cgi` / the HTTP Date header as today). If the camera is still more than 5 seconds behind the
  certificate's start, they ask before starting: "The camera's time is 9 min 12 s behind. The certificate becomes valid on
  the camera only in 4 min 12 s. Set the camera to use NTP. Continue?" **Yes** / **No**. Several cameras: one question
  listing how many and the largest drift. The task gets the user's answer and does not ask again; a camera that is ahead
  is fine. The dialogs read at most the first 100 selected cameras (3 seconds each); the tasks check every camera.
- A run without the answer (no dialog) still refuses a camera behind the certificate's start ("Nothing was changed").
  Renew certificates checks the clock once before it renews the first purpose (HTTPS too, fix of 2026-10-25), with the
  answer of its dialog (since 2026-10-25).

## Pages of the services after this

The two pages are tabs of the PKI page since stage 7 ("The PKI page").

- **802.1X server:** Server (Listen on, Port, Log entries kept; no Advanced dialog: its settings are the 802.1X server
  policy; the link "3 policies applied" on a new server, left in the Save row), Switches, one Sessions table (in progress on top, then the log). No Trusted
  CAs card, no Policies card, no Default VLAN.
- **EST server:** Server (the card was General before 2026-10-25; with the link "4 policies applied" on a new server, left in the Save row), Profiles (name, use, issuing policy; no On column, decision 2026-10-25: every listed profile is on, a profile not wanted is removed), Enrollments in
  its own card. No Sign in with (stage 6: Sign-in CA policies).

## Rules for the move (decisions 2026-10-24)

- The 802.1X tab shows the link "N policies applied" (opens the Policies tab); Sessions and Log rows name the policy that matched
  ("VLAN 30 by Building B", "Blocked by Storage room").
- OADM PRO: see "One plugin"; PKI's own kinds (issuing, Revoke) always work.
- The conversion is tested on a copy of real data (a backup of a 4.1 server and of the development server) before the
  merge, not only on test data.

## Code (stage 3)

Built 2026-10-24. The rules of this stage that the sections above leave open are marked "decision of stage 3" (the most
conservative choice that fits the spec; to be confirmed by the maintainer).

- **Store** (`plugins/Oadm.Plugins.Pki/Policies/`): `PolicyModels.cs` (the contract shared with the page: `PolicyKinds`,
  `PolicyGroups`, `PkiPolicy` with the fields of every kind, `PolicyDevices`, `PolicyList`, `PolicyMethods`, `PolicyTexts`),
  `PolicyRules` (the checks of one policy, the same on the page and in the store; `PolicyMatcher`: hash sets of sites, tags
  and serials, O(1) per device), `PolicyStore` (PKI's setting `policies`: the list in age order and the converted owners;
  an immutable snapshot with a version, `Changed` after the write). PKI offers the store in process
  (`IPluginServices.Offer<PolicyStore>`); the 802.1X server finds it each time. No SDK contract.
- **A stored list that cannot be read** (`PolicyStore.IsUnreadable`): one error in the log per load, nothing converted or
  written (the stored value is never overwritten), every save and delete refused, the 802.1X server refuses every device
  ("The PKI policies cannot be read", alert 80), the Policies card says "The policies cannot be read. Restore a backup."
  below the table and is read only.
- **Checks on save**: the policy on its own (name, the kind's fields, devices; Revoke without tags), then against the
  others: a name taken by another policy of the same kind (decision of stage 3: names are unique per kind, so "All devices"
  can be an Expiry warning and a VLAN policy), one 802.1X server policy, an Allow CA naming a CA PKI does not list or the same
  CA twice, sites that no longer exist, at most 200 policies, and a device of now that the policy shares with another of its
  kind and did not share before the save ("10.0.0.48 is in Building B and PTZ.", "... and 3 other devices are in ..."; an
  overlap stored before, e.g. by the conversion, never blocks an edit of something else). One pass over the devices per other
  policy of the kind, only over the devices the policy holds. A policy keeps its kind (decision of stage 3: the editor offers
  the kind for a new policy only).
- **Later overlaps** (a tag or site change): `PolicyStore.Rows` gives both rows the warning with the one that applies: the
  older VLAN policy, the longer Expiry warning. Block and VLAN may hold the same device (different kinds, no warning): Block
  refuses first. Overlaps of Block or Revoke policies change nothing.
- **Expiry warning**: the days of a device are the longest of the Expiry warning policies that hold it; a device no such
  policy holds keeps 30 days. A check that
  covers no single device (a new Compliance check) gets the longest days of the policies, else 30.
- **Tags**: the tags of every policy except Revoke are protected under PKI (`ctx.ProtectedTags`, "The tag Floor 3 is used by
  a PKI policy. ..."); the 802.1X server releases the tags it protected until OADM 4.5. Without the host's protection the tag
  parts of VLAN policies match no device (fail closed), those of Block and Expiry warning still do.
- **Page methods** (`PolicyMethods`, PKI's `InvokeAsync`): `getPolicies` (rows with summary and warning, the CA choices, the
  cipher suites, the sites, the tags of the devices and policies, `mayManage`; a user limited to a device group gets an
  empty list), `savePolicy` (one policy, `PolicySaveReply` with the field error or the list), `deletePolicy`,
  `countRevocations`. Saving and deleting: Admin with "Manage the certificate authority" and all devices, audited by the
  host; the count only reads (not audited). Event `policies` after every change.
- **Revoke** (PKI's own, never needs OADM PRO): a save of a revoke policy revokes the IEEE 802.1X certificates of its
  devices (never by tag) that are neither ended nor revoked, by the user who saved it, reason Other; a round every minute
  revokes new ones (EST, tasks). The 802.1X server still refuses a device a revoke policy holds and revokes the certificate
  it met.
- **The 802.1X server** builds `RadiusRuleSet` from the store (`RadiusPolicies`): the trusted CAs of the Allow CA policies,
  `PolicyPlan` (VLAN, Block, Revoke) and the server policy's TLS, largest EAP packet and reauthentication. A handshake
  rebuilds it first when the store's version moved or PKI started again (a new store), so a change applies to the next
  handshake and never half; without the store nothing passes ("The PKI has no certificate authority", alert 80).
  The cipher suites follow the minimum TLS version (decision 2026-10-25: no selection; `PkiPolicy.CipherSuites` is no
  longer read): `PolicyRules.DefaultSuites`, the suites listed for the minimum (the old CBC suites only for TLS 1.0 and
  1.1) that the server certificate can use (never the ECDHE_RSA ones with its ECDSA key). The summary says "with secure
  cipher suites".
  Reauthentication (the editor: "Switch decides" or "Every" with Minutes next to it; decision of stage 3: 10 to 1,440
  minutes) sends Session-Timeout and Termination-Action RADIUS-Request in every Access-Accept (RFC 3580 3.17, 3.19).
  Sessions and Log carry the VLAN policy (`VlanPolicy`: "VLAN 30 by Building B"); refusals name Block and Revoke policies
  ("Blocked by Storage room", "Certificate revoked by Stolen").
- **Conversion** (once per owner, `PolicyStore.ConvertOnceAsync`, the owner noted in the stored list): PKI at its start
  (`PkiService.ConvertOwnSettings`: "All devices" with the stored warn days, `PolicyDevices.AllDevices`); the 802.1X server's
  core plugin at its start hands its settings over (`RadiusPolicies.ConvertAsync`; PKI starts first, the core plugins of a
  package start in name order; without the store the next start converts). A conversion that runs after policies exist
  merges: an Allow CA of a CA already allowed changes the one there (only devices OADM manages when either was: the
  stricter), a second 802.1X server policy or a second "All devices" VLAN policy is not added. The 802.1X server reads a CA
  named twice anyway (`CaTrust.Chosen`) as only managed when either is. The old settings stay where they are (decision of
  stage 3: nothing is deleted; they are no longer read). A server without them gets the defaults of the kinds the same way.
- **Expiry events** (`ExpiryEvents`, PKI is an `IEventSource`): the ids `oadm.device.certificate-expires-soon`,
  `oadm.device.certificate-expired` and their Any device twins (group "Certificates" for all four, decision of stage 3), the
  full report at the start, a round every minute and after a change of the policies. The host lost them from its built-in
  events (`BuiltInEvents` keeps the ids as constants); stored rules find them under PKI.
- **The grid's chip and Compliance** (SDK 0.4.0 `ITrustAnchors.SetExpiryWarning(daysFor, defaultDays)`, only PKI's count):
  PKI hands the host its days when the Expiry warning policies changed; the host sends every device row again
  (`ExpiryWarningPublisher`, one batch) with `Device.cert_warning_days`, and the Compliance catalog gives a new certificate
  check the default days. The PKI page's own list: each device's days (`CertificatesReply.WarningDays` is null).
- **PKI page** (`PkiView`, `PoliciesViewModel`, `PolicyEditorViewModel`, `PolicyFormView`): the top row side by side from
  1,280 px of page width, else stacked (`PkiView.Arrange`). Certificate authorities: the OADM CA's state and trust chips,
  the list (previous CAs as rows with Export and Remove, imported CAs with Remove, View for all), Generate new CA and Import
  CA in the header (Import CA a menu: "Own CA with its key" replaced the OADM CA, "CA certificate only" added a row; since
  2026-10-25 "CA certificate" and "CA with its key", both add), Install in trusted root store, Export public certificate and Back up below the list.
  Policies: the filter as a segmented switch (All, Certificates, 802.1X, Revoke; Issuing since stage 4), rows with Edit and Delete icon buttons,
  the warning as an amber chip in the summary. The editor is the shared `DevicePickerWindow` with the form on top; for kinds
  without devices the device part hides (`DevicePickerWindow.IsPickerVisible`). The Device certificates card kept only the
  validity (gone in stage 4); users limited to a device group still see the page with the Certificates of their devices (decision of stage 3:
  the Certificates list of stage 1 stays; the Policies card is hidden for them).
- **802.1X page**: Server (with "6 policies apply" and Edit policies: `ICorePluginClientContext.OpenAsync("plugin:oadm.pki")`)
  and Switches side by side from 1,000 px of page width, one Sessions table below (`AuthenticationsViewModel`: in progress on top, then
  the log; one pixel row inside the page's scroll viewer); every column but the state or result is as wide as its longest text (`MinWidth` =
  `Width`, the grid scrolls sideways on narrow pages). The switch dialog no longer names the RADIUS server's address; the
  shared secret needs 8 characters or more.
- **EST page**: its own cards (`EstPage.HasOwnCards`): two columns (decision 2026-10-25): the cards Server (with Save) above
  Profiles left (680 px, the same as the 802.1X tab: `ServerPageLayout`; Add profile a flat toolbar button), Enrollments with Not renewed right, as high as the page; below 1,400 px
  of page width one column, Enrollments in a 480 px row below (`EstView.Arrange`).
- **Tests**: `Oadm.Plugins.Pki.Tests` `PolicyStoreTests`, `ExpiryEventTests`, `PolicyPageTests` (+ headless at 1,700 and
  1,000 px), `PolicyPerfTests` (Category=Perf: 30,000 devices); `Oadm.Plugins.Radius.Tests` `PolicyTests`, `AllowCaTests`,
  `ConversionTests` (fixtures of every stored format in `Fixtures/Conversion`); `Oadm.Core.Tests` `ExpiryWarningTests` and
  the rule engine's stored rule; `Oadm.Server.Tests` the Compliance default and the device rows' days.
- **Not built yet**: the conversion on a copy of real data (a backup of a 4.1 server and of the development server, see
  "Rules for the move"): no such backup was at hand.

## Code (stage 4)

Built 2026-10-24. Rules the sections above leave open are marked "decision of stage 4" (the most conservative choice that
fits the spec; to be confirmed by the maintainer).

- **Kinds** (`PolicyKinds.HttpsCertificate`, `Dot1xCertificate`, `ServerCertificate`, group Issuing, no devices):
  `PkiPolicy.CaId` (only the OADM CA issues: "Only the OADM CA issues certificates."), `ValidDays` (1 to 3,650, the range of
  the former Device certificates card), `Key` (`PolicyKeys`: ecc, rsa); IEEE 802.1X adds `EapolVersion`, `Identity`,
  `CustomIdentity`, `RadiusCa`, `RadiusCaPem` with the rules of the former IEEE 802.1X card (`PolicyRules.ValidateIssuing`);
  Server certificate adds `Server` (`PolicyServers`: dot1xServer, estServer). One Server certificate policy per server, and
  it keeps its server (decision of stage 4). The 802.1X server's policy allows ECC only ("The 802.1X server uses an ECC key.";
  decision of stage 4: its cipher suites follow an ECDSA key). Rows: "OADM Root CA SERVER01, 365 days, ECC" (the CA's real
  name, as Allow CA rows), a server certificate adds the server.
- **Deleting is refused** for a system policy (the first of HTTPS or IEEE 802.1X certificate among them: "OADM needs this
  policy. Change its values instead."; decision 2026-10-25), an issuing policy an EST profile names ("Used by the EST profile https."; the EST core
  plugin hands the store `ProfileUses`, read from its settings, also while the server is off), and every Server certificate
  policy ("The EST server gets its certificate from this policy. Change it instead.").
- **Conversion** (owner `oadm.pki:issuing`, apart from stage 3's `oadm.pki`, so a server of stage 3 converts too;
  `PkiService.ConvertIssuing`): "HTTPS" and "IEEE 802.1X" with the validity of the Device certificates card and the 802.1X
  settings (an imported RADIUS CA that cannot be read stays "imported" without a certificate: the task refuses until it is
  imported again, never a silent switch to the OADM CA); "802.1X server" (365 days, ECC) and "EST server" (90 days, ECC: as
  they were made before; decision of stage 4, the table had 365 for both). The key: ECC on a new server, **RSA where an
  OADM ran before** (a stored CA or page settings), because the tasks made RSA 2048 keys on the devices (decision of stage
  4: what the devices get does not change with the update). EST profiles (owner `oadm.est:profiles`, `EstPolicies.ConvertAsync`
  at the EST core plugin's start): a profile without a policy names one with its validity and key, the default when the
  values match ("https" -> "HTTPS"), else one named after the profile ("EST 802.1X"; the names "EST https" and "EST dot1x" of earlier builds are renamed
  once on start, `PkiService.RenameEstPoliciesAsync`; an IEEE 802.1X one takes the 802.1X
  settings of the first of its kind). A run after a crash between the two writes finds the policy by its values. The old
  settings stay where they are, no longer read; the page method `saveSettings` is gone.
- **Tasks**: Enable HTTPS and Enable IEEE 802.1X take the policy of the payload (`IssuePayload`: policy id and the values of
  the run; without a policy id the first of the kind; a policy deleted after the dialog: "The chosen policy no longer exists.
  Start the task again. Nothing was changed."), check the values like a saved policy and the key types of the device's default
  keystore (`GET cert/v1`: `settings.keystore`, `keystores[].key_types`) in Check compatibility, before the first write. ECC
  is EC-P256 on the device, RSA is RSA-2048; a device that does not list its key types gets RSA only (decision of stage 4).
  Renew certificates and Install certificates (IEEE 802.1X) always ask for the IEEE 802.1X certificate policy in their
  dialog (decision 2026-10-25: the first one could rewrite a camera's EAP identity, EAPOL version or RADIUS server CA that
  another policy set): Renew's payload `RenewPayload` (`Dot1xPolicyId`, `ClockConfirmed`), Install's `InstallPayload.PolicyId`,
  resolved with `IssuingChoice.Resolve`; a chosen policy deleted after the dialog fails with "The chosen policy no longer
  exists. Start the task again. Nothing was changed." before the first write (Renew: in Check compatibility, before HTTPS
  too). Without a policy id (older clients) the first of the kind, as before. HTTPS of Renew keeps the first HTTPS
  certificate policy. The preset is the first policy: PKI does not keep which policy issued a certificate
  (`IssuedCertificate` has no policy id).
- **Dialogs** (`IssueCertificateWindow`, `IssueCertificateViewModel`, no card, one form; also Renew certificates with the
  field "IEEE 802.1X policy", the 802.1X warning and the button Renew): Policy (the shared field `IssuingPolicyField` /
  `IssuingPolicyFieldViewModel`, also in Install certificates below Use for when it is IEEE 802.1X; the first preselected,
  read once through the task's read-only query `listIssuingPolicies` with the first selected device, `ui:LoadingOverlay`
  while it reads, Cancel usable, the main button disabled until a policy is chosen) with the policy's summary sentence
  below it, and for IEEE 802.1X its warning. Superseded 2026-10-25: the fields CA, Valid (days), Key, EAPOL version, EAP
  identity, Custom identity and RADIUS server CA of stage 4 are gone; the payload carries only the policy id (and the clock
  answer), the run uses the policy's stored values. The clock check follows as before. The 802.1X confirmation popup is gone (its warning is
  in the dialog).
- **EST**: a profile has `PolicyId`; enrollment and renewal issue with the policy's valid days, Set up asks the camera for
  the policy's key. A profile whose policy cannot be read issues nothing (503 "The issuing policy of this profile cannot be
  read.", Set up stops before the first write). Without PKI's store (no PKI: no CA either) the profile's own values count.
  The page: Profiles with "Issuing policy" (the policies of the use's kind, HTTPS certificate for HTTPS and RTSPS, IEEE
  802.1X certificate for IEEE 802.1X and MQTT; decision of stage 4) instead of Valid (days) and Key; it follows changes of
  the policies.
- **Server certificates**: the 802.1X server (`ServerIdentityKeeper.Policy`) and the EST server (`ServerCertificates.Policy`)
  took the days and key of their policy for the next certificate and renewed it before the end. Superseded 2026-10-25 by
  "Server certificates": the policy picks a certificate of the list, nothing renews it.
- **Page**: the Device certificates and IEEE 802.1X cards are gone (no Save on the page); the editor has the issuing fields
  (RADIUS server CA: since 2026-10-25 a CA of the list, no Import and View; see "RADIUS server CA"). Used by of the CAs lists the issuing policies since
  stage 5.
- **Tests**: `Oadm.Plugins.Pki.Tests` `IssuingPolicyTests` (fixtures `pki-config-4.1.json`, `pki-config-4.1-imported-radius.json`,
  `pki-policies-stage3.json`), `IssuingTaskTests`, `Dot1xPolicyChoiceTaskTests` (Renew and Install with the chosen,
  a deleted and no IEEE 802.1X policy), `IssueDialogTests` (+ headless dialogs, Renew and Install for IEEE 802.1X, editors);
  `Oadm.Plugins.Est.Tests` `PolicyTests`, `ServerTests` and `PageTests` (profile policies, server certificate);
  `Oadm.Plugins.Radius.Tests` `ServerTests` (server certificate days).

## Code (stage 5)

Built 2026-10-24. Rules the sections above leave open are marked "decision of stage 5" (the most conservative choice that
fits the spec; to be confirmed by the maintainer).

- **One list** (`PkiService.CertificateList`, page method `getCertificates`): first the CAs (each certificate of the CA card's
  CAs: the OADM CA, the previous ones, every Axis device ID root and intermediate, the imported CAs), then the certificates of
  the 802.1X and EST servers, then the ones created, signed or imported here (newest first), then the device certificates
  (newest first, the issued registry). Decision of stage 5: this fixed order makes paging one count plus one registry pass.
  The rows before the device certificates are at most a few hundred and built per request. Measured at 30,000 devices x 2
  plus 50 created: under 110 ms per page, search or filter (`CertificateListPerfTests`).
- **Columns** (SDK 0.4.0 `IssuedCertificateInfo` gains `Id`, `Kind` (`ListedCertificateKind`: Device, Authority, Server,
  Created, SignedRequest, Imported), `Name`, `IssuedBy`, `UsedBy`, `HasKey`; uses `ListedCertificateUses`: HTTPS, IEEE
  802.1X, MQTT, RTSPS, Server, CA, Other). Decisions of stage 5: **Issued by** is the issuing CA's name (the column and filter
  of Task / EST are gone from the page; the shared list still offers them when a server lists no CAs); the filter lists the CAs
  of the CA card and "Other CA". MQTT and RTSPS stay uses of their own; the state Replaced stays (gone from the list since 2026-10-25, see "Current
  and pending"). The columns Valid from and
  Serial number left the grid (the search still finds a serial; View shows both). **Used by**: a device certificate names
  its device while it is the newest of its use and not deleted from the device; server certificates their server; the rest
  "-". Server and local rows count Expires soon with the longest Expiry warning policy (`DefaultExpiryDays`).
- **Server certificates**: noted when the OADM CA signs a request without a device and a server use
  (`PkiCertificateAuthority.SignAsync`; source "EST" = EST server, `ServerCertificateSources.Dot1x` = 802.1X server) and when
  the 802.1X server reads its stored certificate at its start. In memory only (the servers keep their keys; decision of stage
  5). No PKCS#12 export of server certificates (not built: the EST server keeps its key in memory and its code was not
  changed in this stage).
- **Create** (`createCertificate`): use HTTPS or IEEE 802.1X, an issuing policy of that kind (its days and key: ECC P-256 or
  RSA 2048), name (1 to 64 characters), addresses (host names and IP addresses, at most 20; at least one for HTTPS); the
  dialog then downloads the .p12 with the password (at least 8 characters, like the CA backup). Server certificate policies
  are no choice (decision of stage 5). A name or address like an Axis camera's (`axis-<12 hex>`, any domain, any case:
  `CertificateRules.IsCameraName`, the guard of the device flows) is refused below its field ("This name belongs to an Axis
  camera."): EST and the 802.1X server take a camera's serial from such a name.
- **Sign request** (`signRequest`): PKCS#10 PEM or DER up to 1 MB, its signature checked, SHA-1 / MD5 refused, RSA 2048+ or
  EC P-256 / P-384 / P-521, and the key kind of the chosen policy (decision of stage 5: an RSA request with an ECC policy is
  refused). Names: the request's CN and its host names and IP addresses (plain host names only, never a camera's
  `axis-<12 hex>` name); an HTTPS policy needs one. Nothing else of the request's subject is copied (the certificate's
  subject is its CN only), so no serial number attribute reaches the certificate.
- **Never a device's certificate**: a certificate of the Certificates list (created, signed or imported) is never a camera's,
  whatever it names. `PkiCertificateAuthority.PurposeOf` answers `NotDevicePurpose` for its serial (checked before the issued
  registry, a hash lookup in `LocalCertificates.HasSerial`); EST then finds no camera in it (401 "The camera's certificate is
  not a valid Axis device ID or an EST certificate.") and the 802.1X server refuses it (alert 42, "Certificate is not a
  device certificate"). Fail closed: also when such a certificate was found on a device and listed in the registry.
- **Import** (`importCertificate`): one PEM or DER certificate, or a PKCS#12 file with exactly one certificate with its key
  (RSA or ECC; the chain in it is not kept). Refused (decision of stage 5): CA certificates ("Use Import > CA certificate"), expired ones,
  a PEM with a private key, several certificates in one PEM, one already listed. A wrong password shows below Password only
  for a PKCS#12 file (its outer structure read without the password, `LooksLikePkcs12`); any other file "The file cannot be
  read." below File. Use from the extended key usage: server ->
  HTTPS, client -> IEEE 802.1X, else Other.
- **Export** (download `downloadCertificate`, audited with "Export certificate <name> (<format>)"): PEM or DER of the
  certificate only (no chain); PKCS#12 only of certificates created or imported with a key, with the issuing CA, AES-256,
  100,000 iterations. Never a CA key or a device's key.
- **Delete** (`deleteCertificates`): all or nothing, the first refusal says why ("The certificate of 10.0.0.48 is still
  valid. Revoke it first. Nothing was deleted."). Device certificates and created and signed ones (the OADM CA issued them:
  deleted while valid, they would still be accepted without a trace) only revoked or expired; CAs never ("Remove CAs in
  Certificate authorities."); server certificates only ended; imported ones always. A revocation stays.
- **Revoke** also takes created and signed certificates (the OADM CA issued them; decision of stage 5).
- **Limits** (decision of stage 5): at most 1,000 certificates created, signed or imported; files up to 1 MB. The upload of
  a file goes through `InvokeAsync` as base64 (core plugins have no upload path), within the server's 16 MB message limit.
- **Who**: every action Admin with "Manage the certificate authority" and all devices, audited by the host; a user limited to
  a device group sees only the device certificates of the group's devices and no actions.
- **Page**: Create, Sign request and Import in the card header; row buttons View, Export (PEM, DER, With its key), Revoke,
  Delete; the row menu adds Revoke and Delete for several rows and Revoke all of this device. Dialogs without cards:
  `CreateCertificateWindow`, `SignRequestWindow`, `ImportCertificateWindow`, `ExportKeyWindow`.
- **CA list**: Used by also names the issuing policies of the OADM CA, one prefix per group ("802.1X: OADM CA (only devices
  OADM manages)", "Issuing: HTTPS, IEEE 802.1X, 802.1X server, EST server", then the services); the grid wraps to two lines.
- **Tests**: `Oadm.Plugins.Pki.Tests` `CertificateListTests`, `CertificateListPerfTests` (Category=Perf),
  `CertificateCardHeadlessTests`, `BackupTests` (a created key restored on another server), `CertificatesTabTests`,
  `AuthorityTests`.

## Code (stage 6)

Built 2026-10-24. Rules the sections above leave open are marked "decision of stage 6" (the most conservative choice that
fits the spec; to be confirmed by the maintainer).

- **Kinds** (`PolicyKinds.Enrollment`, `SignInCa`, group EST, the filter chip EST): Enrollment has devices (All devices
  too: every device OADM manages, EST never serves another) and `ProfileScope` (`PolicyProfiles`: all, chosen, none) with
  `Profiles` (names of the EST profiles, case-insensitive; "Choose at least one profile."); Sign-in CA has `CaId` and
  `SignInFor` (`PolicySignIn`: new, renewal), no devices. The CAs a Sign-in CA can name (`PolicyStore.SignInCaChoices`): the
  Axis device ID, the OADM CA and each previous OADM CA (decision of stage 6: never an imported CA, EST reads a serial only
  from the Axis device ID or the axis name the OADM CA put in; "Cameras sign in with the Axis device ID or an OADM CA.");
  the same CA twice is refused ("Another Sign-in CA policy has this CA."). A chosen profile the EST server does not have is
  refused on save ("The EST server has no profile web."; the EST core plugin hands the store `ProfileNames`, also while
  the server is off). Rows: "All profiles", "Profiles https and dot1x", "No enrollment"; "Axis device ID, for new
  certificates", "OADM Root CA SERVER01, for renewals".
- **For new certificates renews too** (decision of stage 6): a Sign-in CA for new certificates lets cameras sign in for
  `simpleenroll` and `simplereenroll` (as before, a renewal with the Axis device ID passes); one for renewals only
  `simplereenroll`. The converted defaults keep today's behaviour except that a certificate of the OADM CA no longer gets
  a new certificate by `simpleenroll` (it was accepted for both before; "for renewals" says it).
- **Overlaps**: as the other kinds with devices. Of two Enrollment policies that hold a device later (a tag or site
  change) a profile must be allowed by both (decision of stage 6: the safer one); the warning names the narrower policy
  when one is within the other. Tags of Enrollment policies match only while the host protects them (like VLAN: an
  Enrollment gives something; `PolicyKinds.GrantsByTag`).
- **EST server** (`Serving/EstRules`, built from the store and again when its version moves or PKI started again): a
  device no Enrollment policy holds: 403 "No enrollment policy for this device." (listed, event Enrollment refused,
  connection closed); a profile a policy that holds the device does not allow (or "No enrollment"): the 404 of an unknown
  profile, listed with "The enrollment policy PTZ does not allow the profile https." / "... allows no enrollment."
  (checked after the device and site, before the revocation list). Sign-in CAs come only from the policies: without
  PKI's store, with a store that cannot be read or without a Sign-in CA policy nothing signs in (401, fail closed; decision
  of stage 6: also without the store, as the 802.1X server). EST reports the CAs of the policies PKI still holds plus the
  OADM CA to PKI (`SetUsedBy`) at the start, on a policy change and on PKI's Changed. `cacerts` stays open to every
  profile that exists (no device is known then).
- **Set up** checks the policies in Check compatibility, before the first write: none holds the camera: "No enrollment
  policy for this device. Add one on the PKI page. Nothing was changed."; a profile not allowed is skipped with the
  policy's reason as a warning; none left: "The enrollment policies allow none of the profiles the camera supports.
  Nothing was changed." (decision of stage 6).
- **Page**: the "Sign in with" check boxes are gone; General shows "3 policies apply" with Edit policies (`plugin:oadm.pki`)
  and, in the warning style, why no camera can enroll ("Cameras cannot sign in: there is no Sign-in CA policy.", "No
  camera can enroll: there is no Enrollment policy.", the store's own texts). Users limited to a device group see neither
  (`EstState.PoliciesApply` and `PolicyProblem` null). `config.signInWith` stays stored and is kept by a save (read only
  by the conversion). The policy editor: Enrollment with Profiles (All profiles, Chosen profiles with a check box per EST
  profile, also one a policy names that the server no longer has, No enrollment) and the device choice; Sign-in CA with
  CA and For (hint "Cameras may renew with it too." for new certificates).
- **Conversion** (owner `oadm.est`, `EstPolicies.ConvertRulesAsync`, at the EST core plugin's start after the profiles'):
  the Enrollment policy "All devices" and one Sign-in CA policy per CA of `config.signInWith` (4.1 had none: the Axis
  device ID and the OADM CA), named "Axis device ID" (new), "OADM CA" (renewals) or the previous CA's name (renewals); a
  previous CA PKI no longer lists is not converted (it let nothing in). A conversion after EST policies were made merges:
  no second policy of a CA (for renewals when either was, the stricter), no "All devices" when an Enrollment policy exists
  (decision of stage 6: it would let every device in). EST settings that cannot be read convert nothing (tried again at
  the next start; fail closed meanwhile). A renamed or removed EST profile is not followed into the policies (decision of
  stage 6: the policy keeps the name, which then allows nothing; the editor shows it to uncheck).
- **Tests**: `Oadm.Plugins.Est.Tests` `EnrollmentPolicyTests` (kinds, defaults, overlaps, rules, conversion with fixtures
  `est-config-4.1.json` and `est-config-stage4.json`, the late merge), `ServerTests` (sign-in per operation and CA, fail
  closed, refused and not allowed devices and profiles over TLS, Used by), `TaskTests` (Set up and the policies), `PageTests`
  (the page line, Edit policies, headless at 1,700 and 1,000 px with and without a Sign-in CA policy); `Oadm.Plugins.Pki.Tests`
  `EstPolicyPageTests` (filter, rows, both editors, headless editors).

## Code (stage 7)

Built 2026-10-24 (branch `feature/pki-tabs`). Rules the sections above leave open are marked "decision of stage 7".

- **Tabs** (`docs/spec/plugins.md` "Pages shown as tabs"): `RadiusPage.TabOf` and `EstPage.TabOf` = `oadm.pki`; the host
  (`MainWindowViewModel.TabPages`) creates their views with their own contexts and hands them to PKI's context
  (`Tabs`, `TabsChanged`); `OpenAsync("plugin:oadm.radius" / "plugin:oadm.est")` and a role's start page open PKI on the tab
  (`OpenTabRequested`), `plugin:oadm.pki` on Certificates. `PkiViewModel.Tabs` (`PkiViewModel.Tabs.cs`): Certificates, then
  802.1X server, EST server, others in the host's order; only the shown tab's view is in the window (a server tab reads its
  state when it opens, decision of stage 7). The server parts keep `HasPage`, so permissions and start page choices stay.
  The page's subtitle: "Certificates, their policies, and the 802.1X and EST servers." (decision of stage 7).
- **List** (`IssuedCertificateList`, `IssuedCertificateListViewModel`, SDK 0.4.0): `IssuedCertificateInfo` gains `IssuerId`,
  `KeyText`, `AuthorityKind`, `AuthorityId`, `IsRootCa`, `UsedByBadges`; `IssuedCertificateQuery.GroupBy`
  (`IssuedCertificateGrouping`), `IssuedCertificateGroups`, `IssuedCertificateGroup`; the page's `MenuOf` / `ActionsOf` /
  `SelectionActions` (`IssuedCertificateAction`), `MoreStatesOf` (`IssuedCertificateStatus`), `Toolbar` and `Banner`. The
  `RowActions` and `RowContextMenu` properties of stage 5 are gone (one menu). Server: `PkiService.GetCertificates` orders
  the parts by the grouping; by issuer one pass over the issued registry per OADM CA (current, previous, Other CA), the
  other CAs issue no device certificates. Measured at 30,000 devices x 2: first page by type 55 ms, by issuer 72 ms (also
  at offset 59,500), the page's list of 500 grouped rows 59 ms (`CertificateListPerfTests`).
- **CA rows**: Remove of a previous CA is `removePrevious`, of an imported one `removeAuthority` (`PkiViewModel.RemoveCa`);
  Export of the OADM CA's row is `exportPublic` (as before), of other CA rows the row's download (PEM, DER; audited
  "Export certificate"; decision of stage 7: a previous CA now also as DER). The OADM CA row shows both trust states
  always ("Not installed on the server" neutral), decision of stage 7.
- **Group by** is kept with `ICorePluginClientContext.SetLocalSetting("certificates.groupBy")`.
- **Theme**: `Border.badge.neutral` (`Oadm.BadgeNeutralBrush`, both appearances), `MenuItem.danger`.
- **Tests**: `Oadm.Client.Tests` `PluginTabTests`; `Oadm.Plugins.Pki.Tests` `PkiTabsTests` (+ headless
  `pki-tab-certificates-1700.png` / `-1000.png`), `CertificateGroupTests`, `PolicySentenceTests`, `CertificateListPerfTests`
  (Category=Perf); `Oadm.Plugins.Radius.Tests` `PkiTabHeadlessTests` (`pki-tab-802-1x-*.png`); `Oadm.Plugins.Est.Tests`
  `PageTests.The_est_server_is_a_tab_of_the_pki_page` (`pki-tab-est-*.png`).
- **Not built**: sorting by a column (the list never sorted; its order is the server's).

## Several CAs and the policy cleanup (decisions 2026-10-25)

Built 2026-10-25 (branch `feature/pki-several-cas`). Rules the decisions leave open are marked "decision of this stage".
It supersedes "only the OADM CA issues" of stage 4 and the Server certificate and Revoke kinds of stages 3 and 4.

- **Store**: plugin setting `importedCas` (`StoredImportedCa`): the certificate, `chainPem` (an imported intermediate's
  issuers kept at import) and `keyProtected` (`ISecretProtector.Protect(PKCS#8 PEM, "pki:ca:<id>")`, as the OADM CA's
  key). Read at the start (`PkiService.ReadImported`); a key the master key cannot read: listed, cannot issue, logged
  once. The server backup carries the setting (and the master key), so a restore keeps the imported keys; Back up of the
  OADM CA stays the OADM CA only.
- **Import**: `import` (`CaImporter`, the former replace path; the checks as before, also "valid a year beyond the
  certificates it would issue") adds; the same CA imported before without its key, or with a key the master key cannot
  read (fix of 2026-10-25), gets the key (decision of this stage);
  the OADM CA, a previous one or a CA with its key already listed: "This CA is already in the list."; at most 50 imported
  CAs. `importAuthority` adds a certificate only, as before. A CA backup of another OADM server imported here is such a CA
  (it no longer becomes this server's OADM CA). `ImportRequest.Confirmed` is gone.
- **Issuing**: `CertificateAuthorityInfo.Issues` is true for the OADM CA and each imported CA with its key; its
  `Certificates` hold the chain. `PkiService.IssuingCa(id)` / `IssuingCas()`; `PolicyStore.IssuingCaChoices()` (the
  editor's Issued by, `PolicyList.IssuingCas`; the OADM CA also while it is made) and the store's check on save
  (`PolicyTexts.CannotIssue`, also for the Axis device ID and previous OADM CAs). Signing paths: the tasks
  (`CertificateDeployment.RequireCa(service, policy)`, Renew per use), Create and Sign request, the CA service
  (`DeviceCertificateRequest.IssuerId`, SDK 0.4.0: EST profiles through `ProfileIssuing.CaId`, the EST server's and the
  802.1X server's own certificates through their policy's `CaId` and `Ca/IssuerChains`). The certificate goes out with the
  issuing CA's chain (task CA installs, EST answers, `cacerts`, the servers' TLS chains). The validity is cut to the
  issuing CA's end.
- **RADIUS server CA** of the IEEE 802.1X certificate policy (decision 2026-10-25, supersedes "OADM 802.1X server" and the
  import in the policy): a CA of the list (`PkiPolicy.RadiusCaId`, the choices of the Certificate authorities card, the
  OADM CA first and by default); a CA the cameras need for another RADIUS server is imported on the Certificates page
  (Import, CA certificate). The hint below the field names the CA that issued the 802.1X server policy's certificate
  ("The 802.1X server's certificate is issued by OADM Root CA."). Enable IEEE 802.1X installs that CA and the
  certificates above it PKI knows; the issue dialog shows its name. Such a CA counts as used (Used in, cannot be removed).
  The tasks never compare it with the 802.1X server's certificate (decision 2026-10-25): Enable, Renew and Install
  certificates (IEEE 802.1X) and EST's Set up always install the policy's RADIUS server CA; turning 802.1X off on the switch
  first is the user's job. After New OADM CA the server keeps its certificate of the previous CA; the replace question says so
  ("The 802.1X server and the EST server keep their certificate from the current CA. Choose a certificate of the new CA
  in their policies before cameras get the new CA.", `PreviewReplaceReply.Servers`).
  The summary adds "Cameras check the RADIUS server with OADM Root CA.". Converted once on start (owner
  `oadm.pki:radius-ca-2026-10-25`, never on a list that cannot be read): "OADM 802.1X server" becomes the CA that issued
  the 802.1X server's certificate (else the OADM CA); a CA imported into the policy goes into the CA list (or the listed
  CA with its fingerprint) and is named; one that cannot be read stays without a CA (logged; the form asks for one). The
  conversion runs at the end of the start, once the OADM CA, the previous and the imported CAs are loaded (it matches
  against them, and an import saves the whole list of imported CAs). The Axis device ID entry never matches: an Axis root
  or intermediate becomes an imported CA of its own, so the camera trusts exactly that CA, never every Axis root and
  intermediate; `importAuthority` accepts a CA that is only inside the Axis device ID entry for the same reason.
  `RadiusCa` and `RadiusCaPem` are read only by this conversion; the page method `importRadiusCa` is gone.
- **Trust**: the server's trust anchors and the renewal issuers (`ITrustAnchors.SetRenewalIssuers`) take every imported CA
  with its key (anchors with its chain), so EST renewals of its certificates pin without "Certificate changed". The
  802.1X server keeps PKI's lists (purpose, revocation) for every CA OADM issues with (`TrustedIssuer.KeepsLists`); an
  imported CA without its key keeps none (its certificates come from elsewhere). The certificate list counts device
  certificates under an imported CA that issues (Group by issuer, Issued by filter).
- **EST sign-in** (decision 5, checked): the existing rules allow only the Axis device ID and OADM CAs (current and
  previous) as EST sign-in CAs (`PolicyStore.SignInCaChoices`); they stay. EST reads a camera's serial from a name in the
  certificate only where OADM vouches for that name: an imported CA's key may be used outside OADM. Renewals (maintainer
  decision 2026-10-25): a camera also signs in with its **current** certificate when OADM issued exactly that one, whatever
  CA of PKI issued it; the camera comes from the store entry, the certificate must match the stored DER, pending and
  revoked ones do not count (`Ca/ICurrentCertificates`, `PkiCertificateAuthority.CurrentCameraAsync`;
  docs/spec/est-server.md "Renewal with the current certificate"). Certificates of the same CA issued outside OADM stay refused.
- **Removal**: `removeAuthority` and `removePrevious` are refused while a policy names the CA (`PolicyStore.NamingCa`:
  issuing, 802.1X trusted CA, EST sign-in CA; a previous OADM CA only when a policy names it itself).
- **Kinds**: `PolicyKinds` Dot1xServer (with `CaId`, `ValidDays`, `Key`; ECC only), EstServer (new), AllowCa ("802.1X
  trusted CA"), Block (`AlsoRevoke`), SignInCa ("EST sign-in CA"); `LegacyServerCertificate` and `LegacyRevoke` only for
  the conversion (`PolicyKinds.IsKnown` refuses them). A server policy without its certificate's values reads the kind's
  defaults (`PolicyRules.Normalize`). The 802.1X tab counts 802.1X server, 802.1X trusted CA, VLAN and Block; the EST tab
  EST server, Enrollment and EST sign-in CA ("4 policies applied" on a new server).
- **Page**: Import menu "CA certificate" (`ImportAuthorityWindow`, "Import CA certificate") and "CA with its key"
  (`ImportCaWindow`, "Import CA with its key"); the editor's Issued by, Block's check box (Revoke row label), no For of
  server certificates; the CA list's Used in; group headers only for groups with rows (`PkiView.PolicyGroupView`, no fixed
  group keys; the table gave way to the cards, see "Policies as a list").
- **Tests**: `Oadm.Plugins.Pki.Tests` `SeveralCasTests` (imports, key encryption and master key, the current certificate
  that names its camera for an EST renewal, signing by issuer with
  its chain and in the list, a used CA kept, previous CA named by an EST sign-in CA, the kind conversion and its start,
  Used in, no empty group headers), `ServiceTests`, `TaskTests` (an imported intermediate through the IEEE 802.1X
  policy), `PageTests` (import adds, headless), `PolicyPageTests` (+ `pki-policy-block.png`), `IssueDialogTests`
  (+ `pki-policy-est-server.png`, `pki-policy-dot1x-server.png`); `Oadm.Plugins.Est.Tests` `ServerTests` (a profile and the
  EST server policy on an imported CA, `cacerts`, a renewal with the current certificate of an imported CA: accepted;
  the same CA's certificate issued elsewhere and one no longer current: refused); `Oadm.Plugins.Radius.Tests` `AllowCaTests` (revocation of an issuing
  imported CA), `ConversionTests`, `PolicyTests`.
- **For the maintainer**: the CA backup no longer restores as this server's OADM CA (it is added as a CA that can issue);
  the "Can issue" badge shows on imported CAs only (the OADM CA always can); an EST or 802.1X server policy whose CA can no longer issue leaves
  that server without a certificate (fail closed).

## Policies as a list (decisions 2026-10-25)

Built 2026-10-25 in two steps. Branch `feature/pki-policy-sections`: cards by area, Check a device, and the servers'
status when the CA of their policy cannot issue. Branch `feature/pki-policy-list`: the cards and Check a device replaced
by one grouped table and a side panel, like Intune (see "Policies").

- **Server status**: see "Server certificates" (the status of a server without a usable certificate).
- **Gone**: the grouped table (`PkiView.PolicyGroupView`), the filter (`PolicyFilterViewModel`), the cards
  (`PolicySettingsView`, `PolicyRulesView`, `PolicySectionViewModel`), the policy dialog (`PolicyFormView.CreateWindow`),
  Check a device (`PolicyMethods.CheckDevice`, `PolicyCheck*`, `PkiService.PolicyCheck.cs`). The policy store and the
  other methods are unchanged (`PolicyGroups` stays for the server tabs' counts).
- **Tests**: `PolicyPageTests`, `EstPolicyPageTests`, `IssueDialogTests`, `PolicyTabHeadlessTests` (`pki-policies-*.png`:
  the table with the side panel), Perf `PolicyPerfTests` (the table with 150 policies at 30,000 devices).

## Server certificates (decisions 2026-10-25)

The 802.1X server and the EST server show one certificate each, picked by their server policy from the Certificates list,
like any TLS server: the server certificate names the server, the cameras' own certificates (mTLS) come from the issuing
policies and are checked against the trusted / sign-in CA policies, the two are independent. Supersedes "Issued by, valid
(days), key, renewed 30 days before the end" of the server policies (stage 4 and "Several CAs").

- **Certificate** (`PkiPolicy.CertificateId`, the id of a row of the Certificates list): a certificate with a key OADM holds
  that a TLS server can use (no CA, server authentication or no key usage limit, not ended, not revoked); the 802.1X server
  only one with an ECC key (its cipher suites follow it). The choices (`PolicyList.ServerCertificates`,
  `PolicyCertificateChoice`, "EST server (OADM CA), until 2035-10-25") come from `PkiService.ServerCertificateChoices`; the
  store checks the pick on save (`PolicyStore.CertificateRefusal`). The field shows the names the certificate holds below it.
  A certificate a server policy shows cannot be deleted or revoked ("Used by the policy 802.1X server. Pick another
  certificate first.", fix of 2026-10-25) and shows the server under Used by. The choices are cached per list, day and
  change of the revocation list.
- **No Create in the policy** (decision 2026-10-25): other certificates come from the Certificates tab (Create, Sign
  request, Import); the field lists them.
- **One certificate for both servers** (decision 2026-10-25): when OADM PRO becomes active, on every start with OADM PRO
  and on Turn on, a server policy without a certificate gets the shared one (`PkiService.EnsureServerCertificatesAsync`, a
  system write `PolicyStore.SetServerCertificateAsync`, only where none is set): "802.1X and EST server", from the OADM CA,
  ECDSA P-256, 10 years (never past the CA's end), server authentication, kept in the Certificates list with its key
  (created, use Server), naming this computer's addresses and name, the names the running servers give and, while the EST
  server runs, the site nodes' addresses. Exception: the 802.1X server's stored certificate of before is imported with its
  key for its policy (cameras keep working). Nothing while the OADM CA cannot issue (tried again at the next start, minute
  or connection). Start order (fix of 2026-10-25): PKI's own start and license change make it only once both server
  plugins started (`PolicyStore.ServerPluginStarted`, also while their servers are off; the EST plugin hands the site
  nodes' addresses from its stored `sites` then) and neither server is turned on: a server that is on makes it at its
  own start with its names, the 802.1X server after it took over its kept certificate (`PkiPlugin.MakesServerCertificates`).
- **No automatic renewal**: nothing makes a server certificate again. The Expiry warning and the server's status remind;
  the user picks another one in the policy.
- **The servers** load the picked certificate with its key (`PkiCertificateAuthority.ServerCertificate`;
  `ServerIdentityKeeper` for 802.1X, `Est/Serving/ServerCertificates` for EST) and again when the policies change. EST
  shows the one certificate everywhere, also at sites (no certificates per site); its tab shows no certificate row (its policy does) but warns
  when a site's node address or one of this server's addresses is not in it ("Site Parking is not in the certificate of
  the EST server. Create a certificate with its address on the Certificates tab and choose it in the EST server
  policy."). EST's TLS chain, and the CA certificates Set up gives the cameras, are the issuer chain of the picked
  certificate (`PkiService.ServerIssuerChain`; `cacerts` is the profile's issuing CA's chain); the IEEE 802.1X policy
  names its RADIUS server CA itself (see "RADIUS server CA").
- **Status** of a server without a usable certificate: "The 802.1X server has no certificate. Choose one in its policy.",
  or "The EST server cannot use its certificate: it has ended." (also gone, revoked, its key cannot be read).

## Older AXIS OS (decisions 2026-10-25)

Certificates, HTTPS and IEEE 802.1X run on **AXIS OS 6.53 and later** (researched on hardware on 6.53, 8.40, 9.80, 10.12,
11.9, 11.11 and 12.11; the recorded answers, sanitized, in `tests/Oadm.Plugins.Pki.Tests/Fixtures/Legacy/<version>/`). EST
stays at 12.10 (`docs/spec/est-server.md`). One decision per device, `Tasks/PkiCompatibility` (`Decide`, `Dot1xApiOf`),
made fresh in Check compatibility before the first write; the menus use the cached firmware version only (6.53 or later).

| | Certificates | HTTPS web server | IEEE 802.1X on/off, identity, EAPOL | 802.1X certificates | Keys | EAPOL |
|---|---|---|---|---|---|---|
| 12.11 and later (network settings 1.38) | REST cert v1 | SOAP, Admin policy | network settings | network settings | keystore's types (ECC, RSA) | 1-3 |
| 11.11.135 to 12.10 | REST cert v1 | SOAP, Admin policy | network settings | SOAP certificate set | keystore's types (11.11: RSA) | 1-3 |
| 10.1 to 11.11.134 | SOAP | SOAP, Admin policy | network settings | SOAP certificate set | RSA 2048 | 1-3 |
| 8.50 to 10.0 (network settings listed) | SOAP | SOAP, Admin policy | network settings | SOAP certificate set | RSA 2048 | 1-2 |
| 7.10 to 8.40 (no API list) | SOAP | SOAP, Admin policy | parameters | SOAP certificate set | RSA 2048 | 1-2 |
| 6.53 to 7.0 | SOAP | SOAP, Admin + Operator + Viewer | parameters | SOAP certificate set | RSA 2048 | 1-2 |

- **Certificate API**: REST `cert` v1 only when `config/discover` lists it **released** (11.11.135 and later; beta on 11.9
  is not used); else the SOAP certificate service at `/vapix/services` (`Device/CertSoapApi`): `tds:GetCertificates`,
  `GetCACertificates`, `LoadCertificates`, `LoadCACertificates`, `DeleteCertificates` (certificates and CA certificates,
  DER in base64), `acertificates:CreateCertificate2`, `GetPkcs10Request2`, `LoadPkcs12`, `GetCertSet` / `SetCertSet`.
  `config/discover` answering 404, 400, 405 or 501 means not listed. The AXIS OS version for the SOAP path comes fresh from
  `basicdeviceinfo.cgi`, else `Properties.Firmware.Version`; an unknown or unreadable version, or one before 6.53, refuses
  ("The device's AXIS OS version cannot be read. Nothing was changed." / "Needs AXIS OS 6.53 or later. Nothing was
  changed."): never "unknown = allowed" for writes.
- **Keys on SOAP**: `CreateCertificate2` takes no key type (10.12 refuses one with `ter:TagMismatch`): RSA 2048 only, an
  ECC policy fails in Check compatibility with the existing "The device cannot create an ECC key. Choose a policy with
  RSA. Nothing was changed." (6.53 also refuses an ECC PKCS#12). The key is created under the placeholder
  "&lt;alias&gt; key" (a self-signed certificate), the request comes from it, the signed certificate is loaded under the
  alias (the device finds the key; the placeholder's own id is refused as existing) and the placeholder is deleted after
  that (the key stays; a failed delete is a Warning log line). A failure before the load deletes the placeholder again.
- **802.1X certificates**: network settings before 1.38 (9.80 1.6, 10.12 1.15, 11.9 1.29, 11.11.232 1.33; 12.10 too)
  answer success to `certClient` / `certsCA` but do not bind them and never report them in `getNetworkInfo`. OADM uses
  network settings alone where `getNetworkInfo` names them (`Wired8021X.ReportsCertificates`; 12.11 lists 1.37 but
  reports them) or the list says 1.38 or later; else the certificates go through `SetCertSet EAPTLS_WIRED` (client
  certificate, CA certificates, trusted ones as read) **before** 802.1X is switched on, and the read-back of Verify 802.1X
  settings takes them from `GetCertSet` (`Device/Dot1xSettings`). Also for Disable IEEE 802.1X (certificates stay bound),
  Renew, Install certificates for 802.1X, View / Delete certificates (in use by 802.1X) and the EST step "Set the RADIUS
  server CA" (only the CA certificates of the set change).
- **Without network settings** (no API list before 8.50, or no `network-settings` in it): on/off, identity and EAPOL
  version through `param.cgi action=update` `Network.Interface.I0.dot1x.Enabled`, `.EAPOLVersion`, `.EAPTLS.Identity`
  (form body), read with `param.cgi list`; the certificates through the certificate set as above. Host names for the
  HTTPS certificate are then not read (Read network settings Skipped, the OADM address and `axis-<serial>.local`).
- **HTTPS before 7.10**: `SetWebServerTlsConfiguration` needs Operator and Viewer next to Admin (6.53: Admin alone is
  `ter:InvalidArgs` "occurrence violation"): OADM sends all three when the device reported them or the version is before
  7.10; Enable switches each HTTP-only role to HTTP and HTTPS, Disable sets all to HTTP.
- **EAPOL version 3** needs AXIS OS 10.1 (release notes: IEEE 802.1X-2010; 6.53 breaks with it, 9.80 refuses it with
  error 4004): an IEEE 802.1X policy with EAPOL 3 fails Check compatibility below 10.1 with "EAPOL version 3 needs AXIS
  OS 10.1 or later. Choose a policy with EAPOL version 1 or 2. Nothing was changed." (Renew: the purpose is Skipped with
  that reason). The default policy of a new installation uses EAPOL 2 (decision 2026-10-25); its ECC key still needs AXIS OS 12, so older devices need a second policy with RSA.
- Reads in the background (the one pass for certificates from before the list, the device reader of Revoke) take the
  API from the cached `config/discover` and the device's cached AXIS OS version (`PkiCompatibility.CachedAsync`).
- Tests: `LegacyDeviceApiTests` (every parser and request body against the recorded answers and requests of each version),
  `CompatibilityMatrixTests` (the decision), `LegacyTaskTests` (every task per family against `FakeCamera.Legacy(version)`:
  SOAP certificate service, certificate set, parameters, the roles of 6.53, the refusals with no write).
- Hardware (2026-10-25, `LegacyHardwareWriteTests`, category HardwareWrite, one test per device, the device's state saved
  first and restored and compared at the end): Enable HTTPS, Renew and Disable HTTPS through OADM's tasks on 6.53, 8.40,
  9.80, 10.12 and 11.9 (the web server presents the new certificate in a TLS handshake, the previous one is deleted, 6.53
  takes all three roles); Enable, Renew and Disable IEEE 802.1X with EAPOL 2 and 3 on 10.12 and 11.9 (bound with
  `SetCertSet`, read back from `GetCertSet`); on 6.53, 8.40 and 9.80 everything up to switching 802.1X on (the binding and
  Set the RADIUS server CA; 802.1X stayed off on these remote devices). ECC and EAPOL 3 below 10.1 were refused with no
  write. 6.53 has neither `basicdeviceinfo.cgi` nor the API list (version from the parameter), 8.40 has
  `basicdeviceinfo.cgi` only. AXIS OS 9.80 refuses connections for a few seconds after a connection policy change (the
  web server restarts). Not verified yet: the REST path of 11.11.135 to 12.10 with the certificate set (the 11.11.232
  device refused the login); the network settings between 8.50 and 9.70 (assumed: getNetworkInfo when listed; 9.80 lists
  1.6 and it works).

## Web server restart and Basic login (decisions 2026-10-25)

- **Verify waits for the web server restart.** After the web server changes, Verify HTTPS (Enable HTTPS, Renew's HTTPS
  part, Install certificates for HTTPS) and Verify (Disable HTTPS) wait at most 60 s (`CertificateDeployment.SwitchTimeout`,
  a try every 3 s on the PKI clock, no fixed sleeps) with the step text "Waiting for the web server to restart". HTTPS: the
  new certificate in the handshake shows the restart happened. HTTP: AXIS OS 9.80 answers right after the change and
  refuses connections a few seconds later while its web server restarts (hardware, 2026-10-25), so the device must keep
  answering for 10 s (`PkiOptions.WebServerSettle`, a parameter read every 3 s; a refused read starts the wait again).
- A device that does not come back fails the step with what was changed and how OADM connects: "The device did not come
  back with the new certificate within 1 minute. The web server was switched to the certificate "OADM HTTPS ...". OADM
  still connects as before.", "The device did not answer over HTTP within 1 minute. The device was set to HTTP only; its
  certificates stay. OADM still connects as before.", or, when it answered once and then stopped, "The device stopped
  answering when its web server restarted and did not come back within 1 minute. ... OADM connects over HTTP."
- **Disable HTTPS on a device with Basic login only** (`Network.HTTP.AuthenticationPolicy=basic`): runs when the server
  setting **Allow Basic login over HTTP** is on (`IVapixClient.AllowsBasicOverHttp`: the user chose Basic over HTTP; Check
  compatibility says "Basic login over HTTP allowed"); with it off it refuses before any write: "The device accepts only
  Basic login over HTTP. To disable HTTPS on it, turn on Allow Basic login over HTTP in Settings > Security. Nothing was
  changed." (`HttpsDisableTask.BasicOnlyRefusal`).
- Tests: `WebServerRestartTests` (the fake camera refuses requests after a web server change, `FakeCamera.RestartRefusals`;
  the clock moves only while the task waits, `TaskHarness.RunWaitingAsync`), `HttpsTaskTests` (Basic both ways).

## Delete and install safety (fixes of 2026-10-25)

- **Delete certificates** deletes nothing while what uses the certificates is unknown: a failed read of the web server
  settings or of the 802.1X settings (REST and SOAP) fails "Read certificates" with "What uses the certificates cannot be
  read (...). Nothing was changed." (a hidden 802.1X client certificate or RADIUS server CA would take the camera off the
  network). The dialog's query then marks every certificate `UseUnknown`: In use "Unknown", not selectable ("What uses it
  cannot be read: it cannot be deleted."). A device without 802.1X (a read that says so) is not unknown.
- **SOAP answers**: an HTTP error is never a success; a body with a SOAP fault throws with the device's reason, any other
  non-2xx answer with the HTTP status (`DeviceSoap.SendAsync`).
- **CA certificate aliases**: every install of CA certificates (Install CA certificates, the CA steps of Enable, Renew and
  Install certificates) takes a free alias when another CA certificate holds "OADM CA &lt;8 hex&gt;" already
  ("&lt;alias&gt;-2", `CertificateDeployment.FreeAlias`).
- **Install certificates, CA certificates only**: a file with a certificate that is not a CA certificate is refused, in
  the dialog's row ("Cannot be used") and again in "Read certificate file" ("&lt;file&gt;: &lt;name&gt; is not a CA
  certificate. Choose a file with CA certificates only. Nothing was changed.", `CertificateFiles.NotCaProblem`).
- **Install certificates, keys over HTTP** (maintainer decision 2026-10-25, the CodeQL alert "sensitive data
  transmission" is used as intended): HTTPS and IEEE 802.1X send each file with its private key and password to its
  device. When OADM reaches some of the devices that get a file over HTTP (`IDeviceInfo.ConnectsOverHttps` false, from the
  device's connection scheme), the dialog shows one line ("2 of the 3 devices that get a file are reached over HTTP: their
  private keys cross the network unencrypted.") and the check box **Send the keys anyway**; Install stays disabled until
  it is ticked ("Tick Send the keys anyway."). Never one question per device; counted once over the files (O(files));
  nothing extra without an HTTP device or for CA certificates only. The task runs anyway and logs a Warning line per
  device it sent a key to over HTTP ("cam48.pfx was sent with its private key over HTTP: unencrypted on the network.",
  never the password or the key).
- Tests: `TaskSafetyFixesTests` (REST and `FakeCamera.Legacy`), `DeviceSoapErrorTests`, `StoredCaUnreadableTests`,
  `AuthorityTests` (Used by), `KeysOverHttpTests` (none, some, all over HTTP; the task's warning).

## Not planned

- A limit of devices per switch port (decision 2026-10-24): OADM sees no logoff without RADIUS accounting; set the limit
  on the switch. (The 802.1X server only caps its open sign-in sessions at 8 per switch port, fix of 2026-10-25,
  `docs/spec/radius-server.md`.)
- Exporting the CA key on its own.

## Stages

1. Done (on main, 2026-10-24): the CA list with Used by, 802.1X Trusted CAs and policies, EST Sign in with, the clock
   check.
2. Done (on main, 2026-10-24): One plugin: EST and 802.1X move into the PKI package (three pages, Turn
   on per server, PRO for the two servers only, taking over the 4.1 data and choices, the site relay), without changing what
   they do.
3. Done (on main, 2026-10-24): The page with CAs, Policies and Certificates (today's list); 802.1X
   server, Allow CA, VLAN, Block, Revoke and Expiry warning policies (the expiry events move to PKI) with the conversion; the
   802.1X and EST page changes above. See "Code (stage 3)".
4. Done (on main, 2026-10-24): Issuing policies: HTTPS, IEEE 802.1X and Server certificate; the
   task dialogs pick a policy; EST profiles point to one. See "Code (stage 4)".
5. Done (on main, 2026-10-24): The Certificates list: server paging, Create, Sign request, Import, Export, Delete.
   See "Code (stage 5)".
6. Done (on main, 2026-10-24): EST Enrollment and Sign-in CA policies; the EST page's Sign in with goes away. See
   "Code (stage 6)".
7. Done (on main, 2026-10-24): One rail entry: the 802.1X and EST servers become tabs of the PKI page;
   the Certificate authorities card goes away, the Certificates list becomes the full management view (badges, Group by,
   the CA actions). See "The PKI page", "Certificates" and "Code (stage 7)".
