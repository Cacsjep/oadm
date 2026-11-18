using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Contracts.Assistant;
using Oadm.Contracts.Mcp;
using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Server.Assistant;
using Oadm.Server.Sites;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The server's own settings and services as tools (docs/spec/mcp.md "Server settings and services", decision 2026-11-15): the
/// Settings page's fields with a diff, the MCP server, the Assistant (its API key only through the secret channel), the PRO
/// trial, the Sites page's changes, Trust new certificate and Delete all devices. Each change: the preview writes nothing and
/// checks with the page's texts, the run does it once and is audited like the client, only administrators see the tools, and
/// the Assistant's card shows the plan.
/// </summary>
public sealed class McpServerChangeTests
{
    /// <summary>The tools of this part (<see cref="McpToolTests"/> lists them with the others).</summary>
    public static readonly string[] ToolNames =
    [
        "oadm_assistant_settings_preview", "oadm_assistant_settings_run", "oadm_assistant_test",
        "oadm_devices_certificate_trust_preview", "oadm_devices_certificate_trust_run", "oadm_devices_remove_all_preview", "oadm_devices_remove_all_run",
        "oadm_mcp_set_preview", "oadm_mcp_set_run",
        "oadm_settings_set_preview", "oadm_settings_set_run",
        "oadm_sites_accept_preview", "oadm_sites_accept_run", "oadm_sites_bind_preview", "oadm_sites_bind_run", "oadm_sites_remove_preview",
        "oadm_sites_remove_run", "oadm_sites_replace_preview", "oadm_sites_replace_run", "oadm_sites_waiting",
    ];

    /// <summary>The gRPC methods these tools call besides those of the other parts.</summary>
    public static readonly string[] Methods =
    [
        "/oadm.v1.AssistantService/SetSettings", "/oadm.v1.AssistantService/Test", "/oadm.v1.DeviceService/GetPresentedCertificates",
        "/oadm.v1.DeviceService/TrustNewCertificates", "/oadm.v1.LicenseService/StartTrial", "/oadm.v1.McpService/Set", "/oadm.v1.SettingsService/Set",
        "/oadm.v1.SiteService/BindSite", "/oadm.v1.SiteService/ListSites", "/oadm.v1.SiteService/RemoveSite", "/oadm.v1.SiteService/ReplaceSite",
        "/oadm.v1.SiteService/SetAccept",
    ];

    public static string[] RunTools => [.. ToolNames.Where(n => n.EndsWith("_run", StringComparison.Ordinal))];

    /// <summary>The runs that only add (<c>destructiveHint</c> false).</summary>
    public static readonly string[] AddingRuns = ["oadm_sites_bind_run"];

    /// <summary>The tools that reach devices or the network (no plain read).</summary>
    public static readonly string[] ActsOutside = ["oadm_assistant_test", "oadm_devices_certificate_trust_preview", "oadm_devices_certificate_trust_run"];

    /// <summary>One call of every tool here for the scan of <see cref="McpSecretTests"/> (no call that changes the server).</summary>
    public static Dictionary<string, JsonObject> SecretScanCalls(string deviceId)
    {
        var calls = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["oadm_settings_set_preview"] = new() { ["changes"] = new JsonObject { ["scan_parallelism"] = 0 } },
            ["oadm_mcp_set_preview"] = new() { ["port"] = 80 },
            ["oadm_assistant_settings_preview"] = new() { ["provider"] = AssistantTexts.Anthropic, ["new_api_key"] = true },
            ["oadm_assistant_test"] = new() { },
            ["oadm_sites_waiting"] = [],
            ["oadm_sites_accept_preview"] = new() { ["accept"] = false },
            ["oadm_sites_bind_preview"] = new() { ["node_id"] = "none", ["fingerprint"] = "none", ["name"] = "Scan" },
            ["oadm_sites_replace_preview"] = new() { ["site_id"] = Guid.NewGuid().ToString(), ["new_site_id"] = Guid.NewGuid().ToString() },
            ["oadm_sites_remove_preview"] = new() { ["site_id"] = Guid.NewGuid().ToString() },
            ["oadm_devices_certificate_trust_preview"] = new() { ["filter"] = new JsonObject { ["ids"] = new JsonArray(deviceId) } },
            ["oadm_devices_remove_all_preview"] = [],
        };
        foreach (var run in RunTools)
        {
            calls[run] = new JsonObject { ["handle"] = "ended" };
        }

        return calls;
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static async Task<IReadOnlyList<Proto.AuditEntry>> AuditAsync(McpTestServer server) =>
        (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;

    private static Proto.AssistantPreviewCard Card(McpTestServer server, string preview, JsonObject answer)
    {
        var registry = server.Host.Get<ToolRegistry>();
        Assert.True(registry.TryGet(preview, out var tool));
        registry.TryGet(AssistantTools.RunOf(preview), out var run);
        var card = AssistantCards.Build(tool, run, answer["handle"]?.GetValue<string>() ?? "h", answer, JsonSerializer.SerializeToElement(new JsonObject()));
        Assert.NotEmpty(card.Details);
        Assert.False(string.IsNullOrEmpty(card.CountText));
        return card;
    }

    private static async Task AssertOnceAsync(McpTestServer server, string key, string run, string handle)
    {
        var again = await server.CallToolAsync(key, run, new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>(), run);
        Assert.Contains(AreaListing.PreviewEnded, Text(again), StringComparison.Ordinal);
    }

    /// <summary>The administrator as the Assistant calls the tools (in process, the user's session).</summary>
    private static async Task<ToolCaller> AdminCallerAsync(McpTestServer server)
    {
        var admin = (await server.Host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var binding = AuthTokenStore.HashOf(server.Host.AdminToken);
        return new ToolCaller(new AuthenticatedCaller(admin.Id, admin.UserName, admin.RoleId, PermissionSet.All, "127.0.0.1", "assistant-session", binding), binding);
    }

    private static async Task<JsonObject> InProcessAsync(McpTestServer server, ToolCaller caller, string tool, JsonObject? arguments = null)
    {
        var result = await server.Host.Get<ToolRegistry>().CallAsync(caller, tool, JsonSerializer.SerializeToElement(arguments ?? []), CancellationToken.None);
        Assert.False(result.IsError, tool + ": " + result.Error);
        return result.Answer!;
    }

    [Fact]
    public async Task ServerSettingsChangeOnlyByTheRunWithTheDiffThePageTextsAndTheWarnings()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var before = await server.Host.Settings.GetAsync(new Proto.Empty());

        var invalid = await server.CallToolAsync(key, "oadm_settings_set_preview", new JsonObject
        {
            ["changes"] = new JsonObject { ["scan_parallelism"] = 0, ["server_name"] = "  ", ["listen_url"] = "ftp://x" },
        });
        Assert.True(invalid["isError"]!.GetValue<bool>());
        var errors = invalid.ToJsonString();
        Assert.Contains("Enter a whole number from 1 to 256.", errors, StringComparison.Ordinal);
        Assert.Contains(Oadm.Contracts.Settings.ServerSettingsRules.EnterServerName, errors, StringComparison.Ordinal);
        Assert.Contains(Oadm.Contracts.Settings.ServerSettingsRules.ListenUrlLike, errors, StringComparison.Ordinal);
        var empty = await server.CallToolAsync(key, "oadm_settings_set_preview", new JsonObject { ["changes"] = new JsonObject() });
        Assert.Contains(ServerChangeTools.NameAChange, Text(empty), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_settings_set_preview", new JsonObject
        {
            ["changes"] = new JsonObject
            {
                ["scan_parallelism"] = 64,
                ["allow_basic_over_http"] = true,
                ["listen_url"] = "https://0.0.0.0:5090",
                ["zero_conf_seconds"] = before.ZeroConfSeconds, // the same value: no change
            },
        });
        Assert.Equal(3, preview["will_change"]!.GetValue<int>());
        Assert.True(preview["restart_needed"]!.GetValue<bool>());
        var rows = preview["changes"]!.AsArray().Select(r => (r!["field"]!.GetValue<string>(), r["old"]!.GetValue<string>(), r["new"]!.GetValue<string>())).ToList();
        Assert.Contains(("scan_parallelism", before.ScanParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture), "64"), rows);
        Assert.Contains(("allow_basic_over_http", "off", "on"), rows);
        var warnings = preview["warnings"]!.ToJsonString();
        Assert.Contains(ServerChangeTools.BasicOverHttpWarning, warnings, StringComparison.Ordinal);
        Assert.Contains(ServerChangeTools.ListenUrlWarning, warnings, StringComparison.Ordinal);
        Assert.Equal(before.ScanParallelism, (await server.Host.Settings.GetAsync(new Proto.Empty())).ScanParallelism); // nothing written
        var card = Card(server, "oadm_settings_set_preview", preview);
        Assert.Equal("3 settings", card.CountText);
        Assert.Contains(card.Details, d => d.StartsWith("Addresses scanned at once: ", StringComparison.Ordinal) && d.EndsWith("→ 64", StringComparison.Ordinal));
        Assert.Contains(ServerChangeTools.ListenUrlWarning, card.Warnings);

        var handle = preview["handle"]!.GetValue<string>();
        var run = await server.ToolAsync(key, "oadm_settings_set_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(3, run["saved"]!.AsArray().Count);
        var after = await server.Host.Settings.GetAsync(new Proto.Empty());
        Assert.Equal((64, true, "https://0.0.0.0:5090", before.ClockToleranceSeconds), (after.ScanParallelism, after.AllowBasicOverHttp, after.ListenUrl, after.ClockToleranceSeconds));
        await AssertOnceAsync(server, key, "oadm_settings_set_run", handle);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.SettingsChanged && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));

        var same = await server.ToolAsync(key, "oadm_settings_set_preview", new JsonObject { ["changes"] = new JsonObject { ["scan_parallelism"] = 64 } });
        Assert.Null(same["handle"]);
        Assert.Contains(ServerChangeTools.NoChange, same["warnings"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorsAndGroupUsersSeeNoneOfTheseToolsAndAreRefused()
    {
        await using var server = await McpTestServer.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.71.0.1", 7101);
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Add = { "Lobby" } });
        var operatorKey = await server.CreateKeyAsync(roleId: BuiltInRoles.OperatorId.ToString());
        var names = await server.ToolNamesAsync(operatorKey);
        Assert.All(ToolNames.Where(n => n != "oadm_devices_certificate_trust_preview" && n != "oadm_devices_certificate_trust_run"), n => Assert.DoesNotContain(n, names));
        var refused = await server.CallToolAsync(operatorKey, "oadm_settings_set_preview", new JsonObject { ["changes"] = new JsonObject { ["scan_parallelism"] = 64 } });
        Assert.True(refused["isError"]!.GetValue<bool>());
        var removeAll = await server.CallToolAsync(operatorKey, "oadm_devices_remove_all_preview");
        Assert.True(removeAll["isError"]!.GetValue<bool>());

        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var groupKey = await server.CreateKeyAsync(group.Invoker);
        var groupNames = await server.ToolNamesAsync(groupKey);
        Assert.All(ToolNames.Where(n => !n.StartsWith("oadm_devices_certificate_trust", StringComparison.Ordinal)), n => Assert.DoesNotContain(n, groupNames));
        Assert.Single((await server.Host.Devices.ListAsync(new Proto.Empty())).Devices); // nothing removed
    }

    [Fact]
    public async Task TheMcpServerChangeChecksThePortWarnsThatTheAgentMayDropAndTurnsOffOnlyWithTheRun()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();

        var port = await server.CallToolAsync(key, "oadm_mcp_set_preview", new JsonObject { ["port"] = 80 });
        Assert.True(port["isError"]!.GetValue<bool>());
        Assert.Contains(McpTexts.PortRange, Text(port), StringComparison.Ordinal);
        var unknown = await server.CallToolAsync(key, "oadm_mcp_set_preview", new JsonObject { ["listen_on"] = "no-such-interface" });
        Assert.Contains(McpTexts.UnknownInterface, Text(unknown), StringComparison.Ordinal);

        var off = await server.ToolAsync(key, "oadm_mcp_set_preview", new JsonObject { ["enabled"] = false });
        Assert.Equal(1, off["will_change"]!.GetValue<int>());
        var warnings = off["warnings"]!.ToJsonString();
        Assert.Contains(ServerChangeTools.McpOffWarning, warnings, StringComparison.Ordinal);
        Assert.Contains(ServerChangeTools.OwnConnectionWarning, warnings, StringComparison.Ordinal); // it came over MCP
        Assert.True(server.Listener.Status.Running); // nothing changed
        Assert.True(Card(server, "oadm_mcp_set_preview", off).Destructive);

        // The Assistant's caller: no warning about its own connection; the run turns the server off and is audited.
        var admin = await AdminCallerAsync(server);
        var preview = await InProcessAsync(server, admin, "oadm_mcp_set_preview", new JsonObject { ["enabled"] = false });
        Assert.DoesNotContain(ServerChangeTools.OwnConnectionWarning, preview["warnings"]!.ToJsonString(), StringComparison.Ordinal);
        var run = await InProcessAsync(server, admin, "oadm_mcp_set_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.False(run["settings"]!["enabled"]!.GetValue<bool>());
        Assert.False(server.Listener.Status.Running);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.McpChanged);
    }

    [Fact]
    public async Task TheAssistantKeyGoesOnlyThroughTheSecretChannelAndNeverIntoAnAnswerOrTheAudit()
    {
        const string ApiKey = "sk-ant-canary-5c1e";
        await using var server = await McpTestServer.StartAsync();
        var admin = await AdminCallerAsync(server);
        var registry = server.Host.Get<ToolRegistry>();

        var missing = await registry.CallAsync(admin, "oadm_assistant_settings_preview",
            JsonSerializer.SerializeToElement(new JsonObject { ["enabled"] = true, ["provider"] = AssistantTexts.Anthropic }), CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Contains(missing.Fields, f => f.Field == "new_api_key" && f.Error == AssistantTexts.EnterApiKey);
        var steps = await registry.CallAsync(admin, "oadm_assistant_settings_preview",
            JsonSerializer.SerializeToElement(new JsonObject { ["provider"] = AssistantTexts.Ollama, ["address"] = "http://10.0.0.5:11434", ["steps_per_answer"] = 99 }), CancellationToken.None);
        Assert.Contains(steps.Fields, f => f.Field == "steps_per_answer" && f.Error == AssistantTexts.StepsRange);

        var preview = await InProcessAsync(server, admin, "oadm_assistant_settings_preview", new JsonObject
        {
            ["enabled"] = true, ["provider"] = AssistantTexts.Anthropic, ["new_api_key"] = true, ["steps_per_answer"] = 10,
        });
        Assert.Equal(["api_key"], preview["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(preview["secret_links"]); // the Assistant's card takes it
        var card = Card(server, "oadm_assistant_settings_preview", preview);
        Assert.Contains(card.Details, d => d.StartsWith("API key", StringComparison.Ordinal));
        Assert.Contains(ServerChangeTools.AssistantKeyWarning, card.Warnings);
        Assert.False((await server.Host.Get<AssistantChats>().SettingsAsync(CancellationToken.None)).Enabled); // nothing written

        var handle = preview["handle"]!.GetValue<string>();
        var noKey = await registry.CallAsync(admin, "oadm_assistant_settings_run", JsonSerializer.SerializeToElement(new JsonObject { ["handle"] = handle }), CancellationToken.None);
        Assert.True(noKey.IsError);
        Assert.Equal("Enter the API key.", noKey.Error);
        Assert.Null(registry.Secrets!.Set(admin.BindingKey, handle, "api_key", ApiKey));
        var run = await InProcessAsync(server, admin, "oadm_assistant_settings_run", new JsonObject { ["handle"] = handle });
        Assert.True(run["settings"]!["has_api_key"]!.GetValue<bool>());
        Assert.Equal(10, run["settings"]!["steps_per_answer"]!.GetValue<int>());
        Assert.Equal("On", run["status"]!.GetValue<string>());

        var audit = await AuditAsync(server);
        Assert.Contains(audit, e => e.Action == AuditActions.AssistantChanged);
        Assert.DoesNotContain(audit, e => (e.Detail + e.Target).Contains(ApiKey, StringComparison.Ordinal));
        Assert.DoesNotContain(ApiKey, preview.ToJsonString() + run.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssistantKeyThatWentForNewerPreviewsRefusesTheRunAndWritesNothing()
    {
        await using var server = await McpTestServer.StartAsync();
        var admin = await AdminCallerAsync(server);
        var registry = server.Host.Get<ToolRegistry>();
        var preview = await InProcessAsync(server, admin, "oadm_assistant_settings_preview", new JsonObject
        {
            ["enabled"] = true, ["provider"] = AssistantTexts.Anthropic, ["new_api_key"] = true,
        });
        var handle = preview["handle"]!.GetValue<string>();
        Assert.Null(registry.Secrets!.Set(admin.BindingKey, handle, "api_key", "sk-ant-canary-77"));

        // Newer previews of another kind (task runs) that need secrets too: the oldest slots go, the settings' handle stays.
        for (var i = 0; i < ToolSecrets.MaxPerCaller; i++)
        {
            registry.Secrets.Declare(admin.BindingKey, "task-preview-" + i, "oadm_task_run", [new ToolSecretSlot("password", "password")]);
        }

        var run = await registry.CallAsync(admin, "oadm_assistant_settings_run", JsonSerializer.SerializeToElement(new JsonObject { ["handle"] = handle }), CancellationToken.None);
        Assert.True(run.IsError);
        Assert.Equal(AreaListing.PreviewEnded, run.Error);
        var settings = await server.Host.Get<AssistantChats>().SettingsAsync(CancellationToken.None);
        Assert.False(settings.Enabled); // nothing written
        Assert.DoesNotContain(await AuditAsync(server), e => e.Action == AuditActions.AssistantChanged);
    }

    [Fact]
    public async Task SitesTurnAcceptOnAndOffOnlyWithTheRunAndRefuseUnknownNodesAndSites()
    {
        await using var server = await McpTestServer.StartAsync(configureServices: s =>
        {
            s.RemoveAll<SiteOptions>(); // a free port: another OADM server on this computer may hold TCP 5081
            s.AddSingleton(new SiteOptions { ListenUrl = "127.0.0.1:0" });
        });
        var key = await server.CreateKeyAsync();
        var sites = new Proto.SiteService.SiteServiceClient(server.Host.Invoker);
        var accept = (await sites.ListSitesAsync(new Proto.Empty())).Accept;

        var preview = await server.ToolAsync(key, "oadm_sites_accept_preview", new JsonObject { ["accept"] = !accept });
        Assert.Contains(accept ? SiteChangeTools.AcceptOffWarning : SiteChangeTools.AcceptOnWarning, preview["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.Equal(accept, (await sites.ListSitesAsync(new Proto.Empty())).Accept); // nothing written
        Assert.Equal(accept, Card(server, "oadm_sites_accept_preview", preview).Destructive);
        var run = await server.ToolAsync(key, "oadm_sites_accept_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(!accept, run["accept"]!.GetValue<bool>());
        Assert.Equal(!accept, (await sites.ListSitesAsync(new Proto.Empty())).Accept);
        var same = await server.ToolAsync(key, "oadm_sites_accept_preview", new JsonObject { ["accept"] = !accept });
        Assert.Null(same["handle"]);

        var waiting = await server.ToolAsync(key, "oadm_sites_waiting");
        Assert.Empty(waiting["nodes"]!.AsArray());
        var bind = await server.CallToolAsync(key, "oadm_sites_bind_preview", new JsonObject { ["node_id"] = "n1", ["fingerprint"] = "AB", ["name"] = "Parking" });
        Assert.True(bind["isError"]!.GetValue<bool>());
        Assert.Contains(SiteChangeTools.UnknownNode, Text(bind), StringComparison.Ordinal);
        var remove = await server.CallToolAsync(key, "oadm_sites_remove_preview", new JsonObject { ["site_id"] = Guid.NewGuid().ToString() });
        Assert.Contains(HostActionTools.UnknownSite, Text(remove), StringComparison.Ordinal);
        var replace = await server.CallToolAsync(key, "oadm_sites_replace_preview", new JsonObject { ["site_id"] = Guid.NewGuid().ToString(), ["new_site_id"] = Guid.NewGuid().ToString() });
        Assert.Contains(HostActionTools.UnknownSite, Text(replace), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAllDevicesCountsEveryDeviceAndRemovesThemOnlyWithTheRun()
    {
        await using var server = await McpTestServer.StartAsync();
        for (var i = 1; i <= 3; i++)
        {
            await DeviceServiceTests.AddDeviceAsync(server.Host, $"10.72.0.{i}", 7200 + i);
        }

        var key = await server.CreateKeyAsync();
        var preview = await server.ToolAsync(key, "oadm_devices_remove_all_preview");
        Assert.Equal(3, preview["devices"]!.GetValue<int>());
        Assert.Equal(3, preview["first"]!.AsArray().Count);
        Assert.Equal(3, (await server.Host.Devices.ListAsync(new Proto.Empty())).Devices.Count); // nothing written
        var card = Card(server, "oadm_devices_remove_all_preview", preview);
        Assert.True(card.Destructive);
        Assert.Equal("3 devices", card.CountText);

        var handle = preview["handle"]!.GetValue<string>();
        var run = await server.ToolAsync(key, "oadm_devices_remove_all_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(3, run["devices"]!.GetValue<int>());
        Assert.Empty((await server.Host.Devices.ListAsync(new Proto.Empty())).Devices);
        await AssertOnceAsync(server, key, "oadm_devices_remove_all_run", handle);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.DevicesRemoved && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));
        var none = await server.ToolAsync(key, "oadm_devices_remove_all_preview");
        Assert.Null(none["handle"]);
    }

    [Fact]
    public async Task TrustingACertificateShowsBothFingerprintsAndNeedsADeviceThatShowsCertificateChanged()
    {
        await using var server = await McpTestServer.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.73.0.1", 7301);
        var key = await server.CreateKeyAsync();

        var preview = await server.ToolAsync(key, "oadm_devices_certificate_trust_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) } });
        var row = Assert.Single(preview["devices"]!.AsArray())!;
        Assert.Equal(device.Id.ToString(), row["id"]!.GetValue<string>());
        Assert.Equal(string.Empty, row["new_fingerprint"]!.GetValue<string>()); // the device presents the trusted certificate (or none)
        Assert.False(string.IsNullOrEmpty(row["message"]!.GetValue<string>()));
        Assert.Equal(0, preview["will_change"]!.GetValue<int>());
        Assert.Null(preview["handle"]);

        for (var i = 2; i <= DeviceTrustTools.MaxTrustDevices + 1; i++)
        {
            await DeviceServiceTests.AddDeviceAsync(server.Host, $"10.73.0.{i}", 7300 + i);
        }

        var tooMany = await server.CallToolAsync(key, "oadm_devices_certificate_trust_preview", new JsonObject { ["filter"] = new JsonObject() });
        Assert.True(tooMany["isError"]!.GetValue<bool>());
        Assert.Contains(DeviceTrustTools.TooMany, Text(tooMany), StringComparison.Ordinal);
    }
}
