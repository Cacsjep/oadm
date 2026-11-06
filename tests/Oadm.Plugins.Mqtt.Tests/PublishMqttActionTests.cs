using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Mqtt;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Mqtt.Tests;

public sealed class PublishMqttActionTests
{
    private static readonly PublishMqttAction Action = new();

    private sealed class RecordingPublisher : IMqttPublisher
    {
        public List<MqttMessage> Sent { get; } = [];

        public Task PublishAsync(MqttMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>The fake context, with the escape it was asked for: <see cref="FakeActionContext"/> leaves the second overload to the interface.</summary>
    private sealed class EscapeContext(FakeActionContext inner) : IActionContext
    {
        public List<PlaceholderEscape> Escapes { get; } = [];

        public IVapixClient? Vapix => inner.Vapix;

        public ILogger Logger => inner.Logger;

        public ICorePlugin? Owner => inner.Owner;

        public IPluginSettings Settings => inner.Settings;

        public void Log(string message) => inner.Log(message);

        public string Text(string? text) => inner.Text(text);

        public string Text(string? text, PlaceholderEscape escape)
        {
            Escapes.Add(escape);
            return escape == PlaceholderEscape.Json
                ? inner.Placeholders.Aggregate(text ?? string.Empty, (current, p) => current.Replace("{" + p.Key + "}", JsonEncodedText.Encode(p.Value).ToString(), StringComparison.Ordinal))
                : inner.Text(text);
        }

        public Task<T> SessionAsync<T>(string fieldKey, CancellationToken ct)
            where T : class => inner.SessionAsync<T>(fieldKey, ct);
    }

    private static JsonElement Settings(object values) => JsonSerializer.SerializeToElement(values);

    private static ActionInvocation Run(object values, ActionEdge edge = ActionEdge.Fired) =>
        new(Guid.Empty, "Rule", edge, Settings(values), null, new ActionTrigger(string.Empty, "Event", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    private static (FakeActionContext Context, RecordingPublisher Publisher) Context()
    {
        var publisher = new RecordingPublisher();
        var context = new FakeActionContext();
        context.Sessions["recipient"] = publisher;
        return (context, publisher);
    }

    [Fact]
    public void TheActionIsDeclaredAsAgreed()
    {
        Assert.Equal("oadm.mqtt.publish", Action.Id);
        Assert.Equal("Publish MQTT message", Action.DisplayName);
        Assert.Equal("Machines", Action.Group);
        Assert.False(Action.NeedsDevice);
        Assert.True(Action.SupportsTurnOff);
        Assert.True(Action.UsesPlaceholders);
        var defaults = ActionSettings.Defaults(Action.Fields);
        Assert.Equal("1", ActionSettings.Text(defaults, "qos"));
        Assert.Equal("text", ActionSettings.Text(defaults, "format"));
        Assert.Equal("false", ActionSettings.Text(defaults, "retain"));
        Assert.Equal("format", Action.Fields.Single(f => f.Key == "payload").LanguageKey);
        Assert.True(Action.Fields.Single(f => f.Key == "offPayload").OffEdge);
    }

    [Fact]
    public void TheRecipientStartsWithoutSecurity()
    {
        var type = Assert.Single(Action.RecipientTypes);
        Assert.Equal("{host}:{port}", type.Target);
        Assert.IsType<MqttSessionFactory>(type.Sessions);
        Assert.Equal(Action.Id, type.TestActionId);
        var defaults = ActionSettings.Defaults(type.Fields);
        Assert.Equal("1883", ActionSettings.Text(defaults, "port"));
        Assert.Equal("5.0", ActionSettings.Text(defaults, "protocolVersion"));
        Assert.Equal("false", ActionSettings.Text(defaults, "tls"));
        Assert.Equal(ActionFieldType.TrustedCertificate, type.Fields.Single(f => f.Key == "trustedCa").Type);
        Assert.Equal(ActionFieldType.ClientCertificate, type.Fields.Single(f => f.Key == "clientCertificate").Type);
        Assert.Equal(ActionFieldType.Password, type.Fields.Single(f => f.Key == "password").Type);
        var recipient = Guid.NewGuid();
        Assert.Equal(MqttRecipient.DefaultClientId(recipient), MqttRecipient.DefaultClientId(recipient));
        Assert.NotEqual(MqttRecipient.DefaultClientId(recipient), MqttRecipient.DefaultClientId(Guid.NewGuid()));
        Assert.NotEqual(MqttRecipient.DefaultClientId(null), MqttRecipient.DefaultClientId(null));
        Assert.StartsWith("oadm-", MqttRecipient.DefaultClientId(recipient), StringComparison.Ordinal);
        Assert.Equal(23, MqttRecipient.DefaultClientId(recipient).Length);
    }

    [Fact]
    public void ATestKeepsAChosenClientIdWithinTheSafeLengthAndUnique()
    {
        var chosen = new string('a', 40);

        var (first, second) = (MqttRecipient.ClientId(chosen, null), MqttRecipient.ClientId(chosen, null));

        Assert.Equal(23, first.Length);
        Assert.StartsWith("aaaaaaaaaaaaaa-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Equal(chosen, MqttRecipient.ClientId(chosen, Guid.NewGuid()));
    }

    [Fact]
    public void AChosenClientIdStaysAndATestGetsItsOwn()
    {
        var recipient = Guid.NewGuid();

        Assert.Equal("line-3", MqttRecipient.ClientId(" line-3 ", recipient));
        Assert.Equal(MqttRecipient.DefaultClientId(recipient), MqttRecipient.ClientId(" ", recipient));
        Assert.StartsWith("line-3-", MqttRecipient.ClientId("line-3", null), StringComparison.Ordinal);
        Assert.NotEqual(MqttRecipient.ClientId("line-3", null), MqttRecipient.ClientId("line-3", null));
    }

    [Fact]
    public void RecipientFieldsAreChecked()
    {
        var type = MqttRecipient.Type;
        var errors = ActionSettings.Check(type.Fields, new JsonObject { ["host"] = "999.1.1.1", ["port"] = 70000 }, statefulRule: true);
        Assert.Equal(ActionFormats.EnterHost, errors["host"]);
        Assert.Contains("port", errors.Keys);
        Assert.Contains("protocolVersion", errors.Keys);
        Assert.Empty(ActionSettings.Check(type.Fields, Filled(), statefulRule: true));
        Assert.Contains("protocolVersion", ActionSettings.Check(type.Fields, Filled(protocol: "4"), statefulRule: true).Keys);
    }

    private static JsonObject Filled(string protocol = "3.1.1") => new()
    {
        ["host"] = "broker.example",
        ["port"] = 8883,
        ["protocolVersion"] = protocol,
    };

    [Theory]
    [InlineData("allowUntrusted")]
    [InlineData("trustedCa")]
    [InlineData("clientCertificate")]
    public void TlsFieldsShowOnlyWithTls(string key)
    {
        var field = MqttRecipient.Type.Fields.Single(f => f.Key == key);
        Assert.False(ActionSettings.IsVisible(field, new JsonObject { ["tls"] = false }, statefulRule: true));
        Assert.True(ActionSettings.IsVisible(field, new JsonObject { ["tls"] = true }, statefulRule: true));
    }

    [Fact]
    public void ActionFieldsAreChecked()
    {
        var errors = ActionSettings.Check(Action.Fields, new JsonObject { ["qos"] = "3", ["format"] = "xml" }, statefulRule: true);
        Assert.Equal(ActionSettings.ChooseRecipient, errors["recipient"]);
        Assert.Contains("topic", errors.Keys);
        Assert.Contains("qos", errors.Keys);
        Assert.Contains("format", errors.Keys);
        Assert.DoesNotContain("payload", errors.Keys);
        Assert.DoesNotContain("offPayload", errors.Keys);
    }

    [Fact]
    public void TheOffPayloadBelongsToRulesThatTurnOff()
    {
        var field = Action.Fields.Single(f => f.Key == "offPayload");
        Assert.False(ActionSettings.IsVisible(field, [], statefulRule: false));
        Assert.True(ActionSettings.IsVisible(field, [], statefulRule: true));
    }

    [Theory]
    [InlineData("a/+/b", true)]
    [InlineData("a/#", true)]
    [InlineData("a/{device}/state", false)]
    public void ATopicWithAWildcardIsRefusedBeforeSaving(string topic, bool refused)
    {
        var errors = Action.Validate(Settings(new { topic }), statefulRule: false);
        Assert.Equal(refused, errors.ContainsKey("topic"));
    }

    [Fact]
    public void ARecipientWithoutATopicPassesValidate()
    {
        Assert.Empty(Action.Validate(Settings(new { host = "broker.example" }), statefulRule: false));
    }

    [Fact]
    public void TheRuleListNamesTheTopic()
    {
        Assert.Equal("Publish to a/b", Action.Describe(Settings(new { topic = "a/b" })));
        Assert.Equal(Action.DisplayName, Action.Describe(Settings(new { })));
    }

    [Fact]
    public void TheOffEdgeRunsOnlyWithAPayload()
    {
        var with = Settings(new { offPayload = "off" });
        var without = Settings(new { payload = "on" });
        Assert.True(Action.RunsOn(ActionEdge.TurnedOff, with));
        Assert.False(Action.RunsOn(ActionEdge.TurnedOff, without));
        Assert.True(Action.RunsOn(ActionEdge.TurnedOn, without));
        Assert.True(Action.RunsOn(ActionEdge.Fired, without));
    }

    [Fact]
    public async Task AMessageGoesOutWithItsSettings()
    {
        var (context, publisher) = Context();
        context.Placeholders["device"] = "door-1";
        await Action.ExecuteAsync(context, Run(new { topic = "site/{device}", payload = "open {device}", qos = "2", retain = true }), CancellationToken.None);
        Assert.Equal(new MqttMessage("site/door-1", "open door-1", 2, true), Assert.Single(publisher.Sent));
        Assert.Equal("Published to site/door-1", Assert.Single(context.Lines));
    }

    [Fact]
    public async Task TheOffEdgeSendsTheOffPayloadToTheSameTopic()
    {
        var (context, publisher) = Context();
        await Action.ExecuteAsync(context, Run(new { topic = "t", payload = "on", offPayload = "off", qos = "0" }, ActionEdge.TurnedOff), CancellationToken.None);
        Assert.Equal(new MqttMessage("t", "off", 0, false), Assert.Single(publisher.Sent));
    }

    [Fact]
    public async Task JsonValuesAreEscapedAndTextIsNot()
    {
        var (fake, publisher) = Context();
        fake.Placeholders["name"] = "a\"b";
        var context = new EscapeContext(fake);
        await Action.ExecuteAsync(context, Run(new { topic = "t", format = "json", payload = "{\"name\": \"{name}\"}" }), CancellationToken.None);
        await Action.ExecuteAsync(context, Run(new { topic = "t", format = "text", payload = "{name}" }), CancellationToken.None);
        Assert.Equal("a\"b", JsonDocument.Parse(publisher.Sent[0].Payload).RootElement.GetProperty("name").GetString());
        Assert.Equal("a\"b", publisher.Sent[1].Payload);
        Assert.Equal([PlaceholderEscape.Json, PlaceholderEscape.None], context.Escapes);
    }

    [Theory]
    [InlineData("", "The topic is empty.")]
    [InlineData("{gone}", "The topic is empty.")]
    [InlineData("a/+/b", "A topic cannot hold + or #.")]
    [InlineData("a/#", "A topic cannot hold + or #.")]
    [InlineData("a/{value}", "A topic cannot hold + or #.")]
    [InlineData("a\0b", "A topic cannot hold a null character.")]
    public async Task ABadTopicSendsNothing(string topic, string text)
    {
        var (context, publisher) = Context();
        context.Placeholders["gone"] = string.Empty;
        context.Placeholders["value"] = "x+y";
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Action.ExecuteAsync(context, Run(new { topic, payload = "p" }), CancellationToken.None));
        Assert.Equal($"{text} Nothing was sent.", refused.Message);
        Assert.Empty(publisher.Sent);
        Assert.Empty(context.Lines);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("+")]
    [InlineData("x#")]
    [InlineData("x\0y")]
    [InlineData("TOTAL 10.60\nORDER 23F7H")]
    [InlineData("a\tb")]
    [InlineData("$SYS")]
    [InlineData("")]
    public async Task AValueThatChangesTheTopicLevelSendsNothing(string value)
    {
        var (context, publisher) = Context();
        context.Placeholders["device"] = value;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Action.ExecuteAsync(context, Run(new { topic = "site/{device}/state", payload = "p" }), CancellationToken.None));

        Assert.EndsWith("Nothing was sent.", refused.Message, StringComparison.Ordinal);
        Assert.Empty(publisher.Sent);
    }

    [Fact]
    public async Task ADollarInsideAValueIsAllowed()
    {
        var (context, publisher) = Context();
        context.Placeholders["device"] = "door$1";

        await Action.ExecuteAsync(context, Run(new { topic = "site/{device}", payload = "p" }), CancellationToken.None);

        Assert.Equal("site/door$1", Assert.Single(publisher.Sent).Topic);
    }

    [Theory]
    [InlineData("site/{gone}", "p")]
    [InlineData("t", "door {gone}")]
    public async Task AnUnknownPlaceholderSendsNothing(string topic, string payload)
    {
        var (context, publisher) = Context();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Action.ExecuteAsync(context, Run(new { topic, payload }), CancellationToken.None));

        Assert.Equal("The placeholder {gone} is unknown. Nothing was sent.", refused.Message);
        Assert.Empty(publisher.Sent);
    }

    [Fact]
    public async Task APayloadThatCameOutEmptySendsNothing()
    {
        var (context, publisher) = Context();
        context.Placeholders["site"] = string.Empty;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Action.ExecuteAsync(context, Run(new { topic = "t", payload = "{site}", retain = true }), CancellationToken.None));

        Assert.Equal("The payload is empty. Nothing was sent.", refused.Message);
        Assert.Empty(publisher.Sent);
    }

    [Fact]
    public async Task AnEmptyPayloadFromTheRuleItselfGoesOut()
    {
        var (context, publisher) = Context();

        await Action.ExecuteAsync(context, Run(new { topic = "t", payload = string.Empty }), CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(publisher.Sent).Payload);
    }

    [Fact]
    public async Task JsonBracesAreNotPlaceholders()
    {
        var (context, publisher) = Context();

        await Action.ExecuteAsync(context, Run(new { topic = "t", format = "json", payload = "{\"door\": {\"open\": true}}" }), CancellationToken.None);

        Assert.Single(publisher.Sent);
    }

    [Fact]
    public async Task ATopicLongerThanTheProtocolAllowsSendsNothing()
    {
        var (context, publisher) = Context();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Action.ExecuteAsync(context, Run(new { topic = new string('ä', 40_000) }), CancellationToken.None));
        Assert.Equal("The topic is too long. Nothing was sent.", refused.Message);
        Assert.Empty(publisher.Sent);
    }
}
