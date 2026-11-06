---
title: Host services
description: "What the server offers a core plugin that runs a service: settings, records, network interfaces, ports, the firewall, trust anchors and adding devices."
---

A core plugin that runs a service for devices (an NTP server, a DHCP server, a CA) needs more than devices and VAPIX:
settings that survive a restart, a port to listen on, the server's network cards. The host has these, so your plugin
never opens a socket or writes a file of its own for them. Every member is on `ICorePluginContext`
([Core plugins and pages](../core-plugins/#what-the-server-gives-it-icoreplugincontext)); most are null on an older host.

## Settings and records

- **Settings**: `ctx.Settings.GetAsync` / `SetAsync(key, json, ct)`, per plugin, in the server database. Store one JSON
  document on Save (the NTP server uses the key `config`: enabled, interface, upstream) and read it in `StartAsync`, so
  the service comes back after a server restart. Never wait for the network in `StartAsync`: start background loops.
- **Secrets** in the settings are encrypted with `ctx.Secrets`, see
  [Settings and secrets](../core-plugins/#settings-and-secrets).
- **Many records** (one per device, certificate or lease): `ctx.Records` (`IPluginRecords`), one row per record.
  Never keep a list that grows with the devices in one settings value: it is rewritten whole on every change.

```csharp
var leases = await ctx.Records!.ListAsync("leases", ct);                   // on start: key -> JSON
await ctx.Records.SetManyAsync("leases",
    [new("B8A44F631339", PluginJson.Serialize(lease, Json)),               // only what changed
     new("B8A44F000001", null)], ct);                                      // null deletes
```

`GetAsync(collection, key, ct)` reads one record, `ClearAsync` empties a collection. `SetManyAsync` writes its records
in one transaction. In tests use `MemoryPluginRecords` (`FakeCoreContext.PluginRecords`).

## The server's network interfaces

`Oadm.Sdk.Network.SystemNetworkInterfaces.Instance.List()` returns the server's network cards as
`ServerNetworkInterface` on Windows, Linux and macOS: id, name, description, addresses (IPv4 first), up, loopback,
`PrimaryAddress`, `PrimaryIpv4`, `PrefixLengths`, `Gateways`, `DnsServers`, `DnsSuffix` and `Ipv4Index`.

- Depend on `IServerNetworkInterfaces`, so tests pass fixed interfaces.
- Return the list from a page method: it lives on the server, and the client computer has other cards.
- `InterfaceOptions.From(nic, addressText)` builds the entry of a select (`InterfaceOption`, label "Ethernet -
  10.0.0.17 (Intel I219)"); `InterfaceOptions.All` is the "All interfaces" entry and `IsAll(id)` tests for it.
- On the page, `Oadm.Sdk.Client.Network.InterfaceSelection` is the select's view model. It keeps the selection across
  refreshes, and an interface that is configured but gone stays in the list as "(not available)". Bind the ComboBox to
  its `Items` and `Selected`.

## Ports and the firewall

Listen through the host, never with your own socket: `ctx.Network.ListenUdpAsync(port, reason, ct)` and
`ListenTcpAsync`. The host binds the port and opens the firewall rule of the Windows service. At sites the same server
runs behind `ctx.Sites.OpenUdpRelayAsync` / `OpenTcpRelayAsync`; both are in
[Sites](../sites/#offer-a-server-to-devices-listeners-and-relays).

- **One interface.** `ListenTcpAsync(port, reason, addresses, ct)` and the same overload of `ListenUdpAsync` bind only
  the addresses of the interface the user chose in "Listen on". An address that is not ready is left out while the
  others serve; `BoundAddresses` of the listener lists what is bound now (null when the host does not tell).
- **Tests.** Make the port injectable. Tests use `Oadm.Sdk.Testing.FakePluginNetwork` or an in-memory relay, never the
  real port.
- **Bind errors.** `Oadm.Sdk.Network.PortBindErrors.Classify(SocketError, HostOs)` says what a failed bind means on this
  system, with the texts `InUseText`, `PermissionText`, `OtherText` and `PermissionFix`. Windows has no privileged
  ports, so an access error there means another program holds the port. Linux needs root or `cap_net_bind_service`.
  macOS needs root for a single address. `HostOsInfo.Current` is the system; `ScQueryServiceProbe` tells whether a
  Windows service runs (W32Time, DHCPServer) that may hold your port.
- **Your own firewall rule.** `ctx.Firewall` (`IFirewallRules`, null outside the Windows service) opens a UDP port, or
  a range with `FirewallRule.ForServiceRange`. Keep it in line with your service through a `FirewallRuleKeeper`:
  `SyncAsync(enabled, ct)` opens the rule while the service is on and closes it otherwise. Errors are logged and never
  fail the service.

## Status and rate limits

- **Status line.** `Oadm.Sdk.Network.ServiceStatus(kind, text, detail)`: the kind is `Ok`, `Neutral`, `Warning` or
  `Error`, the text is plain, the detail is the fix and shows as the tooltip. Protocol details go to the server log
  only.
- **Rate limits.** `Oadm.Sdk.Network.KeyedRateLimiter<TKey>` is a token bucket per key (a client address, a MAC
  address): `Check(key)` answers `Allow`, `Drop` or `GlobalDrop`. `KeyedRateLimitOptions` sets the burst, the refill,
  the most clients it tracks (the least recently seen one goes first), the idle time after which a client is forgotten,
  one limit over all clients and notices per client ("log once a minute"). It allocates nothing in the steady state.
  The NTP server limits per client address, the DHCP server per MAC address. At sites keep one limiter per site: two
  sites may use the same addresses.

## Trust anchors

`ctx.TrustAnchors?.Set(derCertificates)` replaces your plugin's set of CA certificates (public DER only) that the
server trusts, besides the system's store, when it rates device certificates for the **Certificate** column.

- A device certificate that chains to one of them is Trusted; a self-signed one stays Self-signed. The certificate
  OADM pinned for a device is never affected.
- The host keeps one set per plugin (all sets together count) and removes it when the plugin stops.
- Devices show the new rating after their next refresh.

PKI sets every CA with its key and the previous CAs, each with its chain, on start and on every change.

## Add and follow devices

`ctx.AutoAdd` (`IDeviceAutoAdd`) is for a plugin that sees devices on the network, like the DHCP server:

| Call | What it does | Result |
|---|---|---|
| `AddAsync(address, expectedSerial, source, ct)` | Adds an Axis device through the add page's checks: the Axis check without a login, the factory default check, a login with the credential list | `DeviceAutoAddOutcome` with `Added`, `AddedCredentialsRequired`, `AddedPasswordNotSet`, `AlreadyManaged`, `NotAxis`, `SerialMismatch`, `Unreachable` or `LimitReached`, the device id and a plain message |
| `FollowAsync(serial, address, source, ct)` | Moves a managed device's record to an address your plugin saw it at, once the server found it there | `Moved`, `Unchanged`, `NotManaged`, `KeptHostName` or `NotVerified` |

Passwords never reach your plugin, and the server writes the audit entries. Both calls contact the device: run them
from a queue with a few workers, never on the path that answers your clients.

## Services between plugins

Core plugins offer each other services through `ctx.Services` (`IPluginServices`):

```csharp
ctx.Services?.Offer<IDeviceCertificateAuthority>(authority);          // the offering plugin, in StartAsync

var ca = ctx.Services?.Find<IDeviceCertificateAuthority>();           // the using plugin, each time it needs it
if (ca is not { IsReady: true })
{
    throw new InvalidOperationException("Create a CA on the PKI page first.");
}
```

- The contract is an interface of the SDK: plugins load apart from each other and share only SDK types.
- One offer per interface; a second plugin's offer throws.
- The host withdraws a plugin's offers when it stops, fails or is turned off. Ask with `Find` each time and never keep
  the result.

PKI offers `Oadm.Sdk.Certificates.IDeviceCertificateAuthority` (sign a device's certificate request, the list of issued
certificates); the EST server and the 802.1X server use it. In tests: `FakePluginServices`
(`FakeCoreContext.FakeServices`).

## Protected tags

A plugin whose settings act on devices by tag (the 802.1X server's rules) names those tags with
`ctx.ProtectedTags?.Protect(names, refusal)`. Only users with all devices then add them to or remove them from devices;
a user with a device group gets your refusal text.

- Each call replaces your plugin's set. `Protect(names, refusal, changeRefusal)` also refuses a rename or delete of such
  a tag, for every user, while your plugin runs.
- The host keeps the set while the plugin is installed, also while it is stopped or turned off.
- Null on an older host: then treat your tag rules as unsafe and let them match nothing.

In tests: `FakeProtectedTags` (`FakeCoreContext.FakeProtectedTags`).

## Reference

Every member with its signature in the [SDK reference](../../api/): [`IPluginSettings`](../../api/sdk/plugins/ipluginsettings/), [`IPluginRecords`](../../api/sdk/plugins/ipluginrecords/), [`IPluginNetwork`](../../api/sdk/sites/ipluginnetwork/), [`IPluginServices`](../../api/sdk/plugins/ipluginservices/).
