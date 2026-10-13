using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Actions;

namespace Oadm.Samples.VirtualInput;

/// <summary>
/// An event rule action: turns a virtual input of the target devices on when the rule turns on (or fires), and off again
/// when it turns off. The devices' own event rules can react to the input. The rule dialog draws the form from
/// <see cref="Fields"/>; no client part is needed.
/// </summary>
public sealed class VirtualInputAction : IActionPlugin
{
    public const string ActionId = "acme.virtual-input";

    public string Id => ActionId;
    public string DisplayName => "Set virtual input";
    public string? IconKey => "toggle";
    public string Group => "Devices";
    public string? Description => "Turns a virtual input of the devices on while the rule is on.";

    public bool NeedsDevice => true;       // runs once per target device, with ctx.Vapix logged in to it
    public bool SupportsTurnOff => true;   // also runs when the rule turns off
    public bool UsesPlaceholders => true;  // the rule dialog lists the placeholders for the log text

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Number("port", "Virtual input", min: 1, max: 64, defaultValue: 64),
        ActionField.Text("note", "Log text", defaultValue: "{rule} on {address}") with { Hint = "Written to the event log" },
    ];

    /// <summary>The short text of the rules list.</summary>
    public string Describe(JsonElement settings) =>
        "Virtual input " + (settings.TryGetProperty("port", out var port) ? port.ToString() : "64");

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        var port = invocation.GetInt("port", 64);
        var action = invocation.Edge == ActionEdge.TurnedOff ? "deactivate" : "activate";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"/axis-cgi/virtualinput/{action}.cgi?schemaversion=1&port={port}"));
        using var response = await ctx.Vapix!.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // A failed run: the message goes into the event log.
            throw new InvalidOperationException($"The device refused virtual input {port}: HTTP {(int)response.StatusCode}.");
        }

        ctx.Log(ctx.Text(invocation.Get("note")) + (action == "activate" ? ": on" : ": off"));
    }
}
