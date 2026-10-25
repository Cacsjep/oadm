---
title: Ports, data and backup
description: Network ports, the data folder and what to back up.
---

## Ports

| Port | Used by |
|---|---|
| TCP 5080 | Client to server (encrypted, login required) |
| TCP 5081 | [Site nodes](../../sites/overview/) to server, only while **Accept site nodes** is on |
| UDP 123 | [NTP server](../../tools/ntp-server/), only when enabled |
| UDP 67 | [DHCP server](../../tools/dhcp-server/), only when enabled |
| TCP 9454 | [EST server](../../tools/est-server/): cameras get their certificates, only while it runs (the port can be changed) |

The Windows installer opens TCP 5080 for Domain and Private networks. The server opens TCP 5081 while **Accept site
nodes** is on, the NTP and DHCP servers their port while enabled, the EST server its port while it runs. The server runs as Local System / root because these two servers need privileged ports.

The server reaches devices on TCP 443 and 80, and on TCP 554 for the live preview, Live View, Dashboards and the
Metadata Monitor.

## Data folder

| System | Folder |
|---|---|
| Windows | `%ProgramData%\OADM` |
| Linux | `/var/lib/oadm` |
| macOS | `/Library/Application Support/OADM` |

It holds the database (devices, tasks, users), the plugin settings including the PKI's certificate authority, and
`master.key`, which encrypts every stored password. Only administrators / root can read it. Uninstalling keeps it.

## Backup

Use **Settings > Backup and restore**: **Back up now** or the automatic backups (kept in `backups` in the data folder).
A backup restores on another computer too. See [Settings](../settings/#backup-and-restore).

Before every database upgrade OADM copies the database to `backups/before-upgrade` (the newest five kept).

Backing up the whole data folder by hand also works. Without `master.key` the stored passwords cannot be read.

If `master.key` is lost or replaced, the server starts with a new key, removes the passwords it can no longer read and
sets those devices to **Credentials required**. The client shows a banner with the number of devices. Use **Log in**
in the device context menu to enter their password again.

The [PKI](../../tools/pki/) page can also back up the certificate authority to a password-protected file.
