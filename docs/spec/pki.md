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

- Each row: name, kind, valid until, **Used by** (the policies that name it, e.g. "802.1X: OADM CA, managed only");
  **View** (`ui:CertificateWindow`), **Remove** (imported CAs only; refused while a policy names it: "Used by the policy
  <name>. Change or delete the policy first.").
- The CA key is never exported on its own; it leaves OADM only in the encrypted CA backup (Back up) and the server
  backup. The trusted root store of the server stays as it is (what OADM trusts when it connects to devices).
- A CA that leaves the list (expired previous CA) drops out of every policy at once (fail closed).

## Policies

One list, grouped by what the policies do, with a filter: **All**, **Issuing**, **802.1X**, **EST**, **Revoke**. Each row: name, kind, a short summary
("OADM CA, 365 days, ECC", "VLAN 30", "Only devices OADM manages"), devices (where the kind has them); Edit and Delete as
row buttons (like the Permissions page), **Add policy** in the card header. The editor is one dialog: name, kind, the
kind's fields (each kind its own form, e.g. the 802.1X server policy: Minimum TLS version, Cipher suites with
Reset to default, Largest EAP packet), the shared device selector where the kind has devices.

| Group | Kind | Fields | Devices | Default policy (there from the start, editable, can be deleted) |
|---|---|---|---|---|
| Issuing | **HTTPS certificate** | CA (issuing CAs), valid (days), key (RSA, ECC), warn before expiry (days) | no | "HTTPS" (OADM CA, 365 days, ECC, warn 30 days before) |
| Issuing | **IEEE 802.1X certificate** | CA, valid (days), key, warn before expiry (days), EAPOL version, EAP identity, RADIUS server CA | no | "IEEE 802.1X" (OADM CA, 365 days, ECC, the identity and server CA as today) |
| Issuing | **Server certificate** | CA, valid (days), key, warn before expiry (days), for (802.1X server, EST server) | no | "802.1X server", "EST server" (OADM CA, 365 days, renewed 30 days before the end, as today) |
| 802.1X | **802.1X server** (one only) | Minimum TLS version (1.2, 1.3), cipher suites, largest EAP packet (bytes), reauthentication (Switch decides, or every N minutes: sent to the switch as the session timeout) | no | "802.1X server" (TLS 1.2, the default cipher suites, 1,020 bytes as RFC 3748 3.1, Switch decides) |
| 802.1X | **Allow CA** | CA, Only devices OADM manages (on) or any device with a valid certificate of it | no | "OADM CA" (with its previous CAs) and "Axis device ID", both only managed |
| 802.1X | **VLAN** | VLAN ID (1..4094) | yes | none |
| 802.1X | **Block** | - | yes | none |
| EST | **Enrollment** | profiles the devices may use (all, or chosen ones), or no enrollment | yes | "All devices" (every device OADM manages, all profiles) |
| EST | **Sign-in CA** | CA, for new certificates or for renewals | no | "Axis device ID" (new), "OADM CA" (renewals) |
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

- PKI's Device certificates card (validity, warn before expiry) and IEEE 802.1X card (EAPOL version, EAP identity, RADIUS server CA): the
  default issuing policies "HTTPS" and "IEEE 802.1X" with these values.
- 802.1X Advanced (minimum TLS version, cipher suites, largest EAP packet): the 802.1X server policy.
- 802.1X Trusted CAs: one Allow CA policy per ticked CA, with its "Only devices OADM manages".
- 802.1X policies: VLAN, Block and Revoke policies with the same names, devices and order. A Default VLAN of OADM 4.1
  or of a development build becomes a last VLAN policy "All devices" (this server plus every site), so no device changes its VLAN.
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
3. The page with CAs, Policies and Certificates (today's list); 802.1X server, Allow CA, VLAN, Block and Revoke policies
   with the conversion; the 802.1X and EST page changes above.
4. Issuing policies: HTTPS, IEEE 802.1X and Server certificate; the task dialogs pick a policy; EST profiles point to
   one.
5. The Certificates list: server paging, Create, Sign request, Import, Export, Delete.
6. EST Enrollment and Sign-in CA policies.
