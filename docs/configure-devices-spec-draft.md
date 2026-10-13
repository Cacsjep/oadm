# Configure devices (ADM-compatible): spec draft

Status: **built (2026-10-13)**. The decisions below are in CLAUDE.md and the plugin README
(`plugins/Oadm.Plugins.DeviceConfig/README.md`), which hold the details as built.

## Goal

ADM's most used configuration tool, in OADM: set chosen parameters, action rules and stream profiles on many devices at
once, take them from a device or from a configuration file, and save configuration files. ADM users must find it where
they expect it and must be able to bring their ADM configuration files (including Axis' hardening template).

What must hold:

- **Same workflow as ADM.** Choose devices → Configure devices → pick what to apply (Parameters, Action Rules,
  Additional Settings) → review → Finish. "Use configuration from: Device / Configuration File". "Create configuration
  file" from one device.
- **ADM files in and out.** OADM reads and writes ADM's `.cfg` format (version 3) unchanged.
- **Device safety.** Every device checks the values against its own parameter definitions before anything is written;
  a parameter a device does not have is skipped there, never forced.
- **Scale.** 5,000 selected devices of many models: the dialog stays fast (definitions read once per model and AXIS OS).

## How ADM does it (observed, 2026-10-13)

- Context menu **Configure Devices > Configure…** opens a wizard "Which settings do you want to apply?" with three tabs:
  - **Parameters**: a tree of `param.cgi` groups (`API.RemoteSyslog1`, `Image.I0.Appearance`, ...) with a check box per
    parameter, the parameter's nice name, its value as a text field or a drop-down, and a hint ("An integer", "0 - 100",
    "Min 0"); a search.
  - **Action Rules**: the device's action rules by name, check boxes, "Select / Deselect all".
  - **Additional Settings**: Stream Profiles.
  - Right column: "Use configuration from: **Device** / **Configuration File**" and a card with the source device (Address,
    MAC address, Model, Firmware).
- Several devices of different models or firmware: "The firmware or models of your devices are different. Some settings
  do not apply to all devices." A check box "Show parameters which does not exist on all the selected devices"; drop-downs
  show only the choices all devices share, with a warning icon "Only showing mutual value options, some devices have more
  options."
- **Create configuration file**: the same three tabs titled "Select the settings that should be saved", then a summary
  "These settings will be saved in the configuration file" (Parameters, Action Rules, Stream Profiles), Finish.
- Datasheet: "Configure standard parameters to several devices, including action rules, stream profiles, device time and
  IP settings. Duplicate configurations from already configured devices to multiple devices." Time and IP are parameters
  (`Time.*`, `Network.*`).

## Device API (verified read-only on AXIS P3265-V, AXIS OS 12.11.77)

| Need | Request | Answer |
|---|---|---|
| Parameters with values and definitions | `param.cgi?action=listdefinitions&listformat=xmlschema[&group=X]` | Per parameter: `name`, `value`, `niceName`, `securityLevel`, and a type: `string`, `int min max maxlen`, `bool true false`, `enum` with `entry value niceValue`, `hostname`, `ip`, `ipList`, `password`. Type flags: `internal`, `readonly`, `const`, `hidden`, `writeonly`, `dynamic`, `nosync`. The P3265-V has 1,518 parameters (bool 580, int 428, string 283, enum 186, hostname 18, ip 16, ipList 4, password 3); 352 internal, 296 read-only; 395 KB for all. |
| Write parameters | `param.cgi?action=update&<name>=<value>&...` (POST body) | `OK` or `# Error: ...` per call |
| Action rules | `/vapix/services` action1 `GetActionRules`, `AddActionRule`, `RemoveActionRule` | Rule: `RuleID`, `Name`, `Enabled`, `StartEvent` (TopicExpression + MessageContent filter), conditions, activation timeout, `PrimaryAction` = a configuration id |
| Action configurations | action1 `GetActionConfigurations`, `AddActionConfiguration`, `RemoveActionConfiguration` | `ConfigurationID`, `Name`, `TemplateToken` (e.g. `com.axis.action.fixed.recording.storage`), parameters |
| Schedules (recurrences) | event1 `GetScheduledEvents`, `AddScheduledEvent`, `RemoveScheduledEvent` | `EventID`, `Name`, iCalendar (`DTSTART`, `DTEND`, `RRULE`). Built in: `com.axis.schedules.after_hours`, `office_hours`, `weekdays`, `weekends`; own ones `com.axis.schedules.genid.id-N`. |
| Stream profiles | `streamprofile.cgi` JSON `list`, `create`, `update`, `remove` | `name`, `description`, `parameters` (`camera=1&videocodec=h264&fps=30&resolution=1920x1080&...`); `maxProfiles` 26 |

Recorded answers and two ADM files (P3265-V 12.11.77 with a rule, schedules and three stream profiles; M3206-LVE 10.2.0
from 2021) become the test fixtures.

## ADM configuration file (`.cfg`, format 3)

```xml
<AcmDeviceParameterExport>
  <FileFormatVersion>3</FileFormatVersion>
  <Model>AXIS P3265-V</Model>            <!-- "Generic" in templates -->
  <FirmwareVersion>12.11.77</FirmwareVersion>
  <GeneratedTimestamp>2026-10-13 07:28:40Z</GeneratedTimestamp>
  <MacAddress>B8A44F631339</MacAddress>
  <ParameterList><Parameter><Name>ImageSource.I0.CaptureFrequency</Name><Value>60Hz</Value></Parameter>...</ParameterList>
  <ScheduleList><Schedule><Id>com.axis.schedules.genid.id-0</Id><Name>10</Name>
    <ScheduleDefinition>DTSTART:...&#10;RRULE:FREQ=SECONDLY;INTERVAL=10</ScheduleDefinition></Schedule>...</ScheduleList>
  <ActionRuleList><ActionRule><Name>re</Name><IsEnabled>true</IsEnabled><ActivationTimeout />
    <StartEvent><TopicFilter>tns1:UserAlarm/tnsaxis:Recurring/Pulse</TopicFilter>
      <MessageFilter>boolean(//SimpleItem[@Name="id" and @Value="com.axis.schedules.genid.id-0"])</MessageFilter></StartEvent>
    <ConditionList />
    <PrimaryAction><Id>3</Id><Name>Record video</Name><ActionTemplate>com.axis.action.fixed.recording.storage</ActionTemplate>
      <ParameterList>...</ParameterList></PrimaryAction>
    <ScheduleList /></ActionRule></ActionRuleList>
  <StreamProfileList><StreamProfile><Name>...</Name><Description>...</Description>
    <Parameters>camera=1&amp;videocodec=h264&amp;...</Parameters><IsDefault>false</IsDefault></StreamProfile></StreamProfileList>
</AcmDeviceParameterExport>
```

- UTF-8 with BOM; every section optional (Axis' hardening template has only `ParameterList`, "Generic" model).
- The action configuration is copied **into** the rule (on the device the rule only points to a configuration id).
- `ScheduleList` holds every schedule of the device when a chosen rule uses one (ADM saved all five).
- The format is unchanged since at least 2021.

Reading is defensive (`DeviceXml` limits: 1 MB, no DTD); unknown elements are ignored and kept out of the import; a file
that is not this format is refused: "This is not an AXIS Device Manager configuration file."

### Where the format knowledge comes from

Only from files: Axis' published hardening template (meant to be edited by hand by users, per the ADM security guide)
and configuration files that ADM wrote from the user's own devices. ADM itself (its program files) is never decompiled,
disassembled or copied; no ADM code or text is used. Reading and writing a data format for interoperability is what OADM
does with every Axis API; the file names the product only to say what it is compatible with ("AXIS Device Manager
configuration file"), and OADM's terms already say it is not affiliated with Axis.

## Applying to a device (one task per device)

Steps (named like every OADM task), each skipped when nothing of it was chosen:

1. **Check compatibility**: fresh API list; `param.cgi` must answer; read the definitions of the chosen parameters.
2. **Set parameters**: per parameter: exists on this device, not read-only, value valid for its type and range → one
   `param.cgi update` with all of them; parameters the device does not have or refuses are listed in the step's detail and
   make the task "Done with warnings" ("3 parameters skipped: this device has no System.BoaProtViewer, ...").
3. **Create schedules**: the own schedules (`genid`) the chosen rules use; built-in ones are not touched. The device assigns
   new ids; the rules' filters are rewritten to them.
4. **Create action configurations** and **create action rules**: configuration first, then the rule pointing to the new
   configuration id; a rule with the same name is replaced (Q4).
5. **Set stream profiles**: create, or replace one with the same name (Q4); the device's limit (26) is checked first.
6. **Verify**: read the written parameters back.

Task names: "Configure 12 parameters", "Configure 12 parameters, 1 action rule, 3 stream profiles" (at most 48
characters, shortened per CLAUDE.md "Tasks").

## The dialog (OADM look, ADM workflow)

- Opened from the context menu, group Maintenance, entry **Configure devices** (Q1), for the selected devices.
- Wizard steps (shared `WizardSteps`): **1 Settings** (the three tabs) · **2 Overview** (what will be applied, per section,
  and "Different models or AXIS OS: some settings do not apply to all devices.") · Finish starts the tasks.
- Right column: **Use configuration from: Device** (opens the shared device picker for one device of the system; default
  the first selected device) or **Configuration file** (file picker, `.cfg`); a card shows the source (address, MAC
  address, model, AXIS OS, or the file name and its model).
- **Parameters tab**: a virtualized tree (groups collapsible, nice names, the technical name as tooltip), check box per
  parameter, the value as the right input for its type (text, number with range hint, drop-down for enum and bool,
  password box for passwords), errors below the input (shared validation), a search over nice and technical names, "Show
  parameters that not every selected device has". Internal, hidden, read-only, constant and write-only parameters are not
  shown.
- **Action Rules tab**: the source's rules (name, the event it starts on, the action), check boxes, select all; a rule's
  schedules come along by themselves.
- **Additional settings tab**: Stream profiles (name, description, the parameters as text), check boxes.
- **Create configuration file** (Q7): its own context menu entry (group Maintenance, one selected device): the same three
  tabs titled "Select the settings to save", an overview, **Save** opens the file picker. No task, like Export
  configuration.

## Several devices: how values and choices are found

- Parameter definitions depend on model and AXIS OS, not on the device: the server reads them **once per (model, AXIS OS)**
  of the selection, from one device of each group, and caches them (memory, per model and AXIS OS, refreshed when a
  device of that group reports another AXIS OS). 3,000 cameras in 12 groups = 12 reads.
- Shown: the parameters every group has (or all, with the check box); drop-downs with the choices every group shares and a
  warning icon "Only the choices every selected device has."; ranges narrowed to the overlap.
- Values: from the source only (device or file, Q2); the other selected devices are not read for values.
- One query per group through `TaskService.Query` (read-only by contract), bounded parallelism.

## Relation to existing OADM features

- **Export / Import configuration** (`oadm.device-config`, AXIS OS 12.5+): the device's own full configuration file,
  merge-only, same model only. Configure devices joins this plugin as its third entry (Q1); the 12.5 limit stays with
  Export / Import only, Configure devices works wherever `param.cgi` does.
- **VAPIX Commander**: any VAPIX command; stays for everything beyond parameters, rules and profiles.
- **Hardening scan**: checks; Configure devices applies. No configuration files ship with OADM (Q6); Axis' hardening
  template can be loaded like any file.

## Open questions (one by one)

(none)


## Decisions

- **Q1 (2026-10-13): part of the Device configuration plugin.** `oadm.device-config` (always on) gets a third task
  **Configure devices** next to Export configuration and Import configuration (context menu group Maintenance). Own
  dialog, own task, its own section in the plugin README; the AXIS OS 12.5 requirement applies to Export / Import only.
- **Q2 (2026-10-13): values come from the source, like ADM.** The fields show the source device's values (default the
  first selected device, changeable with Use configuration from: Device) or the file's; only the source is read. A checked
  parameter writes that value to every selected device.
- **Q3 (2026-10-13): every writable parameter is offered; dangerous ones are warned about.** The user chooses and must know
  what they do. Dangerous parameters carry an amber warning icon next to their name with the reason as tooltip:
  network identity (addresses, subnet, router, IPv6 addresses, host name, DHCP / boot protocol: "Every selected device
  gets this value; the devices may become unreachable."), connection (HTTP / HTTPS ports, HTTPS on / off, login method,
  IEEE 802.1X: "OADM may lose the connection to the devices."). The list of dangerous parameters is one shared table in
  the plugin. Checking any of them shows one confirmation before Finish in the message window ("These settings can make
  the devices unreachable: ..." with the parameters, confirm "Apply anyway"). They are written last, in their own step
  "Set connection parameters".
  Amended 2026-10-13 (review): values that must differ per device (IPv4 and IPv6 address, host name, the Bonjour, UPnP and DNS
  update names) are not offered when several devices are selected ("Addresses and names differ per device: use Network
  settings or Assign IP address."); for one device they stay, with the warning. The source device, when selected, keeps its
  own action rules, schedules and stream profiles (they are not written back to it).
- **Q4 (2026-10-13): same name = replace.** A new action rule is created first and then the old one of the same name is
  removed (its action configuration too, when no other rule uses it); a schedule with the same name and recurrence is used
  again; a stream profile with the same name is updated. Applying the same configuration twice gives the same result, never duplicates. The step detail says
  "Replaced action rule Record on motion".
- **Q5 (2026-10-13): passwords in plain text like ADM.** Password parameters and action configuration logins are written
  into created files as ADM does; full ADM compatibility. Passwords are never logged and never shown in the dialog
  (password boxes); the task log names the parameter, never its value.
- **Q6 (2026-10-13): no bundled configuration files.** Like ADM, OADM ships none; users load their own (or Axis' hardening
  template from the hardening guide).
- **Q7 (2026-10-13): "Create configuration file" is its own entry, like ADM.** Context menu Maintenance, one selected
  device; the three tabs, an overview, Save to a `.cfg`. A dialog, never a task.
- **Q8 (2026-10-13): files only, like ADM.** No configurations stored on the server in v1; a shared list can follow if users
  ask, with the `.cfg` format as its storage format.
- **Q9 (2026-10-13): rights like Import configuration.** `task:oadm.device-config.configure:configure` "Configure devices"
  (parameters, action rules, stream profiles; Operator yes, Viewer no) and `:dangerous` "Configure dangerous parameters"
  (the warned parameters of Q3; Operator no, full device control; without it they are not offered). "Create configuration
  file" has its own key `task:oadm.device-config.create` (the file can hold passwords in plain text; Operator yes, Viewer
  no).
