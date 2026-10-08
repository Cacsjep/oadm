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

    /// <summary>Server settings can only be changed by administrators (the server checks it too).</summary>
    public bool IsAdmin => _session?.IsAdmin ?? true;

    public bool IsOperator => !IsAdmin;

    public SettingsViewModel(IOadmApi api, ServerConnection connection, ILogger<SettingsViewModel> logger, UserSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _api = api;
        _logger = logger;
        _session = session;
        connection.Connected += (_, _) => _ = LoadAsync();
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    OnPropertyChanged(nameof(IsOperator));
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
            .Rule(nameof(MaxParallelTasksPerPlugin), () => RangeError(MaxParallelTasksPerPlugin, MinParallelTasks, MaxParallelTasks))
            .Rule(nameof(ServerName), () => ServerName.Trim().Length == 0 ? "Enter a server name." : null)
            .Rule(nameof(ListenUrl), () => ListenUrlError(ListenUrl));
        Validation.Validate();
        Validation.Reset();
    }

    private static readonly string[] ServerFields =
    [
        nameof(PollingIntervalSeconds), nameof(FullRefreshMinutes), nameof(ScanParallelism), nameof(ScanTimeoutMs),
        nameof(ZeroConfSeconds), nameof(MaxParallelTasksPerPlugin), nameof(ServerName), nameof(ListenUrl),
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

    /// <summary>
    /// Tasks.MaxParallelPerPlugin: how many devices a task (restart, firmware update, ...) runs on at the same
    /// time (1..256, default 16). Applies to tasks that start after saving.
    /// </summary>
    [ObservableProperty] public partial decimal? MaxParallelTasksPerPlugin { get; set; } = DefaultParallelTasks;
    [ObservableProperty] public partial string ServerName { get; set; } = "";
    [ObservableProperty] public partial string ListenUrl { get; set; } = "";

    /// <summary>Devices.UseHostName: newly added devices are addressed by host name when one is known.</summary>
    [ObservableProperty] public partial bool UseHostName { get; set; }
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
            MaxParallelTasksPerPlugin = (int)(MaxParallelTasksPerPlugin ?? DefaultParallelTasks),
            ServerName = ServerName.Trim(),
            ListenUrl = ListenUrl.Trim(),
            UseHostName = UseHostName,
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
        ServerName = settings.ServerName;
        ListenUrl = settings.ListenUrl;
        UseHostName = settings.UseHostName;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server settings saved")]
    private static partial void LogSaved(ILogger logger);
}
