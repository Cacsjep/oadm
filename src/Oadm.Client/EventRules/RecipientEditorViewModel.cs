using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.EventRules;

/// <summary>A type in the recipient dialog's select.</summary>
public sealed record RecipientTypeOption(RecipientTypeEntry Entry)
{
    public string Text => Entry.DisplayName;

    /// <summary>One shared connection with a status: Test only connects.</summary>
    public bool Persistent => Entry.Persistent;

    /// <summary>A field of this type is a certificate of PKI.</summary>
    public bool HasCertificates => Entry.Fields.Any(f => f.Kind is ActionFieldKind.ActionFieldClientCertificate or ActionFieldKind.ActionFieldTrustedCertificate);

    public override string ToString() => Text;
}

/// <summary>
/// The recipient dialog (one form): Type (only for a new one), Name and the type's fields, drawn from their declaration.
/// Errors stand below their field, a server refusal below the field it names. Test sends once now with the values of the
/// form (a persistent type only connects); the reason of a failure shows as text next to the button, a refusal that names a
/// field below that field, and a reply to a form the user has changed since is dropped. The certificate selects are filled
/// from PKI when the dialog opens (<see cref="LoadCertificatesAsync"/>).
/// </summary>
public sealed partial class RecipientEditorViewModel : ValidatingViewModel
{
    private const int MaxNameLength = RecipientType.MaxNameLength;

    private readonly IOadmApi _api;
    private readonly RecipientEntry? _existing;
    private ActionFieldViewModel? _refused;
    private readonly HashSet<string> _takenNames;
    private RecipientCertificateList? _certificates;
    private string? _certificatesError;
    private int _testGeneration; // every change of the form counts: a Test's reply is about the form it started from

    /// <param name="api">The server.</param>
    /// <param name="types">The types a new recipient can have; every type for a recipient that exists.</param>
    /// <param name="existing">The recipient to change; null for a new one.</param>
    /// <param name="takenNames">The names of the other recipients.</param>
    public RecipientEditorViewModel(IOadmApi api, IReadOnlyList<RecipientTypeEntry> types, RecipientEntry? existing, IReadOnlyCollection<string> takenNames)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(takenNames);
        _existing = existing;
        _takenNames = new HashSet<string>(takenNames, StringComparer.OrdinalIgnoreCase);
        Types = [.. types.Select(t => new RecipientTypeOption(t))];
        Name = existing?.Name ?? string.Empty;
        SelectedType = existing is null ? (Types.Count > 0 ? Types[0] : null) : Types.FirstOrDefault(t => t.Entry.Id == existing.TypeId);
        Validation.Rule(nameof(Name), () =>
        {
            var name = Name.Trim();
            return name.Length == 0 ? "Enter a name."
                : name.Length > MaxNameLength ? string.Create(CultureInfo.InvariantCulture, $"At most {MaxNameLength} characters.")
                : _takenNames.Contains(name) ? RecipientType.NameTakenText
                : null;
        });
        Validation.Rule(nameof(SelectedType), () => SelectedType is null ? "Choose a type." : null);
        Validation.Rule(nameof(FieldProblem), () => FieldProblem);
        BuildFields();
        Validation.Validate();
    }

    public event EventHandler<bool>? CloseRequested;

    public bool IsNew => _existing is null;

    public string Title => IsNew ? "New recipient" : "Change recipient";

    public IReadOnlyList<RecipientTypeOption> Types { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCertificatesLoading), nameof(TestTip))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    public partial RecipientTypeOption? SelectedType { get; set; }

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    /// <summary>The fields of the chosen type.</summary>
    public ObservableCollection<ActionFieldViewModel> Fields { get; } = [];

    /// <summary>The first error of the fields: why Save is off.</summary>
    public string? FieldProblem => Fields.FirstOrDefault(f => f.IsVisible && f.Problem is not null) is { } first ? $"{first.Label}: {first.Problem}" : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerError))]
    public partial string? ServerError { get; private set; }

    public bool HasServerError => !string.IsNullOrEmpty(ServerError);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The saved recipient after Save (the dialog closed with true).</summary>
    public RecipientEntry? Saved { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    public partial bool TestOk { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    public partial bool IsTesting { get; private set; }

    public bool TestFailed => !TestOk && !IsTesting;

    public bool HasTestResult => TestResult is not null;

    /// <summary>The certificates of PKI are being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCertificatesLoading))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    public partial bool IsLoadingCertificates { get; private set; }

    /// <summary>The form of the chosen type waits for the certificates.</summary>
    public bool ShowCertificatesLoading => IsLoadingCertificates && SelectedType?.HasCertificates == true;

    /// <summary>What Test does for the chosen type.</summary>
    public string TestTip => SelectedType?.Persistent == true ? "Connects once now" : "Sends it once now, with sample event values";

    private bool NotTesting() => !IsTesting && !ShowCertificatesLoading;

    private bool CanSave() => !IsBusy && !ShowCertificatesLoading && IsFormValid;

    private SaveRecipientRequest Request() => new()
    {
        Id = _existing?.Id ?? string.Empty,
        Name = Name.Trim(),
        TypeId = SelectedType?.Entry.Id ?? string.Empty,
        SettingsJson = ActionFieldViewModel.ToSettings(Fields).ToJsonString(),
    };

    [RelayCommand(CanExecute = nameof(NotTesting))]
    private async Task TestAsync()
    {
        Refresh();
        if (FieldProblem is not null || SelectedType is null)
        {
            ShowAllErrors();
            TestResult = "Fix the settings first.";
            TestOk = false;
            return;
        }

        var persistent = SelectedType.Persistent;
        var generation = _testGeneration;
        IsTesting = true;
        TestResult = persistent ? RecipientSessionTexts.Connecting : "Sending";
        try
        {
            var reply = await _api.TestRecipientAsync(Request(), CancellationToken.None).ConfigureAwait(true);
            if (generation == _testGeneration)
            {
                TestOk = reply.Ok;
                TestResult = reply.Message.Length > 0 ? reply.Message
                    : reply.Ok ? persistent ? RecipientSessionTexts.Connected : "Sent"
                    : "The test failed. The server log has the details.";
            }
        }
        catch (RpcException ex) when (generation == _testGeneration && NamesField(ex))
        {
            TestOk = false;
            TestResult = null;
            ShowRefusal(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == _testGeneration)
            {
                TestOk = false;
                TestResult = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// Fills the certificate selects when the dialog opens; a list that cannot be read stands below them. Nothing is read when
    /// no type has a certificate field.
    /// </summary>
    public async Task LoadCertificatesAsync(CancellationToken ct)
    {
        if (!Types.Any(t => t.HasCertificates))
        {
            return;
        }

        IsLoadingCertificates = true;
        try
        {
            _certificates = await _api.ListRecipientCertificatesAsync(ct).ConfigureAwait(true);
            _certificatesError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Shown below the selects.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _certificatesError = "The list could not be read: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
        finally
        {
            IsLoadingCertificates = false;
        }

        foreach (var field in Fields.Where(f => f.IsCertificate))
        {
            FillCertificates(field);
        }

        Refresh();
    }

    private void FillCertificates(ActionFieldViewModel field)
    {
        if (_certificates is { } list)
        {
            field.SetCertificates(field.IsClientCertificate ? list.Client : list.Trusted);
        }
        else if (_certificatesError is { } error)
        {
            field.LoadError = error;
            field.ShowError = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ShowAllErrors();
        if (!IsFormValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Saved = await _api.SaveRecipientAsync(Request(), CancellationToken.None).ConfigureAwait(true);
            ServerError = null;
            CloseRequested?.Invoke(this, true);
        }
        catch (RpcException ex)
        {
            ShowRefusal(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private bool NamesField(RpcException refusal) =>
        refusal.Trailers.GetValue(RecipientErrors.FieldTrailer) is { } key && (key == "name" || Fields.Any(f => f.Key == key));

    /// <summary>A refusal stands below the field it names, else below the form.</summary>
    private void ShowRefusal(RpcException refusal)
    {
        var key = refusal.Trailers.GetValue(RecipientErrors.FieldTrailer);
        if (key == "name")
        {
            Validation.SetServerError(nameof(Name), refusal.Status.Detail);
        }
        else if (Fields.FirstOrDefault(f => f.Key == key && f.IsVisible) is { } field)
        {
            field.LoadError = refusal.Status.Detail;
            field.ShowError = true;
            _refused = field;
            Refresh();
        }
        else
        {
            ServerError = refusal.Status.Detail;
        }
    }

    private void ShowAllErrors()
    {
        Validation.ShowAll();
        foreach (var field in Fields)
        {
            field.ShowError = true;
        }
    }

    /// <summary>The form changed: a shown result, and the reply of a Test still running, are about the old form.</summary>
    private void FormChanged()
    {
        _testGeneration++;
        TestResult = null;
    }

    partial void OnNameChanged(string value) => FormChanged();

    partial void OnSelectedTypeChanged(RecipientTypeOption? value)
    {
        if (Validation.Fields.Count > 0)
        {
            BuildFields();
        }
    }

    private void BuildFields()
    {
        Fields.Clear();
        FormChanged();
        foreach (var entry in SelectedType?.Entry.Fields ?? [])
        {
            var field = new ActionFieldViewModel(entry);
            field.SetValue(_existing is not null ? Stored(entry.Key) : entry.HasDefaultValue ? entry.DefaultValue : null);
            field.PasswordStored = _existing?.StoredSecretFields.Contains(entry.Key) == true;
            field.ShowError = false;
            if (field.IsCertificate)
            {
                FillCertificates(field);
            }

            field.Changed += OnFieldChanged;
            Fields.Add(field);
        }

        Refresh();
    }

    private string? Stored(string key)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(_existing!.ValuesJson) is System.Text.Json.Nodes.JsonObject values ? ActionSettings.Text(values, key) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void OnFieldChanged(object? sender, EventArgs e)
    {
        if (sender is ActionFieldViewModel field)
        {
            field.LoadError = null; // a server refusal goes when the field changes
        }

        if (_refused is { } refused)
        {
            refused.LoadError = null; // or when another one does: a target changed back needs no password
            _refused = null;
        }

        FormChanged();
        Refresh();
    }

    private void Refresh()
    {
        ActionFieldViewModel.Refresh(Fields, statefulRule: true);
        Validation.Validate();
    }

    protected override void OnValidationChanged() => SaveCommand.NotifyCanExecuteChanged();
}
