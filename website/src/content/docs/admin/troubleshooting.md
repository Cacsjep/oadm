---
title: Logs and troubleshooting
description: Where the logs are and what to check when something does not work.
---

## Logs

Logs roll daily or at 20 MB (server) / 10 MB (client). OADM keeps the last 14 days and at most 30 files.

| | Server log (`oadm-server-<date>.log`) | If the server fails before its log starts |
|---|---|---|
| Windows | `%ProgramData%\OADM\logs\` | Event Viewer, Windows Logs > Application (source ".NET Runtime") and System (Service Control Manager) |
| Linux | `/var/lib/oadm/logs/` | `journalctl -u oadm-server` |
| macOS | `/Library/Application Support/OADM/logs/` | `/Library/Logs/OADM/oadm-server.err.log` |

The client writes `client-<date>.log` to `%LOCALAPPDATA%\Oadm\logs`, `~/.local/share/Oadm/logs` or
`~/Library/Application Support/Oadm/logs`. The **Logs** page shows the client log live. Client error messages name
the log file.

## Common problems

**The client cannot connect.**
Check that the service runs (`OADM Server` in Windows services, `systemctl status oadm-server`) and that TCP 5080 is
open on the server's firewall.

**"The server certificate changed".**
The server got a new certificate, for example after a reinstall with a new data folder. If you expect that, click
**Forget server** and confirm the new fingerprint.

**Devices show Credentials required.**
The device rejects the stored password. Right-click > **Log in** and enter the right one for all of them.

**Devices show Certificate changed.**
The device presents another HTTPS certificate than when it was added. OADM sends nothing to it. Find out why, then
remove the device and add it again.

**NTP or DHCP server: port in use.**
Another program holds the port. The page names it when it can, for example the Windows Time service. See
[NTP server](../../tools/ntp-server/) and [DHCP server](../../tools/dhcp-server/).

**A task failed with "Nothing was changed".**
The device does not support it, for example its AXIS OS is too old. The task's **Details** show the step and the reason.

Still stuck? [Open an issue](https://github.com/Cacsjep/oadm/issues/new/choose) with the OADM version and the log lines.
