using System.ComponentModel;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Help;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.MyAxis;

/// <summary>
/// The logged-in user's MyAxis sign-in as the client knows it (docs/spec/myaxis.md): read with <c>MyAxisService.Get</c>
/// on every connect and before the title bar button acts, changed by the sign-in window and Sign out. The title bar button
/// (<c>TitleMyAxis</c>) binds to it; task dialogs get it as <see cref="ITaskDialogContext.MyAxis"/>. Lives on the UI
/// thread: <see cref="Changed"/> and the properties change there.
/// </summary>
public sealed partial class MyAxisAccount : ObservableObject, IMyAxisSignIn
{
    private readonly IOadmApi _api;
    private readonly IUiDispatcher? _ui;
    private readonly ILogger _logger;
    private readonly Func<EmbeddedBrowserState> _browser;
    private readonly TimeProvider _time;
    private readonly Func<Window, MyAxisSignInViewModel, Task> _showWindow;

    /// <summary>Counts the state changes: a Get that started before a newer change does not overwrite it.</summary>
    private int _version;

    /// <param name="browser">The embedded-browser check (<see cref="EmbeddedBrowser.Check"/>); tests pass their own.</param>
    /// <param name="showWindow">Shows the sign-in window over the owner until it closes; tests pass their own.</param>
    public MyAxisAccount(IOadmApi api, ILogger<MyAxisAccount> logger, ServerConnection? connection = null, UserSession? session = null,
        IUiDispatcher? ui = null, Func<EmbeddedBrowserState>? browser = null, TimeProvider? time = null,
        Func<Window, MyAxisSignInViewModel, Task>? showWindow = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _logger = logger;
        _ui = ui;
        _browser = browser ?? EmbeddedBrowser.Check;
        _time = time ?? TimeProvider.System;
        _showWindow = showWindow ?? ShowWindowAsync;
        if (connection is not null)
        {
            connection.Connected += (_, _) => _ = RefreshAsync(CancellationToken.None);
        }

        if (session is not null)
        {
            session.PropertyChanged += OnSessionChanged;
        }
    }

    /// <summary>Whether the server has the MyAxis sign-in (an older one answers UNIMPLEMENTED): the button shows only then.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonTip), nameof(HasWarning), nameof(ButtonIcon))]
    public partial bool IsSignedIn { get; private set; }

    /// <summary>axis.com refused the kept session: the button says to sign in again, with a warning dot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonTip), nameof(HasWarning))]
    public partial bool IsExpired { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SinceText))]
    public partial DateTimeOffset? SignedInAt { get; private set; }

    /// <summary>The small panel below the button while signed in.</summary>
    [ObservableProperty]
    public partial bool IsFlyoutOpen { get; set; }

    /// <summary>Sign out runs.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>The title bar icon: a cloud with a check mark while signed in.</summary>
    public string ButtonIcon => IsSignedIn ? "cloudCheck" : "cloud";

    /// <summary>The button's tooltip.</summary>
    public string ButtonTip => IsSignedIn ? MyAxisTexts.SignedIn : IsExpired ? MyAxisTexts.SignInAgain : MyAxisTexts.SignIn;

    /// <summary>The warning dot on the button.</summary>
    public bool HasWarning => IsExpired && !IsSignedIn;

    /// <summary>"Signed in to MyAxis since 2 Nov 2026".</summary>
    public string SinceText => SignedInAt is { } since ? MyAxisTexts.SignedInSince(since) : MyAxisTexts.SignedIn;

    /// <summary>Raised on the UI thread when the state (signed in, expired, since) changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The button asks for the sign-in window; the main window opens it over itself (<see cref="SignInAsync"/>).</summary>
    public event EventHandler? SignInRequested;

    /// <summary>Reads the state from the server; errors leave it as it is (logged without details of the session).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var version = _version;
        try
        {
            var reply = await _api.GetMyAxisAsync(ct).ConfigureAwait(true);
            OnUi(() =>
            {
                IsAvailable = true;
                if (version == _version)
                {
                    Apply(reply);
                }
            });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            OnUi(() =>
            {
                IsAvailable = false;
                IsFlyoutOpen = false;
                Apply(null);
            });
        }
        catch (RpcException ex)
        {
            MyAxisLog.ReadFailed(_logger, ex.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The title bar button: signed in, the panel with Sign out; else the sign-in window. Reads the state first, so an
    /// expired sign-in shows the window.
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        if (IsSignedIn)
        {
            IsFlyoutOpen = true;
        }
        else
        {
            SignInRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        IsBusy = true;
        try
        {
            var reply = await _api.SignOutMyAxisAsync(CancellationToken.None).ConfigureAwait(true);
            OnUi(() => Apply(reply));
        }
        catch (RpcException ex)
        {
            MyAxisLog.SignOutFailed(_logger, ex.StatusCode);
        }
        finally
        {
            IsBusy = false;
            IsFlyoutOpen = false;
        }
    }

    /// <summary>
    /// Opens the sign-in window over <paramref name="owner"/>; true once the user is signed in (also when they already
    /// were), false when they closed it or sign-in is not available here.
    /// </summary>
    public async Task<bool> SignInAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (IsSignedIn)
        {
            return true;
        }

        var signIn = CreateSignIn();
        signIn.Start();
        await _showWindow(owner, signIn).ConfigureAwait(true);
        return signIn.IsAccepted && IsSignedIn;
    }

    /// <summary>A new sign-in window view model; its accepted answer becomes this state.</summary>
    internal MyAxisSignInViewModel CreateSignIn() => new(_api, reply => OnUi(() => Apply(reply)), _browser, _time, _logger);

    /// <summary>The server's answer becomes the state; null: signed out, not expired (logout, an older server).</summary>
    internal void Apply(MyAxisStateReply? reply)
    {
        var signedIn = reply?.SignedIn == true;
        var expired = !signedIn && reply?.Expired == true;
        DateTimeOffset? since = signedIn && reply!.SignedInAt is { } at ? at.ToDateTimeOffset() : null;
        _version++;
        if (signedIn == IsSignedIn && expired == IsExpired && since == SignedInAt)
        {
            return;
        }

        IsSignedIn = signedIn;
        IsExpired = expired;
        SignedInAt = since;
        if (!signedIn)
        {
            IsFlyoutOpen = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserSession.UserName) && sender is UserSession { IsLoggedIn: false })
        {
            OnUi(() => Apply(null)); // logout: the next user reads their own on connect
        }
    }

    private void OnUi(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(action);
        }
    }

    private static Task ShowWindowAsync(Window owner, MyAxisSignInViewModel signIn) =>
        new MyAxisSignInWindow { DataContext = signIn }.ShowDialog(owner);
}
