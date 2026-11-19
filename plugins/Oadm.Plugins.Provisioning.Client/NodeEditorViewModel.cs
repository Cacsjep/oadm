using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// One node of the profile in the editor: its kind, Continue on error, its fields (the side panel) and the profile check's
/// error and warning for it (on the canvas). <see cref="ToFlowNode"/> writes the fields into the settings record and keeps
/// every key it has no field for (a Certificate's policy).
/// </summary>
public sealed partial class NodeEditorViewModel : ObservableObject
{
    private readonly JsonObject _settings;
    private bool _refreshing;

    public NodeEditorViewModel(FlowNode node, NodeKindInfo? kind, INodeFormServices services)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(services);
        Id = node.Id;
        Kind = node.Kind;
        Name = kind?.Name ?? node.Kind.ToString();
        IsFixed = kind?.IsFixed ?? node.Kind is NodeKind.SetPassword or NodeKind.AddToOadm;
        AlwaysStops = kind?.AlwaysStops ?? node.Kind == NodeKind.SetPassword;
        ContinueOnError = !AlwaysStops && node.ContinueOnError;
        _settings = Settings(node.Settings) ?? Settings(kind?.DefaultSettings) ?? [];
        Fields = NodeForms.Create(node.Kind, _settings, services);
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

    /// <summary>What the node does where its fields do not say it (Wait for approval, 802.1X, Autofocus, the MyAxis sign-in).</summary>
    public string? Note => NodeForms.Note(Kind);

    /// <summary>The profile check's error for this node (on the canvas, below the card).</summary>
    [ObservableProperty]
    public partial string? CheckError { get; set; }

    /// <summary>The profile check's warning for this node (on the canvas, below the card, in amber).</summary>
    [ObservableProperty]
    public partial string? CheckWarning { get; set; }

    /// <summary>The fields' errors are all gone (shown or not).</summary>
    public bool IsValid => Fields.All(f => f.IsValid);

    /// <summary>The first field error.</summary>
    public string? FirstError => Fields.Select(f => f.FirstError).FirstOrDefault(e => e is not null);

    /// <summary>The password typed in Set password, sent once with Save; null otherwise.</summary>
    public string? TypedPassword => Fields.OfType<PasswordFieldViewModel>().FirstOrDefault()?.TypedPassword;

    /// <summary>The typed passwords and passphrases that go to the server with Save.</summary>
    public IEnumerable<SecretFieldViewModel> Secrets => Fields.SelectMany(f => f.Secrets);

    /// <summary>A field or Continue on error changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The error shown on the canvas: a field error first, else the check's. A typed secret replaces the missing one the
    /// server's check names (it goes to the server with Save).
    /// </summary>
    public string? CanvasError =>
        FirstError ?? (HasTypedSecret ? null : CheckError);

    /// <summary>The line on the card: what the node does with its settings.</summary>
    public string Summary => NodeForms.Summary(this);

    private bool HasTypedSecret => TypedPassword is not null || Secrets.Any(s => s.Pending is not null);

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
            field.Write(settings);
        }

        return new FlowNode
        {
            Id = Id,
            Kind = Kind,
            ContinueOnError = !AlwaysStops && ContinueOnError,
            Settings = JsonSerializer.SerializeToElement(settings),
        };
    }

    /// <summary>The field of a key (tests and summaries).</summary>
    public T Field<T>(string key)
        where T : NodeFieldViewModel => Fields.OfType<T>().First(f => f.Key == key);

    partial void OnContinueOnErrorChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnCheckErrorChanged(string? value) => OnPropertyChanged(nameof(CanvasError));

    private void OnFieldChanged()
    {
        if (_refreshing)
        {
            return;
        }

        // A choice shows or hides other fields, and rules read other fields: every field checks again.
        _refreshing = true;
        try
        {
            foreach (var field in Fields)
            {
                field.Refresh();
            }
        }
        finally
        {
            _refreshing = false;
        }

        OnPropertyChanged(nameof(CanvasError));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static JsonObject? Settings(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } e ? JsonNode.Parse(e.GetRawText()) as JsonObject : null;
}

/// <summary>
/// The form of each node kind (spec "Nodes"): its fields with their labels and rules (the server's check of the node, field
/// by field), the line on its card and its note. A new kind gets its entry here and in <see cref="NodeLook"/>.
/// </summary>
public static class NodeForms
{
    public static IReadOnlyList<NodeFieldViewModel> Create(NodeKind kind, JsonObject settings, INodeFormServices services)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(services);
        return kind switch
        {
            NodeKind.SetPassword => [new PasswordFieldViewModel(FieldRules.ToGuid(settings["credentialId"]), services.Credentials, services)],
            NodeKind.AddToOadm => [new ListFieldViewModel("tags", ProvisioningTexts.Tags, Strings(settings["tags"]), TagRule) { Hint = ProvisioningTexts.Optional, Help = ProvisioningTexts.TagsHelp }],
            NodeKind.WaitForApproval or NodeKind.Dot1x or NodeKind.Autofocus => [],
            NodeKind.SetIp => SetIp(settings),
            NodeKind.DateTime => DateTime(settings),
            NodeKind.UpgradeOnline => UpgradeOnline(settings),
            NodeKind.UpgradeFiles => [new FilesFieldViewModel("files", ProvisioningTexts.OsFiles, OsFiles(settings["files"]), services) { Help = ProvisioningTexts.OsFilesHelp }],
            NodeKind.NetworkSettings => NetworkSettings(settings),
            NodeKind.Configuration => Configuration(settings, services),
            NodeKind.Users => [new UsersFieldViewModel(settings["accounts"] as JsonArray)],
            NodeKind.Certificate => Certificate(settings),
            NodeKind.SdCard => SdCard(settings),
            NodeKind.VapixCommands => [new VapixCommandsFieldViewModel(settings["commands"] as JsonArray, services.Commands)],
            _ => [],
        };
    }

    /// <summary>What a node does where its fields do not say it; null for most.</summary>
    public static string? Note(NodeKind kind) => kind switch
    {
        NodeKind.WaitForApproval => ProvisioningTexts.WaitHint,
        NodeKind.Dot1x => ProvisioningTexts.Dot1xHint,
        NodeKind.Autofocus => ProvisioningTexts.AutofocusHint,
        NodeKind.UpgradeOnline => ProvisioningTexts.MyAxisHint,
        NodeKind.UpgradeFiles => ProvisioningTexts.OsFilesHint,
        NodeKind.SetPassword or NodeKind.AddToOadm or NodeKind.SetIp or NodeKind.DateTime or NodeKind.NetworkSettings or NodeKind.Configuration
            or NodeKind.Users or NodeKind.Certificate or NodeKind.SdCard or NodeKind.VapixCommands => null,
        _ => ProvisioningTexts.UnknownNode,
    };

    /// <summary>
    /// The settings of a node just added (the + or a new profile): the kind's default; a new Set IP starts with the first
    /// three numbers of the DHCP server's range in First and Last address ("192.168.15.", the user types the last one) and
    /// the DHCP server's subnet mask, else 255.255.255.0. Saved nodes are never changed.
    /// </summary>
    /// <param name="kind">The kind of the new node.</param>
    /// <param name="dhcp">The OADM DHCP server's summary from the page's status; null when it is not installed.</param>
    public static JsonElement NewSettings(NodeKindInfo kind, Sdk.Network.DhcpServerSummary? dhcp)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (kind.Kind != NodeKind.SetIp)
        {
            return kind.DefaultSettings;
        }

        var settings = (kind.DefaultSettings.ValueKind == JsonValueKind.Object ? JsonNode.Parse(kind.DefaultSettings.GetRawText()) as JsonObject : null) ?? [];
        if ((NetworkPart(dhcp?.RangeStart) ?? NetworkPart(dhcp?.ServerAddress)) is { } start)
        {
            settings["first"] = start;
            settings["last"] = start;
        }

        settings["subnetMask"] = Network.Model.Ipv4.TryParsePrefix(dhcp?.SubnetMask, out var prefix) && prefix is > 0 and < 32
            ? Network.Model.Ipv4.MaskText(prefix)
            : Network.Model.Ipv4.MaskText(24);
        return JsonSerializer.SerializeToElement(settings);
    }

    /// <summary>"192.168.15.7" -> "192.168.15."; null when not an address.</summary>
    private static string? NetworkPart(string? address) =>
        Network.Model.Ipv4.TryParse(address, out var value)
            ? string.Create(CultureInfo.InvariantCulture, $"{value >> 24}.{(value >> 16) & 255}.{(value >> 8) & 255}.")
            : null;

    private static IReadOnlyList<NodeFieldViewModel> SetIp(JsonObject s) =>
    [
        new TextFieldViewModel("first", ProvisioningTexts.FirstAddress, Text(s["first"]), FieldRules.RequiredAddress) { Placeholder = "e.g. 192.168.15.10", Help = ProvisioningTexts.FirstAddressHelp },
        new TextFieldViewModel("last", ProvisioningTexts.LastAddress, Text(s["last"]), FieldRules.RequiredAddress) { Placeholder = "e.g. 192.168.15.50", Help = ProvisioningTexts.LastAddressHelp },
        new TextFieldViewModel("subnetMask", ProvisioningTexts.SubnetMask, Text(s["subnetMask"]), FieldRules.Mask) { Placeholder = "e.g. 255.255.255.0", Help = ProvisioningTexts.MaskHelp },
        new TextFieldViewModel("gateway", ProvisioningTexts.DefaultRouter, Text(s["gateway"]), FieldRules.RequiredAddress) { Placeholder = "e.g. 192.168.15.1", Help = ProvisioningTexts.RouterHelp },
        new ListFieldViewModel("dnsServers", ProvisioningTexts.DnsServers, Strings(s["dnsServers"]), FieldRules.AddressItem, 2, "Enter at most 2 DNS servers.")
        {
            Hint = ProvisioningTexts.Optional,
            WritesEmpty = true,
            Help = ProvisioningTexts.DnsHelp,
        },
    ];

    private static IReadOnlyList<NodeFieldViewModel> DateTime(JsonObject s)
    {
        var servers = Strings(s["ntpServers"]).ToList();
        var source = new ChoiceFieldViewModel("ntpSource", ProvisioningTexts.TimeSource,
            [new("dhcp", ProvisioningTexts.NtpFromDhcp), new("own", ProvisioningTexts.NtpOwn), new("oadm", ProvisioningTexts.NtpOadm)],
            s["oadmNtp"]?.GetValueKind() == JsonValueKind.True ? "oadm" : servers.Count > 0 ? "own" : "dhcp")
        {
            WriteAs = settings => { },
            Help = ProvisioningTexts.TimeSourceHelp,
        };
        ListFieldViewModel? list = null;
        list = new ListFieldViewModel("ntpServers", ProvisioningTexts.NtpServers, servers, FieldRules.HostItem, DateTimeNode.MaxServers, $"Enter at most {DateTimeNode.MaxServers} NTP servers.")
        {
            Hint = ProvisioningTexts.ListHint,
            Help = ProvisioningTexts.NtpServersHelp,
            ShownWhen = () => source.Is("own"),
            WriteAs = settings =>
            {
                settings["ntpServers"] = source.Is("own") ? list!.Value ?? new JsonArray() : new JsonArray();
                settings["oadmNtp"] = source.Is("oadm");
            },
        };
        BoolFieldViewModel? nts = null;
        nts = new BoolFieldViewModel("nts", ProvisioningTexts.Nts, s["nts"]?.GetValueKind() == JsonValueKind.True)
        {
            ShownWhen = () => source.Is("own"),
            WriteAs = settings => settings["nts"] = source.Is("own") && nts!.IsChecked,
            Help = ProvisioningTexts.NtsHelp,
        };
        return
        [
            new TimeZoneFieldViewModel("timeZone", ProvisioningTexts.TimeZone, Text(s["timeZone"])) { Help = ProvisioningTexts.TimeZoneHelp },
            source,
            list,
            nts,
        ];
    }

    private static IReadOnlyList<NodeFieldViewModel> UpgradeOnline(JsonObject s) =>
    [
        new ChoiceFieldViewModel("target", ProvisioningTexts.OsTarget,
            [new("ownTrack", ProvisioningTexts.OwnTrack), new("latestLts", ProvisioningTexts.LatestLts), new("latestActive", ProvisioningTexts.LatestActive)],
            Text(s["target"])) { Help = ProvisioningTexts.OsTargetHelp },
        new TextFieldViewModel("onlyIfOlderThan", ProvisioningTexts.OnlyIfOlder, Text(s["onlyIfOlderThan"]), FieldRules.Version)
        {
            Placeholder = "e.g. 12.6.95",
            Help = ProvisioningTexts.OnlyIfOlderHelp,
            Hint = ProvisioningTexts.Optional,
        },
    ];

    private static IReadOnlyList<NodeFieldViewModel> NetworkSettings(JsonObject s) =>
    [
        new ListFieldViewModel("dnsServers", ProvisioningTexts.DnsServers, Strings(s["dnsServers"]), FieldRules.AddressItem, NetworkSettingsNode.MaxDns,
            $"Enter at most {NetworkSettingsNode.MaxDns} DNS servers.", NetworkSettingsNode.EnterDns)
        {
            WritesEmpty = true,
            Help = ProvisioningTexts.DnsHelp,
        },
        new TextFieldViewModel("searchDomain", ProvisioningTexts.SearchDomain, Text(s["searchDomain"]), FieldRules.Domain)
        {
            Placeholder = "e.g. site.example.com",
            Help = ProvisioningTexts.SearchDomainHelp,
            Hint = ProvisioningTexts.Optional,
        },
    ];

    private static IReadOnlyList<NodeFieldViewModel> Configuration(JsonObject s, INodeFormServices services)
    {
        var source = new ChoiceFieldViewModel("source", ProvisioningTexts.ConfigSource,
            [new("file", ProvisioningTexts.ConfigFromFile), new("templateCamera", ProvisioningTexts.ConfigFromCamera)], Text(s["source"])) { Help = ProvisioningTexts.ConfigSourceHelp };
        BoolFieldViewModel Area(string key, string label, bool fallback) =>
            new(key, label, s[key]?.GetValueKind() is { } k && k is JsonValueKind.True or JsonValueKind.False ? k == JsonValueKind.True : fallback);
        var areas = new[] { Area("parameters", ProvisioningTexts.Parameters, true), Area("actionRules", ProvisioningTexts.ActionRules, false), Area("schedules", ProvisioningTexts.Schedules, false) };
        return
        [
            source,
            new FileFieldViewModel("fileId", ProvisioningTexts.ConfigFile, FieldRules.ToGuid(s["fileId"]), Text(s["fileName"]), services, ["*.json"],
                name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? null : ProvisioningTexts.ConfigFileRule)
            {
                NameKey = "fileName",
                Help = ProvisioningTexts.ConfigFileHelp,
                ShownWhen = () => source.Is("file"),
            },
            new CameraFieldViewModel("templateSerial", ProvisioningTexts.TemplateCamera, Text(s["templateSerial"]), services)
            {
                ShownWhen = () => source.Is("templateCamera"),
                Help = ProvisioningTexts.TemplateCameraHelp,
            },
            new CheckGroupFieldViewModel(ProvisioningTexts.Apply, areas, () => areas.Any(a => a.IsChecked) ? null : ProvisioningTexts.ChooseAreas) { Help = ProvisioningTexts.ApplyHelp },
        ];
    }

    private static IReadOnlyList<NodeFieldViewModel> Certificate(JsonObject s)
    {
        var mode = new ChoiceFieldViewModel("mode", ProvisioningTexts.CertificateMode,
            [new("policy", ProvisioningTexts.CertificatePolicy), new("est", ProvisioningTexts.CertificateEst)], Text(s["mode"])) { Help = ProvisioningTexts.CertificateHelp };
        return [mode];
    }

    private static IReadOnlyList<NodeFieldViewModel> SdCard(JsonObject s)
    {
        var format = new BoolFieldViewModel("format", ProvisioningTexts.Format, s["format"]?.GetValueKind() != JsonValueKind.False);
        var encrypt = new BoolFieldViewModel("encrypt", ProvisioningTexts.Encrypt, s["encrypt"]?.GetValueKind() == JsonValueKind.True);
        var days = new TextFieldViewModel("maxAgeDays", ProvisioningTexts.KeepDays, NumberText.Of(s["maxAgeDays"]),
            FieldRules.Whole(1, SdCardNode.MaxDays, $"Enter 1 to {SdCardNode.MaxDays} days.", optional: true))
        {
            IsInteger = true,
            Hint = ProvisioningTexts.KeepDaysHint,
            Help = ProvisioningTexts.KeepDaysHelp,
        };
        return
        [
            new CheckGroupFieldViewModel(ProvisioningTexts.SdCardDo, [format, encrypt],
                () => format.IsChecked || encrypt.IsChecked || days.Text.Trim().Length > 0 ? null : ProvisioningTexts.ChooseSdCard) { Help = ProvisioningTexts.PrepareHelp },
            new SecretFieldViewModel(ProvisioningTexts.Passphrase, FieldRules.ToGuid(s["secretId"]), rule: FieldRules.Passphrase) { ShownWhen = () => encrypt.IsChecked, Help = ProvisioningTexts.SecretHelp },
            days,
        ];
    }

    /// <summary>The line on a node's card in the editor.</summary>
    public static string Summary(NodeEditorViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string Of(string key) => node.Fields.FirstOrDefault(f => f.Key == key) switch
        {
            TextFieldViewModel t => t.Text.Trim(),
            ListFieldViewModel l => string.Join(", ", l.Items),
            ChoiceFieldViewModel c => c.Selected.Label,
            TimeZoneFieldViewModel z => z.Id ?? string.Empty,
            _ => string.Empty,
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
                // A half typed pool ("192.168.15." of a new node) has no line yet.
                return Network.Model.Ipv4.TryParse(Of("first"), out _) && Network.Model.Ipv4.TryParse(Of("last"), out _) ? Of("first") + " to " + Of("last") : string.Empty;
            case NodeKind.DateTime:
                var source = node.Field<ChoiceFieldViewModel>("ntpSource");
                var time = source.Is("own") ? Of("ntpServers") : source.Is("oadm") ? "OADM's NTP server" : "NTP from DHCP";
                return Of("timeZone") is { Length: > 0 } zone ? (time.Length > 0 ? zone + ", " + time : zone) : time;
            case NodeKind.UpgradeOnline:
                return Of("target") + (Of("onlyIfOlderThan") is { Length: > 0 } older ? ", if older than " + older : string.Empty);
            case NodeKind.UpgradeFiles:
                var files = node.Field<FilesFieldViewModel>("files").Count;
                return files == 0 ? string.Empty : ProvisioningTexts.FileCount(files);
            case NodeKind.NetworkSettings:
                return Of("dnsServers") is { Length: > 0 } dns ? "DNS " + dns : string.Empty;
            case NodeKind.Configuration:
                var from = node.Field<ChoiceFieldViewModel>("source");
                var areas = node.Fields.OfType<CheckGroupFieldViewModel>().First().Items.Where(i => i.IsChecked).Select(i => i.Label.ToLowerInvariant()).ToList();
                var what = areas.Count == 0 ? string.Empty : string.Join(", ", areas);
                var where = from.Is("file")
                    ? node.Field<FileFieldViewModel>("fileId").FileName ?? string.Empty
                    : node.Field<CameraFieldViewModel>("templateSerial").Serial is { } serial ? "like " + node.Field<CameraFieldViewModel>("templateSerial").ButtonText : string.Empty;
                return string.Join(" from ", new[] { char.ToUpperInvariant(what.FirstOrDefault(' ')) + (what.Length > 1 ? what[1..] : string.Empty), where }.Where(p => p.Trim().Length > 0));
            case NodeKind.Users:
                var users = node.Field<UsersFieldViewModel>("accounts").Rows.Select(r => r.Name.Trim()).Where(n => n.Length > 0).ToList();
                return users.Count == 0 ? string.Empty : string.Join(", ", users);
            case NodeKind.Certificate:
                return Of("mode");
            case NodeKind.Dot1x:
                return ProvisioningTexts.Dot1xLine;
            case NodeKind.SdCard:
                var parts = node.Fields.OfType<CheckGroupFieldViewModel>().First().Items.Where(i => i.IsChecked).Select(i => i.Label.ToLowerInvariant()).ToList();
                if (Of("maxAgeDays") is { Length: > 0 } keep)
                {
                    parts.Add($"keep {keep} days");
                }

                return parts.Count == 0 ? string.Empty : char.ToUpperInvariant(string.Join(", ", parts)[0]) + string.Join(", ", parts)[1..];
            case NodeKind.Autofocus:
                return ProvisioningTexts.AutofocusLine;
            case NodeKind.VapixCommands:
                var commands = node.Field<VapixCommandsFieldViewModel>("commands").Rows.Select(r => r.Command?.Name).OfType<string>().ToList();
                return commands.Count == 0 ? string.Empty : string.Join(", ", commands);
            default:
                return string.Empty;
        }
    }

    private static string? TagRule(string tag) => tag.Length > 64 ? "A tag has at most 64 characters." : null;

    private static string? Text(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.ToJsonString(),
        _ => null,
    };

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(i => i?.GetValueKind() == JsonValueKind.String ? i.GetValue<string>() : i?.ToJsonString() ?? string.Empty) : [];

    private static IEnumerable<OsFileRow> OsFiles(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonObject>()
                .Select(f => (Id: FieldRules.ToGuid(f["fileId"]), Name: Text(f["name"]) ?? string.Empty))
                .Where(f => f.Id is not null)
                .Select(f => new OsFileRow(f.Id!.Value, f.Name, UpgradeFilesNode.Product(f.Name)))
            : [];

    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
