using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig.Client.Configure;

/// <summary>A chosen configuration file: its name and text (read by the dialog's file picker).</summary>
public sealed record PickedFile(string Name, string Content)
{
    /// <summary>The file is larger than <see cref="AdmConfigFile.MaxFileBytes"/> and was not read.</summary>
    public bool TooLarge { get; init; }
}

/// <summary>The platform parts of the dialogs (pickers, confirmation, save); the window supplies them, tests replace them.</summary>
public interface IConfigureUi
{
    /// <summary>One device of the system for "Use configuration from: Device"; null = cancelled.</summary>
    Task<IDeviceInfo?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, IDeviceInfo? current);

    /// <summary>An ADM configuration file; null = cancelled.</summary>
    Task<PickedFile?> PickFileAsync();

    Task<bool> ConfirmAsync(string title, string message, string confirm);

    /// <summary>Saves a created file; false = cancelled.</summary>
    Task<bool> SaveFileAsync(string suggestedName, byte[] content);
}

/// <summary>
/// "Configure devices" (ADM's Configure Devices > Configure): choose the source (a device or an ADM configuration file), check
/// the parameters, action rules and stream profiles to apply, review, apply. Parameter definitions are read once per model and
/// AXIS OS of the selection; values come from the source only (decision Q2). The result is the task payload.
/// </summary>
public sealed partial class ConfigureDevicesViewModel : ObservableObject, IDisposable
{
    public const string Title = ConfigureIds.ConfigureName;

    public static readonly IReadOnlyList<string> StepNames = ["Settings", "Overview"];

    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly IReadOnlyList<IDeviceInfo> _allDevices;
    private readonly Func<Guid, string, CancellationToken, Task<string?>> _query;
    private readonly IConfigureUi _ui;
    private readonly bool _mayDangerous;

    /// <summary>Definitions per "model|AXIS OS" of the selection, read once per dialog (only successful reads are kept).</summary>
    private readonly Dictionary<string, DeviceSettings> _definitions = new(StringComparer.Ordinal);

    /// <summary>Cancels the server reads still running when the dialog closes.</summary>
    private readonly CancellationTokenSource _closing = new();

    private CancellationToken Token => _closing.IsCancellationRequested ? new CancellationToken(canceled: true) : _closing.Token;

    /// <param name="devices">The selected devices (the targets).</param>
    /// <param name="allDevices">Every device of the system (the source may be any of them).</param>
    /// <param name="query">The task query (device id, method) → JSON.</param>
    /// <param name="mayDangerous">The role has "Configure dangerous parameters".</param>
    public ConfigureDevicesViewModel(IReadOnlyList<IDeviceInfo> devices, IReadOnlyList<IDeviceInfo> allDevices,
        Func<Guid, string, CancellationToken, Task<string?>> query, IConfigureUi ui, bool mayDangerous)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _allDevices = allDevices ?? throw new ArgumentNullException(nameof(allDevices));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _mayDangerous = mayDangerous;
        Steps = [.. StepNames.Select((name, i) => new WizardStepItem(i, name, () => Step, CanOpen, GoTo))];
        Groups = [.. devices.GroupBy(GroupKey, StringComparer.Ordinal).Select(g => g.ToList())];
    }

    public IReadOnlyList<WizardStepItem> Steps { get; }

    /// <summary>The selection grouped by model and AXIS OS (definitions are the same within a group).</summary>
    public IReadOnlyList<List<IDeviceInfo>> Groups { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsStep), nameof(IsOverviewStep), nameof(NextText))]
    public partial int Step { get; private set; }

    public bool IsSettingsStep => Step == 0;

    public bool IsOverviewStep => Step == 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial SettingsEditorViewModel? Editor { get; private set; }

    public bool HasEditor => Editor is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsLoading { get; private set; }

    public bool IsIdle => !IsLoading;

    [ObservableProperty]
    public partial string LoadingText { get; private set; } = string.Empty;

    /// <summary>A file as the source (like ADM, only then): the file name, its model and AXIS OS; empty for a device.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> FileLines { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceSource))]
    public partial bool IsFileSource { get; private set; }

    public bool IsDeviceSource => !IsFileSource;

    public IDeviceInfo? SourceDevice { get; private set; }

    private string? _sourceName;

    /// <summary>Notes under the source: models differ, devices that could not be read, file parameters no device has.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Notes { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string DevicesText => _devices.Count == 1
        ? $"{_devices[0].Address} ({ModelNames.Display(_devices[0].Model)})"
        : string.Create(CultureInfo.CurrentCulture, $"{_devices.Count:N0} devices, {Groups.Count:N0} {(Groups.Count == 1 ? "model" : "models and AXIS OS versions")}");

    public string NextText => IsOverviewStep ? "Apply" : "Next";

    /// <summary>Why Next / Apply is disabled; null when it can be pressed.</summary>
    public string? NextBlockedReason => IsLoading ? "Reading the devices"
        : Editor is null ? Error ?? "Choose a source"
        : !Editor.HasChoice ? "Check what to apply"
        : Editor.FirstProblem;

    public bool CanNext => NextBlockedReason is null;

    /// <summary>The payload when Apply was pressed; null = cancelled.</summary>
    public string? Result { get; private set; }

    public event EventHandler? CloseRequested;

    /// <summary>Overview step: headings and lines of what will be applied.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<OverviewItem> Overview { get; private set; } = [];

    /// <summary>The dangerous parameters chosen (the overview warns, Apply asks).</summary>
    public IReadOnlyList<string> DangerousChosen => [.. Editor?.CheckedParameters.Where(p => DangerousParameters.IsDangerous(p.Name)).Select(p => p.Name) ?? []];

    public bool HasDangerousChosen => DangerousChosen.Count > 0;

    /// <summary>Reads the first selected device that OADM can reach as the source.</summary>
    public Task InitializeAsync()
    {
        var first = _devices.FirstOrDefault(d => d.Status == DeviceStatus.Ok) ?? (_devices.Count > 0 ? _devices[0] : null);
        return first is null ? Task.CompletedTask : LoadDeviceAsync(first);
    }

    [RelayCommand]
    private async Task UseDeviceAsync()
    {
        if (await _ui.PickDeviceAsync(_allDevices, SourceDevice).ConfigureAwait(true) is { } device)
        {
            await LoadDeviceAsync(device).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task UseFileAsync()
    {
        PickedFile? file;
        try
        {
            file = await _ui.PickFileAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "The file could not be read: " + ex.Message;
            return;
        }

        if (file is null)
        {
            return;
        }

        if (file.TooLarge)
        {
            Error = "The file is larger than 1 MB. Nothing was read.";
            return;
        }

        AdmConfig config;
        try
        {
            config = AdmConfigFile.Read(file.Content);
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
            return;
        }

        await LoadAsync(null, config, file.Name).ConfigureAwait(true);
    }

    /// <summary>Reads <paramref name="device"/> as the source (and the definitions of every model of the selection).</summary>
    public Task LoadDeviceAsync(IDeviceInfo device) => LoadAsync(device, null, null);

    /// <summary>Uses an ADM file as the source (tests and the file button).</summary>
    public Task LoadFileAsync(AdmConfig config, string name) => LoadAsync(null, config, name);

    private async Task LoadAsync(IDeviceInfo? device, AdmConfig? file, string? fileName)
    {
        IsLoading = true;
        Error = null;
        RaiseButtons();
        try
        {
            DeviceSettings? source = null;
            if (device is not null)
            {
                LoadingText = $"Reading {device.Address}";
                source = await ReadAsync(device.Id, ConfigureIds.ReadQuery).ConfigureAwait(true);
                if (source.Error is not null)
                {
                    Error = $"{device.Address} could not be read: {source.Error}";
                    return;
                }

                // The source's own group needs no second read.
                var own = Groups.FirstOrDefault(g => g.Any(d => d.Id == device.Id));
                if (own is not null)
                {
                    _definitions[GroupKey(own[0])] = source;
                }
            }

            var problems = await ReadDefinitionsAsync().ConfigureAwait(true);
            var readGroups = Groups.Select(g => _definitions.GetValueOrDefault(GroupKey(g[0]))).OfType<DeviceSettings>().Where(s => s.Error is null).ToList();
            if (readGroups.Count == 0)
            {
                Error = problems.FirstOrDefault() ?? "No selected device could be read.";
                return;
            }

            var merged = DefinitionMerge.Merge([.. readGroups.Select(s => s.Parameters)]);
            var notes = new List<string>(problems);
            if (Groups.Count > 1)
            {
                notes.Insert(0, "Different models or AXIS OS: some settings do not apply to all devices.");
            }

            IReadOnlyDictionary<string, string> values;
            IReadOnlySet<string>? preChecked = null;
            if (file is not null)
            {
                values = file.Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
                var known = merged.Select(m => m.Definition.Name).ToHashSet(StringComparer.Ordinal);
                preChecked = values.Keys.Where(known.Contains).ToHashSet(StringComparer.Ordinal);
                var missing = values.Keys.Where(k => !known.Contains(k)).ToList();
                if (missing.Count > 0)
                {
                    notes.Add(string.Create(CultureInfo.CurrentCulture, $"{missing.Count:N0} {(missing.Count == 1 ? "parameter" : "parameters")} of the file no selected device has: ")
                        + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? string.Create(CultureInfo.CurrentCulture, $" (+{missing.Count - 5:N0} more)") : string.Empty));
                }

                if (!_mayDangerous && preChecked.Any(DangerousParameters.IsDangerous))
                {
                    notes.Add("Your role does not allow the dangerous parameters of the file; they are left out.");
                }

                if (_devices.Count > 1 && preChecked.Any(DangerousParameters.IsPerDevice))
                {
                    notes.Add("The file's addresses and names are left out. " + DangerousParameters.PerDeviceNote);
                }
                else if (preChecked.Any(DangerousParameters.IsAddress))
                {
                    notes.Add("The file's IP addresses are left out. " + DangerousParameters.AddressNote);
                }
            }
            else
            {
                values = source!.Parameters.Where(p => p.Value is not null).ToDictionary(p => p.Name, p => p.Value!, StringComparer.Ordinal);
                notes.AddRange(source.Problems);
                if (_devices.Count > 1 && _devices.Any(d => d.Id == device!.Id))
                {
                    notes.Add($"{device!.Address} is the source: its own action rules and stream profiles stay as they are.");
                }
            }

            // Only the source itself is selected: its rules and profiles are already on it.
            var onlySource = file is null && _devices.All(d => d.Id == device!.Id);

            var editor = new SettingsEditorViewModel(merged, values, preChecked, _mayDangerous,
                onlySource ? [] : file?.Rules ?? source!.Rules,
                file?.Schedules ?? source!.Schedules,
                onlySource ? [] : file?.StreamProfiles ?? source!.StreamProfiles,
                severalDevices: _devices.Count > 1)
            {
                OwnSettingsNote = onlySource ? $"These are the own settings of {device!.Address}; they are already on it." : null,
            };
            foreach (var rule in editor.Rules.Where(_ => file is not null))
            {
                rule.IsChecked = true;
            }

            foreach (var profile in editor.Profiles.Where(_ => file is not null))
            {
                profile.IsChecked = true;
            }

            if (Editor is not null)
            {
                Editor.ChoiceChanged -= OnChoiceChanged;
            }

            editor.ChoiceChanged += OnChoiceChanged;
            Editor = editor;
            Notes = notes;
            IsFileSource = file is not null;
            SourceDevice = device;
            FileLines = file is null
                ? []
                : [fileName ?? "Configuration file", "Model: " + (file.IsGeneric ? "Any" : ModelNames.Display(file.Model)), "AXIS OS: " + (file.IsGeneric ? "Any" : file.FirmwareVersion)];
            _sourceName = device is not null ? $"{device.Address} ({ModelNames.Display(device.Model)})" : fileName;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            Error = ExportConfigViewModel.ErrorText(ex);
        }
        finally
        {
            IsLoading = false;
            LoadingText = string.Empty;
            RaiseButtons();
        }
    }

    /// <summary>One query; a server error (gRPC status, timeout, lost connection) becomes the answer's error.</summary>
    private async Task<DeviceSettings> ReadAsync(Guid id, string method)
    {
        try
        {
            return DeviceSettings.Parse(await _query(id, method, Token).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_closing.IsCancellationRequested)
        {
            return new DeviceSettings { Error = ExportConfigViewModel.ErrorText(ex) };
        }
    }

    /// <summary>One device per model and AXIS OS not read yet (at most <see cref="ConfigureIds.ReadParallelism"/> at a time).</summary>
    private async Task<List<string>> ReadDefinitionsAsync()
    {
        var problems = new List<string>();
        var missing = Groups.Where(g => !_definitions.ContainsKey(GroupKey(g[0]))).ToList();
        var done = 0;
        var gateLock = new object();
        using var gate = new SemaphoreSlim(ConfigureIds.ReadParallelism);
        await Task.WhenAll(missing.Select(async group =>
        {
            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                // The first device OADM can reach stands for its group.
                var device = group.FirstOrDefault(d => d.Status == DeviceStatus.Ok) ?? group[0];
                var settings = await ReadAsync(device.Id, ConfigureIds.DefinitionsQuery).ConfigureAwait(true);
                lock (gateLock)
                {
                    // A failed read is not kept: the next source change tries the group again.
                    if (settings.Error is null)
                    {
                        _definitions[GroupKey(group[0])] = settings;
                    }
                    else
                    {
                        problems.Add($"{device.Address} ({ModelNames.Display(device.Model)}, AXIS OS {device.FirmwareVersion}) could not be read: {settings.Error.TrimEnd('.')}. Its devices check the settings when they run.");
                    }
                }
            }
            finally
            {
                gate.Release();
                lock (gateLock)
                {
                    done++;
                    LoadingText = string.Create(CultureInfo.CurrentCulture, $"Reading the models: {done:N0} of {missing.Count:N0}");
                }
            }
        })).ConfigureAwait(true);
        return problems;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanNext || Editor is null)
        {
            return;
        }

        if (IsSettingsStep)
        {
            GoTo(1);
            return;
        }

        if (HasDangerousChosen && !await _ui.ConfirmAsync(
                "Dangerous settings",
                "These settings can make the devices unreachable:" + Environment.NewLine
                + string.Join(Environment.NewLine, DangerousChosen.Select(n => "• " + n)) + Environment.NewLine + Environment.NewLine
                + string.Create(CultureInfo.CurrentCulture, $"Apply them to {_devices.Count:N0} {(_devices.Count == 1 ? "device" : "devices")}?"),
                "Apply anyway").ConfigureAwait(true))
        {
            return;
        }

        Result = new ConfigurePayload
        {
            Parameters = Editor.CheckedParameters,
            Rules = Editor.CheckedRules,
            Schedules = Editor.CheckedSchedules,
            StreamProfiles = Editor.CheckedProfiles,
            Source = _sourceName,
            SourceDeviceId = SourceDevice?.Id,
        }.ToJson();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Back()
    {
        if (IsOverviewStep)
        {
            GoTo(0);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window closed (also with its X): reads still running are cancelled.</summary>
    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    private bool CanOpen(int index) => index == 0 || CanNext;

    /// <summary>
    /// Opens a step; the overview (from Next or its step dot) is always built from what is checked now, so Apply sends exactly
    /// what the user reviewed.
    /// </summary>
    private void GoTo(int index)
    {
        if (!CanOpen(index) || Editor is null && index > 0)
        {
            return;
        }

        if (index == 1)
        {
            Overview = Editor!.Overview();
            OnPropertyChanged(nameof(DangerousChosen));
            OnPropertyChanged(nameof(HasDangerousChosen));
        }

        Step = index;
        foreach (var item in Steps)
        {
            item.Refresh();
        }
    }

    private void OnChoiceChanged(object? sender, EventArgs e) => RaiseButtons();

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(NextBlockedReason));
        OnPropertyChanged(nameof(CanNext));
        foreach (var item in Steps)
        {
            item.Refresh();
        }
    }

    internal static string GroupKey(IDeviceInfo device) => ModelNames.Normalize(device.Model) + "|" + device.FirmwareVersion;
}

/// <summary>
/// "Create configuration file" (ADM's Configure Devices > Create configuration file): reads one device, the user checks what
/// to save, reviews, and saves an ADM configuration file. Never a task.
/// </summary>
public sealed partial class CreateConfigFileViewModel : ObservableObject, IDisposable
{
    public const string Title = ConfigureIds.CreateName;

    private readonly IDeviceInfo _device;
    private readonly Func<string, CancellationToken, Task<string?>> _query;
    private readonly IConfigureUi _ui;
    private readonly CancellationTokenSource _closing = new();
    private DeviceSettings? _settings;

    private CancellationToken Token => _closing.IsCancellationRequested ? new CancellationToken(canceled: true) : _closing.Token;

    public CreateConfigFileViewModel(IDeviceInfo device, Func<string, CancellationToken, Task<string?>> query, IConfigureUi ui)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Steps = [.. ConfigureDevicesViewModel.StepNames.Select((name, i) => new WizardStepItem(i, name, () => Step, i2 => i2 == 0 || CanNext, GoTo))];
    }

    public IReadOnlyList<WizardStepItem> Steps { get; }

    public string DeviceText => $"{_device.Address} ({ModelNames.Display(_device.Model)})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsStep), nameof(IsOverviewStep), nameof(NextText))]
    public partial int Step { get; private set; }

    public bool IsSettingsStep => Step == 0;

    public bool IsOverviewStep => Step == 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial SettingsEditorViewModel? Editor { get; private set; }

    public bool HasEditor => Editor is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    public partial IReadOnlyList<string> Notes { get; private set; } = [];

    public string NextText => IsOverviewStep ? "Save" : "Next";

    public string? NextBlockedReason => IsLoading ? "Reading the device"
        : Editor is null ? Error ?? "Reading the device"
        : !Editor.HasChoice ? "Check what to save"
        : Editor.FirstProblem;

    public bool CanNext => NextBlockedReason is null;

    /// <summary>Overview step: headings and lines of what will be saved.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<OverviewItem> Overview { get; private set; } = [];

    /// <summary>True once a file was saved.</summary>
    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    public async Task InitializeAsync()
    {
        IsLoading = true;
        Raise();
        try
        {
            _settings = DeviceSettings.Parse(await _query(ConfigureIds.ReadQuery, Token).ConfigureAwait(true));
            if (_settings.Error is not null)
            {
                Error = $"{_device.Address} could not be read: {_settings.Error}";
                return;
            }

            var editor = new SettingsEditorViewModel(
                [.. _settings.Parameters.Select(p => new MergedParam(p, 1, 1, false))],
                null, null, allowDangerous: true, _settings.Rules, _settings.Schedules, _settings.StreamProfiles);
            editor.ChoiceChanged += (_, _) => Raise();
            Editor = editor;
            Notes = _settings.Problems;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The dialog was closed.
        }
        catch (Exception ex)
        {
            Error = $"{_device.Address} could not be read: {ExportConfigViewModel.ErrorText(ex)}";
        }
        finally
        {
            IsLoading = false;
            Raise();
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanNext || Editor is null || _settings is null)
        {
            return;
        }

        if (IsSettingsStep)
        {
            GoTo(1);
            return;
        }

        var config = new AdmConfig
        {
            Model = ModelNames.Display(_settings.Model ?? _device.Model),
            FirmwareVersion = _settings.Firmware ?? _device.FirmwareVersion ?? AdmConfig.Generic,
            MacAddress = _settings.Serial ?? _device.Serial,
            Generated = DateTimeOffset.UtcNow,
            Parameters = Editor.CheckedParameters,
            // Like ADM: with a rule, every schedule of the device goes into the file.
            Schedules = Editor.CheckedRules.Count > 0 ? Editor.Schedules : [],
            Rules = Editor.CheckedRules,
            StreamProfiles = Editor.CheckedProfiles,
        };
        try
        {
            if (await _ui.SaveFileAsync(AdmConfigFile.SuggestedFileName(_settings.Model ?? _device.Model, _settings.Firmware), AdmConfigFile.Write(config)).ConfigureAwait(true))
            {
                Saved = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "The file could not be saved: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (IsOverviewStep)
        {
            GoTo(0);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The window closed: the read still running is cancelled.</summary>
    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    /// <summary>Opens a step; the overview is always built from what is checked now (Next or its step dot).</summary>
    private void GoTo(int index)
    {
        if (index > 0 && (Editor is null || !CanNext))
        {
            return;
        }

        if (index == 1)
        {
            Overview = Editor!.Overview();
        }

        Step = index;
        foreach (var item in Steps)
        {
            item.Refresh();
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(NextBlockedReason));
        OnPropertyChanged(nameof(CanNext));
        foreach (var item in Steps)
        {
            item.Refresh();
        }
    }
}
