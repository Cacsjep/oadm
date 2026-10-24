using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;
using Oadm.Sdk.Events;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode: event rules in memory. The catalog has the built-in device status events, two sample device triggers and the
/// two bundled actions; three sample rules (one on, one off) and a sample event log. No rule really runs; saving checks
/// the basics like the server and logs a line.
/// </summary>
public sealed partial class FakeOadmApi
{
    private readonly List<RuleDefinition> _rules = [];
    private readonly Dictionary<string, (int Active, DateTime? LastFired)> _ruleStates = [];
    private readonly List<EventLogEntry> _eventLog = [];
    private readonly Broadcast<RuleList> _ruleEvents = new();
    private readonly Broadcast<EventLogPage> _eventLogEvents = new();
    private long _eventLogId;

    private static readonly EventCatalog FakeCatalog = BuildFakeCatalog();

    private static EventCatalog BuildFakeCatalog()
    {
        var catalog = new EventCatalog();
        (string Id, string Name, string Group, string Source, string Description)[] types =
        [
            ("oadm.device.unreachable", "Device unreachable", "Device status", "Built in", "The device does not answer."),
            ("oadm.device.credentials-required", "Credentials required", "Device status", "Built in", "The device rejects the stored login."),
            ("oadm.device.certificate-changed", "Certificate changed", "Device status", "Built in", "The device presents another HTTPS certificate."),
            ("oadm.device.certificate-expires-soon", "Certificate expires soon", "Certificates", "PKI", "Within the days of its Expiry warning policy."),
            ("oadm.device.certificate-expired", "Certificate expired", "Certificates", "PKI", ""),
            (FakePersonTrigger, "Person in the yard", "Device triggers", "Device triggers", "AXIS Object Analytics › Scenario 1.\n\nValues:\n• {active}"),
            (FakePersonTrigger + ".lost", "Person in the yard: connection lost", "Device triggers", "Device triggers", "No events of this trigger from the device for 30 s.\n\nValues:\n• {reason}"),
            ("oadm.device-triggers.sample-gate", "Gate contact", "Device triggers", "Device triggers", "Device › Input ports › Digital input port · Port: Port 1 (0).\n\nValues:\n• {port}\n• {state}"),
            ("oadm.device-triggers.sample-gate.lost", "Gate contact: connection lost", "Device triggers", "Device triggers", "No events of this trigger from the device for 30 s.\n\nValues:\n• {reason}"),
        ];
        foreach (var (id, name, group, source, description) in types)
        {
            catalog.EventTypes.Add(new EventTypeEntry
            {
                Id = id, DisplayName = name, Group = group, Stateful = true, DeviceScope = true, Description = description, Available = true, SourceName = source,
            });
        }

        var person = catalog.EventTypes.Single(t => t.Id == FakePersonTrigger);
        person.Description = "AXIS Object Analytics › Scenario 1.\n\nValues:\n• {active}\n• {objectId}\n• {classTypes}\n• {triggerTime}";
        foreach (var (field, label) in new[] { ("active", "Active"), ("objectId", "Object ID"), ("classTypes", "Class types"), ("triggerTime", "Trigger time") })
        {
            person.Fields.Add(new EventFieldInfo { Name = field, DisplayName = label });
        }

        var output = new ActionEntry
        {
            Id = "oadm.outputs.set", DisplayName = "Set output", Group = "Devices", Description = "Switches an output of the device, for example a relay.",
            IconKey = "activity", NeedsDevice = true, SupportsTurnOff = true, Available = true, SourceName = "Outputs",
        };
        output.Fields.Add(new ActionFieldEntry { Key = "port", Label = "Output", Kind = ActionFieldKind.ActionFieldNumber, Required = true, DefaultValue = "1", Min = 1, Max = 64, Hint = "The output number as in the device's I/O settings (1 = the first)." });
        var mode = new ActionFieldEntry { Key = "mode", Label = "Mode", Kind = ActionFieldKind.ActionFieldChoice, Required = true, DefaultValue = "pulse" };
        mode.Choices.Add(new ActionChoiceEntry { Value = "follow", Label = "Follow the rule" });
        mode.Choices.Add(new ActionChoiceEntry { Value = "pulse", Label = "Pulse" });
        mode.Choices.Add(new ActionChoiceEntry { Value = "active", Label = "Set active" });
        mode.Choices.Add(new ActionChoiceEntry { Value = "inactive", Label = "Set inactive" });
        mode.Choices.Add(new ActionChoiceEntry { Value = "toggle", Label = "Toggle" });
        output.Fields.Add(mode);
        output.Fields.Add(new ActionFieldEntry { Key = "seconds", Label = "Seconds", Kind = ActionFieldKind.ActionFieldNumber, Required = true, DefaultValue = "5", Min = 1, Max = 3600, VisibleWhenKey = "mode", VisibleWhenValue = "pulse" });
        catalog.Actions.Add(output);

        var command = new ActionEntry
        {
            Id = "oadm.vapix-commander.command", DisplayName = "Run VAPIX command", Group = "Devices", Description = "Sends a saved VAPIX Commander command to the device.",
            IconKey = "command", NeedsDevice = true, SupportsTurnOff = true, Available = true, SourceName = "VAPIX Commander",
        };
        command.Fields.Add(new ActionFieldEntry { Key = "command", Label = "Command", Kind = ActionFieldKind.ActionFieldDynamicChoice, Required = true, Hint = "Saved commands keep their values and passwords." });
        command.Fields.Add(new ActionFieldEntry { Key = "offCommand", Label = "When the rule turns off", Kind = ActionFieldKind.ActionFieldDynamicChoice, OffEdge = true, Hint = "Optional" });
        command.Fields.Add(new ActionFieldEntry { Key = "allowDangerous", Label = "Allow dangerous commands", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false" });
        catalog.Actions.Add(command);
        catalog.Actions.Add(FakeHttpAction());
        catalog.Actions.Add(FakeEmailAction());
        catalog.Actions.Add(FakeTcpAction());
        return catalog;
    }

    /// <summary>"Send HTTP request" as the Notifications plugin declares it.</summary>
    private static ActionEntry FakeHttpAction()
    {
        var http = new ActionEntry
        {
            Id = "oadm.notifications.http", DisplayName = "Send HTTP request", Group = "Notifications", IconKey = "send", NeedsDevice = false, SupportsTurnOff = true, UsesPlaceholders = true,
            Description = "Sends a request to a web service, for example a webhook of Teams, Slack or a building system.", Available = true, SourceName = "Notifications",
        };
        http.Fields.Add(Choice("method", "Method", "POST", ("GET", "GET"), ("POST", "POST"), ("PUT", "PUT"), ("PATCH", "PATCH"), ("DELETE", "DELETE")));
        http.Fields.Add(new ActionFieldEntry { Key = "url", Label = "URL", Kind = ActionFieldKind.ActionFieldText, Required = true, MaxLength = 1000 });
        http.Fields.Add(new ActionFieldEntry { Key = "query", Label = "Query parameters", Kind = ActionFieldKind.ActionFieldKeyValues, MaxLength = 20_000 });
        http.Fields.Add(new ActionFieldEntry { Key = "headers", Label = "Headers", Kind = ActionFieldKind.ActionFieldKeyValues, MaxLength = 20_000 });
        http.Fields.Add(Choice("auth", "Login", "none", ("none", "None"), ("basic", "Basic"), ("digest", "Digest"), ("bearer", "Bearer token")));
        http.Fields.Add(new ActionFieldEntry { Key = "user", Label = "User name", Kind = ActionFieldKind.ActionFieldText, VisibleWhenKey = "auth", VisibleWhenValue = "basic|digest", MaxLength = 1000 });
        http.Fields.Add(new ActionFieldEntry { Key = "password", Label = "Password", Kind = ActionFieldKind.ActionFieldPassword, VisibleWhenKey = "auth", VisibleWhenValue = "basic|digest", MaxLength = 256 });
        http.Fields.Add(new ActionFieldEntry { Key = "token", Label = "Token", Kind = ActionFieldKind.ActionFieldPassword, VisibleWhenKey = "auth", VisibleWhenValue = "bearer", MaxLength = 4096 });
        http.Fields.Add(Choice("bodyType", "Body type", "json", ("none", "None"), ("json", "JSON"), ("xml", "XML"), ("text", "Text"), ("form", "Form")));
        http.Fields.Add(new ActionFieldEntry
        {
            Key = "body", Label = "Body", Kind = ActionFieldKind.ActionFieldCode, LanguageKey = "bodyType", VisibleWhenKey = "bodyType", VisibleWhenValue = "json|xml|text|form",
            MaxLength = 50_000, Hint = "Form: one name=value per line.",
            DefaultValue = "{\n  \"rule\": \"{rule}\",\n  \"event\": \"{event}\",\n  \"device\": \"{device}\",\n  \"state\": \"{state}\",\n  \"time\": \"{time}\"\n}",
        });
        http.Fields.Add(new ActionFieldEntry { Key = "allowUntrusted", Label = "Allow untrusted certificates", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false" });
        http.Fields.Add(new ActionFieldEntry { Key = "timeout", Label = "Timeout (s)", Kind = ActionFieldKind.ActionFieldNumber, Required = true, Min = 1, Max = 120, DefaultValue = "15" });
        http.Fields.Add(new ActionFieldEntry { Key = "onOff", Label = "Also send when the rule turns off", Kind = ActionFieldKind.ActionFieldBool, OffEdge = true, DefaultValue = "false" });
        return http;
    }

    /// <summary>"Send email" as the Notifications plugin declares it.</summary>
    private static ActionEntry FakeEmailAction()
    {
        var email = new ActionEntry
        {
            Id = "oadm.notifications.email", DisplayName = "Send email", Group = "Notifications", IconKey = "send", NeedsDevice = false, SupportsTurnOff = true, UsesPlaceholders = true,
            Description = "Sends an email through your mail server.", Available = true, SourceName = "Notifications",
        };
        email.Fields.Add(new ActionFieldEntry { Key = "host", Label = "Mail server", Kind = ActionFieldKind.ActionFieldText, Required = true, MaxLength = 1000 });
        email.Fields.Add(new ActionFieldEntry { Key = "port", Label = "Port", Kind = ActionFieldKind.ActionFieldNumber, Required = true, Min = 1, Max = 65535, DefaultValue = "587" });
        email.Fields.Add(Choice("security", "Encryption", "starttls", ("starttls", "STARTTLS (usually port 587)"), ("tls", "TLS (usually port 465)"), ("none", "None")));
        email.Fields.Add(new ActionFieldEntry { Key = "allowUntrusted", Label = "Allow untrusted certificates", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false" });
        email.Fields.Add(new ActionFieldEntry { Key = "user", Label = "User name", Kind = ActionFieldKind.ActionFieldText, Hint = "Optional", MaxLength = 1000 });
        email.Fields.Add(new ActionFieldEntry { Key = "password", Label = "Password", Kind = ActionFieldKind.ActionFieldPassword, MaxLength = 256 });
        email.Fields.Add(new ActionFieldEntry { Key = "from", Label = "From", Kind = ActionFieldKind.ActionFieldText, Required = true, MaxLength = 1000 });
        email.Fields.Add(new ActionFieldEntry { Key = "to", Label = "To", Kind = ActionFieldKind.ActionFieldText, Required = true, Hint = "Several addresses separated by commas", MaxLength = 1000 });
        email.Fields.Add(new ActionFieldEntry { Key = "subject", Label = "Subject", Kind = ActionFieldKind.ActionFieldText, Required = true, DefaultValue = "{rule}: {event} on {device}", MaxLength = 1000 });
        email.Fields.Add(Choice("format", "Format", "text", ("text", "Text"), ("html", "HTML")));
        email.Fields.Add(new ActionFieldEntry
        {
            Key = "body", Label = "Body", Kind = ActionFieldKind.ActionFieldCode, LanguageKey = "format", Required = true, MaxLength = 50_000,
            DefaultValue = "{event} on {device} at {time}.\n\nRule: {rule}\nDevice: {model}, {serial}\n{webUrl}",
        });
        email.Fields.Add(new ActionFieldEntry { Key = "onOff", Label = "Also send when the rule turns off", Kind = ActionFieldKind.ActionFieldBool, OffEdge = true, DefaultValue = "false" });
        return email;
    }

    /// <summary>"Send TCP message" as the Notifications plugin declares it.</summary>
    private static ActionEntry FakeTcpAction()
    {
        var tcp = new ActionEntry
        {
            Id = "oadm.notifications.tcp", DisplayName = "Send TCP message", Group = "Notifications", IconKey = "send", NeedsDevice = false, SupportsTurnOff = true, UsesPlaceholders = true,
            Description = "Sends a message over a TCP connection, for example to a building, alarm or video system.", Available = true, SourceName = "Notifications",
        };
        tcp.Fields.Add(new ActionFieldEntry { Key = "host", Label = "Host", Kind = ActionFieldKind.ActionFieldText, Required = true, Hint = "Host name or IP address", MaxLength = 1000 });
        tcp.Fields.Add(new ActionFieldEntry { Key = "port", Label = "Port", Kind = ActionFieldKind.ActionFieldNumber, Required = true, Min = 1, Max = 65535, DefaultValue = "8000" });
        tcp.Fields.Add(Choice("format", "Format", "text", ("text", "Text"), ("json", "JSON"), ("xml", "XML"), ("hex", "Hex bytes")));
        tcp.Fields.Add(new ActionFieldEntry
        {
            Key = "message", Label = "Message", Kind = ActionFieldKind.ActionFieldCode, LanguageKey = "format", Required = true, MaxLength = 50_000,
            Hint = "For hex bytes: pairs like 02 41 42 03, spaces optional.", DefaultValue = "{event} on {device} at {time}",
        });
        var lineEnd = Choice("lineEnd", "Line end", "none", ("none", "None"), ("cr", "CR"), ("lf", "LF"), ("crlf", "CR LF"));
        lineEnd.VisibleWhenKey = "format";
        lineEnd.VisibleWhenValue = "text|json|xml";
        tcp.Fields.Add(lineEnd);
        tcp.Fields.Add(new ActionFieldEntry { Key = "tls", Label = "Use TLS", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false" });
        tcp.Fields.Add(new ActionFieldEntry { Key = "allowUntrusted", Label = "Allow untrusted certificates", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false", VisibleWhenKey = "tls", VisibleWhenValue = "true" });
        tcp.Fields.Add(new ActionFieldEntry { Key = "waitAnswer", Label = "Wait for an answer", Kind = ActionFieldKind.ActionFieldBool, DefaultValue = "false" });
        tcp.Fields.Add(new ActionFieldEntry { Key = "expect", Label = "Answer must contain", Kind = ActionFieldKind.ActionFieldText, Hint = "Optional", MaxLength = 1000, VisibleWhenKey = "waitAnswer", VisibleWhenValue = "true" });
        tcp.Fields.Add(new ActionFieldEntry { Key = "timeout", Label = "Timeout (s)", Kind = ActionFieldKind.ActionFieldNumber, Required = true, Min = 1, Max = 120, DefaultValue = "10" });
        tcp.Fields.Add(new ActionFieldEntry { Key = "onOff", Label = "Also send when the rule turns off", Kind = ActionFieldKind.ActionFieldBool, OffEdge = true, DefaultValue = "false" });
        return tcp;
    }

    private static ActionFieldEntry Choice(string key, string label, string defaultValue, params (string Value, string Label)[] choices)
    {
        var field = new ActionFieldEntry { Key = key, Label = label, Kind = ActionFieldKind.ActionFieldChoice, Required = true, DefaultValue = defaultValue };
        field.Choices.AddRange(choices.Select(c => new ActionChoiceEntry { Value = c.Value, Label = c.Label }));
        return field;
    }

    /// <summary>The sample device trigger of the fake event rules.</summary>
    internal const string FakePersonTrigger = "oadm.device-triggers.sample-person";

    private static readonly ActionChoiceEntry[] FakeCommands =
    [
        new() { Value = "saved:overlay-on", Label = "Saved: Show warning overlay" },
        new() { Value = "saved:overlay-off", Label = "Saved: Hide warning overlay" },
        new() { Value = "library:restart", Label = "System: Restart device" },
    ];

    private void SeedEventRules()
    {
        var io = _devices.First(d => d.Address == "10.0.0.30");
        AddFakeRule("Person in the yard: warning lamp", true, (1, DateTime.UtcNow.AddMinutes(-12)),
            [Event(FakePersonTrigger, DeviceSelectorMode.All), Schedule("08:00", "18:00", DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)],
            [Output(Guid.Parse(io.Id), "follow"), Command(DeviceSelectorMode.EventDevice, "saved:overlay-on", "saved:overlay-off")]);
        AddFakeRule("Camera offline in Building A", true, (0, DateTime.UtcNow.AddHours(-3)),
            [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = "oadm.device.unreachable", Devices = new DeviceSelector { Mode = DeviceSelectorMode.Tags, Tags = ["Building A"] } }],
            [Output(Guid.Parse(io.Id), "pulse")]);
        AddFakeRule("Certificates about to expire", false, (0, null),
            [Event("oadm.device.certificate-expires-soon", DeviceSelectorMode.All)],
            [Command(DeviceSelectorMode.EventDevice, "saved:overlay-on", null)]);

        var start = DateTime.UtcNow.AddMinutes(-15);
        AddLogLocked(start, EventLogKind.EventLogEvent, EventLogResult.EventLogInfo, "Person in the yard", _devices[5], null, "On");
        AddLogLocked(start.AddSeconds(1), EventLogKind.EventLogRule, EventLogResult.EventLogInfo, "Rule turned on", _devices[5], _rules[0], "Person in the yard");
        AddLogLocked(start.AddSeconds(2), EventLogKind.EventLogAction, EventLogResult.EventLogOk, "Set output", io, _rules[0], "Rule turned on: Output 1 active");
        AddLogLocked(start.AddSeconds(2), EventLogKind.EventLogAction, EventLogResult.EventLogOk, "Run VAPIX command", _devices[5], _rules[0], "Rule turned on: Show warning overlay");
        AddLogLocked(start.AddMinutes(-170), EventLogKind.EventLogEvent, EventLogResult.EventLogInfo, "Device unreachable", _devices[4], null, "On");
        AddLogLocked(start.AddMinutes(-170), EventLogKind.EventLogRule, EventLogResult.EventLogInfo, "Rule turned on", _devices[4], _rules[1], "Device unreachable");
        AddLogLocked(start.AddMinutes(-170), EventLogKind.EventLogAction, EventLogResult.EventLogFailed, "Set output", io, _rules[1], "Unreachable - no route to the device");
        _eventLog.Sort((a, b) => a.Id.CompareTo(b.Id));

        static RuleCondition Event(string type, DeviceSelectorMode mode) =>
            new() { Kind = RuleConditionKind.Event, EventTypeId = type, Devices = new DeviceSelector { Mode = mode } };

        static RuleCondition Schedule(string from, string to, params DayOfWeek[] days) =>
            new() { Kind = RuleConditionKind.Schedule, Schedule = new RuleSchedule { Days = [.. days], From = from, To = to } };

        static RuleAction Output(Guid device, string mode) => new()
        {
            ActionId = "oadm.outputs.set",
            Targets = new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [device] },
            Settings = new JsonObject { ["port"] = 1, ["mode"] = mode, ["seconds"] = 5 },
        };

        static RuleAction Command(DeviceSelectorMode mode, string on, string? off) => new()
        {
            ActionId = "oadm.vapix-commander.command",
            Targets = new DeviceSelector { Mode = mode },
            Settings = off is null
                ? new JsonObject { ["command"] = on, ["allowDangerous"] = false }
                : new JsonObject { ["command"] = on, ["offCommand"] = off, ["allowDangerous"] = false },
        };
    }

    private void AddFakeRule(string name, bool enabled, (int Active, DateTime? LastFired) state, List<RuleCondition> conditions, List<RuleAction> actions)
    {
        var rule = new RuleDefinition { Id = Guid.NewGuid(), Name = name, Enabled = enabled, Conditions = conditions, Actions = actions };
        _rules.Add(rule);
        _ruleStates[rule.Id.ToString()] = state;
    }

    public Task<EventCatalog> GetEventCatalogAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(FakeCatalog.Clone());
        }
    }

    public Task<IReadOnlyList<RuleSummary>> ListRulesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<RuleSummary>>(RuleListLocked().Rules);
        }
    }

    public async IAsyncEnumerable<RuleList> WatchRulesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<RuleList> channel;
        RuleList first;
        lock (_gate)
        {
            ThrowIfOffline();
            channel = _ruleEvents.Subscribe();
            first = RuleListLocked();
        }

        try
        {
            yield return first;
            await foreach (var list in channel.Reader.ReadAllAsync(ct))
            {
                yield return list;
            }
        }
        finally
        {
            _ruleEvents.Unsubscribe(channel);
        }
    }

    public Task<RuleDetail> GetRuleAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var rule = FindRuleLocked(id);
            return Task.FromResult(new RuleDetail { Summary = SummaryLocked(rule), DefinitionJson = rule.ToJson() });
        }
    }

    public Task<RuleDetail> SaveRuleAsync(string definitionJson, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            RuleDefinition rule;
            try
            {
                rule = RuleDefinition.FromJson(definitionJson);
            }
            catch (JsonException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The rule could not be read."));
            }

            var problem = string.IsNullOrWhiteSpace(rule.Name) ? "Enter a name."
                : rule.Conditions.Count == 0 || rule.Conditions[0].Kind != RuleConditionKind.Event ? "The rule needs an event as its first condition."
                : rule.Actions.Count == 0 ? "Add at least one action."
                : null;
            if (problem is not null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, problem));
            }

            rule.Name = rule.Name.Trim();
            foreach (var action in rule.Actions)
            {
                action.StoredSecretFields = [];
            }

            if (rule.Id == Guid.Empty)
            {
                rule.Id = Guid.NewGuid();
                _rules.Add(rule);
                _ruleStates[rule.Id.ToString()] = (0, null);
            }
            else
            {
                var index = _rules.FindIndex(r => r.Id == rule.Id);
                if (index < 0)
                {
                    throw new RpcException(new Status(StatusCode.NotFound, "The rule no longer exists."));
                }

                _rules[index] = rule;
            }

            PublishRulesLocked();
            return Task.FromResult(new RuleDetail { Summary = SummaryLocked(rule), DefinitionJson = rule.ToJson() });
        }
    }

    public Task DeleteRuleAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _rules.Remove(FindRuleLocked(id));
            PublishRulesLocked();
            return Task.CompletedTask;
        }
    }

    public Task<RuleSummary> SetRuleEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var rule = FindRuleLocked(id);
            rule.Enabled = enabled;
            if (!enabled)
            {
                _ruleStates[id] = (0, _ruleStates.GetValueOrDefault(id).LastFired);
            }

            PublishRulesLocked();
            return Task.FromResult(SummaryLocked(rule));
        }
    }

    public Task<RuleDetail> DuplicateRuleAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var copy = FindRuleLocked(id).Clone();
            copy.Id = Guid.NewGuid();
            copy.Enabled = false;
            copy.Name += " (copy)";
            _rules.Add(copy);
            _ruleStates[copy.Id.ToString()] = (0, null);
            PublishRulesLocked();
            return Task.FromResult(new RuleDetail { Summary = SummaryLocked(copy), DefinitionJson = copy.ToJson() });
        }
    }

    public Task<IReadOnlyList<ActionChoiceEntry>> ListActionChoicesAsync(string actionId, string fieldKey, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<ActionChoiceEntry>>(actionId == "oadm.vapix-commander.command" ? [.. FakeCommands.Select(c => c.Clone())] : []);
        }
    }

    public Task<EventLogPage> ListEventLogAsync(int limit, string? ruleId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var page = new EventLogPage { TotalCount = _eventLog.Count };
            IEnumerable<EventLogEntry> entries = Enumerable.Reverse(_eventLog);
            if (!string.IsNullOrEmpty(ruleId))
            {
                entries = entries.Where(e => e.RuleId == ruleId);
            }

            page.Entries.AddRange(entries.Take(limit <= 0 ? 10_000 : limit).Select(e => e.Clone()));
            return Task.FromResult(page);
        }
    }

    public Task<TestActionReply> TestEventActionAsync(TestActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
        }

        return Task.FromResult(new TestActionReply { Ok = true, Message = "Sent (fake mode: nothing left this computer)" });
    }

    public Task<int> ClearEventLogAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var deleted = _eventLog.Count;
            _eventLog.Clear();
            return Task.FromResult(deleted);
        }
    }

    public async IAsyncEnumerable<EventLogPage> WatchEventLogAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<EventLogPage> channel;
        lock (_gate)
        {
            ThrowIfOffline();
            channel = _eventLogEvents.Subscribe();
        }

        try
        {
            await foreach (var page in channel.Reader.ReadAllAsync(ct))
            {
                yield return page;
            }
        }
        finally
        {
            _eventLogEvents.Unsubscribe(channel);
        }
    }

    /// <summary>Tests: a new log line as if a rule ran.</summary>
    public void AddEventLog(string what, string message, EventLogResult result = EventLogResult.EventLogOk)
    {
        lock (_gate)
        {
            var entry = AddLogLocked(DateTime.UtcNow, EventLogKind.EventLogAction, result, what, _devices.FirstOrDefault(), _rules.FirstOrDefault(), message);
            _eventLogEvents.Publish(new EventLogPage { Entries = { entry.Clone() }, TotalCount = 1 });
        }
    }

    private EventLogEntry AddLogLocked(DateTime time, EventLogKind kind, EventLogResult result, string what, Device? device, RuleDefinition? rule, string message)
    {
        var entry = new EventLogEntry
        {
            Id = ++_eventLogId,
            Time = Timestamp.FromDateTime(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
            Kind = kind,
            Result = result,
            What = what,
            DeviceId = device?.Id ?? string.Empty,
            Device = device is null ? string.Empty : $"{device.Address} ({device.Model.Replace("AXIS ", string.Empty, StringComparison.Ordinal)})",
            RuleId = rule?.Id.ToString() ?? string.Empty,
            Rule = rule?.Name ?? string.Empty,
            Message = message,
        };
        _eventLog.Add(entry);
        return entry;
    }

    private RuleDefinition FindRuleLocked(string id) =>
        _rules.FirstOrDefault(r => r.Id.ToString() == id) ?? throw new RpcException(new Status(StatusCode.NotFound, "The rule no longer exists."));

    private void PublishRulesLocked() => _ruleEvents.Publish(RuleListLocked());

    private RuleList RuleListLocked()
    {
        var list = new RuleList();
        list.Rules.AddRange(_rules.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(SummaryLocked));
        return list;
    }

    private RuleSummary SummaryLocked(RuleDefinition rule)
    {
        var (active, lastFired) = _ruleStates.GetValueOrDefault(rule.Id.ToString());
        var summary = new RuleSummary
        {
            Id = rule.Id.ToString(),
            Name = rule.Name,
            Enabled = rule.Enabled,
            ConditionsText = string.Join(" · ", rule.Conditions.Select((c, i) => FakeConditionText(c, i))),
            ActionsText = string.Join(" · ", rule.Actions.Select(FakeActionText)),
            Stateful = true,
            ActiveCount = rule.Enabled ? active : 0,
            StateText = !rule.Enabled ? "Turned off" : active switch { 0 => "Off", 1 => "On", _ => $"On for {active} devices" },
        };
        if (lastFired is { } fired)
        {
            summary.LastFired = Timestamp.FromDateTime(DateTime.SpecifyKind(fired, DateTimeKind.Utc));
        }

        return summary;
    }

    private string FakeConditionText(RuleCondition condition, int index)
    {
        if (condition.Kind == RuleConditionKind.Schedule && condition.Schedule is { } schedule)
        {
            var days = schedule.Days.Count == 7 ? "Every day" : schedule.Days.Count == 5 && !schedule.Days.Contains(DayOfWeek.Saturday) && !schedule.Days.Contains(DayOfWeek.Sunday) ? "Mon-Fri"
                : string.Join(", ", schedule.Days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)));
            return $"{days} {schedule.From}-{schedule.To}";
        }

        var name = FakeCatalog.EventTypes.FirstOrDefault(t => t.Id == condition.EventTypeId)?.DisplayName ?? condition.EventTypeId ?? "?";
        var scope = FakeSelectorText(condition.Devices, "same device");
        return (index == 0 ? name : "and " + name) + $" ({scope})";
    }

    private string FakeActionText(RuleAction action)
    {
        var settings = action.Settings;
        string text = action.ActionId switch
        {
            "oadm.outputs.set" => settings["mode"]?.ToString() switch
            {
                "follow" => $"Output {settings["port"]} follows the rule",
                "pulse" => $"Pulse output {settings["port"]} for {settings["seconds"]} s",
                "active" => $"Set output {settings["port"]} active",
                "inactive" => $"Set output {settings["port"]} inactive",
                _ => $"Toggle output {settings["port"]}",
            },
            "oadm.vapix-commander.command" => FakeCommandText(settings),
            _ => action.ActionId,
        };
        return action.Targets.Mode == DeviceSelectorMode.EventDevice ? text : text + " on " + FakeSelectorText(action.Targets, "the event's device");
    }

    private static string FakeCommandText(JsonObject settings)
    {
        string Name(string key) => FakeCommands.FirstOrDefault(c => c.Value == settings[key]?.ToString())?.Label.Split(": ", 2)[^1] ?? "?";
        return settings["offCommand"] is null ? $"Run \"{Name("command")}\"" : $"Run \"{Name("command")}\", when off \"{Name("offCommand")}\"";
    }

    private string FakeSelectorText(DeviceSelector selector, string sameDevice) => selector.Mode switch
    {
        DeviceSelectorMode.All => "all devices",
        DeviceSelectorMode.EventDevice => sameDevice,
        _ => FakeChoiceText(selector),
    };

    private string FakeChoiceText(DeviceSelector selector)
    {
        var tags = selector.Tags.Count == 0 ? null : (selector.Tags.Count == 1 ? "tag " : "tags ") + string.Join(", ", selector.Tags);
        var devices = selector.DeviceIds.Count switch
        {
            0 => null,
            1 when _devices.FirstOrDefault(d => d.Id == selector.DeviceIds[0].ToString()) is { } device
                => $"{device.Address} ({device.Model.Replace("AXIS ", string.Empty, StringComparison.Ordinal)})",
            _ => $"{selector.DeviceIds.Count} devices",
        };
        return tags is not null && devices is not null ? $"{tags} and {devices}" : tags ?? devices ?? "no devices";
    }
}
