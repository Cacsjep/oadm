# Oadm.Plugins.Network

Task plugin `oadm.network`, "Network settings..." in the device context menu (no toolbar button).
Opens a dialog, then changes IPv4, IPv6, DNS and host name on the selected devices.

| Part | Assembly | Content |
|---|---|---|
| `plugins/Oadm.Plugins.Network` | `Oadm.Plugins.Network.Server.dll` | `NetworkSettingsTaskPlugin` (`ITaskPlugin` + `ITaskPluginQuery`), payload model, validation, VAPIX requests |
| `plugins/Oadm.Plugins.Network.Client` | `Oadm.Plugins.Network.Client.dll` | `NetworkSettingsDialog` (`ITaskPluginDialog`), `NetworkSettingsViewModel`, `NetworkSettingsWindow` |
| `tests/Oadm.Plugins.Network.Tests` | | unit, fixture, view model and headless dialog tests |

Both assemblies build into `artifacts/plugins/oadm.network/` next to `plugin.json`. The client part
references the server assembly for the shared payload model and validator (it is in the same plugin
folder, so the client plugin load context resolves it). The SDKs, Avalonia and CommunityToolkit.Mvvm
come from the host and are never copied.

## Dialog

- Sections **IPv4**, **IPv6**, **DNS**, **Host name**, each starting at **Keep unchanged**. Only
  touched sections go into the payload and only those are written.
- Prefill: the read-only query `getNetworkInfo` reads the first selected device ("Current: ..." line
  per section, fields prefilled). A failed query never blocks the dialog.
- IPv4 static: subnet mask as `255.255.255.0`, `24` or `/24`, default gateway required. With several
  devices the address field is the **start address**: device *n* in list order gets start + *n* - 1,
  shown in a **Preview** table (device, current address, new address, new host name).
- IPv6: Disabled, Automatic (router advertisement), DHCPv6, Static (static only for one device).
- DNS: from DHCP, or static primary/secondary server, domain name, search domains.
- Host name: from DHCP, or static. Several devices use a template with `{n}` (position) or `{serial}`.
- Strong warning (must be acknowledged before **Apply**) for every IPv4 change, naming how many
  devices get a new address, and for IPv6 changes on devices OADM reaches over IPv6.
- Validation is the same `PayloadValidator` the server runs: address/mask/gateway in one subnet, no
  network or broadcast address, no loopback/multicast/link-local, no duplicates in the batch (address
  equal to the gateway included), range fits the subnet, IPv6 syntax and prefix, DNS servers, domain
  and host name syntax, duplicate host names.

## Payload

```json
{
  "ipv4": { "mode": "static", "prefixLength": 24, "gateway": "10.0.0.1" },
  "ipv6": { "mode": "auto" },
  "dns": { "useDhcp": false, "servers": ["10.0.0.2"], "domainName": "example.com", "searchDomains": [] },
  "hostName": { "useDhcp": false },
  "devices": { "<device id>": { "ipv4Address": "10.0.0.100", "hostName": "cam-1" } }
}
```

## Task (per device)

1. Parse and validate the payload for the whole batch. Invalid: fails with "... Nothing was changed."
2. Fresh `GetApiListAsync`; read the current settings (getNetworkInfo, or param.cgi on legacy devices).
3. Build the plan: method per section from the decision table below, `Require(...)` per method, device
   limits (`maxSupportedStaticNameServers`, interface with IPv4/IPv6). Anything unsupported fails with
   `DeviceNotCompatibleException` before the first write.
4. Write in order **host name, DNS, IPv6, IPv4** (the address family OADM connects with is always last;
   for a device reached over IPv6 the order is host name, DNS, IPv4, IPv6). Each step is logged with
   `ctx.Log`; a failing step names the steps already applied.
5. After the connection-relevant write: log "The device will be re-addressed from A to B", wait 5 s,
   then watch the old address for 45 s:
   - re-addressed and the old address stops answering: `ReportWarning` (device moved; OADM still has
     the old address, remove and add it again);
   - re-addressed to a static address but the old address keeps answering: `ReportWarning` (the device
     rolls back failed changes; check its system log);
   - same address but no answer: `ReportWarning` (check mask and gateway).

## VAPIX decision table

`network-settings` = `axis-cgi/network_settings.cgi` (JSON, AXIS OS 8.50+); `param-cgi` =
`axis-cgi/param.cgi` (POST form body, never URL parameters). The JSON API is used whenever
`network-settings` 1.x is listed; `param-cgi` only for what the JSON API cannot do or when it is missing.
Requests carry the `network-settings` version listed by `apidiscovery.cgi` as `apiVersion`.

| Setting | Method | API, min version | Source of the minimum |
|---|---|---|---|
| Read current settings | `getNetworkInfo` | network-settings 1.0 | doc examples use 1.0 |
| Read IPv6 mode, static address, gateway | `param.cgi action=list` `Network.IPv6`, `Network.eth0.IPv6`, `Network.Routing.IPv6` | param-cgi 1.0 | getNetworkInfo has no IPv6 mode when IPv6 is off (verified) |
| Host name | `setHostnameConfiguration` (`useDhcpHostname`, `staticHostname`) | network-settings 1.0 | doc examples use 1.0 |
| DNS | `setResolverConfiguration` (`useDhcpResolverInfo`, `staticNameServers`, `staticDomainName`, `staticSearchDomains`) | network-settings 1.0 | doc examples use 1.0 |
| IPv4 DHCP / static, gateway | `setIPv4AddressConfiguration` (`deviceName`, `configurationMode`, `staticAddressConfigurations[{address,prefixLength}]`, `staticDefaultRouter`) | network-settings 1.0 | doc example "Assign a static IPv4 address" uses 1.0 |
| (not used) link-local mode, DHCP with static fallback | `setIPv4AddressConfiguration` `linkLocalMode`, `useStaticDHCPFallback` | network-settings 1.21 | doc examples use 1.21 |
| (not used) DHCP classless static routes | `setIPv4AddressConfiguration` `useDHCPStaticRoutes` | network-settings 1.29 | doc example uses 1.29 |
| IPv6 on/off | `setIPv6AddressConfiguration` (`deviceName`, `enabled`) | network-settings 1.6 | doc example uses 1.6; below 1.6: `Network.IPv6.Enabled` |
| IPv6 automatic / DHCPv6 / static, IPv6 gateway | `param.cgi action=update` `Network.IPv6.AcceptRA`, `.DHCPv6` (`auto`/`stateful`/`off`), `.IPAddress` (`addr/prefix`), `.DefaultRouter` | param-cgi 1.0 | `setIPv6AddressConfiguration` only has `enabled` |
| Legacy host name | `Network.HostName`, `Network.VolatileHostName.ObtainFromDHCP` | param-cgi 1.0 | Network settings parameter docs (AXIS OS 5.00+) |
| Legacy DNS | `Network.Resolver.ObtainFromDHCP`, `Network.DNSServer1/2`, `Network.DomainName` (no search domains: fails as not compatible) | param-cgi 1.0 | same |
| Legacy IPv4 | `Network.IPAddress`, `.SubnetMask`, `.Broadcast`, `.DefaultRouter`, `.BootProto` (`dhcp`/`none`), one request | param-cgi 1.0 | same |
| (not used) proxies, VLAN, 802.1X, WLAN | `setGlobalProxyConfiguration`, `addVlan`, `setWired8021XConfiguration`, ... | | out of scope |

There is no `setDefaultRouter` method: the IPv4 gateway is `staticDefaultRouter` of
`setIPv4AddressConfiguration`. The documentation has no per-method version history; the minimums
are the earliest `apiVersion` the official examples use for that method or parameter.

## Verified on AXIS P3265-V, AXIS OS 12.11.77 (read-only)

- `apidiscovery.cgi`: `network-settings 1.37`, `param-cgi 1.0`.
- `getSupportedVersions` answers `{"data":{"apiVersions":["1.38"]}}`: version 1.38 although
  apidiscovery lists 1.37, and the field is `apiVersions`, not the documented `supportedVersions`.
- `getNetworkInfo` accepts `apiVersion` 1.0 and 1.37 (answers with 1.38) and returns error 4001 for
  1.99 and 2.0.
- One interface `eth0`, `maxSupportedStaticAddressConfigurations` 1, `maxSupportedStaticNameServers` 3,
  `maxSupportedStaticSearchDomains` 6. With IPv6 disabled the `IPv6` object has only `enabled` and
  `addresses`. The resolver flag is spelled `useDhcpResolverInfo` in the response.
- `param.cgi` still has the full `Network.IPv6.*` (AcceptRA, DHCPv6, IPAddress, DefaultRouter, Enabled),
  `Network.Resolver.*`, `Network.VolatileHostName.*` groups.

Recorded responses (MAC/serial anonymized) are in `tests/Oadm.Plugins.Network.Tests/Fixtures`. No write
was ever sent to a real device; write paths are tested against fakes only.
