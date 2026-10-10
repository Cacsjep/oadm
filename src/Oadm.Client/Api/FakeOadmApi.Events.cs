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
/// Fake mode: event rules in memory. The catalog has the built-in device status events, the Image Health events and the
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
            ("oadm.device.certificate-expires-soon", "Certificate expires soon", "Device status", "Built in", "30 days or less left."),
            ("oadm.device.certificate-expired", "Certificate expired", "Device status", "Built in", ""),
            ("oadm.image-health.blur", "Blur detected", "Image Health", "Image Health Dashboard", "The picture is out of focus."),
            ("oadm.image-health.block", "Block detected", "Image Health", "Image Health Dashboard", "Something covers the view."),
            ("oadm.image-health.redirect", "Redirect detected", "Image Health", "Image Health Dashboard", "The camera points somewhere else."),
            ("oadm.image-health.under-exposure", "Under-exposure detected", "Image Health", "Image Health Dashboard", "The picture is too dark."),
            ("oadm.image-health.unsuitability", "Unsuitable scene detected", "Image Health", "Image Health Dashboard", "The scene is not suitable for the analysis."),
            ("oadm.image-health.not-running", "App not running", "Image Health", "Image Health Dashboard", "AXIS Image Health Analytics is installed but stopped."),
            ("oadm.image-health.unreachable", "Camera could not be checked", "Image Health", "Image Health Dashboard", ""),
        ];
        foreach (var (id, name, group, source, description) in types)
        {
            catalog.EventTypes.Add(new EventTypeEntry
            {
                Id = id, DisplayName = name, Group = group, Stateful = true, DeviceScope = true, Description = description, Available = true, SourceName = source,
            });
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
        return catalog;
    }

    private static readonly ActionChoiceEntry[] FakeCommands =
    [
        new() { Value = "saved:overlay-on", Label = "Saved: Show warning overlay" },
        new() { Value = "saved:overlay-off", Label = "Saved: Hide warning overlay" },
        new() { Value = "library:restart", Label = "System: Restart device" },
    ];

    private void SeedEventRules()
    {
        var io = _devices.First(d => d.Address == "10.0.0.30");
        AddFakeRule("Blurred camera: warning lamp", true, (1, DateTime.UtcNow.AddMinutes(-12)),
            [Event("oadm.image-health.blur", DeviceSelectorMode.All), Schedule("08:00", "18:00", DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)],
            [Output(Guid.Parse(io.Id), "follow"), Command(DeviceSelectorMode.EventDevice, "saved:overlay-on", "saved:overlay-off")]);
        AddFakeRule("Camera offline in Building A", true, (0, DateTime.UtcNow.AddHours(-3)),
            [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = "oadm.device.unreachable", Devices = new DeviceSelector { Mode = DeviceSelectorMode.Tags, Tags = ["Building A"] } }],
            [Output(Guid.Parse(io.Id), "pulse")]);
        AddFakeRule("Certificates about to expire", false, (0, null),
            [Event("oadm.device.certificate-expires-soon", DeviceSelectorMode.All)],
            [Command(DeviceSelectorMode.EventDevice, "saved:overlay-on", null)]);

        var start = DateTime.UtcNow.AddMinutes(-15);
        AddLogLocked(start, EventLogKind.EventLogEvent, EventLogResult.EventLogInfo, "Blur detected", _devices[5], null, "On");
        AddLogLocked(start.AddSeconds(1), EventLogKind.EventLogRule, EventLogResult.EventLogInfo, "Rule turned on", _devices[5], _rules[0], "Blur detected");
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
        DeviceSelectorMode.Tags => (selector.Tags.Count == 1 ? "tag " : "tags ") + string.Join(", ", selector.Tags),
        _ when selector.DeviceIds.Count == 1 && _devices.FirstOrDefault(d => d.Id == selector.DeviceIds[0].ToString()) is { } device
            => $"{device.Address} ({device.Model.Replace("AXIS ", string.Empty, StringComparison.Ordinal)})",
        _ => $"{selector.DeviceIds.Count} devices",
    };
}
