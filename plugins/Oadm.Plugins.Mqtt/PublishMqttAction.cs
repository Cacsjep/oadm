using System.Text;
using System.Text.Json;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Mqtt;

/// <summary>The recipient type of MQTT: a broker, one connection the rules share.</summary>
public static class MqttRecipient
{
    public const string TypeId = "oadm.mqtt.recipient";

    public const int DefaultPort = 1883;

    public const string Version311 = "3.1.1";

    public const string Version500 = "5.0";

    /// <summary>
    /// The client ID of a recipient without one: one per recipient, so two recipients on one broker never take each other's place, and a Test (no id) never takes the open
    /// session's. 23 characters at most: the longest id every broker must accept.
    /// </summary>
    private const int LongestSafeClientId = 23;

    public static string DefaultClientId(Guid? recipientId) => "oadm-" + (recipientId ?? Guid.NewGuid()).ToString("N")[..18];

    /// <summary>
    /// The client ID of a connection. A Test (no recipient id) gets a unique ending on a chosen ID too, within the 23 characters: with the
    /// same ID it would throw the open session off the broker.
    /// </summary>
    public static string ClientId(string? chosen, Guid? recipientId)
    {
        var id = chosen?.Trim();
        return string.IsNullOrEmpty(id) ? DefaultClientId(recipientId)
            : recipientId is null ? id[..Math.Min(id.Length, LongestSafeClientId - 9)] + "-" + Guid.NewGuid().ToString("N")[..8]
            : id;
    }

    public static RecipientType Type { get; } = new(
        TypeId,
        "MQTT",
        "send",
        [
            ActionField.Text("host", "Host", required: true) with { Hint = "Host name or IP address", EmptyText = ActionFormats.EnterHost, Format = ActionFieldFormat.Host },
            ActionField.Number("port", "Port", 1, 65535, DefaultPort),
            ActionField.Choice("protocolVersion", "Protocol version", [new(Version311, Version311), new(Version500, Version500)], Version500),
            ActionField.Bool("tls", "Use TLS"),
            ActionField.Bool("allowUntrusted", "Allow untrusted certificates") with { VisibleWhenKey = "tls", VisibleWhenValue = "true" },
            ActionField.TrustedCertificate("trustedCa", "Trusted CA") with { VisibleWhenKey = "tls", VisibleWhenValue = "true", Hint = "Optional" },
            ActionField.ClientCertificate("clientCertificate", "Client certificate") with { VisibleWhenKey = "tls", VisibleWhenValue = "true", Hint = "Optional" },
            ActionField.Text("user", "User name") with { Hint = "Optional" },
            ActionField.Password("password", "Password"),
            ActionField.Text("clientId", "Client ID") with { Hint = "Optional" },
        ],
        PublishMqttAction.ActionId)
    {
        Target = "{host}:{port}",
        Sessions = new MqttSessionFactory(),
    };
}

/// <summary>
/// Event rule action "Publish MQTT message": one message to a broker, with an optional payload when the rule turns off. The topic is
/// filled per placeholder and checked before anything is sent: a value must not add a level, a wildcard or a system topic. Output
/// only, and a failed message is not sent later.
/// </summary>
public sealed class PublishMqttAction : IActionPlugin
{
    public const string ActionId = "oadm.mqtt.publish";

    public const string NothingSent = "Nothing was sent.";

    private const string RecipientKey = "recipient";
    private const string Json = "json";

    /// <summary>The longest topic of the protocol, in bytes.</summary>
    private const int MaxTopicBytes = 65535;

    public string Id => ActionId;

    public string DisplayName => "Publish MQTT message";

    public string? IconKey => "send";

    public string Group => "Machines";

    public string? Description => "Publishes a message to an MQTT broker.";

    public bool NeedsDevice => false;

    public bool SupportsTurnOff => true;

    public bool UsesPlaceholders => true;

    public IReadOnlyList<RecipientType> RecipientTypes { get; } = [MqttRecipient.Type];

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Recipient(RecipientKey, "Recipient", [MqttRecipient.TypeId]),
        ActionField.Text("topic", "Topic", required: true) with { Hint = "No + or #" },
        ActionField.Choice("format", "Format", [new("text", "Text"), new(Json, "JSON")], "text"),
        ActionField.Code("payload", "Payload", languageKey: "format"),
        ActionField.Choice("qos", "QoS", [new("0", "0"), new("1", "1"), new("2", "2")], "1"),
        ActionField.Bool("retain", "Retain"),
        ActionField.Code("offPayload", "Payload", languageKey: "format") with { OffEdge = true, Hint = "Optional" },
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var topic = Text(settings, "topic");
        if (topic.Length > 0 && TopicProblem(topic) is { } problem)
        {
            errors["topic"] = problem;
        }

        return errors;
    }

    public string Describe(JsonElement settings) =>
        Text(settings, "topic") is { Length: > 0 } topic ? "Publish to " + topic : DisplayName;

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || Text(settings, "offPayload").Length > 0;

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var topic = FilledTopic(ctx, invocation.Get("topic"));
        var message = new MqttMessage(topic, FilledPayload(ctx, invocation), invocation.GetInt("qos", 1), invocation.GetBool("retain"));
        var session = await ctx.SessionAsync<IMqttPublisher>(RecipientKey, ct).ConfigureAwait(false);
        await session.PublishAsync(message, ct).ConfigureAwait(false);
        ctx.Log("Published to " + topic);
    }

    /// <summary>The topic with each placeholder's value checked on its own; the text around them is the user's.</summary>
    private static string FilledTopic(IActionContext ctx, string? template)
    {
        RefuseUnknown(ctx, template);
        string? offending = null;
        var topic = ActionPlaceholders.Fill(template, name =>
        {
            var value = ctx.Text("{" + name + "}");
            if (!FitsInTopic(value))
            {
                offending ??= name;
            }

            return value;
        });
        if (TopicProblem(topic) is { } problem)
        {
            throw new InvalidOperationException($"{problem} {NothingSent}");
        }

        return offending is null ? topic : throw new InvalidOperationException($"The value of {{{offending}}} cannot go in a topic. {NothingSent}");
    }

    private static string FilledPayload(IActionContext ctx, ActionInvocation invocation)
    {
        var template = invocation.Get(invocation.Edge == ActionEdge.TurnedOff ? "offPayload" : "payload");
        RefuseUnknown(ctx, template);
        var payload = ctx.Text(template, invocation.Get("format") == Json ? PlaceholderEscape.Json : PlaceholderEscape.None);
        return payload.Length > 0 || string.IsNullOrEmpty(template) ? payload : throw new InvalidOperationException($"The payload is empty. {NothingSent}");
    }

    private static void RefuseUnknown(IActionContext ctx, string? text)
    {
        if (ActionPlaceholders.FirstUnknown(text, ctx) is { } unknown)
        {
            throw new InvalidOperationException($"The placeholder {unknown} is unknown. {NothingSent}");
        }
    }

    /// <summary>A value stays inside one topic level: no separator, no wildcard, no system topic, and it holds a level at all.</summary>
    private static bool FitsInTopic(string value) =>
        value.Length > 0 && value[0] != '$' && value.AsSpan().IndexOfAny("/+#\0") < 0;

    /// <summary>Why a broker would never take this topic; null when it can.</summary>
    private static string? TopicProblem(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return "The topic is empty.";
        }

        if (topic.AsSpan().IndexOfAny('+', '#') >= 0)
        {
            return "A topic cannot hold + or #.";
        }

        if (topic.Contains('\0', StringComparison.Ordinal))
        {
            return "A topic cannot hold a null character.";
        }

        return Encoding.UTF8.GetByteCount(topic) > MaxTopicBytes ? "The topic is too long." : null;
    }

    private static string Text(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
