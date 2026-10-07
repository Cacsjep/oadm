# PKI research: HTTPS server certificates, IEEE 802.1X, renewal (AXIS OS 10-12)

Research for the planned PKI core plugin and its task plugins (install CA-signed HTTPS certificate,
802.1X EAP-TLS, renew, disable). Collected 2026-10-07. Device facts were read (read-only) from the dev
camera 10.0.0.48, AXIS P3265-V, AXIS OS 12.11.77; raw responses are the fixtures in this folder.

Sources:
- REST Certificate Management (Device Configuration API): https://developer.axis.com/vapix/device-configuration/certificate-management/
- Legacy SOAP Certificate management: https://developer.axis.com/vapix/network-video/certificate-management-api/
- Network settings API 1.x (`network_settings.cgi`, `setWired8021XConfiguration`): https://developer.axis.com/vapix/network-video/network-settings-api/
- Network settings REST v2: https://developer.axis.com/vapix/device-configuration/network-settings-api/ (no 802.1X use case documented; schema from the device: `ondevice-network-settings-v2-doc.md`)
- param.cgi `Network.Interface.I0.dot1x`: https://developer.axis.com/vapix/network-video/network-settings/
- Soft factory default keeps 802.1X: https://developer.axis.com/vapix/network-video/system-settings/
- EST: https://developer.axis.com/vapix/device-configuration/enrollment-over-secure-transport-api/
- Virtual host: https://developer.axis.com/vapix/device-configuration/virtual-host/ ; crypto policy: https://developer.axis.com/vapix/device-configuration/cryptographic-policy-api/
- SOAP WSDL `acertificates`: https://www.axis.com/vapix/ws/certificates/CertificateService.wsdl (+ `http://www.axis.com/vapix/ws/cert/cert.xsd`)
- AXIS OS release notes: https://help.axis.com/download/release_notes_os_release_notes_t10228770_2510.pdf, archive https://help.axis.com/download/release_notes_os_release_notes_archive_t10202207_2606.pdf
- ADM manual: https://help.axis.com/en-us/axis-device-manager , https://help.axis.com/download/um_device_manager_t10211981_2508.pdf ;
  ADM HTTPS how-to: https://www.axis.com/dam/public/c8/f1/73/axis-device-manager-https-certificate-management-en-US-112301.pdf ;
  ACS 5 manual (same certificate engine, more detail): https://help.axis.com/en-us/axis-camera-station-5

## Summary / decision

| Step | AXIS OS >= 11.11 (REST `cert` v1 released; beta since 11.6) | AXIS OS 10.x - 11.5 fallback (SOAP, `/vapix/services`) |
|---|---|---|
| Generate key pair on device (key never leaves it) | `POST /config/rest/cert/v1/create_certificate` (self-signed, key in keystore) | `acertificates:CreateCertificate2` (self-signed) |
| Get CSR | `POST /config/rest/cert/v1/certificates/<alias>/get_csr` | `acertificates:GetPkcs10Request2` |
| Install CA-signed cert for that key | `PATCH /config/rest/cert/v1/certificates/<alias>` `{"data":{"certificate":PEM}}` | ONVIF `tds:LoadCertificates` with the same CertificateID (ONVIF onboard-key flow; not verified on hardware) |
| Install CA certificate (802.1X server trust, chain) | `POST /config/rest/cert/v1/ca_certificates` | ONVIF `tds:LoadCACertificates` |
| Select HTTPS certificate + connection policy | SOAP `aweb:SetWebServerTlsConfiguration` (no REST equivalent on 12.11) | same |
| 802.1X EAP-TLS config + enable/disable | `POST /axis-cgi/network_settings.cgi` `setWired8021XConfiguration` (network-settings 1.x) | SOAP `acertificates:SetCertSet` `EAPTLS_WIRED` + param.cgi `Network.Interface.I0.dot1x.*` |
| Renew | new `get_csr` on the same alias, `PATCH` the new cert (same key); or new alias, switch, delete old | `CreateCertificate2` new id, switch, delete old |
| Delete | `DELETE /config/rest/cert/v1/certificates/<alias>` / `ca_certificates/<alias>` | ONVIF `tds:DeleteCertificates` |

**Yes, the device generates the key pair and the CSR itself.** Recommended flow (Axis doc: "This is the recommended
and most secure way to install a certificate with private key, as the private key will never have existed outside
the device"): `create_certificate` -> `get_csr` -> sign in OADM's CA -> `PATCH certificate`. PKCS#12 / PEM key upload
exist but should be the exception.

## 1. Certificate management

### 1a. REST `cert` v1 (Device Configuration API)

Availability: added (beta) in AXIS OS 11.6 ("A new VAPIX REST API for certificate management has been added ...
CSR-generation of ECC-cryptography ... RSA-cryptography higher than 2048-bit ... select Secure Keystore (TPM, Secure
Element)"); "The beta tag is removed from the Certificate APIs in the Device Configuration API" in 11.11.135 (LTS 2024).
Not on 10.12 LTS. Discovery: **not** in `apidiscovery.cgi getApiList`; it is listed by `GET /config/discover`
(`apis.cert.v1.version` = "1.1.2" on 10.0.0.48; `v1beta` still answers too). Absent API: HTTP 404
`{"status":"error","error":{"code":1,"message":"Resource not found: ..."}}`.
All requests JSON, envelope `{"data": ...}`, reply `{"status":"success"|"error", "data"?, "error":{code,message}}`.
On 10.0.0.48 the REST answered on HTTPS with Basic (virtualhost: 443 basic, 80 digest).

Model (`ondevice-cert-v1-doc.md`, `ondevice-cert-v1-openapi.json`):
- `certificates` (key `alias`, 1..128 chars, client-defined free text incl. spaces and parentheses, e.g.
  `Axis device ID ECC-P256 (802.1AR)`): `certificate` (PEM, settable = replace with a cert for the same key),
  `keystore` (fixed after creation), `private_key` (write-only, only on add). Action `get_csr`.
- `ca_certificates` (key `alias`): `certificate` PEM (replace only with a cert for the same public key).
- `keystores` (read-only): `id`, `security_level` SOFTWARE | TRUSTED_ENVIRONMENT | STRONG, `key_types`
  (EC-P256, EC-P384, EC-P521, RSA-1024, RSA-2048, RSA-3072, RSA-4096), `certifications`.
- `settings.keystore`: default keystore for new keys (PATCH `/config/rest/cert/v1/settings/keystore`).
- Root actions: `create_certificate`, `install_from_pkcs12`.
- Formats: PEM only for certificates and keys ("supports all the common plain text RSA and EC private key PEM header
  formats"); PKCS#12 as base64 in `install_from_pkcs12`. No DER. No operation ever returns a private key.

Requests (Axis doc, PEM shortened):
```http
POST /config/rest/cert/v1/create_certificate
{"data":{"alias":"oadm-https","key_type":"EC-P256","keystore":"SE0",
         "subject":"CN=10.0.0.48","subject_alt_names":["IP:10.0.0.48","DNS:axis-b8a44f631339"],
         "valid_from":1681740370,"valid_to":1713276370}}
-> 200 {"status":"success"}
```
Only `alias` and `subject` (RFC 4514 string, e.g. `C=SE,CN=example.com`) are required; default key type "EC-P256
for most keystores"; default validity today .. +1 year (Unix seconds); SAN entries prefixed `IP:` / `DNS:`.
Errors: code 5 = key type not supported by the keystore ("Validation error: Failed to generate key pair: This key
algorithm is not supported on TPM"), code 100 = maintenance required before that key type can be generated.
(The web doc example uses `"key_type":"NIST-P256"`; the device enum is `EC-P256` - use the enum.)
```http
POST /config/rest/cert/v1/certificates/oadm-https/get_csr
{"data":{"subject":"CN=10.0.0.48","subject_alt_names":["IP:10.0.0.48"]}}
-> 200 {"status":"success","data":"-----BEGIN CERTIFICATE REQUEST-----\n..."}
```
All `get_csr` fields optional: omitted = copied from the existing certificate, empty string/list = left out of the CSR.
The openapi says `data` is a string (the web doc example wraps it in `"alias"`, which looks like a doc error).
```http
PATCH  /config/rest/cert/v1/certificates/oadm-https          {"data":{"certificate":"-----BEGIN CERTIFICATE-----\n..."}}  -> 200
POST   /config/rest/cert/v1/ca_certificates                  {"data":{"alias":"OADM Root CA","certificate":"-----BEGIN ..."}} -> 201
PATCH  /config/rest/cert/v1/ca_certificates/<alias>          {"data":{"certificate":"..."}}   (same public key)
POST   /config/rest/cert/v1/certificates                     {"data":{"alias":"x","certificate":"...","private_key":"...","keystore":"SE0"}} -> 201
POST   /config/rest/cert/v1/install_from_pkcs12              {"data":{"alias":"x","pkcs12":"<base64>","passphrase":"...","keystore":"SE0"}}
DELETE /config/rest/cert/v1/certificates/<alias>             DELETE /config/rest/cert/v1/ca_certificates/<alias>
GET    /config/rest/cert/v1  | /certificates[/<alias>] | /ca_certificates[/<alias>] | /keystores[/<id>] | /settings
```
Aliases go into the URL path: percent-encode (spaces, parentheses). Chain: AXIS OS 10.10 "Added support for full
certificate chains during TLS-based communication such as HTTPS, IEEE 802.1x"; whether a PEM chain is accepted in
the PATCH is not documented - verify on hardware, else install the intermediate as a CA certificate.

Keystores: `SE0` = secure element (Axis Edge Vault, "CC EAL6+"), `TEE0` = trusted execution environment
(selectable since 11.9), `TPM0` on TPM models (no RSA-4096 per doc example). Devices without such hardware have a
SOFTWARE keystore. Deleting a certificate always deletes its key (securely wiped in hardware keystores).

### 1b. EST (`cert-est` v1, AXIS OS 12.10+, RFC 7030)
`/config/rest/cert-est/v1/profiles` {id, server (`192.168.0.1:443/.well-known/est`), certificate (client cert alias
for mutual TLS to the EST server, e.g. the 802.1AR device ID), caCertificates (EST server trust; "Pre-configure the
device with the EST server's root certificate", else `/cacerts` is trusted on first use), services (WEBSERVER,
NETAUTH, MQTT, RTSPS), subject {cn}, subjectAlternativeNames}; status UNCONNECTED/CONNECTED/ENROLLED. The device
enrolls, renews and assigns the certificate to the services itself. Later option: OADM as EST server.
10.0.0.48 has no profile (`rest-cert-est-v1-get.json`).

### 1c. Legacy SOAP (still answered on 12.11)
`POST /vapix/services`, `Content-Type: application/soap+xml; charset=utf-8`, SOAP 1.2 envelope
(`http://www.w3.org/2003/05/soap-envelope`), digest on HTTP. Two services share the same certificate store
(SOAP ids = REST aliases):
- Axis `acertificates` (`http://www.axis.com/vapix/ws/certificates`, types `acert` = `http://www.axis.com/vapix/ws/cert`):
  `CreateCertificate2` (Id, Subject {C, ST, L, O, OU, CN, serialNumber, ...}, ValidNotBefore, ValidNotAfter; reply
  Id + PEM), `GetPkcs10Request2` (Id, optional Subject; reply Pkcs10Request), `LoadPkcs12` (Id, base64 Certificate,
  Password), `ListCertSets`, `GetCertSet(CertSetName)`, `SetCertSet(CertSetName, CertSet{Certificates/Id,
  CACertificates/Id, TrustedCertificates/Id})`, `SetCRL`, `GetCRL`. Faults: CertificateIdInUseFault,
  MaxNumberOfCertificatesFault, KeyPairGenerationFault, Pkcs10GenerationFault, CertificateFormatFault, ...
  `acertificates:GetCertificates` / `GetCACertificates` do not exist (SOAP fault ActionNotSupported, `*-fault.xml`).
- ONVIF device service (`tds` = `http://www.onvif.org/ver10/device/wsdl`): `GetCertificates`, `GetCACertificates`,
  `GetCertificatesStatus`, `LoadCertificateWithPrivateKey` (base64 in `tt:Data`), `LoadCACertificates`,
  `LoadCertificates`, `DeleteCertificates(CertificateID)` (client or CA). `GetServiceCapabilities` on 10.0.0.48:
  `Security OnboardKeyGeneration="true" TLS1.2="true"`, no Dot1X capability.

```xml
<acertificates:CreateCertificate2>
  <acertificates:Id>oadm-https</acertificates:Id>
  <acertificates:Subject><acert:C>SE</acert:C><acert:CN>172.25.154.47</acert:CN></acertificates:Subject>
  <acertificates:ValidNotBefore>2020-10-16T04:08:32</acertificates:ValidNotBefore>
  <acertificates:ValidNotAfter>2040-10-16T04:08:32</acertificates:ValidNotAfter>
</acertificates:CreateCertificate2>
```
Cert sets on 10.0.0.48 (`soap-acertificates-ListCertSets.xml`): EAPTLS_WIRED, EAPTLS_WIRED_SERVER, EAPTLS_WIFI,
EAPPEAP_MSCHAPV2_WIFI, EAPPEAP_MSCHAPV2_WIRED, AUDIOSITE, MediaPlayerClient, NTS, AVHS, SIP, Streaming_RTSPS,
RemoteSyslog, StreamingMediaClient, **WebServer**, StreamingDeviceEventClient, mqtt_client, O3C.
"Centralized certificate management" (one store for HTTPS and 802.1X, preinstalled CAs) exists since AXIS OS 10.0.

## 2. HTTPS

- Select the web server certificate: SOAP `aweb:SetWebServerTlsConfiguration` (`http://www.axis.com/vapix/ws/webserver`).
  No REST web server API on 12.11 (`/config/rest/web-server/v1` = 404; `virtualhost` v1 only has ports and auth types).
  Read with `aweb:GetWebServerTlsConfiguration` (or `GetCertSet WebServer`). Write (Axis doc; for AXIS OS 7.10 and
  later only `Admin` in ConnectionPolicies; "the ciphers listed ... are subject to change with AXIS OS updates":
  send back the list just read):
```xml
<aweb:SetWebServerTlsConfiguration>
  <Configuration>
    <Tls>true</Tls>
    <aweb:ConnectionPolicies><aweb:Admin>HttpAndHttps</aweb:Admin></aweb:ConnectionPolicies>
    <aweb:Ciphers><acert:Cipher>ECDHE-ECDSA-AES128-GCM-SHA256</acert:Cipher>...</aweb:Ciphers>
    <aweb:CertificateSet>
      <acert:Certificates><acert:Id>oadm-https</acert:Id></acert:Certificates>
      <acert:CACertificates/><acert:TrustedCertificates/>
    </aweb:CertificateSet>
  </Configuration>
</aweb:SetWebServerTlsConfiguration>
```
  Connection policy values: `HttpAndHttps` (seen on the device), `Https` (doc example, = HTTPS only), `Http`
  (inferred; no schema is published). Certificate only: `SetCertSet` with name `WebServer` (not verified as a write).
- param.cgi `HTTPS.Enabled` (yes|no), `HTTPS.Port` (1..65535, 443), `HTTPS.Ciphers` (colon list) still exist on 12.11
  (`paramcgi-listdefinitions-dot1x-https.xml`); `Network.HTTP.AuthenticationPolicy` = basic | digest | basic_digest |
  recommended. `System.BoundRequest` does not exist (fixture with the param error).
- HTTPS with a self-signed certificate is on by default since firmware 7.20 / AXIS OS 10 ("HTTPS enabled by default").
  ACS: ports other than 443 are not supported. ACS "Security > HTTPS > Disable" = the VMS connects over HTTP again.
- Safe order for OADM: create -> CSR -> sign -> PATCH -> SetWebServerTlsConfiguration -> reconnect, compare the served
  fingerprint with the new cert -> update OADM's pinned fingerprint (else the TOFU pin flags CertificateChanged) ->
  only then (optionally) policy `Https`.

## 3. IEEE 802.1X

### 3a. network-settings 1.x JSON (preferred write path; 10.0.0.48 has `network-settings` 1.37)
Read: `POST /axis-cgi/network_settings.cgi {"apiVersion":"1.37","method":"getNetworkInfo"}` ->
`data.devices[].wired.8021X` {enabled, status (Stopped | Unauthorized | Authorized | Connecting | UNKNOWN), mode,
configurations[{mode, params{identity, eapolVersion, certClient, certsCA[], ...}}], supportedModes, MACsecSecured}.
Feature detection: the `8021X` object of the wired device ("only available on wired interface devices that have
indicated support for wired 802.1X in getNetworkInfo"); the minimum 1.x minor version is not documented.
Write:
```json
POST /axis-cgi/network_settings.cgi
{"apiVersion":"1.0","method":"setWired8021XConfiguration",
 "params":{"deviceName":"eth0","enabled":true,"mode":"WPA-Enterprise-EAPTLS","identity":"axis-b8a44f631339",
           "eapolVersion":"EAPoLv3","certClient":"oadm-8021x","certsCA":["OADM Root CA"]}}
-> {"apiVersion":"1.0","method":"setWired8021XConfiguration","data":{}}
```
All params except deviceName optional ("If excluded, the value remains unchanged"); `certClient` "Required to
enable 802.1X authentication with WPA-Enterprise-EAPTLS", `""` unsets it; `certsCA: []` unsets the CA list.
Disable: `{"deviceName":"eth0","enabled":false}`. Modes: `WPA-Enterprise-EAPTLS`, `WPA-Enterprise-PEAP-MSCHAPv2`
(+ `password`, `peapVersion`, `label`; PEAP since AXIS OS 11.6), `MACsec-PSK` (`mkaCAK`, `mkaCKN`).
EAPoL: `EAPoLv1|EAPoLv2|EAPoLv3`. Errors: 4001 version not supported, 4004 invalid parameter (subCode 100 deviceName,
101 enabled, 102 mode, ...). Error replies are HTTP 200 with an `error` object.

### 3b. network-settings v2 REST (12.x; read only for OADM)
`GET /config/rest/network-settings/v2/wired` -> `auth{mode NONE|WPA_PASSPHRASE|WPA_PSK|EAP_TLS|EAP_MSCHAPV2|MACSEC_PSK,
status UNKNOWN|AUTHENTICATED|AUTHENTICATING|STOPPED|FAILED, macsecStatus, configs{eapTls{caCerts[], clientCertId,
eapolVersion 1..3, identity 1..32 chars}, mschapv2{...}, macsecPsk{ckn}}}`; PATCH on `.../wired/auth/configs/eapTls`
and `.../wired/auth/mode`. There is no `enabled` flag: on 10.0.0.48 `mode` is `EAP_TLS` while param
`dot1x.Enabled=no` and status STOPPED, so v2's enable semantics are unclear -> use 3a for writes. v2 limits the
identity to 32 characters; param.cgi / 1.x allow 128 (since 10.7).

### 3c. Legacy (no 8021X in getNetworkInfo): param.cgi + SOAP cert set
`Network.Interface.I0.dot1x.Enabled` yes|no, `.EAPOLVersion` 1|2|3 (default 3 since 10.1), `.Status` (read-only:
Stopped | Unauthorized | Authorized | UNKNOWN), `.EAP.MultiAuthEnabled` yes|no, `.EAPTLS.Identity` (max 128),
`.EAPTLS.PrivateKeyPassword` (write-only, max 16; legacy). Client cert + CA via `SetCertSet` `EAPTLS_WIRED`
{Certificates/Id = client alias, CACertificates/Id = CA aliases}. The Axis doc also shows ONVIF
`tds:SetDot1XConfiguration` (token `EAPTLS_WIRED`, "the CertificateID needs to be the certificate common name");
on 12.11 `GetDot1XConfigurations` answers a SOAP Sender fault -> do not use. ACS: "Devices that don't have the
parameter Network.Interface.I0.dot1x.Enabled aren't supported" (e.g. AXIS P39, T85, T87).

### 3d. Behaviour and risk
- AXIS OS 10.1: EAPoL v3 (802.1X-2010) default; 802.1X without CA certificate allowed ("authenticate on any network,
  not just the trusted one"); default identity `axis-<serial>`; "IEEE 802.1x is enabled per default on Axis products
  that support AXIS Edge Vault" (802.1AR device ID). 10.11: multiple CA certificates. 11.8: MACsec (PSK and dynamic
  CAK/EAP-TLS). 11.6: PEAP-MSCHAPv2.
- On a switch port without 802.1X/RADIUS the port is uncontrolled: EAPOL gets no answer, traffic flows, status stays
  not Authorized - no lock-out. The risk is a controlled port where authentication fails (wrong/missing CA, client
  cert not trusted by RADIUS, identity mismatch, device clock wrong so the cert is "not yet valid"): the device is
  cut off. ACS: "Before enabling IEEE 802.1X, make sure the time on the Axis devices is synchronized."
  Recovery: on the switch (fallback VLAN, disable port control) or hard factory default; the soft
  `factorydefault.cgi` **keeps** "The IEEE 802.1X settings". The plugin must warn (HARD RULE no lock-out), check the
  device time first and verify reachability after enabling.

## 4. API discovery ids

`apidiscovery.cgi getApiList` on 10.0.0.48 (`apidiscovery-getApiList.json`), relevant: `network-settings` 1.37,
`param-cgi` 1.0, `api-discovery` 1.1, `basic-device-info` 1.3 (`custom-firmware-certificate` 1.0 is firmware
signing, unrelated). There is **no** getApiList id for certificates, the web server, SOAP or dot1x. REST APIs appear
only in `GET /config/discover` (`config-discover.json`): `cert` v1 1.1.2, `cert-est` v1 1.0.1, `crypto-policy` v1
1.1.0, `network-settings` v2 2.0.3, `virtualhost` v1 1.2.0, `firewall` v1, `oauth-ccgrant` (beta), `oidcsetup`, ...
Compatibility rules for OADM:
- REST cert path when `config/discover` lists `cert.v1` with state released; else the SOAP path (AXIS OS 10+; probe
  with `acertificates:ListCertSets`).
- 802.1X write via 3a when `network-settings` 1.x is listed and getNetworkInfo has `wired.8021X`; else 3c when the
  parameter `Network.Interface.I0.dot1x.Enabled` exists; else not supported.
- The SDK `DeviceApi` list (apidiscovery) cannot express REST APIs: the plugin needs its own `config/discover` probe
  (cached per full refresh, or read at task start).

## 5. What 10.0.0.48 reports (AXIS P3265-V, AXIS OS 12.11.77)

- Keystores: `TEE0` TRUSTED_ENVIRONMENT (RSA-2048/4096, EC-P256/384/521), `SE0` STRONG "CC EAL6+" (also RSA-1024);
  default `SE0` (`rest-cert-v1-keystores.json`, `rest-cert-v1-settings.json`).
- Certificates (all keystore SE0): `Axis device ID ECC-P256 (802.1AR)`, `Axis device ID RSA-2048 (802.1AR)`,
  `Axis device ID RSA-4096 (802.1AR)` (factory IDevIDs, CN `axis-b8a44f631339-eccp256-1` etc., valid until 9999,
  issuer "Axis device ID Intermediate CA ECC 2 / RSA 2"), and `Trustlix Device HTTPS lUZuIXuu` (CN
  axis-b8a44f631339, issuer "Trustlix Root CA", SAN DNS axis-b8a44f631339, localhost, IP 127.0.0.1, 10.0.0.48,
  192.168.0.90, EKU serverAuth, valid 2026-08-02 .. 2028-11-04), installed by another tool.
- CA certificates: 33 preinstalled public roots (COMODO, Amazon, GlobalSign, DigiCert, Entrust, ISRG Root X1,
  USERTrust, Axis device ID roots and intermediates) plus `[CA]_cde32664935743c699b6bd14ca3` = "AXIS Camera Station
  root certificate" (ACS's alias scheme for CAs it installs).
- Web server: `GetWebServerTlsConfiguration` -> Tls true, ConnectionPolicies Admin `HttpAndHttps`, 6 ECDHE ciphers,
  certificate `Trustlix Device HTTPS lUZuIXuu` (= the certificate served on 443, SHA-256 00:4D:BF:FE:...:6F:A6).
  `GetCertificatesStatus`: only that certificate has `Status=true`. param `HTTPS.Enabled=yes`, `HTTPS.Port=443`.
- virtualhost: default-http port 80 digest, default-https port 443 basic.
- 802.1X: `dot1x.Enabled=no`, `EAPOLVersion=3`, `Status=Stopped`, `EAP.MultiAuthEnabled=no`, `EAPTLS.Identity=48`;
  getNetworkInfo: mode WPA-Enterprise-EAPTLS, certClient `Axis device ID ECC-P256 (802.1AR)`, certsCA [], supported
  modes PEAP-MSCHAPv2, EAPTLS, MACsec-PSK. Cert sets: EAPTLS_WIRED = device ID ECC; EAPTLS_WIRED_SERVER = device ID
  ECC + Axis device ID root CAs; Streaming_RTSPS = device ID ECC.
- crypto-policy: DEFAULT (FIPS 140-3 selectable). EST: no profiles; supported services MQTT, NETAUTH, WEBSERVER, RTSPS.

## 6. ADM (and ACS 5, same feature) behaviour to mirror

ADM manual (2508) "Install certificates":
- Configuration > Security > Certificates: **Certificate authority** Generate... (self-signed root + key protected by a
  passphrase; "A certificate generated by AXIS Device Manager will last for 3 years"; **Remember passphrase** is needed
  for automatic renewal), Import (certificate + private key + passphrase; ADM then acts as intermediate CA), Save to
  file (.cer/.crt, public only), Backup (certificate + key, passphrase protected).
- **Certificate expiration warning**: days before expiry; system alarm, Status column, icons in the "View installed
  certificates" dialog, Configuration workspace. "By default, AXIS Device Manager-generated server and client
  certificates will be automatically renewed seven days before the expiration warning is set to appear" (a nightly
  job, ADM HTTPS how-to).
- HTTPS: **Common name** "Device IP address or Device host name (FQDN)" (ADM how-to). Context menu **Security > HTTPS >
  Enable/Update** and **Disable**; manual path "Install server certificates for each device"; "There can be only one
  server certificate present on each device before enabling HTTPS. Excess certificates can be deleted from the context
  menu." Option **Ignore certificate validation** (on by default in older ADM).
- 802.1X: **EAPOL Version**; **EAP identity**: the device's MAC address, the device host name or Custom (text);
  **IEEE 802.1X authentication CA certificate** Import / View (installed on the devices when enabling or updating
  802.1X); **Common name**: Device EAP identity or Device IP address ("If the custom field is left empty, the host
  name will be selected. If there is an issue with the host name, the IP address will be used as the common name").
  Context menu **Security > IEEE 802.1X > Enable/Update** and **Disable**; "install client certificates for each
  device"; only one client certificate before enabling.
- "The CA instructs the devices to create certificates using their own private keys, sign them and then install them"
  (= on-device key + CSR, section 1).
- ACS 5 adds: "Number of days the signed client/server certificates will be valid for", maximum 1095 days, never
  beyond the CA's own expiry; ACS CA valid 10 years (ADM: 3 years); "Certificates..." in the context menu = overview,
  delete, details ("When the same certificate is installed on several devices, it appears as one item. Deleting it
  removes the certificate from all devices it's installed on"); status columns HTTPS On / Off / Unknown / Unsupported
  firmware / Unsupported device, IEEE 802.1X Enabled / Disabled / Unsupported firmware / Unsupported device; renewal
  "7 days before the expiration warning appears", only while HTTPS or 802.1X is on; "Certificate operations over
  unencrypted channels, such as Basic aren't supported"; only the first network adapter gets 802.1X.
- Not found in the current manuals: a default for the validity days (365) or for the expiration warning days. Treat
  365 days validity and a 30-day warning as OADM defaults unless confirmed in ADM itself.

## Fixtures

`apidiscovery-getApiList.json`, `config-discover.json`, `rest-*.json` (GET responses), `ondevice-*-doc.md` /
`*-openapi.json` (API docs served by the device at `/config/discover/apis/<api>/<ver>/`; the network-settings v2
openapi is omitted for size: `GET /config/discover/apis/network-settings/v2/openapi.json`), `soap-*.xml` (SOAP Get /
List responses; `*-fault.xml` = unsupported operations), `paramcgi-*.txt|xml`, `network_settings-getNetworkInfo.json`.
Every request was read-only; the files contain no secrets (the device masks `PrivateKeyPassword`).
