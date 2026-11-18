using System.Globalization;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// One field of a node's settings in the side panel of the profile editor: its key in the settings record, its label, its
/// rule (the error shows directly below the input) and its JSON value. <see cref="Changed"/> after every edit.
/// </summary>
public abstract class NodeFieldViewModel : ValidatingViewModel
{
    protected NodeFieldViewModel(string key, string label)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    /// <summary>The key in the settings record ("first").</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>A line below the input while it has no error ("Optional").</summary>
    public string? Hint { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>The value to store under <see cref="Key"/>; null: the key is left out (the kind's default).</summary>
    public abstract JsonNode? Value { get; }

    /// <summary>The first error of this field, shown or not.</summary>
    public string? FirstError => Validation.FirstError;

    public event EventHandler? Changed;

    /// <summary>Save was tried: every error shows.</summary>
    public void ShowErrors() => Validation.ShowAll();

    /// <summary>A value changed: <see cref="Changed"/> once the rules ran (the node reads the new errors).</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(HasErrors) or nameof(IsFormValid) or nameof(FormError)))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>A text: a name, an address, a time zone. The value is the trimmed text; empty: left out.</summary>
public sealed partial class TextFieldViewModel : NodeFieldViewModel
{
    private readonly Func<string, string?>? _rule;

    public TextFieldViewModel(string key, string label, string? text, Func<string, string?>? rule = null)
        : base(key, label)
    {
        _rule = rule;
        Text = text ?? string.Empty;
        Validation.Rule(nameof(Text), () => _rule?.Invoke(Text.Trim()));
        Validation.Reset();
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>The value is a number (a setting of a kind this page has no form for).</summary>
    public bool IsNumber { get; init; }

    public override JsonNode? Value
    {
        get
        {
            var text = Text.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            return IsNumber && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(text);
        }
    }

}

/// <summary>A list typed with commas ("10.0.0.1, 10.0.0.2"): the value is a JSON array; empty: left out.</summary>
public sealed partial class ListFieldViewModel : NodeFieldViewModel
{
    private readonly Func<string, string?>? _itemRule;

    public ListFieldViewModel(string key, string label, IEnumerable<string>? items, Func<string, string?>? itemRule = null)
        : base(key, label)
    {
        _itemRule = itemRule;
        Text = string.Join(", ", items ?? []);
        Validation.Rule(nameof(Text), () => _itemRule is null ? null : Items.Select(_itemRule).FirstOrDefault(e => e is not null));
        Validation.Reset();
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>The items, trimmed, without empty ones.</summary>
    public IReadOnlyList<string> Items =>
        [.. Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public override JsonNode? Value
    {
        get
        {
            var items = Items;
            return items.Count == 0 ? null : new JsonArray([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);
        }
    }

}

/// <summary>A yes or no (a check box).</summary>
public sealed partial class BoolFieldViewModel : NodeFieldViewModel
{
    public BoolFieldViewModel(string key, string label, bool value)
        : base(key, label)
    {
        IsChecked = value;
    }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public override JsonNode? Value => JsonValue.Create(IsChecked);

}

/// <summary>An entry of the credential list (never its password).</summary>
public sealed record CredentialChoice(Guid Id, string UserName)
{
    public override string ToString() => UserName;
}

/// <summary>
/// The root password of Set password: an entry of the credential list, or a password typed here (twice). A typed password
/// goes to the server once, with Save (<see cref="SaveProfileRequest.Password"/>), lands in the credential list and is never
/// shown again: the node keeps only the entry's id.
/// </summary>
public sealed partial class PasswordFieldViewModel : NodeFieldViewModel
{
    public const string CredentialKey = "credentialId";

    public PasswordFieldViewModel(Guid? credentialId, IReadOnlyList<CredentialChoice> credentials)
        : base(CredentialKey, ProvisioningTexts.Password)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Credentials = credentials;
        SelectedCredential = credentials.FirstOrDefault(c => c.Id == credentialId)
            ?? (credentialId is null ? credentials.FirstOrDefault(c => string.Equals(c.UserName, "root", StringComparison.Ordinal)) : null);
        UseCredential = credentials.Count > 0 || credentialId is not null;
        Validation
            .Rule(nameof(SelectedCredential), () => UseCredential && SelectedCredential is null
                ? (Credentials.Count == 0 ? ProvisioningTexts.NoRootCredential : ProvisioningTexts.ChooseCredential)
                : null)
            .Rule(nameof(Password), () => UseCredential ? null
                : Password.Length == 0 ? ProvisioningTexts.EnterPassword
                : Password.Length > 64 || Password.Any(c => c is < (char)0x20 or > (char)0x7E) ? ProvisioningTexts.PasswordRule
                : null)
            .Rule(nameof(Confirm), () => UseCredential || Confirm == Password ? null : ProvisioningTexts.PasswordsDiffer);
        Validation.Reset();
        Validation.Validate();
    }

    /// <summary>The credential list's entries of root.</summary>
    public IReadOnlyList<CredentialChoice> Credentials { get; }

    /// <summary>True: an entry of the credential list; false: a password typed here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypesPassword))]
    public partial bool UseCredential { get; set; }

    public bool TypesPassword
    {
        get => !UseCredential;
        set => UseCredential = !value;
    }

    [ObservableProperty]
    public partial CredentialChoice? SelectedCredential { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Confirm { get; set; } = string.Empty;

    /// <summary>The typed password to send with Save; null when an entry of the credential list is chosen.</summary>
    public string? TypedPassword => !UseCredential && IsFormValid ? Password : null;

    public override JsonNode? Value => UseCredential && SelectedCredential is { } c ? JsonValue.Create(c.Id.ToString()) : null;

    /// <summary>After Save: the typed password is in the credential list now; the field forgets it and points at the entry.</summary>
    public void Saved(CredentialChoice? entry)
    {
        Password = string.Empty;
        Confirm = string.Empty;
        if (entry is not null)
        {
            SelectedCredential = entry;
            UseCredential = true;
        }

        Validation.Reset();
        Validation.Validate();
    }

}

/// <summary>The rules of the fields (the server checks the node again: one error per node, shown on the canvas).</summary>
public static class FieldRules
{
    public static string? RequiredAddress(string text) =>
        text.Length == 0 ? ProvisioningTexts.EnterAddress : Ipv4.TryParse(text, out _) ? null : ProvisioningTexts.NotAnAddress;

    public static string? Address(string text) =>
        text.Length == 0 || Ipv4.TryParse(text, out _) ? null : ProvisioningTexts.NotAnAddress;

    public static string? AddressItem(string text) => Ipv4.TryParse(text, out _) ? null : ProvisioningTexts.NotAnAddressList(text);

    public static string? Mask(string text) =>
        text.Length == 0 ? ProvisioningTexts.EnterAddress : Ipv4.TryParsePrefix(text, out _) ? null : ProvisioningTexts.NotAMask;

    public static string? Number(string text) =>
        text.Length == 0 || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? null : "Enter a number.";

    /// <summary>The root entries of the credential list.</summary>
    public static IReadOnlyList<CredentialChoice> RootEntries(IEnumerable<DeviceCredentialName> credentials) =>
        [.. credentials.Where(c => string.Equals(c.UserName, "root", StringComparison.Ordinal)).Select(c => new CredentialChoice(c.Id, c.UserName))];
}
