# Recipients

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Shared targets of event rule actions, like the Recipients of an Axis device (System > Events): a recipient says where and
how to reach something, the action says what to send. Agreed with the maintainer on 2026-11-05; until then each action
kept its own server settings.

## Decisions

| Topic | Decision |
|---|---|
| Name | **Recipients**, one word for the whole concept, in every UI text and doc |
| Link | A rule keeps a link to its recipient. Axis copies the values into the rule and keeps no link; OADM does not: change a recipient and all its rules follow |
| Direction | Output only. A recipient never feeds events into OADM |
| Used by | No column in the list; a refused delete names the rules |
| Types | HTTP (http and https in one type), Email, TCP. No FTP, SFTP or Network share: OADM sends no files. The persistent types MQTT, Modbus and OPC UA add a shared session ([recipient sessions](recipient-sessions.md)). A type is declared by its plugin, never hard-coded in the host |
| What moves | The recipient holds the target and its login, the action keeps the message (table below). No placeholders in a recipient: a target is fixed |
| Email | From and To belong to the recipient: an Email recipient is one mailbox or group ("Gate team"). A second audience is a second recipient |
| Existing rules | The user moves them. No conversion. An old Send email, Send HTTP request or Send TCP message action has no recipient: its rule shows "Send email: Choose a recipient.", the action does not run (event log: Failed with that text) and the rule cannot be saved until one is chosen. The old values stay unread in the stored action and go at the next save, passwords included |
| Delete | Refused while a rule uses it, with the rule names: "Used by OCR, Gate alarm. Change those rules first." |
| Rights | The event rules' own: "See event rules" lists, "Change event rules" creates, changes, deletes and tests (audited). Users with a device group do not reach them, like every `EventService` method (`AccessRule.AllDevices`) |
| Secrets | Password fields are stored with `ISecretProtector` (purpose `oadm:recipient:<id>:<field>`), never sent back to a client, decrypted only for a run or a test. Empty password on save keeps the stored one, only while every other field is unchanged: with a changed target Test and Save refuse an empty password below the field, "Enter the password again. The target changed."; a hidden one, which nobody can be asked for, is dropped on save and not used by Test. A stored optional password cannot be cleared on a changed target: a target without a login gets a new recipient |
| Limit | At most 200 recipients (`RecipientStore.MaxRecipients`), names unique, at most 64 characters, no control characters (line break, tab: the server log writes the name as it is; refused below the name with "Use letters, digits and punctuation only.", `RecipientType.NameInvalidText`). A recipient stored before the check still loads and sends, only a save is refused; Test takes them out of the name it writes to the audit |

| Type | The recipient holds | The action keeps |
|---|---|---|
| HTTP | URL, login None / Basic / Digest / Bearer token with its user, password or token, Allow untrusted certificates. The URL is the final address: a redirect is never followed (the body, the headers and the login would go to an address nobody entered), Test and a run fail with "The address redirects to <host>. Enter the final address." | Method, query, headers, body, timeout, off edge |
| Email | Mail server, port, encryption, Allow untrusted certificates, user name, password, From, To | Subject, format, body, off edge |
| TCP | Host, port, Use TLS, Allow untrusted certificates | Format, message, line end, wait for an answer, answer must contain, timeout, off edge |

## SDK

- New field type `ActionFieldType.Recipient` with `ActionField.Recipient(key, label, typeIds)`: required, the value is the
  recipient's id. `ActionField.EmptyText`: the error of a required field left empty, for a label that does not read in a
  sentence ("To", "URL"); default "Enter" and the label. `ActionField.Format` (`ActionFieldFormat`: Url, Host, Email, EmailList, OpcEndpoint,
  `ActionFormats`): what a text field must hold, part of `ActionSettings.Check`, so the dialog shows the error while the user types
  and the server refuses the same. Url: http or https, no placeholders, no spaces, no login in it (the target shows in the list and
  the audit log). The checks of a plugin's `Validate` run on the server only, for what a format cannot say. One `Recipient` field per action: the merge is flat, a second one would
  replace the keys of the first. New SDK members are default interface members; this is SDK 0.11.0 (the table in
  `website/src/content/docs/dev/compatibility.md`, `SdkInfo.Version`, `Oadm.Plugin.Versions.props`).
- `RecipientType(Id, DisplayName, IconKey, Fields, TestActionId)`: the fields use the `ActionField` kinds and their checks.
  `IActionPlugin.RecipientTypes` (default none) declares them; the host collects them in the event catalog.
  `TestActionId` is the action that sends the test of the type.
- `ActionInvocation.RecipientIds`: field key to recipient id of a run, empty in a recipient's own Test. `SessionAsync` finds the recipient through it,
  and `RecipientStore.Changed` closes the session of a changed recipient ([recipient sessions](recipient-sessions.md)).
- `RecipientType.Target`: the one-line target of the list with {key} for a field's value ("{host}:{port}"), default the first field.
- `IActionPlugin.Validate` also checks a recipient: on save and on Test the host calls the type's test action with its default values
  and the recipient's, and keeps the errors of the type's fields. `Describe` gets the recipient's fields without passwords; while a
  recipient is missing the rules list shows the display name.
- `IActionPlugin.Validate` and `ExecuteAsync` stay as they are: **the host merges the recipient's fields into the action's
  settings before a run**, under their own keys, so `invocation.Get("host")` keeps working and a plugin reads one flat
  list. A recipient's value wins over an old value of the same key in the stored action. The password fields of a persistent type stay out of the settings: they reach only the session.

## Server

- Entity `Recipient` (`Recipients` table, a new migration; EF migrations are never deleted): Id, Name, TypeId, SettingsJson
  with the password values encrypted, UpdatedUtc. `RecipientStore` in `src/Oadm.Core/Events` next to `RuleStore`, one write
  at a time, keeps the rule `MaxRecipients`. Every create, change and delete goes through it and raises one `Changed` with the id,
  after the write.
- `ActionSettings.Check` of `Recipient`: empty is "Choose a recipient."; `RuleValidator` also refuses an id that does not
  exist or is of another type. The merge is in `ActionRunner` for a run and for `TestAsync`, after `RunsOn`, so an edge that
  does not run reads nothing; a recipient that is gone fails the run with "Choose a recipient.", a failed read with "The recipient
  cannot be read" (event log, server log). Backups hold the table like every other one.
- gRPC on `EventService`: `ListRecipients`, `SaveRecipient` (create or change, field errors like a rule, the trailer `oadm-field`
  names the field so the dialog shows the error below it), `DeleteRecipient` (FAILED_PRECONDITION with the rule names, at most five,
  then "and 12 more": a status text must stay small), `TestRecipient` (a persistent type only connects, a one shot type runs its `TestActionId` with that action's default
  values and sample event values; reply ok + message, a failure goes to the server log like Send test; what the checks refuse is
  INVALID_ARGUMENT with the trailer `oadm-field`, like a save, so the dialog shows it below the field). `EventCatalog` gains
  `recipient_types`. A `RecipientEntry` has id, name, type id, the values without passwords, which password fields are stored,
  a one-line target ("smtp.office365.com:587", "https://hooks.example.com") and the names of the rules that use it (`used_by`).
  Every method in `AccessPolicy`: list like `ListRules`, the others like `SaveRule`, `AllDevices`; change, delete and test
  are audited ("Created recipient", "Changed recipient", "Deleted recipient", "Tested recipient" with the target; a Test the checks refused
  names the type instead: a refused value may hold a login). A test of a stored
  recipient with another type id is refused, so a stored password never reaches a target of another type. A list is at most 200
  rows, so no page is needed.

## Client

- **Event rules page:** the top row has two columns, **Rules** left and **Recipients** right, with a vertical
  `GridSplitter` (`paneSplitter vertical`): 7* and 3* by default, MinWidth 560 and 280. The event log stays below at full
  width with its own splitter. Below 900 px of page width the Recipients card moves below Rules and the log takes at most 200 px
  (`EventRulesView.StackedBelowWidth`, small screens, `docs/spec/visual-style.md`). The toolbars of both cards are
  `ui:ToolbarOverflowPanel`, the grids scroll sideways at their minimum.
- **Recipients card:** header row New, Edit, Delete (red, apart) and below it a search box (`SearchBox.stretch`: the card is too narrow
  for one row); a virtualized `DataGrid`: Name, Type, Target (the full text as tooltip). The list
  is read when the page opens, after a change here and when the rules list shows a change of what the rules run. Empty list: "Create a recipient, then choose it in a rule.", without "Change event rules" "No recipients yet." Hidden without the right to
  see event rules. Without "Change event rules" New, Edit and Delete are off with the read-only hint.
- **Recipient dialog** (one form, `Border.dialogBody`, like `ui:MessageWindow`'s padding): Type (only for a new one), Name, then
  the type's fields drawn by `SettingsFormView`; validation below each field (`ValidatingViewModel`). Footer: **Test** with the
  result chip next to it (the reason as text, never only an icon), Cancel, Save.
- **Rule dialog:** a `Recipient` field is a select of the recipients of its types, sorted by name, with a **New** command button
  beside it that opens the recipient dialog and selects the result. An old action shows the field empty with its error.
- `Send test` of an action stays for one shot types: the host merges the chosen recipient first. An action on a persistent type is refused
  ([recipient sessions](recipient-sessions.md)): "Test the recipient instead: it connects and sends nothing."

## Tests

- `RecipientStore`: create, change, unique name, limit, password kept on an empty save, never in a reply.
- A changed target: Test and Save refuse an empty password, an unchanged recipient keeps working, a hidden password is dropped.
- The merge in a run and in a test; a recipient gone; another type; an old action without a recipient (fails, the rule shows it, no
  send); the three Notifications actions read their target through the merge.
- `DeleteRecipient` with rules, `used_by`, `AccessPolicy` rules, the audit.
- View models and headless screenshots (`OADM_SCREENSHOT_DIR`): the two columns with min sizes, the dialog of each type, the Test
  result, the select with New, the old action. Tests never assert positions or sizes.
- A list at the limit stays fast; the rule count in `used_by` is computed once per list, not per row.
