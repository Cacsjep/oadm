using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Server.Assistant;
using Oadm.Server.Auth;
using Oadm.Server.Licensing;
using Oadm.Server.Plugins;
using Oadm.Server.Vulnerabilities;

using static Oadm.Server.Tools.ToolSchema;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>
/// <c>oadm_server_status</c> (docs/spec/mcp.md "Other areas", read only): what OADM's Plugins page, the Settings card License and
/// the page MCP &amp; Assistant show every signed-in user: OADM PRO, the plugins and whether they are on, MCP and the assistant on
/// or off, the date of the list of Axis CVEs. Only the reads of these services (every one signed in), never a change; no server
/// ID, no license key, no customer, no API key, no certificate.
/// </summary>
internal static class ServerStatusTool
{
    public static readonly string LicensePath = InProcessCalls.PathOf(Proto.LicenseService.Descriptor, "GetLicense");
    public static readonly string PackagesPath = InProcessCalls.PathOf(Proto.PluginService.Descriptor, "ListPackages");
    public static readonly string McpPath = InProcessCalls.PathOf(Proto.McpService.Descriptor, "Get");
    public static readonly string AssistantPath = InProcessCalls.PathOf(Proto.AssistantService.Descriptor, "GetSettings");
    public static readonly string VulnerabilitiesPath = InProcessCalls.PathOf(Proto.VulnerabilityService.Descriptor, "Get");

    /// <summary>The tool; <paramref name="mcpState"/> reads <c>McpService.Get</c> as the caller (the host passes it: the tools name no MCP type).</summary>
    public static OadmTool Create(Func<ToolCall, Task<Proto.McpState>> mcpState) => new(
        "oadm_server_status",
        "Server status",
        "OADM's own state: OADM PRO, which plugins are on, MCP and the assistant, and the date of the list of Axis vulnerabilities. Read only.",
        Object(null, [P("plugins", Choice("Which plugins to list (default on).", ["on", "all", "none"]))]),
        OutObject(null,
        [
            P("license", OutObject("OADM PRO.",
            [
                P("status", Out("string", "None, Active, Expired, WrongServer or Invalid.")),
                P("text", Out("string", "What it means, as the OADM PRO row of the Settings page says it.")),
                P("plan", Out("string", "The plan; empty without one.")),
                P("max_devices", OrNull(Out("integer", "Devices the plan covers; null: no limit."))),
                P("expires", Out("string", "When it ends (yyyy-MM-dd); empty: never or no license.")),
                P("trial", Out("boolean", "A trial.")),
            ], "status", "text", "plan", "max_devices", "expires", "trial")),
            P("plugins_on", Out("integer", "Plugins that are on.")),
            P("plugins_off", Out("integer", "Plugins that are off.")),
            P("plugins", OutList("The plugins asked for, by name.", OutObject(null,
            [
                P("id", Out("string", "The plugin id.")),
                P("name", Out("string", "Its name.")),
                P("version", Out("string", "Its version.")),
                P("on", Out("boolean", "It is on.")),
                P("group", Out("string", "Its group on the rail.")),
                P("pro", Out("boolean", "It needs OADM PRO.")),
                P("license", Out("string", "Its license now; empty for a free plugin.")),
            ], "id", "name", "version", "on", "group", "pro", "license"))),
            P("mcp", OutObject("The MCP server.",
            [
                P("on", Out("boolean", "Turned on.")),
                P("running", Out("boolean", "It takes connections now.")),
                P("status", Out("string", "Its state in one line.")),
            ], "on", "running", "status")),
            P("assistant", OutObject("The assistant.",
            [
                P("on", Out("boolean", "Turned on.")),
                P("provider", Out("string", "The AI provider; empty when off.")),
                P("send_pictures", Out("boolean", "Camera pictures may go to the provider.")),
            ], "on", "provider", "send_pictures")),
            P("vulnerability_list", OutObject("The list of Axis vulnerabilities OADM matches.",
            [
                P("date", OrNull(Out("string", "The list's date (UTC)."))),
                P("count", Out("integer", "Vulnerabilities in it.")),
                P("source", Out("string", "release (shipped with OADM), server or client (updated online).")),
                P("online_update", Out("string", "Who updates it online: off, server or clients.")),
            ], "date", "count", "source", "online_update")),
        ], "license", "plugins_on", "plugins_off", "plugins", "mcp", "assistant", "vulnerability_list"),
        [LicensePath, PackagesPath, McpPath, AssistantPath, VulnerabilitiesPath],
        call => RunAsync(call, mcpState));

    private static async Task<JsonObject> RunAsync(ToolCall call, Func<ToolCall, Task<Proto.McpState>> mcpState)
    {
        var license = await call.CallAsync<LicenseGrpcService, Proto.Empty, Proto.ProLicenseState>(LicensePath, new Proto.Empty(), (s, r, c) => s.GetLicense(r, c)).ConfigureAwait(false);
        var packages = await call.CallAsync<PluginGrpcService, Proto.Empty, Proto.PluginPackageList>(PackagesPath, new Proto.Empty(), (s, r, c) => s.ListPackages(r, c)).ConfigureAwait(false);
        var mcp = await mcpState(call).ConfigureAwait(false);
        var assistant = await call.CallAsync<AssistantGrpcService, Proto.Empty, Proto.AssistantSettingsState>(AssistantPath, new Proto.Empty(), (s, r, c) => s.GetSettings(r, c)).ConfigureAwait(false);
        var cves = await call.CallAsync<VulnerabilityGrpcService, Proto.Empty, Proto.VulnerabilityListReply>(VulnerabilitiesPath, new Proto.Empty(), (s, r, c) => s.Get(r, c)).ConfigureAwait(false);

        var which = call.Text("plugins") ?? "on";
        var plugins = new JsonArray();
        foreach (var package in packages.Packages
            .Where(p => which == "all" || (which == "on" && p.Enabled))
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            plugins.Add(new JsonObject
            {
                ["id"] = package.Id,
                ["name"] = package.DisplayName,
                ["version"] = package.Version,
                ["on"] = package.Enabled,
                ["group"] = package.Group,
                ["pro"] = package.Pro,
                ["license"] = package.License is { Status: not Proto.PluginLicenseStatus.PluginLicenseNotRequired } state ? state.Reason : string.Empty,
            });
        }

        var on = assistant.Settings?.Enabled ?? false;
        return new JsonObject
        {
            ["license"] = new JsonObject
            {
                ["status"] = license.Status switch
                {
                    Proto.ProLicenseStatus.ProLicenseActive => "Active",
                    Proto.ProLicenseStatus.ProLicenseExpired => "Expired",
                    Proto.ProLicenseStatus.ProLicenseWrongServer => "WrongServer",
                    Proto.ProLicenseStatus.ProLicenseInvalid => "Invalid",
                    _ => "None",
                },
                ["text"] = license.Reason,
                ["plan"] = license.Plan,
                ["max_devices"] = license.HasMaxDevices ? license.MaxDevices : null,
                ["expires"] = license.Expires,
                ["trial"] = license.Trial,
            },
            ["plugins_on"] = packages.Packages.Count(p => p.Enabled),
            ["plugins_off"] = packages.Packages.Count(p => !p.Enabled),
            ["plugins"] = plugins,
            ["mcp"] = new JsonObject
            {
                ["on"] = mcp.Settings?.Enabled ?? false,
                ["running"] = mcp.Running,
                ["status"] = mcp.Status,
            },
            ["assistant"] = new JsonObject
            {
                ["on"] = on,
                ["provider"] = on ? assistant.Settings!.Provider : string.Empty,
                ["send_pictures"] = on && assistant.Settings!.SendPictures,
            },
            ["vulnerability_list"] = new JsonObject
            {
                ["date"] = cves.Updated is { Seconds: > 0 } date ? date.ToDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null,
                ["count"] = cves.Count,
                ["source"] = cves.Source,
                ["online_update"] = cves.Update,
            },
        };
    }
}
