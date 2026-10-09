# Security

## Report a vulnerability

Please report security problems privately, not in a public issue: on GitHub, open the repository's **Security** tab and
choose **Report a vulnerability**. Include what you found, how to reproduce it and the OADM version.

We answer within a week and tell you when a fix is released.

## Supported versions

Only the latest release gets security fixes. Update to it before you report.

## What counts

Anything that weakens how OADM protects devices and their access, for example:

- device passwords or the credential list readable by someone who should not see them
- the server accepting calls without a valid login, or an operator doing administrator actions
- the client trusting a server certificate it should not trust
- a plugin or a device answer that can run code on the server or the client

Problems in a camera's own firmware belong to Axis: https://www.axis.com/support/cybersecurity.
