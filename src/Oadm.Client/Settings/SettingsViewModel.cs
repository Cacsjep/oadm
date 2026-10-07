using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Grpc.Core;

using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>One credential list entry: user name and when it was added (never a password).</summary>
public sealed record CredentialItemViewModel(string Id, string UserName, string AddedText);

/// <summary>
/// Server settings (SettingsService), the credential list, plus the client-side server address. Every field
/// reports its error below itself (<see cref="ValidatingViewModel"/>); Save, Add credential and Connect stay
/// disabled while their fields have errors.
/// </summary>
public sealed partial class SettingsViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IClientSettingsStore _clientSettings;
    private readonly ServerConnection _connection;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(IOadmApi api, IClientSettingsStore clientSettings, ServerConnection connection, ILogger<SettingsViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(clientSettings);
        ArgumentNullException.ThrowIfNull(connection);
        _api = api;
        _clientSettings = clientSettings;
        _connection = connection;
        _logger = logger;
        ServerAddress = clientSettings.Current.ServerAddress;
        connection.Connected += (_, _) => _ = LoadAsync();

        Validation
            .Rule(nameof(PollingIntervalSeconds), () => RangeError(PollingIntervalSeconds, 5, 3600))
            .Rule(nameof(FullRefreshMinutes), () => RangeError(FullRefreshMinutes, 1, 1440))
            .Rule(nameof(ScanParallelism), () => RangeError(ScanParallelism, 1, 256))
            .Rule(nameof(ScanTimeoutMs), () => RangeError(ScanTimeoutMs, 100, 30000))
            .Rule(nameof(ZeroConfSeconds), () => RangeError(ZeroConfSeconds, 5, 300))
            .Rule(nameof(MaxParallelTasksPerPlugin), () => RangeError(MaxParallelTasksPerPlugin, MinParallelTasks, MaxParallelTasks))
            .Rule(nameof(ServerName), () => ServerName.Trim().Length == 0 ? "Enter a server name." : null)
            .Rule(nameof(ListenUrl), () => ListenUrlError(ListenUrl))
            .Rule(nameof(NewCredentialUserName), () => NewCredentialUserName.Trim().Length == 0 ? "Enter a user name." : null)
            .Rule(nameof(NewCredentialPassword), () => NewCredentialPassword.Length == 0 ? "Enter the password." : null)
            .Rule(nameof(ServerAddress), () => ServerAddressError(ServerAddress));
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

    private static readonly string[] CredentialFields = [nameof(NewCredentialUserName), nameof(NewCredentialPassword)];

    /// <summary>Why Save is disabled (tooltip).</summary>
    public string? SaveBlockedReason => Validation.FirstErrorOf(ServerFields);

    /// <summary>Why Add credential is disabled (tooltip).</summary>
    public string? AddCredentialBlockedReason => Validation.FirstErrorOf(CredentialFields);

    /// <summary>Why Connect is disabled (tooltip).</summary>
    public string? ConnectBlockedReason => Validation.FirstErrorOf([nameof(ServerAddress)]);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(AddCredentialBlockedReason));
        OnPropertyChanged(nameof(ConnectBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
        AddCredentialCommand.NotifyCanExecuteChanged();
        ApplyServerAddressCommand.NotifyCanExecuteChanged();
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
            return "Enter a listen URL, e.g. http://0.0.0.0:5080.";
        }

        string probe = text.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal);
        return Uri.TryCreate(probe, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : "Enter a URL like http://0.0.0.0:5080.";
    }

    /// <summary>The server address of this client: http(s) URL (a bare host gets http:// and the default port).</summary>
    public static string? ServerAddressError(string? address)
    {
        if ((address ?? "").Trim().Length == 0)
        {
            return "Enter the server address, e.g. http://server:5080.";
        }

        string normalized = GrpcOadmApi.Normalize(address!);
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : "Enter an address like http://server:5080.";
    }

    // client side
    [ObservableProperty] public partial string ServerAddress { get; set; }
    [ObservableProperty] public partial string? ClientMessage { get; private set; }

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

    /// <summary>The server's credential list, tried on every discovered device when adding devices.</summary>
    public ObservableCollection<CredentialItemViewModel> Credentials { get; } = [];

    [ObservableProperty] public partial string NewCredentialUserName { get; set; } = "root";
    [ObservableProperty] public partial string NewCredentialPassword { get; set; } = "";
    [ObservableProperty] public partial string? CredentialMessage { get; private set; }
    [ObservableProperty] public partial bool CredentialMessageIsError { get; private set; }

    public bool HasNoCredentials => Credentials.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanSave => IsLoaded && !IsBusy && Validation.IsValidFor(ServerFields);

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
            await LoadCredentialsAsync().ConfigureAwait(true);
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

    [RelayCommand(CanExecute = nameof(CanAddCredential))]
    private async Task AddCredentialAsync()
    {
        string user = NewCredentialUserName.Trim();
        if (!Validation.IsValidFor(CredentialFields))
        {
            Validation.ShowAll(CredentialFields);
            return;
        }

        CredentialMessage = null;

        try
        {
            await _api.AddCredentialAsync(user, NewCredentialPassword, CancellationToken.None).ConfigureAwait(true);
            NewCredentialPassword = "";
            Validation.Reset(CredentialFields); // ready for the next one
            CredentialMessageIsError = false;
            CredentialMessage = $"Added. OADM tries {user} on every device it finds.";
            await LoadCredentialsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // e.g. the entry exists already or the list is full: below the user name
            Validation.SetServerError(nameof(NewCredentialUserName), "Adding failed: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message));
        }
    }

    private bool CanAddCredential() => Validation.IsValidFor(CredentialFields);

    [RelayCommand]
    private async Task RemoveCredentialAsync(CredentialItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _api.RemoveCredentialAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
            CredentialMessage = null;
            await LoadCredentialsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Removing failed: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
    }

    private async Task LoadCredentialsAsync()
    {
        IReadOnlyList<CredentialEntry> entries = await _api.ListCredentialsAsync(CancellationToken.None).ConfigureAwait(true);
        Credentials.Clear();
        foreach (CredentialEntry entry in entries)
        {
            string added = entry.Created is null ? "" : "Added " + entry.Created.ToDateTime().ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            Credentials.Add(new CredentialItemViewModel(entry.Id, entry.UserName, added));
        }

        OnPropertyChanged(nameof(HasNoCredentials));
    }

    [RelayCommand(CanExecute = nameof(CanApplyServerAddress))]
    private void ApplyServerAddress()
    {
        if (!Validation.IsValidFor(nameof(ServerAddress)))
        {
            Validation.ShowAll(nameof(ServerAddress));
            return;
        }

        string address = GrpcOadmApi.Normalize(ServerAddress);

        ServerAddress = address;
        _clientSettings.Current.ServerAddress = address;
        _clientSettings.Save();
        _connection.Reconnect(address);
        ClientMessage = "Connecting to " + _connection.ServerAddress;
    }

    private bool CanApplyServerAddress() => Validation.IsValidFor(nameof(ServerAddress));

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
