using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.EventRules;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;

namespace Oadm.Client.Compliance;

/// <summary>
/// The check dialog (new check, or change one): name, kind of check, devices, the kind's settings (drawn from its fields),
/// Problem or Warning, how often it reads the devices, turned on. A default check (Reachable, Login works) changes only its
/// devices. Errors stand below their field; Save is disabled with the first problem as tooltip.
/// </summary>
public sealed partial class ComplianceCheckEditorViewModel : ValidatingViewModel
{
    public const int DefaultInterval = 60;

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly ComplianceCheckDetail? _initial;
    private bool _loading;

    public ComplianceCheckEditorViewModel(
        IOadmApi api, IDialogService dialogs, ComplianceCatalog catalog, IReadOnlyList<IDeviceInfo> devices, ComplianceCheckDetail? existing = null,
        IEnumerable<string>? tagNames = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _initial = existing;
        var summary = existing?.Summary;
        IsBuiltIn = summary?.BuiltIn == true;
        var types = catalog.Entries.Where(t => t.Available || t.Id == summary?.TypeId).ToList();
        Types = GroupedChoice.Build(types, t => t.Group.Length == 0 ? "General" : t.Group, t => t.Available ? t.DisplayName : $"{t.DisplayName} ({t.SourceName} is turned off)");
        Devices = new DeviceSelectorViewModel(
            devices,
            DevicePickerOptions.Full(tagNames: tagNames),
            DeviceChoice.Devices([]),
            summary is null ? null : ToSelector(summary.Devices),
            ShowPickerAsync);
        Devices.Changed += (_, _) => Revalidate();

        Validation.Rule(nameof(Name), () => Name.Trim().Length == 0 ? "Enter a name." : Name.Trim().Length > 64 ? "Use at most 64 characters." : null);
        Validation.Rule(nameof(SelectedType), () => SelectedType?.Value is ComplianceTypeEntry ? null : "Choose the kind of check.");
        Validation.Rule(nameof(IntervalText), () => !ReadsDevices || (int.TryParse(IntervalText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n is >= 15 and <= 1440)
            ? null : "Enter 15 to 1440 minutes.");

        _loading = true;
        Name = summary?.Name ?? string.Empty;
        IsEnabled = summary?.Enabled ?? true;
        IntervalText = (summary is { IntervalMinutes: > 0 } ? summary.IntervalMinutes : DefaultInterval).ToString(CultureInfo.CurrentCulture);
        IsWarning = summary?.Severity == ComplianceSeverity.ComplianceWarning;
        SelectedType = Types.FirstOrDefault(t => t.Value is ComplianceTypeEntry entry && entry.Id == summary?.TypeId);
        _loading = false;
        BuildFields(existing?.SettingsJson);
        Validation.Validate();
    }

    public string Title => _initial is null ? "New compliance check" : "Change compliance check";

    public bool IsNew => _initial is null;

    /// <summary>Reachable or Login works: only the devices change.</summary>
    public bool IsBuiltIn { get; }

    public bool CanChangeKind => !IsBuiltIn;

    public IReadOnlyList<GroupedChoice> Types { get; }

    public DeviceSelectorViewModel Devices { get; }

    public ObservableCollection<ActionFieldViewModel> Fields { get; } = [];

    public bool HasFields => Fields.Count > 0;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadsDevices), nameof(TypeDescription))]
    public partial GroupedChoice? SelectedType { get; set; }

    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    /// <summary>When it fails: Warning (amber) instead of Problem (red).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeverityIndex))]
    public partial bool IsWarning { get; set; }

    /// <summary>The choices of "When it fails".</summary>
    public IReadOnlyList<string> Severities { get; } = ["Problem", "Warning"];

    /// <summary>0 Problem, 1 Warning (the select).</summary>
    public int SeverityIndex
    {
        get => IsWarning ? 1 : 0;
        set => IsWarning = value == 1;
    }

    [ObservableProperty] public partial string IntervalText { get; set; } = string.Empty;

    /// <summary>A refused save, directly below the form.</summary>
    [ObservableProperty] public partial string? ServerError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    public ComplianceTypeEntry? Type => SelectedType?.Value as ComplianceTypeEntry;

    /// <summary>The kind reads the devices (interval); false for kinds decided from what OADM knows.</summary>
    public bool ReadsDevices => Type?.ReadsDevices == true;

    public string? TypeDescription => Type?.Description is { Length: > 0 } text ? text : null;

    /// <summary>The first problem of the form (Save's tooltip), or null.</summary>
    public string? SaveBlockedReason =>
        Validation.FirstErrorOf(null)
        ?? Devices.Problem
        ?? Fields.Where(f => f.IsVisible && f.Problem is not null).Select(f => f.Label + ": " + f.Problem).FirstOrDefault();

    /// <summary>True: saved; false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Shows the device picker (set by the window).</summary>
    public Func<DevicePickerViewModel, Task<bool>>? ShowPicker { get; set; }

    /// <summary>The summary the server answered to the save.</summary>
    public ComplianceCheckSummary? Saved { get; private set; }

    partial void OnSelectedTypeChanged(GroupedChoice? value)
    {
        if (!_loading)
        {
            BuildFields(null);
            if (Type is { } type)
            {
                IsWarning = type.DefaultSeverity == ComplianceSeverity.ComplianceWarning;
                if (Name.Trim().Length == 0)
                {
                    Name = type.DisplayName;
                }
            }
        }
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Revalidate()
    {
        ActionFieldViewModel.Refresh(Fields, statefulRule: true);
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void BuildFields(string? settingsJson)
    {
        Fields.Clear();
        var settings = (settingsJson is { Length: > 0 } ? JsonNode.Parse(settingsJson) as JsonObject : null) ?? [];
        foreach (var entry in Type?.Fields ?? [])
        {
            var field = new ActionFieldViewModel(entry);
            field.SetValue(settingsJson is null ? (entry.HasDefaultValue ? entry.DefaultValue : null) : Oadm.Sdk.Actions.ActionSettings.Text(settings, entry.Key));
            field.ShowError = false;
            field.FilePicker = ChooseFileAsync;
            field.DeviceReader = ReadChoicesAsync;
            field.Changed += (_, _) => Revalidate();
            Fields.Add(field);
        }

        OnPropertyChanged(nameof(HasFields));
        Revalidate();
    }

    private Task<bool> ShowPickerAsync(DevicePickerViewModel picker) => ShowPicker is { } show ? show(picker) : Task.FromResult(false);

    private async Task ChooseFileAsync(ActionFieldViewModel field)
    {
        var extension = field.Entry.Accept.FirstOrDefault()?.TrimStart('.') ?? "*";
        var file = await _dialogs.OpenFileAsync(field.Label, new FileType(field.Label, extension, "application/octet-stream"), field.Field.MaxLength).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        if (file.IsTooLarge)
        {
            field.LoadError = string.Create(CultureInfo.CurrentCulture, $"The file is larger than {field.Field.MaxLength / 1024:N0} KB.");
            field.ShowError = true;
            Revalidate();
            return;
        }

        field.LoadError = null;
        field.SetFile(file.Name, file.Content);
    }

    /// <summary>"Read from device": one device from the picker, then the choices the kind reads from it.</summary>
    private async Task ReadChoicesAsync(ActionFieldViewModel field)
    {
        if (Type is not { } type || ShowPicker is not { } show)
        {
            return;
        }

        var picker = new DevicePickerViewModel(_devices, DeviceChoice.Devices([]), new DevicePickerOptions { SingleDevice = true });
        if (!await show(picker).ConfigureAwait(true) || picker.Choice.DeviceIds.Count == 0)
        {
            return;
        }

        var deviceId = picker.Choice.DeviceIds[0];

        var device = _devices.FirstOrDefault(d => d.Id == deviceId);
        try
        {
            var choices = await _api.ListComplianceChoicesAsync(type.Id, field.Key, deviceId.ToString(), CancellationToken.None).ConfigureAwait(true);
            field.LoadError = choices.Count == 0 ? "The device has none." : null;
            field.SetChoices(choices);
            field.ReadFromText = "Read from " + (device?.Address ?? deviceId.ToString());
        }
        catch (RpcException ex)
        {
            field.LoadError = "The list could not be read: " + ex.Status.Detail;
        }

        field.ShowError = true;
        Revalidate();
    }

    private bool CanSave() => !IsSaving && SaveBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        foreach (var field in Fields)
        {
            field.ShowError = true;
        }

        if (SaveBlockedReason is not null || Type is not { } type)
        {
            return;
        }

        IsSaving = true;
        ServerError = null;
        try
        {
            var selector = Devices.ToSelector();
            var summary = new ComplianceCheckSummary
            {
                Id = _initial?.Summary.Id ?? string.Empty,
                Name = Name.Trim(),
                TypeId = type.Id,
                Severity = IsWarning ? ComplianceSeverity.ComplianceWarning : ComplianceSeverity.ComplianceProblem,
                IntervalMinutes = ReadsDevices ? int.Parse(IntervalText, NumberStyles.Integer, CultureInfo.CurrentCulture) : 0,
                Enabled = IsEnabled,
                Version = _initial?.Summary.Version ?? 0,
                Devices = new ComplianceDevices
                {
                    All = selector.Mode == DeviceSelectorMode.All,
                    DeviceIds = { selector.DeviceIds.Select(id => id.ToString()) },
                    Tags = { selector.Tags },
                },
            };
            Saved = await _api.SaveComplianceCheckAsync(
                new ComplianceCheckDetail { Summary = summary, SettingsJson = ActionFieldViewModel.ToSettings(Fields).ToJsonString() },
                CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, true);
        }
        catch (RpcException ex)
        {
            ServerError = ex.Status.Detail;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private static DeviceSelector ToSelector(ComplianceDevices? devices) => devices is null or { All: false, DeviceIds.Count: 0, Tags.Count: 0 }
        ? new DeviceSelector { Mode = DeviceSelectorMode.Devices }
        : devices.All
            ? new DeviceSelector { Mode = DeviceSelectorMode.All }
            : new DeviceSelector
            {
                Mode = DeviceSelectorMode.Devices,
                DeviceIds = [.. devices.DeviceIds.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)],
                Tags = [.. devices.Tags],
            };
}
