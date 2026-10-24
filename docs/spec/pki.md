# PKI, the certificate control center

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.
Agreed 2026-10-24 (planned on branch `feature/pki-center`). The plugin's details: `docs/spec/bundled-plugins.md` (PKI) and
`plugins/Oadm.Plugins.Pki/README.md`.

**Goal (decision 2026-10-24):** PKI is the one place for certificates and certificate authorities. Services that use
certificates (EST server, 802.1X server, later others) keep no CA or certificate settings of their own: they **select**
from what PKI holds and say where they use it. Later stages add creating certificates by hand, inspecting, deleting and
importing more of them in PKI.

## Certificate authorities (PKI page)

- **One list of CAs** (card "Certificate authorities" on the PKI page, with the existing CA card's actions):

  | Kind | Example | Issues certificates | Source |
  |---|---|---|---|
  | OADM CA | "OADM CA" | yes (the current one) | generated or imported, as today |
  | Previous OADM CA | "OADM CA 2025" | no (kept while its certificates live) | a replaced OADM CA |
  | Axis device ID | "Axis device ID" | no | built in (the RSA and ECC roots and intermediates EST ships, `plugins/Shared/DeviceId`) |
  | Imported CA | "Contoso Root" | no | **Import CA** (a root or intermediate certificate, PEM or DER; no key) |

- Each row: name, kind, valid until, fingerprint (SHA-256, in the details), **Used by** ("802.1X server, EST server" or
  "-"); **View** (the shared `ui:CertificateWindow`), **Remove** (imported CAs only; refused while a service uses it:
  "Used by the 802.1X server. Remove it there first."). The trusted root store of the server (today's "Trusted" card)
  stays as it is; it is not the same thing (that store is what OADM trusts when it connects to devices).
- Kept in PKI's settings and records, part of the backup; changes audited; managing needs "Manage the certificate
  authority" and all devices.

## Services select CAs

- SDK (0.4.0 default members on `IDeviceCertificateAuthority`, or a companion service PKI offers): `Authorities` (the
  list: id, name, kind, certificate DER, valid until), `Changed`; `UsedBy(serviceId, ids)` so PKI shows "Used by".
- **802.1X server:** "Trusted CAs" — check boxes over PKI's list; per CA the option **Only devices OADM manages** (on by
  default; off lets any device with a valid certificate of that CA in, e.g. PCs from a company CA). Default after
  install: the OADM CA (and its previous ones) and Axis device ID, both "only managed". See `radius-server.md`.
- **EST server:** which CAs a camera may sign in with (default: Axis device ID, and the OADM CA for renewals) and which
  CA issues (the OADM CA; a selector, ready for more issuing CAs later).
- A CA a service uses cannot be removed from PKI; a CA that leaves PKI's list (expired previous CA) drops out of the
  services' selections at once (fail closed: nothing passes through it any more).

## Clock check before installing a certificate

Decision 2026-10-24: a camera whose clock is behind sees a new certificate as "not valid yet".

- Every certificate the OADM CA issues to a device starts **5 minutes in the past** (`NotBefore = now - 5 min`), which
  covers small drifts silently (PKI tasks and EST).
- The dialogs of **Enable HTTPS**, **Enable IEEE 802.1X** and EST's **Set up** read the camera's clock first (read-only
  query, `time.cgi` / the HTTP Date header as today). If the camera is still more than 5 seconds behind the
  certificate's start, they ask before starting: "The camera's time is 9 min 12 s behind. The certificate becomes valid on
  the camera only in 4 min 12 s. Set the camera to use NTP. Continue?" **Yes** / **No**. Several cameras: one question
  listing how many and the largest drift. The task gets the user's answer and does not ask again; a camera that is ahead
  is fine. The dialogs read at most the first 100 selected cameras (3 seconds each); the tasks check every camera.
- This replaces the 802.1X task's hard refusal at more than 5 minutes off. A run without the answer (no dialog) still
  refuses a camera behind the certificate's start ("Nothing was changed").

## Stages

1. PKI's CA list (OADM, previous, Axis device ID, imported; Used by), the SDK members, 802.1X "Trusted CAs" with "Only
   devices OADM manages" per CA, EST's selection.
2. The clock check and the 5-minute start.
3. Later: certificates created by hand, inspected, deleted and imported in PKI.
