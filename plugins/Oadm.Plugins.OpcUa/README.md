# OPC UA

Event rule action **Write OPC UA value** and the recipient type **OPC UA**, spec: [recipient sessions](../../docs/spec/recipient-sessions.md).

- Library: OPCFoundation.NetStandard.Opc.Ua.Client 2.0.1 (OPC Foundation MIT License 1.00).
- One session per recipient, shared by its rules (`RecipientSessionPool`). One write of the Value attribute per action; nothing is browsed or subscribed, and the only reads are the stack's own: the keep alive and what it reads while a session opens.
- A value is encoded before the session is asked for; a value that does not fit, an unknown placeholder or an empty text from placeholders fails the action and writes nothing. The user chooses the type, so a wrong type is the server's refusal (BadTypeMismatch).
- Nothing is written to disk and no certificate is created. The configuration is built in memory, `OpcUaCertificates` is the stack's `ICertificateManager` without stores, and the chosen certificate goes straight to the channel and the session. Sign and Sign and encrypt need a chosen client certificate; its URI name is the application URI.
- Trust of the server: Allow untrusted accepts; a Trusted CA checks against it and its issuers (a self-signed server: the imported certificate itself); otherwise the operating system's roots. A certificate accepted through a CA or the roots must also name the host of the endpoint. With security None nothing identifies the server, so nothing checks it.
- The stack compares the server's application URI with the certificate the server sends, also with Allow untrusted. OADM cannot turn this off.
- The stack refuses a password that neither Sign and encrypt nor the server's login policy protects. OADM adds no rule and shows that refusal.
- An uncertain write status fails the action: a machine write that is not certain is not done.
- An unknown Security or Policy value fails the connection; it never means None or Best.
- BadSecurityChecksFailed has no finer status: only the text tells the stack's password refusal, and a client certificate can be refused only when one went out. Everything else gets a neutral text.
- No reconnect: `Dropped` goes to the pool, which opens a new session. A connect that ends after the pool gave up closes its session itself.
- The stack decides a drop in its keep alive: one read takes at most two intervals, and nothing back for one interval plus a second also counts. At 5 s a stall of the server process of 6 s is a drop; the pool reconnects.
