using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>
/// Settings page: the server settings (SettingsService). Every field reports its error below itself
/// (<see cref="ValidatingViewModel"/>); Save stays disabled while a field has an error. Operators see the values
/// read-only. Users, the credential list and About are pages of their own. The server address of this client is set
/// with <c>--server</c> or the client settings file.
/// </summary>
public sealed partial class SettingsViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly UserSession? _session;

    private bool _gpuDecoding;

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

    /// <summary>Below the select while the chosen appearance is not the running one.</summary>
    public string? AppearanceHint => _appearance == Oadm.Theme.OadmColors.Current ? null : AppearanceRestartHint;

    public const string AppearanceRestartHint = "Takes effect after the client restarts";

    /// <summary>Live video on this computer: details over the picture, the adaptive frame rate.</summary>
    public LiveView.LiveVideoSettings Video { get; }

    /// <summary>The card "License" (every user; administrators import and remove); null in tests without it.</summary>
    public Licensing.LicenseViewModel? License { get; }

    public bool ShowLicense => License is not null;

    /// <summary>The card "Backup and restore" (administrators); null without a dialog service (tests).</summary>
    public Oadm.Client.Backup.BackupViewModel? Backup { get; }

    public bool ShowBackup => Backup is not null && IsAdmin;

    /// <summary>The card "Support" (administrators): the support report; null in tests without it.</summary>
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
        Licensing.LicenseViewModel? license = null, Oadm.Client.Support.SupportViewModel? support = null)
    {
        License = license;
        Support = support;
        Video = video ?? new LiveView.LiveVideoSettings(clientSettings);
        Backup = backup;
        ArgumentNullException.ThrowIfNull(connection);
        _api = api;
        _logger = logger;
        _session = session;
        _clientSettings = clientSettings;
        _gpu = gpu;
        _ui = ui;
        _gpuDecoding = clientSettings?.Current.GpuDecoding ?? true;
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
                    SaveCommand.NotifyCanExecuteChanged();
                }
            };
        }

        Validation
            .Rule(nameof(PollingIntervalSeconds), () => RangeError(PollingIntervalSeconds, 5, 3600))
            .Rule(nameof(FullRefreshMinutes), () => RangeError(FullRefreshMinutes, 1, 1440))
            .Rule(nameof(ScanParallelism), () => RangeError(ScanParallelism, 1, 256))
            .Rule(nameof(ScanTimeoutMs), () => RangeError(ScanTimeoutMs, 100, 30000))
            .Rule(nameof(ZeroConfSeconds), () => RangeError(ZeroConfSeconds, 5, 300))
            .Rule(nameof(WaitingRetryMinutes), () => RangeError(WaitingRetryMinutes, 1, 1440))
            .Rule(nameof(MaxParallelTasksPerPlugin), () => RangeError(MaxParallelTasksPerPlugin, MinParallelTasks, MaxParallelTasks))
            .Rule(nameof(ServerName), () => ServerName.Trim().Length == 0 ? "Enter a server name." : null)
            .Rule(nameof(ListenUrl), () => ListenUrlError(ListenUrl));
        Validation.Validate();
        Validation.Reset();
    }

    private static readonly string[] ServerFields =
    [
        nameof(PollingIntervalSeconds), nameof(FullRefreshMinutes), nameof(ScanParallelism), nameof(ScanTimeoutMs),
        nameof(ZeroConfSeconds), nameof(WaitingRetryMinutes), nameof(MaxParallelTasksPerPlugin), nameof(ServerName), nameof(ListenUrl),
    ];

    /// <summary>Range and default of Tasks.MaxParallelPerPlugin (same as the server).</summary>
    public const int MinParallelTasks = 1;
    public const int MaxParallelTasks = 256;
    public const int DefaultParallelTasks = 16;

    /// <summary>Why Save is disabled (tooltip).</summary>
    public string? SaveBlockedReason => Validation.FirstErrorOf(ServerFields);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Enter a value from 5 to 3600." for an empty or out-of-range number field.</summary>
    public static string? RangeError(decimal? value, int min, int max) =>
        value is null || value < min || value > max || decimal.Truncate(value.Value) != value
            ? string.Create(CultureInfo.CurrentCulture, $"Enter a whole number from {min} to {max}.")
            : null;

    /// <summary>The listen URL Kestrel binds to: http(s), a host (also * or +) and optionally a port.</summary>
    public static string? ListenUrlError(string? url)
    {
        string text = (url ?? "").Trim();
        if (text.Length == 0)
        {
            return "Enter a listen URL, e.g. https://0.0.0.0:5080.";
        }

        string probe = text.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal);
        return Uri.TryCreate(probe, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : "Enter a URL like https://0.0.0.0:5080.";
    }

    // server side
    [ObservableProperty] public partial decimal? PollingIntervalSeconds { get; set; } = 60;
    [ObservableProperty] public partial decimal? FullRefreshMinutes { get; set; } = 10;
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
    public string AllowBasicOverHttpText => AllowBasicOverHttp ? "On" : "Off";

    [ObservableProperty] public partial string? ServerMessage { get; private set; }
    [ObservableProperty] public partial bool ServerMessageIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanSave => IsLoaded && !IsBusy && IsAdmin && Validation.IsValidFor(ServerFields);

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            Apply(await _api.GetSettingsAsync(CancellationToken.None).ConfigureAwait(true));
            Validation.Reset(ServerFields); // loaded values: nothing edited yet
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

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!Validation.IsValidFor(ServerFields))
        {
            Validation.ShowAll(ServerFields);
            return;
        }

        var settings = new ServerSettings
        {
            PollingIntervalSeconds = (int)(PollingIntervalSeconds ?? 60),
            FullRefreshMinutes = (int)(FullRefreshMinutes ?? 10),
            ScanParallelism = (int)(ScanParallelism ?? 32),
            ScanTimeoutMs = (int)(ScanTimeoutMs ?? 1500),
            ZeroConfSeconds = (int)(ZeroConfSeconds ?? 30),
            WaitingRetryMinutes = (int)(WaitingRetryMinutes ?? 15),
            MaxParallelTasksPerPlugin = (int)(MaxParallelTasksPerPlugin ?? DefaultParallelTasks),
            AllowBasicOverHttp = AllowBasicOverHttp,
            ServerName = ServerName.Trim(),
            ListenUrl = ListenUrl.Trim(),
        };
        try
        {
            IsBusy = true;
            Apply(await _api.SetSettingsAsync(settings, CancellationToken.None).ConfigureAwait(true));
            Validation.Reset(ServerFields);
            ServerMessageIsError = false;
            ServerMessage = "Saved. A changed listen URL takes effect after the server restarts.";
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

    private void Apply(ServerSettings settings)
    {
        PollingIntervalSeconds = settings.PollingIntervalSeconds;
        FullRefreshMinutes = settings.FullRefreshMinutes;
        ScanParallelism = settings.ScanParallelism;
        ScanTimeoutMs = settings.ScanTimeoutMs;
        ZeroConfSeconds = settings.ZeroConfSeconds > 0 ? settings.ZeroConfSeconds : 30; // 0 = older server without the setting
        MaxParallelTasksPerPlugin = settings.HasMaxParallelTasksPerPlugin ? settings.MaxParallelTasksPerPlugin : DefaultParallelTasks; // unset = older server
        WaitingRetryMinutes = settings.HasWaitingRetryMinutes ? settings.WaitingRetryMinutes : 15; // unset = older server
        AllowBasicOverHttp = settings.HasAllowBasicOverHttp && settings.AllowBasicOverHttp; // unset = older server (off)
        ServerName = settings.ServerName;
        ListenUrl = settings.ListenUrl;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server settings saved")]
    private static partial void LogSaved(ILogger logger);
}
