# Notifications

Plugin id `oadm.notifications`, server only, always on. Two event rule actions without devices: they run once per rule
edge (and, when "Also send when the rule turns off" is checked, on the off edge of a stateful rule).

User decisions 2026-10-11:

| Topic | Decision |
|---|---|
| Mail server | Part of every **Send email** action (host, port, encryption, login). No shared setting: an action stands on its own and is copied with its rule. |
| Escaping | Placeholder values are escaped for where they go: URL-encoded in the URL, the query and form bodies, JSON-escaped in JSON bodies, XML-escaped in XML and HTML bodies. The text around them is never touched. |
| HTTP login | None, Basic, Digest, Bearer token. Passwords and tokens are stored encrypted (password fields). |
| Certificates | "Allow untrusted certificates" per action, off by default. |
| Test | "Send test" in the rule dialog sends once with sample values (`EventService.TestAction`, administrators, audited). |

## Send HTTP request (`oadm.notifications.http`)

- Method GET, POST, PUT, PATCH, DELETE; URL (http or https); query parameters and headers as name / value rows.
- Body type None, JSON, XML, Text, Form (one `name=value` per line); the body is a code field colored by its type.
- Success is any 2xx answer; the event log line says "HTTP 200 OK". Anything else fails with the status and the first
  200 characters of the answer. Timeout 1..120 s (default 15).
- Basic login is sent with the first request; Digest answers the server's challenge (`SocketsHttpHandler`).
- A header value never spans lines (line breaks of a filled value are removed).

## Send email (`oadm.notifications.email`)

- Mail server, port (default 587), encryption STARTTLS / TLS / none, optional user name and password, From, To (several
  addresses separated by commas or semicolons), Subject, Format Text or HTML, Body.
- HTML mails carry a plain-text part made from the HTML. Sent with MailKit (MIT).
- Errors in plain words: wrong login, untrusted certificate, refused mail, server not reachable.

## Placeholders

Every text takes the placeholders of `Oadm.Sdk.Actions.ActionPlaceholders` ({rule}, {event}, {state}, {time}, {date},
{timeUtc}, {device}, {address}, {hostName}, {model}, {serial}, {mac}, {firmware}, {category}, {tags}, {status}, {webUrl})
and every value of the event by its name ({objectId}, {port}). The device is the device of the event. The rule dialog
lists them next to the actions with a copy button.

## Tests

`tests/Oadm.Plugins.Notifications.Tests`: a small HTTP server on 127.0.0.1 (plain and TLS with a self-signed
certificate, Digest challenge) and a small SMTP server: escaping per body type, query and headers, every login,
untrusted certificates, error answers, validation, HTML mails, unreachable servers.
