using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

using Microsoft.Extensions.DependencyInjection;

namespace Oadm.Server.Tests;

/// <summary>Recipients through gRPC EventService: catalog, list, save, delete, test, roles and the audit.</summary>
public sealed class RecipientServiceTests
{
    private const string TypeId = "test.hook";

    private sealed class HookAction : IActionPlugin
    {
        public string? FailWith { get; set; }

        public string Id => "test.hook-action";

        public string DisplayName => "Call hook";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<RecipientType> RecipientTypes { get; } =
        [
            new(TypeId, "Hook", "send", [ActionField.Text("url", "URL", required: true), ActionField.Password("token", "Token"), ActionField.Text("relay", "Relay") with { Format = ActionFieldFormat.Host }], "test.hook-action") { Target = "{url}" },
        ];

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Recipient("recipient", "Recipient", [TypeId])];

        public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) =>
            settings.TryGetProperty("url", out var url) && (url.GetString() == "bad" || url.GetString()!.Contains('@', StringComparison.Ordinal))
                ? new Dictionary<string, string> { ["url"] = "Enter a URL." }
                : new Dictionary<string, string>();

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) =>
            FailWith is { } reason ? throw new InvalidOperationException(reason) : Task.CompletedTask;
    }

    private static async Task<(TestServerHost Host, Proto.EventService.EventServiceClient Events, HookAction Action)> StartAsync()
    {
        var host = await TestServerHost.StartAsync();
        var action = new HookAction();
        host.Get<PluginRegistry>().RegisterActionPlugin(action, new PluginOrigin("test.hook", "1.0", null) { DisplayName = "Hook" });
        return (host, new Proto.EventService.EventServiceClient(host.Invoker), action);
    }

    private static Proto.SaveRecipientRequest Request(string name, string url = "https://hooks.example.com", string? token = "t0ken", string id = "") => new()
    {
        Id = id,
        Name = name,
        TypeId = TypeId,
        SettingsJson = (token is null ? new JsonObject { ["url"] = url } : new JsonObject { ["url"] = url, ["token"] = token }).ToJsonString(),
    };

    private static async Task<string> SaveRuleAsync(Proto.EventService.EventServiceClient events, string name, string recipientId)
    {
        var rule = new RuleDefinition
        {
            Name = name,
            Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.Unreachable, Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
            Actions = [new RuleAction { ActionId = "test.hook-action", Settings = new JsonObject { ["recipient"] = recipientId } }],
        };
        return (await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = rule.ToJson() })).Summary.Id;
    }

    [Fact]
    public async Task TheCatalogDeclaresTypesAndARecipientNeverComesBackWithItsPassword()
    {
        var (host, events, _) = await StartAsync();
        await using (host)
        {
            var catalog = await events.GetCatalogAsync(new Proto.Empty());
            var type = Assert.Single(catalog.RecipientTypes, t => t.Id == TypeId);
            Assert.Equal(Proto.ActionTextFormat.Host, type.Fields.Single(f => f.Key == "relay").Format); // the form checks it while the user types
            Assert.Equal("Hook", type.DisplayName);
            Assert.True(type.Available);
            Assert.Equal(Proto.ActionFieldKind.ActionFieldPassword, type.Fields.Single(f => f.Key == "token").Kind);
            var field = Assert.Single(catalog.Actions.Single(a => a.Id == "test.hook-action").Fields);
            Assert.Equal(Proto.ActionFieldKind.ActionFieldRecipient, field.Kind);
            Assert.Equal([TypeId], field.RecipientTypes);

            var saved = await events.SaveRecipientAsync(Request("Teams"));
            Assert.Equal("https://hooks.example.com", saved.Target);
            Assert.DoesNotContain("t0ken", saved.ToString(), StringComparison.Ordinal);
            Assert.Equal(["token"], saved.StoredSecretFields);

            var listed = Assert.Single((await events.ListRecipientsAsync(new Proto.Empty())).Recipients);
            Assert.DoesNotContain("t0ken", listed.ToString(), StringComparison.Ordinal);

            var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
            Assert.Contains(audit, e => e.Action == AuditActions.RecipientCreated && e.Target == "Teams");
            Assert.DoesNotContain(audit, e => e.Detail.Contains("t0ken", StringComparison.Ordinal));

            await events.SaveRecipientAsync(Request("Teams", token: null, id: saved.Id));
            Assert.Contains((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.RecipientChanged && e.Target == "Teams");
        }
    }

    [Fact]
    public async Task ARefusalNamesItsField()
    {
        var (host, events, _) = await StartAsync();
        await using (host)
        {
            var bad = await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRecipientAsync(Request("Teams", "bad")));
            Assert.Equal(StatusCode.InvalidArgument, bad.StatusCode);
            Assert.Equal("Enter a URL.", bad.Status.Detail);
            Assert.Equal("url", bad.Trailers.GetValue(Proto.RecipientErrors.FieldTrailer));

            await events.SaveRecipientAsync(Request("Teams"));
            var taken = await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRecipientAsync(Request("teams")));
            Assert.Equal("name", taken.Trailers.GetValue(Proto.RecipientErrors.FieldTrailer));

            var gone = await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRecipientAsync(Request("Other", id: Guid.NewGuid().ToString())));
            Assert.Equal(StatusCode.NotFound, gone.StatusCode);
        }
    }

    [Fact]
    public async Task DeleteIsRefusedWithTheRuleNamesWhileARuleUsesIt()
    {
        var (host, events, _) = await StartAsync();
        await using (host)
        {
            var saved = await events.SaveRecipientAsync(Request("Teams"));
            var rule = await SaveRuleAsync(events, "Gate alarm", saved.Id);
            Assert.Equal(["Gate alarm"], Assert.Single((await events.ListRecipientsAsync(new Proto.Empty())).Recipients).UsedBy);

            var refused = await Assert.ThrowsAsync<RpcException>(async () => await events.DeleteRecipientAsync(new Proto.RecipientId { Id = saved.Id }));
            Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
            Assert.Equal("Used by Gate alarm. Change those rules first.", refused.Status.Detail);

            await events.DeleteRuleAsync(new Proto.RuleId { Id = rule });
            await events.DeleteRecipientAsync(new Proto.RecipientId { Id = saved.Id });
            Assert.Empty((await events.ListRecipientsAsync(new Proto.Empty())).Recipients);
            Assert.Contains((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.RecipientDeleted && e.Target == "Teams");

            var gone = await Assert.ThrowsAsync<RpcException>(async () => await events.DeleteRecipientAsync(new Proto.RecipientId { Id = saved.Id }));
            Assert.Equal(StatusCode.NotFound, gone.StatusCode);
        }
    }

    [Fact]
    public async Task TestAnswersWithTheReasonAndIsAudited()
    {
        var (host, events, action) = await StartAsync();
        await using (host)
        {
            var sent = await events.TestRecipientAsync(Request("Teams"));
            Assert.True(sent.Ok, sent.Message);

            action.FailWith = "The hook said no";
            var failed = await events.TestRecipientAsync(Request("Teams"));
            Assert.False(failed.Ok);
            Assert.Equal("The hook said no", failed.Message);

            var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
            Assert.Contains(audit, e => e.Action == AuditActions.RecipientTested && e.Target == "Teams" && e.Detail.Contains("The hook said no", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AChangedTargetIsRefusedBelowThePasswordOnTestAndSave()
    {
        var (host, events, _) = await StartAsync();
        await using (host)
        {
            var saved = await events.SaveRecipientAsync(Request("Teams"));
            var changed = Request("Teams", "https://evil.example.com", token: null, id: saved.Id);

            var test = await Assert.ThrowsAsync<RpcException>(async () => await events.TestRecipientAsync(changed));
            var save = await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRecipientAsync(changed));

            foreach (var refused in new[] { test, save })
            {
                Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
                Assert.Equal("Enter the password again. The target changed.", refused.Status.Detail);
                Assert.Equal("token", refused.Trailers.GetValue(Proto.RecipientErrors.FieldTrailer));
            }

            var same = Request("Teams", token: null, id: saved.Id);
            Assert.True((await events.TestRecipientAsync(same)).Ok);
            await events.SaveRecipientAsync(same);
        }
    }

    [Fact]
    public async Task ALoginInAURLOfARefusedTestStaysOutOfTheAudit()
    {
        var (host, events, _) = await StartAsync();
        await using (host)
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await events.TestRecipientAsync(Request("Teams", "https://user:s3cret@hooks.example.com")));
            Assert.Equal("url", refused.Trailers.GetValue(Proto.RecipientErrors.FieldTrailer));

            var entry = Assert.Single((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.RecipientTested);
            Assert.Equal("Failed to Hook · Enter a URL.", entry.Detail);
        }
    }

    private sealed class ListedCertificates : IRecipientCertificates
    {
        public Task<IReadOnlyList<ActionChoice>> ClientCertificatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ActionChoice>>([new ActionChoice("local:1", "Key")]);

        public Task<IReadOnlyList<ActionChoice>> TrustedCertificatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ActionChoice>>([]);

        public Task<IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>?> LoadAsync(string id, bool withKey, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>?>([]);
    }

    [Fact]
    public async Task ACertificateOutsideTheOfferedListIsRefusedAndTheTestNamesTheOneItUses()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Get<PluginRegistry>().RegisterActionPlugin(new SessionAction(), new PluginOrigin("test.session", "1.0", null) { DisplayName = "Session" });
        host.Get<PluginServiceRegistry>().For("test.pki").Offer<IRecipientCertificates>(new ListedCertificates());
        var events = new Proto.EventService.EventServiceClient(host.Invoker);
        Proto.SaveRecipientRequest Using(string certificate) => new() { Name = "Broker", TypeId = SessionAction.SessionType, SettingsJson = new JsonObject { ["host"] = "broker", ["client"] = certificate }.ToJsonString() };

        foreach (var refused in new[]
        {
            await Assert.ThrowsAsync<RpcException>(async () => await events.TestRecipientAsync(Using("local:other"))),
            await Assert.ThrowsAsync<RpcException>(async () => await events.SaveRecipientAsync(Using("local:other"))),
        })
        {
            Assert.Equal(RecipientSessionTexts.CertificateGone, refused.Status.Detail);
            Assert.Equal("client", refused.Trailers.GetValue(Proto.RecipientErrors.FieldTrailer));
        }

        Assert.True((await events.TestRecipientAsync(Using("local:1"))).Ok);
        await events.SaveRecipientAsync(Using("local:1"));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.RecipientTested && e.Detail == "Connected to broker · Client certificate local:1");
    }

    /// <summary>Holds until the stop cancels it, then asks for its session as a slow action does.</summary>
    private sealed class StopAction : IActionPlugin
    {
        public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Exception?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "test.stop-action";

        public string DisplayName => "Run into the stop";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Recipient("recipient", "Recipient", [SessionAction.SessionType])];

        public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) => new Dictionary<string, string>();

        public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
        {
            Running.SetResult();
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            try
            {
                await ctx.SessionAsync<ConnectedSession>("recipient", CancellationToken.None);
                Result.SetResult(null);
            }
#pragma warning disable CA1031 // The test reports what the action got.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Result.SetResult(ex);
            }
        }
    }

    [Fact]
    public async Task ARunningActionKeepsItsSessionDuringAStop()
    {
        var host = await TestServerHost.StartAsync(configureServices: s => s.AddSingleton(new ActionRunnerOptions { StopWait = TimeSpan.FromMinutes(5) }));
        var action = new StopAction();
        host.Get<PluginRegistry>().RegisterActionPlugin(new SessionAction(), new PluginOrigin("test.session", "1.0", null) { DisplayName = "Session" });
        host.Get<PluginRegistry>().RegisterActionPlugin(action, new PluginOrigin("test.stop", "1.0", null) { DisplayName = "Stop" });
        var events = new Proto.EventService.EventServiceClient(host.Invoker);
        var saved = await events.SaveRecipientAsync(new Proto.SaveRecipientRequest { Name = "Broker", TypeId = SessionAction.SessionType, SettingsJson = """{"host":"broker"}""" });
        var rule = new RuleDefinition { Id = Guid.NewGuid(), Name = "Stop", Actions = [new RuleAction { ActionId = action.Id, Settings = new JsonObject { ["recipient"] = saved.Id } }] };
        var trigger = new ActionTrigger(string.Empty, "Test", null, null, null, true, DateTimeOffset.UtcNow, new Dictionary<string, string>());

        host.Get<ActionRunner>().Dispatch(new RuleEdgeJob(rule, false, ActionEdge.Fired, null, true, trigger));
        await action.Running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await host.DisposeAsync();

        Assert.Null(await action.Result.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private sealed class ConnectedSession : IRecipientSession
    {
        public event EventHandler<RecipientDroppedEventArgs>? Dropped
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConnectedSessions : IRecipientSessionFactory
    {
        public IRecipientSession Create(RecipientSessionRequest request) => new ConnectedSession();
    }

    private sealed class SessionAction : IActionPlugin
    {
        public const string SessionType = "test.session";

        public string Id => "test.session-action";

        public string DisplayName => "Use session";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<RecipientType> RecipientTypes { get; } =
            [new(SessionType, "Session", "send", [ActionField.Text("host", "Host", required: true), ActionField.ClientCertificate("client", "Client certificate")], "test.session-action") { Target = "{host}", Sessions = new ConnectedSessions() }];

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Recipient("recipient", "Recipient", [SessionType])];

        public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) => new Dictionary<string, string>();

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task TestOfAPersistentTypeConnectsAndIsAuditedSo()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Get<PluginRegistry>().RegisterActionPlugin(new SessionAction(), new PluginOrigin("test.session", "1.0", null) { DisplayName = "Session" });
        var events = new Proto.EventService.EventServiceClient(host.Invoker);

        var reply = await events.TestRecipientAsync(new Proto.SaveRecipientRequest { Name = "Broker", TypeId = SessionAction.SessionType, SettingsJson = """{"host":"broker.example.com"}""" });

        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(RecipientSessionTexts.Connected, reply.Message);
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.RecipientTested && e.Target == "Broker" && e.Detail == "Connected to broker.example.com");
    }

    [Fact]
    public async Task TheRightsAreThoseOfTheEventRules()
    {
        Assert.Equal(AccessPolicy.For("/oadm.v1.EventService/ListRules"), AccessPolicy.For("/oadm.v1.EventService/ListRecipients"));
        foreach (var method in new[] { "SaveRecipient", "DeleteRecipient", "TestRecipient" })
        {
            Assert.Equal(AccessPolicy.For("/oadm.v1.EventService/SaveRule"), AccessPolicy.For("/oadm.v1.EventService/" + method));
        }

        var (host, events, _) = await StartAsync();
        await using (host)
        {
            await events.SaveRecipientAsync(Request("Teams"));
            var op = new Proto.EventService.EventServiceClient(await host.InvokerForUserAsync("tech1", UserRole.Operator));

            Assert.Single((await op.ListRecipientsAsync(new Proto.Empty())).Recipients);
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await op.SaveRecipientAsync(Request("Other")));
            Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
            refused = await Assert.ThrowsAsync<RpcException>(async () => await op.TestRecipientAsync(Request("Other")));
            Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        }
    }
}
