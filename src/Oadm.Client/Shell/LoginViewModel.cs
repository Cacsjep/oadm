using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.Security;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;
using Oadm.Theme.Controls;

namespace Oadm.Client.Shell;

/// <summary>
/// The login window: server (remembered, recent servers), user name, password, "Remember me". On the first connection
/// to a server it asks to confirm the server certificate's fingerprint (trust on first use); a changed certificate is
/// refused until Forget server. While the server has no user it creates the first administrator (with the setup code
/// when this computer is not the server). Errors appear below their fields; a problem that stops the login (server not reachable,
/// certificate changed, login locked) is a box with a heading below the server or password field.
/// </summary>
public sealed partial class LoginViewModel : ValidatingViewModel
{
    public const string CertificateChangedTitle = "The server certificate changed";

    public const string CertificateChangedMessage =
        "If the server was reinstalled or got a new certificate, forget it and compare the new fingerprint.";

    public const string UnreachableTitle = "Cannot reach the server";

    public const string NotTrustedTitle = "The server is not trusted";

    public const string NotTrustedMessage = "Log in again to trust it.";

    private readonly IOadmApi _api;
    private readonly IServerTrust _trust;
    private readonly IClientSettingsStore _settings;
    private readonly UserSession _session;
    private readonly IDialogService _dialogs;
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(IOadmApi api, IServerTrust trust, IClientSettingsStore settings, UserSession session, IDialogService dialogs, AppOptions options, ILogger<LoginViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        _api = api;
        _trust = trust;
        _settings = settings;
        _session = session;
        _dialogs = dialogs;
        _logger = logger;
        ServerAddress = options.ServerAddress ?? settings.Current.ServerAddress;
        foreach (string server in settings.Current.RecentServers)
        {
            RecentServers.Add(server);
        }

        Validation
            .Rule(nameof(ServerAddress), () => ServerAddressError(ServerAddress))
            .Rule(nameof(UserName), () => UserName.Trim().Length == 0 ? "Enter your user name."
                : UserName.Trim().Length > 64 ? "The user name has at most 64 characters." : null)
            .Rule(nameof(Password), () => Password.Length == 0 ? "Enter your password." : null)
            .Rule(nameof(ConfirmPassword), () => IsFirstAdminMode && ConfirmPassword != Password ? "The passwords do not match." : null)
            .Rule(nameof(SetupCode), () => IsFirstAdminMode && SetupCodeRequired && SetupCode.Trim().Length == 0
                ? "Enter the setup code from the server." : null);
        Validation.Validate();
    }


    /// <summary>Raised on the UI thread after a successful login; the app opens the main window.</summary>
    private bool _initialized;
    private bool _connectionFailed;

    /// <summary>How long "Connecting to ..." shows at least (each Connect again visibly tries again; tests set zero).</summary>
    internal TimeSpan ConnectFeedback { get; init; } = TimeSpan.FromMilliseconds(500);

    public event EventHandler? LoggedIn;

    public ObservableCollection<string> RecentServers { get; } = [];

    [ObservableProperty]
    public partial string ServerAddress { get; set; } = "";

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    public partial string SetupCode { get; set; } = "";

    [ObservableProperty]
    public partial bool RememberMe { get; set; }

    /// <summary>No user exists on the server: the form creates the first administrator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(SubmitText))]
    public partial bool IsFirstAdminMode { get; private set; }

    /// <summary>This computer is not the server: creating the first administrator needs the setup code.</summary>
    [ObservableProperty]
    public partial bool SetupCodeRequired { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(TryAgainCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Connecting to localhost:5080" while busy.</summary>
    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    /// <summary>The server cannot be used (unreachable, changed certificate): the text of the box below the server field.</summary>
    [ObservableProperty]
    public partial string? ServerProblem { get; private set; }

    /// <summary>The heading of that box ("Cannot reach the server").</summary>
    [ObservableProperty]
    public partial string? ServerProblemTitle { get; private set; }

    /// <summary>Error (red) or warning (amber: the certificate changed or was not trusted).</summary>
    [ObservableProperty]
    public partial CalloutKind ServerProblemKind { get; private set; }

    /// <summary>Too many failed logins: the server's text in a box below the password.</summary>
    [ObservableProperty]
    public partial string? LockedMessage { get; private set; }

    /// <summary>The server certificate changed: Forget server is offered.</summary>
    [ObservableProperty]
    public partial bool CanForgetServer { get; private set; }

    /// <summary>Why the login window is shown again ("Your session has ended. Log in again.").</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    public string Title => IsFirstAdminMode ? "Create the first administrator" : "Log in to OADM";

    public string SubmitText => IsFirstAdminMode ? "Create administrator" : "Log in";


    private string[] Fields => IsFirstAdminMode
        ? [nameof(ServerAddress), nameof(UserName), nameof(Password), nameof(ConfirmPassword), nameof(SetupCode)]
        : [nameof(ServerAddress), nameof(UserName), nameof(Password)];

    public static string? ServerAddressError(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "Enter the server address, e.g. localhost:5080.";
        }

        try
        {
            OadmChannel.NormalizeAddress(address);
            return null;
        }
        catch (FormatException)
        {
            return "Enter a host name or IP address, optionally with a port, e.g. localhost:5080.";
        }
    }

    protected override void OnValidationChanged() => SubmitCommand.NotifyCanExecuteChanged();

    partial void OnServerAddressChanged(string value) => ClearServerProblem();

    private void SetServerProblem(string title, string text, CalloutKind kind = CalloutKind.Error)
    {
        ServerProblemTitle = title;
        ServerProblemKind = kind;
        ServerProblem = text;
    }

    private void ClearServerProblem()
    {
        ServerProblem = null;
        ServerProblemTitle = null;
        CanForgetServer = false;
    }

    /// <summary>
    /// When the window opens: a remembered session of the server is resumed without asking; otherwise the server is
    /// asked whether it still needs its first administrator (the form switches at once).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return; // already done behind the start splash
        }

        _initialized = true;
        await ConnectToServerAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Connect again (a refresh icon right of the server field): the server could not be
    /// reached, e.g. it was still starting, or another address was typed. Repeats what opening the window does (resume a remembered login, else ask the server whether it needs its first administrator)
    /// without restarting the client.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTryAgain))]
    private Task TryAgainAsync()
    {
        ClearServerProblem();
        return ConnectToServerAsync();
    }

    private bool CanTryAgain() => !IsBusy;

    private async Task ConnectToServerAsync()
    {
        if (ServerAddressError(ServerAddress) is not null)
        {
            return;
        }

        string address = ServerAddress.Trim();
        if (_settings.Current.RememberedLogins.TryGetValue(KeyOf(address), out RememberedLogin? remembered) && Notice is null)
        {
            UserName = remembered.UserName;
            Validation.Reset();
            if (await ResumeAsync(address, remembered).ConfigureAwait(true))
            {
                return;
            }
        }

        await RunBusyAsync("Connecting to " + address, async () =>
        {
            if (await ConnectAsync(address).ConfigureAwait(true) is { } status)
            {
                ApplyStatus(status);
            }
        }, ConnectFeedback).ConfigureAwait(true);
    }

    private bool CanSubmit() => !IsBusy && Validation.IsValidFor(Fields);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        Validation.ShowAll(Fields);
        if (!Validation.IsValidFor(Fields))
        {
            return;
        }

        string address = ServerAddress.Trim();
        await RunBusyAsync((IsFirstAdminMode ? "Creating the administrator on " : "Logging in to ") + address, async () =>
        {
            if (await ConnectAsync(address).ConfigureAwait(true) is not { } status)
            {
                return;
            }

            if (status.NeedsFirstAdmin != IsFirstAdminMode)
            {
                // The server's state decides the form: the first administrator still has to be created, or exists already.
                ApplyStatus(status);
                if (!status.NeedsFirstAdmin)
                {
                    Validation.SetServerError(nameof(UserName), "The first administrator exists already. Log in.");
                }

                return;
            }

            SetupCodeRequired = status.SetupCodeRequired;
            try
            {
                LoginReply reply = IsFirstAdminMode
                    ? await _api.CreateFirstAdminAsync(UserName.Trim(), Password, SetupCode.Trim(), RememberMe, CancellationToken.None).ConfigureAwait(true)
                    : await _api.LoginAsync(UserName.Trim(), Password, RememberMe, CancellationToken.None).ConfigureAwait(true);
                Complete(address, reply.Token, reply.User);
            }
            catch (RpcException ex)
            {
                ShowLoginError(ex);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ForgetServerAsync()
    {
        string address = ServerAddress.Trim();
        PointApiAt(address);
        _trust.Forget();
        ClearServerProblem();
        await RunBusyAsync("Connecting to " + address, async () =>
        {
            if (await ConnectAsync(address).ConfigureAwait(true) is { } status)
            {
                ApplyStatus(status);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>Picks a recent server.</summary>
    [RelayCommand]
    private void UseServer(string? server)
    {
        if (!string.IsNullOrWhiteSpace(server))
        {
            ServerAddress = server;
        }
    }

    private async Task<bool> ResumeAsync(string address, RememberedLogin remembered)
    {
        bool resumed = false;
        await RunBusyAsync("Logging in to " + address, async () =>
        {
            PointApiAt(address);
            _api.AccessToken = remembered.Token;
            try
            {
                if (await ConnectAsync(address).ConfigureAwait(true) is null)
                {
                    return;
                }

                UserInfo user = await _api.GetCurrentUserAsync(CancellationToken.None).ConfigureAwait(true);
                Complete(address, remembered.Token, user);
                resumed = true;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                // Expired or revoked: log in again; the remembered token is gone.
                _settings.Current.RememberedLogins.Remove(KeyOf(address));
                _settings.Save();
                _api.AccessToken = null;
            }
            catch (RpcException ex)
            {
                Unreachable(address);
                LogConnectFailed(_logger, address, ex.Status.Detail);
            }
        }).ConfigureAwait(true);
        return resumed;
    }

    /// <summary>
    /// AuthService.Status with the TLS trust handling: a new certificate asks for the fingerprint (and tries again once
    /// trusted), a changed one is refused. Null when the server cannot be used; the reason is below the server field.
    /// </summary>
    private async Task<AuthStatus?> ConnectAsync(string address)
    {
        PointApiAt(address);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            _trust.ResetCheck();
            try
            {
                return await _api.GetAuthStatusAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is RpcException or HttpRequestException)
            {
                LogConnectFailed(_logger, address, ex.Message);
                switch (_trust.LastCheck)
                {
                    case PinCheck.Unknown when attempt == 0 && _trust.PresentedFingerprint is { } fingerprint:
                        if (!await _dialogs.ConfirmServerTrustAsync(address, fingerprint).ConfigureAwait(true))
                        {
                            SetServerProblem(NotTrustedTitle, NotTrustedMessage, CalloutKind.Warning);
                            return null;
                        }

                        _trust.Trust(fingerprint);
                        continue;
                    case PinCheck.Changed:
                        SetServerProblem(CertificateChangedTitle, CertificateChangedMessage, CalloutKind.Warning);
                        CanForgetServer = true;
                        return null;
                    default:
                        Unreachable(address);
                        return null;
                }
            }
        }

        Unreachable(address);
        return null;
    }

    /// <summary>"AA:BB:...:PP\nQQ:..." (16 pairs per line) so the window never breaks inside a pair.</summary>
    internal static string InTwoLines(string fingerprint)
    {
        string[] pairs = fingerprint.Split(':');
        return pairs.Length <= 16 ? fingerprint : string.Join(':', pairs.Take(16)) + ":\n" + string.Join(':', pairs.Skip(16));
    }

    private void ApplyStatus(AuthStatus status)
    {
        IsFirstAdminMode = status.NeedsFirstAdmin;
        SetupCodeRequired = status.SetupCodeRequired;
        Validation.Validate();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private void ShowLoginError(RpcException ex)
    {
        string message = ex.Status.Detail;
        switch (ex.StatusCode)
        {
            case StatusCode.Unauthenticated:
                Validation.SetServerError(nameof(Password), message);
                break;
            case StatusCode.ResourceExhausted:
                LockedMessage = message;
                break;
            case StatusCode.PermissionDenied:
                Validation.SetServerError(IsFirstAdminMode && SetupCodeRequired ? nameof(SetupCode) : nameof(UserName), message);
                break;
            case StatusCode.InvalidArgument:
                Validation.SetServerError(message.Contains("password", StringComparison.OrdinalIgnoreCase) ? nameof(Password) : nameof(UserName), message);
                break;
            case StatusCode.FailedPrecondition:
                IsFirstAdminMode = false;
                Validation.SetServerError(nameof(UserName), message);
                break;
            default:
                Unreachable(ServerAddress.Trim());
                LogConnectFailed(_logger, ServerAddress, message);
                break;
        }
    }

    private void Complete(string address, string token, UserInfo user)
    {
        _api.AccessToken = token;
        ClientSettings current = _settings.Current;
        current.ServerAddress = address;
        current.RecentServers.RemoveAll(s => string.Equals(s, address, StringComparison.OrdinalIgnoreCase));
        current.RecentServers.Insert(0, address);
        if (current.RecentServers.Count > ClientSettings.MaxRecentServers)
        {
            current.RecentServers.RemoveRange(ClientSettings.MaxRecentServers, current.RecentServers.Count - ClientSettings.MaxRecentServers);
        }

        string key = KeyOf(address);
        if (RememberMe)
        {
            current.RememberedLogins[key] = new RememberedLogin { UserName = user.UserName, Token = token };
        }
        else if (current.RememberedLogins.TryGetValue(key, out RememberedLogin? old) && old.Token != token)
        {
            current.RememberedLogins.Remove(key);
        }

        _settings.Save();
        Password = "";
        ConfirmPassword = "";
        SetupCode = "";
        Notice = null;
        Validation.Reset();
        _session.SignIn(user, _api.ServerAddress);
        LogLoggedIn(_logger, user.UserName, _api.ServerAddress);
        LoggedIn?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Points the API at the server. After the server could not be reached a fresh connection is opened: the old one
    /// waits out the gRPC reconnect backoff (up to minutes) and fails every call at once in that time, so a click on
    /// Connect again or Log in would seem to do nothing although the server is up now.
    /// </summary>
    private void PointApiAt(string address)
    {
        _api.SetServerAddress(address);
        if (_connectionFailed)
        {
            _connectionFailed = false;
            _api.Reconnect();
        }
    }

    private void Unreachable(string address)
    {
        _connectionFailed = true;
        SetServerProblem(UnreachableTitle, UnreachableText(address));
    }

    private async Task RunBusyAsync(string text, Func<Task> work, TimeSpan minimum = default)
    {
        IsBusy = true;
        BusyText = text;
        ClearServerProblem();
        LockedMessage = null;
        try
        {
            await Task.WhenAll(work(), minimum > TimeSpan.Zero ? Task.Delay(minimum) : Task.CompletedTask).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    internal static string UnreachableText(string address) =>
        $"Check the address and that the OADM server is running on {address}.";

    /// <summary>Key of a server in the client settings (pins, remembered logins): "host:port".</summary>
    internal static string KeyOf(string address) => ServerCertificatePinning.KeyOf(OadmChannel.NormalizeAddress(address));

    [LoggerMessage(Level = LogLevel.Information, Message = "Logged in as {UserName} on {Server}")]
    private static partial void LogLoggedIn(ILogger logger, string userName, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connecting to {Server} failed: {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, string server, string reason);
}
