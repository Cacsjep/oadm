using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// One field of a node's settings in the side panel of the profile editor: its key in the settings record, its label, its
/// rules (the error shows directly below the input, never while the field is hidden) and how it writes its value.
/// <see cref="Changed"/> after every edit, once the rules ran.
/// </summary>
public abstract class NodeFieldViewModel : ValidatingViewModel
{
    private static readonly HashSet<string> Quiet = new(StringComparer.Ordinal)
    {
        nameof(HasErrors), nameof(IsFormValid), nameof(FormError), nameof(IsShown), "IsBusy", "Summary", "Error",
        "RevealedPassword", "IsRevealed", "PasswordText", "RevealError", "HasRevealError", "ShowsStoredPassword",
    };

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

    /// <summary>The short help of the "?" at the right inside the field (<c>FormField.Help</c>); never the label again.</summary>
    public string? Help { get; init; }

    /// <summary>When the field shows (another field's choice); null: always.</summary>
    public Func<bool>? ShownWhen { get; init; }

    public bool IsShown => ShownWhen?.Invoke() ?? true;

    /// <summary>The value to store under <see cref="Key"/>; null: the key is left out (the kind's default).</summary>
    public abstract JsonNode? Value { get; }

    /// <summary>The first error of this field, shown or not; none while hidden.</summary>
    public virtual string? FirstError => IsShown ? Validation.FirstError : null;

    /// <summary>The field is valid (hidden fields always are).</summary>
    public virtual bool IsValid => !IsShown || Validation.IsValid;

    public event EventHandler? Changed;

    /// <summary>Save was tried: every error shows.</summary>
    public virtual void ShowErrors() => Validation.ShowAll();

    /// <summary>Another field changed: the rules run again and the field shows or hides.</summary>
    public virtual void Refresh()
    {
        Validation.Validate();
        OnPropertyChanged(nameof(IsShown));
    }

    /// <summary>Writes the field its own way (a choice that sets other keys); null: under <see cref="Key"/>.</summary>
    public Action<JsonObject>? WriteAs { get; init; }

    /// <summary>Writes the value into the settings record.</summary>
    public virtual void Write(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (WriteAs is { } write)
        {
            write(settings);
            return;
        }

        if (Value is { } value)
        {
            settings[Key] = value.DeepClone();
        }
        else
        {
            settings.Remove(Key);
        }
    }

    /// <summary>The typed secrets of this field that go to the server with Save.</summary>
    public virtual IEnumerable<SecretFieldViewModel> Secrets => [];

    /// <summary>A rule that holds only while the field shows.</summary>
    protected void Rule(string property, Func<string?> rule) => Validation.Rule(property, () => IsShown ? rule() : null);

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && !Quiet.Contains(name))
        {
            RaiseChanged();
        }
    }

    /// <summary>Starts the rules: the field is untouched.</summary>
    protected void Ready()
    {
        Validation.Reset();
        Validation.Validate();
    }
}

/// <summary>A text: an address, a time zone, a version, a number. The value is the trimmed text (or number); empty: left out.</summary>
public sealed partial class TextFieldViewModel : NodeFieldViewModel
{
    private readonly Func<string, string?>? _rule;

    public TextFieldViewModel(string key, string label, string? text, Func<string, string?>? rule = null)
        : base(key, label)
    {
        _rule = rule;
        Text = text ?? string.Empty;
        Rule(nameof(Text), () => _rule?.Invoke(Text.Trim()));
        Ready();
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>The value is a whole number.</summary>
    public bool IsInteger { get; init; }

    public override JsonNode? Value
    {
        get
        {
            var text = Text.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            return IsInteger && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(text);
        }
    }
}

/// <summary>A list typed with commas ("10.0.0.1, 10.0.0.2"): the value is a JSON array; empty: left out.</summary>
public sealed partial class ListFieldViewModel : NodeFieldViewModel
{
    private readonly Func<string, string?>? _itemRule;
    private readonly int _max;

    /// <param name="emptyError">The error of an empty list; null when the list may stay empty.</param>
    public ListFieldViewModel(
        string key, string label, IEnumerable<string>? items, Func<string, string?>? itemRule = null, int max = int.MaxValue, string? maxError = null,
        string? emptyError = null)
        : base(key, label)
    {
        _itemRule = itemRule;
        _max = max;
        Text = string.Join(", ", items ?? []);
        Rule(nameof(Text), () => Items.Count == 0 && emptyError is not null ? emptyError
            : Items.Count > _max ? maxError
            : _itemRule is null ? null
            : Items.Select(_itemRule).FirstOrDefault(e => e is not null));
        Ready();
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>The items, trimmed, without empty ones.</summary>
    public IReadOnlyList<string> Items =>
        [.. Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>An empty list is written as [] (not left out).</summary>
    public bool WritesEmpty { get; init; }

    public override JsonNode? Value
    {
        get
        {
            var items = Items;
            return items.Count == 0 && !WritesEmpty ? null : new JsonArray([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);
        }
    }
}

/// <summary>A time zone of AXIS OS, chosen from the list (<see cref="TimeZoneCatalog"/>): the value is its IANA id.</summary>
public sealed partial class TimeZoneFieldViewModel : NodeFieldViewModel
{
    private readonly string? _other;

    public TimeZoneFieldViewModel(string key, string label, string? id)
        : base(key, label)
    {
        Selected = TimeZoneCatalog.Find(id?.Trim());
        _other = Selected is null && !string.IsNullOrWhiteSpace(id) ? id.Trim() : null; // a zone of an older profile stays until another is chosen
        Rule(nameof(Selected), () => Selected is null && _other is null ? ProvisioningTexts.ChooseTimeZone : null);
        Ready();
    }

    /// <summary>Every zone, sorted by UTC offset.</summary>
    public static IReadOnlyList<TimeZoneEntry> Zones => TimeZoneCatalog.All;

    [ObservableProperty]
    public partial TimeZoneEntry? Selected { get; set; }

    /// <summary>The IANA id, or an id of an older profile the list does not have.</summary>
    public string? Id => Selected?.Id ?? _other;

    public override JsonNode? Value => Id is { } id ? JsonValue.Create(id) : null;
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

/// <summary>One entry of a choice: the stored value and what the user reads.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One of several (a select): the value is the chosen entry's value.</summary>
public sealed partial class ChoiceFieldViewModel : NodeFieldViewModel
{
    public ChoiceFieldViewModel(string key, string label, IReadOnlyList<Choice> choices, string? value)
        : base(key, label)
    {
        Choices = choices ?? throw new ArgumentNullException(nameof(choices));
        Selected = choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    public IReadOnlyList<Choice> Choices { get; }

    [ObservableProperty]
    public partial Choice Selected { get; set; }

    public bool Is(string value) => string.Equals(Selected.Value, value, StringComparison.OrdinalIgnoreCase);

    public override JsonNode? Value => JsonValue.Create(Selected.Value);
}

/// <summary>
/// The PKI policy of a Certificate or 802.1X node: PKI's policies of its use by name. One policy is chosen already; with none,
/// the error says to add one on the PKI page. A stored policy PKI no longer has shows <see cref="PolicyChoice.Gone"/>.
/// </summary>
public sealed partial class PolicyFieldViewModel : NodeFieldViewModel
{
    private readonly IssuingPolicyUse _use;
    private readonly bool _gone;

    public PolicyFieldViewModel(string key, string label, IssuingPolicyUse use, Guid? policyId, IReadOnlyList<IssuingPolicyInfo> policies)
        : base(key, label)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _use = use;
        Policies = [.. policies.Where(p => p.Use == use)];
        Selected = policyId is { } id ? Policies.FirstOrDefault(p => p.Id == id) : Policies.Count == 1 ? Policies[0] : null;
        _gone = policyId is not null && Selected is null;
        Rule(nameof(Selected), () => Selected is not null ? null
            : _gone ? PolicyChoice.Gone
            : Policies.Count == 0 ? PolicyChoice.NoneOf(_use)
            : PolicyChoice.Choose);
        Ready();
        if (_gone)
        {
            Validation.ShowAll(); // a policy deleted in PKI shows at once, like the server's check
        }
    }

    public IReadOnlyList<IssuingPolicyInfo> Policies { get; }

    [ObservableProperty]
    public partial IssuingPolicyInfo? Selected { get; set; }

    public string? Error => ErrorOf(nameof(Selected));

    /// <summary>The chosen policy's name for the node's card; empty without one.</summary>
    public string Name => Selected?.Name ?? string.Empty;

    public override JsonNode? Value => Selected is { } p ? JsonValue.Create(p.Id.ToString()) : null;

    partial void OnSelectedChanged(IssuingPolicyInfo? value) => OnPropertyChanged(nameof(Error));
}

/// <summary>An entry of the credential list (never its password) and when it was added, which tells equal user names apart.</summary>
public sealed record CredentialChoice(Guid Id, string UserName, DateTimeOffset? Added = null)
{
    /// <summary>"Added 18.11.2026 14:02"; empty when the host cannot tell.</summary>
    public string AddedText => Added is { } added ? CredentialTexts.Added(added) : string.Empty;

    public override string ToString() => AddedText.Length == 0 ? UserName : UserName + ", " + AddedText.ToLowerInvariant();
}

/// <summary>
/// The root password of Set password: an entry of the credential list, or a password typed here (twice). A typed password
/// goes to the server once, with Save (<see cref="SaveProfileRequest.Password"/>), lands in the credential list and is never
/// shown again: the node keeps only the entry's id. An administrator's eye button shows the chosen entry's stored password
/// through the host (<see cref="INodeFormServices.RevealCredentialAsync"/>, the Credentials page's call with its audit entry);
/// it is masked again and forgotten on the second click, on another choice and when the editor closes
/// (<see cref="HidePassword"/>). It never goes to the server part.
/// </summary>
public sealed partial class PasswordFieldViewModel : NodeFieldViewModel
{
    public const string CredentialKey = "credentialId";

    private readonly INodeFormServices? _services;
    private int _reveal;

    public PasswordFieldViewModel(Guid? credentialId, IReadOnlyList<CredentialChoice> credentials, INodeFormServices? services = null)
        : base(CredentialKey, ProvisioningTexts.Password)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _services = services;
        Credentials = credentials;
        SelectedCredential = credentials.FirstOrDefault(c => c.Id == credentialId)
            ?? (credentialId is null ? credentials.FirstOrDefault(c => string.Equals(c.UserName, "root", StringComparison.Ordinal)) : null);
        UseCredential = credentials.Count > 0 || credentialId is not null;
        Rule(nameof(SelectedCredential), () => UseCredential && SelectedCredential is null
            ? (Credentials.Count == 0 ? ProvisioningTexts.NoRootCredential : ProvisioningTexts.ChooseCredential)
            : null);
        Rule(nameof(Password), () => UseCredential ? null : FieldRules.Password(Password));
        Rule(nameof(Confirm), () => UseCredential || Confirm == Password ? null : ProvisioningTexts.PasswordsDiffer);
        Ready();
    }

    /// <summary>The credential list's entries of root.</summary>
    public IReadOnlyList<CredentialChoice> Credentials { get; }

    /// <summary>True: an entry of the credential list; false: a password typed here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypesPassword), nameof(ShowsStoredPassword))]
    public partial bool UseCredential { get; set; }

    public bool TypesPassword
    {
        get => !UseCredential;
        set => UseCredential = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsStoredPassword))]
    public partial CredentialChoice? SelectedCredential { get; set; }

    /// <summary>The eye shows: an administrator (the server refuses everyone else anyway) and the host can reveal.</summary>
    public bool CanReveal => _services?.CanRevealCredentials ?? false;

    /// <summary>The chosen entry's stored password after the eye asked for it; null while masked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRevealed), nameof(PasswordText))]
    public partial string? RevealedPassword { get; private set; }

    public bool IsRevealed => RevealedPassword is not null;

    /// <summary>The masked or shown password line below the choice: an administrator with an entry chosen.</summary>
    public bool ShowsStoredPassword => CanReveal && UseCredential && SelectedCredential is not null;

    /// <summary>The stored password, or the 8 bullets of the Credentials page while masked.</summary>
    public string PasswordText => RevealedPassword ?? CredentialTexts.Masked;

    /// <summary>Why showing the password failed (below the choice), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRevealError))]
    public partial string? RevealError { get; private set; }

    public bool HasRevealError => RevealError is not null;

    /// <summary>The eye: loads the chosen entry's password through the host and shows it; a second click masks and forgets it.</summary>
    [RelayCommand]
    public async Task ToggleRevealAsync()
    {
        if (IsRevealed)
        {
            HidePassword();
            return;
        }

        if (!CanReveal || _services is null || SelectedCredential is not { } entry)
        {
            return;
        }

        var attempt = ++_reveal;
        RevealError = null;
        try
        {
            var password = await _services.RevealCredentialAsync(entry.Id, CancellationToken.None).ConfigureAwait(true);
            if (attempt == _reveal)
            {
                RevealedPassword = password; // a later choice or hide wins over this answer
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (attempt == _reveal)
            {
                RevealError = CredentialTexts.ShowFailed + ServerErrors.Message(ex);
            }
        }
    }

    /// <summary>Masks and forgets the shown password (another choice, the editor closes).</summary>
    public void HidePassword()
    {
        _reveal++;
        RevealedPassword = null;
        RevealError = null;
    }

    partial void OnSelectedCredentialChanged(CredentialChoice? value) => HidePassword();

    partial void OnUseCredentialChanged(bool value) => HidePassword();

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Confirm { get; set; } = string.Empty;

    /// <summary>The typed password to send with Save; null when an entry of the credential list is chosen.</summary>
    public string? TypedPassword => !UseCredential && Validation.IsValid ? Password : null;

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

        Ready();
    }
}

/// <summary>
/// A password or passphrase of a node (an account's password, the SD card's passphrase), typed twice: it goes to the server
/// once, with Save (<c>storeSecret</c>), and the node keeps only its id (<c>secretId</c>). A stored one is never shown; typing
/// replaces it.
/// </summary>
public sealed partial class SecretFieldViewModel : NodeFieldViewModel
{
    public const string SecretKey = "secretId";

    public SecretFieldViewModel(string label, Guid? storedId, bool confirm = true, Func<string, string?>? rule = null)
        : base(SecretKey, label)
    {
        StoredId = storedId;
        AsksConfirm = confirm;
        Rule(nameof(Text), () => Text.Length == 0 ? (StoredId is null ? ProvisioningTexts.TypeIt(Label) : null) : (rule ?? FieldRules.Password)(Text));
        Rule(nameof(Confirm), () => AsksConfirm && Text.Length > 0 && Confirm != Text ? ProvisioningTexts.PasswordsDiffer : null);
        Ready();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsConfirm))]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Confirm { get; set; } = string.Empty;

    /// <summary>The stored secret's id; null until one is stored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoredPlaceholder))]
    public partial Guid? StoredId { get; private set; }

    public bool AsksConfirm { get; }

    /// <summary>The confirmation shows once something is typed.</summary>
    public bool ShowsConfirm => AsksConfirm && Text.Length > 0;

    /// <summary>"Confirm passphrase".</summary>
    public string ConfirmLabel => "Confirm " + Label.ToLowerInvariant();

    /// <summary>"Stored on the server" while one is kept and nothing is typed.</summary>
    public string? StoredPlaceholder => StoredId is null ? null : ProvisioningTexts.StoredSecret;

    /// <summary>A typed secret that passes its rules: the editor stores it with Save.</summary>
    public string? Pending => IsShown && Text.Length > 0 && Validation.IsValid ? Text : null;

    public override JsonNode? Value => StoredId is { } id ? JsonValue.Create(id.ToString()) : null;

    public override IEnumerable<SecretFieldViewModel> Secrets => [this];

    /// <summary>The server kept the typed secret: the field forgets it and keeps the id.</summary>
    public void Stored(Guid id)
    {
        StoredId = id;
        Text = string.Empty;
        Confirm = string.Empty;
        Ready();
    }
}

/// <summary>What the editor does for the fields: the file picker with the upload, the camera choice, the commands.</summary>
public interface INodeFormServices
{
    /// <summary>The credential list's root entries.</summary>
    IReadOnlyList<CredentialChoice> Credentials { get; }

    /// <summary>Whether the eye of the credential choice shows (administrators only, like the Credentials page).</summary>
    bool CanRevealCredentials { get; }

    /// <summary>The stored password of a credential list entry through the host (audited on the server, never kept).</summary>
    Task<string> RevealCredentialAsync(Guid credentialId, CancellationToken ct);

    /// <summary>The VAPIX commands a profile can run.</summary>
    IReadOnlyList<ProfileCommand> Commands { get; }

    /// <summary>PKI's HTTPS and IEEE 802.1X certificate policies (the Certificate and 802.1X nodes); empty when PKI does not run.</summary>
    IReadOnlyList<IssuingPolicyInfo> Policies => [];

    /// <summary>Lets the user choose files, uploads and keeps them for the profile (<c>storeFile</c>); empty when closed.</summary>
    Task<IReadOnlyList<StoredFile>> ChooseFilesAsync(string title, bool multiple, IReadOnlyList<string> patterns);

    /// <summary>The text of a configuration file kept for a node (<c>readConfigFile</c>), for the file preview.</summary>
    Task<string> ReadConfigFileAsync(Guid fileId, CancellationToken ct) => throw new NotSupportedException("This editor cannot read kept files.");
}

/// <summary>One file kept for a node (Configuration): Choose file uploads it and keeps its id (<c>fileId</c>).</summary>
public sealed partial class FileFieldViewModel : NodeFieldViewModel
{
    private readonly INodeFormServices _services;
    private readonly IReadOnlyList<string> _patterns;
    private readonly Func<string, string?>? _nameRule;

    public FileFieldViewModel(string key, string label, Guid? fileId, string? name, INodeFormServices services, IReadOnlyList<string> patterns, Func<string, string?>? nameRule = null)
        : base(key, label)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _nameRule = nameRule;
        FileId = fileId;
        FileName = name ?? (fileId is null ? null : ProvisioningTexts.StoredFile);
        Rule(nameof(FileName), () => FileId is null ? ProvisioningTexts.ChooseIt(Label) : FileName is { } n && _nameRule is not null ? _nameRule(n) : null);
        Ready();
    }

    [ObservableProperty]
    public partial Guid? FileId { get; private set; }

    [ObservableProperty]
    public partial string? FileName { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The error of the field below the file row (the theme shows it under the button).</summary>
    public string? Error => ErrorOf(nameof(FileName));

    /// <summary>The key the file's name is kept under too (the editor shows it again), or null.</summary>
    public string? NameKey { get; init; }

    public override JsonNode? Value => FileId is { } id ? JsonValue.Create(id.ToString()) : null;

    public override void Write(JsonObject settings)
    {
        base.Write(settings);
        if (NameKey is { } key)
        {
            settings[key] = FileId is null ? null : FileName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    public async Task ChooseAsync()
    {
        IsBusy = true;
        try
        {
            if (await _services.ChooseFilesAsync(Label, false, _patterns).ConfigureAwait(true) is [var file, ..])
            {
                FileId = file.Id;
                FileName = file.Name;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Validation.SetServerError(nameof(FileName), ServerErrors.Message(ex));
        }
        finally
        {
            IsBusy = false;
            Validation.Touch(nameof(FileName));
            OnPropertyChanged(nameof(Error));
        }
    }

    public bool CanChoose() => !IsBusy;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(HasErrors))
        {
            OnPropertyChanged(nameof(Error));
        }
    }
}

/// <summary>A file of Upgrade AXIS OS from files in the list.</summary>
public sealed record OsFileRow(Guid FileId, string Name, string? Product)
{
    public string Text => Product is null ? Name : $"{Name} ({Product})";
}

/// <summary>The AXIS OS files, one per model: Choose files uploads them; each row can go again.</summary>
public sealed partial class FilesFieldViewModel : NodeFieldViewModel
{
    private readonly INodeFormServices _services;

    public FilesFieldViewModel(string key, string label, IEnumerable<OsFileRow> files, INodeFormServices services)
        : base(key, label)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        foreach (var file in files)
        {
            Files.Add(file);
        }

        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(Summary));
            Validation.Touch(nameof(Count));
            OnPropertyChanged(nameof(Error));
        };
        Rule(nameof(Count), Check);
        Ready();
    }

    public ObservableCollection<OsFileRow> Files { get; } = [];

    public int Count => Files.Count;

    /// <summary>"3 files" on the file row's button.</summary>
    public string? Summary => Files.Count == 0 ? null : ProvisioningTexts.FileCount(Files.Count);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    public partial bool IsBusy { get; private set; }

    public string? Error => ErrorOf(nameof(Count));

    public override JsonNode? Value =>
        new JsonArray([.. Files.Select(f => (JsonNode?)new JsonObject { ["fileId"] = f.FileId.ToString(), ["name"] = f.Name })]);

    [RelayCommand(CanExecute = nameof(CanChoose))]
    public async Task ChooseAsync()
    {
        IsBusy = true;
        try
        {
            foreach (var file in await _services.ChooseFilesAsync(Label, true, ["*.bin"]).ConfigureAwait(true))
            {
                Files.Add(new OsFileRow(file.Id, file.Name, Nodes.UpgradeFilesNode.Product(file.Name)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Validation.SetServerError(nameof(Count), ServerErrors.Message(ex));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Error));
        }
    }

    public bool CanChoose() => !IsBusy;

    [RelayCommand]
    public void RemoveFile(OsFileRow? row)
    {
        if (row is not null)
        {
            Files.Remove(row);
        }
    }

    private string? Check()
    {
        if (Files.Count == 0)
        {
            return ProvisioningTexts.ChooseOsFiles;
        }

        if (Files.Count > Nodes.UpgradeFilesNode.MaxFiles)
        {
            return $"Choose at most {Nodes.UpgradeFilesNode.MaxFiles} files.";
        }

        var products = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file.Product is not { } product)
            {
                return $"{file.Name} is not an AXIS OS file (e.g. P3265-LVE_12_6_95.bin).";
            }

            if (!products.Add(product))
            {
                return $"Two files are for {product}.";
            }
        }

        return null;
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
        text.Length == 0 ? ProvisioningTexts.EnterMask : Ipv4.TryParsePrefix(text, out _) ? null : ProvisioningTexts.NotAMask;

    /// <summary>1 to 64 printable ASCII characters (the cameras' passwords).</summary>
    public static string? Password(string text) =>
        text.Length == 0 ? ProvisioningTexts.EnterPassword
        : text.Length > 64 || text.Any(c => c is < (char)0x20 or > (char)0x7E) ? ProvisioningTexts.PasswordRule
        : null;

    /// <summary>An SD card passphrase: at most 512 bytes, no control characters (the Edge storage task's rule).</summary>
    public static string? Passphrase(string text) =>
        System.Text.Encoding.UTF8.GetByteCount(text) > Nodes.SdCardNode.MaxPassphraseBytes ? ProvisioningTexts.PassphraseTooLong
        : text.Any(char.IsControl) ? ProvisioningTexts.PassphraseCharacters
        : null;

    /// <summary>A host name or IPv4 address (NTP servers).</summary>
    public static string? HostItem(string text) =>
        text.Length <= 253 && Nodes.NodeRules.IsDomainName(text) ? null : $"{text} is not a host name or IP address.";

    public static string? Version(string text) =>
        text.Length == 0 || Nodes.NodeRules.IsVersion(text) ? null : "Enter a version like 12.6.95.";

    public static string? Domain(string text) =>
        text.Length == 0 || Nodes.NodeRules.IsDomainName(text) ? null : $"{text} is not a domain name.";

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>; empty allowed when <paramref name="optional"/>.</summary>
    public static Func<string, string?> Whole(int min, int max, string error, bool optional = false) => text =>
        text.Length == 0 ? (optional ? null : error)
        : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? null
        : error;

    /// <summary>The root entries of the credential list.</summary>
    public static IReadOnlyList<CredentialChoice> RootEntries(IEnumerable<DeviceCredentialName> credentials) =>
        [.. credentials.Where(c => string.Equals(c.UserName, "root", StringComparison.Ordinal)).Select(c => new CredentialChoice(c.Id, c.UserName, c.Added))];

    /// <summary>A JSON value as a Guid, or null.</summary>
    public static Guid? ToGuid(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String && System.Guid.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, out var id) ? id : null;
}

/// <summary>Check boxes under one label ("Apply": parameters, action rules, schedules), each its own key, with one rule.</summary>
public sealed class CheckGroupFieldViewModel : NodeFieldViewModel
{
    private readonly Func<string?>? _rule;

    public CheckGroupFieldViewModel(string label, IReadOnlyList<BoolFieldViewModel> items, Func<string?>? rule = null)
        : base(string.Empty, label)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        _rule = rule;
        foreach (var item in items)
        {
            item.Changed += (_, _) =>
            {
                Validation.Touch(nameof(Items));
                OnPropertyChanged(nameof(Error));
                RaiseChanged();
            };
        }

        Rule(nameof(Items), () => _rule?.Invoke());
        Ready();
    }

    public IReadOnlyList<BoolFieldViewModel> Items { get; }

    public string? Error => ErrorOf(nameof(Items));

    public override JsonNode? Value => null;

    public override void Write(JsonObject settings)
    {
        foreach (var item in Items)
        {
            item.Write(settings);
        }
    }

    public override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(Error));
    }
}
