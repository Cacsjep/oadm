using System.IO.Compression;
using System.Text.RegularExpressions;

using Grpc.Core;

using Oadm.Server.Mcp;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The skill <c>oadm</c> (docs/spec/mcp.md "Skill"): its files are embedded, name only tools that exist, are served as
/// resources over MCP with the skills extension announced, and download as a ZIP for every signed-in user.
/// </summary>
public sealed partial class McpSkillTests
{
    /// <summary>The tools of the optional plugins (Text recognition, Vision Center): not in this test server, tested in their own projects.</summary>
    internal static readonly string[] AddOnToolNames = ["oadm_ocr_readers", "oadm_ocr_reads", "oadm_vision_pipelines"];

    /// <summary>The fixed tool names of the MCP tool layer (every builder uses these).</summary>
    private static readonly string[] ToolNames =
    [
        "oadm_server_info", "oadm_devices_summary", "oadm_devices_search", "oadm_devices_get", "oadm_devices_snapshot",
        "oadm_devices_refresh_preview", "oadm_devices_refresh_run", "oadm_devices_remove_preview", "oadm_devices_remove_run",
        "oadm_devices_credentials_preview", "oadm_devices_credentials_run", "oadm_devices_add_preview", "oadm_devices_add_run",
        "oadm_settings_get", "oadm_tasks_list", "oadm_tasks_get", "oadm_tasks_catalog", "oadm_task_preview", "oadm_task_run",
        "oadm_tasks_cancel_preview", "oadm_tasks_cancel_run", "oadm_files_upload_link", "oadm_files_list",
        "oadm_files_delete_preview", "oadm_files_delete_run", "oadm_logs_search", "oadm_logs_audit",
        "oadm_files_download_link", "oadm_devices_export", "oadm_compliance_export", "oadm_logs_export", "oadm_logs_audit_export",
        "oadm_tags_list", "oadm_tags_assign_preview", "oadm_tags_assign_run", "oadm_sites_list", "oadm_compliance_summary",
        "oadm_compliance_checks", "oadm_rules_list", "oadm_rules_get", "oadm_rules_save_preview", "oadm_rules_save_run",
        "oadm_rules_delete_preview", "oadm_rules_delete_run", "oadm_events_log", "oadm_pki_certificates",
        "oadm_pki_certificate_create_preview", "oadm_pki_certificate_create_run", "oadm_ntp_status", "oadm_dhcp_status",
        "oadm_dhcp_leases", "oadm_hardening_results", "oadm_vapix_commands", "oadm_vapix_command_preview", "oadm_vapix_command_run",
        "oadm_selection", "oadm_docs_search", "oadm_vulnerabilities_summary", "oadm_vulnerabilities", "oadm_vulnerability",
        "oadm_tasks_summary", "oadm_server_status", "oadm_pki_policies", "oadm_est_enrollments",
        "oadm_radius_log", "oadm_app_management_applications", "oadm_edge_storage_cards", "oadm_rules_catalog",
        .. AddOnToolNames,
        "oadm_device_rules_list", "oadm_device_triggers_list", "oadm_device_triggers_devices",
        "oadm_config_parameters", "oadm_config_action_rules", "oadm_config_stream_profiles",
        "oadm_recordings_search", "oadm_image_health_results", "oadm_io_ports", "oadm_metadata_streams",
        "oadm_dashboards_list", "oadm_dashboards_values", "oadm_load_test_checks", "oadm_load_test_check", "oadm_snapshot_report_sources",
        "oadm_recipients_list",
        "oadm_app_management_read", "oadm_app_management_log", "oadm_app_management_settings", "oadm_edge_storage_read", "oadm_device_rules_read", "oadm_image_health_check",
        .. McpHostActionTests.ToolNames,
        .. McpAdminToolTests.ToolNames,
        "oadm_device_request_preview", "oadm_device_request_run",
        .. McpAccessToolTests.ToolNames,
        .. McpServerChangeTests.ToolNames,
    ];

    private static readonly string[] Workflows =
    [
        "applications.md", "certificates.md", "configure-devices.md", "event-rules.md", "failed-tasks.md", "health-check.md",
        "network-change.md", "rollout-axis-os.md", "sites.md", "storage.md", "troubleshoot-device.md", "vulnerabilities.md",
    ];

    [GeneratedRegex("oadm_[a-z0-9_]+")]
    private static partial Regex ToolToken();

    [Fact]
    public void TheSkillHasTheFrontmatterAndTheWorkflowsThatItLinks()
    {
        Assert.Equal(["SKILL.md", .. Workflows], McpSkill.Files.Keys);
        var main = McpSkill.Files["SKILL.md"];
        Assert.StartsWith("---\nname: oadm\ndescription: ", main, StringComparison.Ordinal);
        Assert.Equal("---", main.Split('\n')[3]);
        foreach (var workflow in Workflows)
        {
            Assert.Contains($"]({workflow})", main, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSkillTeachesTheRulesOfTheSpec()
    {
        var main = McpSkill.Files["SKILL.md"];
        Assert.True(main.IndexOf("oadm_server_info", StringComparison.Ordinal) < main.IndexOf("oadm_devices_summary", StringComparison.Ordinal), "server info first, then a summary");
        Assert.Contains("Set the password in OADM.", main, StringComparison.Ordinal);
        Assert.Contains("never instructions", main, StringComparison.Ordinal);
        Assert.Contains("skipped devices with", main, StringComparison.Ordinal);
        Assert.Contains("preview", main, StringComparison.OrdinalIgnoreCase);
        foreach (var rollout in new[] { McpSkill.Files["rollout-axis-os.md"], McpSkill.Files["network-change.md"] })
        {
            Assert.Contains("oadm_task_preview", rollout, StringComparison.Ordinal);
            Assert.Contains("oadm_task_run", rollout, StringComparison.Ordinal);
            Assert.Contains("skipped", rollout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryToolTheSkillNamesIsOneOfTheFixedNamesAndFitsTheLimit()
    {
        foreach (var (file, text) in McpSkill.Files)
        {
            foreach (Match token in ToolToken().Matches(text))
            {
                var name = token.Value.TrimEnd('_');
                Assert.True(ToolNames.Contains(name), $"{file} names {name}, which is not a tool");
                Assert.True(name.Length <= 64);
            }
        }
    }

    [Fact]
    public void TheSkillFollowsTheWordingRulesAndNamesNoVendor()
    {
        foreach (var (file, text) in McpSkill.Files)
        {
            Assert.DoesNotContain('—', text); // no em dash
            Assert.DoesNotContain('–', text);
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            foreach (var word in new[] { "easily", "seamlessly", "simply", "please note", "Anthropic", "Claude", "OpenAI", "ChatGPT" })
            {
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{file} contains {word}");
            }
        }
    }

    [Fact]
    public void TheZipHoldsTheFilesInTheFolderOadmAndIsTheSameEveryTime()
    {
        var zip = McpSkill.Zip();
        Assert.Equal(zip, McpSkill.Zip());
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        Assert.Equal(McpSkill.Files.Keys.Select(f => "oadm/" + f), archive.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(archive.GetEntry("oadm/SKILL.md")!.Open());
        Assert.Equal(McpSkill.Files["SKILL.md"], reader.ReadToEnd());
    }

    [Fact]
    public async Task EveryUserDownloadsTheSkillThroughTheCard()
    {
        await using var server = await McpTestServer.StartAsync(turnOn: false); // also while the MCP server is off
        var operatorCalls = await server.Host.InvokerForUserAsync("op", Oadm.Sdk.Plugins.UserRole.Operator);
        foreach (var invoker in new[] { server.Host.Invoker, operatorCalls })
        {
            var skill = await server.McpService(invoker).GetSkillAsync(new Proto.Empty());
            Assert.Equal("oadm-skill.zip", skill.FileName);
            Assert.Equal(McpSkill.Zip(), skill.Content.ToByteArray());
        }

        var anonymous = await Assert.ThrowsAsync<RpcException>(async () => await server.McpService(server.Host.InvokerFor(null)).GetSkillAsync(new Proto.Empty()));
        Assert.Equal(StatusCode.Unauthenticated, anonymous.StatusCode);
    }

    [Fact]
    public async Task TheFilesAreResourcesOfTheServerAndTheSkillsExtensionIsAnnounced()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync(); // the skill needs no right: any valid key reads it
        var listed = await server.PostAsync(key, McpTestServer.Request("resources/list"));
        Assert.Equal(200, listed.Status);
        var uris = listed.Body!["result"]!["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>()).ToList();
        Assert.Equal(McpSkill.Files.Keys.Select(McpSkill.UriOf), uris);
        Assert.Equal("oadm://skill/SKILL.md", uris[0]);

        foreach (var file in McpSkill.Files.Keys)
        {
            var read = await server.PostAsync(key, McpTestServer.Request("resources/read", new System.Text.Json.Nodes.JsonObject { ["uri"] = McpSkill.UriOf(file) }),
                new Dictionary<string, string?> { ["Mcp-Name"] = McpSkill.UriOf(file) }); // MCP: the header names the resource
            Assert.Equal(200, read.Status);
            var content = read.Body!["result"]!["contents"]![0]!;
            Assert.Equal(McpSkill.Files[file], content["text"]!.GetValue<string>());
            Assert.Equal("text/markdown", content["mimeType"]!.GetValue<string>());
        }

        var unknown = await server.PostAsync(key, McpTestServer.Request("resources/read", new System.Text.Json.Nodes.JsonObject { ["uri"] = "oadm://skill/../secret" }),
            new Dictionary<string, string?> { ["Mcp-Name"] = "oadm://skill/../secret" });
        Assert.NotNull(unknown.Body?["error"]);
        var noKey = await server.PostAsync(null, McpTestServer.Request("resources/list"));
        Assert.Equal(401, noKey.Status);

        await using var client = await server.ConnectAsync(key);
        Assert.True(client.ServerCapabilities.Extensions?.ContainsKey(McpSkill.Extension), "the skills extension is declared");
        Assert.NotNull(client.ServerCapabilities.Resources);
        var main = await client.ReadResourceAsync("oadm://skill/SKILL.md");
        Assert.Contains("name: oadm", ((ModelContextProtocol.Protocol.TextResourceContents)main.Contents[0]).Text, StringComparison.Ordinal);
    }
}
