using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Grpc.Core;

using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>
/// One credential list entry: user name, when it was added and, only after the eye button asked the server
/// (<see cref="IOadmApi.RevealCredentialAsync"/>), its password. Masked again (and forgotten) on the second click.
/// </summary>
public sealed partial class CredentialItemViewModel(string id, string userName, string addedText) : ObservableObject
{
    /// <summary>Shown while the password is hidden; always the same length so it says nothing about the password.</summary>
    public const string MaskedText = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    public string Id { get; } = id;
    public string UserName { get; } = userName;
    public string AddedText { get; } = addedText;

    /// <summary>The revealed password; null while hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRevealed), nameof(PasswordText), nameof(RevealTooltip))]
    public partial string? Password { get; private set; }

    public bool IsRevealed => Password is not null;

    public string PasswordText => Password ?? MaskedText;

    public string RevealTooltip => IsRevealed ? "Hide password" : "Show password";

    internal void Reveal(string password) => Password = password;

    internal void Hide() => Password = null;
}

/// <summary>
/// Server settings (SettingsService) and the credential list. Every field reports its error below itself
/// (<see cref="ValidatingViewModel"/>); Save and Add credential stay disabled while their fields have errors.
/// The server address of this client is set with <c>--server</c> or the client settings file.
/// </summary>
public sealed partial class SettingsViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(IOadmApi api, ServerConnection connection, IClipboardService clipboard, ILogger<SettingsViewModel> logger, AboutViewModel? about = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clipboard);
        About = about ?? new AboutViewModel(api, connection);
        _api = api;
        _clipboard = clipboard;
        _logger = logger;
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
            .Rule(nameof(NewCredentialPassword), () => NewCredentialPassword.Length == 0 ? "Enter the password." : null);
        Validation.Validate();
        Validation.Reset();
    }

    /// <summary>Card "About and licenses" (versions, license texts).</summary>
    public AboutViewModel About { get; }

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

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(AddCredentialBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
        AddCredentialCommand.NotifyCanExecuteChanged();
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

    /// <summary>Eye button: loads the stored password from the server and shows it; a second click masks it again.</summary>
    [RelayCommand]
    private async Task ToggleRevealCredentialAsync(CredentialItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsRevealed)
        {
            item.Hide();
            return;
        }

        try
        {
            item.Reveal(await _api.RevealCredentialAsync(item.Id, CancellationToken.None).ConfigureAwait(true));
            CredentialMessage = null;
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Showing the password failed: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
        }
    }

    /// <summary>Copy button: copies the stored password to the clipboard (loaded from the server unless shown already).</summary>
    [RelayCommand]
    private async Task CopyCredentialPasswordAsync(CredentialItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            string password = item.Password ?? await _api.RevealCredentialAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
            bool copied = await _clipboard.SetTextAsync(password).ConfigureAwait(true);
            CredentialMessageIsError = !copied;
            CredentialMessage = copied ? $"Password of {item.UserName} copied to the clipboard." : "The clipboard is not available.";
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Copying the password failed: " + (ex is RpcException rpc ? rpc.Status.Detail : ex.Message);
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
