# PKI, the certificate control center

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
The plugin's details: `docs/spec/bundled-plugins.md` (PKI) and `plugins/Oadm.Plugins.Pki/README.md`.

**Release (decision 2026-10-24):** all stages below ship together in **OADM 4.5** (with device groups and the 802.1X
server; there is no 4.2 release). Main may hold a stage in between; nothing is released before stage 6.

**Goal (decisions 2026-10-24):** PKI is the one place for everything about certificates. One page with three parts:
the **CAs**, the **policies** (every rule: which certificate a device gets, what a certificate lets it do) and the
**certificates** (every certificate OADM knows). Services that use certificates (802.1X server, EST server, the PKI tasks)
keep no certificate rules of their own: they apply PKI's policies and show only how they run (server state, switches,
sessions, logs, enrollments).

## One plugin: PKI with the 802.1X and EST servers

Decision 2026-10-24: the EST server (`oadm.est`) and the 802.1X server (`oadm.radius`) become part of the PKI plugin
package (`oadm.pki`). No contract between plugins is needed any more: one settings store, one backup, one set of
permissions, one policy list in PKI's own code.

- **One package, three core plugins, three rail pages:** PKI, 802.1X server, EST server (a package may hold several core
  plugins, `docs/spec/plugins.md`). One folder `plugins/Oadm.Plugins.Pki` (+ `.Client`); the EST and 802.1X code keeps its
  own folders inside it (`Est/`, `Radius/`) with its source-available `LICENSE` (`docs/spec/licensing.md`); the rest of PKI
  stays Apache-2.0. THIRD-PARTY-NOTICES and the plugin's license notice name both.
- **On and off:** PKI is always on (as today). The 802.1X and EST servers are off by default and switched on their own
  page ("Turn on" in the Server card, the stored choice kept like a plugin's on/off). They open their ports (UDP 1812,
  TCP 9454) only while on.
- **OADM PRO:** the 802.1X and EST servers start only with an active OADM PRO license (`PluginRegistry.ProActive`); without
  it their page shows `ProLicenseTexts.NeedsPro` and the Turn on button is disabled; a license that runs out stops them,
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

One page, no tabs, cards like the 802.1X page:

- Top row: **Certificate authorities** | **Policies** (side by side on wide screens, stacked on small ones).
- Below, full width: **Certificates** (search and filters, like the 802.1X Log).
- The current CA card's actions (Generate new CA, Import CA, Back up, Export public certificate, Install in trusted root
  store) move into the Certificate authorities card. The Previous certificate authorities card, Device certificates card
  and IEEE 802.1X card go away (their content becomes rows and policies, see "Converting existing settings").
- Managing needs "Manage the certificate authority" and all devices (like Revoke today). Users without it see the page
  read-only; users limited to a device group do not see the page.

## Certificate authorities

| Kind | Example | Issues certificates | Source |
|---|---|---|---|
| OADM CA | "OADM CA" | yes (the current one) | generated or imported, as today |
| Previous OADM CA | "OADM CA 2025" | no (kept while its certificates live) | a replaced OADM CA |
| Axis device ID | "Axis device ID" | no | built in (the RSA and ECC roots and intermediates EST ships, `plugins/Shared/DeviceId`) |
| Imported CA | "Contoso Root" | no | **Import CA** (a root or intermediate certificate, PEM or DER; no key) |

- Each row: name, kind, valid until, **Used by** (the policies that name it, e.g. "802.1X: OADM CA (only devices OADM manages)");
  **View** (`ui:CertificateWindow`), **Remove** (imported CAs only; refused while a policy names it: "Used by the policy
  <name>. Change or delete the policy first.").
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
| Issuing | **Server certificate** | CA, valid (days), key, for (802.1X server, EST server) | no | "802.1X server", "EST server" (OADM CA, 365 days, renewed 30 days before the end, as today) |
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
devices with two certificates each stay fast (today's list stops at 5,000).

- Columns: name or device, use (HTTPS, IEEE 802.1X, Server, CA, Other), issued by, valid until, **used by** (the device it
  is installed on, the 802.1X server, the EST server, "-"), state (Valid, Expires soon, Revoked, Expired).
- Includes the CAs, the device certificates the OADM CA issued (tasks and EST), OADM's server certificates and the
  certificates created, signed or imported here.
- Actions (toolbar of the card; row buttons for the row ones):
  - **Create**: OADM makes the key and the certificate from an issuing policy (name, addresses, use); download as
    PKCS#12 with a password. The key is kept, encrypted with the master key like the CA key, and is part of the backup.
  - **Sign request**: upload a request (PKCS#10, PEM or DER) from another system; its key never leaves it.
  - **Import**: a certificate (PEM, DER) or a PKCS#12 with its password (the key is kept like a created one).
  - **Export**: PEM, DER; PKCS#12 only where OADM holds the key (created, imported with key, server certificates), with
    a password. Device certificates have no key in OADM (the camera made it). Every export with a key is audited.
  - **View** (`ui:CertificateWindow`), **Revoke** (as today), **Delete** (only certificates nothing uses; revoked or
    expired ones, or created ones no longer needed).

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

- **802.1X server:** Server (Listen on, Port, Log entries kept; no Advanced dialog: its settings are the 802.1X server
  policy), Switches, Sessions and Log (a splitter between
  the two), a line "Rules: Policies on the PKI page". No Trusted CAs card, no Policies card, no Default VLAN.
- **EST server:** General, Profiles (name, issuing policy, on), Enrollments in its own card.

## Rules for the move (decisions 2026-10-24)

- The 802.1X page shows "4 policies apply" with **Edit in PKI**; Sessions and Log rows name the policy that matched
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
  Policies: the filter as a segmented switch (All, Certificates, 802.1X, Revoke), rows with Edit and Delete icon buttons,
  the warning as an amber chip in the summary. The editor is the shared `DevicePickerWindow` with the form on top; for kinds
  without devices the device part hides (`DevicePickerWindow.IsPickerVisible`). The Device certificates card keeps only the
  validity; users limited to a device group still see the page with the Certificates of their devices (decision of stage 3:
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
4. Issuing policies: HTTPS, IEEE 802.1X and Server certificate; the task dialogs pick a policy; EST profiles point to
   one.
5. The Certificates list: server paging, Create, Sign request, Import, Export, Delete.
6. EST Enrollment and Sign-in CA policies.
