# ACAP license keys (spec draft)

Status: decided 2026-10-13, built (key files); Q3 changed: no license codes, see below. Part of Applications (ACAP)
(`oadm.acap`), which already showed the license status (License column: "Valid", "Valid until ...", "None").

## Decisions

| # | Topic | Decision |
|---|---|---|
| Q1 | Where | Like ADM, an optional **license key file** in the install form next to the `.eap`, installed right after the app; plus an action **Install license** for an app that is already installed (a license bought later or renewed). |
| Q2 | Key files | Several key files at once. A key file is XML (`<LicenseKey>`: `ApplicationID`, `DeviceID` = the MAC address, `ExpirationDate`, `Signature`; seen in a real key, format version 1), so each device gets the key with its own MAC address, and a key for another app is refused before the upload. Before anything runs the list shows per device "Key found" or "No key for this device"; an expired key is named. A device without a key gets the app only (Done with warnings). |
| Q3 | License code | **Not offered.** Research on the device's web page: the device has no API for a license code; the page's script from axis.com asks an Axis gateway for the key with a token of Axis' own, then uploads it like a key file. OADM does not call that service. Users activate codes on the device's Apps page (Web View). |

## Open points

- The upload (`license.cgi?action=uploadlicensekey`, field `fileData`) is taken from the device's web page script; no
  write test on hardware yet (it would need a valid key for a dev camera).
