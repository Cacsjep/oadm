# OADM PRO license

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

OADM stays fully open source. An OADM PRO license only tells PRO plugins and the PRO parts of PKI (the EST server and the
802.1X server, `docs/spec/pki.md` "One plugin") that PRO is active, which plan, how many devices and until when. **OADM
itself is never limited by it**: before, during and after a license nothing in OADM changes, only PRO plugins and the PRO
parts of PKI read it (PKI's own parts, CAs, issuing, Revoke and Certificates, never need a key).

## The license key

- The plugin license format (`docs/spec/plugins.md` "Plugin licenses", `Oadm.Sdk.Licensing.LicenseFile`), one line:
  `OADM1.<content>.<signature>`, both parts base64url without padding. A `.oadmlicense` file holds the same line; spaces
  and line breaks are ignored; at most 16 KB.
- `<content>`: the UTF-8 bytes of a JSON object, signed exactly as sent (no canonical form needed):
  `{"plugin":"oadm.pro","server":"<server ID>","customer":"ACME GmbH","issued":"2026-10-22","expires":"2028-10-22"|null,
  "plan":"Up to 500 devices","features":["trial"],"limits":{"devices":500}}`. `plugin` is always `oadm.pro`
  (`ProLicenseFile.ProductId`); `features` is only present for a trial key (`["trial"]`; the license service issued such
  keys until OADM started the trial itself, they stay valid); `limits.devices` only when the plan
  has a device count (none = no limit); `plan` is shown as written. Dates are `yyyy-MM-dd`; `expires` is the last valid
  day (server local date), null = never.
- `<signature>`: ECDSA P-256 with SHA-256 over the content bytes, in IEEE P1363 form (r and s, 32 bytes each, 64 bytes;
  not DER). Go: `ecdsa.Sign` and the two values padded to 32 bytes each. Built-in crypto on both sides.
- Reference signer: `ProLicenseFile.Sign` (tests and fake mode use it).

## Keys

- `src/Oadm.Core/Licensing/ProLicenseKeys.cs`: the public keys (base64 SubjectPublicKeyInfo) by id, oldest first (`k1`).
  A license is valid when any key verifies it, so a new key is added next to the old ones and old licenses keep working.
- The private keys are never in this repository. They live only with the license service that issues licenses.

## Checks (`ProLicenseFile.Check`, `Oadm.Core.Licensing.ProLicense`)

- The signature with any known key, `plugin` = `oadm.pro`, `server` = this server's ID (case-insensitive), `expires` not
  before today. The server ID is the one of plugin licenses (`Server.Id`, a GUID made at the first start, kept in the
  database, so it moves with a backup restore).
- States (`ProLicenseStatus`, texts in `ProLicenseTexts`): None "No license", Active ("Up to 500 devices, until 2028-10-22"
  next to the label OADM PRO, a trial "Trial, until 2026-11-21"; the result of Import and Remove sits next to the buttons), Expired "Expired on 2028-10-22." (the trial:
  "Trial ended on 2026-11-21."), WrongServer "This license is for another server.", Invalid "This is not a valid OADM PRO
  license." (unknown key, changed content, another product).
- Stored in the setting `License.Pro` (the key text; not secret), read at start step 4a after the plugin licenses, checked
  again hourly (`PluginLicenseRefreshHostedService`); only an Active key is accepted on import.
- Plugins: `ICorePluginContext.Pro` and `ITaskExecutionContext.Pro` (`IProLicense`: `Current` (`ProLicenseInfo`),
  `IsActive`, `Changed`), default interface members (null on hosts without). Test fakes: `FakeProLicense`.
- PRO plugins (`plugin.json` `"pro": true`) run only while the license is active: the host turns them on and off with it
  (`docs/spec/plugins.md` "OADM PRO plugins"). The PRO parts of PKI follow it themselves: the EST and 802.1X servers run
  only while turned on on their page and the license is active (`docs/spec/plugins.md` "PRO parts of PKI"). Everything else
  in OADM stays the same with or without a license.

## Trial

OADM PRO can be tried for 30 days without going online: **Start 30-day trial** in the app, once per server
(`ProLicense.StartTrialAsync`, decision 2026-10-25; the license service no longer issues trials).

- Administrator only, audited "Started trial". The first day (server local date) is recorded; the trial is Active
  (`IsTrial`, "Trial, until <day>") through `ProLicenseTexts.TrialDays` (30) days including the first, then Expired "Trial
  ended on <day>.". It starts only while no trial was recorded and PRO is not active; a second start is refused
  "The trial was already used on this server." (FAILED_PRECONDITION), a start while a license is active "OADM PRO is
  already active.". Nothing in the app resets it.
- Order: an active imported key always wins (the card shows its plan); without one a running trial; else the key's own
  problem (expired, other server); else the ended trial; else None. Remove license removes only the key, a running trial
  stays. `ProLicenseInfo.TrialAvailable` (SDK 0.4.0) = no trial recorded and PRO not active: the only case that offers
  the button.
- Stored twice: the setting `License.ProTrial` (`{"server","install","started"}`, in the database and so in backups) and
  the file `install.json` of the data folder (`{"id": <install ID, a GUID made at the first trial>, "proTrials": {<server
  ID>: <first day>}}`, never in a backup). A trial runs only where both name this installation, or where only the
  install file knows it (an older backup restored on the same computer: still used, the earlier first day wins). A backup
  restored on another computer brings the record of another installation: the trial counts as used and does not run
  there (Expired, "The trial was used on another computer."); a license key still works there. An unreadable record
  counts as used. The trial follows the server ID like a license, so a new server (new ID) can try PRO once.
- Starting raises `IProLicense.Changed` at once (PRO plugins turn on with the host's `PluginActivation.FollowPro`, the
  EST and 802.1X servers start when turned on and publish their switch state), and the hourly check ends it the same day.
- Where: Settings > License (the button, secondary, with "Try OADM PRO free for 30 days. Once per server."; others see
  "An administrator can start the trial.") and the off state of the EST and 802.1X servers while they need OADM PRO (the
  button primary, the one main action there; `ServerSwitchState.TrialAvailable`). Plugin pages start it through
  `ICorePluginClientContext.StartProTrialAsync` and check `IsAdministrator` (SDK 0.4.0 default members; older hosts:
  NotSupportedException / false); the host routes it through the License card's view model, so the card, the plugin list
  and the task catalog follow (`LicenseViewModel.LicenseChanged`).

## gRPC and client

- `LicenseService` (`license.proto`): `GetLicense` (every signed-in user: server ID, status, reason, customer, plan,
  max_devices, expires, trial, trial_available, has_key), `ImportLicense(license)` (Admin, audited "Imported license",
  INVALID_ARGUMENT with the reason, nothing stored), `RemoveLicense` (Admin, audited "Removed license" when a key was
  imported), `StartTrial` (Admin, audited "Started trial", FAILED_PRECONDITION with the reason).
- Settings page, card **License** (right column below Backup and restore; every user, read only for operators):
  Server ID with copy, "OADM PRO" with the state (status chip: ok active, warning trial, error expired / trial ended /
  other server / not valid; "Licensed to ..." below), **Start 30-day trial** (see "Trial"), **Import license** (file
  picker `.oadmlicense`) and **Remove license** (red, confirmed, only while a key is imported) for administrators, the
  link **Activate license** (unless a bought license is active) that opens `https://oadm.app/activate/?server=<id>`.
- Issuing: the page `oadm.app/activate/` (`docs/spec/packaging.md` "Activation page") sends the server ID with a bought
  license code, and the support forum returns the signed key as a `.oadmlicense` file (a code works for one server; the
  same server can fetch it again). The forum's own rules are documented in its repository; it issues no trials.
- Fake mode: keys signed with `FakeOadmApi.SampleLicenseKeys` for `FakeOadmApi.FakeServerId`; Start 30-day trial runs
  once like on a server.
- Tests: `tests/Oadm.Core.Tests/Licensing/ProLicenseTests` and `ProTrialTests`, `tests/Oadm.Server.Tests/ProLicenseServiceTests`,
  `tests/Oadm.Client.Tests/LicenseCardTests`, the off state with the trial in `tests/Oadm.Plugins.Radius.Tests/Client/HeadlessPageTests`.
