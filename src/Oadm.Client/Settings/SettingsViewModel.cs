using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

using Rules = Oadm.Contracts.Settings.ServerSettingsRules;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>
/// Settings page (docs/spec/settings-backup.md "Settings page"): the client settings (saved at once), the server settings
/// (SettingsService) and, for administrators, the automatic backups, saved together by the one Save. Every field reports
/// its error below itself (<see cref="ValidatingViewModel"/>); Save stays disabled while a field of either has an error.
/// Operators see the server values read-only. <see cref="Filter"/> is the page's search. Users, the credential list and
/// About are pages of their own. The server address of this client is set with <c>--server</c> or the client settings file.
/// </summary>
public sealed partial class SettingsViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly UserSession? _session;

    private bool _gpuDecoding;
    private bool _tellAboutNewVersions;

    /// <summary>"Tell me about new versions" (this computer, saved at once): the new version dialog at client start.</summary>
    public bool TellAboutNewVersions
    {
        get => _tellAboutNewVersions;
        set
        {
            if (SetProperty(ref _tellAboutNewVersions, value) && _clientSettings is not null)
            {
                _clientSettings.Current.TellAboutNewVersions = value;
                _clientSettings.Save();
            }
        }
    }

    /// <summary>
    /// "Decode video on the graphics card" (this computer, saved at once, every user): the live view decodes on the graphics
    /// card when the test at the start passed, else on the processor with a warning.
    /// </summary>
    public bool GpuDecoding
    {
        get => _gpuDecoding;
        set
        {
            if (!SetProperty(ref _gpuDecoding, value))
            {
                return;
            }

            if (_clientSettings is not null)
            {
                _clientSettings.Current.GpuDecoding = value;
                _clientSettings.Save();
            }

            _gpu?.SetEnabled(value);
            OnPropertyChanged(nameof(GpuDecodingHint));
        }
    }

    /// <summary>The test's result below the check box.</summary>
    public string GpuDecodingHint => !GpuDecoding ? "The processor decodes the live view."
        : _gpu?.Status is not { } status ? "Used when the graphics card can decode the video."
        : status.State switch
        {
            LiveView.GpuDecodingState.Checking => "Testing the graphics card",
            LiveView.GpuDecodingState.NotChecked => "Used when the graphics card can decode the video.",
            LiveView.GpuDecodingState.Available => "The graphics card decodes " + string.Join(" and ", status.Codecs.OrderBy(c => c).Select(c => c == VideoCodec.H265 ? "H.265" : "H.264")) + ".",
            _ => status.Problem ?? LiveView.GpuDecodingTexts.Generic,
        };

    private Oadm.Theme.Appearance _appearance;

    public IReadOnlyList<Oadm.Theme.Appearance> Appearances { get; } = [Oadm.Theme.Appearance.Dark, Oadm.Theme.Appearance.Light];

    /// <summary>"Appearance" (this computer, saved at once); the theme loads its colors at the start, so it applies after a restart.</summary>
    public Oadm.Theme.Appearance Appearance
    {
        get => _appearance;
        set
        {
            if (!SetProperty(ref _appearance, value))
            {
                return;
            }

            if (_clientSettings is not null)
            {
                _clientSettings.Current.Appearance = value;
                _clientSettings.Save();
            }

            OnPropertyChanged(nameof(AppearanceHint));
        }
    }

    /// <summary>The hint of Appearance; while the chosen appearance is not the running one, that it needs a restart.</summary>
    public string AppearanceHint => _appearance == Oadm.Theme.OadmColors.Current ? AppearanceDefaultHint : AppearanceRestartHint;

    public const string AppearanceDefaultHint = "Dark or light colors";

    public const string AppearanceRestartHint = "Takes effect after the client restarts";

    /// <summary>The search of the page ("Find a setting"): shows the rows whose title or hint contains it.</summary>
    [ObservableProperty] public partial string? Filter { get; set; }

    /// <summary>Live video on this computer: details over the picture, the adaptive frame rate.</summary>
    public LiveView.LiveVideoSettings Video { get; }

    /// <summary>OADM PRO and the server ID (every user; administrators import and remove); null in tests without it.</summary>
    public Licensing.LicenseViewModel? License { get; }

    public bool ShowLicense => License is not null;


    /// <summary>Backup and restore and the automatic backups (administrators); null without a dialog service (tests).</summary>
    public Oadm.Client.Backup.BackupViewModel? Backup { get; }

    public bool ShowBackup => Backup is not null && IsAdmin;

    /// <summary>The support report (administrators); null in tests without it.</summary>
    public Oadm.Client.Support.SupportViewModel? Support { get; }

    public bool ShowSupport => Support is not null && IsAdmin;

    /// <summary>Server settings can only be changed by administrators (the server checks it too).</summary>
    public bool IsAdmin => _session?.IsAdmin ?? true;

    public bool IsOperator => !IsAdmin;

    private readonly Infrastructure.IClientSettingsStore? _clientSettings;
    private readonly LiveView.GpuDecodingService? _gpu;
    private readonly Infrastructure.IUiDispatcher? _ui;

    public SettingsViewModel(IOadmApi api, ServerConnection connection, ILogger<SettingsViewModel> logger, UserSession? session = null,
        Infrastructure.IClientSettingsStore? clientSettings = null, LiveView.GpuDecodingService? gpu = null, Infrastructure.IUiDispatcher? ui = null,
        LiveView.LiveVideoSettings? video = null, Oadm.Client.Backup.BackupViewModel? backup = null,
        Licensing.LicenseViewModel? license = null, Oadm.Client.Support.SupportViewModel? support = null,
        Devices.DeviceStore? devices = null)
    {
        License = license;
        Support = support;
        Video = video ?? new LiveView.LiveVideoSettings(clientSettings);
        Backup = backup;
        if (backup is not null)
        {
            backup.PropertyChanged += OnBackupChanged;
        }

        ArgumentNullException.ThrowIfNull(connection);
        _api = api;
        _logger = logger;
        _session = session;
        _clientSettings = clientSettings;
        _gpu = gpu;
        _ui = ui;
        if (devices is not null)
        {
            AttachVulnerabilities(devices.Vulnerabilities, new Vulnerabilities.VulnerabilityUpdates(
                api, devices.Vulnerabilities, ui ?? new Infrastructure.ImmediateUiDispatcher()));
        }

        _gpuDecoding = clientSettings?.Current.GpuDecoding ?? true;
        _tellAboutNewVersions = clientSettings?.Current.TellAboutNewVersions ?? true;
        _appearance = clientSettings?.Current.Appearance ?? Oadm.Theme.OadmColors.Current;
        if (gpu is not null)
        {
            gpu.Changed += (_, _) =>
            {
                if (_ui is null)
                {
                    OnPropertyChanged(nameof(GpuDecodingHint));
                }
                else
                {
                    _ui.Post(() => OnPropertyChanged(nameof(GpuDecodingHint)));
                }
            };
        }
        connection.Connected += (_, _) => _ = LoadAsync();
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    OnPropertyChanged(nameof(IsOperator));
                    OnPropertyChanged(nameof(ShowBackup));
                    OnPropertyChanged(nameof(ShowSupport));
                    if (ShowBackup && IsLoaded)
                    {
                        _ = Backup!.LoadAsync();
                    }
                    RefreshSave();
                }
            };
        }

        Validation
            .Rule(nameof(ClockToleranceSeconds), () => RangeError(ClockToleranceSeconds, MinClockTolerance, MaxClockTolerance))
            .Rule(nameof(ScanParallelism), () => RangeError(ScanParallelism, Rules.MinScanParallelism, Rules.MaxScanParallelism))
            .Rule(nameof(ScanTimeoutMs), () => RangeError(ScanTimeoutMs, Rules.MinScanTimeoutMs, Rules.MaxScanTimeoutMs))
            .Rule(nameof(ZeroConfSeconds), () => RangeError(ZeroConfSeconds, Rules.MinZeroConfSeconds, Rules.MaxZeroConfSeconds))
            .Rule(nameof(WaitingRetryMinutes), () => RangeError(WaitingRetryMinutes, Rules.MinWaitingRetryMinutes, Rules.MaxWaitingRetryMinutes))
            .Rule(nameof(MaxParallelTasksPerPlugin), () => RangeError(MaxParallelTasksPerPlugin, MinParallelTasks, MaxParallelTasks))
            .Rule(nameof(ListenUrl), () => ListenUrlError(ListenUrl));
        Validation.Validate();
        Validation.Reset();
    }

    private static readonly string[] ServerFields =
    [
        nameof(ClockToleranceSeconds), nameof(ScanParallelism), nameof(ScanTimeoutMs),
        nameof(ZeroConfSeconds), nameof(WaitingRetryMinutes), nameof(MaxParallelTasksPerPlugin), nameof(ListenUrl),
    ];

    /// <summary>Range and default of Tasks.MaxParallelPerPlugin (same as the server).</summary>
    public const int MinParallelTasks = Rules.MinParallelTasks;
    public const int MaxParallelTasks = Rules.MaxParallelTasks;
    public const int DefaultParallelTasks = 16;

    /// <summary>Range and default of Polling.ClockToleranceSeconds (same as the server).</summary>
    public const int MinClockTolerance = Rules.MinClockTolerance;
    public const int MaxClockTolerance = Rules.MaxClockTolerance;
    public const int DefaultClockTolerance = 2;

    /// <summary>Why Save is disabled (tooltip): the first error of the server settings, else of the automatic backups.</summary>
    public string? SaveBlockedReason => Validation.FirstErrorOf(ServerFields) ?? (ShowBackup ? Backup!.FormError : null);

    /// <summary>The automatic backups are saved with the server settings (administrators).</summary>
    private bool ScheduleIsValid => !ShowBackup || Backup!.IsFormValid;

    protected override void OnValidationChanged() => RefreshSave();

    private void RefreshSave()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void OnBackupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ValidatingViewModel.FormError) or nameof(ValidatingViewModel.IsFormValid))
        {
            RefreshSave();
        }
        else if (Oadm.Client.Backup.BackupViewModel.IsScheduleField(e.PropertyName))
        {
            ClearSaved();
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (Array.IndexOf(ServerFields, e.PropertyName) >= 0 || e.PropertyName is nameof(AllowBasicOverHttp) or nameof(VulnerabilityUpdateIndex))
        {
            ClearSaved();
        }
    }

    /// <summary>An edit after "Saved." takes the message away.</summary>
    private void ClearSaved()
    {
        if (ServerMessage is not null && !ServerMessageIsError && !IsBusy)
        {
            ServerMessage = null;
        }
    }

    /// <summary>"Enter a whole number from 5 to 86400." for an empty or out-of-range number field.</summary>
    public static string? RangeError(decimal? value, int min, int max) => Rules.RangeError(value, min, max);

    /// <summary>The listen URL Kestrel binds to: http(s), a host (also * or +) and optionally a port.</summary>
    public static string? ListenUrlError(string? url) => Rules.ListenUrlError(url);

    // server side

    /// <summary>Polling.ClockToleranceSeconds: a device clock further from the server's counts as off (1..3600, default 2).</summary>
    [ObservableProperty] public partial decimal? ClockToleranceSeconds { get; set; } = DefaultClockTolerance;
    [ObservableProperty] public partial decimal? ScanParallelism { get; set; } = 32;
    [ObservableProperty] public partial decimal? ScanTimeoutMs { get; set; } = 1500;

    /// <summary>Discovery.ZeroConfSeconds: a zero-conf scan of the add page ends after this many seconds (5..300).</summary>
    [ObservableProperty] public partial decimal? ZeroConfSeconds { get; set; } = 30;

    /// <summary>Devices.WaitingRetryMinutes: how often the server tries the waiting devices of imports again (1..1440).</summary>
    [ObservableProperty] public partial decimal? WaitingRetryMinutes { get; set; } = 15;

    /// <summary>
    /// Tasks.MaxParallelPerPlugin: how many devices a task (restart, firmware update, ...) runs on at the same
    /// time (1..256, default 16). Applies to tasks that start after saving.
    /// </summary>
    [ObservableProperty] public partial decimal? MaxParallelTasksPerPlugin { get; set; } = DefaultParallelTasks;
    [ObservableProperty] public partial string ServerName { get; set; } = "";
    [ObservableProperty] public partial string ListenUrl { get; set; } = "";

    /// <summary>
    /// Devices.AllowBasicOverHttp: devices that accept only Basic login get it over plain HTTP too (default off; Digest
    /// stays preferred). Applies to the next request after saving.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllowBasicOverHttpText))]
    public partial bool AllowBasicOverHttp { get; set; }

    /// <summary>"On" or "Off" for operators, who read the server settings as plain text.</summary>
    public string AllowBasicOverHttpText => AllowBasicOverHttp ? Oadm.Sdk.Client.Controls.OnOffSwitch.OnText : Oadm.Sdk.Client.Controls.OnOffSwitch.OffText;

    [ObservableProperty] public partial string? ServerMessage { get; private set; }
    [ObservableProperty] public partial bool ServerMessageIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanSave => IsLoaded && !IsBusy && IsAdmin && Validation.IsValidFor(ServerFields) && ScheduleIsValid;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            Apply(await _api.GetSettingsAsync(CancellationToken.None).ConfigureAwait(true));
            Validation.Reset(ServerFields); // loaded values: nothing edited yet
            if (!Validation.IsValidFor(ServerFields))
            {
                Validation.ShowAll(ServerFields); // a stored value the form refuses: shown (its expander opens), else Save stays disabled without a reason in sight
            }

            IsLoaded = true;
            ServerMessage = null;
            if (ShowBackup)
            {
                await Backup!.LoadAsync().ConfigureAwait(true);
            }

            if (License is not null)
            {
                await License.LoadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            IsLoaded = false;
            ServerMessageIsError = true;
            ServerMessage = "Server settings are not available: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The one Save of the page: the server settings, then (administrators) the automatic backups when they changed.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!Validation.IsValidFor(ServerFields) || !ScheduleIsValid)
        {
            Validation.ShowAll(ServerFields);
            Backup?.ShowScheduleErrors();
            return;
        }

        var settings = new ServerSettings
        {
            ClockToleranceSeconds = (int)(ClockToleranceSeconds ?? DefaultClockTolerance),
            ScanParallelism = (int)(ScanParallelism ?? 32),
            ScanTimeoutMs = (int)(ScanTimeoutMs ?? 1500),
            ZeroConfSeconds = (int)(ZeroConfSeconds ?? 30),
            WaitingRetryMinutes = (int)(WaitingRetryMinutes ?? 15),
            MaxParallelTasksPerPlugin = (int)(MaxParallelTasksPerPlugin ?? DefaultParallelTasks),
            AllowBasicOverHttp = AllowBasicOverHttp,
            VulnerabilityUpdate = VulnerabilityValue,
            ServerName = ServerName.Trim(), // not on the page (decision 2026-11-19): the loaded name goes back
            ListenUrl = ListenUrl.Trim(),
        };
        try
        {
            IsBusy = true;
            Apply(await _api.SetSettingsAsync(settings, CancellationToken.None).ConfigureAwait(true));
            await ReadVulnerabilitiesAsync().ConfigureAwait(true); // Check now follows the saved setting
            Validation.Reset(ServerFields);
            if (ShowBackup && Backup!.IsScheduleChanged)
            {
                await Backup.SaveScheduleAsync(CancellationToken.None).ConfigureAwait(true);
            }

            ServerMessageIsError = false;
            ServerMessage = SavedText;
            LogSaved(_logger);
        }
        catch (Exception ex)
        {
            ServerMessageIsError = true;
            ServerMessage = "Saving failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The result of Save, directly left of it.</summary>
    public const string SavedText = "Saved.";

    private void Apply(ServerSettings settings)
    {
        ClockToleranceSeconds = settings.HasClockToleranceSeconds ? settings.ClockToleranceSeconds : DefaultClockTolerance; // unset = older server
        ScanParallelism = settings.ScanParallelism;
        ScanTimeoutMs = settings.ScanTimeoutMs;
        ZeroConfSeconds = settings.ZeroConfSeconds > 0 ? settings.ZeroConfSeconds : 30; // 0 = older server without the setting
        MaxParallelTasksPerPlugin = settings.HasMaxParallelTasksPerPlugin ? settings.MaxParallelTasksPerPlugin : DefaultParallelTasks; // unset = older server
        WaitingRetryMinutes = settings.HasWaitingRetryMinutes ? settings.WaitingRetryMinutes : 15; // unset = older server
        AllowBasicOverHttp = settings.HasAllowBasicOverHttp && settings.AllowBasicOverHttp; // unset = older server (off)
        VulnerabilityUpdateIndex = VulnerabilityIndexOf(settings.HasVulnerabilityUpdate ? settings.VulnerabilityUpdate : null); // unset = older server
        ServerName = settings.ServerName;
        ListenUrl = settings.ListenUrl;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server settings saved")]
    private static partial void LogSaved(ILogger logger);
}
