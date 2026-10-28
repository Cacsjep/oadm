using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Acap;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>
/// One setting of the Settings window: the value of most devices ("differs on 3" when others have another), checked with
/// the Applications plugin's own rules (<see cref="AppSettings.TryNormalize"/>) directly below the field. Passwords are
/// never shown: an empty field keeps them ("Unchanged").
/// </summary>
public sealed partial class AppSettingRowViewModel : ValidatingViewModel
{
    public const string Unchanged = "Unchanged";

    public AppSettingRowViewModel(AppSettingDefinition definition, string? common, int differsOn, bool editable)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Original = IsPassword ? string.Empty : common ?? string.Empty;
        DiffersOn = differsOn;
        IsEditable = editable && definition.IsChangeable;
        Choices = definition.Kind switch
        {
            AppSettingKind.List => [.. definition.Choices.Select(c => c.Value)],
            _ => [],
        };
        _value = Original;
        Validation.Rule(nameof(Value), () => Problem());
        Validation.Validate();
    }

    public AppSettingDefinition Definition { get; }

    public string Name => Definition.Name;

    public string Label => Definition.DisplayName;

    /// <summary>The shown value before an edit (the value of most devices; empty for a password).</summary>
    public string Original { get; }

    public int DiffersOn { get; }

    public bool IsEditable { get; }

    public bool IsReadOnly => !IsEditable;

    public bool IsPassword => Definition.Kind == AppSettingKind.Password;

    public bool IsChoice => Choices.Count > 0;

    /// <summary>A yes/no setting: a check box (its device texts behind it).</summary>
    public bool IsYesNo => Definition.Kind == AppSettingKind.YesNo && Definition.TrueValue is not null && Definition.FalseValue is not null;

    public bool IsText => !IsChoice && !IsPassword && !IsYesNo;

    /// <summary>The check box of a yes/no setting.</summary>
    public bool IsChecked
    {
        get => string.Equals(Value, Definition.TrueValue, StringComparison.OrdinalIgnoreCase);
        set
        {
            Value = value ? Definition.TrueValue! : Definition.FalseValue!;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> Choices { get; }

    /// <summary>"Differs on 3 devices", the range of a number, or "Unchanged" for a password.</summary>
    public string? Hint
    {
        get
        {
            var parts = new List<string>();
            if (DiffersOn > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"Differs on {DiffersOn:N0} {(DiffersOn == 1 ? "device" : "devices")}"));
            }

            if (Definition.Kind == AppSettingKind.Number && Definition.Ranges.Count > 0)
            {
                parts.Add(AppSettings.RangeText(Definition.Ranges));
            }

            return parts.Count == 0 ? null : string.Join(". ", parts);
        }
    }

    public string? Placeholder => IsPassword ? Unchanged : null;

    private string _value;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    /// <summary>The user changed it (a password: typed one).</summary>
    public bool IsChanged => IsEditable && (IsPassword ? Value.Length > 0 : !string.Equals(Value, Original, StringComparison.Ordinal));

    /// <summary>The value as the device takes it (only when <see cref="IsChanged"/> and valid).</summary>
    public string? Normalized => IsChanged && AppSettings.TryNormalize(Definition, Value, out var fit, out _) ? fit : null;

    private string? Problem()
    {
        if (!IsChanged)
        {
            return null;
        }

        return AppSettings.TryNormalize(Definition, Value, out _, out var problem) ? null
            : problem is null ? null
            : char.ToUpperInvariant(problem[0]) + problem[1..];
    }

    /// <summary>A submit was tried: every error shows.</summary>
    public void ShowErrors() => Validation.ShowAll(nameof(Value));
}

/// <summary>A device whose settings could not be read.</summary>
public sealed record SettingsDeviceProblem(string Device, string Error);

/// <summary>
/// The Settings window (docs/spec/app-management.md "Log and settings"): one application's settings on one device or on
/// every listed device with it, read through the server part (read only). Values show only to roles that may change
/// application settings; Save starts the Applications plugin's Change settings task with only the changed values.
/// </summary>
public sealed partial class AppSettingsViewModel : ObservableObject
{
    public const string NoValueRight = "Your role cannot change application settings, so their values are not shown.";

    private readonly ICorePluginClientContext _ctx;
    private readonly string _application;
    private readonly string _applicationName;
    private readonly IReadOnlyList<Guid> _deviceIds;
    private IReadOnlyList<Guid> _readDevices = [];

    /// <param name="configurationPath">The application's own page on the device ("/local/ax_msf/redirect.html"); null when it lists none.</param>
    public AppSettingsViewModel(ICorePluginClientContext ctx, string application, string applicationName, IReadOnlyList<Guid> deviceIds, string where,
        string? configurationPath = null)
    {
        ConfigurationPath = configurationPath;
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _application = application;
        _applicationName = applicationName;
        _deviceIds = deviceIds;
        Title = $"Settings of {applicationName}";
        Scope = where;
    }

    public string Title { get; }

    /// <summary>"10.0.0.48" or "12 devices".</summary>
    public string Scope { get; }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<AppSettingRowViewModel> Rows { get; private set; } = [];

    public IReadOnlyList<SettingsDeviceProblem> Problems { get; private set; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSettings))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValueNote))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool ValuesShown { get; private set; }

    public bool MayChange => ValuesShown && _ctx.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Settings));

    public bool ShowValueNote => !IsLoading && HasRows && !ValuesShown;

    /// <summary>No device has settings of this application: its own page on the device.</summary>
    public bool ShowNoSettings => !IsLoading && !HasError && !HasRows && _readDevices.Count > 0;

    public static string NoSettingsText => AppSettings.NoSettings;

    /// <summary>The application's own page on the device, as list.cgi names it; null when it lists none.</summary>
    public string? ConfigurationPath { get; }

    public const string WebViewId = "oadm.web-view";

    public const string OneDeviceHint = "Open it on one device.";

    public const string AppPageNotOpened = "Web View cannot open this device.";

    /// <summary>The application has its own page and Web View is on.</summary>
    private bool HasAppPage => ConfigurationPath is not null && _ctx.IsPluginOn(WebViewId) == true;

    /// <summary>One device: "Open app page" opens the application's own page in Web View.</summary>
    public bool CanOpenAppPage => HasAppPage && _deviceIds.Count == 1;

    /// <summary>Several devices: no link, "Open it on one device."</summary>
    public bool ShowOneDeviceHint => HasAppPage && _deviceIds.Count > 1;

    /// <summary>Why Save is disabled (tooltip), or null.</summary>
    public string? SaveBlockedReason => Rows.FirstOrDefault(r => r.HasErrors || !r.IsFormValid)?.FormError
        ?? (Rows.Any(r => r.IsChanged) ? null : "Change a setting first.");

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var json = await _ctx.InvokeAsync(AppManagementMethods.GetSettings,
                AppManagementJson.Serialize(new SettingsRequest { Application = _application, DeviceIds = _deviceIds }), ct).ConfigureAwait(true);
            Apply(AppManagementJson.Deserialize<SettingsReply>(json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown in the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowValueNote));
            OnPropertyChanged(nameof(ShowNoSettings));
        }
    }

    /// <summary>One row per setting over the read devices: the value of most devices and on how many it differs.</summary>
    public void Apply(SettingsReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ValuesShown = reply.ValuesShown;
        var read = reply.Devices.Where(d => d.Error is null).ToList();
        _readDevices = [.. read.Select(d => d.DeviceId)];
        Problems = [.. reply.Devices.Where(d => d.Error is not null).Select(d => new SettingsDeviceProblem(d.Address, d.Error!))];
        var order = new List<string>();
        var definitions = new Dictionary<string, AppSettingDefinition>(StringComparer.Ordinal);
        var values = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var device in read)
        {
            foreach (var setting in device.Settings)
            {
                if (definitions.TryAdd(setting.Name, setting))
                {
                    order.Add(setting.Name);
                    values[setting.Name] = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                var value = setting.Value ?? string.Empty;
                values[setting.Name][value] = values[setting.Name].GetValueOrDefault(value) + 1;
            }
        }

        var editable = MayChange;
        Rows =
        [
            .. order.Select(name =>
            {
                var counts = values[name];
                var common = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).FirstOrDefault();
                var differs = definitions[name].Kind == AppSettingKind.Password || !ValuesShown ? 0 : counts.Values.Sum() - common.Value;
                var row = new AppSettingRowViewModel(definitions[name], ValuesShown ? common.Key : null, differs, editable);
                row.PropertyChanged += (_, _) => OnFormChanged();
                return row;
            }),
        ];
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(MayChange));
        IsLoading = false;
        OnPropertyChanged(nameof(ShowValueNote));
        OnPropertyChanged(nameof(ShowNoSettings));
        OnFormChanged();
    }

    private bool CanSave => !IsLoading && MayChange && Rows.Any(r => r.IsChanged) && Rows.All(r => r.IsFormValid);

    /// <summary>Starts the Change settings task on the read devices with only the changed values.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        foreach (var row in Rows)
        {
            row.ShowErrors();
        }

        if (!CanSave)
        {
            return;
        }

        var values = Rows.Where(r => r.IsChanged).ToDictionary(r => r.Name, r => r.Normalized!, StringComparer.Ordinal);
        try
        {
            var request = new RunRequest
            {
                Action = AcapAction.Settings,
                Owner = _ctx.OwnerName,
                Values = values,
                Items = [new RunItem { Application = _application, DisplayName = _applicationName, DeviceIds = _readDevices }],
            };
            await _ctx.InvokeAsync(AppManagementMethods.Run, AppManagementJson.Serialize(request), CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // Shown in the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The application's own page of the device in its Web View tab; the window closes once it opened.</summary>
    [RelayCommand]
    private void OpenAppPage()
    {
        if (!CanOpenAppPage)
        {
            return;
        }

        if (_ctx.OpenDevicePage(_deviceIds[0], ConfigurationPath!))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ErrorText = AppPageNotOpened;
        }
    }

    private void OnFormChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }
}
