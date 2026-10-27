---
title: FAQ
description: Short answers to common questions.
---

**Is OADM made by Axis?**
No. OADM is an independent open source project. It is not affiliated with, sponsored by or endorsed by Axis
Communications.

**How is OADM different from AXIS Device Manager?**
The daily work is the same, but OADM runs on Windows, Linux and macOS, is open source and adds tools such as live video,
event rules and NTP and DHCP servers. See [OADM and AXIS Device Manager](../../start/axis-device-manager-alternative/).

**What does it cost?**
Nothing. OADM is free under the Apache-2.0 license, also for commercial use.

**Which devices does it support?**
Axis devices with AXIS OS: cameras, encoders, intercoms, speakers, radars, I/O modules, door controllers. Some tasks
need a newer AXIS OS; the context menu tells you which devices cannot run a task and why.

**Does it use ONVIF?**
No, only VAPIX over HTTPS or HTTP.

**Can several technicians work at the same time?**
Yes. Install the server once and the client on every computer. Each person logs in with their own user.

**Where are the device passwords stored?**
On the server, encrypted. The password OADM uses for a device never reaches a client. Administrators can show the
passwords of the credential list on the Credentials page; each time is written to the audit log. See [Ports, data and backup](../../admin/ports-and-data/).

**Can I run the server on Linux and the client on Windows?**
Yes, any combination.

**Does OADM need internet?**
No. With internet the client looks for a new OADM version after you log in; turn that off with **Tell me about new
versions** in [Settings](../../admin/settings/) > Client. Links such as the AXIS OS release notes and **Activate license**
open a web page. The 30-day trial of OADM PRO needs no internet.

**ARM computers or Apple silicon?**
Linux has packages for ARM64 too (Raspberry Pi 4 and 5, ARM servers). Windows and macOS installers are for x64 only
(Intel on macOS). [Build from source](../../dev/build/) for other systems.

**Something is missing.**
[Open a feature request](https://github.com/Cacsjep/oadm/issues/new/choose).
