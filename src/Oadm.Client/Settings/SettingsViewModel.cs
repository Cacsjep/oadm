using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Settings;

/// <summary>Server settings (SettingsService) plus the client-side server address.</summary>
public sealed partial class SettingsViewModel : ObservableObject
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
    }

    // client side
    [ObservableProperty] public partial string ServerAddress { get; set; }
    [ObservableProperty] public partial string? ClientMessage { get; private set; }

    // server side
    [ObservableProperty] public partial decimal? PollingIntervalSeconds { get; set; } = 60;
    [ObservableProperty] public partial decimal? FullRefreshMinutes { get; set; } = 10;
    [ObservableProperty] public partial decimal? ScanParallelism { get; set; } = 32;
    [ObservableProperty] public partial decimal? ScanTimeoutMs { get; set; } = 1500;
    [ObservableProperty] public partial string ServerName { get; set; } = "";
    [ObservableProperty] public partial string ListenUrl { get; set; } = "";
    [ObservableProperty] public partial string? ServerMessage { get; private set; }
    [ObservableProperty] public partial bool ServerMessageIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanSave => IsLoaded && !IsBusy;

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            Apply(await _api.GetSettingsAsync(CancellationToken.None).ConfigureAwait(true));
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
        var settings = new ServerSettings
        {
            PollingIntervalSeconds = (int)(PollingIntervalSeconds ?? 60),
            FullRefreshMinutes = (int)(FullRefreshMinutes ?? 10),
            ScanParallelism = (int)(ScanParallelism ?? 32),
            ScanTimeoutMs = (int)(ScanTimeoutMs ?? 1500),
            ServerName = ServerName.Trim(),
            ListenUrl = ListenUrl.Trim(),
        };
        try
        {
            IsBusy = true;
            Apply(await _api.SetSettingsAsync(settings, CancellationToken.None).ConfigureAwait(true));
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

    [RelayCommand]
    private void ApplyServerAddress()
    {
        string address = GrpcOadmApi.Normalize(ServerAddress);
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ClientMessage = "Enter an address like http://server:5080.";
            return;
        }

        ServerAddress = address;
        _clientSettings.Current.ServerAddress = address;
        _clientSettings.Save();
        _connection.Reconnect(address);
        ClientMessage = "Connecting to " + _connection.ServerAddress;
    }

    private void Apply(ServerSettings settings)
    {
        PollingIntervalSeconds = settings.PollingIntervalSeconds;
        FullRefreshMinutes = settings.FullRefreshMinutes;
        ScanParallelism = settings.ScanParallelism;
        ScanTimeoutMs = settings.ScanTimeoutMs;
        ServerName = settings.ServerName;
        ListenUrl = settings.ListenUrl;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server settings saved")]
    private static partial void LogSaved(ILogger logger);
}
