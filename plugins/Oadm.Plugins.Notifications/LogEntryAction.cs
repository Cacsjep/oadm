using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications;

/// <summary>
/// Event rule action "Log entry": writes one line to the server log and nothing else, for testing a rule or for a rule
/// that only needs a record. Runs once per rule edge, no device.
/// </summary>
public sealed partial class LogEntryAction : IActionPlugin
{
    public const string ActionId = "oadm.notifications.log";

    /// <summary>Most characters of the filled text kept in the server log (event values can be long).</summary>
    private const int MaxLength = 2000;

    public string Id => ActionId;

    public string DisplayName => "Log entry";

    public string? IconKey => "log";

    public string Group => "Notifications";

    public string? Description => "Writes a line to the server log.";

    public bool NeedsDevice => false;

    public bool SupportsTurnOff => true;

    public bool UsesPlaceholders => true;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Text("message", "Message", required: true, defaultValue: "{event} on {device} at {time}"),
        ActionField.Bool("onOff", "Also write when the rule turns off") with { OffEdge = true },
    ];

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("onOff", out var on) && on.ValueKind == JsonValueKind.True);

    public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        // One line whatever the event values hold: a value can never fake a second log line.
        var text = ctx.Text(invocation.Get("message")).ReplaceLineEndings(" ");
        LogMessage(ctx.Logger, invocation.RuleName, text.Length > MaxLength ? text[..MaxLength] : text);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event rule {Rule}: {Text}")]
    private static partial void LogMessage(ILogger logger, string rule, string text);
}
