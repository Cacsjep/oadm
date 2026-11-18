using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// One node of the profile in the editor: its kind, Continue on error, its fields (the side panel) and the profile check's
/// error and warning for it (on the canvas). <see cref="ToFlowNode"/> writes the fields into the settings record and keeps
/// every key it has no field for.
/// </summary>
public sealed partial class NodeEditorViewModel : ObservableObject
{
    private readonly JsonObject _settings;

    public NodeEditorViewModel(FlowNode node, NodeKindInfo? kind, IReadOnlyList<CredentialChoice> credentials)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(credentials);
        Id = node.Id;
        Kind = node.Kind;
        Name = kind?.Name ?? node.Kind.ToString();
        IsFixed = kind?.IsFixed ?? node.Kind is NodeKind.SetPassword or NodeKind.AddToOadm;
        AlwaysStops = kind?.AlwaysStops ?? node.Kind == NodeKind.SetPassword;
        ContinueOnError = !AlwaysStops && node.ContinueOnError;
        _settings = Settings(node.Settings) ?? Settings(kind?.DefaultSettings) ?? [];
        Fields = NodeForms.Create(node.Kind, _settings, Settings(kind?.DefaultSettings), credentials);
        foreach (var field in Fields)
        {
            field.Changed += (_, _) => OnFieldChanged();
        }
    }

    public string Id { get; }

    public NodeKind Kind { get; }

    /// <summary>The kind's name ("Set IP").</summary>
    public string Name { get; }

    /// <summary>Set password and Add to OADM: always first, never moved or removed.</summary>
    public bool IsFixed { get; }

    /// <summary>An error always stops the flow (Set password): Continue on error is off and cannot be turned on.</summary>
    public bool AlwaysStops { get; }

    public bool CanChangeContinueOnError => !AlwaysStops;

    [ObservableProperty]
    public partial bool ContinueOnError { get; set; }

    public IReadOnlyList<NodeFieldViewModel> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    /// <summary>The line of a node without fields: Wait for approval says what it does; another kind that it has none.</summary>
    public string? Note => HasFields ? null : Kind == NodeKind.WaitForApproval ? ProvisioningTexts.WaitHint : ProvisioningTexts.NoSettings;

    /// <summary>The profile check's error for this node (on the canvas, below the card).</summary>
    [ObservableProperty]
    public partial string? CheckError { get; set; }

    /// <summary>The profile check's warning for this node (on the canvas, below the card, in amber).</summary>
    [ObservableProperty]
    public partial string? CheckWarning { get; set; }

    /// <summary>The fields' errors are all gone (shown or not).</summary>
    public bool IsValid => Fields.All(f => f.IsFormValid);

    /// <summary>The first field error (the Save tooltip).</summary>
    public string? FirstError => Fields.Select(f => f.FirstError).FirstOrDefault(e => e is not null);

    /// <summary>The password typed in Set password, sent once with Save; null otherwise.</summary>
    public string? TypedPassword => Fields.OfType<PasswordFieldViewModel>().FirstOrDefault()?.TypedPassword;

    /// <summary>A field or Continue on error changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The error shown on the canvas: a field error first, else the check's (a typed password replaces the missing entry).</summary>
    public string? CanvasError =>
        FirstError ?? (Kind == NodeKind.SetPassword && TypedPassword is not null ? null : CheckError);

    /// <summary>The line on the card: what the node does with its settings.</summary>
    public string Summary => NodeForms.Summary(this);

    public void ShowErrors()
    {
        foreach (var field in Fields)
        {
            field.ShowErrors();
        }
    }

    /// <summary>The node as the profile stores it.</summary>
    public FlowNode ToFlowNode()
    {
        var settings = (JsonObject)_settings.DeepClone();
        foreach (var field in Fields)
        {
            if (field.Value is { } value)
            {
                settings[field.Key] = value.DeepClone();
            }
            else
            {
                settings.Remove(field.Key);
            }
        }

        return new FlowNode
        {
            Id = Id,
            Kind = Kind,
            ContinueOnError = !AlwaysStops && ContinueOnError,
            Settings = JsonSerializer.SerializeToElement(settings),
        };
    }

    partial void OnContinueOnErrorChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnCheckErrorChanged(string? value) => OnPropertyChanged(nameof(CanvasError));

    private void OnFieldChanged()
    {
        OnPropertyChanged(nameof(CanvasError));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static JsonObject? Settings(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } e ? JsonNode.Parse(e.GetRawText()) as JsonObject : null;
}

/// <summary>
/// The fields of each node kind (spec "Nodes"). A kind this page has no form for gets one field per key of its default
/// settings (text, list, yes or no, number), labelled from the key; a new kind gets its own entry here.
/// </summary>
public static class NodeForms
{
    public static IReadOnlyList<NodeFieldViewModel> Create(NodeKind kind, JsonObject settings, JsonObject? defaults, IReadOnlyList<CredentialChoice> credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return kind switch
        {
            NodeKind.SetPassword => [new PasswordFieldViewModel(Guid(settings["credentialId"]), credentials)],
            NodeKind.AddToOadm => [new ListFieldViewModel("tags", ProvisioningTexts.Tags, Strings(settings["tags"])) { Hint = ProvisioningTexts.Optional }],
            NodeKind.WaitForApproval => [],
            NodeKind.SetIp =>
            [
                new TextFieldViewModel("first", ProvisioningTexts.FirstAddress, Text(settings["first"]), FieldRules.RequiredAddress) { Placeholder = "e.g. 192.168.15.10" },
                new TextFieldViewModel("last", ProvisioningTexts.LastAddress, Text(settings["last"]), FieldRules.RequiredAddress) { Placeholder = "e.g. 192.168.15.50" },
                new TextFieldViewModel("subnetMask", ProvisioningTexts.SubnetMask, Text(settings["subnetMask"]), FieldRules.Mask) { Placeholder = "e.g. 255.255.255.0" },
                new TextFieldViewModel("gateway", ProvisioningTexts.DefaultRouter, Text(settings["gateway"]), FieldRules.RequiredAddress) { Placeholder = "e.g. 192.168.15.1" },
                new ListFieldViewModel("dnsServers", ProvisioningTexts.DnsServers, Strings(settings["dnsServers"]), FieldRules.AddressItem) { Hint = ProvisioningTexts.Optional },
            ],
            NodeKind.DateTime =>
            [
                new TextFieldViewModel("timeZone", ProvisioningTexts.TimeZone, Text(settings["timeZone"]), t => t.Length == 0 ? "Choose a time zone." : null)
                {
                    Placeholder = ProvisioningTexts.TimeZonePlaceholder,
                },
                new ListFieldViewModel("ntpServers", ProvisioningTexts.NtpServers, Strings(settings["ntpServers"])) { Hint = ProvisioningTexts.NtpHint },
                new BoolFieldViewModel("nts", ProvisioningTexts.Nts, settings["nts"]?.GetValueKind() == JsonValueKind.True),
            ],
            _ => Generic(settings, defaults),
        };
    }

    /// <summary>The line on a node's card in the editor.</summary>
    public static string Summary(NodeEditorViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string? Of(string key) => node.Fields.FirstOrDefault(f => f.Key == key) switch
        {
            TextFieldViewModel t => t.Text.Trim(),
            ListFieldViewModel l => string.Join(", ", l.Items),
            _ => null,
        };

        switch (node.Kind)
        {
            case NodeKind.SetPassword:
                var password = node.Fields.OfType<PasswordFieldViewModel>().First();
                return password.UseCredential
                    ? password.SelectedCredential is null ? string.Empty : "root, from the credential list"
                    : password.Password.Length > 0 ? "root, a new password" : string.Empty;
            case NodeKind.AddToOadm:
                return Of("tags") is { Length: > 0 } tags ? "Tags " + tags : "No tags";
            case NodeKind.WaitForApproval:
                return ProvisioningTexts.WaitsForContinue;
            case NodeKind.SetIp:
                return Of("first") is { Length: > 0 } first ? first + " to " + Of("last") : string.Empty;
            case NodeKind.DateTime:
                var zone = Of("timeZone");
                var servers = Of("ntpServers");
                return string.IsNullOrEmpty(zone) ? string.Empty : zone + ", " + (string.IsNullOrEmpty(servers) ? "NTP from DHCP" : servers);
            default:
                return string.Empty;
        }
    }

    /// <summary>A field per key of the defaults (and of the stored settings): the label from the key ("hostNamePattern": "Host name pattern").</summary>
    private static List<NodeFieldViewModel> Generic(JsonObject settings, JsonObject? defaults)
    {
        var keys = (defaults?.Select(p => p.Key) ?? []).Concat(settings.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList();
        var fields = new List<NodeFieldViewModel>();
        foreach (var key in keys)
        {
            var value = settings[key] ?? defaults?[key];
            var label = Label(key);
            NodeFieldViewModel? field = value?.GetValueKind() switch
            {
                JsonValueKind.True or JsonValueKind.False => new BoolFieldViewModel(key, label, value.GetValueKind() == JsonValueKind.True),
                JsonValueKind.Array => new ListFieldViewModel(key, label, Strings(value)) { Hint = ProvisioningTexts.ListHint },
                JsonValueKind.Number => new TextFieldViewModel(key, label, value.ToJsonString(), FieldRules.Number) { IsNumber = true },
                JsonValueKind.String or null => new TextFieldViewModel(key, label, Text(value)),
                _ => null, // an object: the page cannot edit it; the node keeps it as it is
            };
            if (field is not null)
            {
                fields.Add(field);
            }
        }

        return fields;
    }

    /// <summary>"hostNamePattern" as "Host name pattern".</summary>
    public static string Label(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var text = new StringBuilder();
        foreach (var c in key)
        {
            if (char.IsUpper(c) && text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(text.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    private static string? Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node?.ToJsonString();

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(i => i?.GetValueKind() == JsonValueKind.String ? i.GetValue<string>() : i?.ToJsonString() ?? string.Empty) : [];

    private static Guid? Guid(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String && System.Guid.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, out var id) ? id : null;
}
