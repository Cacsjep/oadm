using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Server.Mcp;
using Oadm.Server.Tools;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Files and secrets of the agents (docs/spec/mcp.md "Files", "Secrets"): download links of a tool's file and of a task's
/// result file (once, bound to the key, 10 minutes, the size, the rights of the caller), and the secret channel (a secret
/// link once, bound to the key and the preview, 10 minutes, its size, never in an answer, a log line or the audit log).
/// </summary>
public sealed class McpTransferTests
{
    private const string Canary = "canary-secret-7Qx!e2 \"ü\"";

    private static async Task<McpTestServer> StartAsync(Action<IServiceCollection>? services = null)
    {
        var server = await McpTestServer.StartAsync(configureServices: services);
        var registry = server.Host.Get<PluginRegistry>();
        Assert.True(registry.RegisterTaskPlugin(new ResultFileTask(), new PluginOrigin(ResultFileTask.PluginId, "1.0.0", null)));
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None); // its permission for the roles
        return server;
    }

    private static async Task<HttpResponseMessage> SendAsync(McpTestServer server, HttpMethod method, string key, string url, byte[]? body = null)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(method, new Uri(url).PathAndQuery);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>Runs the result task on <paramref name="deviceId"/> with <paramref name="key"/> and waits until it ended; its task id.</summary>
    private static async Task<string> RunResultTaskAsync(McpTestServer server, string key, Guid deviceId)
    {
        var preview = await server.ToolAsync(key, "oadm_task_preview", new JsonObject
        {
            ["task"] = ResultFileTask.PluginId,
            ["filter"] = new JsonObject { ["ids"] = new JsonArray(deviceId.ToString()) },
            ["input"] = new JsonObject(),
        });
        var run = await server.ToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        var batch = run["batch"]!.GetValue<string>();
        string? id = null;
        await Eventually.TrueAsync(async () =>
        {
            var tasks = (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Where(t => t.BatchId == batch).ToList();
            id = tasks.FirstOrDefault(t => t.State is Proto.TaskState.Done or Proto.TaskState.Failed)?.Id;
            return id is not null;
        }, "the result task ended");
        return id!;
    }

    [Fact]
    public async Task ATaskResultFileGoesToItsOwnerThroughALinkOnceAndNeverToAnotherUserOrGroup()
    {
        await using var server = await StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.61.0.1", 6101);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.61.0.2", 6102);
        var role = await GroupCallers.EverythingRoleAsync(server.Host);
        var owner = await server.CreateKeyAsync(name: "owner", roleId: role.ToString());
        var other = await server.CreateKeyAsync(name: "other", roleId: role.ToString());
        var group = await GroupCallers.GroupAsync(server.Host, "Second", new Proto.DeviceGroupRules { Serials = { FakeSerials.Make(6102) } });
        var grouped = await server.CreateKeyAsync(name: "grouped", roleId: role.ToString(), groupId: group.ToString());
        var taskId = await RunResultTaskAsync(server, owner, device.Id);

        // The task offers its file to its owner; the bytes come only through a link.
        var details = await server.ToolAsync(owner, "oadm_tasks_get", new JsonObject { ["task_id"] = taskId });
        var file = Assert.IsType<JsonObject>(details["result_file"]);
        Assert.Equal("config-" + FakeSerials.Make(6101) + ".txt", file["name"]!.GetValue<string>());
        Assert.Equal(ResultFileTask.Content.Length, file["size"]!.GetValue<long>());
        Assert.Equal("text/plain", file["content_type"]!.GetValue<string>());
        Assert.Contains(details["log"]!.AsArray(), l => l!["message"]!.GetValue<string>().StartsWith("Kept the result file", StringComparison.Ordinal));

        var link = await server.ToolAsync(owner, ToolFiles.LinkTool, new JsonObject { ["file_ref"] = file["file_ref"]!.GetValue<string>() });
        Assert.Equal(ResultFileTask.Content.Length, link["size"]!.GetValue<long>());
        Assert.Contains("curl --cacert oadm-ca.pem", link["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var url = link["url"]!.GetValue<string>();
        using (var foreign = await SendAsync(server, HttpMethod.Get, other, url))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); // another key's link answers like an unknown one and stays the owner's
        }

        using (var fetched = await SendAsync(server, HttpMethod.Get, owner, url))
        {
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
            Assert.Equal(ResultFileTask.Content, await fetched.Content.ReadAsByteArrayAsync());
            Assert.Equal("text/plain", fetched.Content.Headers.ContentType?.MediaType);
        }

        using (var twice = await SendAsync(server, HttpMethod.Get, owner, url))
        {
            Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
        }

        // By task id: the owner gets a new link; another user and a group without the device get none (and no reference).
        var byTask = await server.ToolAsync(owner, ToolFiles.LinkTool, new JsonObject { ["task_id"] = taskId });
        using (var again = await SendAsync(server, HttpMethod.Get, owner, byTask["url"]!.GetValue<string>()))
        {
            Assert.Equal(ResultFileTask.Content, await again.Content.ReadAsByteArrayAsync());
        }

        foreach (var refused in new[] { other, grouped })
        {
            var answer = await server.CallToolAsync(refused, ToolFiles.LinkTool, new JsonObject { ["task_id"] = taskId });
            Assert.True(answer["isError"]!.GetValue<bool>(), answer.ToJsonString());
            Assert.Contains(Oadm.Server.Tasks.TaskGrpcService.NoResultFile, answer.ToJsonString(), StringComparison.Ordinal);
        }

        var seen = await server.CallToolAsync(other, "oadm_tasks_get", new JsonObject { ["task_id"] = taskId });
        Assert.Null(seen["structuredContent"]?["result_file"]);

        var both = await server.CallToolAsync(owner, ToolFiles.LinkTool, new JsonObject { ["task_id"] = taskId, ["file_ref"] = "x" });
        Assert.True(both["isError"]!.GetValue<bool>());
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Equal(2, audit.Count(e => e.Action == AuditActions.FileDownloaded && e.UserName == AccessKeyStore.AgentNameOf("owner")
            && e.Target == "config-" + FakeSerials.Make(6101) + ".txt" && e.Detail.Contains("307,200 bytes", StringComparison.Ordinal)));
    }

    [Fact]
    public void FileReferencesAndLinksEndAfterTenMinutesAndBelongToTheirCaller()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handles = new ToolHandles(time);
        var file = new PlannedDownload("a.csv", "text/csv", (_, _) => Task.FromResult<Stream>(new MemoryStream([1]))) { Size = 1 };
        var reference = handles.AddOffer("key-a", file);
        Assert.Null(handles.FindOffer("key-b", reference));
        Assert.Same(file, handles.FindOffer("key-a", reference));
        Assert.Same(file, handles.FindOffer("key-a", reference)); // a reference makes several links
        Assert.Null(handles.TakeDownload("key-a", reference)); // a reference is no link

        var link = handles.AddDownload("key-a", file);
        Assert.Null(handles.TakeDownload("key-b", link));
        Assert.Same(file, handles.TakeDownload("key-a", link));
        Assert.Null(handles.TakeDownload("key-a", link)); // once

        var late = handles.AddDownload("key-a", file);
        time.Advance(ToolHandles.Lifetime);
        Assert.Null(handles.TakeDownload("key-a", late));
        Assert.Null(handles.FindOffer("key-a", reference));
    }

    [Fact]
    public async Task ATaskKeepsOneResultFileOfAtMost64MegabytesForADay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-results-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var files = new TaskResultFiles(new OadmPaths(folder), time) { MaxBytes = 1024 * 1024 };
            var task = Guid.NewGuid();
            var owner = Guid.NewGuid();
            await files.SaveAsync(task, owner, "../a\\b:c.txt", "text/plain", new MemoryStream([1, 2, 3]), CancellationToken.None);
            var second = await files.SaveAsync(task, owner, "second.txt", "text/plain", new MemoryStream([4, 5]), CancellationToken.None);
            Assert.Equal(2, second.Size);
            Assert.Equal("second.txt", files.Find(task)!.FileName);
            await using (var read = files.Open(task)!)
            {
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy);
                Assert.Equal([4, 5], copy.ToArray());
            }

            var big = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => files.SaveAsync(big, owner, "big.bin", "application/octet-stream",
                new ZeroStream(files.MaxBytes + 1), CancellationToken.None));
            Assert.Equal(64L * 1024 * 1024, new TaskResultFiles(new OadmPaths(folder)).MaxBytes);
            Assert.Null(files.Find(big));

            time.Advance(TaskResultFiles.Retention);
            Assert.Null(files.Find(task));
            Assert.Null(files.Open(task));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheDeviceListExportIsACsvFileOnlyForARoleThatMayExport()
    {
        await using var server = await StartAsync();
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.62.0.1", 6201);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.62.0.2", 6202);
        var key = await server.CreateKeyAsync();
        var export = await server.ToolAsync(key, "oadm_devices_export", new JsonObject { ["filter"] = new JsonObject { ["search"] = "10.62.0." } });
        Assert.Equal(2, export["devices"]!.GetValue<int>());
        var file = export["file"]!;
        Assert.Equal("text/csv", file["content_type"]!.GetValue<string>());
        var link = await server.ToolAsync(key, ToolFiles.LinkTool, new JsonObject { ["file_ref"] = file["file_ref"]!.GetValue<string>() });
        using var fetched = await SendAsync(server, HttpMethod.Get, key, link["url"]!.GetValue<string>());
        var csv = Encoding.UTF8.GetString(await fetched.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("﻿MAC address,Status,Address", csv, StringComparison.Ordinal);
        Assert.Contains("10.62.0.1", csv, StringComparison.Ordinal);
        Assert.Contains("10.62.0.2", csv, StringComparison.Ordinal);

        var compliance = await server.ToolAsync(key, "oadm_compliance_export", new JsonObject { ["filter"] = new JsonObject() });
        Assert.Equal(2, compliance["devices"]!.GetValue<int>());

        var saved = await new Proto.RoleService.RoleServiceClient(server.Host.Invoker).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Name = "No export", Permissions = { HostPermissions.TagsAssign } } },
        });
        var noExport = await server.CreateKeyAsync(name: "no-export", roleId: saved.Roles.Single(r => r.Name == "No export").Id);
        Assert.DoesNotContain("oadm_devices_export", await server.ToolNamesAsync(noExport));
    }

    [Fact]
    public async Task TheLogsAreExportedLikeOnTheLogsPageAndTheirToolsTakeAnEndTime()
    {
        await using var server = await StartAsync();
        var logs = server.Host.Get<OadmPaths>().LogsDirectory;
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "oadm-server-20261017.log"), """
            2026-10-17 08:00:00.000 +00:00 [INF] Oadm.Server.Tests: export early
            2026-10-17 09:00:00.000 +00:00 [ERR] Oadm.Server.Tests: export late

            """);
        var key = await server.CreateKeyAsync();

        // until: lines and entries up to that time.
        var early = await server.ToolAsync(key, "oadm_logs_search", new JsonObject { ["text"] = "export", ["until"] = "2026-10-17T08:30:00Z" });
        Assert.Equal(["export early"], early["lines"]!.AsArray().Select(l => l!["message"]!.GetValue<string>()));
        var range = await server.ToolAsync(key, "oadm_logs_search", new JsonObject { ["text"] = "export", ["since"] = "2026-10-17T08:30:00Z", ["until"] = "2026-10-17T10:00:00Z" });
        Assert.Equal(["export late"], range["lines"]!.AsArray().Select(l => l!["message"]!.GetValue<string>()));
        Assert.Empty((await server.ToolAsync(key, "oadm_logs_audit", new JsonObject { ["until"] = "2000-01-01T00:00:00Z" }))["entries"]!.AsArray());
        Assert.NotEmpty((await server.ToolAsync(key, "oadm_logs_audit", new JsonObject { ["until"] = "2999-01-01T00:00:00Z" }))["entries"]!.AsArray());

        var export = await server.ToolAsync(key, "oadm_logs_export", new JsonObject { ["text"] = "export", ["level"] = "Error" });
        Assert.Equal((1, false), (export["entries"]!.GetValue<int>(), export["more"]!.GetValue<bool>()));
        var file = export["file"]!;
        Assert.Equal("text/csv", file["content_type"]!.GetValue<string>());
        var link = await server.ToolAsync(key, ToolFiles.LinkTool, new JsonObject { ["file_ref"] = file["file_ref"]!.GetValue<string>() });
        using (var fetched = await SendAsync(server, HttpMethod.Get, key, link["url"]!.GetValue<string>()))
        {
            var csv = Encoding.UTF8.GetString(await fetched.Content.ReadAsByteArrayAsync());
            Assert.StartsWith("\uFEFFTime,Time (ISO 8601),Level,Source,Message,Exception\r\n2026-10-17 09:00:00,", csv, StringComparison.Ordinal); // UTC for agents
            Assert.Contains("export late", csv, StringComparison.Ordinal);
        }

        var audit = await server.ToolAsync(key, "oadm_logs_audit_export", new JsonObject { ["format"] = "pdf" });
        Assert.True(audit["entries"]!.GetValue<int>() > 0);
        Assert.Equal("application/pdf", audit["file"]!["content_type"]!.GetValue<string>());
        var pdfLink = await server.ToolAsync(key, ToolFiles.LinkTool, new JsonObject { ["file_ref"] = audit["file"]!["file_ref"]!.GetValue<string>() });
        using (var fetched = await SendAsync(server, HttpMethod.Get, key, pdfLink["url"]!.GetValue<string>()))
        {
            Assert.Equal("%PDF", Encoding.ASCII.GetString((await fetched.Content.ReadAsByteArrayAsync())[..4]));
        }

        // One "Exported log" per file, by the key.
        var exported = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest { Search = AuditActions.LogExported })).Entries;
        Assert.Equal(2, exported.Count);
        Assert.All(exported, e => Assert.Equal(AccessKeyStore.AgentNameOf("agent"), e.UserName));

        // Without Export logs the tools are not offered.
        var saved = await new Proto.RoleService.RoleServiceClient(server.Host.Invoker).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Name = "Log reader", Permissions = { HostPermissions.LogsServer } } },
        });
        var reader = await server.CreateKeyAsync(name: "log-reader", roleId: saved.Roles.Single(r => r.Name == "Log reader").Id);
        var names = await server.ToolNamesAsync(reader);
        Assert.Contains("oadm_logs_search", names);
        Assert.DoesNotContain("oadm_logs_export", names);
        Assert.DoesNotContain("oadm_logs_audit_export", names);
    }

    [Fact]
    public async Task AFileIsMadeWithTheRightsOfTheKeyWhenItsLinkIsFetchedNotWhenItWasOffered()
    {
        await using var server = await StartAsync();
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.63.0.1", 6301);
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Name = "Compliance reader", Permissions = { HostPermissions.ComplianceView } } },
        });
        var role = saved.Roles.Single(r => r.Name == "Compliance reader");
        var key = await server.CreateKeyAsync(name: "compliance-reader", roleId: role.Id);
        var export = await server.ToolAsync(key, "oadm_compliance_export", new JsonObject { ["filter"] = new JsonObject() });
        var link = await server.ToolAsync(key, ToolFiles.LinkTool, new JsonObject { ["file_ref"] = export["file"]!["file_ref"]!.GetValue<string>() });

        // The administrator takes the right away before the link is fetched: the file is no longer made for this key.
        await roles.SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = role.Id, Name = role.Name, Permissions = { HostPermissions.TagsAssign } } },
        });
        using var fetched = await SendAsync(server, HttpMethod.Get, key, link["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, fetched.StatusCode);
    }

    [Fact]
    public async Task ASecretGoesThroughItsLinkOnceToItsRunAndNeverIntoAnAnswerALogLineOrTheAuditLog()
    {
        SecretDemoTools? demo = null;
        await using var server = await StartAsync(services => services.AddSingleton<IToolSource>(sp => demo = new SecretDemoTools(sp.GetRequiredService<ToolHandles>())));
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var other = await server.CreateKeyAsync(name: "other");
        var answers = new StringBuilder();

        var preview = await server.ToolAsync(key, SecretDemoTools.Preview, new JsonObject { ["name"] = "Alarm desk" });
        answers.Append(preview.ToJsonString());
        Assert.Equal("password", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        var link = preview["secret_links"]!["password"]!;
        var url = link["url"]!.GetValue<string>();
        Assert.StartsWith("https://localhost:5082/secrets/", url, StringComparison.Ordinal);
        Assert.Contains("read -rs", link["command"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("--data-binary @-", link["command"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("-MaskInput", link["command_powershell"]!.GetValue<string>(), StringComparison.Ordinal);
        var handle = preview["handle"]!.GetValue<string>();

        // Without the secret the run is refused and its handle stays.
        var early = await server.CallToolAsync(key, SecretDemoTools.Run, new JsonObject { ["handle"] = handle });
        Assert.True(early["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the password.", early.ToJsonString(), StringComparison.Ordinal);
        answers.Append(early.ToJsonString());

        var value = Encoding.UTF8.GetBytes(Canary + "\n"); // the line end of read and printf is dropped
        using (var foreign = await SendAsync(server, HttpMethod.Put, other, url, value))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); // another key's link answers like an ended one and stays
            answers.Append(await foreign.Content.ReadAsStringAsync());
        }

        using (var large = await SendAsync(server, HttpMethod.Put, key, url, new byte[ToolSecrets.MaxBytes + 10]))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        }

        using (var get = await SendAsync(server, HttpMethod.Get, key, url))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        }

        using (var set = await SendAsync(server, HttpMethod.Put, key, url, value))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var text = await set.Content.ReadAsStringAsync();
            answers.Append(text);
            Assert.Equal("password", JsonNode.Parse(text)!["secret"]!.GetValue<string>());
        }

        using (var twice = await SendAsync(server, HttpMethod.Put, key, url, value))
        {
            Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode); // once
        }

        var run = await server.ToolAsync(key, SecretDemoTools.Run, new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        Assert.True(run["has_password"]!.GetValue<bool>());
        Assert.Equal(Canary, Assert.Single(demo!.Received));

        // The value is in no answer, no audit entry and no log line; the audit says who set which secret.
        answers.Append((await server.PostAsync(key, McpTestServer.Request("tools/list"))).Body?.ToJsonString());
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.SecretSet && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot") && e.Target == "password"
            && e.Detail == "For Save a recipient");
        var logs = McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);
        foreach (var part in new[] { Canary, Canary[..12] })
        {
            Assert.DoesNotContain(part, answers.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(audit, e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail).Contains(part, StringComparison.Ordinal));
            Assert.DoesNotContain(part, logs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASecretIsBoundToItsCallerAndPreviewTakenOnceAndEndsWithThePreview()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var protector = new CredentialProtector(new byte[CredentialProtector.KeySize]);
        var secrets = new ToolSecrets(time, protector);
        var slot = new ToolSecretSlot("password", "password", MaxLength: 64);
        var tokens = secrets.Declare("key-a", "handle-1", "run", [slot]);

        Assert.Equal(ToolSecrets.LinkEnded, secrets.SetByLink("key-b", tokens["password"], "x").Error); // another caller's
        Assert.Equal(ToolSecrets.TooLong, secrets.SetByLink("key-a", tokens["password"], new string('a', 65)).Error);
        Assert.Equal("Enter the password.", secrets.SetByLink("key-a", tokens["password"], string.Empty).Error);
        Assert.Equal([slot], secrets.Missing("key-a", "handle-1", "run"));
        Assert.Empty(secrets.Missing("key-a", "handle-1", "another run")); // another run tool's handle has no slots
        Assert.Null(secrets.SetByLink("key-a", tokens["password"], "s3cret").Error);
        Assert.Equal(ToolSecrets.LinkEnded, secrets.SetByLink("key-a", tokens["password"], "again").Error); // the link once
        Assert.Equal(ToolSecrets.NoSuchSlot, secrets.Set("key-b", "handle-1", "password", "x"));
        Assert.Null(secrets.Take("key-b", "handle-1", "run"));
        Assert.Null(secrets.Take("key-a", "handle-1", "another run"));
        Assert.Equal("s3cret", secrets.Take("key-a", "handle-1", "run")!["password"]);
        Assert.Null(secrets.Take("key-a", "handle-1", "run")); // once

        var late = secrets.Declare("key-a", "handle-2", "run", [slot]);
        Assert.Null(secrets.Set("key-a", "handle-2", "password", "typed"));
        Assert.Equal(ToolSecrets.TooLong, secrets.Set("key-a", "handle-2", "password", new string('b', ToolSecrets.MaxBytes + 1)));
        time.Advance(ToolHandles.Lifetime);
        Assert.Equal(ToolSecrets.LinkEnded, secrets.SetByLink("key-a", late["password"], "x").Error);
        Assert.Null(secrets.Take("key-a", "handle-2", "run"));
        Assert.Empty(secrets.SlotsOf("key-a", "handle-2"));
    }

    /// <summary>A stream of <paramref name="length"/> zero bytes, made while it is read.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
