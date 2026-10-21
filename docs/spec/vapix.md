# Device communication (VAPIX)

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

- Auth: AXIS OS 12 uses Digest on port 80 and Basic on port 443 (seen in
  `/config/rest/virtualhost/v1` and the 401 headers). The client offers Digest only over
  HTTP and Basic or Digest over HTTPS. Basic is never sent over plain HTTP, with one exception: the add
  page's automatic login to a device that passed the anonymous Axis check (`AllowBasicOverHttp`, see
  "Security and access" 2).
- Factory-default check without credentials: `systemready.cgi` returns `needsetup` (yes =
  no admin user yet) and `passphrasepolicy` (none, length, complex).
- Prefer HTTPS, fall back to HTTP if 443 is closed. Store which scheme worked.
- Certificates: trust on first use. Store SHA-256 fingerprint on add. A later mismatch sets
  status `CertificateChanged` and blocks tasks until the user accepts the new certificate.
- Certificate info: the TLS callback (`CertificatePinning`) also describes every presented
  certificate with a separate `X509Chain` build (system trust store, `RevocationMode.NoCheck`,
  no downloads, validity dates ignored) into `VapixClient.ObservedCertificate`; pinning and
  TOFU decisions are unaffected. Expiry is applied at refresh time (`TrustAt(now)`). Captured
  on add and on every full refresh. Devices OADM reaches over HTTP (e.g. a port forward of port 80 only, HTTPS not reachable)
  have no handshake to observe: the full refresh reads the web server certificate over VAPIX instead
  (`WebServerCertificateReader`: SOAP `GetWebServerTlsConfiguration` at `/vapix/services` names it and its CA certificates,
  REST cert v1 `certificates/<alias>` and `ca_certificates/<alias>` return the PEM; AXIS OS 11.11+, read-only, verified on
  the development camera) and rates it the same way (nothing is pinned); cleared when HTTPS is off or the API is missing. Cross-platform APIs only.
- **Old AXIS OS** (verified read-only on an AXIS Q6154-E with 8.40.104;
  `tests/Oadm.Server.Tests/Hardware/OldFirmwareHardwareTests`, opt-in with `OADM_OLD_CAMERA_URL` / `_USER` / `_PASSWORD`):
  no `apidiscovery.cgi` (404, it came with 8.50), no anonymous `getAllUnrestrictedProperties` (401: the Axis check uses the
  realm, see "Security and access" 2), no `systemready.cgi`, `time.cgi`, `ntp.cgi`, `/config`; `network_settings.cgi`
  without `getNetworkInfo`. Authenticated `basicdeviceinfo` (incl. Soc), `param.cgi`, `firmwaremanagement.cgi` (1.0),
  `pwdgrp.cgi`, `applications/list.cgi` work. `VapixClient.GetApiListAsync` answers a 404 with `LegacyApiList` from the
  `param.cgi` group `Properties` (param-cgi 1.0 always, fwmgr from `Properties.FirmwareManagement.Version`, application
  when `Properties.EmbeddedDevelopment.Version`, user-management when `Properties.API.Browser.UserGroup=yes`; status "from
  parameters"); an API list without "param-cgi", "application" or "user-management" gets them the same way from one read
  of `Properties` (VAPIX identifies these by parameters, not by the API list; an AXIS OS 11.9 camera did not list
  "application"), so Upgrade firmware, Users, Applications and (through their param.cgi fallbacks) Network settings and Date
  and time run; PKI (11.11) and Device configuration (12.5) stay greyed with their reason. Writes on such firmware are not
  tested yet; the discovered row has no model (no anonymous properties); a factory-default 8.x device is not recognized
  (no systemready).
- Endpoints of the core:
  - `basicdeviceinfo.cgi` -> SerialNumber, ProdNbr, ProdShortName, Version, HardwareID
  - `param.cgi?action=list&group=Network.BootProto,Network.UPnP.FriendlyName,Network.Interface.I0.dot1x.Enabled,HTTPS.Enabled`
    -> DHCP, UPnP name, 802.1X, HTTPS (`HTTPS.Enabled=yes|no` still exists on 12.x)
  - `pwdgrp.cgi` action add, user root, grp root, sgrp admin:operator:viewer:ptz
    -> first password on a factory-default device. Works over HTTP without credentials, but
    we send it over HTTPS when available and always with arguments in the POST body, never
    in the URL. Since AXIS OS 11.5 the first user does not have to be named root.
  - `restart.cgi` -> Restart task plugin
  - `param.cgi?action=list&group=Properties.Image,Image` -> live view codecs
    (`Properties.Image.Format`, e.g. `jpeg,mjpeg,h264,h265`), resolutions and video sources
  - RTSP `rtsp://<host>/axis-media/media.amp?videocodec=h264|h265&camera=<n>&resolution=WxH&fps=N&videokeyframeinterval=N&audio=0`
    on port 554 -> live view (see "Live view")
- All VAPIX access lives in `Oadm.Core.Vapix.VapixClient`, one method per endpoint,
  unit-tested against recorded responses in `tests/.../Fixtures`.
- **Routing** (see "# Sites"): every device connection opens through
  `Oadm.Core.Sites.DeviceRouter` on the device's `DeviceRoute` (`Device.SiteId`, `DiscoveredDevice.Route`): the server's
  own network = a normal socket connect, a site = a new `ISiteTransport.OpenStreamAsync` stream per connection.
  `VapixClient` runs on `SocketsHttpHandler` (router connect callback, pinning via `CertificatePinning.ValidateSslCallback`;
  WebSockets through the same handler); `VapixDeviceProbe` keeps one handler per route (a pool keyed by host and port only
  would share a connection between two sites with the same IP) and reads the route from `DeviceRouter.RouteOption`;
  `RtspClient.ConnectAsync(..., route, router)` keeps the per-device RTSP port. `DeviceConnections.For(device |
  discovered)` is the only place that builds `VapixConnectionOptions` (test `RoutingRulesTests`). The factory's cache key
  holds the route; a change of route or address drops the cached client. Through a site: at most 2 connections per
  device plus 2 for the status reads (basicdeviceinfo: the poll never waits behind an upload or a page), 30 s idle; every request timeout + 4 x the site's round trip; never Basic over plain HTTP; a `SiteException`
  passes through unchanged; a WebSocket upgrade on a new connection counts as a permanent dial. Names resolve on the route
  (`RoutedDns`; through a site in stream 5). With no site everything connects as before.
- **Banned APIs**: `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `src/Oadm.Plugin.Sdk/targets/BannedSymbols.txt`
  (Socket, TcpClient, UdpClient, Ping, Dns, HttpClientHandler, SocketsHttpHandler, ClientWebSocket.ConnectAsync): errors in
  src/ and plugins/ (`OadmBannedApis=error`), warnings RS0030 in every plugin built with Oadm.Plugin.Sdk
  (`OadmBannedApis=warning`, `none` turns it off; version `OadmBannedApiAnalyzersVersion` in Oadm.Plugin.Versions.props);
  tools/ and tests/ outside. Exceptions carry a SuppressMessage with a justification and are listed in
  `RoutingRulesTests.AllowedBannedApiUsers` (`AddressProbe`, `NtpServerService`, `UseOadmNtpServerTask` until they use
  `IDeviceNetwork`).
