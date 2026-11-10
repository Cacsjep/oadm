# Modbus

Id `oadm.modbus`, server only, always on. Event rule action **Write Modbus value** and the recipient type **Modbus**, spec: [recipient sessions](../../docs/spec/recipient-sessions.md).

- Our own Modbus TCP client, no library: functions 05, 06 and 16 only. There is no read function in the code.
- One connection per recipient, shared by its rules (`RecipientSessionPool`). Requests go one at a time; a reply counts only with its transaction id, the unit id and exactly the echo of the request.
- A value is encoded before anything is sent; a value that does not fit, an unknown placeholder, an empty text from placeholders or a write past address 65535 fails the action and writes nothing.
- A write without an answer in 5 s (from its turn) or cancelled after sending resets the connection and drops the session, so it cannot fire later. Keep alive shows an idle cut.
- A pulse: the off write runs in a `finally`, on its own 10 s, on a session asked for again, tried every second. If it fails the text ends "The coil may still be on." During the wait the session is asked for at least every 60 s, or the pool closes it as unused after 5 minutes.
- Pulse is a value type: a field shows on one condition or either of two, so Seconds could not hang below a choice that hides itself.
- Modbus RTU and Modbus Security are left out.
