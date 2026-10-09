---
title: Coming from ADM
description: Where you find the AXIS Device Manager features in OADM.
---

OADM follows AXIS Device Manager: a device list, a context menu with tasks and a task list below. This table shows
where things are.

| In ADM | In OADM |
|---|---|
| Add devices (search, IP range, manual) | **Add** on the Devices page: **Discovery**, **Network range**, **Add manually**, **Import from file** |
| Device list and columns | Devices page; **Choose columns** right of the toolbar, order and widths are remembered |
| Right-click > configure | Right-click > a group submenu (Maintenance, Network, Security, Users, Applications) |
| Upgrade firmware | Maintenance > **Upgrade firmware** |
| Set date and time | Maintenance > **Date and time** |
| Assign IP address | Network > **Assign IP address** (same range syntax, for example `192.168.0.10-20`) |
| Users | Users > **Users** |
| Install ACAP | Applications > **Applications (ACAP)** |
| Certificates, HTTPS, IEEE 802.1X | Security submenu and the **PKI** page |
| Get system report | **System report** in the toolbar |
| Task list | **Tasks** pane below the device list, one row per device, **Details** for steps and log |

## What OADM adds

- Client and server on Windows, Linux and macOS, several technicians on one server with users and roles.
- [Live view](../../devices/live-view/) next to the device list.
- [Tags](../../devices/tags/) for your own grouping.
- [Snapshot report](../../tools/snapshot-report/), [VAPIX Commander](../../tools/vapix-commander/),
  [Hardening scan](../../tools/hardening-scan/), [Metadata Monitor](../../tools/metadata-monitor/).
- Built-in [NTP server](../../tools/ntp-server/) and [DHCP server](../../tools/dhcp-server/) for sites without
  infrastructure.
- Plugins: add your own tasks and pages.

## Not there yet

- Scheduled tasks.
- Warranty and replacement data.
- Other languages than English.
