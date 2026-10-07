# Oadm.Plugins.Network

Two task plugins in one package (`artifacts/plugins/oadm.network/`):

- `oadm.network`, **"Network settings"** (context menu group Network, no toolbar button): IPv4, IPv6, DNS and host name
  for the selected devices.
- `oadm.network.assign-ip`, **"Assign IP address"** (context menu group Network and toolbar, multi-device first): clone of
  ADM's "Assign IP address to selected devices", DHCP or an IP address range.

Both open a dialog, run one task per device with the same per-device task (`NetworkTaskRunner`) and OADM
follows a device to its new static address.

| Part | Assembly | Content |
|---|---|---|
| `plugins/Oadm.Plugins.Network` | `Oadm.Plugins.Network.Server.dll` | `NetworkSettingsTaskPlugin`, `AssignIpTaskPlugin` (`ITaskPlugin` + `ITaskPluginQuery`), `NetworkTaskRunner`, `AddressCheck` (query), `IAddressProbe` / `NetworkAddressProbe` (ping + TCP 80/443), `NetworkTaskNames`, payload model, validation, IP range syntax (`IpRangeExpression`), address suggestion and conflicts (`AddressAssigner`, `AddressConflicts`), VAPIX requests |
| `plugins/Oadm.Plugins.Network.Client` | `Oadm.Plugins.Network.Client.dll` | `NetworkSettingsDialog`/`ViewModel`/`Window`, `AssignIpDialog`/`ViewModel`/`Window`, the shared address table `AddressAssignmentGrid` + `AddressAssignmentViewModel` + `AddressRowViewModel`, `NetworkWarnings`, `FieldErrors` (INotifyDataErrorInfo) |
| `tests/Oadm.Plugins.Network.Tests` | | unit, fixture, view model and headless dialog tests |

The client part references the server assembly for the shared payload model, validator, range parser and
assignment (same plugin folder, so the client plugin load context resolves it). The SDKs, Avalonia and
CommunityToolkit.Mvvm come from the host and are never copied.

## ADM reference: "Assign IP address"

Researched October 2026. Sources:
- ADM user manual, https://help.axis.com/en-us/axis-device-manager (mentions only that ADM can "assign IP
  addresses"; no dialog details).
- "How to assign an IP address and access your device", https://help.axis.com/en-us/access-your-device: ADM
  procedure. Several devices: *Device manager > Manage devices*, select the devices, **Assign IP address to
  selected devices**, select **Assign the following IP address range**, enter the range in the **IP range**
  field, **Next**, "To change the IP address, select a device and click **Edit IP**", **Finish**. "AXIS Device
  Manager suggests IP addresses from a specified range." One device: **Assign the following IP address**, IP
  address, subnet mask, default router, **OK**.
- AXIS Camera Station 5 manual, https://help.axis.com/en-us/axis-camera-station-5, "Assign IP address" (same
  options): **Obtain IP addresses automatically (DHCP)** or **Assign the following IP address range** with IP
  range, subnet mask and default router; range syntax "192.168.0.* or 10.*.1.*", "192.168.0.10-192.168.0.20
  (this address range can be shortened to 192.168.0.10-20) or 10.10-30.1.101", "10.10-30.1.*",
  "192.168.0.*,192.168.1.10-192.168.1.20"; then "Review the current IP addresses and the new IP addresses",
  **Edit IP** per device, **Finish**; inaccessible devices are skipped.
- Not documented anywhere: what happens with more devices than addresses, how addresses in use are handled,
  and whether ADM follows a device to its new address. OADM's decisions are below.

How OADM clones it:

| ADM | OADM |
|---|---|
| Menu "Assign IP address to selected devices" | Context menu (Network) and toolbar **Assign IP address** |
| Obtain IP addresses automatically (DHCP) | Same wording; Finish on page 1 (single device: "Obtain IP address automatically (DHCP)") |
| Assign the following IP address range: IP range, subnet mask, default router | Same wording and fields, plus optional DNS servers (domain name and search domains of each device are kept: `DnsChange.KeepDomains`). Single device: "Assign the following IP address", field "IP address" |
| Range syntax: wildcards, first-last (also shortened), range in any octet, commas | All of them (`IpRangeExpression`), plus a single address alone = start address (consecutive addresses up to the end of its subnet); at most 1,048,576 addresses per expression |
| Next: current and new IP addresses, Edit IP per device, Finish | Page 2 "New IP addresses": MAC address, Model, Current IP address, New IP address (edited in the cell instead of an Edit IP dialog), Status chip; Back, Finish |
| Suggests addresses from the range | In grid order (the order the host passes the selection), the first free addresses of the range. Skipped: network and broadcast address, loopback/multicast/link-local, the default router, addresses of other managed devices, addresses found in use, duplicates. A device keeps its own address when the range reaches it |
| (undocumented) too few addresses | "Not enough addresses: the IP range has N free addresses for M devices. Extend the range." and rows without an address; Finish stays disabled until every row has a valid address |
| (undocumented) addresses in use | Page 2 runs the read-only query `checkAddresses` (also the **Check addresses** button): all managed device addresses plus, from the server, an ICMP echo (`System.Net.NetworkInformation.Ping`, 2 tries, 1 s each; cross-platform, on Linux .NET falls back to the `ping` utility without root) and a TCP connect to port 80 and 443 of each candidate (no ARP request of our own, no HTTP; a refused connection also means "in use"). Anything that answers and is not the device's own current address is in use; taken addresses are skipped and suggested again around them (3 rounds); remaining ones are flagged "In use (answers ping)" / "In use (answers on port 80/443)". The task checks again before writing (step **Check address is free**) |
| Invalid devices are skipped | Each device is its own task; one failing device never affects the others |

Conflicts per row (status chip, Finish disabled): "No address", "Not a valid IPv4 address", network/broadcast/
loopback/multicast/link-local, "Same as the default router", "Outside the subnet of the default router",
"Assigned to more than one device", "Used by <model serial>", "In use (answers ping)", "In use (answers on port
80/443)". Errors are shown once: a row's problem only in its Status column; errors of a field (IP range, subnet
mask, default router, DNS servers) directly below that field (`INotifyDataErrorInfo`); "Not enough addresses"
below the table. No error list, no info line with the current values (they are in the fields and the table).
Next / Finish stay disabled while any error exists; their tooltip says why. **Finish** opens the host's shared
confirmation window (`ui:MessageWindow`) with the reachability warning ("The devices get new IP addresses" /
"The devices get their addresses from DHCP"; buttons Cancel / Finish); there is no inline acknowledgement.

## Network settings dialog

- Sections **IPv4**, **IPv6**, **DNS**, **Host name**, each starting at **Keep unchanged**. Only
  touched sections go into the payload and only those are written.
- Prefill: the read-only query `getNetworkInfo` reads the first selected device and prefills the fields (no
  "Current: ..." info lines). A failed query never blocks the dialog (one line in the Devices card).
- IPv4 static: subnet mask as `255.255.255.0`, `24` or `/24`, default gateway required (shared fields). One
  device: field **IP address**. Several devices: **no IP range field**; the column **New IP address** of the
  Devices table (`AddressAssignmentGrid`) is the only place to set addresses. It is suggested from the first
  device's current address and subnet (like Assign IP address with that address as start; without a known
  address every row is typed by hand), every row editable; a new mask or gateway suggests again around edits.
- IPv6: Disabled, Automatic (router advertisement), DHCPv6, Static. Static, one device: field **IP address**;
  several devices: column **New IPv6 address** in the table (shown only for static IPv6, validated per row:
  missing, invalid, loopback/multicast/IPv4-mapped, duplicate, managed device, in use), prefix length and
  gateway shared. The address check probes IPv6 addresses too.
- DNS: from DHCP, or static primary/secondary server, domain name, search domains.
- Host name: from DHCP, or static. Several devices use a template with `{n}` (position) or `{serial}`.
- Errors: a field's error directly below the field (`INotifyDataErrorInfo` through `FieldErrors`; mask,
  gateway, IP/IPv6 address of one device, prefix length, DNS server, domain name, search domains, host name),
  per-device problems only in the table's Status column ("IPv6: ..." for the IPv6 column). No error list. Apply
  stays disabled while any error exists; its tooltip says why.
- **Apply** on a risky change (an IPv4 change: new address, subnet, gateway or switch to DHCP; an IPv6 change on
  a device OADM reaches over IPv6) opens the host's shared confirmation window (`ui:MessageWindow`, title "The
  devices may become unreachable", the warning naming how many devices get a new address, buttons Cancel /
  Apply). No inline warning section or checkbox.
- Validation is the same `PayloadValidator` the server runs: address/mask/gateway in one subnet, no
  network or broadcast address, no loopback/multicast/link-local, no duplicates in the batch (address
  equal to the gateway included), IPv6 syntax and prefix, DNS servers, domain and host name syntax,
  duplicate host names; plus the table's conflicts.

## Payload

Both plugins send a `NetworkPayload`; Assign IP address only `ipv4` and optionally `dns` (anything else is
refused before the first request).

```json
{
  "ipv4": { "mode": "static", "prefixLength": 24, "gateway": "10.0.0.1" },
  "ipv6": { "mode": "auto" },
  "dns": { "useDhcp": false, "servers": ["10.0.0.2"], "domainName": "example.com", "searchDomains": [], "keepDomains": false },
  "hostName": { "useDhcp": false },
  "devices": { "<device id>": { "ipv4Address": "10.0.0.100", "hostName": "cam-1", "ipv6Address": "2001:db8::100" } }
}
```

Static IPv6 (`"ipv6": {"mode":"static","prefixLength":64,"gateway":null}`) takes each device's `ipv6Address`
(`Ipv6Change.AddressFor`; `ipv6.address` is the fallback for a device without one).

Query `checkAddresses` (both plugins, read-only): request `{"addresses":["10.0.0.100","2001:db8::100"],"probe":true}`
(IPv4 and IPv6, at most 256), answer `{"managed":[{"address","deviceId","device"}],"probed":[{"address","inUse","answersPing"}]}`.

Task names (`GetTaskName`, once per run from the shared payload): Network settings "Set static IP 10.0.0.60"
(one device) / "Set static IP addresses" (several), "Switch to DHCP", "Set DNS servers" / "Use DNS from DHCP",
"Set host name cam-1" / "Set host names" / "Use host name from DHCP", "Change IPv6 settings" when only that
section changes, else "Change network settings"; Assign IP address "Assign IP 10.0.0.60" (one device) / "Assign IP
addresses" (several) / "Assign IP via DHCP". An unreadable payload gives the display name.

## Task (per device)

Steps of **Network settings**: **Check compatibility** ("network-settings 1.37" or "param.cgi"), **Read
current settings** (getNetworkInfo, or the param.cgi Network group on legacy devices), **Read IPv6 address mode**
(param.cgi; Skipped when it is already part of the first read or param.cgi is missing), **Validate settings**
(the plan below), **Check address is free**, **Set host name**, **Set DNS**, **Set IPv6** (+ **Enable IPv6** when the interface is switched
on with a second request), **Set IPv4** (IPv4 before IPv6 for a device reached over IPv6), **Wait for the
settings to apply**, **Check reachability**, **Wait for the device at the new address**, **Verify device
identity**, **Update OADM device address**.

Steps of **Assign IP address**: Check compatibility, Read current settings, Validate settings, Check address is
free, Set DNS,
Set IPv4, Wait for the settings to apply, Check reachability, Wait for the device at the new address, Verify
device identity, Update OADM device address (no IPv6 mode read, no host name or IPv6 step).

Every device request is its own step; sections the user kept unchanged are Skipped ("Keep unchanged"). A
failing write is Failed and the later steps Skipped. A successful task ends with the engine's step **Completed**.

**Check address is free** runs right before the first write: every new static address the task writes (IPv4,
and IPv6 static) is probed with `IAddressProbe` (ping 2 x 1 s plus TCP 80/443, `NetworkAddressProbe`; tests use a
fake). The device's own current address (OADM's address, its current IPv4 and IPv6 addresses) is never probed
("10.0.0.48 is the device's own address"). When anything answers, the step fails with "10.0.0.60 is already in use
(answers ping). Nothing was changed." (or "(answers on port 80/443)") and no request is sent, not even DNS.
Skipped "DHCP: no static address is set" / "No static address is set".

1. Parse and validate the payload for the whole batch. Invalid: fails with "... Nothing was changed."
2. Fresh `GetApiListAsync`; read the current settings (getNetworkInfo, or param.cgi on legacy devices).
3. Build the plan: method per section from the decision table below, `Require(...)` per method, device
   limits (`maxSupportedStaticNameServers`, interface with IPv4/IPv6). Anything unsupported fails with
   `DeviceNotCompatibleException` before the first write.
4. Write in order **host name, DNS, IPv6, IPv4** (the address family OADM connects with is always last;
   for a device reached over IPv6 the order is host name, DNS, IPv4, IPv6). Each step is logged with
   `ctx.Log`; a failing step names the steps already applied.
5. After the connection-relevant write: log "The device will be re-addressed from A to B", **Wait for the
   settings to apply** (5 s), then:

| Case | Check reachability | Wait for the device at the new address | Verify device identity | Update OADM device address |
|---|---|---|---|---|
| New static address (IPv4, or IPv6 when OADM connects over IPv6) | Skipped "The device moves to B." | polls B every 3 s with `ctx.CreateClientForAsync(B)` for up to 90 s; Done "The device answers at B" | serial number at B equals the device's: Done | `ctx.UpdateDeviceAddressAsync(B)`: Done "A -> B" |
| ... not answering at B | Skipped | **Warning**: "does not answer at B within 90 s, nor at A" (check mask and router; record keeps A) or "... but still answers at A, so the new address may not be active" (the device rolls back failed changes) | Skipped "The device was not found at the new address." | Skipped (same) |
| ... another serial at B | Skipped | Done | **Warning** "Another device answers at B (serial ..., expected ...). The address may be in use." | Skipped "The device at the new address is not this device." |
| ... OADM uses the host name (`Devices.UseHostName`) | Skipped | Done | Done | Skipped "OADM reaches the device by host name H; the host name is kept." |
| ... server refuses (serial check on the server) | Skipped | Done | Done | **Warning** with the server's message |
| DHCP (new address unknown) | watches A for 45 s; **Warning** when A stops answering ("OADM keeps the old address until the next mDNS scan finds the device again") | Skipped | Skipped | Skipped "DHCP: address assigned by the network, the device will be found again by the next scan" |
| Same address | watches A; Done, or **Warning** "check the subnet mask and gateway" | Skipped "The address does not change." | Skipped | Skipped |
| Change does not affect OADM's connection | Skipped | Skipped | Skipped | Skipped |

Re-finding moved devices (server): every 5 minutes (first run 30 s after start), when at least one device is
**Unreachable** and addressed by IP, the server browses mDNS for 15 s (`DeviceRelocationHostedService`). A
managed device announced at another address is verified there (authenticated basicdeviceinfo, same serial)
and its record moves (`DeviceAddressService.TryRelocateAsync`: logged, published, full refresh queued, status
Unknown until the refresh). Devices addressed by host name are never moved.

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
