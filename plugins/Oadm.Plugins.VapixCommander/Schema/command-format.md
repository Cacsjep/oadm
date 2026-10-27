# VAPIX Commander command format (v1)

The contract between the VAPIX Commander ([README](../README.md)) and the command library
([Library/README.md](../Library/README.md)): bundled `Library/*.json`, saved commands and their exports. JSON Schema:
`command.schema.json` next to this file. The library loader reads strict JSON (unknown properties are errors) and checks
every command with `CommandValidator`.

## Command

```json
{
  "id": "image.daynight.shiftlevel.set",
  "version": 1,
  "name": "Set day/night shift level",
  "category": "Image",
  "description": "Light level at which the camera switches between day and night mode (IR cut filter on auto).",
  "docs": "https://developer.axis.com/vapix/network-video/imaging-api/",
  "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
  "writes": true,
  "hasVideoOnly": true,
  "fields": [
    { "name": "source", "label": "Image source", "type": "integer", "default": 0, "min": 0, "max": 7 },
    { "name": "level", "label": "Shift level", "type": "integer", "default": 50, "min": 0, "max": 100, "unit": "%" }
  ],
  "request": {
    "method": "GET",
    "path": "/axis-cgi/param.cgi",
    "query": { "action": "update", "ImageSource.I{{source}}.DayNight.ShiftLevel": "{{level}}" },
    "headers": {},
    "bodyType": "none",
    "body": null,
    "timeoutSeconds": 15
  },
  "response": { "kind": "param-cgi", "success": "OK" }
}
```

Required: `id`, `version`, `name`, `category`, `requires`, `writes`, `fields`, `request`, `response`. Optional:
`description`, `docs`, `dangerous`, `hasVideoOnly` (only devices with video).

## Requires

`requires` is mandatory: a list of `{ "api", "minVersion" }` from the device's API list. A requirement means the same
major version and at least the minor one. `"minVersion": "any"` accepts the API in any version, for CGIs whose interface
does not follow the listed major (PTZ `ptz-control` 1.x / 2.x, `privacy-mask` 2.x / 3.x).

## Fields

| Property | Meaning |
|---|---|
| `name` | Placeholder name, `[a-zA-Z][a-zA-Z0-9_]*`, used as `{{name}}` anywhere in `request` |
| `label`, `description` | Shown in the field form |
| `type` | `string`, `integer`, `number`, `boolean`, `enum`, `password` (never logged; in saved commands stored encrypted on the server, never listed or exported), `file` (see [Multipart and file fields](#multipart-and-file-fields)) |
| `default` | Default value |
| `min`, `max`, `pattern`, `options` (`[{ "value": "...", "label": "..." }]` for enum), `unit`, `required` (default true) | Validation, checked before any request is sent |
| `trueValue`, `falseValue` | The text of a boolean (default `yes` / `no` for the `param-cgi` kind, `true` / `false` elsewhere) |

## Placeholders

`CommandRenderer` fills `{{field}}`:

- path: URL-escaped; the filled path must stay on the device (`CommandValidator.IsDevicePath`);
- query: escaped, "," and ":" kept readable;
- headers: as they are; `Authorization`, `Proxy-Authorization`, `Cookie`, `Host` and `Content-Length` are refused (OADM
  logs in);
- body: `json` (a value that is exactly `"{{field}}"` gets the typed value: numbers and booleans stay unquoted), `form`,
  `text`, `xml` (XML-escaped; a SOAP envelope gets the SOAP content type), `multipart`;
- `response.extract` paths.

## Request

| Property | Meaning |
|---|---|
| `method` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` |
| `path` | Relative to the device base address, starts with `/` |
| `query` | Map of query parameters (keys and values may contain placeholders) |
| `headers` | Extra headers |
| `bodyType` | `none`, `json`, `form`, `text`, `xml`, `multipart` |
| `body` | JSON value for `json`, map for `form`, string for `text` / `xml`, array of parts for `multipart` |
| `timeoutSeconds` | Default 15, at most 600 |

## Multipart and file fields

A `multipart` body is sent as `multipart/form-data` (quoted part names and file names, unquoted boundary, like the
firmware upload). It is an array of 1 to 10 parts; each part has a `name` and exactly one of:

| Property | Part content |
|---|---|
| `json` | A JSON value, filled like a `json` body; content type `application/json` unless `contentType` says otherwise |
| `text` | A string with placeholders |
| `file` | Exactly `"{{field}}"` of a `file` field: the file's bytes; `fileName` is the name sent (default the chosen file's name), content type `application/octet-stream` unless `contentType` says otherwise |

A `file` field's value is `{"name", "data"}` with the content in base64 (the SDK's `ActionFile`), at most 1 MB
(`CommandLimits.MaxFileBytes`). It may only be the `file` of a part (never in the path, query, headers or another body);
the name must not hold folders, quotes or control characters.

```json
"bodyType": "multipart",
"body": [
  { "name": "request", "json": { "apiVersion": "1.0", "method": "uploadProfiles", "params": { "uploadMode": "{{mode}}" } } },
  { "name": "file", "contentType": "application/json", "fileName": "profiles", "file": "{{profiles}}" }
]
```

## Response

`response.kind` decides how success, errors and values are read (`ResponseInterpreter`):

| Kind | Success rule | Error text shown to the user |
|---|---|---|
| `param-cgi` | HTTP 2xx and the body does not start with `# Error` / contain `Error:`; for `action=list` the parsed key=value pairs are shown | the `# Error: ...` line |
| `json-rpc` | HTTP 2xx and no `error` object (`{"apiVersion","method","data"}` / `{"error":{"code","message"}}`) | `error.message (code error.code)` |
| `rest` | HTTP 2xx (`/config/rest/...`); body `status` = `error` is a failure | `error.message` or `title` / `detail` (problem+json) |
| `text` | HTTP 2xx and, if `success` is set, the body contains it | first line of the body |
| `xml` | HTTP 2xx and no SOAP fault / `<GeneralError>` | fault string |
| `raw` | HTTP 2xx | body excerpt (first 300 characters) |

- HTTP 204 or an empty 2xx body is a success ("OK") for every kind.
- Binary answers (`image/*`, `video/*`, `audio/*`, `application/octet-stream`, zip, pdf) show content type and size
  ("image/jpeg, 123.4 KB").
- `response.errorPattern` (regex, multi-line): a body that matches is a device error even with HTTP 2xx (ptz.cgi answers
  `Error: ...` with 200); the matching line is the error text.
- Non-2xx answers show the shared HTTP text of `DeviceMessages` plus the device's body text when it has one ("Bad Request
  - HTTP 400: <device text>", "Server error - HTTP 500: <device text>"). Transport errors: "Timeout after N s",
  "Unreachable - Connection refused", "Unreachable - no route to the device", "TLS/certificate error: ...".
- An answer `apiVersion` that differs from the request is never an error (devices answer with their highest minor
  version).
- `response.extract`: a list of `{ "label": "...", "path": "data.x.y" | "param": "Key.Name" }` values shown in the step
  detail after success (arrays and objects as compact JSON). Paths support array indices (`data.devices.0.name` or `data.devices[0].name`) and placeholders;
  `param` keys may carry a leading `root.` and quotes (both ignored). `"optional": true` marks a value only some devices
  have (WDR, an audio output, a zoom lens, a value of a newer AXIS OS): it is left out when missing instead of "(not
  found)", and a param.cgi `action=list` answer whose `# Error: ... getting param in group 'X'` lines are only for groups
  with all extracted values optional is a success.

## Categories and files

`Common`, `System`, `Network`, `Security`, `Users`, `Time`, `Video`, `Image`, `PTZ`, `Audio`, `I/O`, `Siren and light`,
`Events`, `Storage`, `Applications`, `Maintenance`, plus `Custom` for saved commands and their exports. One file per
category: `Library/<category>.json` = `{ "formatVersion": 1, "category": "...", "commands": [ ... ] }`, the file name is
the category in PascalCase without spaces and "/" (`IO.json`, `SirenAndLight.json`).

## Safety rules

From the [device safety rule](../../../CLAUDE.md#hard-rule-device-safety-for-task-plugins):

- `requires` is checked against the cached API list (page) and a fresh API list before the first write on each device.
- `writes: false` commands are read-only and may run without confirmation; `writes: true` needs a confirmation before
  it runs. Commands that restart, factory-default, change network or users set `"dangerous": true` and need a second
  confirmation.
- Secrets (`password` fields) never appear in logs, task steps or exports.
- Commands made in the request builder follow the same rules.
