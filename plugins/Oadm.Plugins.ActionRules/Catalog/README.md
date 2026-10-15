# Action catalog

`action-catalog.json` describes every action template and recipient template the devices offer in their action rules:
a plain label per template and, per parameter, how to show and check it. The Action Rule Editor uses it to draw the
settings form of an action or recipient. Values are written exactly as the device stores them; OADM never converts them.

## Schema

```json
{
  "actions":    { "<action template token>":    { "label": "...", "fields": { "<parameter>": { ... } } } },
  "recipients": { "<recipient template token>": { "label": "...", "fields": { "<parameter>": { ... } } } }
}
```

Field order = the order the device's web interface shows the fields, then the parameters it never shows.

| Property | Meaning |
|---|---|
| `label` | Plain English label; a unit in parentheses is the stored unit ("Duration (ms)"). Absent for `hidden`. |
| `kind` | `text`, `multiline`, `number`, `bool`, `choice`, `password`, `hidden` (never shown, the device's value is kept). |
| `default` | String, exactly as stored, for a new action or recipient. |
| `required` | `true` when the field must not be empty. |
| `min`, `max` | Number range in the stored unit. |
| `choices` | `[{ "value", "label" }]`, value as stored. |
| `true`, `false` | Stored values of a `bool` (`"1"`/`"0"` or `"true"`/`"false"`). |
| `hint` | A few words below the field, only when it adds something. |

An action template with a recipient template lists only its own parameters; the recipient's parameters are under
`recipients`.

## Source

- Tokens and parameter names: the union of `GetActionTemplates` and `GetRecipientTemplates` of AXIS OS 10, 11 and 12
  (`tests/Oadm.Plugins.ActionRules.Tests/Fixtures/os1x-Get*Templates.xml`). Nothing outside these is listed.
- Labels, field kinds, choices, defaults, ranges and units: the AXIS OS 12.11 web interface (its JavaScript bundles and
  English texts), reworded for OADM (see "Wording" in CLAUDE.md).
- Where the web interface picks values from the device (I/O ports, lights, audio clips, storage, stream profiles,
  network shares, SIP accounts), the field is `text` with a hint.

## Extending

1. Add the new device's `GetActionTemplates` / `GetRecipientTemplates` answer to the fixtures.
2. Add each new token and parameter here. Look up the web interface definition for kind, default and unit; without
   one, use a plain label and `text` (or `hidden` for internal values).
3. Check: the file parses, every fixture token and parameter is present, no other parameter is listed, every
   `choice` default is one of its choices and every `bool` default is its `true` or `false` value.
