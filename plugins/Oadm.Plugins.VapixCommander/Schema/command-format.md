# VAPIX Commander command format (v1)

Shared contract between the VAPIX Commander core plugin (`plugins/Oadm.Plugins.VapixCommander*`)
and the battery-included command library (`plugins/Oadm.Plugins.VapixCommander/Library/*.json`).
JSON Schema: `command.schema.json` next to this file. Decisions by the user (2026-10-07):

- A technician picks one or more commands, fills their fields and rolls them out to any number
  of devices. One task per device; every command is a named task step.
- "Stop on first error" stops the WHOLE rollout: the failing device skips its remaining commands,
  devices not started yet are cancelled, running devices stop after their current command.
- Saved commands (e.g. "Day Night Level 50") are stored on the server and shared by all clients;
  export/import as JSON files.
- A raw editor (method, path, query, body) exists like Postman; values can become `{{field}}`
  placeholders and be saved as a new command. Raw commands obey the same safety rules.
- On failure the user must see the device's own error text (VAPIX error message / code) or, for
  transport problems, a meaningful text such as "Timeout after 15 s" or "Bad Request - HTTP 400".

## One command = one JSON object

```json
{
  "id": "video.daynight.shiftlevel",
  "version": 1,
  "name": "Set day/night shift level",
  "category": "Video",
  "description": "Light level at which the camera switches between day and night mode.",
  "docs": "https://developer.axis.com/vapix/...",
  "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
  "writes": true,
  "fields": [
    { "name": "channel", "label": "Image channel", "type": "integer", "default": 0, "min": 0, "max": 7 },
    { "name": "level", "label": "Shift level", "type": "integer", "default": 50, "min": 0, "max": 100, "unit": "%" }
  ],
  "request": {
    "method": "GET",
    "path": "/axis-cgi/param.cgi",
    "query": { "action": "update", "ImageSource.I{{channel}}.DayNight.ShiftLevel": "{{level}}" },
    "headers": {},
    "body": null,
    "bodyType": "none",
    "timeoutSeconds": 15
  },
  "response": {
    "kind": "param-cgi",
    "success": "OK"
  }
}
```

### Fields

| Property | Meaning |
|---|---|
| `name` | Placeholder name, `[a-zA-Z][a-zA-Z0-9_]*`, used as `{{name}}` anywhere in `request` |
| `label`, `description` | Shown in the editor |
| `type` | `string`, `integer`, `number`, `boolean`, `enum`, `password` (never logged, never saved in plain text in saved commands unless the user opts in; stored encrypted on the server) |
| `default` | Default value |
| `min`, `max`, `pattern`, `options` (`[{ "value": "...", "label": "..." }]` for enum), `unit`, `required` (default true) | Validation, checked before any request is sent |

`boolean` placeholders render as `yes`/`no` for param.cgi kinds and `true`/`false` for JSON bodies
unless the field sets `"trueValue"`/`"falseValue"`.

### Request

| Property | Meaning |
|---|---|
| `method` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` |
| `path` | Relative to the device base address, starts with `/` |
| `query` | Map of query parameters (keys and values may contain placeholders) |
| `headers` | Extra headers (no `Authorization`; auth is handled by OADM) |
| `bodyType` | `none`, `json`, `form`, `text`, `xml` |
| `body` | JSON value for `json`, map for `form`, string for `text`/`xml`; placeholders allowed in strings. In JSON, a value that is exactly `"{{field}}"` is replaced with the typed value (number/bool stay unquoted) |
| `timeoutSeconds` | Default 15, max 600 |

### Response interpretation (`response.kind`)

| Kind | Success rule | Error text shown to the user |
|---|---|---|
| `param-cgi` | HTTP 2xx and body does not start with `# Error` / contain `Error:` ; for `action=list` the parsed key=value pairs are shown | the `# Error: ...` line |
| `json-rpc` | HTTP 2xx and no `error` object (Axis JSON APIs: `{"apiVersion","method","data"}` / `{"error":{"code","message"}}`) | `error.message (code error.code)` |
| `rest` | HTTP 2xx (`/config/rest/...`); body `status`=`error` is a failure | `error.message` or `title`/`detail` (problem+json) |
| `text` | HTTP 2xx and, if `success` is set, body contains it | first line of the body |
| `xml` | HTTP 2xx and no SOAP fault / `<GeneralError>` | fault string |
| `raw` | HTTP 2xx | body excerpt (first 300 chars) |

- Response bodies are decoded as UTF-8 by the plugin (Axis devices send `charset=utf8`). Binary answers
  (`image/*`, `video/*`, `audio/*`, `application/octet-stream`, zip, pdf) show content type and size
  ("image/jpeg, 123.4 KB") instead of text.
- HTTP 204 or an empty body with 2xx is a success ("OK") for every kind.
- Optional `response.errorPattern` (regex, multi-line): a body that matches is a device error even with
  HTTP 2xx (e.g. ptz.cgi answers `Error: ...` with 200); the matching line is the error text.
- Non-2xx answers show the status text plus the device's body text (JSON `error`, `# Error:` line,
  SOAP fault, first text line), e.g. daynight.cgi HTTP 500 bodies.
- A JSON `apiVersion` in the answer that differs from the request (devices answer with their highest
  minor version) is never an error.

Transport errors always map to readable text: `Timeout after N s`, `Connection refused`,
`Host unreachable`, `TLS/certificate error: ...`, `Unauthorized - HTTP 401 (check credentials)`,
`Forbidden - HTTP 403 (user lacks permission)`, `Not Found - HTTP 404 (API not available on this
firmware)`, `Bad Request - HTTP 400`, `Server error - HTTP 5xx`, plus the device's body text when
it has one.

Optional `response.extract`: list of `{ "label": "...", "path": "data.x.y" | "param": "Key.Name" }`
values shown in the step detail after success (e.g. the current shift level for read commands).
Paths support array indices (`data.devices.0.name` or `data.devices[0].name`) and `{{placeholders}}`;
`param` keys may carry a leading `root.` and surrounding quotes (both ignored).

### Categories

`Common`, `System`, `Network`, `Security`, `Users`, `Time`, `Video`, `Image`, `PTZ`, `Audio`,
`I/O`, `Events`, `Storage`, `Applications`, `Maintenance`, plus `Custom` for saved (user-made) commands
and exports. Each library file holds one category:
`Library/<category>.json` = `{ "formatVersion": 1, "category": "...", "commands": [ ... ] }`.

### Safety rules (CLAUDE.md "device safety")

- `requires` is mandatory; the plugin checks it against the cached API list (menu/selection) and a
  fresh API list before the first write on each device.
- `writes: false` commands are read-only and may run without confirmation; `writes: true` needs a
  confirmation summary before rollout. Commands that restart, factory-default, change network or
  users set `"dangerous": true` and need an explicit extra confirmation.
- Secrets (`password` fields) never appear in logs, task steps or exports.
