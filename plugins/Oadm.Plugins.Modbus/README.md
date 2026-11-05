# Modbus

Event rule action **Write Modbus value** and the recipient type **Modbus**, spec: [recipient sessions](../../docs/spec/recipient-sessions.md).

- Our own Modbus TCP client, no library: functions 05, 06 and 16 only. There is no read function in the code.
- One connection per recipient, shared by its rules (`RecipientSessionPool`). Requests go one at a time; a late reply is ignored by its transaction id.
- A value is encoded before anything is sent; a value that does not fit fails the action and writes nothing.
- Modbus RTU and Modbus Security are left out.
