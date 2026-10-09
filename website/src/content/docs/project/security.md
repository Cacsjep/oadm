---
title: Security
description: How OADM protects devices and their passwords, and how to report a problem.
---

## How OADM protects your devices

- Client and server talk over TLS. The client checks the server's fingerprint on the first connection and refuses a
  changed certificate.
- Every user logs in. Operators and administrators have different rights; changes are in the audit log.
- Device passwords are stored encrypted on the server and never reach the client.
- When adding devices, OADM only sends a password after the device answered like an Axis device, and tries HTTPS
  first.
- OADM remembers each device's HTTPS certificate and stops talking to a device whose certificate changed.
- Tasks check that a device supports a change before the first write.

## Report a vulnerability

Report security problems privately, not in a public issue: open the repository's
[Security tab](https://github.com/Cacsjep/oadm/security) and choose **Report a vulnerability**. Include what you found,
how to reproduce it and the OADM version. We answer within a week.

Only the latest release gets security fixes.

Problems in a camera's own firmware belong to Axis: [axis.com/support/cybersecurity](https://www.axis.com/support/cybersecurity).
