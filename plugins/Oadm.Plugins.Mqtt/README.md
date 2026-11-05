# MQTT

Event rule action **Publish MQTT message** and the recipient type **MQTT**, spec: [recipient sessions](../../docs/spec/recipient-sessions.md).

- Library MQTTnet. One connection per recipient, shared by its rules (`RecipientSessionPool`); the plugin never reconnects by itself.
- Output only: nothing subscribes, and no received message is handled.
- QoS 1 and 2 wait for the broker's acknowledge; a refusal fails the action. A failed action is not sent later.
- The topic is filled per placeholder: a value with `/`, `+`, `#` or NUL, a leading `$` or no text sends nothing, because it would change the topic level (`{category}` of an I/O module holds a `/`). The filled topic is checked again: empty, `+`, `#` or NUL sends nothing.
- An unknown placeholder and a payload that came out empty send nothing: an empty retained message clears the topic on the broker.
- JSON payloads escape the placeholder values, never the text around them.
- TLS is off by default. `MqttCertificateCheck` is always the certificate check, because the fix of MQTTnet's own check (#2254) is not in 5.2.0:
  Allow untrusted certificates accepts; a Trusted CA replaces the operating system's roots and the host name must still match; otherwise only a
  certificate the operating system trusts passes. Revocation is not checked: a broker on a closed network must not fail on an unreachable list.
- A client certificate goes without its issuers, because MQTTnet offers no way to send them. A broker must know an intermediate CA itself.
- On Windows the session copies a client certificate into the key store until it closes: Windows TLS refuses a key that lives only in memory.
- Two connections with one client ID take each other's place on the broker. The default is one ID per recipient (`MqttRecipient.DefaultClientId`), so
  two recipients on one broker need no setting. A Test gets a unique ID, and a unique ending on a chosen Client ID, so it never throws the open session off.
