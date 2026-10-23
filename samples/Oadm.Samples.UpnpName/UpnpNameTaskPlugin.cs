using System.Text;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Samples.UpnpName;

public sealed class UpnpNameTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    private const string StepCheck = "Check compatibility";
    private const string StepRead = "Read the current name";
    private const string StepSet = "Set the name";
    private const string StepVerify = "Verify the name";

    public string Id => UpnpName.PluginId;
    public string DisplayName => "Set UPnP name";
    public string? IconKey => "edit";
    public string Group => TaskGroups.Network;
    public bool ShowInToolbar => false;
    public bool RequiresDialog => true;

    // Cached data only: the status and the API list of the last full refresh.
    public bool CanRun(IDeviceInfo device) =>
        device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && device.Apis.Supports("param-cgi", "1.0");

    public string? NotSupportedReason(IDeviceInfo device) =>
        TaskSupportReasons.ForStatus(device) ?? TaskSupportReasons.NeedsApi("the parameter API", device);

    public string GetTaskName(string? payloadJson)
    {
        var payload = Read(payloadJson);
        return payload is null ? DisplayName : "Set UPnP name " + payload.Name.Trim();
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        // Validate everything before the first request.
        var name = Read(payloadJson)?.Name.Trim();
        if (UpnpName.Problem(name) is { } problem)
        {
            throw new ArgumentException(problem + " Nothing was changed.");
        }

        ctx.PlanSteps(StepCheck, StepRead, StepSet, StepVerify);

        await ctx.StepAsync(StepCheck, async _ =>
            (await ctx.Vapix.GetApiListAsync(ct)).Require("param-cgi", "1.0"));

        var current = await ctx.StepAsync(StepRead, async step =>
        {
            var value = await ReadNameAsync(ctx.Vapix, ct);
            step.Complete(value.Length == 0 ? "No name" : value);
            return value;
        });

        if (current == name)
        {
            ctx.SkipStep(StepSet, "The device already has this name");
            ctx.SkipStep(StepVerify, "Nothing was changed");
            return;
        }

        await ctx.StepAsync(StepSet, _ => WriteNameAsync(ctx.Vapix, name!, ct));

        using var verify = ctx.BeginStep(StepVerify);
        var after = await ReadNameAsync(ctx.Vapix, ct);
        if (after == name)
        {
            verify.Complete(after);
        }
        else
        {
            verify.Warn($"The device reports \"{after}\" after the change.");
        }
    }

    // Read-only: the dialog shows the current name of the first selected device. OADM checks that the user sees the
    // device and logs ctx.Vapix in to it; any other device would be read through caller.
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        method switch
        {
            "current" => JsonSerializer.Serialize(new UpnpNamePayload(await ReadNameAsync(ctx.Vapix, ct)), UpnpName.Json),
            _ => throw new ArgumentException("Unknown query " + method),
        };

    // OADM calls the query above, with the user's call.
    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("The query needs the user's call.");

    private static async Task<string> ReadNameAsync(IVapixClient vapix, CancellationToken ct)
    {
        var values = await vapix.ListParametersAsync([UpnpName.Parameter], ct);
        return values.TryGetValue(UpnpName.Parameter, out var value) ? value : "";
    }

    private static async Task WriteNameAsync(IVapixClient vapix, string name, CancellationToken ct)
    {
        // Values go in the POST body, never in the URL.
        var body = "action=update&" + Uri.EscapeDataString(UpnpName.Parameter) + "=" + Uri.EscapeDataString(name);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/axis-cgi/param.cgi")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        using var response = await vapix.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode || !text.TrimStart().StartsWith("OK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The device refused the name: " + text.Trim());
        }
    }

    private static UpnpNamePayload? Read(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<UpnpNamePayload>(json, UpnpName.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
