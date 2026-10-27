<p align="center">
  <img src="icon/oadm-app-icon-256.png" alt="OADM" width="128" height="128">
</p>

<h1 align="center">OADM - Open AXIS Device Management</h1>

<p align="center">Open source alternative to AXIS Device Manager for Windows, Linux and macOS.</p>

<p align="center">
  <a href="https://github.com/Cacsjep/oadm/releases/latest"><img src="https://img.shields.io/github/v/release/Cacsjep/oadm" alt="Latest release"></a>
  <a href="https://oadm.app/"><img src="https://img.shields.io/badge/docs-oadm.app-6C5CE7" alt="Docs"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue" alt="License: Apache-2.0"></a>
  <a href="https://acs-dev.org/"><img src="https://img.shields.io/badge/web-acs--dev.org-6C5CE7" alt="Website: acs-dev.org"></a>
  <a href="https://www.linkedin.com/in/christoph-acs-70150795/"><img src="https://img.shields.io/badge/LinkedIn-Christoph-0A66C2?logo=linkedin&amp;logoColor=white" alt="LinkedIn"></a>
  <a href="https://discord.gg/j4FNjg5VvK"><img src="https://img.shields.io/badge/Discord-join-5865F2?logo=discord&amp;logoColor=white" alt="Discord"></a>
</p>

**OADM is an independent open source project. Not affiliated with or endorsed by Axis Communications AB. AXIS is a trademark of Axis AB.**

OADM manages Axis cameras, speakers, intercoms and I/O devices like AXIS Device Manager: find and add devices, upgrade
firmware, set users, network, date and time, certificates and applications on many devices at once, with one task per
device. A server runs as a service and manages up to 30,000 devices; the client connects to it from any computer. The
user guide is at [oadm.app](https://oadm.app/).

## Install

Download the installer for your system from the [latest release](https://github.com/Cacsjep/oadm/releases/latest).
The installers are not signed yet; check a download against `SHA256SUMS.txt` of the release.

| System | Installer |
|---|---|
| Windows x64 | `OADM-<version>-win-x64.msi` |
| Debian, Ubuntu (x64, ARM64) | `oadm-server_*.deb` and `oadm-client_*.deb`: `sudo apt install ./oadm-server_*.deb ./oadm-client_*.deb` |
| macOS | `OADM-<version>-osx-x64.pkg` (not signed yet: right-click it and choose **Open**) |

Every installer offers **Server and client** (default) or **Client only**, for a computer that connects to a server
elsewhere. With the server, the service starts now and on every boot. Details per system, the data folders and how to
uninstall: [Install](https://oadm.app/start/install/).

## First start

1. Start the client. It connects to `localhost:5080`; for another computer enter `<server>:5080`.
2. On the first connection the client asks **Trust this server?**: click **Trust server**.
3. While the server has no users, the login window creates the first administrator. From another computer you also
   need the setup code from `setup-code.txt` in the server's data folder.
4. Add the passwords you use on the **Credentials** page, then add devices with **Add** on the Devices page.

More: [First start](https://oadm.app/start/first-start/), [Ports, data and backup](https://oadm.app/admin/ports-and-data/),
[Logs and troubleshooting](https://oadm.app/admin/troubleshooting/).

## For developers

- [CONTRIBUTING.md](CONTRIBUTING.md): build, run and test OADM from this repository, and the rules for changes.
- [Developer guide](https://oadm.app/dev/overview/): write your own plugins with the NuGet packages, no checkout needed.
- [`CLAUDE.md`](CLAUDE.md) and [`docs/spec/`](docs/spec/): the spec and the coding rules.

## License

Apache-2.0, see [LICENSE](LICENSE). The OADM PRO parts of PKI, the EST server and the 802.1X server (`Est/` and `Radius/`
in `plugins/Oadm.Plugins.Pki` and `plugins/Oadm.Plugins.Pki.Client`), have their own source-available licenses, see
[plugins/Oadm.Plugins.Pki/Est/LICENSE](plugins/Oadm.Plugins.Pki/Est/LICENSE) and
[plugins/Oadm.Plugins.Pki/Radius/LICENSE](plugins/Oadm.Plugins.Pki/Radius/LICENSE). Third-party components:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Every installer ships the full `THIRD-PARTY-NOTICES.txt`; the client
shows it on the **About** page.
