using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Events;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Rule 4 "No secrets" (docs/spec/mcp.md "Tests"): with known passwords, a credential list entry, a session token and the access
/// key itself in the server, a call of every tool, the refusals, the audit log and the server log hold none of them.
/// </summary>
public sealed class McpSecretTests
{
    private const string DevicePassword = "canary-device-6f1d";
    private const string ListPassword = "canary-list-93ac";

    [Fact]
    public async Task NoAnswerAuditEntryOrLogLineHoldsAPasswordATokenOrTheKey()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.4.0.1", 4001, password: DevicePassword);
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = ListPassword });
        var key = await server.CreateKeyAsync(name: "canary");
        var answers = new StringBuilder();

        var calls = new Dictionary<string, JsonObject>
        {
            ["oadm_server_info"] = [],
            ["oadm_devices_summary"] = new() { ["group_by"] = "model" },
            ["oadm_devices_search"] = new() { ["fields"] = new JsonArray([.. Oadm.Server.Tools.DeviceTools.ExtraFields.Select(f => (JsonNode)f)]) },
            ["oadm_devices_get"] = new() { ["device_id"] = device.Id.ToString() },
            ["oadm_tasks_list"] = [],
            ["oadm_tasks_summary"] = [],
            ["oadm_server_status"] = new() { ["plugins"] = "all" },
            ["oadm_tasks_get"] = new() { ["task_id"] = Guid.NewGuid().ToString() },
            ["oadm_logs_search"] = new() { ["text"] = "MCP" },
            ["oadm_logs_audit"] = [],
            ["oadm_tasks_catalog"] = [],
            ["oadm_task_preview"] = new() { ["task"] = "oadm.restart", ["filter"] = new JsonObject(), ["input"] = new JsonObject() },
            ["oadm_task_run"] = new() { ["handle"] = "ended" },
            ["oadm_files_upload_link"] = new() { ["name"] = "a.bin", ["size"] = 1 },
            ["oadm_devices_snapshot"] = new() { ["device_id"] = device.Id.ToString() },
            ["oadm_devices_refresh_preview"] = new() { ["filter"] = new JsonObject() },
            ["oadm_devices_refresh_run"] = new() { ["handle"] = "ended" },
            ["oadm_devices_remove_preview"] = new() { ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) } },
            ["oadm_devices_remove_run"] = new() { ["handle"] = "ended" },
            ["oadm_devices_credentials_preview"] = new() { ["filter"] = new JsonObject(), ["credential"] = "root" },
            ["oadm_devices_credentials_run"] = new() { ["handle"] = "ended" },
            ["oadm_devices_add_preview"] = new() { ["addresses"] = new JsonArray("10.4.0.1"), ["keep_waiting"] = true },
            ["oadm_devices_add_run"] = new() { ["handle"] = "ended" },
            ["oadm_settings_get"] = [],
            ["oadm_files_list"] = [],
            ["oadm_files_delete_preview"] = new() { ["file_ids"] = new JsonArray("0123456789abcdef0123456789abcdef") },
            ["oadm_files_delete_run"] = new() { ["handle"] = "ended" },
            ["oadm_tasks_cancel_preview"] = new() { ["batch"] = Guid.NewGuid().ToString() },
            ["oadm_tasks_cancel_run"] = new() { ["handle"] = "ended" },
            ["oadm_selection"] = [],
            ["oadm_docs_search"] = new() { ["query"] = "credentials" },
            ["oadm_files_download_link"] = new() { ["file_ref"] = "ended" },
            ["oadm_devices_export"] = new() { ["filter"] = new JsonObject() },
            ["oadm_compliance_export"] = new() { ["filter"] = new JsonObject() },
            ["oadm_logs_export"] = new() { ["format"] = "csv" },
            ["oadm_logs_audit_export"] = new() { ["format"] = "pdf" },
        };
        var areaKey = await server.CreateKeyAsync(name: "canary-areas"); // a key allows 20 calls a second: the areas' tools use their own
        var areaCalls = McpAreaToolTests.SecretScanCalls();
        foreach (var (tool, arguments) in McpHostActionTests.SecretScanCalls(device.Id.ToString()).Concat(McpAdminToolTests.SecretScanCalls()).Concat(McpAccessToolTests.SecretScanCalls()).Concat(McpServerChangeTests.SecretScanCalls(device.Id.ToString())))
        {
            areaCalls[tool] = arguments;
        }
        Assert.Equal(calls.Keys.Concat(areaCalls.Keys).Order(StringComparer.Ordinal), await server.ToolNamesAsync(key));
        // 20 calls a second per key: ten calls with each key.
        var keys = new List<string> { key };
        var n = 0;
        foreach (var (tool, arguments) in calls)
        {
            if (n++ % 10 == 0 && n > 1)
            {
                keys.Add(await server.CreateKeyAsync(name: "canary" + keys.Count));
            }

            clock.Advance(TimeSpan.FromMinutes(2)); // the limits count per second and minute, per key and per user
            answers.Append((await server.CallToolAsync(keys[^1], tool, arguments)).ToJsonString());
        }

        foreach (var (tool, arguments) in areaCalls)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            answers.Append((await server.CallToolAsync(areaKey, tool, arguments)).ToJsonString());
        }

        answers.Append((await server.PostAsync(key, McpTestServer.Request("tools/list"))).Body?.ToJsonString());
        answers.Append((await server.PostAsync(key + "x", McpTestServer.Request("tools/list"))).Body?.ToJsonString());
        answers.Append((await server.PostAsync(server.Host.AdminToken, McpTestServer.Request("tools/list"))).Body?.ToJsonString());

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries
            .Select(e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail, e.ClientAddress));
        var logs = ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);

        string[] secrets = [DevicePassword, ListPassword, server.Host.AdminToken, areaKey, AuthTokenStore.HashOf(areaKey), areaKey["oadm_".Length..], .. keys, .. keys.Select(AuthTokenStore.HashOf), .. keys.Select(k => k["oadm_".Length..])];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, answers.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(audit, e => e.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }

        Assert.Contains("Device details", answers.ToString(), StringComparison.Ordinal); // the scan read real answers
    }

    [Fact]
    public async Task NoAnswerAuditEntryOrLogLineOfARunThatSucceededHoldsAPasswordATokenOrTheKey()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var network = new FakeAxisNetwork();
        network.Add("10.4.1.1", FakeSerials.Make(4101), ListPassword); // answers to the credential list: the add run logs in to it
        await using var server = await McpTestServer.StartAsync(network, accessClock: clock, configureServices: services =>
        {
            services.RemoveAll<IServerNetworkInterfaces>(); // the server's own network of this test: 10.4.1.0/24
            services.AddSingleton<IServerNetworkInterfaces>(new OwnCards());
        });
        var plugins = server.Host.Get<PluginRegistry>();
        Assert.True(plugins.RegisterActionPlugin(new ScanAction(), new PluginOrigin("test.scan.notify", "1.0", null) { DisplayName = "Notify" }));
        Assert.True(plugins.RegisterTaskPlugin(new Oadm.Plugins.Restart.RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System), new PluginOrigin("oadm.restart", "1.0.0", null)));
        Assert.True(plugins.RegisterTaskPlugin(new WaitingTask(), new PluginOrigin(WaitingTask.PluginId, "1.0.0", null)));
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.4.0.1", 4001, password: DevicePassword);
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = ListPassword });
        var key = await server.CreateKeyAsync(name: "canary-runs");
        var answers = new StringBuilder();
        var steps = new List<string>();

        // Each step is a real preview or run that must succeed; the clock moves on so no per-key or per-user limit counts them together.
        async Task<JsonObject> StepAsync(string tool, JsonObject arguments)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            var answer = await server.ToolAsync(key, tool, arguments);
            answers.Append(answer.ToJsonString());
            steps.Add(tool);
            return answer;
        }

        async Task<JsonObject> ChangeAsync(string name, JsonObject arguments)
        {
            var preview = await StepAsync(name + "_preview", arguments);
            Assert.True(preview["handle"] is not null, preview.ToJsonString());
            return await StepAsync(name + "_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        }

        var device1 = new JsonObject { ["filter"] = new JsonObject { ["search"] = "10.4.0.1" } };
        await ChangeAsync("oadm_devices_add", new JsonObject { ["addresses"] = new JsonArray("10.4.1.1") });
        await ChangeAsync("oadm_devices_credentials", new JsonObject { ["filter"] = device1["filter"]!.DeepClone(), ["credential"] = "root" });
        await ChangeAsync("oadm_tags_assign", new JsonObject { ["filter"] = device1["filter"]!.DeepClone(), ["add"] = new JsonArray("Scan") });

        var rule = new JsonObject
        {
            ["name"] = "Scan rule",
            ["conditions"] = new JsonArray(new JsonObject { ["kind"] = "event", ["event"] = BuiltInEvents.Unreachable, ["devices"] = new JsonObject { ["mode"] = "all" } }),
            ["actions"] = new JsonArray(new JsonObject { ["action"] = "test.scan.notify", ["settings"] = new JsonObject { ["message"] = "{device} is {state}" } }),
        };
        var saved = await ChangeAsync("oadm_rules_save", new JsonObject { ["rule"] = rule });
        await ChangeAsync("oadm_rules_delete", new JsonObject { ["rule_id"] = saved["rule_id"]!.GetValue<string>() });

        var run = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = WaitingTask.PluginId, DeviceIds = { device.Id.ToString() } });
        await ChangeAsync("oadm_tasks_cancel", new JsonObject { ["batch"] = run.BatchId });
        await server.Host.Get<Oadm.Core.Tasks.TaskEngine>().WaitForCompletionAsync(Guid.Parse(run.TaskIds[0]), CancellationToken.None);
        var restart = await ChangeAsync("oadm_task", new JsonObject { ["task"] = "oadm.restart", ["filter"] = new JsonObject { ["search"] = "10.4.1.1" }, ["input"] = new JsonObject() });
        var engine = server.Host.Get<Oadm.Core.Tasks.TaskEngine>();
        var restartBatch = restart["batch"]!.GetValue<string>();
        var restartTasks = (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Where(t => t.BatchId == restartBatch).Select(t => Guid.Parse(t.Id)).ToList();
        Assert.NotEmpty(restartTasks);
        foreach (var started in restartTasks)
        {
            await engine.WaitForCompletionAsync(started, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); // its log lines are written before the scan
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        var uploadBytes = new byte[] { 1, 2, 3 };
        var link = await server.ToolAsync(key, "oadm_files_upload_link", new JsonObject { ["name"] = "scan.bin", ["size"] = uploadBytes.Length });
        var linkUrl = new Uri(link["url"]!.GetValue<string>());
        using (var client = server.Http())
        using (var put = new HttpRequestMessage(HttpMethod.Put, linkUrl.PathAndQuery) { Content = new ByteArrayContent(uploadBytes) })
        {
            put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var sent = await client.SendAsync(put);
            Assert.Equal(System.Net.HttpStatusCode.OK, sent.StatusCode);
            var fileId = JsonNode.Parse(await sent.Content.ReadAsStringAsync())!["file_id"]!.GetValue<string>();
            await ChangeAsync("oadm_files_delete", new JsonObject { ["file_ids"] = new JsonArray(fileId) });
        }

        Assert.Equal(
            ["oadm_devices_add_preview", "oadm_devices_add_run", "oadm_devices_credentials_preview", "oadm_devices_credentials_run", "oadm_tags_assign_preview", "oadm_tags_assign_run",
                "oadm_rules_save_preview", "oadm_rules_save_run", "oadm_rules_delete_preview", "oadm_rules_delete_run", "oadm_tasks_cancel_preview", "oadm_tasks_cancel_run",
                "oadm_task_preview", "oadm_task_run", "oadm_files_delete_preview", "oadm_files_delete_run"],
            steps);

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries
            .Select(e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail, e.ClientAddress)).ToList();
        var logs = ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);

        // The runs really did their work (the scan read answers of runs that succeeded, not refusals).
        Assert.Contains(audit, e => e.Contains("canary-runs", StringComparison.Ordinal));
        Assert.Contains("\"added\":1", answers.ToString(), StringComparison.Ordinal);

        var token = linkUrl.AbsolutePath[(linkUrl.AbsolutePath.LastIndexOf('/') + 1)..];
        string[] secrets = [DevicePassword, ListPassword, server.Host.AdminToken, key, AuthTokenStore.HashOf(key), key["oadm_".Length..], AuthTokenStore.HashOf(key)];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, answers.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(audit, e => e.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }

        // The upload token is in the link answer by design; the audit and the server log never hold it.
        Assert.DoesNotContain(audit, e => e.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(token, logs, StringComparison.Ordinal);
    }

    private sealed class OwnCards : IServerNetworkInterfaces
    {
        public IReadOnlyList<ServerNetworkInterface> List()
        {
            var ip = IPAddress.Parse("10.4.1.254");
            return [new ServerNetworkInterface("eth0", "eth0", "eth0", [ip], true, false) { PrefixLengths = new Dictionary<IPAddress, int> { [ip] = 24 } }];
        }
    }

    private sealed class ScanAction : IActionPlugin
    {
        public string Id => "test.scan.notify";

        public string DisplayName => "Notify";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Text("message", "Message", required: true)];

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A task that waits until it is cancelled (no timing: it never ends by itself).</summary>
    private sealed class WaitingTask : ITaskPlugin
    {
        public const string PluginId = "test.scan.waiting";

        public string Id => PluginId;

        public string DisplayName => "Waits";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    internal static string ReadLogs(string directory)
    {
        var text = new StringBuilder();
        foreach (var file in Directory.Exists(directory) ? Directory.GetFiles(directory) : [])
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }
}
