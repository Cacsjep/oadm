using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// Event rule action "Run VAPIX command": a saved command (with its stored values and
/// passwords) or a library command that needs no input, when the rule turns on or fires, and optionally a second one
/// when it turns off. Device safety like a rollout: the command must fit the device's cached API list, a command that
/// changes the device re-checks a fresh API list first, and commands marked dangerous need "Allow dangerous commands".
/// Contributed by the VAPIX Commander, so it is available while that plugin is on.
/// </summary>
public sealed class RunCommandAction(VapixCommanderPlugin owner) : IActionPlugin
{
    public const string ActionId = "oadm.vapix-commander.command";
    public const string SavedPrefix = "saved:";
    public const string LibraryPrefix = "library:";

    public string Id => ActionId;

    public string DisplayName => "Run VAPIX command";

    public string? IconKey => "command";

    public string Group => "Devices";

    public string? Description => "Sends a saved VAPIX Commander command to the device.";

    public bool NeedsDevice => true;

    public bool SupportsTurnOff => true;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.DynamicChoice("command", "Command") with { Hint = "Saved commands keep their values and passwords." },
        new ActionField("offCommand", "When the rule turns off", ActionFieldType.DynamicChoice) { OffEdge = true, Hint = "Optional" },
        ActionField.Bool("allowDangerous", "Allow dangerous commands"),
    ];

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || !string.IsNullOrEmpty(Text(settings, "offCommand"));

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var errors = new Dictionary<string, string>();
        var allowDangerous = string.Equals(Text(settings, "allowDangerous"), "true", StringComparison.OrdinalIgnoreCase)
            || settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("allowDangerous", out var flag) && flag.ValueKind == JsonValueKind.True;
        foreach (var key in new[] { "command", "offCommand" })
        {
            var reference = Text(settings, key);
            if (string.IsNullOrEmpty(reference))
            {
                continue;
            }

            var command = owner.FindForAction(reference);
            if (command is null)
            {
                errors[key] = "The command no longer exists.";
            }
            else if (command.Dangerous && !allowDangerous)
            {
                errors[key] = "This command is marked dangerous. Turn on \"Allow dangerous commands\" to use it.";
            }
        }

        return errors;
    }

    public string Describe(JsonElement settings)
    {
        var on = owner.FindForAction(Text(settings, "command") ?? string.Empty)?.Name ?? "?";
        var off = Text(settings, "offCommand") is { Length: > 0 } offRef ? owner.FindForAction(offRef)?.Name ?? "?" : null;
        return off is null ? $"Run \"{on}\"" : $"Run \"{on}\", when off \"{off}\"";
    }

    public Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IActionQueryContext ctx, string fieldKey, CancellationToken ct) =>
        Task.FromResult(owner.ActionChoices());

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var reference = invocation.Edge == ActionEdge.TurnedOff ? invocation.Get("offCommand") : invocation.Get("command");
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        var device = invocation.Device ?? throw new InvalidOperationException("No device.");
        var vapix = ctx.Vapix ?? throw new InvalidOperationException("No device.");
        var (command, values) = await owner.ResolveForActionAsync(reference, ct).ConfigureAwait(false);
        FillEventValues(ctx, command, values);
        if (command.Dangerous && !invocation.GetBool("allowDangerous"))
        {
            throw new InvalidOperationException($"{command.Name} is marked dangerous and \"Allow dangerous commands\" is off. Nothing was sent.");
        }

        var cached = Compatibility.Check(command, device.Apis, device.HasVideo);
        if (cached.State is CompatibilityState.MissingApi or CompatibilityState.VersionTooOld or CompatibilityState.NoVideo)
        {
            throw new InvalidOperationException($"{cached.Text}. Nothing was sent.");
        }

        var rendered = CommandRenderer.Render(command, values);
        if (command.Writes)
        {
            // Device safety: a fresh API list before the first write.
            Compatibility.Require(command, await vapix.GetApiListAsync(ct).ConfigureAwait(false));
        }

        var outcome = await CommandExecutor.ExecuteAsync(vapix, command, rendered, ct).ConfigureAwait(false);
        ctx.Log(command.Name);
        if (!outcome.Success)
        {
            throw new InvalidOperationException($"{command.Name}: {outcome.Summary}");
        }

        if (!string.IsNullOrWhiteSpace(outcome.Summary))
        {
            ctx.Log(outcome.Summary);
        }
    }

    /// <summary>
    /// The event's values in the command: a text field of the command whose value holds
    /// placeholders ("{carID}", "{port}", "{device}") gets them filled from the rule and the event before the command is
    /// checked and sent; the field checks (pattern, length) then apply to the filled value. Passwords are never touched.
    /// </summary>
    internal static void FillEventValues(IActionContext ctx, CommandDefinition command, Dictionary<string, JsonElement> values)
    {
        foreach (var field in command.Fields.Where(f => f.Type == FieldTypes.String))
        {
            var text = values.TryGetValue(field.Name, out var entered) && entered.ValueKind == JsonValueKind.String ? entered.GetString()
                : field.Default is { ValueKind: JsonValueKind.String } stored ? stored.GetString()
                : null;
            if (text is not null && text.Contains('{', StringComparison.Ordinal))
            {
                values[field.Name] = JsonSerializer.SerializeToElement(ctx.Text(text));
            }
        }
    }

    private static string? Text(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
