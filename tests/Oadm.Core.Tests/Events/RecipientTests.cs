using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Plugins.Notifications;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;

namespace Oadm.Core.Tests.Events;

/// <summary>Recipients (docs/spec/recipients.md): the store, the merge in a run and in a test, rules without a recipient.</summary>
public sealed class RecipientTests
{
    private const string TargetType = "test.target";
    private const string OtherType = "test.other";

    private static readonly ActionField[] TargetFields =
    [
        ActionField.Text("url", "URL", required: true),
        ActionField.Password("password", "Password"),
    ];

    /// <summary>An action that sends to a recipient and records what it got.</summary>
    private sealed class SendAction : IActionPlugin
    {
        public ConcurrentQueue<ActionInvocation> Runs { get; } = new();

        public string Id => "test.send";

        public string DisplayName => "Send it";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public bool SupportsTurnOff => true;

        public IReadOnlyList<RecipientType> RecipientTypes { get; } =
        [
            new(TargetType, "Target", null, TargetFields, "test.send") { Target = "{url}" },
            new(OtherType, "Other", null, [ActionField.Text("url", "URL", required: true)], "test.send"),
        ];

        public IReadOnlyList<ActionField> Fields { get; } =
        [
            ActionField.Recipient("recipient", "Recipient", [TargetType]),
            ActionField.Text("text", "Text"),
        ];

        public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) =>
            settings.TryGetProperty("url", out var url) && url.GetString() == "bad"
                ? new Dictionary<string, string> { ["url"] = "Enter a good URL.", ["text"] = "Not a field of the recipient." }
                : new Dictionary<string, string>();

        public string Describe(JsonElement settings) => "To " + (settings.TryGetProperty("url", out var url) ? url.GetString() : "nobody");

        public bool RunsOn(ActionEdge edge, JsonElement settings) => edge != ActionEdge.TurnedOff;

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
        {
            Runs.Enqueue(invocation);
            return Task.CompletedTask;
        }
    }

    private static Task<EventRulesRig> RigAsync(IActionPlugin action) =>
        EventRulesRig.StartAsync(beforeStart: rig => rig.Registry.RegisterActionPlugin(action, rig.ActionOrigin));

    private static JsonObject Values(string url, string? password = null) =>
        password is null ? new JsonObject { ["url"] = url } : new JsonObject { ["url"] = url, ["password"] = password };

    private static async Task<StoredRecipient> NewAsync(EventRulesRig rig, string name, string url = "https://a.example.com", string? password = null, string type = TargetType)
    {
        var (saved, created) = await rig.Recipients.SaveAsync(null, name, type, Values(url, password), TargetFields, CancellationToken.None);
        Assert.True(created);
        return saved;
    }

    private static RuleAction Send(StoredRecipient? recipient, JsonObject? extra = null)
    {
        var settings = extra ?? [];
        if (recipient is not null)
        {
            settings["recipient"] = recipient.Id.ToString();
        }

        return new RuleAction { ActionId = "test.send", Settings = settings };
    }

    private static async Task<IReadOnlyList<EventLogLine>> ActionLinesAsync(EventRulesRig rig)
    {
        await rig.Log.FlushAsync(CancellationToken.None);
        var (lines, _) = await rig.Log.ListAsync(0, 0, null, CancellationToken.None);
        return [.. lines.Where(l => l.Kind == EventLogKind.Action)];
    }

    [Fact]
    public async Task ARecipientIsCreatedAndChangedAndItsNameStaysUnique()
    {
        await using var rig = await RigAsync(new SendAction());
        var first = await NewAsync(rig, "  Gate team ");
        Assert.Equal("Gate team", first.Name);
        await NewAsync(rig, "Night");

        var (changed, created) = await rig.Recipients.SaveAsync(first.Id, "Gate", TargetType, Values("https://b.example.com"), TargetFields, CancellationToken.None);
        Assert.False(created);
        Assert.Equal("https://b.example.com", (await rig.Recipients.GetAsync(first.Id, CancellationToken.None))!.Settings["url"]!.ToString());
        Assert.Equal(first.Id, changed.Id);

        var taken = await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Recipients.SaveAsync(null, "NIGHT", TargetType, Values("https://c.example.com"), TargetFields, CancellationToken.None));
        Assert.Equal("A recipient with this name already exists.", taken.Message);
        Assert.Equal(RecipientValidationException.NameField, taken.Field);
        var tooLong = await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Recipients.SaveAsync(null, new string('x', 65), TargetType, Values("https://c.example.com"), TargetFields, CancellationToken.None));
        Assert.Equal("At most 64 characters.", tooLong.Message);
        await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Recipients.SaveAsync(first.Id, "Gate", OtherType, Values("https://c.example.com"), TargetFields, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Recipients.SaveAsync(Guid.NewGuid(), "Gone", TargetType, Values("https://c.example.com"), TargetFields, CancellationToken.None));
    }

    [Fact]
    public async Task ThereAreAtMost200Recipients()
    {
        await using var rig = await RigAsync(new SendAction());
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            for (var i = 0; i < RecipientStore.MaxRecipients; i++)
            {
                db.Recipients.Add(new RecipientEntity { Id = Guid.NewGuid(), Name = "R" + i, TypeId = TargetType });
            }

            await db.SaveChangesAsync();
        }

        var refused = await Assert.ThrowsAsync<RecipientValidationException>(() => NewAsync(rig, "One more"));
        Assert.Equal("At most 200 recipients.", refused.Message);
    }

    [Fact]
    public async Task APasswordIsStoredEncryptedNeverSentBackAndKeptOnAnEmptySave()
    {
        await using var rig = await RigAsync(new SendAction());
        var saved = await NewAsync(rig, "Gate", password: "s3cret");

        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            var row = await db.Recipients.FindAsync(saved.Id);
            Assert.DoesNotContain("s3cret", row!.SettingsJson, StringComparison.Ordinal);
            Assert.Contains(RuleStore.SecretPrefix, row.SettingsJson, StringComparison.Ordinal);
        }

        var model = Assert.Single(await rig.Rules.ListRecipientsAsync(CancellationToken.None));
        Assert.False(model.Values.ContainsKey("password"));
        Assert.Equal(["password"], model.StoredSecretFields);

        var (kept, _) = await rig.Recipients.SaveAsync(saved.Id, "Gate", TargetType, Values("https://b.example.com", string.Empty), TargetFields, CancellationToken.None);
        Assert.Equal("s3cret", rig.Recipients.DecryptSettings(kept)["password"]!.ToString());
        var (replaced, _) = await rig.Recipients.SaveAsync(saved.Id, "Gate", TargetType, Values("https://b.example.com", "new"), TargetFields, CancellationToken.None);
        Assert.Equal("new", rig.Recipients.DecryptSettings(replaced)["password"]!.ToString());

        // A client never sends encrypted values, and a field the type does not declare is not stored.
        var values = Values("https://b.example.com", RuleStore.SecretPrefix + "x");
        values["other"] = "dropped";
        var (cleaned, _) = await rig.Recipients.SaveAsync(saved.Id, "Gate", TargetType, values, TargetFields, CancellationToken.None);
        Assert.Equal("new", rig.Recipients.DecryptSettings(cleaned)["password"]!.ToString());
        Assert.False(cleaned.Settings.ContainsKey("other"));
    }

    [Fact]
    public async Task CreateChangeAndDeleteRaiseOneChangeEachWithTheId()
    {
        await using var rig = await RigAsync(new SendAction());
        var changes = new List<Guid>();
        rig.Recipients.Changed += (_, id) => changes.Add(id);

        var saved = await NewAsync(rig, "Gate");
        await rig.Recipients.SaveAsync(saved.Id, "Gate 2", TargetType, Values("https://b.example.com"), TargetFields, CancellationToken.None);
        await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Recipients.SaveAsync(saved.Id, " ", TargetType, Values("https://b.example.com"), TargetFields, CancellationToken.None));
        await rig.Recipients.DeleteAsync(saved.Id, CancellationToken.None);
        Assert.Null(await rig.Recipients.DeleteAsync(saved.Id, CancellationToken.None));

        Assert.Equal([saved.Id, saved.Id, saved.Id], changes);
    }

    [Fact]
    public async Task ARunGetsTheRecipientMergedAndItsIdAndTheRecipientWinsOverAnOldValue()
    {
        var action = new SendAction();
        await using var rig = await RigAsync(action);
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var recipient = await NewAsync(rig, "Gate", password: "s3cret");
        var saved = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, Send(recipient, new JsonObject { ["text"] = "hello" })));

        // An old value of the same key in the stored action.
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            var row = await db.EventRules.FindAsync(saved.Id);
            var definition = RuleDefinition.FromJson(row!.DefinitionJson);
            definition.Actions[0].Settings["url"] = "https://old.example.com";
            row.DefinitionJson = definition.ToJson();
            await db.SaveChangesAsync();
            await rig.Engine.ApplyRuleChangeAsync(null, (await rig.Store.GetAsync(saved.Id, CancellationToken.None))!, CancellationToken.None);
        }

        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();

        var run = Assert.Single(action.Runs);
        Assert.Equal("https://a.example.com", run.Get("url"));
        Assert.Equal("s3cret", run.Get("password"));
        Assert.Equal("hello", run.Get("text"));
        Assert.Equal(recipient.Id, run.RecipientIds["recipient"]);
    }

    [Fact]
    public async Task AChangedRecipientReachesItsRulesAtTheNextRun()
    {
        var action = new SendAction();
        await using var rig = await RigAsync(action);
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var recipient = await NewAsync(rig, "Gate");
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, Send(recipient)));

        await rig.Rules.SaveRecipientAsync(recipient.Id, "Gate", TargetType, Values("https://new.example.com"), CancellationToken.None);
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();

        Assert.Equal("https://new.example.com", Assert.Single(action.Runs).Get("url"));
    }

    [Fact]
    public async Task SendTestMergesTheChosenRecipient()
    {
        var action = new SendAction();
        await using var rig = await RigAsync(action);
        var recipient = await NewAsync(rig, "Gate", password: "s3cret");

        var settings = new JsonObject { ["recipient"] = recipient.Id.ToString() };
        var (ok, _) = await rig.Runner.TestAsync("test.send", settings, "Rule", null, null, null, null, CancellationToken.None);

        Assert.True(ok);
        var run = Assert.Single(action.Runs);
        Assert.Equal("s3cret", run.Get("password"));
        Assert.Equal(recipient.Id, run.RecipientIds["recipient"]);

        var (none, message) = await rig.Runner.TestAsync("test.send", [], "Rule", null, null, null, null, CancellationToken.None);
        Assert.False(none);
        Assert.Equal("Choose a recipient.", message);
    }

    [Fact]
    public async Task TheTestOfARecipientUsesTheFormTheStoredPasswordAndTheActionDefaults()
    {
        var action = new SendAction();
        await using var rig = await RigAsync(action);
        var recipient = await NewAsync(rig, "Gate", password: "s3cret");

        var (ok, _) = await rig.Runner.TestRecipientAsync(TargetType, recipient.Id, Values("https://form.example.com"), CancellationToken.None);

        Assert.True(ok);
        var run = Assert.Single(action.Runs);
        Assert.Equal("https://form.example.com", run.Get("url"));
        Assert.Equal("s3cret", run.Get("password"));
        Assert.Empty(run.RecipientIds);

        var (bad, reason) = await rig.Runner.TestRecipientAsync(TargetType, null, Values("bad"), CancellationToken.None);
        Assert.False(bad);
        Assert.Equal("URL: Enter a good URL.", reason);
        var (unknown, _) = await rig.Runner.TestRecipientAsync("test.nothing", null, [], CancellationToken.None);
        Assert.False(unknown);
    }

    [Fact]
    public async Task ARecipientThatIsGoneFailsTheRunAndTheRuleSaysSo()
    {
        var action = new SendAction();
        await using var rig = await RigAsync(action);
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var recipient = await NewAsync(rig, "Gate");
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, Send(recipient)));
        await rig.Recipients.DeleteAsync(recipient.Id, CancellationToken.None); // a restored or edited database: the store alone does not check rules

        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();

        Assert.Empty(action.Runs);
        var line = Assert.Single(await ActionLinesAsync(rig));
        Assert.Equal(EventLogResult.Failed, line.Result);
        Assert.Equal("Choose a recipient.", line.Message);
        Assert.Equal("Send it: Choose a recipient.", Assert.Single(await rig.Rules.ListAsync(CancellationToken.None)).Problem);
    }

    /// <summary>A rule stored before recipients existed: its target sits in the action, nothing converts it.</summary>
    private static async Task<Guid> StoreOldRuleAsync(EventRulesRig rig)
    {
        var rule = EventRulesRig.Rule(EventRulesRig.Pulse, new RuleAction { Key = Guid.NewGuid(), ActionId = "test.send", Settings = new JsonObject { ["url"] = "https://old.example.com", ["password"] = RuleStore.SecretPrefix + "old", ["text"] = "hello" } });
        rule.Id = Guid.NewGuid();
        await using var db = await rig.DbFactory.CreateDbContextAsync();
        db.EventRules.Add(new EventRuleEntity { Id = rule.Id, Name = rule.Name, Enabled = true, DefinitionJson = rule.ToJson() });
        await db.SaveChangesAsync();
        return rule.Id;
    }

    [Fact]
    public async Task AnOldActionFailsWithItsTextSendsNothingAndTheRuleShowsIt()
    {
        var action = new SendAction();
        await using var rig = await RigAsync(action);
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var id = await StoreOldRuleAsync(rig);
        await rig.Engine.ApplyRuleChangeAsync(null, (await rig.Store.GetAsync(id, CancellationToken.None))!, CancellationToken.None);

        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();

        Assert.Empty(action.Runs);
        var line = Assert.Single(await ActionLinesAsync(rig));
        Assert.Equal(EventLogResult.Failed, line.Result);
        Assert.Equal("Send it", line.What);
        Assert.Equal("Choose a recipient.", line.Message);
        var summary = Assert.Single(await rig.Rules.ListAsync(CancellationToken.None));
        Assert.Equal("Send it: Choose a recipient.", summary.Problem);
        Assert.Equal("Send it", summary.ActionsText); // the old values stay unread
    }

    [Fact]
    public async Task AnOldActionCannotBeSavedWithoutARecipientAndItsOldValuesGoWithTheNextSave()
    {
        await using var rig = await RigAsync(new SendAction());
        var id = await StoreOldRuleAsync(rig);
        var recipient = await NewAsync(rig, "Gate");
        var rule = (await rig.Store.GetAsync(id, CancellationToken.None))!.Definition;

        var refused = await Assert.ThrowsAsync<RuleValidationException>(() => rig.Rules.SaveAsync(rule, CancellationToken.None));
        Assert.Equal("Send it: Choose a recipient.", refused.Message);

        rule.Actions[0].Settings["recipient"] = recipient.Id.ToString();
        await rig.SaveAsync(rule);

        var stored = (await rig.Store.GetAsync(id, CancellationToken.None))!.Definition.Actions[0].Settings;
        Assert.Equal(recipient.Id.ToString(), stored["recipient"]!.ToString());
        Assert.Equal("hello", stored["text"]!.ToString());
        Assert.False(stored.ContainsKey("url"));
        Assert.False(stored.ContainsKey("password"));
    }

    [Fact]
    public async Task ARuleRefusesARecipientThatDoesNotExistOrIsOfAnotherType()
    {
        await using var rig = await RigAsync(new SendAction());
        var other = await NewAsync(rig, "Other", type: OtherType);

        var wrongType = EventRulesRig.Rule(EventRulesRig.Pulse, Send(other));
        Assert.Equal("Send it: Choose a recipient.", (await Assert.ThrowsAsync<RuleValidationException>(() => rig.Rules.SaveAsync(wrongType, CancellationToken.None))).Message);
        var unknown = EventRulesRig.Rule(EventRulesRig.Pulse, new RuleAction { ActionId = "test.send", Settings = new JsonObject { ["recipient"] = Guid.NewGuid().ToString() } });
        Assert.Equal("Send it: Choose a recipient.", (await Assert.ThrowsAsync<RuleValidationException>(() => rig.Rules.SaveAsync(unknown, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task ARecipientInUseCannotBeDeletedAndTheRefusalNamesTheRules()
    {
        await using var rig = await RigAsync(new SendAction());
        var recipient = await NewAsync(rig, "Gate");
        foreach (var name in new[] { "OCR", "Gate alarm" })
        {
            var rule = EventRulesRig.Rule(EventRulesRig.Pulse, Send(recipient));
            rule.Name = name;
            await rig.SaveAsync(rule);
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rules.DeleteRecipientAsync(recipient.Id, CancellationToken.None));
        Assert.Equal("Used by Gate alarm, OCR. Change those rules first.", refused.Message);
        Assert.Equal(["Gate alarm", "OCR"], Assert.Single(await rig.Rules.ListRecipientsAsync(CancellationToken.None)).UsedBy);

        for (var i = 0; i < 5; i++)
        {
            var rule = EventRulesRig.Rule(EventRulesRig.Pulse, Send(recipient));
            rule.Name = "Rule " + i;
            await rig.SaveAsync(rule);
        }

        var many = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rules.DeleteRecipientAsync(recipient.Id, CancellationToken.None));
        Assert.Equal("Used by Gate alarm, OCR, Rule 0, Rule 1, Rule 2 and 2 more. Change those rules first.", many.Message);

        foreach (var rule in await rig.Store.ListAsync(CancellationToken.None))
        {
            await rig.Rules.DeleteAsync(rule.Definition.Id, CancellationToken.None);
        }

        Assert.NotNull(await rig.Rules.DeleteRecipientAsync(recipient.Id, CancellationToken.None));
        Assert.Null(await rig.Rules.DeleteRecipientAsync(recipient.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TheRulesAreWalkedOncePerListNotPerRecipient()
    {
        await using var rig = await RigAsync(new SendAction());
        var first = await NewAsync(rig, "One");
        var second = await NewAsync(rig, "Two");
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, Send(first), Send(first), Send(second)));
        var rules = await rig.Store.ListAsync(CancellationToken.None);

        var walks = 0;
        IEnumerable<StoredRule> Counted()
        {
            walks++;
            foreach (var rule in rules)
            {
                yield return rule;
            }
        }

        var used = RecipientUse.ByRecipient(Counted(), [first.Id, second.Id]);

        Assert.Equal(1, walks);
        Assert.Single(used[first.Id]); // two actions of one rule name it once
        Assert.Single(used[second.Id]);
    }

    [Fact]
    public async Task SavingChecksTheFieldsAndTheActionAndAFieldErrorNamesItsField()
    {
        await using var rig = await RigAsync(new SendAction());

        var empty = await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Rules.SaveRecipientAsync(null, "Gate", TargetType, [], CancellationToken.None));
        Assert.Equal("url", empty.Field);
        var bad = await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Rules.SaveRecipientAsync(null, "Gate", TargetType, Values("bad"), CancellationToken.None));
        Assert.Equal("url", bad.Field);
        Assert.Equal("Enter a good URL.", bad.Message);
        await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Rules.SaveRecipientAsync(null, "Gate", "test.nothing", [], CancellationToken.None));

        var (saved, created) = await rig.Rules.SaveRecipientAsync(null, "Gate", TargetType, Values("https://a.example.com"), CancellationToken.None);
        Assert.True(created);
        Assert.Equal("https://a.example.com", saved.Target);
    }

    [Fact]
    public async Task TheRulesListDescribesAnActionWithTheValuesOfItsRecipient()
    {
        await using var rig = await RigAsync(new SendAction());
        var recipient = await NewAsync(rig, "Gate", password: "s3cret");
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, Send(recipient)));

        var summary = Assert.Single(await rig.Rules.ListAsync(CancellationToken.None));
        Assert.Equal("To https://a.example.com", summary.ActionsText);
        Assert.Null(summary.Problem);
    }

    [Fact]
    public async Task TheCatalogCollectsTheTypesOfThePlugins()
    {
        await using var rig = await RigAsync(new SendAction());
        Assert.Equal([TargetType, OtherType], rig.Catalog.RecipientTypes.Select(t => t.Id));
        Assert.True(rig.Catalog.FindRecipientType(TargetType)!.Available);
        Assert.Null(rig.Catalog.FindRecipientType("test.nothing"));
    }

    private static async Task<EventRulesRig> NotificationsRigAsync() => await EventRulesRig.StartAsync(beforeStart: rig =>
    {
        var origin = new PluginOrigin("oadm.notifications", "1.0", null) { DisplayName = "Notifications" };
        rig.Registry.RegisterActionPlugin(new SendHttpAction(), origin);
        rig.Registry.RegisterActionPlugin(new SendEmailAction(), origin);
        rig.Registry.RegisterActionPlugin(new SendTcpAction(), origin);
    });

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task SendTCPMessageReadsItsTargetThroughTheMerge()
    {
        await using var rig = await NotificationsRigAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var values = new JsonObject { ["host"] = "127.0.0.1", ["port"] = port, ["tls"] = false, ["allowUntrusted"] = false };
        var (recipient, _) = await rig.Rules.SaveRecipientAsync(null, "Panel", NotificationRecipients.TcpType, values, CancellationToken.None);
        Assert.Equal($"127.0.0.1:{port}", recipient.Target);

        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            return await reader.ReadToEndAsync();
        });
        var settings = ActionSettings.Defaults(new SendTcpAction().Fields);
        settings["recipient"] = recipient.Id.ToString();
        settings["message"] = "ALARM {rule}";

        var (ok, message) = await rig.Runner.TestAsync(SendTcpAction.ActionId, settings, "Gate", null, null, null, null, CancellationToken.None);

        Assert.True(ok, message);
        Assert.Equal("ALARM Gate", await received.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task SendHTTPRequestAndSendEmailReadTheirTargetThroughTheMerge()
    {
        await using var rig = await NotificationsRigAsync();
        var port = FreePort(); // nothing listens: the error names the target the action got
        var (http, _) = await rig.Rules.SaveRecipientAsync(null, "Hook", NotificationRecipients.HttpType, new JsonObject { ["url"] = $"http://127.0.0.1:{port}/in", ["auth"] = "none" }, CancellationToken.None);
        var (mail, _) = await rig.Rules.SaveRecipientAsync(
            null,
            "Team",
            NotificationRecipients.EmailType,
            new JsonObject { ["host"] = "127.0.0.1", ["port"] = port, ["security"] = "none", ["from"] = "oadm@example.com", ["to"] = "team@example.com" },
            CancellationToken.None);
        Assert.Equal($"127.0.0.1:{port}", mail.Target);

        var httpSettings = ActionSettings.Defaults(new SendHttpAction().Fields);
        httpSettings["recipient"] = http.Id.ToString();
        var (httpOk, httpMessage) = await rig.Runner.TestAsync(SendHttpAction.ActionId, httpSettings, "Gate", null, null, null, null, CancellationToken.None);
        Assert.False(httpOk);
        Assert.Contains("127.0.0.1 cannot be reached", httpMessage, StringComparison.Ordinal);

        var mailSettings = ActionSettings.Defaults(new SendEmailAction().Fields);
        mailSettings["recipient"] = mail.Id.ToString();
        var (mailOk, mailMessage) = await rig.Runner.TestAsync(SendEmailAction.ActionId, mailSettings, "Gate", null, null, null, null, CancellationToken.None);
        Assert.False(mailOk);
        Assert.Contains($"127.0.0.1:{port} cannot be reached", mailMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecipientOfNotificationsIsCheckedByItsAction()
    {
        await using var rig = await NotificationsRigAsync();

        var bad = new JsonObject { ["host"] = "mail.example.com", ["port"] = 587, ["security"] = "none", ["from"] = "oadm@example.com", ["to"] = "not an address" };
        var refused = await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Rules.SaveRecipientAsync(null, "Team", NotificationRecipients.EmailType, bad, CancellationToken.None));
        Assert.Equal("to", refused.Field);
        Assert.Equal("Enter email addresses separated by commas.", refused.Message);

        // No placeholders in a recipient: a target is fixed.
        var placeholder = new JsonObject { ["url"] = "{webUrl}", ["auth"] = "none" };
        var url = await Assert.ThrowsAsync<RecipientValidationException>(() => rig.Rules.SaveRecipientAsync(null, "Hook", NotificationRecipients.HttpType, placeholder, CancellationToken.None));
        Assert.Equal("url", url.Field);
    }
}
