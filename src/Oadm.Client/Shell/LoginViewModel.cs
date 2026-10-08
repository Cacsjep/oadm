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

namespace Oadm.Client.Shell;

/// <summary>
/// The login window: server (remembered, recent servers), user name, password, "Remember me". On the first connection
/// to a server it asks to confirm the server certificate's fingerprint (trust on first use); a changed certificate is
/// refused until Forget server. While the server has no user it creates the first administrator (with the setup code
/// when this computer is not the server). Errors appear below their fields.
/// </summary>
public sealed partial class LoginViewModel : ValidatingViewModel
{
    public const string CertificateChangedMessage =
        "The server certificate changed. If the server was reinstalled or got a new certificate, use Forget server and confirm the new fingerprint.";

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
    [NotifyPropertyChangedFor(nameof(Title), nameof(SubmitText), nameof(Intro))]
    public partial bool IsFirstAdminMode { get; private set; }

    /// <summary>This computer is not the server: creating the first administrator needs the setup code.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Intro))]
    public partial bool SetupCodeRequired { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Connecting to localhost:5080" while busy.</summary>
    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    /// <summary>The server cannot be used (unreachable, changed certificate): shown below the server field.</summary>
    [ObservableProperty]
    public partial string? ServerProblem { get; private set; }

    /// <summary>The server certificate changed: Forget server is offered.</summary>
    [ObservableProperty]
    public partial bool CanForgetServer { get; private set; }

    /// <summary>Why the login window is shown again ("Your session has ended. Log in again.").</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    public string Title => IsFirstAdminMode ? "Create the first administrator" : "Log in to OADM";

    public string SubmitText => IsFirstAdminMode ? "Create administrator" : "Log in";

    public string? Intro => !IsFirstAdminMode ? null
        : SetupCodeRequired
            ? "This server has no users yet. Create the first administrator. As this computer is not the server, the setup code is needed too."
            : "This server has no users yet. Create the first administrator; it can add more users on the Users page.";

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

    partial void OnServerAddressChanged(string value)
    {
        ServerProblem = null;
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
        }).ConfigureAwait(true);
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
        CanForgetServer = false;
        ServerProblem = null;
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
                ServerProblem = UnreachableText(address);
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
                        if (!await ConfirmFingerprintAsync(address, fingerprint).ConfigureAwait(true))
                        {
                            ServerProblem = "The server certificate was not trusted. Log in to trust it after comparing the fingerprint.";
                            return null;
                        }

                        _trust.Trust(fingerprint);
                        continue;
                    case PinCheck.Changed:
                        ServerProblem = CertificateChangedMessage;
                        CanForgetServer = true;
                        return null;
                    default:
                        ServerProblem = UnreachableText(address);
                        return null;
                }
            }
        }

        ServerProblem = UnreachableText(address);
        return null;
    }

    /// <summary>The fingerprint confirmation (shared confirmation window).</summary>
    private Task<bool> ConfirmFingerprintAsync(string address, string fingerprint) =>
        _dialogs.ConfirmAsync(
            "Trust this server?",
            $"This is the first connection to {address}. Compare the fingerprint below with the one the OADM server writes to "
            + "its log at startup (\"Server TLS certificate fingerprint\"). Trust the server only if they are the same.\n\n"
            + "SHA-256 fingerprint:\n" + InTwoLines(fingerprint),
            "Trust server");

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
            case StatusCode.ResourceExhausted:
                Validation.SetServerError(nameof(Password), message);
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
                ServerProblem = UnreachableText(ServerAddress.Trim());
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

    private void PointApiAt(string address)
    {
        _api.SetServerAddress(address);
    }

    private async Task RunBusyAsync(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        ServerProblem = null;
        CanForgetServer = false;
        try
        {
            await work().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private static string UnreachableText(string address) =>
        $"Cannot reach the OADM server at {address}. Check the address and that the server is running.";

    /// <summary>Key of a server in the client settings (pins, remembered logins): "host:port".</summary>
    internal static string KeyOf(string address) => ServerCertificatePinning.KeyOf(OadmChannel.NormalizeAddress(address));

    [LoggerMessage(Level = LogLevel.Information, Message = "Logged in as {UserName} on {Server}")]
    private static partial void LogLoggedIn(ILogger logger, string userName, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connecting to {Server} failed: {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, string server, string reason);
}
