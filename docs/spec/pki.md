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
  tab ("Turn on" in the Server card, the stored choice kept like a plugin's on/off). They open their ports (UDP 1812,
  TCP 9454) only while on.
- **OADM PRO:** the 802.1X and EST servers start only with an active OADM PRO license (`PluginRegistry.ProActive`); without
  it their tab shows `ProLicenseTexts.NeedsPro` and the Turn on button is disabled; a license that runs out stops them,
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
**Certificates** | **802.1X server** | **EST server**.

- **Certificates** (PKI's own): the **Policies** card on top, full width; the **Certificates** card below it, full width:
  every certificate OADM knows, the CAs among them, with all their actions (see "Certificates"). There is no Certificate
  authorities card any more.
- **802.1X server** and **EST server**: the pages of the core plugins `oadm.radius` and `oadm.est`, unchanged in content
  (Turn on / Turn off, the OADM PRO texts, their cards, permissions, events, tasks and audit names). They have no rail
  entry: their client pages say `ICorePluginPage.TabOf` = `oadm.pki` and the host hands their views to the PKI page
  (`docs/spec/plugins.md` "Pages shown as tabs"). The server parts keep their pages (`HasPage`), so the "show" and
  "manage" permissions stay as they are.
- A server's tab shows only for users whose role has that core plugin's "show" permission (the server lists only the
  pages the role may show, the host follows role changes). With no server tab the tab row is left out.
- **Edit in PKI** on a server tab opens the Certificates tab. Everything that opens `plugin:oadm.radius` or
  `plugin:oadm.est` (`OpenAsync`, a role's start page, links of the Plugins page) opens the PKI page on that tab;
  `plugin:oadm.pki` opens it on Certificates. A click on the rail entry keeps the tab that was open.
- Only the open tab's view is in the window: a server tab reads its state when it is opened (like a page).
- Decision of stage 7: a role that shows a server but not PKI gets that server's page in the rail as before (there is no
  PKI page to hold the tab).
- Managing needs "Manage the certificate authority" and all devices (like Revoke today). Users without it see the page
  read only (no toolbar buttons, no row buttons but View); users limited to a device group see only the device
  certificates of their devices (no Policies card, no actions).

## Certificate authorities

| Kind | Example | Issues certificates | Source |
|---|---|---|---|
| OADM CA | "OADM CA" | yes (the current one) | generated or imported, as today |
| Previous OADM CA | "OADM CA 2025" | no (kept while its certificates live) | a replaced OADM CA |
| Axis device ID | "Axis device ID" | no | built in (the RSA and ECC roots and intermediates EST ships, `plugins/Shared/DeviceId`) |
| Imported CA | "Contoso Root" | no | **Import CA** (a root or intermediate certificate, PEM or DER; no key) |

- The CAs are rows of the Certificates list (stage 7; before, a card of their own): name, kind, valid until, **Used by**
  (the policies that name it, e.g. "802.1X: OADM CA (only devices OADM manages)"); **View** (`ui:CertificateWindow`),
  **Remove** (imported and previous CAs; refused while a policy names it: "Used by the policy <name>. Change or delete the
  policy first.").
- The CA key is never exported on its own; it leaves OADM only in the encrypted CA backup (Back up) and the server
  backup. The trusted root store of the server stays as it is (what OADM trusts when it connects to devices).
- A CA that leaves the list (expired previous CA) drops out of every policy at once (fail closed).

## Policies

One list, grouped by what the policies do, with a filter: **All**, **Issuing**, **Certificates**, **802.1X**, **EST**, **Revoke**. Each row: name, kind, a short summary
("OADM CA, 365 days, ECC", "VLAN 30", "Only devices OADM manages"), devices (where the kind has them); Edit and Delete as
row buttons (like the Permissions page), **Add policy** in the card header. The editor is one dialog: name, kind, the
kind's fields (each kind its own form, e.g. the 802.1X server policy: Minimum TLS version, Cipher suites with
Reset to default, Largest EAP packet), the shared device selector where the kind has devices.

| Group | Kind | Fields | Devices | Default policy (there from the start, editable, can be deleted) |
|---|---|---|---|---|
| Issuing | **HTTPS certificate** | CA (issuing CAs), valid (days), key (RSA, ECC) | no | "HTTPS" (OADM CA, 365 days, ECC) |
| Issuing | **IEEE 802.1X certificate** | CA, valid (days), key, EAPOL version, EAP identity, RADIUS server CA | no | "IEEE 802.1X" (OADM CA, 365 days, ECC, the identity and server CA as today) |
| Issuing | **Server certificate** | CA, valid (days), key, for (802.1X server, EST server) | no | "802.1X server" (OADM CA, 365 days, ECC) and "EST server" (OADM CA, 90 days, ECC), renewed 30 days before the end, as today |
| 802.1X | **802.1X server** (one only) | Minimum TLS version (1.2, 1.3), cipher suites, largest EAP packet (bytes), reauthentication (Switch decides, or every N minutes: sent to the switch as the session timeout) | no | "802.1X server" (TLS 1.2, the default cipher suites, 1,020 bytes as RFC 3748 3.1, Switch decides) |
| 802.1X | **Allow CA** | CA, Only devices OADM manages (on) or any device with a valid certificate of it | no | "OADM CA" (with its previous CAs) and "Axis device ID", both only managed |
| 802.1X | **VLAN** | VLAN ID (1..4094) | yes | none |
| 802.1X | **Block** | - | yes | none |
| EST | **Enrollment** | profiles the devices may use (all, or chosen ones), or no enrollment | yes | "All devices" (every device OADM manages, all profiles) |
| EST | **Sign-in CA** | CA, for new certificates or for renewals | no | "Axis device ID" (new), "OADM CA" (renewals) |
| Certificates | **Expiry warning** | days before the end | yes | "All devices" (30 days) |
| Revoke | **Revoke** | - | devices and sites only, never tags | none |

- **Issuing policies have no devices.** The task dialogs (Enable HTTPS, Enable IEEE 802.1X) let the user pick the issuing
  policy of their kind; the first one is preselected and its values are the default, which the user may change for this
  run (decision 2026-10-24). EST issues by profile: an EST profile names its issuing policy (the profile name stays what
  the camera uses in its URL). Users add more issuing policies, e.g. "HTTPS 2 years".
- **No overlaps** (decision 2026-10-24): saving is refused when a device is in two policies of the same kind
  ("10.0.0.48 is in Building B and PTZ."), checked against the devices of now; the same CA in two Allow CA or two Sign-in
  CA policies is refused too. When a tag or site change creates an overlap later, both policies show a warning in their
  row and the safer one applies: Block over VLAN; of two VLAN policies, the older one.
- A device that no Enrollment policy matches is refused by the EST server ("No enrollment policy for this device"),
  as a device OADM does not manage is today.
- A device that no VLAN policy matches gets no VLAN from the server (the switch port keeps its own). There is no default
  VLAN.
- **All devices** (decision 2026-10-24): the device selector offers "All devices" next to "Chosen devices" (every kind with
  devices except Revoke). It holds every device, also those of sites added later and, for the 802.1X kinds, devices OADM
  does not manage (a certificate of an Allow CA for any device: an "All devices" VLAN gives its VLAN, an "All devices"
  Block refuses it). It overlaps every other policy of its kind: saving is refused as for any overlap ("All devices includes
  every device, also those of PTZ."); converted data keeps the overlap with its warning.
- **Expiry warning** (decision 2026-10-24) applies to every device certificate, whoever issued it (the HTTPS certificate
  polling reads, as today). The events **Certificate expires soon** and **Certificate expired** move from the built-in
  events (fixed 30 days, `docs/spec/event-rules.md`) to PKI's event group "Certificates" and use the policy's days; they
  keep their ids and their Any device twins, so stored event rules keep working. The device grid's "Certificate
  expires" chip and the Compliance check's default days follow the policy too (a Compliance check with its own days keeps
  them).
- **Revoke** works as today on the 802.1X page: on save it revokes the certificates of its devices, then every new
  certificate of them while it exists (no tags: a tag could revoke every device that gets it later, without undo).
- Every change is audited; the 802.1X and EST servers apply a change at once (fail closed while a change is applied).
- The policy kinds live in PKI's code (no SDK): one store, one editor, the 802.1X and EST servers read it in process; a
  server never stores rules of its own. Kinds of a server that is off stay listed and editable (they apply once it runs).

## Certificates

Every certificate OADM knows, paged on the server (search over every column, filters: use, issued by, state), so 30,000
devices with two certificates each stay fast. The full management view of PKI (stage 7): still a table (the shared
`ui:IssuedCertificateList`, virtualized, pages of 500), with the CAs and their actions in it.

- Includes the CAs, the device certificates the OADM CA issued (tasks and EST), OADM's server certificates and the
  certificates created, signed or imported here.
- **Columns:** **Name** with a second line where it helps (the key and the issuer: "RSA 4096 · issued by OADM Root CA
  SERVER01", a device certificate "Issued by OADM Root CA SERVER01", a root CA its key only); **Type** as badges (neutral
  pills, `Border.badge.neutral`): Root CA or Intermediate CA plus OADM CA, Previous OADM CA, Axis device ID or Imported for
  a CA; HTTPS, IEEE 802.1X, MQTT, RTSPS or Server; Created, Signed or Imported for the certificates made here; **Valid
  until**; **Used by** (the device it is installed on, the 802.1X server, the EST server, for a CA the policies and services
  that name it, "-"); **State** (`ui:StatusChip`, statuses are never badges: "Valid for 245 days", "Expires soon" with the
  days left as tooltip, "Expired", "Revoked" with the revocation as tooltip, "Replaced"; the OADM CA row adds "Installed on
  the server" and "Installed on this computer" once it is in that trusted root store, "Failed on ..." with the reason, and
  the progress of an install); the row buttons.
- **Group by** (a select in the toolbar, decision 2026-10-24): **Type** (default): Certificate authorities, Server
  certificates, Created, signed and imported, Device certificates; **Issuer**: one group per CA in the order of the CAs
  (OADM CA, previous OADM CAs, Axis device ID, imported CAs, Other CA); **None**. The server orders and counts the groups
  (`CertificatesRequest.GroupBy`, `CertificatesReply.Groups`), so a page of 500 cuts across groups and each header names the
  group's count over every page ("Device certificates · 60,000"). The grid's group headers are the shared `DataGrid.groups`
  style (collapsible). The choice is kept on this computer.
- **Toolbar** (left of the filters): **Create** (menu: **Certificate**, **New OADM CA**), **Sign request**, **Import**
  (menu: **Certificate**, **CA** with **Own CA with its key** (replaces the OADM CA) and **CA certificate only** (adds a
  row)), then Group by, the filters and the search.
- **Row buttons** (destructive ones red):
  - OADM CA: **View**, **Export** (PEM, DER: the public certificate, as Export public certificate before), **Install**
    (in the trusted root store of the server and this computer, `oadm.pki:trustServer`), **Back up** (the CA with its key).
  - Previous OADM CA and imported CA: **View**, **Export** (PEM, DER), **Remove** (refused while a policy names it).
  - Axis device ID: **View**, **Export** (PEM, DER).
  - Every other row: **View**, **Export** (PEM, DER; **With its key** where OADM holds it), **Revoke**, **Delete**.
- No OADM CA, a CA being created, a key that cannot be read or a server that cannot keep a CA: the PKI status
  (`ui:StatusChip` with its detail) stands in the card above the list; nothing when the OADM CA is fine (its row says it).
- Actions:
  - **Create**: OADM makes the key and the certificate from an issuing policy (name, addresses, use); download as
    PKCS#12 with a password. The key is kept, encrypted with the master key like the CA key, and is part of the backup.
  - **Sign request**: upload a request (PKCS#10, PEM or DER) from another system; its key never leaves it.
  - **Import**: a certificate (PEM, DER) or a PKCS#12 with its password (the key is kept like a created one).
  - **Export**: PEM, DER; PKCS#12 only where OADM holds the key (created, imported with key, server certificates), with
    a password. Device certificates have no key in OADM (the camera made it). Every export with a key is audited.
  - **View** (`ui:CertificateWindow`), **Revoke** (as today), **Delete** (only certificates nothing uses; revoked or
    expired ones, or imported ones no longer needed).
  - Create and Sign request never name an Axis camera (`axis-<12 hex>`, any domain): only device certificates do.

## Converting existing settings

On the first start after the update, once, without asking:

- PKI's Device certificates card (validity; its warn before expiry becomes the Expiry warning policy "All devices") and
  IEEE 802.1X card (EAPOL version, EAP identity, RADIUS server CA): the
  default issuing policies "HTTPS" and "IEEE 802.1X" with these values.
- 802.1X Advanced (minimum TLS version, cipher suites, largest EAP packet): the 802.1X server policy.
- 802.1X Trusted CAs: one Allow CA policy per ticked CA, with its "Only devices OADM manages".
- 802.1X policies: VLAN, Block and Revoke policies with the same names, devices and order. A Default VLAN of OADM 4.1
  or of a development build becomes a last VLAN policy "All devices" (every device, also of sites added later and devices
  OADM does not manage), so no device changes its VLAN.
- EST: the Enrollment policy "All devices" (as today: every managed device, every profile). Sign in with: one Sign-in CA
  policy per ticked CA. EST profiles keep their names and point to an issuing policy
  with their validity and key ("https" → "HTTPS", or a new one when the values differ).
- Overlaps found while converting are kept and show their warning; nothing is dropped.

## Clock check before installing a certificate

Decision 2026-10-24: a camera whose clock is behind sees a new certificate as "not valid yet".

- Every certificate the OADM CA issues to a device starts **5 minutes in the past** (`NotBefore = now - 5 min`), which
  covers small drifts silently (PKI tasks and EST).
- The dialogs of **Enable HTTPS**, **Enable IEEE 802.1X** and EST's **Set up** read the camera's clock first (read-only
  query, `time.cgi` / the HTTP Date header as today). If the camera is still more than 5 seconds behind the
  certificate's start, they ask before starting: "The camera's time is 9 min 12 s behind. The certificate becomes valid on
  the camera only in 4 min 12 s. Set the camera to use NTP. Continue?" **Yes** / **No**. Several cameras: one question
  listing how many and the largest drift. The task gets the user's answer and does not ask again; a camera that is ahead
  is fine. The dialogs read at most the first 100 selected cameras (3 seconds each); the tasks check every camera.
- A run without the answer (no dialog) still refuses a camera behind the certificate's start ("Nothing was changed").

## Pages of the services after this

The two pages are tabs of the PKI page since stage 7 ("The PKI page").

- **802.1X server:** Server (Listen on, Port, Log entries kept; no Advanced dialog: its settings are the 802.1X server
  policy), Switches, Sessions and Log (a splitter between
  the two), a line "Rules: Policies on the PKI page". No Trusted CAs card, no Policies card, no Default VLAN.
- **EST server:** General (with "3 policies apply" and Edit in PKI), Profiles (name, issuing policy, on), Enrollments in
  its own card. No Sign in with (stage 6: Sign-in CA policies).

## Rules for the move (decisions 2026-10-24)

- The 802.1X tab shows "4 policies apply" with **Edit in PKI** (opens the Certificates tab); Sessions and Log rows name the policy that matched
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
  The default cipher suites (a new server, Reset to default, the fallback of a policy the server cannot use) have one
  source, `PolicyRules.DefaultSuites`: the suites listed for the minimum that the server certificate can use (never the
  ECDHE_RSA ones with its ECDSA key).
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
  CA in the header (Import CA a menu: "Own CA with its key" replaces the OADM CA, "CA certificate only" adds a row; decision
  of stage 3: one button, two entries), Install in trusted root store, Export public certificate and Back up below the list.
  Policies: the filter as a segmented switch (All, Certificates, 802.1X, Revoke; Issuing since stage 4), rows with Edit and Delete icon buttons,
  the warning as an amber chip in the summary. The editor is the shared `DevicePickerWindow` with the form on top; for kinds
  without devices the device part hides (`DevicePickerWindow.IsPickerVisible`). The Device certificates card kept only the
  validity (gone in stage 4); users limited to a device group still see the page with the Certificates of their devices (decision of stage 3:
  the Certificates list of stage 1 stays; the Policies card is hidden for them).
- **802.1X page**: Server (with "6 policies apply" and Edit in PKI: `ICorePluginClientContext.OpenAsync("plugin:oadm.pki")`)
  and Switches side by side from 1,000 px of page width, Sessions and Log below with a `paneSplitter` between them (pixel
  rows inside the page's scroll viewer); every column but the state or result is as wide as its longest text (`MinWidth` =
  `Width`, the grid scrolls sideways on narrow pages). The switch dialog no longer names the RADIUS server's address; the
  shared secret needs 8 characters or more.
- **EST page**: its own cards (`EstPage.HasOwnCards`): General and Profiles (side by side while they fit) with Save, then
  Enrollments with Not renewed.
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
- **Deleting is refused** for the last policy of HTTPS or IEEE 802.1X certificate ("Enable HTTPS needs an HTTPS certificate
  policy. Add another one first."), an issuing policy an EST profile names ("Used by the EST profile https."; the EST core
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
  values match ("https" -> "HTTPS"), else one named after the profile ("EST dot1x"; an IEEE 802.1X one takes the 802.1X
  settings of the first of its kind). A run after a crash between the two writes finds the policy by its values. The old
  settings stay where they are, no longer read; the page method `saveSettings` is gone.
- **Tasks**: Enable HTTPS and Enable IEEE 802.1X take the policy of the payload (`IssuePayload`: policy id and the values of
  the run; without a policy id the first of the kind; a policy deleted after the dialog: "The chosen policy no longer exists.
  Start the task again. Nothing was changed."), check the values like a saved policy and the key types of the device's default
  keystore (`GET cert/v1`: `settings.keystore`, `keystores[].key_types`) in Check compatibility, before the first write. ECC
  is EC-P256 on the device, RSA is RSA-2048; a device that does not list its key types gets RSA only (decision of stage 4).
  Renew certificates and Install certificates (IEEE 802.1X) take the first policy of each kind (decision of stage 4).
- **Dialogs** (`IssueCertificateWindow`, `IssueCertificateViewModel`, no card, one form): Policy (the first preselected,
  read once through the task's read-only query `listIssuingPolicies` with the first selected device, `ui:LoadingOverlay`
  while it reads), CA (the OADM CA's name, no choice today), Valid (days), Key, and for IEEE 802.1X its warning, EAPOL
  version, EAP identity, Custom identity and RADIUS server CA (the imported CA only when the policy has one). Enable gives
  the payload with every value; the clock check follows as before. The 802.1X confirmation popup is gone (its warning is
  in the dialog).
- **EST**: a profile has `PolicyId`; enrollment and renewal issue with the policy's valid days, Set up asks the camera for
  the policy's key. A profile whose policy cannot be read issues nothing (503 "The issuing policy of this profile cannot be
  read.", Set up stops before the first write). Without PKI's store (no PKI: no CA either) the profile's own values count.
  The page: Profiles with "Issuing policy" (the policies of the use's kind, HTTPS certificate for HTTPS and RTSPS, IEEE
  802.1X certificate for IEEE 802.1X and MQTT; decision of stage 4) instead of Valid (days) and Key; it follows changes of
  the policies.
- **Server certificates**: the 802.1X server (`ServerIdentityKeeper.Policy`) and the EST server (`ServerCertificates.Policy`)
  take the days and key of their policy for the next certificate; renewed 30 days before the end, at most a third of its time,
  at once for a new CA or (EST) another key; other days alone wait for the next renewal.
- **Page**: the Device certificates and IEEE 802.1X cards are gone (no Save on the page); the editor has the issuing fields
  (RADIUS server CA with Import and View owned by the editor window). Used by of the CAs lists the issuing policies since
  stage 5.
- **Tests**: `Oadm.Plugins.Pki.Tests` `IssuingPolicyTests` (fixtures `pki-config-4.1.json`, `pki-config-4.1-imported-radius.json`,
  `pki-policies-stage3.json`), `IssuingTaskTests`, `IssueDialogTests` (+ headless dialogs and editors);
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
  of the CA card and "Other CA". MQTT and RTSPS stay uses of their own; the state Replaced stays. The columns Valid from and
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
  (RSA or ECC; the chain in it is not kept). Refused (decision of stage 5): CA certificates ("Use Import CA"), expired ones,
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
- **Page**: the "Sign in with" check boxes are gone; General shows "3 policies apply" with Edit in PKI (`plugin:oadm.pki`)
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
  (the page line, Edit in PKI, headless at 1,700 and 1,000 px with and without a Sign-in CA policy); `Oadm.Plugins.Pki.Tests`
  `EstPolicyPageTests` (filter, rows, both editors, headless editors).

## Not planned

- A limit of devices per switch port (decision 2026-10-24): OADM sees no logoff without RADIUS accounting; set the limit
  on the switch.
- Exporting the CA key on its own.

## Stages

1. Done (on main, 2026-10-24): the CA list with Used by, 802.1X Trusted CAs and policies, EST Sign in with, the clock
   check.
2. Built (2026-10-24, branch `feature/pki-policies`): One plugin: EST and 802.1X move into the PKI package (three pages, Turn
   on per server, PRO for the two servers only, taking over the 4.1 data and choices, the site relay), without changing what
   they do.
3. Built (2026-10-24, branch `feature/pki-policies`): The page with CAs, Policies and Certificates (today's list); 802.1X
   server, Allow CA, VLAN, Block, Revoke and Expiry warning policies (the expiry events move to PKI) with the conversion; the
   802.1X and EST page changes above. See "Code (stage 3)".
4. Built (2026-10-24, branch `feature/pki-policies`): Issuing policies: HTTPS, IEEE 802.1X and Server certificate; the
   task dialogs pick a policy; EST profiles point to one. See "Code (stage 4)".
5. Built (2026-10-24, branch `pp-certs`): The Certificates list: server paging, Create, Sign request, Import, Export, Delete.
   See "Code (stage 5)".
6. Built (2026-10-24, branch `pp-est`): EST Enrollment and Sign-in CA policies; the EST page's Sign in with goes away. See
   "Code (stage 6)".
7. Built (2026-10-24, branch `feature/pki-tabs`): One rail entry: the 802.1X and EST servers become tabs of the PKI page;
   the Certificate authorities card goes away, the Certificates list becomes the full management view (badges, Group by,
   the CA actions). See "The PKI page", "Certificates" and "Code (stage 7)".
