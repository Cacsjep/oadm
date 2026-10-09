---
title: FAQ
description: Short answers to common questions.
---

**Is OADM made by Axis?**
No. OADM is an independent open source project. It is not affiliated with, sponsored by or endorsed by Axis
Communications.

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
On the server, encrypted. They never reach the client. See [Ports, data and backup](../../admin/ports-and-data/).

**Can I run the server on Linux and the client on Windows?**
Yes, any combination.

**Does OADM need internet?**
No. Only the AXIS OS release notes link opens a web page.

**ARM computers or Apple silicon?**
The installers are for x64 only (Intel on macOS). [Build from source](../../dev/build/) for other systems.

**Something is missing.**
[Open a feature request](https://github.com/Cacsjep/oadm/issues/new/choose).
