# MCP server

Status: **built**. The four stages ("Stages" and their "as built" sections), then (decision 2026-11-15, everything a
user can do): the host actions ("Host actions as built"), the administration ("Administration as built"), users, roles,
device groups and access keys ("Access as built"), server settings and services ("Server settings and services as
built") and every secret through the secret channel ("Secrets"). Not offered on purpose: "Not over MCP" and "Open
points".

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

An AI agent (Claude Code, VS Code, any MCP client) can do through MCP what a user can do in the client, with the role,
device group, checks and audit log of its access key; every change is a preview then a run (decision 2026-11-15). "MCP"
is the name of this feature in the UI (the rail page MCP & Assistant, card MCP; the PKI policy MCP server). The MCP server is part of the OADM server (core, free,
Apache-2.0), off by default. Plugins add their own tools through the SDK, so a new plugin is usable by agents without a
host change.

## Rules that never break

1. **The agent is a client.** Every tool calls the server's gRPC services in process with the access key's caller
   (`InProcessAccess`, `CallerContext`): `AccessPolicy`, device groups (`DeviceView`), validation, limits and the audit log
   apply exactly as for the client. A tool never reads stores, `VapixClient` or plugins around that layer.
2. **No change without a preview.** Every tool that changes something (devices, tags, rules, files, tasks) is two calls:
   a preview that checks all input on the server and returns the plan and a handle, and a run that takes only that
   handle. Nothing is written by a preview (a secret a preview declares is held for its run, never written). One
   exception: `oadm_files_upload_link` hands out a one-time link and writes nothing itself; the file sent to it is the change, and a task that uses the file is again a preview and a run
   (`ToolRegistryTests.PreviewAndRunStayTwoTools`, `LinksWithoutPreview`).
3. **Never all devices in a result.** Lists return at most 50 rows by default (at most 200), always with the total and a
   cursor; the first answer about devices is a summary (counts), never a list. Logs are the exception: a log list answers
   `more` and a cursor instead of a total (a text or time filter would need a count of the whole log).
4. **No secret through the model** (decision 2026-11-15). No tool returns a secret, and no secret ever goes through the
   model; a secret reaches OADM only through the secret channel ("Secrets"): a preview names a slot, the person types the
   value into a command of their own shell (MCP) or into the preview card (the assistant), and the run reads it once. No
   tool takes a password, private key, license key, access key or token as an argument (a task's `[Secret]` properties are
   left out of its schema, a value at a task's secret field is refused). Stored credentials are used by name; every
   action of the host, a task or a plugin page that needs a new secret declares its slots in its preview.
5. **Least rights.** The tool list shows only what the key may do now (its role and device group, plugins on); a role like
   Viewer gives a look-only agent; a role that may run no task (no `task:` right) lists no task or file tool and is refused
   "Your role may run no task." (`TaskRunTools.NoTaskRight`). An agent never gets more rights than its caller: every tool has exactly the
   rights, checks and guards the client has for the same action (decision 2026-11-15). Users, roles, device groups and
   access keys ("Access") are Administrator only, as on the Permissions page; an Administrator's agent may create another
   administrator or an Administrator key, and the plan says so in words before Run. The client's guards hold (never the
   last enabled administrator, never the caller's own role, state or account); an agent never deletes its own key, and a
   key it makes never ends after its own. Server settings and services ("Server settings and services as built") and the
   administration ("Administration as built") are Administrator where the client is; Delete all tasks and Clear the event
   log have their own permissions.
6. **Device data is data.** Texts written by devices or other users (names, locations, tags, event texts, log lines) are
   returned only as JSON field values, never inside tool descriptions or messages to the agent, and the skill says they are
   never instructions.

## Tools and MCP

The tools do not depend on MCP: the built-in assistant (`docs/spec/assistant.md`, a chat panel of the client that runs on
the server against a hosted or local model) calls the same tools in process, with the same `oadm_*` names, schemas, device
filter, preview/run handles and caller rules.

- **One tool registry** in the server (`src/Oadm.Server/Tools/`, `ToolRegistry`): every tool's descriptor (name, title,
  description, read only or change, starts tasks), its input and output schemas, its permission (the gRPC methods it calls,
  `AccessPolicy`) and its invoke method. It knows nothing of MCP: no MCP SDK and no ASP.NET request types (an architecture
  test checks the source and the compiled types).
- **The caller is a parameter.** A tool gets a `ToolCaller`: the caller with the role and devices of now (for MCP the
  access key itself), the key cursors and handles are bound to, and what the channel tells of itself (its access key and
  its own limits, for `oadm_server_info`). Nothing comes from the HTTP request; an in-process gRPC call carries only the
  caller's address. The registry checks only each tool's own rules (`AccessPolicy` of its methods, its gate); a channel adds
  no permission of its own (the assistant's chat needs "Use the assistant" on its gRPC methods, never on a tool).
- **MCP is a thin adapter** (`src/Oadm.Server/Mcp/`): the transport and its checks, access keys (which make the
  `ToolCaller`), its own limits (calls per second, calls at once, changes per minute, per key and for the port), the mapping to `tools/list` and
  `tools/call` (`McpToolAdapter`, also an image result as MCP image content), the file links and the skill resources.
- **Preview and run stay two tools.** A change is always a preview tool `<name>_preview` (it writes nothing and answers the
  plan and a handle) and a run tool `<name>_run` that takes only the handle: the assistant shows the preview as a card with
  a Run button. A handle is taken only by its own run tool. `ToolRegistryTests` checks all three lines: the tool layer's
  source and types name no MCP or ASP.NET, two tools run in process with a caller object, and every change is such a pair.

## Transport and certificate

- Streamable HTTP, MCP revision 2026-07-28 (stateless: no session id, no `initialize`), one endpoint `POST /mcp` on its own
  port (default **TCP 5082**), Kestrel in the server process, official C# SDK `ModelContextProtocol.AspNetCore` 2.x
  (`WithHttpTransport` stateless, `MapMcp`). No stdio, no SSE GET stream, no sampling, no MCP logging (deprecated).
- **Off by default.** Rail page **MCP & Assistant** (between Logs and Settings, every user; administrators change it), card **MCP** (the card Assistant stands right of it; agents change it with `oadm_mcp_set_preview`): Turn
  on, Port, Listen on (the interfaces that are up with an address other than IPv6 link-local, and the loopback;
  `McpListener.Interfaces`), the certificate, **Connect an agent** (see below), **Download
  skill**, **Access keys** (administrators). Off: the port is closed.
- **Certificate**: picked from the PKI Certificates list by the PKI server policy **MCP server**, like the EST and 802.1X
  servers (`docs/spec/pki.md` "Server certificates": a certificate with its key, server authentication, not ended, not on
  the not-accepted list; the policy refuses a certificate that does not name the server's addresses or host name). Its own
  certificate, never the shared "802.1X and EST server" (decision 2026-11-15): the first Turn on, while the policy has none,
  makes "MCP Server Certificate" (this computer's addresses and host name, plus `localhost`, `127.0.0.1` and `::1` for an
  agent on the server's computer, `PkiService.LoopbackNames`; the CA of the shared one, ECC P-256, server
  authentication), also without OADM PRO; the policy's Certificate field changes it afterwards. PKI never makes or assigns
  one for this policy at a start or a license change. The agent's computer trusts its CA once (**Download CA certificate** on the card; Claude Code: `NODE_EXTRA_CA_CERTS`, else
  the OS store). Without PKI running, the card says "PKI is not running. The server log says why." and MCP cannot be turned on.
- Origin check (403 for a browser origin that is not the server), `MCP-Protocol-Version` / `Mcp-Method` / `Mcp-Name`
  checked against the body (-32020), request body at most 1 MB (files go through upload links), answers at most 32 KB and
  not compressed.
- Windows service: the firewall rule for the port exists only while MCP is on (`FirewallRuleKeeper`,
  `docs/spec/security.md` "Service: firewall and folders"). Site nodes do not relay MCP.

## Access keys

Decision 2026-11-15 (redesign): a key is an identity of its own, made by an administrator, never a login of the user who
made it. There is no Read only flag and no permission "Use AI agents": the key's role decides what an agent may do.

- **Administrators only**, on the page **MCP & Assistant**, card **MCP**, button **Access keys** (left in the row of Turn on and Save; decision 2026-11-15), which opens the
  dialog **Access keys** (`Settings/AccessKeysWindow`: the list, Delete, Create access key; `AccessKeyService`, every
  method `AccessRule.Admin`). **Create access key** opens a dialog (`Settings/AccessKeyFormWindow`): Name (1..64 letters,
  digits, spaces, `-`, `_` and `.`, unique on the server, case-insensitive), Role (any role of the role list), Devices
  (All devices or one device group; an Administrator key always has all devices, the field is disabled with "Administrators
  always have all devices."), Expires (30, 90 or 365 days, default 90). Errors show below their fields; the server's refusals
  name their field (trailer `oadm-field`: name, role, devices, days). After Create the dialog shows the key once with Copy
  ("Copy it now. OADM cannot show it again."); Cancel becomes Close. At most 100 keys ("Use at most 100 access keys.
  Delete one first.").
- The list: Name, Role, Devices, Created, Expires, Last used and Delete (a red icon button; it asks "Delete access key",
  "Agents that use <name> can no longer connect.", the red Delete). Other roles see no access keys.
- **Stored** in the table AccessKeys (migration `AgentAccessKeys`): its id, the SHA-256 of the key (32 random bytes,
  base64url, prefix `oadm_`), name, role, all devices or the device group, who made it (for the audit only), created,
  expires, last used (written at most once a minute). Never a session: a session token is no key and a key is no session.
- **Who calls**: the key itself. Its calls run with the key's role and devices as stored at each call, so a role change or
  new group rules apply to the next call. The caller's user name, the audit user and the task owner read "MCP <key name>"
  (for example "MCP Lobby-bot", `AccessKeyStore.AgentNameOf`); its user id is the key's id (handles, limits, queued tasks:
  `DeviceViewFactory.ForUserAsync` finds the key's devices when no user has that id).
- **What ends a key**: Delete (at once, its jobs end too), its end date (the hourly cleanup removes the row; until then a
  refusal names the key), a restore (backups hold no keys). A role or device group a key names cannot be deleted: the
  refusal names the keys ("The access key Lobby-bot has the role Agents. Delete the key first.", the same for a device
  group), like a role that users have.
- The agent sends `Authorization: Bearer <key>`. Missing or ended key: HTTP 401 with "The access key is not valid. An
  administrator creates a new one in OADM on the page MCP & Assistant."; not allowed: the tool is not listed, and a call answers
  `isError` with `AccessPolicy.DeniedMessage`.
- Audit: every change, upload and download an agent causes as "MCP <key name>"; "Created access key" (the name; role,
  devices and the end in the detail) and "Deleted access key" by the administrator who did it; every failed key as "Access
  key refused".
- **Keys made by agents** (decision 2026-11-15, "Access"): an Administrator's agent lists, creates and deletes keys with the
  tools `oadm_access_keys_*`, through the same `AccessKeyService` calls as the dialog. A new key never comes back in a tool
  answer, a log line or the audit log: the run holds it encrypted with the master key in server memory for one fetch
  (`OneTimeSecret`), over MCP as `key_link` (a one-time `GET /files/<token>` link, 10 minutes, the caller's key only, the
  command saves it to `oadm-key.txt` in the user's shell; audited "Downloaded file"), in the Assistant as a card whose Copy
  fetches it once through `AssistantService.Download` straight to the clipboard (`AssistantRunReply.secret`); a second fetch
  answers "This access key was fetched already. ..." Guards, checked by the service itself (so every path holds them): a
  key never deletes itself (`AccessKeyTexts.OwnKey`, like a user's own account), and a key a key makes ends at the latest
  with the maker's own key (no agent extends its own life; the preview says the end). A key has no change method, so no
  agent can give a key, its own or another, another role or more devices; the Administrator role it may hold never
  changes.
- Limits (HTTP 429 with Retry-After), per key: at most 20 calls a second, at most 4 calls waiting at once and at most 10
  changes a minute ("Too many changes with this access key. Wait a minute."); the whole port takes at most 32 calls waiting
  at once. Every tool that is not a plain read counts as a change (`OadmTool.CountsAsChange`: a run, a task start, or a call
  that acts outside OADM such as the add preview). A call is counted as soon as its key is valid, before the role and device
  view are read. Open handles and links: at most 20 of a kind, 60 in all and 16 MB per key, the oldest go first. The skill
  needs no permission (any valid key). Camera pictures have their own limit, kept by the server for every channel
  (`LiveViewService.GetSnapshot`): 30 a minute per caller (a user's sessions together, each key alone); MCP answers 429 "Too
  many pictures. Wait a minute." with Retry-After 60. One `oadm_devices_snapshot` call can take up
  to three pictures (a smaller size when the first is over 1 MB). File transfers (PUT and GET on `/files/`) have their own
  places, 2 at once per key and 8 on the port, and never take the 32 places of `/mcp`; a transfer must move at least 1 KB a
  second after 10 seconds, and end within 2 minutes plus its size at 100 KB/s (15 minutes when the size is not known), or
  it ends ("The transfer took too long and was ended.", 408) and its place is freed. Changes and pictures are counted from
  the call's own tool name, not from a header; a call the tool list refuses (the role) and an unknown tool count against no
  limit. `oadm_server_info` names the change limit `changes_per_minute`. The MCP host logs ASP.NET requests at Warning
  only, so no upload token reaches the server log.


## Set up a desktop app

The MCP card opens the existing access key form with the desktop setup title.
An administrator chooses role, devices and lifetime. Published client builds create the OADM desktop extension from this
repository. Install `OADM.mcpb` in the desktop app, configure the MCP HTTPS address, and enter the access key in its sensitive
field. If the app does not already trust the MCP server CA, select the CA certificate downloaded from OADM. This uses the
normal access key permissions and preview/run behavior. Delete the key under **Access keys** to disconnect server access,
then remove the extension in the desktop app.

## Tools

One tool per user action, not per gRPC call, named `oadm_<area>_<action>` (`[A-Za-z0-9_]`, at most 64 characters).
Every tool has a title, a description of one to three sentences (what it is for, what to call before or after), a strict
`inputSchema` (JSON Schema 2020-12, `additionalProperties: false`, enums and formats such as `ipv4`) and an
`outputSchema` with `structuredContent` plus the same JSON as text. Annotations are honest: reads `readOnlyHint`; previews
read only; runs `destructiveHint` true unless the action only adds (tag, rule) and `idempotentHint` where it holds;
`openWorldHint` false (OADM's own devices).

### Devices and the device filter

Every tool that works on devices takes one **device filter**, resolved on the server inside the caller's device group:

```json
{ "ids": [], "search": "lobby", "tags": ["Lobby"], "sites": ["Parking"], "models": ["M3085-V"],
  "firmware": { "below": "12.11" }, "status": ["Ok", "Unreachable"], "categories": ["Camera"],
  "clock": ["off"], "clock_offset_over_seconds": 10, "ntp": ["NotSynced"],
  "certificate": { "expires_within_days": 30, "trust": ["SelfSigned"] }, "socs": ["A8"],
  "vulnerabilities": { "state": "affected", "min_severity": "High", "cve": "CVE-2024-6509" } }
```

**What OADM knows comes from OADM.** Every fact the device grid shows that the server holds without asking the device (the
clock against the allowed difference and NTP, the certificate's end and trust, the chip, the AXIS OS and its CVEs) is a
filter, a count and a row field, so an agent never sends a VAPIX command for it. A filter on such a fact adds its fields to
the rows by itself (`DeviceTools.FieldsOf`): the clock filter `clock`, `clock_off`, `clock_offset_ms`, `ntp`; the
certificate filter `certificate_expires_in` (the column's text), `certificate_state`, `certificate_trust`; `socs` the
`soc`; a vulnerability filter `vulnerabilities` (the column's text). The filter runs on the server (`DeviceSearch`,
`Devices/DeviceFacts`), so it holds for previews and task plans too.

All fields optional and combined with AND; an empty filter means every device the caller sees: a list of them comes page by
page, every page with the total and a cursor. `"selection": true` means the devices the user selected in the client's
device grid (`ToolCaller.Selection`, sent by the assistant with every message and cut to the caller's view; with `ids`
as well, the ids that are both). The registry applies it after the schema check (`SelectionTools.Apply`); nothing
selected is a refusal ("No devices are selected. Select them in the device list first."), never every device. Over MCP
nothing is selected.

- `oadm_server_info`: version, who calls ("MCP <key name>"), role, the permissions, the limits, the device count. The first call.
- `oadm_devices_summary(filter, group_by)`: counts by status, model, AXIS OS version, site, tag, category, chip, clock,
  NTP, certificate, vulnerabilities or CVE (at most 50 groups, the rest as "Other"), and always `needs_a_look`: not Ok,
  clock off and not measured, certificates expired, expiring soon (the device's warning days) and not trusted, vulnerable,
  Critical or High, possible only. One call answers "how is my fleet".
- `oadm_devices_search(filter, fields, sort, cursor, limit)`: rows with id, address, host name, UPnP name, model, AXIS OS
  version, status and the requested fields (also `clock`, `clock_off`, `ntp`, `certificate_expires_in`,
  `certificate_state`, `vulnerabilities` as the grid shows them) and those of the filter; `total`, `next_cursor`. A device has no one name: its address, host name and
  UPnP name are separate fields and the search matches each. The cursor is the last row's sort key and id, bound to the key
  and the search, so a device added, removed or changed elsewhere never moves the rows between pages. Truncated answers say
  how to narrow ("312 more. Add a filter, e.g. site or model.").
- `oadm_selection(fields, cursor, limit)`: the selected devices as pages of `oadm_devices_search` rows; over MCP an empty
  page with a note.
- `oadm_devices_get(device_id)`: one device with its details (the client's device details, no credentials).
- `oadm_devices_snapshot(device_id)`: one JPEG of the camera as image content (Live view permission, at most 1280 px wide).
- Refresh, Remove, Set credentials (by stored credential name), Add devices (Discovery, Network range, Add manually and
  Import from file as up to 1,000 addresses, the waiting list; logins with the credential list; only addresses inside a
  network of the server or of a connected site, at most 200 devices a run) as preview/run pairs; the waiting list itself
  (list, remove).
- Logins typed by the person (decision 2026-11-15, `Tools/LoginTools`, the password always through the secret channel):
  Log in (`oadm_devices_login_preview`/`_run`: a user name, slot `password`, `DeviceService.LogIn`, stored where the
  device accepts it; optionally into the credential list for administrators), First password
  (`oadm_devices_first_password_preview`/`_run`: only devices with Password not set, at most 1,000, the devices' passphrase
  rules in `rules`, checked when the password is entered, `SetFirstPassword`), the add run's `user_name` (a typed login
  tried with `RetryAuth` where the credential list fails, never saved) and `first_password` (factory-default devices added
  with the entered first password, checked against each device's policy, `first_password_rules`); the credential list
  `oadm_credentials_add_preview`/`_run` and `oadm_credentials_remove_preview`/`_run` (Administrator with all devices, by
  user name, slot `password`; a password is never shown).

### Tasks

- `oadm_tasks_catalog(filter)`: the tasks the caller may run with, per task, how many of the filtered devices can run it
  and the reasons of the others summed up (`NotSupportedReason`, as the context menu), and the task's input schema.
- `oadm_task_preview(task, filter, input)`: checks the input (the plugin's `ValidatePayload`, see "SDK"), resolves the
  filter to a fixed device list and answers the plan: `will_run` count with the first 20 devices, `skipped` grouped by
  reason ("No file for this model.", "Already up to date", "Needs AXIS OS 11.11 or later"), the task name
  (`GetTaskName`), warnings (the dialog's warnings, e.g. a network change that can make devices unreachable), `files`
  and `details` (the plan's lines, e.g. the package each device group gets; the Assistant's card lists them), and a
  **handle** (10 minutes, once, bound to the key; the input stays in memory only, like every payload). Invalid input:
  `isError` with one entry per field (`{"field": "ip", "error": "Enter an IPv4 address."}`, the dialog's texts).
- `oadm_task_run(handle)`: the run; re-checks the permissions and the role's `RequiredPermissions`; answers the batch id
  and the task count. Each task still re-checks its device (`Require`, HARD RULE device safety).
- `oadm_tasks_list(batch, device_id, status, plugin, owner, text, since, cursor)`, `oadm_tasks_get(task_id)` (steps and
  per-task log), `oadm_tasks_cancel` (preview/run). Waiting for tasks: the agent polls `oadm_tasks_list(batch)`, which
  answers counts per state first.
- `oadm_tasks_summary(filters, group_by)`: the task history OADM keeps (the caller's devices, the list's filters) summed
  up: counts per state, the oldest task, and at most 50 groups by result message (default), state, task name, plugin,
  owner, device or day, the largest first, each with its failed count, device count, newest time and an example task id;
  the rest as `other`. "Which tasks failed and why" is one call with `status: ["Failed"]`.

### Other areas

Each a few tools with the same rules (summary or search first, preview/run for changes): tags, sites, compliance
(results, checks), vulnerabilities (read, `Tools/VulnerabilityTools`, docs/spec/vulnerabilities.md:
`oadm_vulnerabilities_summary(filter)` the list's date, source and size, the devices affected (counted), possible only and
not affected, affected devices by their worst level and the 10 CVEs that affect the most devices;
`oadm_vulnerabilities(filter, cursor, limit)` the devices with any CVE unless the filter says otherwise, each with its
counted CVEs (id, level, CVSS, fixed in) and its possible ones apart, pages with the total and a cursor; the same matcher
and inputs as the client's column); `oadm_vulnerability(cve)` what one CVE is from OADM's list (description, level, CVSS, published, the
https advisory, at most 50 models it names, the caller's devices counted and possible), nothing goes online), event rules (the catalog of events and actions `oadm_rules_catalog`, list, get, create, change,
delete, the event log), logs (server log search with `since`, `level`, `text`, at most 200 lines; audit log), files
(`oadm_files_upload_link`, list, delete), settings (`oadm_settings_get`, and their changes, see "Server settings and services
as built"), PKI policies (`oadm_pki_policies`, a tool of the PKI plugin), the user docs (`oadm_docs_search(query)`: at most 5 pages of oadm.app with title, address and the best
passage, from the index the server is built with, `Assistant/DocsIndex`; no gRPC method, so every caller has it).

`oadm_server_status` (read only, every signed-in caller): what the Plugins page, the License card and the page MCP &
Assistant show every user, through the reads `LicenseService.GetLicense`, `PluginService.ListPackages`, `McpService.Get`,
`AssistantService.GetSettings` and `VulnerabilityService.Get`: OADM PRO (status, plan, device limit, end, trial), the
plugins (on by default, `all` or `none`) with version, group, PRO and license state, MCP on and running, the assistant on
with its provider and the picture setting, the date, count and source of the list of Axis vulnerabilities. Never the
server ID, a customer, a license key, an API key or a certificate. The tools name no MCP type: the host passes the read of
`McpService.Get` as a function.

### Access

Users, roles, device groups and access keys (decision 2026-11-15: AI agents manage them with the caller's rights). Every
tool calls the Administrator-only services of the Permissions page and the dialog Access keys in process as its caller, so
only an Administrator's key or session lists them, and the services' checks, refusals and audit apply. Every change is a
preview that checks what the page checks and writes nothing, and a run with only its handle; plans that raise rights say so
in words ("anna becomes Administrator and can change everything in OADM, also users and access keys.", "The new key has the
role Administrator and all devices: ..."). Passwords only through the secret channel ("Secrets"); a new key only through its
one-time link or card ("Access keys"). Tools and guards: "Access as built".

**OADM's data first.** OADM already holds most facts (the polled device data, the task history, the logs, the last
reads of its pages). The tools answer from it; a tool that asks the devices (a VAPIX command, a snapshot) is the last
resort, and the skill says so. A page's read tool answers what the page read last; the page's own Read or Check is a
tool of its own (see "Page actions").

### Plugins

Core plugins and task plugins bring their tools through the SDK; the host prefixes them `oadm_<plugin area>_` and lists
them only while the plugin is on and the role has its permission. Bundled plugins get tools for their pages (e.g. PKI
certificate list and Create certificate, NTP and DHCP status and leases, Hardening scan results, VAPIX Commander commands
and their run, Device configuration's settings, action rules and stream profiles of many devices). A task plugin without a payload schema is not offered over MCP (fail closed).

The page reads of the bundled plugins (read only, the page's own rights and device group rules, nothing read from a device):

- `oadm_edge_storage_cards(state, used_above)` (Edge storage, "show"): the SD card table the page read last for the
  caller's device scope, with the five counts of the page's cards; rows by device id.
- `oadm_app_management_applications(application, status)` (Application management, "View applications"): the rows the
  Applications page read last in the caller's login session (an MCP key has its own session, so nothing until a page
  read; the assistant has the user's), of the devices the caller sees now: per application its device count, running
  count, versions (at most 10) and license problems (at most 30 applications), then one row per device and application.
- `oadm_est_enrollments(result, text)` (EST server, Operator): its state, the certificates it issued, how many cameras did
  not renew in time, and the enrollments of the caller's devices, newest first. While the server is off: its state only.
- `oadm_radius_log(result, text)` (802.1X server, Operator): its state and its log of the caller's devices, newest first
  (switch, port, MAC, device, result, reason, VLAN, the serial of the device's certificate). Never a shared secret or a
  certificate. While the server is off: its state only.
- `oadm_device_rules_list(search, enabled, device_id)` (Action Rule Editor, "show"; its area is `device_rules`, apart
  from OADM's own event rules `oadm_rules_*`): the action rules on the devices as the editor read them last for the
  caller's device scope: per rule name the devices that have it and run it (at most 30 names), then one row per device
  and rule (event, conditions, action, the device's recipient and schedule counts). Passwords are empty in the page's
  rows already and never named.
- `oadm_device_triggers_list(search, problems)`, `oadm_device_triggers_devices(trigger_id, connection)` (Device triggers,
  "show"): the triggers of the caller's scope with their counts, and one trigger's devices with how OADM receives the
  event from each (the page's Get, so another scope's trigger answers like an unknown one).
- `oadm_ocr_readers(search, problems)`, `oadm_ocr_reads(reader_id)` (Text recognition, "show"): the readers with the
  last text and problem, and the recent reads of one as text (at most 50, never a cut-out or picture).
- `oadm_vision_pipelines(search, problems)` (Vision Center, "show"): the pipelines with mode, rate, problem and live
  summary, and the load of the caller's pipelines; never a picture or a model.

**Every plugin is readable.** A new page, column or plugin view gets its read tool in the same change: each bundled core
plugin declares `ICorePlugin.Tools` (`McpPluginCoverageTests` reads the source of every bundled plugin); a package
without a core plugin (a task plugin, an event rule action) is covered by the task tools (`oadm_tasks_catalog`,
`oadm_task_preview`, `oadm_tasks_get` with its steps and log) and `oadm_rules_catalog`. The exceptions carry their reason:
Web View (the device's own web interface), System report (its reports belong to one user's session) and the event rule
action plugins Notifications, Outputs, MQTT, Modbus and OPC UA (`oadm_recipients_list`, `oadm_io_ports`).
Live View offers its saved views; its video stays in the client.

**VAPIX Commander** (`oadm_vapix_commands`, `oadm_vapix_command_preview` / `_run`, `CommanderTools`): library and saved
commands by id (`library:<id>`, `saved:<id>`); a request of the agent's own is the device request below. Not offered: a
dangerous command (`RequestSafety`, of the definition and again of the filled-in request), one with a file field or more
than 8 password fields, and one that changes what OADM's own tasks change (date and time, NTP, applications, AXIS OS,
restart, users, network; their reads stay). A password field is never a value of the agent ("Leave the password out. The
user enters it."): the preview names a secret per password field (the field's name, its label in lower case), the run
takes the entered values, never a stored password of a saved command.
Every command, a read too, goes preview then run. The preview checks the values (the page's renderer) and plans the chosen
devices from cached data (an id outside the caller's view refuses the call; Certificate changed, Site offline and a missing
API skip a device); the run is the page's rollout, one task per device, with the user's OK of the preview as the
confirmation, and needs the plugin's "Run on many devices" right. At most 1,000 devices a run. An agent's run masks secret
values as `***` in its steps and log (`RolloutPayload.MaskSecrets`, `SecretValues`): a value whose label, parameter or path
names a secret (community, password, key, token, ...), never a yes/no value.

**Device request** (decision 2026-11-15, replaces "never a request of the agent's own"; `oadm_device_request_preview` /
`_run`, VAPIX Commander's `DeviceRequestTool`, area `device` by `PluginTool.Area`): one VAPIX request the agent writes
itself (method GET, POST, PUT, PATCH or DELETE; a path under `/axis-cgi/`, `/config/rest/` or `/vapix/` that
`CommandValidator.IsDevicePath` accepts, without `?`; query pairs; a body of at most 64 KB with its media type; a wait of 1
to 60 s) on the devices of the device filter. It is an inline command sent with the page's own code (`CommandRenderer`,
`RequestSafety`, `Compatibility`, `SendCheckedAsync` of Send: a fresh API list before a write), never a second VAPIX client;
the API is the known path's (as the request builder fills it in), else `param-cgi` in any version. Rules:
- The plugin's right "send" (Send to one device, full device control); every request, a read too, is a preview and a run.
- No secret in the request: a query pair, form pair, JSON property or XML element whose name points to a secret
  (`SecretValues.IsSecretName`, plus `pwd`, `pass`, `pin`) with a value other than yes/no is refused "This request carries
  a password or another secret. Set passwords in OADM.".
- A request that can lock OADM out of a device or break it (`RequestSafety` Dangerous: accounts through `pwdgrp.cgi`,
  network settings, `param.cgi` `Network.*`, `HTTPS.*`, `IEEE8021X.*`, factory default, AXIS OS; and any change of a path
  whose words name accounts, the network, HTTPS, TLS, certificates, 802.1X, AXIS OS or a reset, such as
  `/config/rest/network-settings/...`) needs the plugin's right **Send dangerous requests** (`dangerous`, not an Operator
  default, full device control), else "This request can lock OADM out of the device or break it (accounts, network, HTTPS,
  802.1X, AXIS OS, factory default). It needs the right Send dangerous requests."; with it the plan says `dangerous` and
  warns. A restart (`restart.cgi`) needs no such right and warns that the devices are offline for a few minutes.
- One device first (user decision): a change (not a read) runs on more than one device only after the same request (method,
  the path as the device reads it, query, media type and body) succeeded on exactly one device (2xx, no device error) for
  the same caller (its user name, "MCP <key name>" for a key), kept 24 h in plugin memory (`DeviceRequestTrials`, at most
  1,000); else "Run this request on one device first. When it succeeded there, run it on more devices.". Reads run on
  many devices at once.
- At most 100 devices, 8 at a time. The plan: read or change, the request line and body (secret values `***`), `will_run`,
  `will_change` and `unit` for the Assistant's card, the first 20 devices, the skipped ones by reason (Certificate changed,
  Site offline, a missing API), the warnings. The run answers per device status, ok and error (the first 50 devices), the
  bodies of the first 10 (pretty JSON or XML, binary as type and size), cut so the answer stays under 28 KB, every value
  whose name points to a secret as `***` (JSON, `name=value`, XML elements and attributes, the interpreted parameters).
- Audit: the run as "Device request POST /axis-cgi/param.cgi on 3 devices" (`ICorePlugin.AuditDetail`), never the query
  values or the body; the server log has the masked request line per device.

**Monitoring and maintenance pages** (read only, what each page shows, with the page's right, the caller's devices and
counts first; decision 2026-11-15 "the agents see everything OADM shows"): `oadm_edge_storage_cards`,
`oadm_image_health_results` (the last read or check the page made, never a camera read by the tool),
`oadm_recordings_search` (the page's search of one camera), `oadm_io_ports` (one read of one device's ports, no stream),
`oadm_metadata_streams` (the streams pages have open and their last messages; it opens none), `oadm_dashboards_list` and
`oadm_dashboards_values` (the values the widgets show now, nothing watched), `oadm_load_test_checks` and `oadm_load_test_check`,
`oadm_snapshot_report_sources` (1 to 20 cameras). The host tool `oadm_recipients_list` (`Tools/RecipientTools`, rights of
`ListRecipients`, users with all devices) lists the recipients of the action plugins (Notifications, MQTT, Modbus, OPC UA)
with the target as scheme, host and port, `has_password` only, the rules that use them and the connection of a persistent
type (the first answer of `WatchRecipientStatus`). Not offered: System report (its jobs belong to a user's session and
hold the devices' configuration), Outputs (an action without state; `oadm_io_ports` shows the outputs), live streams and
video (they stay in the client); exports and reports are files ("Files", decision 2026-11-15: the snapshot report is a preview
and a run of its job, its PDF a file). A read's name never ends in `_preview` or `_run`,
which name the two halves of a change (the load test's are `checks` and `check`).

**Page actions** (decision 2026-11-15: AI agents, MCP with the key's role and devices and the Assistant with the signed-in
user, can do everything a user can on the plugin pages; this drops the earlier rules "starting reads, checks, streams,
exports and reports stay in the pages" and "a page tool answers what its page read last, never a read of its own", except
that live streams and video stay in the client). Each action wraps one declared page method with the SDK's
`PluginPageTools` (see "SDK"): the tool has the page method's role, permission, device group rule and audit
(`RulesMethodOf`), its input is a strict typed record, its checks are the page's own code and its refusals the page's
texts. A read, or a start the page does without asking (a scan, a check, a read of the devices), is one tool and may
refresh what the page shows; everything the page confirms or that changes a device, a setting or something a user saved
is a preview (the page's validation, nothing written) and a run (validated again, the page's code with the caller). A
start on devices takes the device filter or `all_devices` (more than 500 matches need `all_devices`). Plans count what
changes (`will_change`, `unit`) for the Assistant's card. Bundled:

- Hardening scan: `scan_start`, `scan_stop` ("Run scans"), `detail` (one device's checks, "show").
- VPN Fleet: `results` (paged), `read`, `releases`, `settings` (never a secret value), `install` and `settings_save`
  (refuses secrets: "change secrets on the VPN Fleet page"); setting up many cameras with keys stays on the page.
- Image health: `check` (Check now). Edge storage: `read` (the storage task stays a task). Applications: `read`, `log`
  (newest lines of one device's application), `settings` (grouped values, shown only with the right to change ACAP
  settings, never a secret value); settings writes and installs stay tasks.
- Action Rule Editor: `read` (Refresh), `recipients`, `schedules`, `recipient_kinds`, and `recipient_create`,
  `recipient_change`, `recipient_copy`, `recipient_delete`, `schedule_create`, `schedule_change`, `schedule_copy`,
  `schedule_delete` (the page's runs: one editor task per device, kept for undo). A recipient password is named in
  `set_passwords` of `recipient_create` / `recipient_change` and entered by the user (a slot per setting, labels such as
  "password", "HTTP headers"); a password in `settings` is refused, a login in an address is set in OADM ("Set this in
  OADM."); a copy takes the source's password on the server. Rule deploy, delete and undo are the editor's task.
- Device triggers: `events_read` and `events` (the editor's read of the device events, then its result), `save`,
  `set_enabled`, `copy`, `delete`. Dashboards: `get`, `events`, `save`, `delete`, `press` (a change: its event goes to
  the event rules, and the preview says whether a rule uses it).
- Text recognition: `reader_save`, `reader_delete`, `reader_read_now` (a change: it fires the reader's events; answers the
  text, never a picture). Vision Center: `pipeline`, `models`, `class_lists` (reads; never a model file or a picture),
  `pipeline_save` (the editor's `VisionGraph.Check`, one error per node), `pipeline_copy`, `pipeline_delete`,
  `class_list_save`, `model_upload` (a `file_id` of the caller's uploads, through the page's upload steps and model check:
  standard operators only, no external data, the limits; audited like the page), `model_update` (name and classes; the page
  replaces no model file), `model_delete` (refused while pipelines use it), `use_gpu`. Text recognition: `use_gpu`.
- Load test: `plan`, `start` (the page's limits: 1,000 streams, batches of 200; the exposure always goes back), `cancel`,
  `check_delete`. DHCP server: `static_lease_save`, `static_lease_delete`, `lease_make_static`, `lease_release`,
  `settings_save` (on/off, interface, range, automatic add; the plan warns that a DHCP server hands out addresses to every
  device that asks; the run changes nothing and names another DHCP server that answers unless `confirm_other_servers` lists
  it, the page's Enable anyway). NTP server: `settings_save` (on/off, interface, time source, Serve at sites; the run asks
  the time source once, like the page).
- EST server: `enrollments_clear`, `enable` (Turn on needs OADM PRO), `settings_save`, `profile_save`, `profile_delete`.
  802.1X server: `sessions`, `log_delete`, `switches`, `enable`, `settings_save`, `switch_save` (the shared secret kept,
  entered through the secret channel as `shared_secret`, or generated by the run and handed out once as a file, never in an
  answer; the plan warns to set a new secret on the switch too), `switch_delete`.
- PKI (Administrator with all devices; a CA by its row id `ca:<SHA-256>`): `all_policies` (read), `ca_create`, `ca_import`
  (`file_id` and `key_file_id` of the caller's uploads; the PKCS#12 password through the secret channel when the file needs
  one), `ca_delete` (the page's refusal while a policy names the CA), `ca_key_backup` (password through the secret channel;
  the run hands out the PKCS#12 once over a download link, `ToolFileStore`, the only private-key file a tool gives),
  `server_trust_install` (root CAs; warns that the server trusts everything the CA signs), `policy_save`, `policy_delete`
  (dry runs of the Policies tab's checks, `PolicyStore` `dryRun`; Block and VLAN warn). A CA certificate without its key is
  imported in OADM.
- VAPIX Commander: `saved_save`, `saved_delete`, `saved_import`, `saved_export` (the page's command file, at most 24 KB,
  secret values as `***`, password values left out; a password value in a command is refused, a password to store is named
  in `set_passwords` (save: field names; import: command id and field), entered by the user and stored encrypted like the
  page's; without the master key's protector the preview refuses "OADM cannot store passwords for saved commands here.").
- Live View: `views`, `view_save`, `view_delete` (saved views; no tool opens video).

### Not over MCP

Live video, Live View tabs, Dashboards live updates, Metadata Monitor and I/O Monitor streams (their state and one read
are offered, see "Plugins"), continuous pan and tilt (a held button), Web View, System report; Forget server (a client's
own list); the credential list's passwords (its entries are added and removed by tools); a role's start page; Import from
ADM ("Open points"). Everything else a user can do is a tool (decision 2026-11-15).

## Files

`oadm_files_upload_link(name, size)` answers a one-time HTTPS URL on the MCP port (10 minutes, one file, the file limits
of `FileService`, PUT with the same Bearer key) and the command for the agent's shell (`curl --cacert oadm-ca.pem -H
"Authorization: Bearer $OADM_KEY" -T <file> <url>`). The file never passes the model. The upload answers the file id and
SHA-256, used in a task preview like a file uploaded in the client (`IUploadedFiles`).

**Downloads** (decision 2026-11-15; never a private key or a PKCS#12 file, with one exception: Back up a CA key of PKI,
whose file is protected with a password the person enters through the secret channel, "Server settings and services as
built"; a backup only to an administrator, encrypted with its password, "Administration as built"):

- A tool that makes a file answers a **file reference** (`ToolFiles.Offer`: `file_ref`, `name`, `size` or null,
  `content_type`, `expires`), never the bytes. The reference is bound to the caller (key or session), good for 10 minutes
  and may make several links (`ToolHandles.AddOffer`/`FindOffer`); the bytes are made only when a link is fetched, through
  the gRPC download of the area as the caller of then (its rights, device group and audit).
- `oadm_files_download_link(file_ref | task_id)` (read, signed in; exactly one of both): a one-time `GET /files/<token>`
  link of the reference, or of a task's result file (`TaskService.GetResultFile`, only the task's owner and
  administrators), with `name`, `size`, `content_type`, `expires` and the shell command (`curl --cacert oadm-ca.pem -H
  "Authorization: Bearer $OADM_KEY" -o <name> <url>`). The assistant has no links: a file reference in a tool answer is a
  file card with Save there (`AssistantEvent.file`, `AssistantService.Download`, docs/spec/assistant.md).
- **Task result files**: a task keeps one file as its result (SDK `ITaskExecutionContext.SaveResultFileAsync(name, type,
  stream)`, at most 64 MB; core `TaskResultFiles` in `<datafolder>/task-results`, kept a day or until the server restarts,
  1 GB for all, the oldest first, deleted with the task; the task log says "Kept the result file <name>."). Any task with
  a result works without a change of the host: `oadm_tasks_get` answers `result_file` (a reference, or null).
  `TaskService.DownloadResult` audits the download itself.
- **Plugin files**: a plugin tool with `PluginTool.ReturnsFile` puts a `PluginToolFile` (one of its downloads,
  `ICorePluginDownloads`, with its payload) into the answer's `file`; the host replaces it by a reference whose link calls
  `PluginService.Download` in process as the caller (the download's permission and the plugin's audit).
- **Producers** (2026-11-15): `oadm_devices_export(filter)` (the device list as CSV, the columns of Export devices the
  server knows, the permission Export the device list), `oadm_compliance_export(filter)` (the Compliance page's export:
  device columns, then one column per check that is on), both written at download time from `DeviceService.Search` and
  `ComplianceService` (`Tools/ExportTools`, the shared `Oadm.Contracts.Text.CsvWriter`); a task's `result_file`;
  `oadm_recordings_export` (Edge Recording Export, the download `export`), `oadm_snapshot_report_report_preview`/`_run`
  and `oadm_snapshot_report_report_file(job_id)` (the report PDF of the caller's job, download `downloadReport`),
  `oadm_pki_certificate_file(id, format)` and `oadm_pki_ca_file(previous_id, format)` (PEM or DER of the certificate only,
  downloads `downloadCertificate`, `exportPublic`, `exportPrevious`; Administrator), `oadm_hardening_results_csv` (download
  `resultsCsv`).
- Every download over a link is audited "Downloaded file" as the caller ("MCP <key name>", or the user from the
  assistant), once (a source that audits itself, such as `TaskService.DownloadResult`, is not audited twice).

## Secrets

Decision 2026-11-15: a password, a PKCS#12 password, a license key or an SD card passphrase can be set by an agent's user
without ever passing through the AI model.

- **A preview declares a slot.** After it made its handle, the preview calls `ToolSecretAnswers.Declare(call, answer,
  handle, runTool, [new ToolSecretSlot("password", "password")])` (`Tools/ToolSecrets.cs`): at most 8 slots, each a name
  (lower case words with underscores) and a label in lower case ("password", "PKCS#12 password"). The answer gets
  `needs_secrets` (the names) and, over MCP, `secret_links`: per name a one-time `url` (`PUT /secrets/<token>`), `command`
  (POSIX shell: asks with `read -rs` and sends with `curl --data-binary @-`), `command_powershell` (PowerShell 7,
  `Read-Host -MaskInput`) and `expires`. List `ToolSecretAnswers.OutputProperties()` in the preview's output schema.
- **The person fills it.** MCP: the agent gives the user the command; the user runs it in their own terminal, so the
  value goes from the keyboard to OADM and the model sees only the command and `{"secret": "password", "set": true}`. The
  link takes PUT only, with the same access key (another key's link answers like an ended one and stays), once, at most
  4,096 bytes of UTF-8 (one line end at its end is dropped; more: 413), on the transfer places of `/files/`; audited "Set
  secret" (the slot's label, "For <run tool title>") as "MCP <key name>", never the value. The assistant: the preview card
  shows a password field per slot; the value goes with Run (`AssistantRunRequest.secrets`).
- **The run reads it.** The registry refuses a run whose handle still misses a slot ("Enter the password.", the field
  `secrets.<name>`) and leaves the handle; otherwise it takes the values once and hands them to the run tool, which reads
  them with `call.Secret("password")` and never echoes one in an answer, a log line or the audit log.
- **Held** in server memory only (`ToolSecrets`), encrypted with the master key (AES-256-GCM, the binding key, the handle
  and the slot as associated data), bound to the caller and the preview's run tool, ended with the handle (10 minutes,
  `FakeTimeProvider` in the tests), at most 20 previews with slots per caller; zeroed when taken or ended. A preview whose
  slots went for newer ones (its handle may live on: handles count per kind) refuses its run, "This preview has ended or
  already ran." (`ToolSecrets.WasDropped`): a run never goes on without the secrets its preview declared.
- **Checked when entered.** A slot may carry the run's own check of a value (`ToolSecretSlot.Check`: a passphrase policy, a
  PKCS#12 password that opens its file, the plugin's rules). It runs before the value is taken: MCP answers 400 with the
  refusal and the link stays; the assistant's card shows it below the field and stays. The run checks everything again.
- **Task secrets** (`oadm_task_preview`/`_run`, SDK `ITaskPlugin.SecretsOf`): the task names what a run of the payload
  needs (`TaskSecret`: name, label, field such as `password` or `settings[2].value`; default: the top-level `[Secret]`
  properties, `[Secret(Label = "passphrase")]`). `TaskService.Plan` answers them (`secrets`, never a value) and leaves
  out the payload's errors at their fields; the preview refuses an agent's value there ("Leave this empty. The user
  enters it through the secret link.") and declares one slot each for `oadm_task_run`, whose check is `Plan` with
  `check_only` and the value at its field (the error at that field). The run puts the values into the payload at their
  fields for `TaskService.Run` only (in memory, `TaskSecretPaths`); the handle keeps the payload without them. Payloads are
  never persisted, the task name, steps and log never hold a value, and `TaskRun` is audited with the task and the count.
- **Plugin tool secrets** (SDK `PluginToolSecret`, `PluginTool.Secrets`, `PluginToolPlan.SecretsNeeded`,
  `PluginToolInput.Secret`): a change declares the secrets its run may need (`PluginTool.Secrets`, at most 8); its plan
  chooses those this run needs in `secrets_needed` (some of the declared ones or secrets it makes itself, such as one per
  password field of a saved command; empty: none; absent or null: all declared). The host takes `secrets_needed` out of
  the answer (and of the published output schema), declares the slots for the run tool, checks an entered value by calling
  the preview method with that one value in the payload's `secrets` (a field error at `secrets.<name>` refuses it) and
  hands all values in `secrets` to the run method. A caller never sends `secrets` (the strict schema; the host removes it
  from every payload it did not make). A list the host cannot use (more than 8, a bad name, a name twice, no label)
  refuses the preview. The plugin's audit detail and answers never name a value.
- The test tool `SecretDemoTools` (tests) shows the whole path over MCP and the assistant; `McpTaskSecretTests`,
  `McpPluginSecretTests`, `McpPluginSecretUploadTests` and `AssistantTransferTests` the task and plugin paths; `SecretScan` checks a canary against the
  answers, the audit log, the server log, the tasks (names, steps, logs) and every file of the data folder.

## SDK

New default interface members (SDK minor version up, `docs/spec/plugins.md`):

- `ITaskPlugin.PayloadType` (`Type?`): the payload record; the host exports its JSON Schema (`JsonSchemaExporter`, with
  `[Description]` and validation attributes) as the tool's input. Null: not offered over MCP. `ITaskPlugin.PayloadJsonOptions`:
  the serializer options the plugin reads the payload with (its names and enum texts); null: `PluginJson.CreateOptions()`.
- `ITaskPlugin.ValidatePayload(string payloadJson, ICallContext caller)` → field errors (the dialog's rules, reused by the
  dialog view model). Called by MCP preview **and by `TaskService.Run`**, which then refuses an invalid payload with
  INVALID_ARGUMENT before any task starts.
- `ITaskPlugin.PlanAsync(payload, devices, ct)` → per device run or skip with a reason, from cached data only (no device
  request). The server-side twin of `ITaskPluginDialog.ShowForDevicesAsync` (Upgrade AXIS OS: `FirmwareCompatibility.Choose`).
  Default: `CanRun` / `NotSupportedReason`.
- `ICorePlugin.Tools` and `ICorePlugin.ToolArea`: tool descriptors (`PluginTool`: name, title, description, input and
  output schema, read only or change, permission), called through the plugin's own `InvokeAsync` with the caller
  (`tool.<name>`). Changes get the preview/run pair from the host; the plugin implements `tool.<name>.preview`, which never
  writes. Nothing in the SDK names MCP: the Assistant uses the same tools.
- `PluginPageTools<TOwner>` (`src/Oadm.Sdk/Plugins/PluginPageTools.cs`, "Page actions"): declared page methods as tools,
  never a bridge to any method. `Read<TArgs, TAnswer>` is one tool; `Change<TArgs, TPlan, TResult>` a check (the page's
  validation, the preview's answer, never writes) and a run (the check again, then the page's code with the caller and the
  plan). It reads the arguments (broken JSON refused), checks their validation attributes, hands `PluginToolInput` (the
  arguments, `Devices`, `Page`), turns `PluginToolInputException` into field errors, and maps a tool method to its page
  method (`PageMethodOf`, `RulesMethodOf`) for the plugin's rules. `PluginToolPlan(WillChange, Unit)` is the base of a plan,
  so the Assistant's card says "1 reader" (`AssistantCards`). Tests: `McpPageActionTests` and each plugin's `PageToolTests`;
  `McpAddOnToolSchemaTests` checks the schemas of the optional plugins without starting them.
- `PluginTool.Area` (decision 2026-11-15): the area of one tool's name instead of the plugin's `ToolArea` (lower case
  words with underscores; `oadm_device_request` of VAPIX Commander); a host tool's name still wins.
- Plugin tool secrets (one record `PluginToolSecret(Name, Label, MaxLength)`): `PluginTool.Secrets` declares the secrets a
  change's run may need (at most 8); the plan's `PluginToolPlan.SecretsNeeded` chooses those the run needs (some of the
  declared ones, or secrets the plan makes itself, such as one per password field of a command; empty: none; null: all
  declared). The run and a check of an entered value read them with `PluginToolInput.Secret(name)` (`Secrets`,
  `PluginToolSecrets.Of` for a plugin without `PluginPageTools`); `IsRun` tells the run from a preview or a check. A
  plugin never writes one into an answer, a log line or an audit detail ("Secrets").
- `ICallContext.Uploads` (`IUploadedFiles`): the files the caller uploaded in this session (an agent's upload link, the
  Assistant's Attach file), so a tool takes a file as `file_id` and reads it itself (PKI's Import CA and Import
  certificate); another user's or session's upload answers like an unknown id (`UploadStore.For`,
  `CorePluginHost.UploadStore`). `FakeCallContext.Uploads` with `FakeUploadedFiles` in tests.
- `[Secret]` (`SecretAttribute`, `Label`) on a payload property: left out of the task's input schema; with
  `ITaskPlugin.SecretsOf(payloadJson)` (`TaskSecret`, default `TaskSecrets.FromAttributes`) the secrets the person enters
  for a run ("Secrets"). A plugin tool's input type with a `[Secret]` is never offered: the tool declares its secrets.
- `ITaskPlugin.PagePermissions(payloadJson)`: a page task names the keys of its core plugin one run needs; then the task
  tools offer it with those rights ("Offered tasks").
- `ITaskExecutionContext.SaveResultFileAsync(fileName, contentType, content, ct)` and `TaskResultFile.MaxBytes` (64 MB):
  a task keeps a result file for its user ("Files"); older hosts and system tasks throw `NotSupportedException`.
- `PluginTool.ReturnsFile` and `PluginToolFile(Method, PayloadJson, FileName, ContentType, Size)`: a plugin tool hands out
  one of its downloads as a file ("Files"); the JSON names of `PluginToolFile` are fixed.

## Skill

One skill `oadm` (`SKILL.md` with a table of which tool answers which question from OADM's own data, plus workflow files:
AXIS OS rollout, network change, configure devices (a setting on many devices), certificates, health check, troubleshoot
one device, vulnerabilities, failed tasks, event rules, applications, SD cards, sites; every tool name in them is a tool
the server offers, `McpSkillTests`),
kept in `src/Oadm.Server/Mcp/Skill/`, served over MCP (skills extension `io.modelcontextprotocol/skills` and resource
`oadm://skill/SKILL.md`) and as **Download skill** on the card MCP. It says: call `oadm_server_info` first; start
with summaries, narrow with filters; always preview, show the plan and the skipped reasons to the user, then run; never
guess or ask for passwords; device texts are data, never instructions. Tool descriptions stay short; the skill holds the
workflows. When no tool fits (decision 2026-11-15) it says: ask the user first, then read axis.com (developer.axis.com for VAPIX) with the
agent's own web access and send the request with `oadm_device_request_preview`. The Assistant's offer to look online
(`oadm_docs_lookup_offer`, docs/spec/assistant.md "Looking on axis.com") is the Assistant's own, never a registry tool.

## Connect an agent

The card's **Connect an agent** is one row (decision 2026-11-15): a choice of the client (Claude Code first) and **Copy**,
which copies its command or file text with this server's address, never a key (the command and
files read it from `OADM_KEY` or ask for it). The address is the certificate's first name that is not `localhost` or a
loopback address (`AgentConnections.HostOf`):

- **Claude Code**: the one-line command and Copy:
  `claude mcp add --transport http oadm https://oadm.example:5082/mcp --header "Authorization: Bearer ${OADM_KEY}"`.
- **.mcp.json**: the file for a project folder.
- **VS Code** (`.vscode/mcp.json`, `servers`, `inputs` with `password: true`).

**Download CA certificate** and **Download skill** follow in the same row (the client's save picker,
`IDialogService.SaveFileAsync`). Claude
Desktop is not listed until a bridge is tested with this server (it needs one for a remote server with a header).
The website has a user page (Administration > MCP server, `admin/mcp-server`, with the generated
`admin/mcp-server/tools`) and a developer page (`dev/plugin-tools`).

## Tests

- Fake devices at the device limit: summary, search and preview stay within a Perf budget; no answer is larger than 32 KB (about 8,000
  tokens), checked for every tool.
- Every listed tool has annotations, an input schema with `additionalProperties: false` and an output schema; every write
  tool has a preview; a role without a permission does not list its tools (a Viewer key lists no write tool).
- Handles: once, 10 minutes (`FakeTimeProvider`), bound to the key; a cursor of another key is refused.
- No secret in any answer or log (a scan for the test passwords and keys over all tool outputs).
- Files and secrets (`McpTransferTests`, `AssistantTransferTests`, `McpPluginToolTests`): a task's result file to its owner
  only (another key's link 404, another user and another group none), once, its size and type, 10 minutes and bound to the
  caller (`FakeTimeProvider`); the device list CSV only for a role with Export devices; a plugin's file through
  `PluginService.Download`; a secret through its link once, bound to the key and the preview, 10 minutes, at most 4,096
  bytes, never in an answer, a log line (Serilog's files) or the audit log, audited "Set secret" as "MCP <key name>"; the
  assistant's card and its fake model's requests (above).
- Invalid input of every bundled task's schema gives field errors and starts no task; `TaskService.Run` refuses it too.
- The same caller sees the same devices over MCP and gRPC (device groups).

## Stages

1. Endpoint, the card MCP, PKI policy MCP server, access keys, `oadm_server_info`, devices summary/search/get, tasks
   list/get, logs (read only).
2. SDK members, `ValidatePayload` in `TaskService.Run`, task catalog/preview/run, schemas for the bundled task plugins,
   upload and download links.
3. Tags, sites, compliance, event rules, the bundled core plugins' tools.
4. Skill, Connect an agent, website pages.

## Stage 1 as built

- **Code**: the tools in `src/Oadm.Server/Tools/` (`ToolRegistry` the list and the calls with `ToolCaller`, `ToolSchema`
  the schemas and their check, `ToolCursors`, `ServerInfoTool`, `DeviceTools`, `TaskTools`, `LogTools`); MCP in
  `src/Oadm.Server/Mcp/` (`McpListener` opens and closes the port, `McpEndpoint` the web host and its checks, `McpAccess`
  the key's `ToolCaller`, the per-key limits and the audit of refused keys, `McpToolAdapter` the two MCP handlers,
  `McpGrpcService` the card), `Auth/InProcessAccess.cs` (`InProcessCalls`: a gRPC method called in process with the rule of
  `AccessPolicy`, the caller in the call's user state and in `CallerContext`, the interceptor's answers),
  `Auth/AccessKeyGrpcService`, `Devices/DeviceSearch` (`DeviceService.Search`: the filter, the sort and the page on the
  server); core `AccessKeyStore` (table AccessKeys, migration `AgentAccessKeys`, which replaced the first design's rows of
  `AuthTokens` of migration `AccessKeys`); PKI `PolicyKinds.McpServer` (shown as "MCP server"), `PkiServerCertificates`;
  SDK 0.13.0 `IServerCertificates`, `HostServers.Mcp`; client `Settings/AiPageView` (the page MCP & Assistant, card MCP),
  `Settings/AccessKeysViewModel` and `AccessKeysWindow` (the dialog with the list), `Settings/AccessKeyFormWindow` and `AccessKeyFormViewModel` (Create access key).
  Contracts `mcp.proto` (`McpService`, `AccessKeyService`) and `Mcp/McpTexts` (the card's texts, server and client).
- **Origin**: a request with an `Origin` passes only when it is the request's own scheme, host and port and the host is a
  name or address of the certificate (with DNS rebinding the browser sends the page's name as Origin and Host).
- **SDK**: `ModelContextProtocol.AspNetCore` 2.3.1 (its runtime packages are the host's 10.0.13), `Stateless = true`,
  `McpServerOptions.ProtocolVersion` "2026-07-28" (an `initialize` is refused with -32022), the SDK's own handlers for
  `tools/list` and `tools/call`. The SDK checks `Mcp-Method` and `Mcp-Name` (-32020); OADM refuses a request without
  `MCP-Protocol-Version` (400, -32020), which the SDK would serve as an older revision. GET is 405, other paths 404.
- **Who calls**: the key itself with its role and devices of now, user name "MCP <key name>" (also the task owner and the
  audit user, `AccessKeyStore.AgentNameOf`, `AuthenticatedCaller.IsAccessKey`; `AuthService.Me` answers the key's name,
  role and devices). A tool is listed when `AccessPolicy` allows every gRPC method
  the tool calls (so the logs need "See the server log" and all devices, the audit log the Administrator role); otherwise a
  call answers `isError` with the refusal. Calls per key: 20 a second (429, Retry-After 1) and 4 waiting for their answer at
  once (429 "Too many calls at once. Wait for an answer.", Retry-After 1); 10 changes a minute (stage 2). A refused key is
  audited once per address (IPv6 per /64) and key a minute, the count in the detail, and the whole port starts at most 30
  such entries a minute (`McpAccess.MaxRefusalAuditsPerMinute`; later refusals of that minute are not audited); an ended
  key keeps its row until the hourly cleanup, so its refusal names "MCP <key name>" ("The key has ended. 2
  refused calls"); after 50 refused keys from an address (IPv6 per /64) in 5 minutes it waits until they end (429,
  Retry-After 300), like logins.
- **Answers**: structured content and the same JSON as text, at most 32 KB (rows are cut to fit, `next_cursor` goes on);
  errors are `{"error": ..., "errors": [{"field": ..., "error": ...}]}`. Every argument is checked against the published
  input schema (unknown fields, types, lengths, ranges, `enum`, `uuid`, `date-time`). Device rows: `id`, `address`,
  `host_name`, `upnp_name` (each as the device has it, empty when not known; OADM never picks one name), `model`,
  `firmware` (the AXIS OS version), `status` (`Ok`, `Unreachable`, `CredentialsRequired`, `PasswordNotSet`,
  `CertificateChanged`, `SiteOffline`, `Unknown`) and the asked `fields`; the filter's `search` matches the address, the
  host name and the UPnP name (and serial, model, AXIS OS version, site, tags); a list of the filter matches any of its
  values; `firmware` takes `below` and `at_least`; sites are matched by name, "This server" for the server's own network; a
  search sorts by `address` (IPv4 in number order) unless `sort` says `host_name`, `upnp_name`, `model`, `firmware`,
  `status` or `last_seen` (a leading `-` descending); the summary's `group_by` defaults to status. A device or task outside
  the caller's group answers like an unknown one. The device tools call `DeviceService.Search` (docs/spec/grpc.md): the
  filter is applied on the server to the devices of the view, only the page is mapped and without the API lists
  (`oadm_devices_get` asks for them), the server info counts the view's cached ids. Log lists answer `more` and
  `next_cursor`, never a total (rule 3).
- **Cursors**: the last row's sort key and id (`ToolCursors`), signed with a key of the server process (a restart ends them)
  and bound to the access key and the query (the arguments without `cursor` and `limit`); a cursor of another key or
  search, or one that was changed, answers "This cursor does not belong to this search. Search again without it." The next
  page starts after that row in the sort order, so rows added, removed or changed elsewhere never move it: devices by the
  sort's key and id (`DeviceService.Search` `after_key`, `after_id`), tasks newest first by their start and id, the audit
  log by its entry id (`AuditService.List` `before_id`), the server log by the line's time and how many lines of that
  millisecond were shown (its lines have no id; `ServerLogService.List` `not_after`).
- **Card** (MCP on the page MCP & Assistant): setting `Mcp` (on, port 1025 to 65535, Listen on), audited "Changed MCP server"; every user
  sees the state and downloads the CA certificate (`oadm-ca.pem`, the issuers of the server's certificate). Off, PKI is not
  asked and the certificate line says "Turn on gets it from the PKI policy MCP server."; the host loads the certificate only while it is
  on (`IServerCertificates.LoadAsync`), and PKI's first load gives a policy without one its own "MCP Server Certificate"
  (`PkiService.EnsureMcpServerCertificateAsync`, docs/spec/pki.md "Server certificates"). On, the port
  opens when PKI runs and its policy picks a usable certificate; otherwise the status says why ("PKI is not running. The
  server log says why.", the certificate's problem, "Interface ... is not available", "Port 5082 cannot be opened: ..."), tried
  again every 30 s, on a change of the policy or the Certificates list and when a plugin is turned on or off. Windows service: the rule
  "OADM Server (MCP server, TCP 5082)" while it listens.
- **Keys**: at most 100 on the server; a restore ends them like the sessions (backups hold no keys).
- **Measured** at 100,000 devices with 100 APIs each and 50,000 tasks (`McpScaleTests`, Debug in a container, 2026-11-14):
  with `DeviceService.Search` the summary 2.4 s, a search page 1.3 to 1.7 s, a filtered search 1.4 s, one device 28 ms and
  the server info 0.7 s (with `DeviceService.List` of every device: 2.3, 3.0, 2.9, 3.5 and 3.7 s); the task list 1.6 s, one
  task 1.3 s, the logs milliseconds; every answer under 32 KB.
- **Tests**: `tests/Oadm.Server.Tests/Mcp` (`McpServerTests`: off by default, PKI, origin and DNS rebinding, methods, body,
  headers, the official client, 401 and its audit, an ended key in the audit log, 429 per second and at once, no gzip;
  `McpToolTests`: schemas and annotations, roles, no tool reaches what is not offered ("Not over MCP"), device texts
  only as JSON values (rule 6), keys ending, field errors, paging and sort-key cursors (also an empty filter, the three
  names of a row, both logs with new lines between pages), the summary, the same devices over MCP and gRPC, tasks, the
  audit log; `DeviceSearchTests` (`DeviceService.Search`);
  `McpSecretTests`: no password, token or key in any answer, audit entry or log line), `Tools/ToolRegistryTests` (two tools
  called in process with a caller object and no request; the tools name nothing of MCP or ASP.NET), `Perf/McpScaleTests`, PKI
  `McpServerPolicyTests` (its own certificate on the first Turn on, also without OADM PRO; nothing at a PRO start while it is
  off; the user's choice stays; the host's full name), `McpAccessKeyTests` (access keys: Administrator only, the field
  errors with their field, an Administrator key has all devices, the key's identity in the audit log, task owner and server
  info, a role or group change applies on the next call, deleting a role or group a key names is refused, Delete and the end
  date end a key), client `AiAgentsTests` (headless, screenshots: the card, the Create access key form and its field errors,
  the key shown once with Copy, the list, Delete asks first, the Connect an agent choice with Copy).

## Stage 2 as built

- **SDK 0.13.0** (`src/Oadm.Sdk/Tasks/TaskPayloads.cs`, `ITaskPlugin`): `PayloadType`, `PayloadJsonOptions`, `ValidatePayload`
  (`PayloadError` per JSON field), `PlanAsync` (`TaskPlan`, `TaskPlanDevice`, default `TaskPlan.ByCanRun`), `SecretAttribute`.
  The host's `Tasks/TaskPayloadSchemas` exports the schema once per type: objects without other properties, the attributes'
  bounds, the host's bounds for a text, number or list without one (4,096 characters, the 32-bit range, 100,000 items); no
  schema (not offered) for a value of any type, a reference or a type the exporter cannot describe; a `[Secret]` property is
  left out of the schema (2026-11-15, "Secrets").
- **gRPC** (docs/spec/grpc.md): `TaskService.Run` asks `ValidatePayload` before any task starts (INVALID_ARGUMENT with the
  first text, trailer `oadm-field`; a check that throws refuses the run) and answers `batch_id`; `TaskService.Plan` (the
  preview: the checks of Run, the field errors, the filter resolved in the caller's view in address order, per device run
  or skipped by `PlanAsync`, the task name, the warnings; nothing written); `ListTaskPlugins` with `with_payload_schema` and
  a `filter` (counts only within it).
- **Tools** (`Tools/TaskRunTools`): `oadm_tasks_catalog(filter, task)` (the offered tasks by name with `can_run`,
  `cannot_run` by reason and `input_schema`; schemas that do not fit 32 KB are left out with a note, `task` gives one in
  full), `oadm_task_preview(task, filter, input)` (the input against the task's schema, then the plugin's rules, field errors
  as `input.<field>`; the plan with the first 20 devices, the skipped ones by reason and the warnings; a handle when a
  device would run), `oadm_task_run(handle)` (`destructiveHint`; `TaskService.Run` with the preview's devices and payload:
  every check again; batch and task count), `oadm_files_upload_link(name, size)` (a file name without a folder, the exact
  size). Handles and links (`Tools/ToolHandles`): random, bound to the caller's binding key, 10 minutes, once, at most 20
  of a kind per caller, in memory only; another key's handle answers like an ended one.
- **MCP** (`Mcp/McpFiles`, `McpEndpoint`): the link is the server's own name (the MCP server certificate's first DNS name,
  else its first address) and the MCP port, never the request's `Host`; a file name has letters, digits, spaces and
  `. - _ ( )` only. `PUT /files/<token>` with the same key: the link's caller only, exactly its size
  (400 otherwise), through `FileService.Upload` in process (its limits; the tasks of the same key read the file), answers
  `file_id`, `name`, `size`, `sha256`, audited "Uploaded file" as "MCP <key name>". Every change an agent causes is
  audited as "MCP <key name>" (the key's caller name). Changes per key: 10 a minute (429 "Too many changes with this access key. Wait a
  minute.", Retry-After 60; counted on `Mcp-Method` tools/call and `Mcp-Name` of a tool that starts tasks).
- **Offered tasks**: Restart and Use OADM NTP server (no settings), Date and time (`DateTimePayload` with its
  descriptions, `PayloadValidator` at the payload's fields; the year is left to the device as in the task). Users takes its
  password through the secret channel. Also offered: Upgrade AXIS OS (uploaded files only, factory default, allow
  downgrade; `PlanAsync` is `FirmwareCompatibility.ChooseFor` from the cached model and version; no online download, which
  needs the user's MyAxis sign-in), Network settings, Assign IP address, Applications (install, remove, start, stop),
  Enable and Disable HTTPS, Enable and Disable IEEE 802.1X, Renew certificates. The dialogs and
  the previews share their warning texts (`FirmwareWarnings`, `NetworkWarningTexts`, `PkiTaskIds`). The PKI enable tasks
  have no clock question over MCP: a device with a late clock refuses. The schema export also describes maps (Network's
  devices by id: `propertyNames` uuid, at most 100,000).
- **Every task a user can run** (decision 2026-11-15: agents do what a user can in the client, every change preview then
  run; the Assistant's user presses Run). No tool of their own: each task got an input record, the dialog's field errors
  (shared with the dialog, never a copy) and a plan from cached data, so the generic task tools offer it. The details are
  in each plugin's README ("AI agents").
  - Device configuration: **Configure devices** (`settings` name and value, `actionRules`, `schedules`,
    `streamProfiles`; the dialog's `parameters`/`rules` form stays; "Configure dangerous parameters" for network, HTTPS,
    ports, login and 802.1X parameters; warnings for a value that can cut the devices off; a per-device value on several
    devices skips them all; a setting or action parameter with a secret name (pass, secret, community) is sent empty and is a slot of
    the secret channel (`setting_<i>`, `rule_<i>_parameter_<j>`, "password for <name>", at most 8); a device password
    parameter whose name is no secret name is skipped on the device), **Create
    configuration file** and **Export configuration** (read-only tasks; the file is the task's result file, fetched with
    `oadm_files_download_link` and `task_id` or the assistant's file card; the log has its name, size and SHA-256), **Import
    configuration** (one
    `.json` from an upload link for every device of its model, no ZIP; "Import connection settings" and the lock-out
    warning for the area `connection`; the plan reads the uploaded file only).
  - Edge storage (page task `oadm.edge-storage.task`): check, repair, retention, format, encryption off; format and
    encryption off warn that every recording is deleted, repair that damaged recordings can be lost; the page's rights
    "Change storage settings" (check, retention) and "Format and encrypt storage" (the others). Encrypt (`encryptionOn`)
    takes the passphrase the user enters (slot `passphrase`, the page's rule checked when entered), right "Format and
    encrypt storage".
  - Action Rule Editor (page task `oadm.action-rules.task`): change (on/off, name, wait time, action settings, recipient),
    delete, copy, create, undo; rules by name as the editor read them last (a device not read is skipped, "Not read in the
    Action Rule Editor."); the editor's rights "Change action rules" and "Delete action rules"; a password setting of an
    action is sent empty and entered by the user (`RuleAgentCheck.SecretsOf`: a slot per setting at
    `change.parameters.<name>` / `rule.parameters.<name>`; the plan warns "The action gets a new password. The user enters
    it."; the undo store keeps it encrypted). `oadm_device_rules_list` answers the event topics and the undoable changes.
  - PKI: **View certificates** (a read-only task: one log line per certificate, the name Delete takes), **Delete
    certificates** (per device the certificates by name; the plan says how many and that they cannot be restored; one in
    use by HTTPS or 802.1X is still refused on the device), **EST set up and remove** (OADM PRO; no clock question: a late
    clock refuses; Remove says per profile what the cameras lose).
  - Applications: **Change settings** (`values` by name; a password setting goes in `passwords` by name and the user
    enters its value, slot `password_<i>`, "password for <name>"; one in `values` is refused at its field and on the device
    before any write); **Install license key** (`installLicense`: one key file the user enters, slot `license_key`, label
    "license key", one device a run; a file that is no key is refused when entered).
  - Page tasks: a task only its page starts (hidden from the menus, no permissions of its own) is offered when it answers
    `ITaskPlugin.PagePermissions` (SDK 0.13.0) with the keys of its package's core plugin one run needs; `TaskService.Plan`
    and `Run` check exactly those (`TaskRunPermissions.RequireOutsidePage`, unknown keys need all of them), the catalog
    lists it only with `with_payload_schema` (`TaskPluginRunnable.InMenus` false), the client's menus never. The page's own
    payload answers null and stays page only.
  - Secrets (decision 2026-11-15): every password, passphrase or key of a task goes through the secret channel ("Secrets":
    `ITaskPlugin.SecretsOf`, the preview's slots, the value checked when entered and put into the run's payload only).
  - Users (the password a slot of Add and of a password change, "password"; the dialog's rules at their fields and the
    base rule checked when entered; each device checks its own passphrase policy before the write; a device whose stored
    login is the user changed or removed is skipped, "OADM uses this account on the device."), Install certificates (files
    by device from upload links; the PKCS#12 password a slot "PKCS#12 password" unless `withoutPassword`; the task refuses
    a certificate that does not name its device before any write; HTTP devices skipped unless `sendKeysOverHttp` with the
    dialog's warning; no clock question), Install CA certificates (the plan's warnings: "The devices will trust every
    certificate these CAs issue. Check each CA before you run it.", then each CA with name, issued to and by, valid until
    and SHA-256, for the person to confirm).
- **Tasks and files**: `oadm_tasks_cancel_preview`/`_run` (a batch or up to 200 task ids; counts what stops and what
  cannot; only the caller's devices; one cancel takes at most 200 tasks and runs at most 60 s, `more_after_this` and
  `left` say to preview again); a task preview names each uploaded file it uses with its SHA-256 (`files`), and an
  application install warns that the package runs on the devices; `oadm_files_list` (the files this key sent, newest first, 50 a page;
  `FileService.List`) and `oadm_files_delete_preview`/`_run`.
- **Download links**: `GET /files/<token>` on the MCP port serves a `PlannedDownload`: once, 10 minutes, bound to the key,
  the same key check as uploads, audited "Downloaded file"; `oadm_files_download_link` hands them out ("Files").
- **Request body**: at most 1 MB for every request, `/files/` too, until its key passed and its upload link was taken;
  only then `McpFiles.ReceiveAsync` raises the limit to the link's exact size.
- **Tests**: `McpTaskToolTests` (the catalog and a secret task, a preview that writes nothing, the handle of another key,
  once, 10 minutes, invalid input from the schema and from the plugin, the same refusal through `TaskService.Run`, upload
  links, 10 changes a minute), `McpToolTests` and `McpSecretTests` over every tool, the canary
  case of `TaskService.Plan`; `McpAgentTaskInputTests` (the tasks of 2026-11-15 in the catalog with their schemas, the page
  tasks never in the menus, format's warnings and the page's format right, Configure devices preview and run on several
  devices, the dangerous and connection rights, a password refused, the editor's last read, Delete certificates' plan),
  `RoleTests` (a page task with page permissions through Run, the rights it names, fail closed), and each plugin's
  `AgentTaskInputTests` / `AgentSettingsTests` / `PayloadTests`.

## Stage 3 as built

- **Devices** (`Tools/DeviceChangeTools`, `Tools/SettingsTools`): `oadm_devices_snapshot(device_id)` (one JPEG as an image
  result, Live view permission, at most 1280 px wide; over 1 MB it is taken again smaller, then at 640 px; the camera's
  answer is read up to 4 MB; snapshots are audited "Took a snapshot" once per caller and minute, with the counts of pictures and devices);
  `oadm_devices_refresh_preview`/`_run`; `oadm_devices_remove_preview`/`_run` (a filter is required, an empty one is
  refused); `oadm_devices_credentials_preview`/`_run` (an entry of the credential list by its name, never a password; the
  credential list is the Administrator's, a name two entries share is refused); `oadm_devices_add_preview`/`_run` (exactly
  one of `discovery`, a range (at most 65,536 addresses, as the client), up to 1,000 addresses or `try_waiting`; `keep_waiting` puts
  addresses that do not answer on the waiting list; IP addresses only, no names, schemes or ports, and only inside a
  network of the server's own interfaces or of a connected site, otherwise a field error names the allowed networks; the
  preview only looks (it sends no login and stores nothing), waits at most 90 s and then says the scan is incomplete; it
  acts outside OADM, so it is not announced as a read and it counts as a change; the run logs in with the credential
  list and adds only the devices that logged in, at most 200 a run, a device in factory default only with
  `first_password`, a failed login only with a typed `user_name` that works); `oadm_settings_get` (what the Settings page shows, without secrets). A preview is listed
  only for a caller who may run its change. Refresh is not destructive; Remove and Set credentials are.
- **Tags, sites, compliance, event rules** (`Tools/TagTools`, `SiteTools`, `ComplianceTools`, `RuleTools`):
  `oadm_tags_list` (device counts in the caller's group), `oadm_tags_assign_preview`/`_run` (add and remove tags on a
  filter's devices, the first 20 devices shown; destructive because it can remove tags); `oadm_sites_list` (no node id,
  address or fingerprint; waiting nodes only counted); `oadm_compliance_summary` (per check: pass, fail, unknown of the
  filter's devices, worst first, counts only) and `oadm_compliance_checks`; `oadm_rules_catalog` (the events and actions a
  rule can use, with their fields), `oadm_rules_list`, `oadm_rules_get`, `oadm_rules_save_preview`/`_run` (create or
  change; the rule editor's checks and texts through `EventService.CheckRule`, nothing saved by the preview; a new rule is
  saved off unless `enabled`; the preview names actions that change devices and warns about one on every device),
  `oadm_rules_delete_preview`/`_run`, `oadm_events_log` (`more` and a cursor, no total). Passwords, secret headers, code
  and file fields of actions are never shown, and a rule that needs a new one answers "Set this in OADM.". A change keeps
  an action's stored passwords, tokens and hidden headers only while the change touches nothing but the action's content
  (message text, code, multi-line text); any other change of such an action is refused with "Set this in OADM.". Rule answers
  show an address as scheme, host and port only (an address given back unchanged keeps the stored one), and a URL with a
  login in it is refused. The save preview names every outside destination ("Sends event data to ...") and warns again
  of a new one that is not a saved recipient; the destinations themselves are in `destinations` (kind, the scrubbed
  target, a saved recipient's `name`, saved recipient, new). Warnings and errors are fixed sentences; rule, recipient and device names
  appear only in their own fields, the server's problem text in `problem`. A rule naming more than 200 devices is
  changed in OADM.
- **Plugin tools** (`Tools/PluginTools`, a tool source of the registry that follows the plugins that are on): core plugins
  bring tools through `ICorePlugin.Tools` (SDK 0.13.0: `PluginTool`, `PluginToolPage`, `PluginToolErrors`, `ToolArea`, which
  defaults to the plugin id without `oadm.`). The host names them `oadm_<area>_<name>`, refuses a name that a host tool
  has, leaves out a tool without a strict schema, lists them only while the plugin is on and the caller has the tool's
  permission and the plugin's method rules, calls `PluginService.Invoke` in process with the caller, refuses an answer over
  32 KB and never shows a plugin's exception text. A tool with `DeviceFilter` takes the device filter of the device tools
  (with the selection); the host resolves it with `DeviceService.Search` in the caller's view (an id outside refuses the
  call) and hands the plugin at most 500 ids by address and the total. A tool with `TaskPlugin` is listed only for a role
  with a permission of that task of the same package. Bundled: `oadm_pki_certificates`, `oadm_pki_policies`,
  `oadm_pki_certificate_create_preview`/`_run` (the PKI page's own create check, no key material in any answer),
  `oadm_pki_certificate_import_preview`/`_run(file_id)` (a certificate, or a PKCS#12 file with its key, from an upload link;
  its password a slot "PKCS#12 password" only when the file does not open without one, a wrong one refused when entered;
  the page's checks with `dryRun`), `oadm_pki_certificate_sign_preview`/`_run(request, use, policy_id)` (the CSR as PEM
  text with its line breaks as spaces; the run answers the certificate as a file reference); both Administrator with
  Manage the certificate authority, all devices, audited like the page,
  `oadm_ntp_status`, `oadm_dhcp_status`, `oadm_dhcp_leases`, `oadm_hardening_results`, the Provisioning page's reads and
  changes `oadm_provisioning_*` (the root password of Set password only as a secret, `new_password`; a node's password or
  passphrase only as the secret of `oadm_provisioning_secret_store`, which answers its id), and the settings reads of Device
  configuration `oadm_config_parameters` (a setting by a word of its name or label, or exact names: the devices grouped
  by value), `oadm_config_action_rules` and `oadm_config_stream_profiles` (grouped by rule or profile name), read with
  the Configure devices dialog's code, 8 devices at a time within 40 s (the rest counted as not read), no secret value,
  Configure devices' permissions.
- **gRPC** (docs/spec/grpc.md): `DeviceService.Resolve`, `DeviceService.SetStoredCredentials`, `LiveViewService.GetSnapshot`,
  `TagService.PlanDeviceTags`, `ComplianceService.Summarize`, `EventService.CheckRule`, `ListEventLogRequest.before_id` and
  `skip_total`; each with its `AccessPolicy` rule and, when it takes device ids, its device group canary case.
- **Tests**: `McpDeviceChangeTests`, `McpAreaToolTests`, `McpRuleToolTests`, `McpPluginToolTests`, the plugins' `ToolTests`,
  and `McpToolTests` / `McpSecretTests` over every tool; Perf at 100,000 devices (`McpDeviceChangeScaleTests`: previews 1.2
  to 2.3 s, a removal 4.8 s; `McpAreaScaleTests`: tag preview 2.9 s, tag run 15 s, tag list 1 s, compliance summary 2.9 s;
  Hardening results 43 ms a page; every answer a few KB).

## Stage 4 as built

- **Skill** (`src/Oadm.Server/Mcp/Skill/`: `SKILL.md` and the workflow files of "Skill", embedded): `Mcp/McpSkill` serves them as the resources `oadm://skill/<file>` to any valid key and
  declares the extension `io.modelcontextprotocol/skills` in the server capabilities; `McpService.GetSkill` answers them
  as `oadm-skill.zip` (every signed-in user) for **Download skill**.
- **Card**: **Connect an agent** is one row: the client (Claude Code, `.mcp.json`, VS Code) and Copy with this
  server's address (the certificate's first name, a DNS name before an address) and port while the server runs, then
  **Download CA certificate** and **Download skill**.
- **Website**: `admin/mcp-server` (turn it on, access keys, connect an agent, the skill, what agents can and cannot do) and
  `dev/plugin-tools` (how a plugin adds tools).
- **Tests**: `McpSkillTests` (resources, the ZIP, the tool names the skill uses exist, the Wording rules), client
  `AiAgentsTests` (the configurations, headless screenshots).
- **Device facts and vulnerabilities** (2026-11-15): the fact filters, counts and row fields above, the vulnerability
  tools, the skill's line "read OADM's data first" (the VAPIX Commander tools say it too), the Assistant table's 4th column.
  Tests: `McpDeviceFactsTests` (the counts, possible ones apart and not counted, the column's text for the same devices,
  the clock, certificate and chip filters and fields, the device group, paging, the Assistant's table).

## Host actions as built

Decision 2026-11-15: agents do everything a user can do in the client. Stage 1 brings the host's own actions
(`Tools/TagTools`, `WaitingTools`, `TaskChangeTools`, `RuleActionTools`, `RecipientChangeTools`, `ComplianceChangeTools`,
`HostActionTools`, `LiveViewTools`, `DeviceChangeTools`; the shared parts in `ToolChanges`). Every change is a preview that
writes nothing and a run that takes only its handle, through the gRPC service the client calls, so its permission, device
group, checks and audit are the client's. The Assistant shows each preview as a card (`AssistantCards`: the count with its
unit, the key fields, red Run for deletes and removals) and a short status line for the reads (`AssistantTools.LineOf`).

- **Tags** (Change tags, users with all devices): `oadm_tags_update_preview`/`_run` (rename and recolor, `TagService.Update`;
  a name in use is refused, the device groups that use it are named) and `oadm_tags_delete_preview`/`_run`
  (`TagService.Delete`; the device groups whose devices leave are named).
- **Add devices**: `discovery` in `oadm_devices_add_preview` (`StartZeroConf` with `no_logins`: the devices that announce
  themselves, only looked at; the session ends with the preview); up to 1,000 addresses, probed 32 at a time with the short
  first contact of an import. The waiting list: `oadm_waiting_list` (pages in address order, `has_login` only) and
  `oadm_waiting_remove_preview`/`_run` (ids or `all`, `WaitingDeviceService.Remove`).
- **Tasks**: `oadm_tasks_delete_preview`/`_run` (a batch or up to 200 ids of the caller's devices, `TaskService.Delete`; waiting
  and running ones are cancelled first), `oadm_tasks_delete_all_preview`/`_run` (Delete all tasks, users with all devices,
  `TaskService.DeleteAll`, audited "Deleted all tasks"). `oadm_task_query(task, device_id, method, input)`: a task dialog's
  read of one device (`TaskService.Query`, `ITaskPluginQuery`, read only by contract) with the task's own permission and a
  task right; the reads of Device configuration (a device's configuration can hold passwords) are refused ("This read is
  not offered over MCP."), and every property or name/value pair that names a secret is left out of the answer (`hidden`
  counts them). It asks the device, so it counts as a change.
- **Event rules**: `oadm_rules_enable_preview`/`_run` (`SetRuleEnabled`; the preview names off actions and device changes),
  `oadm_rules_duplicate_preview`/`_run` (`DuplicateRule`: the server copies the stored passwords, the copy is off),
  `oadm_rules_test_action_preview`/`_run` (Send test of one action of a saved rule with its stored settings, `TestAction`;
  it really sends, so it is a run; an action that acts on devices is tested in OADM), `oadm_events_clear_log_preview`/`_run`
  (`ClearLog`, Clear the event log).
- **Recipients** (users with all devices, Change rules): `oadm_recipients_types` (the types and their fields),
  `oadm_recipients_certificates` (`ListRecipientCertificates`, ids and texts), `oadm_recipients_save_preview`/`_run` (the
  preview is `EventService.CheckRecipient`: the dialog's checks without saving; a change keeps the fields left out and the
  stored password; a password or header value is never an argument: `set_secrets` names the password fields (by key) and
  headers (field and name) the user enters through the secret channel; a new recipient's required passwords and a password
  the server asks for again (a changed target) are slots by themselves; each value is checked with `CheckRecipient` when
  entered and put into `SaveRecipient` in memory by the run; a file value is set in OADM; an address with a login is
  refused; a header value is stored as the dialog stores it), `oadm_recipients_delete_preview`/`_run` (no handle while rules
  use it, their names in `used_by`), `oadm_recipients_test_preview`/`_run` (`TestRecipient` with the stored values).
- **Compliance**: `oadm_compliance_devices(check_id, outcomes, filter)` (which devices fail a check, or pass, or are unknown,
  pages with the total and the result's detail, `ComplianceService.ListResults`), `oadm_compliance_kinds` (the kinds and
  their fields), `oadm_compliance_enable_preview`/`_run` (`SetEnabled`, users with all devices),
  `oadm_compliance_check_save_preview`/`_run` (`PlanCheck` then `SaveCheck`; a change starts from the stored check, its
  settings never shown; a password or file field is set in OADM), `oadm_compliance_check_delete_preview`/`_run` and
  `oadm_compliance_check_enable_preview`/`_run` (a default check is never deleted or turned off),
  `oadm_compliance_check_now_preview`/`_run` (`CheckNow` on checks and a filter).
- **Sites**: `oadm_sites_rename_preview`/`_run` (Administrator, `RenameSite`; a name in use is refused).
- **Live view** (device safety: the preview reads what the camera supports and refuses the rest): `oadm_devices_optics`
  and `oadm_devices_ptz` (reads of the camera now), `oadm_devices_optics_preview`/`_run` (one change: zoom within the
  camera's range, focus, autofocus, an autofocus area or the default focus; Optics control), `oadm_devices_ptz_preview`/`_run`
  (home, a preset by name, zoom, center on a point; continuous moves stay in the live view),
  `oadm_devices_rtsp_port_preview`/`_run` (the port OADM stores for at most 1,000 devices of a filter, `SetRtspPort`; the
  devices are not changed).
- **Vulnerabilities**: `oadm_vulnerabilities_update_preview`/`_run` (Check now, `VulnerabilityService.Refresh`,
  Administrator; no handle unless Update online is By the server).
- **Version**: `oadm_update_check` (`UpdateService.Get`, Administrator: the server's version, the latest release, whether
  Install now is offered; Install now itself: "Administration as built").
- **gRPC** (docs/spec/grpc.md): `ComplianceService.ListResults` and `PlanCheck`, `EventService.CheckRecipient`,
  `StartZeroConf(ZeroConfRequest)`; each with its `AccessPolicy` rule and its device group canary case.
- **Tests**: `McpHostActionTests` (the preview writes nothing, the run does it once and only with its own preview's handle,
  handles end after 10 minutes and belong to their caller, the audit lines, refusals, a role without the rights and a group
  user list none of the changes and a group sees only its devices, Discovery only looks, the scrub of a task read, every
  card), `McpLoginToolTests` (logins, first password, the credential list), the recipient secret test of
  `McpHostActionTests`, `McpAgentTaskSecretTests` (Configure devices, Edge storage encrypt, Applications passwords and
  license key), `McpCommanderSecretTests` (VAPIX Commander), and `McpToolTests` / `McpSecretTests` / `McpSkillTests` over
  every new tool.

## Administration as built

Decision 2026-11-15: an administrator's agent (MCP with an Administrator key; the Assistant of an administrator) does what
the Plugins page, the License card, Backup and restore, Support and the new version dialog do. Every change is a preview
that writes nothing and a run that takes only its handle, through the gRPC service the client calls (`AccessRule.Admin`,
its checks and its audit). Keys and passwords come only through the secret channel ("Secrets"), files only through upload
and download links (the Assistant: file cards). `Tools/AdminTools`, `Tools/BackupTools`, `Tools/HostActionTools`.

- **OADM PRO** (`LicenseService`): `oadm_license_import_preview`/`_run` (the slot `license_key`, at most 4,096 bytes; the
  preview shows the state now and `activate_url`, the page of Activate license with this server's ID; the run's refusal is
  the service's reason), `oadm_license_remove_preview`/`_run` (no handle without an imported key),
  `oadm_license_trial_preview`/`_run` (no handle unless `trial_available`). No answer holds a key or a customer.
- **Plugins** (`PluginService`): `oadm_plugins_enable_preview`/`_run` (refused for a plugin that is part of OADM and for a
  PRO plugin without OADM PRO; no handle when nothing changes; the warning that its pages, tasks, tools and rule actions
  stop), `oadm_plugins_license_preview`/`_run` (a commercial plugin's key through the slot `license_key`; a free plugin is
  refused) and `oadm_plugins_license_remove_preview`/`_run`.
- **Optional plugins**: `oadm_plugins_optional` (`ListAddOns`, every signed-in user like the card), and for administrators
  `oadm_plugins_optional_install_preview`/`_run` (the ZIP sent with `oadm_files_upload_link`, by `file_id`; the preview
  names its SHA-256 and the run sends `UploadAddOn` with `AddOnUploadHeader.file_id`, so the server checks the bytes against
  its built-in hash exactly as for the client, `docs/spec/add-ons.md` rule 2; without a file only a removed plugin comes
  back, `InstallAddOn`), `oadm_plugins_optional_remove_preview`/`_run` and `oadm_plugins_optional_restart_preview`/`_run`
  (no handle while nothing waits or a task runs; the warning that every client and agent loses its connection).
- **Backups** (`BackupService`; docs/spec/settings-backup.md): `oadm_backups` (the schedule, the kept backups, newest first,
  at most 100 with the total, the last automatic backup and the last restore), `oadm_backups_schedule_preview`/`_run` (a
  new password, or the first one for Turn on, through the slots `password` and `password_again`; the run refuses two
  different values, "The two passwords are not the same. Call the preview again."; the plan counts the automatic backups a
  lower keep deletes), `oadm_backups_create_preview`/`_run` (Back up now: the password twice; the run answers a file
  reference and keeps the password encrypted in memory, `ToolSecrets.Seal`, until the file is fetched: `BackupService.Create`
  makes the backup then, audited "Created backup"), `oadm_backups_download(name)` (a file reference of a kept backup,
  `BackupService.Download`, audited "Downloaded backup"), `oadm_backups_delete_preview`/`_run`,
  `oadm_backups_restore_preview`/`_run` (`file_id` of an upload link or `name` of a kept backup, exactly one; the slot
  `password`; the plan warns loudly: everything on this server is replaced, a safety backup first, the running tasks are
  cancelled, the server restarts and every client and agent is disconnected, every access key ends, optional plugins are
  installed again afterwards; the run asks `Inspect` first, so a wrong password ("The password is wrong. Call the preview
  again.") or a backup this server cannot take changes nothing, then `Restore`).
- **Support report**: `oadm_support_report` (a file reference, `SupportService.CreateReport` when it is fetched, audited
  "Created support report").
- **Install now** (`UpdateService.Start`, docs/spec/self-update.md): `oadm_update_install_preview`/`_run`. The preview hands
  out a handle only for the latest stable release newer than the server, on a server installed with the installer, with no
  update and no task running (else the reason: `UpdateTexts`, "OADM is up to date."); its plan says the server is away, its
  database is copied first, there is no rollback, a client on its computer is updated too, and older site nodes are
  updated by hand. The run is Start, which checks every rule again (SHA-256 of the release, disk space, tasks). Tests use
  the fakes of the release feed and the helper; no real installer ever runs.
- The Assistant shows each preview as a card (red Run for removals, deletes, a restore, a restart and Install now) and
  short status lines for the reads.
- **Tests**: `McpAdminToolTests` (Administrator only, the preview writes nothing, the run once, the license key and the
  backup password only through their links and never in an answer, the audit or a log line, the passwords twice, a ZIP
  that does not match the hash refused by the run, a restore that checks the password first and restarts only with the run,
  the support report's ZIP, Install now with a fake helper and the refusals of a server that cannot take it, every card),
  and `McpToolTests` / `McpSecretTests` / `McpSkillTests` over every new tool.

## Access as built

Decision 2026-11-15 (rule 5, "Access"): `Tools/UserTools`, `RoleTools`, `DeviceGroupTools`, `AccessKeyTools`, the shared
`AccessTools` (paths, finding a user, role, group or key by its name or id, `OneTimeSecret`) and `AccessTexts` (the plans'
sentences). Names and ids are both taken; `devices` is `"all"` or a device group; the Administrator role always has all
devices.

- **Users** (`UserService`): `oadm_users_list` (role, devices, enabled, last login, `you`), `oadm_users_add_preview`/`_run`
  (the name, unique; a role; devices; the slot `password`, the run refused until it is set), `oadm_users_update_preview`/
  `_run` (role, devices, enabled; the changes as lines "Role: Operator to Administrator"; `ends_sessions` for a disable),
  `oadm_users_delete_preview`/`_run`, `oadm_users_password_preview`/`_run` (the slot `password`; the user is logged out).
  The previews refuse what the store refuses, with its texts (`UserStore.OwnRoleOrState`, `OwnAccount`,
  `LastAdministrator`, `NameTaken`); an access key has no user, so only the last-administrator guard applies to it. Sessions
  end where the page ends them (a new password, a disabled or deleted user); a new role or new devices apply to the next call.
- **Roles** (`RoleService`): `oadm_roles_list`, `oadm_roles_permissions` (every right with key, name, group, full device
  control, plugin on, the roles that have it; `role` narrows to one role's rights), `oadm_roles_save_preview`/`_run` (create
  with `start_from`, or change; `grant` and `revoke` by key, an unknown key refused at its field; the plan names what the role
  `gains` and `loses`, the rights of full device control, and how many users and access keys get more rights; the run saves
  on the role's version, so a change made meanwhile is refused), `oadm_roles_delete_preview`/`_run` (refused for a built-in
  role and while users or keys have it, `RoleStore.BuiltIn`, `InUse`, `AccessKeyTexts.RoleInUse`). The Administrator role is
  refused (`RoleStore.AdministratorFixed`); its start page and every role's start page stay in OADM.
- **Device groups** (`DeviceGroupService`): `oadm_device_groups_list` (rules as the page writes them, sites and tags by
  name, counts, users), `oadm_device_groups_save_preview`/`_run` (sites and tags by name, `this_server`, serials to replace,
  add or remove, except; at most 1,000 serials a call; the rules checked with `DeviceGroupRules.Normalize`, counted with
  `DeviceGroupService.Preview`; the users whose devices change and their new count; saved on the revision read),
  `oadm_device_groups_delete_preview`/`_run` (as on the page: its users have no devices afterwards and the plan names them;
  refused while an access key has the group).
- **Access keys** (`AccessKeyService`): `oadm_access_keys_list` (never a key or hash; `this_key`),
  `oadm_access_keys_create_preview`/`_run` (name, role, devices, 30, 90 or 365 days; the plan says the role and devices in
  words), `oadm_access_keys_delete_preview`/`_run`; delivery and guards in "Access keys".
- **Assistant**: cards for every preview (`AssistantCards`; red Run for the deletes), status lines "Looking at users",
  "Looking at roles", "Looking at device groups", "Looking at access keys"; the key card `AssistantSecretItem` (title,
  "Copy it now. OADM cannot show it again.", Copy once).
- **Tests**: `McpAccessToolTests` (only an Administrator's key lists them, a role with every other right and a group user are
  refused; the previews write nothing; the last administrator and the caller's own account; a new administrator with the
  password from the secret channel and the login with it; sessions end on a new password and a disable, a new role on the
  next call; role rights and refusals; device groups by tag name and the users who lose devices; the key's link once, never
  in an answer, the audit log or the logs, its end capped by the agent's own; an agent never deletes its own key; the audit
  lines as "MCP <key name>"), `AssistantTransferTests` (the key card, Copy once, never in the model's requests), client
  `AssistantTransferUiTests` (the card, Copy to the clipboard once, never shown), and `McpToolTests`, `McpSecretTests`,
  `McpSkillTests` over every new tool.

## Server settings and services as built

Decision 2026-11-15: AI agents (MCP with the key's role and devices, the Assistant with the signed-in user) change server
settings and services like a user in the client. Every change is a preview (the GUI's checks and field texts, nothing
written, the old and the new value) and a run through the gRPC service the client calls (its checks again, its audit);
Administrator where the GUI is, the caller's role always. Secrets only through the secret channel, files only through
upload and download links. The tools: `Tools/ServerChangeTools`, `SiteChangeTools`, `DeviceTrustTools`; the plugins' in
their `Tools/` folders; the shared rules of the Settings page in `Oadm.Contracts.Settings.ServerSettingsRules` (the page
and the tool use the same checks and texts).

- **Server settings** `oadm_settings_set_preview`/`_run` (`SettingsService.Set`, Administrator with all devices): `changes`
  takes only the Settings page's server fields (an allow-list: server name, listen URL, addresses scanned at once, scan
  timeout, Discovery duration, waiting-device retry, parallel tasks per plugin, allowed clock difference, Basic login over
  HTTP, Update the list online); the preview answers per changed field its label, old and new value, `restart_needed` for
  the listen URL and the warnings: Basic over HTTP sends passwords readable on the network; a new listen URL applies at the
  next restart and cuts off clients (and the Assistant in them) that cannot reach it; the online list's source. The run
  writes only the planned fields on top of what is stored then. The Settings page has no host name field; none is offered.
- **MCP** `oadm_mcp_set_preview`/`_run` (`McpService.Set`, Administrator; the tools get the host's `McpService` calls as
  functions, `AgentServerCalls`, so the tool layer names no MCP type): Turn on, Port, Listen on with the card's texts
  (`McpTexts`); the plan warns that agents can no longer connect (off), must change their configuration (port) or lose an
  interface, and, for a caller that came over MCP, that its own connection may drop with the run.
- **Assistant** `oadm_assistant_settings_preview`/`_run` (`AssistantService.SetSettings`, Administrator): the card's fields
  and checks (`AssistantTexts`); `new_api_key: true` declares the secret `api_key` (the card's or the shell's field, never
  the chat); a new provider or address needs the key again like the card; Turn off and a new provider warn that every chat
  ends. `oadm_assistant_test` (`Test` with the saved settings, a read that reaches the provider).
- **OADM PRO trial**: `oadm_license_trial_preview`/`_run` of "Administration as built".
- **Sites** (Administrator): `oadm_sites_waiting` (the waiting nodes with id, key fingerprint, address and version, for the
  bind), `oadm_sites_accept_preview`/`_run` (`SetAccept`; off warns that every site goes Site offline),
  `oadm_sites_bind_preview`/`_run` (`BindSite`; the pairing code is the secret `pairing_code`; the plan shows the
  fingerprint to compare with the node app), `oadm_sites_replace_preview`/`_run` (`ReplaceSite`, only to an online site;
  answers moved and stayed), `oadm_sites_remove_preview`/`_run` (`RemoveSite`, keep the devices as Site offline by default or
  remove them; the plan names the device groups that use the site and warns that the node must be bound again).
- **Trust new certificate** `oadm_devices_certificate_trust_preview`/`_run` (`GetPresentedCertificates`, then
  `TrustNewCertificates` with the fingerprints the preview showed; Add and remove devices; at most 50 devices a preview, it
  asks each device): per device the trusted and the new fingerprint for the person to compare, with a warning that an
  unexplained change can mean someone between OADM and the device; a device that presents another certificate meanwhile is
  refused by the run.
- **Delete all devices** `oadm_devices_remove_all_preview`/`_run` (Administrator; `DeviceService.Remove` with every id):
  the plan counts every device and the devices per site; audited "Removed devices" as the caller.
- **Plugin tools with secrets and files** (SDK `PluginTool.Secrets`, `ICallContext.Uploads`, see "SDK" and "Secrets"): a
  plugin's change declares its secrets and the host declares them on the handle; a `file_id` argument is read through the
  caller's own uploads.
- **Assistant cards** (`AssistantCards`): each preview above has its card (old → new per setting, "3 settings", the
  fingerprints, the sites, "3 devices"); Run is red for MCP or the Assistant turned off, Accept site nodes off, a site removed
  and Delete all devices. Status lines for the new reads (`AssistantTools.LineOf`: "Reading the server settings", "Looking at
  sites", "Testing the provider", "Reading the device certificates").
- **Tests**: `McpServerChangeTests` (the diff and the page's texts, nothing written by a preview, the run once and audited as
  "MCP <key>", Operators and group users list none of the administrator tools, the MCP warnings and Turn off, the API key
  only through the secret channel and never in an answer, the audit or the log, Accept site nodes, unknown
  nodes and sites, Delete all devices, the trust plan and its limit), the lists of `McpToolTests`, `McpSecretTests` and
  `McpSkillTests`.

## Open points

- Client support for the MCP tasks extension: if it spreads, `oadm_task_run` may answer a task handle instead of polling.
- The waiting list keeps no tags or site for a device an agent puts there.
- Import from ADM with logins needs the import parser on the server (it is the client's `DeviceImportFile`) and a gRPC read
  of an upload; until then it stays in OADM. Compliance password and file fields stay in OADM until a kind has one (no
  bundled kind has, and the compliance store does not encrypt settings).
- A VAPIX Commander command treats every password field as required; an optional one cannot be left empty over MCP.
- Install certificates checks its PKCS#12 password on each device (a wrong one fails "Nothing was changed" before any
  write), not when it is entered: `ValidatePayload` cannot read uploads.
- The client's task details have no Save for a task's result file yet (agents and the assistant have it).
- Create configuration file and Export configuration keep their file in the task log only, not as a result file.
- The skills extension is declared without fields; when its schema settles, `McpSkill` may list the skill there too.

