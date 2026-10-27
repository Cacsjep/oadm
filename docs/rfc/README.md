# RFCs

Verbatim plain-text copies from rfc-editor.org (the IETF Trust allows copying an RFC in full, unchanged). They are the
source of truth for the protocol code: read the RFC, not a summary, and cite the section in code comments and tests
("RFC 2865 5.33"). Never edit these files; a newer RFC is a new file.

## 802.1X server ([radius-server.md](../spec/radius-server.md))

| RFC | What we take from it |
|---|---|
| [2865](rfc2865.txt) | RADIUS: packets, attributes, Request and Response Authenticator, Proxy-State (5.33), silent discard |
| [2869](rfc2869.txt) | RADIUS extensions: EAP-Message and Message-Authenticator first defined (updated by 3579) |
| [3579](rfc3579.txt) | EAP over RADIUS: EAP-Message splitting (3.1), Message-Authenticator (3.2), State |
| [3580](rfc3580.txt) | IEEE 802.1X with RADIUS: Calling-Station-Id format, NAS-Port-Type, VLAN attributes (3.31) |
| [3748](rfc3748.txt) | EAP: packet format, Identity, NAK, Identifier and retransmission rules |
| [5080](rfc5080.txt) | RADIUS implementation issues: duplicate detection, State handling, common mistakes |
| [5216](rfc5216.txt) | EAP-TLS: flags L/M/S, fragmentation (2.1.5), key derivation (2.3), certificate checks |
| [9190](rfc9190.txt) | EAP-TLS with TLS 1.3: commitment message, no resumption, exporter label |
| [2548](rfc2548.txt) | Microsoft vendor attributes: MS-MPPE-Send-Key / MS-MPPE-Recv-Key and their encryption |
| [5997](rfc5997.txt) | Status-Server |
| [7542](rfc7542.txt) | Network Access Identifier: the EAP identity format |
| [8044](rfc8044.txt) | RADIUS data types (text, string, integer, address) |
| [5246](rfc5246.txt) | TLS 1.2: handshake, alerts (7.2), PRF |
| [8446](rfc8446.txt) | TLS 1.3: handshake, alerts (6) |
| [5705](rfc5705.txt) | TLS keying material exporters |
| [8996](rfc8996.txt) | TLS 1.0 and 1.1 deprecated (why the default minimum is 1.2 and lower ones warn) |
| [9325](rfc9325.txt) | TLS recommendations: cipher suites (the ones the 802.1X server offers for its minimum TLS version) |

Not an RFC, also read: the Blast-RADIUS advisory (2024, blastradius.fail): why Message-Authenticator is required on every
request and sent first in every answer.

## EST server ([est-server.md](../spec/est-server.md))

| RFC | What we take from it |
|---|---|
| [7030](rfc7030.txt) | EST: cacerts, simpleenroll and simplereenroll, client authentication, the answers (4.1.3) |
