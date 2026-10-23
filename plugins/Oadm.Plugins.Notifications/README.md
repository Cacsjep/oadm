# Notifications

Plugin id `oadm.notifications`, server only, always on. Three event rule actions without devices: they run once each
time the rule turns on or fires, and, when "Also send when the rule turns off" is checked, also when a stateful rule
turns off (`RunsOn`).

| Topic | Decision |
|---|---|
| Mail server | Part of every **Send email** action (host, port, encryption, login). No shared setting: an action stands on its own and is copied with its rule. |
| Escaping | Placeholder values are escaped for where they go: URL-encoded in the URL, the query and form bodies, JSON-escaped in JSON bodies and JSON TCP messages, XML-escaped in XML and HTML bodies and XML TCP messages. Text bodies, text emails and Text TCP messages get the values as they are. The text around them is never touched. |
| HTTP login | None, Basic, Digest, Bearer token. Passwords and tokens are stored encrypted (password fields). |
| Certificates | "Allow untrusted certificates" per action, off by default. |
| Test | "Send test" in the rule dialog sends once with sample values (`EventService.TestAction`, administrators, audited). |
| Sites | `siteSupport: full`: the actions need no device, so rules about devices at a site use them like any other. They send on the server's own network (the user's own servers); a "Send through site" option can come later. Their sockets are the reviewed exceptions of the banned-API analyzer. |

## Device groups

Rules of a device group may send only to the addresses an administrator allowed (Settings, `Events.ActionDestinations`).
Both actions declare their address (`IActionPlugin.Destinations`: the URL's host and port, the TCP host and port, as
written) so the server refuses a rule on save, and check it with `IActionContext.IsDestinationAllowed` right before they
connect, after the placeholders were filled: "Rules of a device group can send only to the addresses an administrator
allowed in Settings. <host> was not contacted." Send HTTP request follows redirects itself (at most 10; never from https to
http; a 301 / 302 after a POST and a 303 become a GET without the body; the login header only to the first address) and
checks every address. Rules of users with all devices are not limited. Send email is not limited (its mail server is part
of the action). `minSdkVersion` 0.4.0.

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

## Send TCP message (`oadm.notifications.tcp`)

- Host (name or IP, placeholders allowed), port (default 8000), Format Text / JSON / XML / Hex bytes, Message (code field
  colored by the format), Line end None / CR / LF / CR LF (not for hex), Use TLS (+ "Allow untrusted certificates"), Wait for
  an answer (+ "Answer must contain"), timeout 1..120 s (default 10).
- One connection per run: connect, write the message (UTF-8; hex: "02 41 0x42", spaces, commas and line breaks ignored),
  close. With TLS the certificate must be trusted unless allowed.
- Without "Wait for an answer" success = the message was written; the log says "Sent 42 bytes to host:port". With it, the
  action reads until the answer contains the expected text (or, without one, until the first data), the other side closes
  or 4 KB arrived: no data = "The connection was closed without an answer.", another text = "The answer does not contain
  ...", nothing in time = "Timeout after 10 s - host:port sent no matching answer". The log adds the first 200 characters
  of the answer.

## Placeholders

Every text takes the placeholders of `Oadm.Sdk.Actions.ActionPlaceholders` ({rule}, {event}, {state}, {time}, {date},
{timeUtc}, {device}, {address}, {hostName}, {model}, {serial}, {mac}, {firmware}, {category}, {tags}, {status}, {webUrl})
and every value of the event by its name ({objectId}, {port}). The device is the device of the event. The rule dialog
lists them next to the actions with a copy button.

## Tests

`tests/Oadm.Plugins.Notifications.Tests`: a small HTTP server on 127.0.0.1 (plain and TLS with a self-signed
certificate, Digest challenge), a small SMTP server and a small TCP server (plain and TLS, answers or not): escaping per body type, query and headers, every login,
untrusted certificates, error answers, validation, HTML mails, unreachable servers, TCP text / JSON / hex messages, line ends and expected answers.
