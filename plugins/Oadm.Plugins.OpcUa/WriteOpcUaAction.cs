using System.Text.Json;

using Opc.Ua;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.OpcUa;

/// <summary>The recipient type of OPC UA: a server, one session the rules share. A new one works with only the address.</summary>
public static class OpcUaRecipient
{
    public const string TypeId = "oadm.opcua.recipient";

    public const string Endpoint = "endpoint";
    public const string Security = "security";
    public const string Policy = "policy";
    public const string Login = "login";
    public const string User = "user";
    public const string Password = "password";
    public const string ClientCertificate = "clientCertificate";
    public const string TrustedCa = "trustedCa";
    public const string AllowUntrusted = "allowUntrusted";

    public const string SecurityNone = "none";
    public const string SecuritySign = "sign";
    public const string SecuritySignAndEncrypt = "signAndEncrypt";

    public const string PolicyBest = "best";
    public const string PolicyBasic256Sha256 = "basic256sha256";
    public const string PolicyAes128 = "aes128";
    public const string PolicyAes256 = "aes256";

    public const string LoginAnonymous = "anonymous";
    public const string LoginUser = "user";
    public const string LoginCertificate = "certificate";

    private const string Secured = SecuritySign + "|" + SecuritySignAndEncrypt;

    public static RecipientType Type { get; } = new(
        TypeId,
        "OPC UA",
        "send",
        [
            ActionField.Text(Endpoint, "Endpoint", required: true) with { Hint = "opc.tcp://host:4840", EmptyText = ActionFormats.EnterOpcEndpoint, Format = ActionFieldFormat.OpcEndpoint },
            ActionField.Choice(Security, "Security", [new(SecurityNone, "None"), new(SecuritySign, "Sign"), new(SecuritySignAndEncrypt, "Sign and encrypt")], SecurityNone),
            ActionField.Choice(Policy, "Policy",
                [
                    new(PolicyBest, "Best the server offers"),
                    new(PolicyBasic256Sha256, "Basic256Sha256"),
                    new(PolicyAes128, "Aes128_Sha256_RsaOaep"),
                    new(PolicyAes256, "Aes256_Sha256_RsaPss"),
                ],
                PolicyBest) with { VisibleWhenKey = Security, VisibleWhenValue = Secured },
            ActionField.Choice(Login, "Login", [new(LoginAnonymous, "Anonymous"), new(LoginUser, "User name and password"), new(LoginCertificate, "Certificate")], LoginAnonymous),
            ActionField.Text(User, "User name", required: true) with { VisibleWhenKey = Login, VisibleWhenValue = LoginUser, EmptyText = "Enter the user name." },
            ActionField.Password(Password, "Password", required: true) with { VisibleWhenKey = Login, VisibleWhenValue = LoginUser, EmptyText = "Enter the password." },
            ActionField.ClientCertificate(ClientCertificate, "Client certificate", required: true) with
            {
                VisibleWhenKey = Security,
                VisibleWhenValue = Secured,
                AlsoVisibleWhenKey = Login,
                AlsoVisibleWhenValue = LoginCertificate,
                EmptyText = OpcUaTexts.ChooseClientCertificate,
            },
            ActionField.TrustedCertificate(TrustedCa, "Trusted CA") with { VisibleWhenKey = Security, VisibleWhenValue = Secured },
            ActionField.Bool(AllowUntrusted, "Allow untrusted certificates") with { VisibleWhenKey = Security, VisibleWhenValue = Secured },
        ],
        WriteOpcUaAction.ActionId)
    {
        Target = "{endpoint}",
        Sessions = new OpcUaSessionFactory(),
    };
}

/// <summary>
/// Event rule action "Write OPC UA value": one write of the Value attribute of a node, with an optional value when the rule turns
/// off. A write changes a real machine: the value is checked before anything is sent, and nothing is read or retried. A write the
/// server is not certain of fails.
/// </summary>
public sealed class WriteOpcUaAction : IActionPlugin
{
    public const string ActionId = "oadm.opcua.write";

    private const string RecipientKey = "recipient";
    private const string NodeKey = "node";
    private const string ValueKey = "value";
    private const string OffValueKey = "offValue";
    private const string EnterNode = "Enter a node ID such as ns=2;s=Valve1.";
    private const string NothingWritten = "Nothing was written.";

    public string Id => ActionId;

    public string DisplayName => "Write OPC UA value";

    public string? IconKey => "send";

    public string Group => "Machines";

    public string? Description => "Writes a value to a node of an OPC UA server.";

    public bool NeedsDevice => false;

    public bool SupportsTurnOff => true;

    public bool UsesPlaceholders => true;

    public IReadOnlyList<RecipientType> RecipientTypes { get; } = [OpcUaRecipient.Type];

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Recipient(RecipientKey, "Recipient", [OpcUaRecipient.TypeId]),
        ActionField.Text(NodeKey, "Node ID", required: true) with { Hint = "ns=2;s=Valve1", EmptyText = "Enter the node ID." },
        ActionField.Choice("valueType", "Value type", [new(OpcUaValue.OnOff, "On or off"), new(OpcUaValue.Number, "Number"), new(OpcUaValue.Text, "Text")], OpcUaValue.OnOff),
        ActionField.Choice("numberFormat", "Number format",
            [
                new(OpcUaValue.Signed16, "Int16"),
                new(OpcUaValue.Unsigned16, "UInt16"),
                new(OpcUaValue.Signed32, "Int32"),
                new(OpcUaValue.Unsigned32, "UInt32"),
                new(OpcUaValue.Signed64, "Int64"),
                new(OpcUaValue.Decimal32, "Float"),
                new(OpcUaValue.Decimal64, "Double"),
            ],
            OpcUaValue.Signed32) with { VisibleWhenKey = "valueType", VisibleWhenValue = OpcUaValue.Number },
        ActionField.Text(ValueKey, "Value", required: true) with { Hint = "A placeholder such as {number} is checked at the run" },
        ActionField.Text(OffValueKey, "Value") with { OffEdge = true, Hint = "Optional" },
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Text(settings, NodeKey) is { Length: > 0 } node && !NodeId.TryParse(node.Trim(), out _))
        {
            errors[NodeKey] = EnterNode;
        }

        Check(ValueKey, applies: true);
        Check(OffValueKey, statefulRule);
        return errors;

        void Check(string key, bool applies)
        {
            var value = Text(settings, key);
            if (!applies || value.Length == 0 || value.Contains('{', StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                Encode(settings, value);
            }
            catch (FormatException ex)
            {
                errors[key] = ex.Message;
            }
        }
    }

    public string Describe(JsonElement settings) => "Write " + Text(settings, NodeKey).Trim();

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || Text(settings, OffValueKey).Length > 0;

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var settings = invocation.Settings;
        var text = Filled(ctx, Text(settings, invocation.Edge == ActionEdge.TurnedOff ? OffValueKey : ValueKey));
        Variant value;
        try
        {
            value = Encode(settings, text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{ex.Message} {NothingWritten}", ex);
        }

        var nodeText = Text(settings, NodeKey).Trim();
        if (!NodeId.TryParse(nodeText, out var node))
        {
            throw new InvalidOperationException($"{EnterNode} {NothingWritten}");
        }

        var session = await ctx.SessionAsync<IOpcUaWriter>(RecipientKey, ct).ConfigureAwait(false);
        await session.WriteAsync(node, value, ct).ConfigureAwait(false);
        ctx.Log($"Node {nodeText}: written");
    }

    /// <summary>The text with its placeholders filled; refuses a name nobody fills and a value that came out empty.</summary>
    private static string Filled(IActionContext ctx, string text)
    {
        if (ActionPlaceholders.FirstUnknown(text, ctx) is { } unknown)
        {
            throw new InvalidOperationException($"The placeholder {unknown} is unknown. {NothingWritten}");
        }

        var filled = ctx.Text(text);
        return filled.Length > 0 || text.Length == 0 ? filled : throw new InvalidOperationException($"The value is empty. {NothingWritten}");
    }

    private static Variant Encode(JsonElement settings, string value) =>
        OpcUaValue.Encode(Text(settings, "valueType"), value, Text(settings, "numberFormat"));

    private static string Text(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
