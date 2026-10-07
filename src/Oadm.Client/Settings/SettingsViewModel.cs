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

namespace Oadm.Client.Settings;

/// <summary>One credential list entry: user name and when it was added (never a password).</summary>
public sealed record CredentialItemViewModel(string Id, string UserName, string AddedText);

/// <summary>Server settings (SettingsService), the credential list, plus the client-side server address.</summary>
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

    /// <summary>Discovery.ZeroConfSeconds: a zero-conf scan of the add page ends after this many seconds (5..300).</summary>
    [ObservableProperty] public partial decimal? ZeroConfSeconds { get; set; } = 30;
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
        var settings = new ServerSettings
        {
            PollingIntervalSeconds = (int)(PollingIntervalSeconds ?? 60),
            FullRefreshMinutes = (int)(FullRefreshMinutes ?? 10),
            ScanParallelism = (int)(ScanParallelism ?? 32),
            ScanTimeoutMs = (int)(ScanTimeoutMs ?? 1500),
            ZeroConfSeconds = (int)(ZeroConfSeconds ?? 30),
            ServerName = ServerName.Trim(),
            ListenUrl = ListenUrl.Trim(),
            UseHostName = UseHostName,
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
    private async Task AddCredentialAsync()
    {
        string user = NewCredentialUserName.Trim();
        if (user.Length == 0 || NewCredentialPassword.Length == 0)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Enter a user name and a password.";
            return;
        }

        try
        {
            await _api.AddCredentialAsync(user, NewCredentialPassword, CancellationToken.None).ConfigureAwait(true);
            NewCredentialPassword = "";
            CredentialMessageIsError = false;
            CredentialMessage = $"Added. OADM tries {user} on every device it finds.";
            await LoadCredentialsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Adding failed: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
    }

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
        ZeroConfSeconds = settings.ZeroConfSeconds > 0 ? settings.ZeroConfSeconds : 30; // 0 = older server without the setting
        ServerName = settings.ServerName;
        ListenUrl = settings.ListenUrl;
        UseHostName = settings.UseHostName;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server settings saved")]
    private static partial void LogSaved(ILogger logger);
}
