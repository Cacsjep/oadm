namespace Oadm.Core.Events;

/// <summary>
/// An event rule (table EventRules). <see cref="DefinitionJson"/> is the <c>RuleDefinition</c> as stored on the server:
/// password values of action settings are encrypted (purpose <c>oadm:event-rule:&lt;rule id&gt;:&lt;action key&gt;:&lt;field&gt;</c>).
/// </summary>
public sealed class EventRuleEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string DefinitionJson { get; set; } = "{}";

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? LastFiredUtc { get; set; }
}

/// <summary>
/// A rule that is on for one subject (table EventRuleStates): the device its trigger fired for, or
/// <see cref="Guid.Empty"/> for a server-wide trigger. Only subjects whose on actions ran are stored; a server restart
/// compares them with the fresh evaluation and runs only real changes.
/// </summary>
public sealed class EventRuleStateEntity
{
    public Guid RuleId { get; set; }

    public Guid SubjectId { get; set; }

    public DateTime SinceUtc { get; set; }
}

public enum EventLogKind
{
    Event = 0,
    Rule = 1,
    Action = 2,
    System = 3,
}

public enum EventLogResult
{
    Info = 0,
    Ok = 1,
    Failed = 2,
    Skipped = 3,
}

/// <summary>One line of the event log (table EventLogEntries): an event a rule uses, a rule edge or an action run.</summary>
public sealed class EventLogEntryEntity
{
    public const int MaxTextLength = 200;
    public const int MaxMessageLength = 2000;

    public long Id { get; set; }

    public DateTime TimeUtc { get; set; }

    public EventLogKind Kind { get; set; }

    public EventLogResult Result { get; set; }

    public string What { get; set; } = string.Empty;

    public Guid? DeviceId { get; set; }

    public string Device { get; set; } = string.Empty;

    public Guid? RuleId { get; set; }

    public string Rule { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
