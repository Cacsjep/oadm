---
title: Sell a plugin with a license
description: Licenses for commercial plugins, checked offline by OADM, without a license system of your own.
---

OADM has a license check for plugins you sell. You don't need a license server or your own license code: you sign a
license key for each customer, OADM checks it offline, and your plugin asks OADM whether it may run.

Plugins under the Apache 2.0 license of OADM may be closed source and sold; nothing has to be published.

## How it works

1. Every OADM server has a **server ID**, made at its first start. The customer finds it on the **Settings** page (and on
   the **Plugins** page while a plugin needs a license) and sends it to you. A restored backup keeps it, so moving a server keeps its licenses.
2. You make a key pair once. The **public key** goes into your plugin's project (and from there into its
   `plugin.json`); the **private key** stays with you.
3. For each sale you sign a license key for the customer's server ID: who it is for, until when and, if you want, which
   features.
4. The customer pastes the key on the Plugins page. OADM checks the signature with the public key of your
   `plugin.json`, the plugin id, the server ID and the date.
5. Your plugin reads the result and decides what runs.

OADM is open source, so someone determined can always remove a check. The license keeps honest customers honest, which is
what most commercial plugins need.

## 1. Make your keys

```
dotnet oadm license keys --out ../acme-keys
```

This writes `private-key.pem` (keep it secret and back it up: it signs every license) and `public-key.txt`, and prints the
properties for your project. It never overwrites a key: a new key would make every license you sold invalid. In a
checkout of OADM the same command is `manage license keys`.

## 2. Declare the license in the project

In the server part's project file:

```xml
<PropertyGroup>
  <OadmPluginId>acme.site-reports</OadmPluginId>
  <OadmLicensePublicKey>MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...</OadmLicensePublicKey>
  <OadmLicenseFeatures>reports;export</OadmLicenseFeatures>
  <OadmLicenseTrialDays>30</OadmLicenseTrialDays>
</PropertyGroup>
```

| Property | Meaning |
|---|---|
| `OadmLicensePublicKey` | Your public key from `oadm license keys` |
| `OadmLicenseFeatures` | The features a license can unlock, separated by `;` (shown on the Plugins page, optional) |
| `OadmLicenseTrialDays` | Days the plugin runs without a license, from the day the server first loaded it; 0 or missing = no trial |

The build writes them into `plugin.json` as its `license` entry (`publicKey`, `features`, `trialDays`); with a
`plugin.json` of your own, write the entry there. Without a public key the plugin is free and OADM asks for nothing.

## 3. Sign a license

```
dotnet oadm license sign --key ../acme-keys/private-key.pem --plugin acme.site-reports
    --server 7f3c2a1e-5b8d-4c6f-9a2e-1d0b4e8f6c37 --customer "ACME GmbH"
    --expires 2027-12-31
```

| Option | Meaning |
|---|---|
| `--server` | The customer's server ID |
| `--customer` | Shown on the Plugins page |
| `--expires` | The last day it is valid; without it the license never expires |
| `--features` | The features it unlocks, separated by commas (the project property `OadmLicenseFeatures` takes `;`); without it every feature |
| `--limit` | Optional: a number your plugin enforces, for example `sites=3`; repeat for more |
| `--out` | The file name (default `<customer>-<plugin id>.oadmlicense`) |

The result is one line, the license key, printed and written to the file:

```
OADM1.eyJwbHVnaW4iOiJhY21lLnNpdGUtcmVwb3J0cyIsInNlcnZlciI6IjdmM2MyYTFlLTViOGQtNGM2Zi05YTJlLTFkMGI0ZThmNmMzNyIs....MEUCIQ...
```

Send the customer the key or the file. The key carries the plugin, the server ID, the customer, the dates and, when you
gave them, the features and limits, signed with your private key; any change breaks the signature. Spaces and line breaks
a mail program adds are ignored. To sign from your own shop instead of `oadm license sign`, call `LicenseFile.Sign` of the SDK
(`Oadm.Sdk.Licensing`) with the same data and key.

## 4. Check the license in the plugin

The license is in every context of your plugin: `ICorePluginContext.License`, `ITaskExecutionContext.License` and
`IActionContext.License`. It is live: it changes when the customer enters or removes a license and when it or the trial
runs out.

```csharp
public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
{
    _license = ctx.License;
    if (_license is not null)
    {
        _license.Changed += (_, _) => ctx.Logger.LogInformation("License: {Reason}", _license.Current.Reason);
    }

    return Task.CompletedTask;
}

public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
{
    if (_license is { IsValid: false })
    {
        throw new InvalidOperationException(_license.Current.Reason); // "The trial ended on 2026-11-15. Enter a license."
    }

    if (method == "export" && _license?.Has("export") == false)
    {
        throw new InvalidOperationException("Your license does not include the export.");
    }

    var sites = _license?.Limit("sites"); // only when you sell limits; null = no limit
    ...
}
```

| Member | Meaning |
|---|---|
| `IsValid` | A valid license, a running trial, or no license needed |
| `Has(feature)` | The license lists the feature or lists none; always true during the trial |
| `Limit(name)` | The limit of a valid license, null when it sets none or during the trial, 0 without a valid license |
| `Current.Status` | `Valid`, `Trial`, `TrialEnded`, `Missing`, `Expired`, `WrongServer`, `Invalid`, `NotRequired` |
| `Current.Reason` | One plain sentence for your messages, for example "Licensed to ACME GmbH until 2027-12-31." |
| `Current.Data` | Customer, dates, features and limits of a readable license |
| `Changed` | Raised when the state changed |

OADM only checks. Your plugin decides what runs without a valid license: nothing, a limited version, or a reminder.
`License` is null on hosts without licenses (older OADM): treat that as you prefer.

A plugin page reads the same state with `ICorePluginClientContext.LicenseOf(packageId)`, for example to show "No license"
or to lock buttons. The server part must check again, because a client can be changed.

## On the Plugins page

While a plugin needs a license, administrators see the server ID with a copy button and a **License** column: the state
(Licensed, Trial, Trial ended, No license, Expired, Other server, Not valid) with the reason as tooltip, **Enter license**
(paste the key or choose its file; below the field OADM shows whom the key is for, and a key that does not fit is refused
with the reason; the button reads **Change license** once a key is stored) and **Remove**. Entering and
removing a license is written to the audit log.
