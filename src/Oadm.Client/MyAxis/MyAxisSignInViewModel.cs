using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Help;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.MyAxis;

/// <summary>
/// The MyAxis sign-in window (docs/spec/myaxis.md): Axis's login page in the embedded browser. After each page that
/// finished loading on www.axis.com outside a login path it reads the browser's cookies and sends the session cookie to
/// the server; the server checks it with axis.com. The cookie can come a moment before the session is complete, so a
/// refusal is tried again (<see cref="Attempts"/>, <see cref="RetryDelay"/> apart) before the window says so and shows
/// the login page again. The session is a secret: never logged, never shown.
/// <para>
/// The browser is a native window that nothing can be drawn over: while the overlay shows (<see cref="OverlayText"/>:
/// "Loading MyAxis" until a page finished loading, "Signing in" from the moment the browser heads for a page after the
/// login, then "Checking the sign-in") the browser is hidden (<see cref="IsBrowserVisible"/>), so the user never sees a
/// blank page or the download listing the login returns to. A page that fails or does not load within
/// <see cref="LoadTimeout"/> shows <see cref="LoadErrorText"/> with Try again.
/// </para>
/// </summary>
public sealed partial class MyAxisSignInViewModel : ObservableObject
{
    /// <summary>Reads and sign-in tries per finished page.</summary>
    public const int Attempts = 6;

    /// <summary>The pause between two tries.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>A page that takes longer counts as not reachable.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    private readonly IOadmApi _api;
    private readonly Action<MyAxisStateReply> _accepted;
    private readonly Func<EmbeddedBrowserState> _browser;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private bool _loading;
    private bool _signingIn;
    private bool _askingServer;
    private CancellationTokenSource? _loadTimer;

    public MyAxisSignInViewModel(IOadmApi api, Action<MyAxisStateReply> accepted, Func<EmbeddedBrowserState> browser, TimeProvider? time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _accepted = accepted;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public static string WindowTitle => MyAxisTexts.WindowTitle;

    /// <summary>The page the browser opens on.</summary>
    public static Uri StartAddress => MyAxisPages.StartAddress;

    /// <summary>Signing in or checking: a page after the login was reached.</summary>
    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    /// <summary>"Loading MyAxis", "Signing in" or "Checking the sign-in" over the hidden browser; null: no overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlay), nameof(IsBrowserVisible))]
    public partial string? OverlayText { get; private set; }

    public bool HasOverlay => OverlayText is not null;

    /// <summary>Why the last sign-in did not work (above the login page); null: nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;

    /// <summary>"axis.com could not be reached. ..." with Try again, in place of the browser; null: none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(IsBrowserVisible))]
    public partial string? LoadErrorText { get; private set; }

    public bool HasLoadError => LoadErrorText is not null;

    /// <summary>"Sign-in is not available here." (and what is missing); null while the browser can sign in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotAvailable), nameof(ShowsBrowser), nameof(IsBrowserVisible))]
    public partial string? NotAvailableText { get; private set; }

    public bool IsNotAvailable => NotAvailableText is not null;

    /// <summary>Whether the window has a browser at all.</summary>
    public bool ShowsBrowser => !IsNotAvailable;

    /// <summary>Whether the browser shows now: not under an overlay, not in place of an error.</summary>
    public bool IsBrowserVisible => ShowsBrowser && !HasOverlay && !HasLoadError;

    /// <summary>The server kept the session: the window closes.</summary>
    public bool IsAccepted { get; private set; }

    /// <summary>The window closes (accepted).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The browser opens this page (the login page again after a refusal, Try again).</summary>
    public event EventHandler<Uri>? NavigateRequested;

    /// <summary>Checks the embedded browser when the window opens; the start page loads under "Loading MyAxis".</summary>
    public void Start()
    {
        var state = _browser();
        if (!state.IsAvailable)
        {
            SetNotAvailable(state.Problem);
            return;
        }

        _loading = true;
        StartLoadTimer();
        UpdateOverlay();
    }

    /// <summary>This browser gives no way to its cookies (or could not be created).</summary>
    public void SetNotAvailable(string? problem = null)
    {
        StopLoadTimer();
        _loading = false;
        _signingIn = false;
        NotAvailableText = string.IsNullOrWhiteSpace(problem) ? MyAxisTexts.NotAvailable : MyAxisTexts.NotAvailable + " " + problem;
        UpdateOverlay();
    }

    /// <summary>
    /// The browser starts loading a page (before it shows anything): a page after the login is covered by "Signing in"
    /// at once, any other by "Loading MyAxis" until it finished loading.
    /// </summary>
    public void NavigationStarting(Uri? page)
    {
        // WebKitGTK (Avalonia.Controls.WebView 12.1.1) reports every frame here, also the about:blank iframes of Axis's
        // login pages, and reports a completed load only for the page itself: a frame must never cover the page.
        if (IsAccepted || IsNotAvailable || !IsWebPage(page))
        {
            return;
        }

        if (MyAxisPages.ReadsCookies(page))
        {
            _signingIn = true;
        }
        else if (!IsChecking)
        {
            _signingIn = false;
            _loading = true;
        }

        StartLoadTimer();
        UpdateOverlay();
    }

    /// <summary>
    /// A page finished loading (<paramref name="succeeded"/> false: it failed). On a page after the login: reads the
    /// cookies, sends the session and closes once the server kept it; a refusal tries again, then says so and opens the
    /// login page again. Another page that failed shows the load error.
    /// </summary>
    public async Task PageLoadedAsync(Uri? page, ICookieSource cookies, CancellationToken ct, bool succeeded = true)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        if (IsAccepted || IsNotAvailable)
        {
            return;
        }

        StopLoadTimer();
        if (succeeded)
        {
            LoadErrorText = null;
            _reachedAxis = true;
        }

        var reads = MyAxisPages.ReadsCookies(page);
        if (reads)
        {
            _signingIn = true; // before the loading overlay goes: the page after the login never shows
        }

        _loading = false;
        if (!reads)
        {
            if (!IsChecking)
            {
                _signingIn = false;
            }

            // Once axis.com answered, a "failed" page is its own answer (a wrong one-time code or password comes with an
            // HTTP error status): the user reads Axis's message. Only a failure before any answer means not reachable.
            if (!succeeded && !IsChecking && !_reachedAxis)
            {
                LoadErrorText = MyAxisTexts.NotReachable;
            }

            UpdateOverlay();
            return;
        }

        UpdateOverlay();
        if (IsChecking)
        {
            return; // one check at a time; it covers this page too
        }

        await CheckAsync(cookies, ct).ConfigureAwait(true);
    }

    /// <summary>Whether a page of axis.com loaded at least once in this window.</summary>
    private bool _reachedAxis;

    /// <summary>The load error's Try again: the login page once more.</summary>
    [RelayCommand]
    private void TryAgain()
    {
        LoadErrorText = null;
        _signingIn = false;
        _loading = true;
        StartLoadTimer();
        UpdateOverlay();
        NavigateRequested?.Invoke(this, StartAddress);
    }

    private async Task CheckAsync(ICookieSource cookies, CancellationToken ct)
    {
        IsChecking = true;
        ErrorText = null;
        var refused = false;
        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(true);
                }

                var read = await cookies.ReadAsync(MyAxisPages.CookieAddress, ct).ConfigureAwait(true);
                if (read is null)
                {
                    SetNotAvailable();
                    return;
                }

                if (MyAxisPages.SessionOf(read) is not { } session)
                {
                    continue; // the cookie can come a moment after the page
                }

                try
                {
                    _askingServer = true;
                    UpdateOverlay();
                    var reply = await _api.SignInMyAxisAsync(session, ct).ConfigureAwait(true);
                    IsAccepted = true;
                    _accepted(reply);
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
                {
                    // not accepted (yet): try again
                }
                catch (RpcException ex)
                {
                    MyAxisLog.SignInFailed(_logger, ex.StatusCode);
                    ErrorText = ServerErrors.Message(ex);
                    refused = true;
                    return;
                }
            }

            MyAxisLog.NotAccepted(_logger, Attempts);
            ErrorText = MyAxisTexts.NotAccepted;
            refused = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the window closed
        }
        finally
        {
            IsChecking = false;
            _askingServer = false;
            if (refused)
            {
                // The login page again, under "Loading MyAxis" until it is there.
                _signingIn = false;
                _loading = true;
                StartLoadTimer();
                NavigateRequested?.Invoke(this, StartAddress);
            }

            UpdateOverlay();
        }
    }

    private void UpdateOverlay()
    {
        if (IsAccepted)
        {
            return; // stays covered until the window is gone
        }

        OverlayText = IsNotAvailable || HasLoadError ? null
            : _askingServer ? MyAxisTexts.Checking
            : _signingIn || IsChecking ? MyAxisTexts.SigningIn
            : _loading ? MyAxisTexts.Loading
            : null;
    }

    /// <summary>A page that does not finish loading within <see cref="LoadTimeout"/> shows the load error.</summary>
    private void StartLoadTimer()
    {
        StopLoadTimer();
        var timer = new CancellationTokenSource();
        _loadTimer = timer;
        _ = WaitForLoadAsync(timer);
    }

    private async Task WaitForLoadAsync(CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(LoadTimeout, _time, timer.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_loadTimer, timer) || IsAccepted || IsNotAvailable || IsChecking)
        {
            return;
        }

        _loadTimer = null;
        timer.Dispose();
        _loading = false;
        _signingIn = false;

        // Once axis.com answered, a load that never reported back (a frame, a load the page cancelled itself; WebKitGTK
        // reports neither) is no connection problem: the browser shows what it has.
        if (!_reachedAxis)
        {
            LoadErrorText = MyAxisTexts.NotReachable;
        }

        UpdateOverlay();
    }

    /// <summary>An http(s) page; about:blank, data: and the like are frames, never a page of the login.</summary>
    private static bool IsWebPage(Uri? page) =>
        page is { IsAbsoluteUri: true } && (page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp);

    private void StopLoadTimer()
    {
        if (_loadTimer is { } timer)
        {
            _loadTimer = null;
            timer.Cancel();
            timer.Dispose();
        }
    }
}
