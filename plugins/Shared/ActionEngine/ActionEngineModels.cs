namespace Oadm.Plugins.ActionEngine;

// The device's action rule engine as one model, shared as source by the Action Rule Editor and Device configuration.

/// <summary>A parameter of an action, a recipient or a template, exactly as the device stores it.</summary>
public sealed record NameValue(string Name, string Value);

/// <summary>An event filter of a rule: the topic and an optional message filter, as the device stores them.</summary>
public sealed record EventFilter(string Topic, string? Message);

/// <summary>
/// The action of a rule: the template (<c>com.axis.action.fixed.notification.tcp</c>) and every parameter in one flat list,
/// the action's own and the copied recipient's (SOAP stores them together; REST splits them by the recipient template).
/// </summary>
public sealed record RuleAction(string Template, IReadOnlyList<NameValue> Parameters)
{
    /// <summary>The device's action configuration id (SOAP <c>PrimaryAction</c>); never compared.</summary>
    public string? ConfigurationId { get; init; }

    /// <summary>The configuration's name (the web interface writes the action's label); kept when recreated.</summary>
    public string? Name { get; init; }

    public string? Get(string name) => Parameters.FirstOrDefault(p => p.Name == name)?.Value;
}

/// <summary>
/// An action rule with its action, read over SOAP (passwords in plain text: server only, never sent to the client or
/// logged). <see cref="StartEvent"/> null and <see cref="Conditions"/> set = a rule that is active while the conditions
/// hold ("while the rule is active" actions need that).
/// </summary>
public sealed record DeviceRule(string Id, string Name, bool Enabled, EventFilter? StartEvent, IReadOnlyList<EventFilter> Conditions, string? Timeout, RuleAction Action);

/// <summary>An action template the device offers: its parameters and the recipient template whose parameters it copies.</summary>
public sealed record ActionTemplate(string Token, string? RecipientTemplate, IReadOnlyList<string> Parameters)
{
    /// <summary>Fixed = once per start; unlimited = while the rule is active.</summary>
    public bool IsWhileActive => Token.StartsWith("com.axis.action.unlimited.", StringComparison.Ordinal);
}

/// <summary>A recipient template (<c>com.axis.recipient.ftp</c>) and its parameter names.</summary>
public sealed record RecipientTemplate(string Token, IReadOnlyList<string> Parameters);

/// <summary>A recipient preset of the device (Recipients tab). Rules copy its parameters when created; no link stays.</summary>
public sealed record DeviceRecipient(string Id, string Name, string Template, IReadOnlyList<NameValue> Parameters);

/// <summary>A schedule (iCalendar text) and its event id, which rules name in their filters.</summary>
public sealed record DeviceSchedule(string Id, string Name, string Definition)
{
    /// <summary>The schedules every device has (<c>com.axis.schedules.office_hours</c> ...).</summary>
    public bool IsBuiltIn => Id.StartsWith("com.axis.schedules.", StringComparison.Ordinal)
        && !Id.StartsWith("com.axis.schedules.genid.", StringComparison.Ordinal)
        && !Id.StartsWith("com.axis.schedules.oadm.", StringComparison.Ordinal);
}

/// <summary>A rule as listed, also one whose action configuration is missing: id, name and the configuration it names.</summary>
public sealed record RuleListing(string Id, string Name, string? ConfigurationId);

/// <summary>Everything the device's rule engine holds, read in one pass.</summary>
public sealed record DeviceRuleSet(
    IReadOnlyList<DeviceRule> Rules,
    IReadOnlyList<ActionTemplate> ActionTemplates,
    IReadOnlyList<RecipientTemplate> RecipientTemplates,
    IReadOnlyList<DeviceRecipient> Recipients,
    IReadOnlyList<DeviceSchedule> Schedules,
    IReadOnlyDictionary<string, string> TopicNames)
{
    /// <summary>The device's event list as read (<c>GetEventInstances</c>), null when it could not be read.</summary>
    public System.Xml.Linq.XDocument? Events { get; init; }

    public ActionTemplate? Template(string token) => ActionTemplates.FirstOrDefault(t => t.Token == token);

    /// <summary>The parameter names of the recipient part of <paramref name="token"/> (empty without a recipient).</summary>
    public IReadOnlySet<string> RecipientParameters(string token) =>
        Template(token)?.RecipientTemplate is { } recipient && RecipientTemplates.FirstOrDefault(r => r.Token == recipient) is { } r
            ? r.Parameters.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>A device answer OADM cannot use, or a refusal; the message is for the user.</summary>
public sealed class ActionEngineException : Exception
{
    public ActionEngineException()
    {
    }

    public ActionEngineException(string message)
        : base(message)
    {
    }

    public ActionEngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
