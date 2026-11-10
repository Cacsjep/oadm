# Recipient sessions: MQTT, Modbus and OPC UA

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The recipient types MQTT, Modbus TCP and OPC UA: each recipient keeps one open connection that all its rules share.
Built on 2026-11-05 (SDK 0.12.0). Libraries: MQTTnet, the OPC Foundation's .NET stack, Modbus
our own. **Client and output only**: OADM connects out and writes, it never runs a broker or server and never reads values.

## The rule

A recipient type is **one shot** (HTTP, Email, TCP: open, send, close inside the action) or **persistent** (MQTT, Modbus, OPC
UA: one open connection that many rules share). The plugin declares it, the user never chooses. Many rules must never mean many
connections, and an action never owns a connection.

## SDK

- `RecipientType.Sessions`: null for a one shot type, an `IRecipientSessionFactory` for a persistent one.
- `IRecipientSessionFactory.Create(RecipientSessionRequest)` returns an `IRecipientSession` (`IAsyncDisposable`): `ConnectAsync(ct)`, and
  `Dropped`, raised when the connection ended. The request holds the settings, the certificates and `RecipientId` (null for a Test). What a
  session can send is the plugin's own interface: the action asks for it with `ctx.SessionAsync<T>(fieldKey, ct)`, and the host never knows the
  protocol.
- `ActionField.ClientCertificate` and `ActionField.TrustedCertificate`; `AlsoVisibleWhenKey` for a field that shows on either of two conditions.
- `ActionPlaceholders.FirstUnknown(text, ctx)`: the first `{name}` of a setting that the run does not fill. It reads the setting, so a value
  that holds braces never counts.
- Default interface members, a new SDK version. A plugin of an older SDK sees nothing new.

## Host

`RecipientSessionPool` in `src/Oadm.Core/Events`, one per server:

- **One session per recipient id**, opened at the first action that needs it, never at the start: a recipient no rule uses holds no
  connection.
- **Reconnect** with a growing wait, 1 s up to 60 s, through a `TimeProvider`. The wait starts again only after a connection stayed up for the
  60 s: a broker that accepts and then kicks gets no reconnect every second.
- **Idle:** only a connected session that no action used for 5 minutes closes (maintainer's default, 2026-11-05). A failed one keeps
  trying and stays reported until it connects or the recipient changes or goes, also when no rule uses it any more. Idle is decided under the
  pool's lock and the slot leaves there, before the close: an action keeps the session or gets a new one, never "Not connected" from a close.
- **A changed recipient** closes its session, the next action opens the new one. A deleted recipient closes it at once. A recipient that no
  longer exists is not retried.
- **No lock around a close:** the pool collects its slots under the lock and cancels outside it, without running what waits on them, so a close
  or `Changed` never runs under the lock (the event source takes its own lock and then the pool's).
- **A run never waits long:** the first action of a new session waits for the first attempt, at most 15 s. After that an action on a session that
  is not connected fails at once with "Not connected: <reason>" (event log, server log), and the rule's normal limits apply. No queue of messages, no replay after a reconnect: an alarm from 10 minutes ago
  must not fire a relay now.
- **Status** per recipient in memory (Connected, Connecting, Not connected with the reason, Not used), never stored, on its own stream
  `WatchRecipientStatus` (the whole list first, then after each change, at most every 500 ms; a recipient missing there is not used).
  The Recipients card shows a `ui:StatusChip` inside the Target cell after the text, no column of its own; one shot recipients and Not
  used show nothing, the reason is its tooltip. While the word would leave the target less than 64 px (`RecipientTargetCell`, at
  the card's default width and below) the chip shows only its icon and the tooltip says the word too, so the target keeps its room. The page
  watches only while it is shown, one post per burst, again after a drop (1, 2, 4 ... 15 s); a stream that ended clears the chips.
- Secrets: every run decrypts the recipient, as in Recipients, but for a persistent type the password fields stay out of the action's settings. Passwords reach only the session and stay inside it.
- **Connecting** is only the first attempt. Until a session reaches Connected a retry stays Not connected with the last reason, so the event
  below never turns off between attempts.
- Stops with the server, after `ActionRunner`: a running action keeps its session. The event source stops first, so a stop ends no event. A
  stuck close never holds up the stop (5 s).
- Output only: nothing a session receives becomes an event. Sessions run on the server; recipients at sites are later.

Event **Recipient not connected** (stateful, server, values `count` and `recipient`) tells a rule when a broker or a PLC is gone.

## Plugins

| Package | Id | Recipient holds | Action |
|---|---|---|---|
| MQTT, library MQTTnet (MIT; before a release look at issue #2280, a private security report, and the unreleased TLS fix #2254) | `oadm.mqtt` | Broker host, port, protocol version (3.1.1 or 5.0), see "Security", user name, password, client id (default one per recipient; a Test adds a unique ending, also to a chosen id, so it never takes the open session's place) | **Publish MQTT message**: topic, payload (text or JSON, placeholders escaped), QoS 0, 1 or 2, retain, off edge |
| Modbus, our own client, no library: function codes 05, 06 and 16 only (the same connection rule as Send TCP message) | `oadm.modbus` | Host, port (502), unit id | **Write Modbus value**: coil or holding register, address, value, optional pulse with a time; a value on the off edge |
| OPC UA, library OPCFoundation.NetStandard.Opc.Ua.Client (OPC Foundation MIT License 1.00 since the stack's relicensing, checked in the 2.0.1 package on 2026-11-05) | `oadm.opcua` | Endpoint (`opc.tcp://host:4840`, a new `ActionFieldFormat`), see "Security" | **Write OPC UA value**: node id, Value type (on or off, number, text), value; a value on the off edge |

All free and always on, no page (decision 2026-11-05): they only add actions and recipient types, and hold no connection until a
rule uses one.

**Write Modbus value** has a **Value type** (decision 2026-11-05: on or off, numbers and text):

| Value type | Written to | Settings |
|---|---|---|
| On or off | One coil | On or off |
| Pulse | One coil | On, a time, off |
| Number | One holding register, or two for 32 bits | 16-bit or 32-bit, signed or unsigned, or 32-bit float; word order (high word first by default), used by two registers |
| Text | As many holding registers as the length needs | Fixed length in characters, two per register, padded with zeros, longer text refused; byte order; ASCII only |

Pulse is a value type and not a choice of On or off, and Word order shows for every Number: a field shows on one condition (or either of
two), so a choice nested under another stayed visible after the first one changed.

Values may be placeholders ({number}, {text}), checked at the run: a value that does not fit the type fails the action and writes
nothing. An unknown {name} is never written literally, and a Text value that came out empty is not written ("Nothing was written.").
A number too small for a float is refused, it never becomes 0; a whole number above 2^24 stays the nearest float. A write past
address 65535 is refused at a save and before the write. A Modbus write changes a real machine: the dialog says the address counts from
0, the value is validated before anything is sent, and one action is one write. A value on the off edge is optional. Modbus RTU is out
of scope.

**A pulse never leaves a relay on silently.** The off write runs whatever ends the wait (a cancel, a failed on write; a drop does not end it, the pulse runs its time), on its own 10 s
limit and on a session asked for again, so a reconnect or a change that keeps host, port and unit does not matter; a recipient that now leads to another PLC gets no write and the action fails ("The recipient changed during the pulse. The coil may still be on."); inside the 10 s a failed try is repeated
every second, because a dropped connection comes back in that time. If it still fails the action fails with a text ending "The coil
may still be on." During the wait the action asks for the session at least every 60 s, so that a pulse longer than 5 minutes is not closed
as unused. The run may take the pulse plus 45 s: connect, queue and off write. A run that asks for more than 30 s takes a slot of the
runner's lane for long runs, so a pulse never holds a slot of the other actions; the off write is part of the same run, and a "turns off"
edge is never skipped for waiting ([event-rules.md](event-rules.md), Actions).

**A write without an answer never fires later.** A write that gets no answer in 5 s (counted from its turn, not from the queue), or that is
cancelled after sending, resets the connection (linger 0) and drops the session: a graceful close would still deliver what the kernel
holds. A closed session resets too (a changed or deleted recipient, a stop), since every write waits for its reply. TCP keep alive shows an idle connection that was cut after about a minute. A reply counts only with the session's unit id and exactly
the echo of the request.

**Placeholders in a Publish MQTT message:** the topic is filled per placeholder, and a value with `/`, `+`, `#`, a line break or another control character, a leading `$` or no text
is refused ("Nothing was sent."): a value must never change the topic level or reach a system topic, and a broker may close the connection on a control character. An unknown {name} is never sent literally, in
the topic or the payload, and a payload whose placeholders came out empty is not sent. An empty Payload field is allowed; with Retain it clears the topic.

**Write OPC UA value** refuses an unknown {name} and a Text value that came out empty ("Nothing was written."), and a number too small for its
format: it never becomes 0, and a whole number above 2^24 stays the nearest float. An uncertain write status fails the action: a machine write that is
not certain is not done. An unknown Security or Policy value fails the connection and never means None or Best. A connect that ends after the
pool gave up closes its session itself.

Nothing is read: no MQTT subscribe, no Modbus read function, no OPC UA read or subscription (rule confirmed by the maintainer on
2026-11-05). What a protocol needs for a write stays: the broker's acknowledge, the echo or exception of a Modbus write, the status of
an OPC UA write, and the stack's keep alive, which reads. The user chooses the OPC UA value type, so OADM never reads a node's data type; a wrong type is the server's refusal.
A failed action is not sent later (decision 2026-11-05).

## Security

Decision 2026-11-05: every standard option from the first version, and OADM never forces security: the user chooses. A new recipient
works with the fewest fields, and security is there when it is needed.

| | Default | Options |
|---|---|---|
| MQTT | TLS off, port 1883 | TLS (usually port 8883; the port stays 1883 until the user changes it), client certificate (mutual TLS) optional |
| OPC UA | Security none, anonymous login | Security none, sign, sign and encrypt; a chosen policy; login anonymous, user name and password, or a certificate |
| Modbus | Plain TCP, port 502 | None: Modbus Security is left out |

- **The certificates come from PKI** (always on, free): the recipient dialog offers a **Client certificate** select (certificates of the
  Certificates list with a key OADM holds) and a **Trusted CA** select (the CAs of the list, and imported certificates for the self-signed
  servers common with OPC UA). Default trust is the server's operating system root store. Allow untrusted certificates stays an option.
- **In the SDK:** two field types and `IRecipientCertificates`, which PKI offers through `IPluginServices`. Only the host reads it, by convention:
  any core plugin in the process could find it. The host loads the certificate when it opens a session (`LoadAsync(id, withKey, ct)`: a key only for a
  Client certificate field, a Trusted CA field loads without one), hands it to the factory and disposes what it loaded also when a later field or
  the factory fails, so a plugin never searches PKI. The key stays inside the session. Windows TLS refuses a key held only
  in memory (decision 2026-11-05: accepted), so on Windows it goes through the key store of the service account until the session closes.
- **Server check:** on save and Test the host refuses a value of a certificate field that is not in `ClientCertificatesAsync` or
  `TrustedCertificatesAsync` for its field, below the field with "The chosen certificate no longer exists. Choose another one."; hidden fields
  count, an unchanged stored value stays. The audit of a Test names the certificate id. A role with Change event rules may use any listed key
  through a recipient: accepted (decision 2026-11-05).
- **Rules in PKI:** a certificate a recipient uses cannot be deleted and shows the recipient under Used by; a hidden field still counts, and of a
  type that is not installed every stored value starting `local:` or `ca:` counts. The Expiry warning covers it
  as any certificate of the list ("Expires in 12 days" in its row, the expiry events stay about devices).
- **In the dialog:** both selects are read once when it opens (`ListRecipientCertificates`, ring and "Reading certificates" while a shown
  type waits); an empty list says "Import or create one on the PKI page."; a value no longer in the list leaves the select empty with
  "This certificate cannot be used now. Choose another one." below it, and Save stays off until the user chooses: the list cannot say whether
  it ended, a server shows it, PKI is stopped or it is gone, so the text claims none of them (a session that cannot load its
  certificate keeps "The chosen certificate no longer exists. Choose another one." from the server). In a hidden field the
  value stays. For a persistent type Test says Connecting, then the server's Connected or reason; the audit says "Connected to".
  A change of the form during a Test (a field, the name, the type) drops that Test's reply. A refusal with the field trailer stands
  below that field for Test as for Save ("Enter the password again. The target changed." below Password).
- **Send test in the rule dialog:** off for an action whose recipient is persistent, the tooltip says "Test the recipient instead.": it would
  write to a real machine, the recipient's Test only connects. The server refuses it too.
- **No new certificates in PKI:** Create stays as it is. An IEEE 802.1X certificate has client authentication, so it already works for MQTT.
  OPC UA's sign, sign and encrypt and certificate login take a certificate imported into PKI. Its application URI comes from the certificate's URI name.
- **Trust of an OPC UA server:** Allow untrusted accepts; a Trusted CA checks against it and its issuers (a self-signed server: the imported
  certificate itself); otherwise the operating system's roots. A certificate accepted through a CA or the roots must name the host of the endpoint
  (built 2026-11-05). With security None OADM checks no trust: nothing identifies the server, also for a password the login policy encrypts. The
  stack's own check that the server's application URI is in the certificate the server sends still runs, also with Allow untrusted, and OADM cannot turn it off.
- **Passwords with OPC UA:** the stack itself refuses a user name and password when neither a sign and encrypt channel nor the server's login
  policy encrypts it (Bad security checks failed). OADM adds no rule of its own and shows that refusal in plain words.

## Notes

- MQTTnet: the fix of its TLS check (#2254) is not in 5.2.0, the newest release. OADM sets its own certificate check, so both versions behave
  the same. The fix is marked a breaking change: read it when updating.
- `Oadm.Notices` lists a license file that names itself by that name (the OPC UA packages: OPC Foundation MIT License 1.00).
- Their Microsoft.Extensions packages must fit the host: the plugin resolves 10.0.14, the host ships 10.0.13 (both assembly version
  10.0.0.0), and the plugin load context uses the host's; what the host does not have (Http.Resilience, Polly) comes from the plugin folder.
- Opc.Ua.Client.dll is named like a client part. The client loader takes only a `*.Client.dll` that references the client SDK.

## Tests

- The pool with a fake session and a `FakeTimeProvider`: one session for many rules under load, the wait grows, resets only after a connection
  held and keeps growing for one that is kicked, idle close and an action during it, change and delete, a dropped connection, a stop with a stuck
  session, a close that blocks with the lock free, a target that stays down past the idle time with the event on without a break.
- Unit tests: MQTT against MQTTnet's own in-process broker, Modbus against a fake PLC on the loopback (exception replies, wrong transaction
  id, a reply cut short), OPC UA without a network where possible. No network outside the machine.
- The real servers run only when someone works on these types (decision 2026-11-05): `manage test recipients` starts Mosquitto, a Modbus server
  and an OPC UA server from `tests/RecipientStack/docker-compose.yml`, connects and writes once to each (`Category=RecipientStack`, also
  `HardwareWrite`, so no other run and no CI touches them), and stops them.
