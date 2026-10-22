# OADM PRO license

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

OADM stays fully open source. An OADM PRO license only tells PRO plugins (none yet; EST Server later) that PRO is active,
which plan, how many devices and until when. **OADM itself is never limited by it**: before, during and after a license
nothing in OADM changes, only PRO plugins read it.

## The license key

- The plugin license format (`docs/spec/plugins.md` "Plugin licenses", `Oadm.Sdk.Licensing.LicenseFile`), one line:
  `OADM1.<content>.<signature>`, both parts base64url without padding. A `.oadmlicense` file holds the same line; spaces
  and line breaks are ignored; at most 16 KB.
- `<content>`: the UTF-8 bytes of a JSON object, signed exactly as sent (no canonical form needed):
  `{"plugin":"oadm.pro","server":"<server ID>","customer":"ACME GmbH","issued":"2026-10-22","expires":"2028-10-22"|null,
  "plan":"Up to 500 devices","features":["trial"],"limits":{"devices":500}}`. `plugin` is always `oadm.pro`
  (`ProLicenseFile.ProductId`); `features` is only present for a trial (`["trial"]`); `limits.devices` only when the plan
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
  next to the label OADM PRO, a trial "Trial, until 2026-11-21"; the result of Import and Remove sits next to the buttons), Expired "Expired on 2028-10-22.", WrongServer "This license is for
  another server.", Invalid "This is not a valid OADM PRO license." (unknown key, changed content, another product).
- Stored in the setting `License.Pro` (the key text; not secret), read at start step 4a after the plugin licenses, checked
  again hourly (`PluginLicenseRefreshHostedService`); only an Active key is accepted on import.
- Plugins: `ICorePluginContext.Pro` and `ITaskExecutionContext.Pro` (`IProLicense`: `Current` (`ProLicenseInfo`),
  `IsActive`, `Changed`), default interface members (null on hosts without). Test fakes: `FakeProLicense`.

## gRPC and client

- `LicenseService` (`license.proto`): `GetLicense` (every signed-in user: server ID, status, reason, customer, plan,
  max_devices, expires, trial), `ImportLicense(license)` (Admin, audited "Imported license", INVALID_ARGUMENT with the
  reason, nothing stored), `RemoveLicense` (Admin, audited "Removed license").
- Settings page, card **License** (right column below Backup and restore; every user, read only for operators):
  Server ID with copy, "OADM PRO" with the state (status chip: ok active, warning trial, error expired / other server /
  not valid; "Licensed to ..." below), **Import license** (file picker `.oadmlicense`) and **Remove license** (red,
  confirmed) for administrators, links **Start 30-day trial** (no license) and **Activate license** (unless a bought
  license is active) that open `https://oadm.app/activate/?server=<id>[&trial=1]`.
- Fake mode: keys signed with `FakeOadmApi.SampleLicenseKeys` for `FakeOadmApi.FakeServerId`.
- Tests: `tests/Oadm.Core.Tests/Licensing/ProLicenseTests`, `tests/Oadm.Server.Tests/ProLicenseServiceTests`,
  `tests/Oadm.Client.Tests/LicenseCardTests`.
