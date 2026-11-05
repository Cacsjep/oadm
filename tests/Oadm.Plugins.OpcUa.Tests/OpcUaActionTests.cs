using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

using Opc.Ua;

namespace Oadm.Plugins.OpcUa.Tests;

public sealed class OpcUaValueTests
{
    private static object? Number(string value, string format) => OpcUaValue.Encode(OpcUaValue.Number, value, format).AsBoxedObject();

    [Fact]
    public void NumbersGetTheChosenType()
    {
        Assert.Equal((short)-5, Number("-5", OpcUaValue.Signed16));
        Assert.Equal((ushort)65535, Number("65535", OpcUaValue.Unsigned16));
        Assert.Equal(-70000, Number("-70000", OpcUaValue.Signed32));
        Assert.Equal(4294967295u, Number("4294967295", OpcUaValue.Unsigned32));
        Assert.Equal(5000000000L, Number("5000000000", OpcUaValue.Signed64));
        Assert.Equal(1.5f, Number("1.5", OpcUaValue.Decimal32));
        Assert.Equal(-2.25d, Number("-2.25", OpcUaValue.Decimal64));
    }

    [Theory]
    [InlineData("32768", OpcUaValue.Signed16)]
    [InlineData("-1", OpcUaValue.Unsigned16)]
    [InlineData("1.5", OpcUaValue.Signed32)]
    [InlineData("4294967296", OpcUaValue.Unsigned32)]
    [InlineData("9223372036854775808", OpcUaValue.Signed64)]
    [InlineData("abc", OpcUaValue.Decimal32)]
    [InlineData("NaN", OpcUaValue.Decimal64)]
    [InlineData("1e40", OpcUaValue.Decimal32)]
    [InlineData("1", "byte")]
    public void ANumberThatDoesNotFitIsRefused(string value, string format)
    {
        Assert.Throws<FormatException>(() => Number(value, format));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("TRUE", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData(" False ", false)]
    public void OnAndOffAreUnderstood(string value, bool state)
    {
        Assert.Equal(state, OpcUaValue.Encode(OpcUaValue.OnOff, value, string.Empty).AsBoxedObject());
    }

    [Fact]
    public void OnOrOffRefusesAnythingElse()
    {
        var refused = Assert.Throws<FormatException>(() => OpcUaValue.Encode(OpcUaValue.OnOff, "maybe", string.Empty));

        Assert.Equal("Enter on or off.", refused.Message);
    }

    [Fact]
    public void TextStaysAsItIs()
    {
        Assert.Equal(" Line 1 ", OpcUaValue.Encode(OpcUaValue.Text, " Line 1 ", string.Empty).AsBoxedObject());
    }
}

public sealed class OpcUaTextsTests
{
    [Fact]
    public void ALoginRefusalSaysWhatToCheck()
    {
        var endpoint = "opc.tcp://plc:4840";

        Assert.Equal("The server refused the login. Check the user name and password.", OpcUaTexts.ConnectFailed(StatusCodes.BadUserAccessDenied, null, endpoint, OpcUaRecipient.LoginUser));
        Assert.Equal("The server refused the login. Check the user name and password.", OpcUaTexts.ConnectFailed(StatusCodes.BadIdentityTokenRejected, null, endpoint, OpcUaRecipient.LoginUser));
        Assert.Equal("The server refused the login.", OpcUaTexts.ConnectFailed(StatusCodes.BadIdentityTokenRejected, null, endpoint, OpcUaRecipient.LoginCertificate));
        Assert.Equal("The server does not offer this login. Choose another one.", OpcUaTexts.ConnectFailed(StatusCodes.BadIdentityTokenInvalid, null, endpoint, OpcUaRecipient.LoginUser));
    }

    [Fact]
    public void SecurityChecksHaveTwoMeanings()
    {
        var endpoint = "opc.tcp://plc:4840";

        Assert.Equal(
            "The server does not trust the client certificate. Trust it on the server.",
            OpcUaTexts.ConnectFailed(StatusCodes.BadSecurityChecksFailed, "Error received from remote host", endpoint, OpcUaRecipient.LoginAnonymous));
        Assert.Contains(
            "unprotected",
            OpcUaTexts.ConnectFailed(StatusCodes.BadSecurityChecksFailed, "A UserName identity token without token encryption requires a SignAndEncrypt channel.", endpoint, OpcUaRecipient.LoginUser),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownStatusKeepsItsName()
    {
        Assert.Equal("The connection to opc.tcp://plc failed (BadLicenseExpired).", OpcUaTexts.ConnectFailed(StatusCodes.BadLicenseExpired, null, "opc.tcp://plc", OpcUaRecipient.LoginAnonymous));
        Assert.Equal("The server refused the write (BadLicenseExpired).", OpcUaTexts.WriteFailed(StatusCodes.BadLicenseExpired));
    }

    [Fact]
    public void AWriteThatGotNoAnswerMayHaveHappened()
    {
        Assert.Equal(OpcUaTexts.NoAnswer, OpcUaTexts.WriteFailed(StatusCodes.BadTimeout));
        Assert.Equal("This login may not write the node.", OpcUaTexts.WriteFailed(StatusCodes.BadUserAccessDenied));
        Assert.Equal("The server refused the value: out of range.", OpcUaTexts.WriteFailed(StatusCodes.BadOutOfRange));
    }
}

public sealed class OpcUaRecipientTests
{
    private static RecipientType Type => Assert.Single(new WriteOpcUaAction().RecipientTypes);

    private static Dictionary<string, string> Errors(JsonObject values) => ActionSettings.Check(Type.Fields, values, statefulRule: true);

    private static bool Shown(string key, JsonObject values) =>
        ActionSettings.IsVisible(Type.Fields.Single(f => f.Key == key), values, statefulRule: true);

    private static JsonObject Secured(string login = OpcUaRecipient.LoginAnonymous) => new()
    {
        ["endpoint"] = "opc.tcp://plc:4840",
        ["security"] = OpcUaRecipient.SecuritySign,
        ["policy"] = OpcUaRecipient.PolicyBest,
        ["login"] = login,
    };

    [Fact]
    public void ANewRecipientNeedsOnlyTheAddress()
    {
        var values = ActionSettings.Defaults(Type.Fields);
        values["endpoint"] = "opc.tcp://plc:4840";

        Assert.Empty(Errors(values));
        Assert.Equal(OpcUaRecipient.SecurityNone, values["security"]!.GetValue<string>());
        Assert.Equal(OpcUaRecipient.LoginAnonymous, values["login"]!.GetValue<string>());
        Assert.False(Shown(OpcUaRecipient.ClientCertificate, values));
        Assert.False(Shown(OpcUaRecipient.TrustedCa, values));
        Assert.False(Shown(OpcUaRecipient.AllowUntrusted, values));
        Assert.False(Shown(OpcUaRecipient.Policy, values));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://plc:4840")]
    public void TheEndpointIsChecked(string endpoint)
    {
        var errors = Errors(new JsonObject { ["endpoint"] = endpoint, ["security"] = OpcUaRecipient.SecurityNone, ["login"] = OpcUaRecipient.LoginAnonymous });

        Assert.Equal(ActionFormats.EnterOpcEndpoint, errors["endpoint"]);
    }

    [Fact]
    public void TheTargetIsTheEndpointAndTheTypeIsPersistent()
    {
        Assert.Equal("{endpoint}", Type.Target);
        Assert.IsType<OpcUaSessionFactory>(Type.Sessions);
        Assert.Equal(WriteOpcUaAction.ActionId, Type.TestActionId);
    }

    [Fact]
    public void SecurityNeedsAClientCertificate()
    {
        Assert.Equal(OpcUaTexts.ChooseClientCertificate, Errors(Secured())[OpcUaRecipient.ClientCertificate]);

        var values = Secured();
        values[OpcUaRecipient.ClientCertificate] = "certificate-id";
        Assert.Empty(Errors(values));
    }

    [Fact]
    public void ACertificateLoginNeedsOneWithoutSecurityToo()
    {
        var values = new JsonObject { ["endpoint"] = "opc.tcp://plc", ["security"] = OpcUaRecipient.SecurityNone, ["login"] = OpcUaRecipient.LoginCertificate };

        Assert.True(Shown(OpcUaRecipient.ClientCertificate, values));
        Assert.Equal(OpcUaTexts.ChooseClientCertificate, Errors(values)[OpcUaRecipient.ClientCertificate]);
        Assert.False(Shown(OpcUaRecipient.TrustedCa, values));
    }

    [Fact]
    public void ThePasswordShowsOnlyForItsLogin()
    {
        var values = new JsonObject { ["endpoint"] = "opc.tcp://plc", ["security"] = OpcUaRecipient.SecurityNone, ["login"] = OpcUaRecipient.LoginUser };

        var errors = Errors(values);

        Assert.Equal("Enter the user name.", errors[OpcUaRecipient.User]);
        Assert.Equal("Enter the password.", errors[OpcUaRecipient.Password]);
        Assert.False(Shown(OpcUaRecipient.ClientCertificate, values));
        Assert.Equal(ActionFieldType.Password, Type.Fields.Single(f => f.Key == OpcUaRecipient.Password).Type);
    }

    [Fact]
    public void ThePolicyChoicesNameTheStacksPolicies()
    {
        var policies = Type.Fields.Single(f => f.Key == OpcUaRecipient.Policy).Choices.Select(c => c.Label).ToList();

        Assert.Equal(["Best the server offers", "Basic256Sha256", "Aes128_Sha256_RsaOaep", "Aes256_Sha256_RsaPss"], policies);
    }
}

public sealed class OpcUaEndpointsTests
{
    private static EndpointDescription Offer(MessageSecurityMode mode, string policy, byte level, string url = "opc.tcp://internal-name:4841/Plc") => new()
    {
        EndpointUrl = url,
        SecurityMode = mode,
        SecurityPolicyUri = policy,
        SecurityLevel = level,
    };

    private static readonly EndpointDescription[] Offers =
    [
        Offer(MessageSecurityMode.None, SecurityPolicies.None, 0),
        Offer(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256, 10),
        Offer(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Aes256_Sha256_RsaPss, 20),
        Offer(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.ECC_nistP256, 90),
        Offer(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Https, 95, "https://plc/Plc"),
    ];

    private static readonly Uri Address = new("opc.tcp://10.0.0.9:4840");

    [Fact]
    public void TheBestPolicyIsTheHighestThatFitsTheKey()
    {
        using var own = Opc.Ua.Security.Certificates.Certificate.FromRawData(TestCertificates.Client().RawData);

        var best = OpcUaEndpoints.Select(Offers.ToArrayOf(), Address, MessageSecurityMode.SignAndEncrypt, null, own);

        Assert.Equal(SecurityPolicies.Aes256_Sha256_RsaPss, best.SecurityPolicyUri);
    }

    [Fact]
    public void AChosenPolicyMustBeOffered()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => OpcUaEndpoints.Select(Offers.ToArrayOf(), Address, MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Aes128_Sha256_RsaOaep, null));

        Assert.Equal(OpcUaTexts.PolicyNotOffered, refused.Message);
    }

    [Fact]
    public void AModeThatIsNotOfferedIsRefused()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => OpcUaEndpoints.Select(Offers.ToArrayOf(), Address, MessageSecurityMode.Sign, null, null));

        Assert.Equal(OpcUaTexts.ModeNotOffered, refused.Message);
    }

    [Fact]
    public void TheAddressStaysTheOneTheUserEntered()
    {
        var selected = OpcUaEndpoints.Select(Offers.ToArrayOf(), Address, MessageSecurityMode.None, null, null);

        Assert.Equal("opc.tcp://10.0.0.9:4840/Plc", selected.EndpointUrl);
        Assert.Equal("opc.tcp://internal-name:4841/Plc", Offers[0].EndpointUrl);
    }
}

public sealed class WriteOpcUaActionTests
{
    private sealed class RecordingWriter : IOpcUaWriter
    {
        public List<(NodeId Node, object? Value)> Writes { get; } = [];

        public Task WriteAsync(NodeId node, Variant value, CancellationToken ct)
        {
            Writes.Add((node, value.AsBoxedObject()));
            return Task.CompletedTask;
        }
    }

    private static readonly NodeId Valve = NodeId.Parse("ns=2;s=Valve1");

    private static JsonElement Settings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ActionInvocation Run(string json, ActionEdge edge = ActionEdge.Fired) =>
        new(Guid.Empty, "Rule", edge, Settings(json), null, new ActionTrigger(string.Empty, "Event", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    private static (FakeActionContext Context, RecordingWriter Writer) Context()
    {
        var writer = new RecordingWriter();
        var context = new FakeActionContext();
        context.Sessions["recipient"] = writer;
        return (context, writer);
    }

    [Fact]
    public void ItIsAnActionForMachines()
    {
        var action = new WriteOpcUaAction();

        Assert.Equal(("oadm.opcua.write", "Write OPC UA value", "Machines"), (action.Id, action.DisplayName, action.Group));
        Assert.False(action.NeedsDevice);
        Assert.True(action.SupportsTurnOff);
        Assert.True(action.UsesPlaceholders);
    }

    [Fact]
    public async Task OneRunIsOneWriteOfItsType()
    {
        var (context, writer) = Context();

        await new WriteOpcUaAction().ExecuteAsync(context, Run("""{"node":"ns=2;s=Valve1","valueType":"onoff","value":"on"}"""), CancellationToken.None);
        await new WriteOpcUaAction().ExecuteAsync(context, Run("""{"node":"ns=2;s=Valve1","valueType":"number","numberFormat":"uint16","value":"7"}"""), CancellationToken.None);
        await new WriteOpcUaAction().ExecuteAsync(context, Run("""{"node":"ns=2;s=Valve1","valueType":"text","value":"Open"}"""), CancellationToken.None);

        Assert.Equal([(Valve, true), (Valve, (ushort)7), (Valve, "Open")], writer.Writes);
        Assert.Equal("Node ns=2;s=Valve1: written", context.Lines[0]);
    }

    [Fact]
    public async Task APlaceholderIsFilledAndCheckedAtTheRun()
    {
        var (context, writer) = Context();
        context.Placeholders["number"] = "42";

        await new WriteOpcUaAction().ExecuteAsync(context, Run("""{"node":"ns=2;s=Valve1","valueType":"number","numberFormat":"int32","value":"{number}"}"""), CancellationToken.None);

        Assert.Equal([(Valve, 42)], writer.Writes);
    }

    [Fact]
    public async Task AValueThatDoesNotFitWritesNothing()
    {
        var (context, writer) = Context();
        context.Placeholders["number"] = "70000";

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new WriteOpcUaAction().ExecuteAsync(
            context, Run("""{"node":"ns=2;s=Valve1","valueType":"number","numberFormat":"uint16","value":"{number}"}"""), CancellationToken.None));

        Assert.Equal("Enter a whole number from 0 to 65535. Nothing was written.", refused.Message);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task APlaceholderInTheNodeIdIsNotFilled()
    {
        var (context, writer) = Context();
        context.Placeholders["number"] = "1";

        await new WriteOpcUaAction().ExecuteAsync(context, Run("""{"node":"ns=2;s={number}","valueType":"onoff","value":"on"}"""), CancellationToken.None);

        Assert.Equal("ns=2;s={number}", writer.Writes[0].Node.ToString());
    }

    [Fact]
    public void TheOffEdgeRunsOnlyWithAValue()
    {
        var action = new WriteOpcUaAction();

        Assert.False(action.RunsOn(ActionEdge.TurnedOff, Settings("""{"valueType":"onoff"}""")));
        Assert.True(action.RunsOn(ActionEdge.TurnedOff, Settings("""{"valueType":"onoff","offValue":"off"}""")));
        Assert.True(action.RunsOn(ActionEdge.TurnedOn, Settings("""{"valueType":"onoff"}""")));
    }

    [Fact]
    public async Task TheOffEdgeWritesItsOwnValue()
    {
        var (context, writer) = Context();

        await new WriteOpcUaAction().ExecuteAsync(context, Run("""{"node":"ns=2;s=Valve1","valueType":"onoff","value":"on","offValue":"off"}""", ActionEdge.TurnedOff), CancellationToken.None);

        Assert.Equal([(Valve, false)], writer.Writes);
    }

    [Theory]
    [InlineData("ns=2;s=Valve1")]
    [InlineData("i=2258")]
    [InlineData("ns=3;g=09087e75-8e5e-499b-954f-f2a9603db28a")]
    [InlineData("ns=4;b=YWJj")]
    public void AValidNodeIdPasses(string node)
    {
        var settings = Settings(JsonSerializer.Serialize(new { node, valueType = "onoff", value = "on" }));

        Assert.Empty(new WriteOpcUaAction().Validate(settings, false));
    }

    [Theory]
    [InlineData("Valve1")]
    [InlineData("ns=x;s=Valve1")]
    [InlineData("ns=2;i=abc")]
    public void AWrongNodeIdIsRefusedAtASave(string node)
    {
        var settings = Settings(JsonSerializer.Serialize(new { node, valueType = "onoff", value = "on" }));

        Assert.Equal("Enter a node ID such as ns=2;s=Valve1.", new WriteOpcUaAction().Validate(settings, false)["node"]);
    }

    [Fact]
    public void TheValueIsCheckedAtASaveUnlessItIsAPlaceholder()
    {
        var action = new WriteOpcUaAction();

        Assert.Equal("Enter a whole number from 0 to 65535.", action.Validate(Settings("""{"valueType":"number","numberFormat":"uint16","value":"-5"}"""), false)["value"]);
        Assert.Empty(action.Validate(Settings("""{"valueType":"number","numberFormat":"uint16","value":"{number}"}"""), false));
        Assert.Equal("Enter on or off.", action.Validate(Settings("""{"valueType":"onoff","value":"on","offValue":"maybe"}"""), true)["offValue"]);
        Assert.Empty(action.Validate(Settings("""{"valueType":"onoff","value":"on","offValue":"maybe"}"""), false));
    }

    [Fact]
    public void TheFieldsAreCheckedByTheHost()
    {
        var errors = ActionSettings.Check(new WriteOpcUaAction().Fields, new JsonObject { ["valueType"] = "number" }, statefulRule: false);

        Assert.Equal(ActionSettings.ChooseRecipient, errors["recipient"]);
        Assert.Equal("Enter the node ID.", errors["node"]);
        Assert.Equal("Enter value.", errors["value"]);
    }

    [Fact]
    public void TheNumberFormatShowsOnlyForNumbers()
    {
        var format = new WriteOpcUaAction().Fields.Single(f => f.Key == "numberFormat");

        Assert.Equal(["Int16", "UInt16", "Int32", "UInt32", "Int64", "Float", "Double"], format.Choices.Select(c => c.Label));
        Assert.False(ActionSettings.IsVisible(format, new JsonObject { ["valueType"] = "text" }, false));
        Assert.True(ActionSettings.IsVisible(format, new JsonObject { ["valueType"] = "number" }, false));
    }
}
