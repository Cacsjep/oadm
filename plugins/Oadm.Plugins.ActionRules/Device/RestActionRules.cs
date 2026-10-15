using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.ActionRules.Device;

/// <summary>
/// The REST API <c>action-rules</c> v2 (Device Configuration API, verified on AXIS OS 12.11): a rule and its action are one
/// object, and <c>PATCH rules/&lt;id&gt;</c> changes a rule in place (the rule id stays). Used only to write: reads show
/// passwords as <c>**Redacted**</c>, and sending that text back would store it as the password. So OADM reads over SOAP
/// (<see cref="SoapActionEngine"/>) and sends the action only when it changes, with the real passwords.
/// </summary>
public static class RestActionRules
{
    public const string DiscoverPath = "/config/discover";
    public const string RulesPath = "/config/rest/action-rules/v2/rules";
    public const string RecipientsPath = "/config/rest/action-rules/v2/recipients";
    public const string SchedulesPath = "/config/rest/event-schedules/v2/schedules";

    /// <summary>The value the device shows instead of a password. Never written back.</summary>
    public const string Redacted = "**Redacted**";

    /// <summary>Whether the device lists <c>action-rules</c> v2 (AXIS OS 12; older ones answer 404 or do not list it).</summary>
    public static async Task<bool> IsAvailableAsync(IVapixClient vapix, CancellationToken ct) =>
        (await ReadCapabilitiesAsync(vapix, ct).ConfigureAwait(false)).Rules;

    /// <summary>
    /// Which REST APIs the device lists in <c>/config/discover</c>: <c>action-rules</c> v2 (rules and recipients in place)
    /// and <c>event-schedules</c> v2 (schedules in place). AXIS OS 10.12 has no discover (404), AXIS OS 11.9 lists neither.
    /// </summary>
    public static async Task<RestCapabilities> ReadCapabilitiesAsync(IVapixClient vapix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var request = new HttpRequestMessage(HttpMethod.Get, DiscoverPath);
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new RestCapabilities(false, false);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ActionEngineException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? $"HTTP {(int)response.StatusCode}");
        }

        try
        {
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var apis = (root?["data"] ?? root)?["apis"];
            return new RestCapabilities(apis?["action-rules"]?["v2"] is not null, apis?["event-schedules"]?["v2"] is not null);
        }
        catch (JsonException)
        {
            return new RestCapabilities(false, false);
        }
    }

    /// <summary>Changes a recipient in place (its id stays). The parameters carry the real passwords.</summary>
    public static Task PatchRecipientAsync(IVapixClient vapix, DeviceRecipient recipient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (recipient.Parameters.Any(p => p.Value == Redacted))
        {
            throw new ActionEngineException("A password of the recipient is missing. Nothing was changed.");
        }

        var data = new JsonObject
        {
            ["name"] = recipient.Name,
            ["template"] = recipient.Template,
            ["parameters"] = Pairs(recipient.Parameters),
        };
        return SendAsync(vapix, new HttpMethod("PATCH"), RecipientsPath + "/" + Uri.EscapeDataString(recipient.Id), new JsonObject { ["data"] = data }, ct);
    }

    /// <summary>Changes a schedule in place (its id stays, so the rules that use it keep working).</summary>
    public static Task PatchScheduleAsync(IVapixClient vapix, DeviceSchedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var data = new JsonObject { ["name"] = schedule.Name, ["schedule"] = schedule.Definition };
        return SendAsync(vapix, new HttpMethod("PATCH"), SchedulesPath + "/" + Uri.EscapeDataString(schedule.Id), new JsonObject { ["data"] = data }, ct);
    }

    /// <summary>
    /// Changes the rule in place. Only the given parts are sent; <paramref name="action"/> null leaves the action (and its
    /// passwords) untouched on the device.
    /// </summary>
    public static Task PatchRuleAsync(IVapixClient vapix, string ruleId, DeviceRule rule, bool name, bool enabled, bool trigger, DeviceRuleSet? action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var data = new JsonObject();
        if (name)
        {
            data["name"] = rule.Name;
        }

        if (enabled)
        {
            data["enabled"] = rule.Enabled;
        }

        if (trigger)
        {
            data["activationConfig"] = ActivationConfig(rule);
        }

        if (action is not null)
        {
            data["actionConfig"] = ActionConfig(rule.Action, action);
        }

        return data.Count == 0
            ? Task.CompletedTask
            : SendAsync(vapix, new HttpMethod("PATCH"), RulesPath + "/" + Uri.EscapeDataString(ruleId), new JsonObject { ["data"] = data }, ct);
    }

    /// <summary>Creates the rule with its action; returns the new rule id.</summary>
    public static async Task<string> AddRuleAsync(IVapixClient vapix, DeviceRule rule, DeviceRuleSet templates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var data = new JsonObject
        {
            ["name"] = rule.Name,
            ["enabled"] = rule.Enabled,
            ["activationConfig"] = ActivationConfig(rule),
            ["actionConfig"] = ActionConfig(rule.Action, templates),
        };
        var answer = await SendAsync(vapix, HttpMethod.Post, RulesPath, new JsonObject { ["data"] = data }, ct).ConfigureAwait(false);
        return answer?["data"]?["id"]?.GetValue<string>() is { Length: > 0 } id
            ? id
            : throw new ActionEngineException("The device did not return the new action rule.");
    }

    /// <summary>Removes the rule and its action.</summary>
    public static Task DeleteRuleAsync(IVapixClient vapix, string ruleId, CancellationToken ct) =>
        SendAsync(vapix, HttpMethod.Delete, RulesPath + "/" + Uri.EscapeDataString(ruleId), null, ct);

    internal static JsonObject ActivationConfig(DeviceRule rule) => new()
    {
        ["startEvent"] = rule.StartEvent is { } start ? Filter(start) : null,
        ["condition"] = rule.Conditions.Count == 0 ? null : new JsonArray([.. rule.Conditions.Select(c => (JsonNode)Filter(c))]),
        ["timeout"] = rule.Timeout,
    };

    /// <summary>The action split as the API wants it: the recipient template's parameters apart from the action's own.</summary>
    internal static JsonObject ActionConfig(RuleAction action, DeviceRuleSet templates)
    {
        var recipient = templates.RecipientParameters(action.Template);
        if (action.Parameters.Any(p => p.Value == Redacted))
        {
            throw new ActionEngineException("A password of the action is missing. Nothing was changed.");
        }

        var hasRecipient = templates.Template(action.Template)?.RecipientTemplate is not null;
        return new JsonObject
        {
            ["template"] = action.Template,
            ["actionParameters"] = Pairs(action.Parameters.Where(p => !recipient.Contains(p.Name))),
            ["recipientParameters"] = hasRecipient ? Pairs(action.Parameters.Where(p => recipient.Contains(p.Name))) : null,
            ["recipientId"] = null,
        };
    }

    private static JsonArray Pairs(IEnumerable<NameValue> parameters) =>
        new([.. parameters.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["value"] = p.Value })]);

    private static JsonObject Filter(EventFilter filter) => new()
    {
        ["topicExpression"] = Rules.RuleText.Topic(filter.Topic), // never the "//." AXIS OS 10 appends
        ["messageContent"] = filter.Message,
    };

    /// <summary>One request; an error answer becomes <see cref="ActionEngineException"/> with the device's message.</summary>
    private static async Task<JsonNode?> SendAsync(IVapixClient vapix, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode? answer = null;
        try
        {
            answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // handled below by the status code
        }

        if (answer?["status"]?.GetValue<string>() == "error" || answer?["error"] is not null)
        {
            var message = answer?["error"]?["message"]?.GetValue<string>();
            throw new ActionEngineException(string.IsNullOrWhiteSpace(message) ? "The device refused the request." : "The device refused: " + message);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ActionEngineException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? $"HTTP {(int)response.StatusCode}");
        }

        return answer;
    }
}

/// <summary>The REST APIs a device offers for changing in place.</summary>
public sealed record RestCapabilities(bool Rules, bool Schedules);
