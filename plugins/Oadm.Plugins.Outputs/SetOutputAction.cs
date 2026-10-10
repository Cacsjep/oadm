using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Outputs;

/// <summary>
/// Event rule action "Set output": switches an output port of a device (a relay of a camera or an I/O module). Modes
/// (user decision 2026-10-10): Follow the rule (active while a stateful rule is on), Pulse for N seconds, Set active,
/// Set inactive, Toggle. Uses the I/O port management API when the device lists it, else port.cgi.
/// </summary>
public sealed class SetOutputAction : IActionPlugin
{
    public const string ActionId = "oadm.outputs.set";

    public const string Follow = "follow";
    public const string Pulse = "pulse";
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Toggle = "toggle";

    public string Id => ActionId;

    public string DisplayName => "Set output";

    public string? IconKey => "activity";

    public string Group => "Devices";

    public string? Description => "Switches an output of the device, for example a relay.";

    public bool NeedsDevice => true;

    public bool SupportsTurnOff => true;

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Number("port", "Output", 1, 64, 1) with { Hint = "The output number as in the device's I/O settings (1 = the first)." },
        ActionField.Choice("mode", "Mode",
        [
            new(Follow, "Follow the rule"),
            new(Pulse, "Pulse"),
            new(Active, "Set active"),
            new(Inactive, "Set inactive"),
            new(Toggle, "Toggle"),
        ], Pulse),
        ActionField.Number("seconds", "Seconds", 1, 3600, 5) with { VisibleWhenKey = "mode", VisibleWhenValue = Pulse },
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var mode = Text(settings, "mode");
        return mode == Follow && !statefulRule
            ? new Dictionary<string, string> { ["mode"] = "Follow the rule needs a rule that turns on and off." }
            : new Dictionary<string, string>();
    }

    public string Describe(JsonElement settings)
    {
        var port = Number(settings, "port", 1);
        return Text(settings, "mode") switch
        {
            Follow => string.Create(CultureInfo.InvariantCulture, $"Output {port} follows the rule"),
            Pulse => string.Create(CultureInfo.InvariantCulture, $"Pulse output {port} for {Number(settings, "seconds", 5)} s"),
            Active => string.Create(CultureInfo.InvariantCulture, $"Set output {port} active"),
            Inactive => string.Create(CultureInfo.InvariantCulture, $"Set output {port} inactive"),
            Toggle => string.Create(CultureInfo.InvariantCulture, $"Toggle output {port}"),
            _ => DisplayName,
        };
    }

    /// <summary>port.cgi answers a pulse only after it ended: allow its length plus 15 s.</summary>
    public TimeSpan? MaxRunTime(JsonElement settings) =>
        Text(settings, "mode") == Pulse ? TimeSpan.FromSeconds(Number(settings, "seconds", 5) + 15) : null;

    public bool RunsOn(ActionEdge edge, JsonElement settings) => edge != ActionEdge.TurnedOff || Text(settings, "mode") == Follow;

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var vapix = ctx.Vapix ?? throw new InvalidOperationException("No device.");
        var port = invocation.GetInt("port", 1);
        var mode = invocation.Get("mode") ?? Pulse;
        var seconds = invocation.GetInt("seconds", 5);
        var device = invocation.Device;
        IOutputPorts ports = device is not null && device.Apis.Supports(PortManagementApi.ApiId, "1.0")
            ? new PortManagementApi(vapix)
            : new PortCgi(vapix);

        var target = (mode, invocation.Edge) switch
        {
            (Follow, ActionEdge.TurnedOff) => OutputCommand.Inactive,
            (Follow, _) => OutputCommand.Active,
            (Pulse, _) => OutputCommand.Pulse,
            (Active, _) => OutputCommand.Active,
            (Inactive, _) => OutputCommand.Inactive,
            (Toggle, _) => OutputCommand.Toggle,
            _ => throw new InvalidOperationException($"Unknown mode '{mode}'."),
        };
        var result = await ports.SetAsync(port, target, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        ctx.Log(result);
    }

    private static string? Text(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Number(JsonElement settings, string key, int fallback) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.TryGetInt32(out var n) ? n : fallback;
}
