# Recipient sessions: MQTT, Modbus and OPC UA

Spec, not built, **parked on 2026-11-05 until the maintainer asks**: Recipients only stays ready for it. Agreed with the maintainer on 2026-11-05: MQTT, Modbus TCP
and OPC UA on one architecture. Libraries decided the same day: MQTTnet, the OPC Foundation's .NET stack, Modbus our own. **Client
and output only**: OADM connects out and writes, it never runs a broker or server and never reads values.

## The rule

A recipient type is **one shot** (HTTP, Email, TCP: open, send, close inside the action) or **persistent** (MQTT, Modbus, OPC
UA: one open connection that many rules share). The plugin declares it, the user never chooses. Many rules must never mean many
connections, and an action never owns a connection.

## SDK

- `RecipientType.Kind`: `OneShot` (default) or `Persistent`.
- A persistent type gives `IRecipientSessionFactory.Create(settings, ILogger)`, which returns an `IRecipientSession`
  (`IAsyncDisposable`): `ConnectAsync(ct)`, and `Dropped`, raised when the connection ended. What it can send is the plugin's own
  interface: the action asks for it with `ctx.Session<T>(fieldKey)`, and the host never knows MQTT, Modbus or OPC UA.
- Default interface members, a new SDK version. A plugin of an older SDK sees nothing new.

## Host

`RecipientSessionPool` in `src/Oadm.Core/Events`, one per server:

- **One session per recipient id**, opened at the first action that needs it, never at the start: a recipient no rule uses holds no
  connection.
- **Reconnect** with a growing wait, 1 s up to 60 s, through a `TimeProvider`. A session that stands unused for 5 minutes closes.
- **A changed recipient** closes its session, the next action opens the new one. A deleted recipient closes it at once.
- **A run never waits long:** an action on a session that is not connected fails at once with "Not connected: <reason>" (event log,
  server log), and the rule's normal limits apply. No queue of messages, no replay after a reconnect: an alarm from 10 minutes ago
  must not fire a relay now.
- **Status** per recipient in memory (Connected, Connecting, Not connected with the reason, Not used), never stored, on its own stream
  `WatchRecipientStatus` (the whole list first, then after each change, at most every 500 ms; a recipient missing there is not used).
  The Recipients card shows a `ui:StatusChip` inside the Target cell after the text, no column of its own; one shot recipients and Not
  used show nothing, the reason is its tooltip. While the word would leave the target less than 64 px (`RecipientTargetCell`, at
  the card's default width and below) the chip shows only its icon and the tooltip says the word too, so the target keeps its room. The page
  watches only while it is shown, one post per burst, again after a drop (1, 2, 4 ... 15 s); a stream that ended clears the chips.
- Secrets stay as in Recipients: decrypted only to open a session, kept only inside it.
- Stops with the server; a stuck close never holds up the stop (5 s).
- Output only: nothing a session receives becomes an event. Sessions run on the server; recipients at sites are later.

Event **Recipient not connected** (stateful, server, values `count` and `recipient`) tells a rule when a broker or a PLC is gone.

## Plugins

| Package | Id | Recipient holds | Action |
|---|---|---|---|
| MQTT, library MQTTnet (MIT; before a release look at issue #2280, a private security report, and the unreleased TLS fix #2254) | `oadm.mqtt` | Broker host, port, protocol version (3.1.1 or 5.0), see "Security", user name, password, client id (default `oadm-<server>`) | **Publish MQTT message**: topic, payload (text or JSON, placeholders escaped), QoS 0, 1 or 2, retain, off edge |
| Modbus, our own client, no library: function codes 05, 06 and 16 only (the same connection rule as Send TCP message) | `oadm.modbus` | Host, port (502), unit id | **Write Modbus value**: coil or holding register, address, value, optional pulse with a time; a value on the off edge |
| OPC UA, library OPCFoundation.NetStandard.Opc.Ua.Client (OPC Foundation MIT License 1.00 since the stack's relicensing, checked in the 2.0.1 package on 2026-11-05) | `oadm.opcua` | Endpoint (`opc.tcp://host:4840`, a new `ActionFieldFormat`), see "Security" | **Write OPC UA value**: node id, Value type (on or off, number, text), value; a value on the off edge |

All free and always on, no page (decision 2026-11-05): they only add actions and recipient types, and hold no connection until a
rule uses one.

**Write Modbus value** has a **Value type** (decision 2026-11-05: on or off, numbers and text):

| Value type | Written to | Settings |
|---|---|---|
| On or off | One coil | On, off or pulse with a time |
| Number | One holding register, or two for 32 bits | 16-bit or 32-bit, signed or unsigned, or 32-bit float; word order (high word first by default) for two registers |
| Text | As many holding registers as the length needs | Fixed length in characters, two per register, padded with zeros, longer text refused; byte order; ASCII only |

Values may be placeholders ({number}, {text}), checked at the run: a value that does not fit the type fails the action and writes
nothing. A Modbus write changes a real machine: the dialog says the address counts from 0, the value is validated before anything is
sent, and one action is one write. A value on the off edge is optional. Modbus RTU is out of scope.

Nothing is read: no MQTT subscribe, no Modbus read function, no OPC UA read or subscription (rule confirmed by the maintainer on
2026-11-05). What a protocol needs for a write stays: the broker's acknowledge, the echo or exception of a Modbus write, the status of
an OPC UA write. The user chooses the OPC UA value type, so OADM never reads a node's data type; a wrong type is the server's refusal.
A failed action is not sent later (decision 2026-11-05).

## Security

Decision 2026-11-05: every standard option from the first version, and OADM never forces security: the user chooses. A new recipient
works with the fewest fields, and security is there when it is needed.

| | Default | Options |
|---|---|---|
| MQTT | TLS off, port 1883 | TLS (port 8883), client certificate (mutual TLS) optional |
| OPC UA | Security none, anonymous login | Security none, sign, sign and encrypt; a chosen policy; login anonymous, user name and password, or a certificate |
| Modbus | Plain TCP, port 502 | None: Modbus Security is left out |

- **The certificates come from PKI** (always on, free): the recipient dialog offers a **Client certificate** select (certificates of the
  Certificates list with a key OADM holds) and a **Trusted CA** select (the CAs of the list, and imported certificates for the self-signed
  servers common with OPC UA). Default trust is the server's operating system root store. Allow untrusted certificates stays an option.
- **Open points of the SDK:** a new field type for the two selects, and a new SDK interface PKI offers through `IPluginServices`
  (`IServerCertificateSource` is PKI's own, and plugins share only SDK types). The host resolves the certificate when it opens a session and hands
  it to `IRecipientSessionFactory`, so a plugin never searches PKI. The key stays in memory inside the session.
- **Rules in PKI:** a certificate a recipient uses cannot be deleted and shows the recipient under Used by; the Expiry warning covers it
  as any certificate of the list ("Expires in 12 days" in its row, the expiry events stay about devices).
- **In the dialog:** both selects are read once when it opens (`ListRecipientCertificates`, ring and "Reading certificates" while a shown
  type waits); an empty list says "Import or create one on the PKI page."; a value no longer in the list leaves the select empty with
  "The chosen certificate no longer exists. Choose another one." below it, and Save stays off until the user chooses. In a hidden field the
  value stays. For a persistent type Test says Connecting, then the server's Connected or reason; the audit says "Connected to".
- **No new certificates in PKI:** Create stays as it is. An IEEE 802.1X certificate has client authentication, so it already works for MQTT.
  OPC UA's sign, sign and encrypt and certificate login take a certificate imported into PKI. Its application URI comes from the certificate's URI name.
- **Trust of an OPC UA server:** Allow untrusted accepts; a Trusted CA checks against it and its issuers (a self-signed server: the imported
  certificate itself); otherwise the operating system's roots. A certificate accepted through a CA or the roots must name the host of the endpoint
  (built 2026-11-05). With security None nothing identifies the server, so nothing checks it, also for a password the login policy encrypts.
- **Passwords with OPC UA:** the stack itself refuses a user name and password when neither a sign and encrypt channel nor the server's login
  policy encrypts it (Bad security checks failed). OADM adds no rule of its own and shows that refusal in plain words.

## Before the build

- MQTTnet: the fix of its TLS check (#2254) is not in 5.2.0, the newest release. OADM sets its own certificate check, so both versions behave
  the same. The fix is marked a breaking change: read it when updating.
- `Oadm.Notices` lists a license file that names itself by that name (the OPC UA packages: OPC Foundation MIT License 1.00).
- Their Microsoft.Extensions packages must fit the host: the plugin's 10.0.12 are older than the host's 10.0.13, which the plugin load context
  uses; what the host does not have (Http.Resilience, Polly) comes from the plugin folder.
- Opc.Ua.Client.dll is named like a client part. The client loader takes only a `*.Client.dll` that references the client SDK.

## Tests

- The pool with a fake session and a `FakeTimeProvider`: one session for many rules under load, the wait grows and resets, idle close,
  change and delete, a dropped connection, a stop with a stuck session.
- Unit tests: MQTT against MQTTnet's own in-process broker, Modbus against a fake PLC on the loopback (exception replies, wrong transaction
  id, a reply cut short), OPC UA without a network where possible. No network outside the machine.
- The real servers run only when someone works on these types (decision 2026-11-05): `manage test recipients` starts Mosquitto, a Modbus server
  and an OPC UA server from `tests/RecipientStack/docker-compose.yml`, connects and writes once to each (`Category=RecipientStack`, also
  `HardwareWrite`, so no other run and no CI touches them), and stops them.
