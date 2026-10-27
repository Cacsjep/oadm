# Device discovery

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The add page that shows the results: [add-devices.md](add-devices.md).

## Zero-conf: mDNS only

Browse `_axis-video._tcp.local`. Verified on AXIS OS 12.11: the TXT record has exactly one
key `macaddress=<SERIAL>`, the instance name is `<Bonjour.FriendlyName> - <SERIAL>`, SRV
points to port 80 at `axis-<serial lowercase>.local`, A/AAAA gives the address. Ignore
169.254.x.x link-local addresses when a routable one is announced.
Must work on all three OS and on multiple NICs (bind one socket per interface). A scan of the add
page runs at most `Discovery.ZeroConfSeconds` (default 30, 5..300; `DiscoveryService.StartZeroConf(duration)`,
timer from the server's TimeProvider) or until `StopScan`: browsing stops, the probes already started
finish, watchers get scan_finished, the devices stay in the session until `Stop`. Without a duration
(server start, periodic re-find) it runs until stopped. No SSDP or WS-Discovery.

## Following moved devices

OADM keeps a device managed when its address changes (`Oadm.Core.Devices.DeviceAddressService`):
- **By a task** (Network plugins after setting a static address): `ITaskExecutionContext.UpdateDeviceAddressAsync`.
  The server reads basicdeviceinfo at the new address with the stored credentials, scheme and pinned
  certificate; a different or missing serial throws `DeviceIdentityException` and the record stays. Otherwise
  the record's Address changes (credentials and pin kept, Unreachable becomes Unknown), the change is published
  and a full refresh queued. Logged ("Device X moved from A to B").
- **By the periodic re-find** (`Oadm.Server.Devices.DeviceRelocationHostedService`, `DeviceRelocationOptions`):
  every 5 minutes (first run 30 s after start), only while at least one device is Unreachable and addressed by
  IP, a 15 s zero-conf session looks for those serials; a device announced at another address is verified
  there the same way and moved (`TryRelocateAsync`). This covers DHCP changes, where the new address is unknown.
- **By a DHCP lease**: when the DHCP server plugin ACKs a lease to the MAC (= serial) of a
  managed device and the address differs from the record, the plugin calls `IDeviceAutoAdd.FollowAsync`
  (`ICorePluginContext.AutoAdd`, see [bundled-plugins.md](bundled-plugins.md#dhcp-server-core-plugin)): the server verifies the device at the leased address the same
  way and moves it right away, also while the device still counts as reachable (`TryRelocateAsync(..., requireUnreachable:
  false)`; a lease proves the change). Not verified yet (the device is still starting): one retry after 30 s. Logged
  and audited ("Device moved", user "system", "DHCP server: from A to B"). Works whether or not the automatic add is on.
- Devices addressed by a name (added by their DNS name or a typed host name) keep it; their record never moves.

## IP range scan

For every address in the range (parallelism from settings, default 32): try HTTPS 443 then
HTTP 80, `POST /axis-cgi/basicdeviceinfo.cgi` with
`{"apiVersion":"1.0","method":"getAllProperties"}`.
- `401` with `WWW-Authenticate` realm `AXIS_<serial>` -> Axis device, serial from realm.
- `200` (anonymous allowed) -> parse properties directly.
- Connection refused / timeout (default 1500 ms) -> not a device.
No ICMP, no ARP. Result goes into the same discovered list as mDNS, deduplicated by serial.
