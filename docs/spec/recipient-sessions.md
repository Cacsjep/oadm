# Recipient sessions: MQTT and Modbus

Spec, not built, **parked on 2026-11-05 until the maintainer asks**: Recipients only stays ready for it. Agreed with the maintainer on 2026-11-05: MQTT and Modbus TCP
together, on one architecture that also carries OPC UA later. OPC UA itself waits for a license decision: the core of the OPC
Foundation's .NET stack is RCL for corporate members and GPL 2.0 for others, which does not fit OADM's Apache-2.0 code.

## The rule

A recipient type is **one shot** (HTTP, Email, TCP: open, send, close inside the action) or **persistent** (MQTT, Modbus, later OPC
UA: one open connection that many rules share). The plugin declares it, the user never chooses. Many rules must never mean many
connections, and an action never owns a connection.

## SDK

- `RecipientType.Kind`: `OneShot` (default) or `Persistent`.
- A persistent type gives `IRecipientSessionFactory.Create(settings, ILogger)`, which returns an `IRecipientSession`
  (`IAsyncDisposable`): `ConnectAsync(ct)`, and `Dropped`, raised when the connection ended. What it can send is the plugin's own
  interface: the action asks for it with `ctx.Session<T>(fieldKey)`, and the host never knows MQTT or Modbus.
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
- **Status** per recipient in memory (Connected, Connecting, Not connected with the reason, Not used), `RecipientEntry.status`, a live
  chip in the Recipients card, through the same batched stream as the rest of the page. Never stored.
- Secrets stay as in Recipients: decrypted only to open a session, kept only inside it.
- Stops with the server; a stuck close never holds up the stop (5 s).
- Output only: nothing a session receives becomes an event. Sessions run on the server; recipients at sites are later.

Event **Recipient not connected** (stateful, server, values `count` and `recipient`) tells a rule when a broker or a PLC is gone.

## Plugins

| Package | Id | Recipient holds | Action |
|---|---|---|---|
| MQTT, library MQTTnet (MIT; before a release look at issue #2280, a private security report, and the unreleased TLS fix #2254) | `oadm.mqtt` | Broker host, port (1883, 8883 with TLS), TLS, Allow untrusted certificates, user name, password, client id (default `oadm-<server>`) | **Publish MQTT message**: topic, payload (text or JSON, placeholders escaped), QoS 0, 1 or 2, retain, off edge |
| Modbus, library open (a small own client was proposed: FluentModbus has one maintainer and no code change since 2024, NModbus an open report of hanging reads) | `oadm.modbus` | Host, port (502), unit id | **Write Modbus value**: coil or holding register, address, value, optional pulse with a time; a value on the off edge |

Both free and always on, no page (decision 2026-11-05): they only add actions and recipient types, and hold no connection until a
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

Nothing is read: reading an MQTT topic or a PLC is a later step with its own decision, because output only is a rule today
(decision 2026-11-05). A failed action is not sent later (decision 2026-11-05).

## Tests

- The pool with a fake session and a `FakeTimeProvider`: one session for many rules under load, the wait grows and resets, idle close,
  change and delete, a dropped connection, a stop with a stuck session.
- MQTT against MQTTnet's own in-process broker; Modbus against FluentModbus's in-process server or a fake socket. No network outside
  the machine in unit tests; a real broker and PLC only in hardware tests, on request.
