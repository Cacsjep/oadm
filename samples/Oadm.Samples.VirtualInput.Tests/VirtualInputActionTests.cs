using System.Net;
using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

namespace Oadm.Samples.VirtualInput.Tests;

public sealed class VirtualInputActionTests
{
    private readonly VirtualInputAction _action = new();

    [Fact]
    public async Task Turning_on_activates_the_input_and_turning_off_deactivates_it()
    {
        var camera = new FakeCamera();
        var ctx = new FakeActionContext(camera) { Placeholders = { ["rule"] = "Door open" } };

        await _action.ExecuteAsync(ctx, Run(ActionEdge.TurnedOn, """{"port":12,"note":"{rule}"}"""), CancellationToken.None);
        await _action.ExecuteAsync(ctx, Run(ActionEdge.TurnedOff, """{"port":12,"note":"{rule}"}"""), CancellationToken.None);

        Assert.Equal(
            ["GET /axis-cgi/virtualinput/activate.cgi?schemaversion=1&port=12", "GET /axis-cgi/virtualinput/deactivate.cgi?schemaversion=1&port=12"],
            camera.Requests);
        Assert.Equal(["Door open: on", "Door open: off"], ctx.Lines);
    }

    [Fact]
    public async Task A_refused_request_fails_the_run()
    {
        var ctx = new FakeActionContext(new FakeCamera { Status = HttpStatusCode.Forbidden });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _action.ExecuteAsync(ctx, Run(ActionEdge.Fired, """{"port":64}"""), CancellationToken.None));

        Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rules_list_names_the_input()
    {
        using var settings = JsonDocument.Parse("""{"port":7}""");
        Assert.Equal("Virtual input 7", _action.Describe(settings.RootElement));
    }

    private static ActionInvocation Run(ActionEdge edge, string settingsJson) => new(
        Guid.NewGuid(),
        "Door open",
        edge,
        JsonDocument.Parse(settingsJson).RootElement,
        null,
        new ActionTrigger("input", "Input 1 active", null, null, null, edge != ActionEdge.TurnedOff, DateTimeOffset.UtcNow, new Dictionary<string, string>()));
}
