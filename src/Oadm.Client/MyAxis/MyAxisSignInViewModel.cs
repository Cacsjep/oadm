using CommunityToolkit.Mvvm.ComponentModel;

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
/// </summary>
public sealed partial class MyAxisSignInViewModel : ObservableObject
{
    /// <summary>Reads and sign-in tries per finished page.</summary>
    public const int Attempts = 6;

    /// <summary>The pause between two tries.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly IOadmApi _api;
    private readonly Action<MyAxisStateReply> _accepted;
    private readonly Func<EmbeddedBrowserState> _browser;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

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

    public static string CheckingText => MyAxisTexts.Checking;

    /// <summary>The page the browser opens on.</summary>
    public static Uri StartAddress => MyAxisPages.StartAddress;

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    /// <summary>Why the last sign-in did not work (above the login page); null: nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;

    /// <summary>"Sign-in is not available here." (and what is missing); null while the browser can sign in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotAvailable), nameof(ShowsBrowser))]
    public partial string? NotAvailableText { get; private set; }

    public bool IsNotAvailable => NotAvailableText is not null;

    /// <summary>Whether the window shows the browser.</summary>
    public bool ShowsBrowser => !IsNotAvailable;

    /// <summary>The server kept the session: the window closes.</summary>
    public bool IsAccepted { get; private set; }

    /// <summary>The window closes (accepted).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The browser opens this page (the login page again after a refusal).</summary>
    public event EventHandler<Uri>? NavigateRequested;

    /// <summary>Checks the embedded browser when the window opens.</summary>
    public void Start()
    {
        var state = _browser();
        if (!state.IsAvailable)
        {
            SetNotAvailable(state.Problem);
        }
    }

    /// <summary>This browser gives no way to its cookies (or could not be created).</summary>
    public void SetNotAvailable(string? problem = null) =>
        NotAvailableText = string.IsNullOrWhiteSpace(problem) ? MyAxisTexts.NotAvailable : MyAxisTexts.NotAvailable + " " + problem;

    /// <summary>
    /// A page finished loading. On a page after the login: reads the cookies, sends the session and closes once the
    /// server kept it; a refusal tries again, then says so and opens the login page again.
    /// </summary>
    public async Task PageLoadedAsync(Uri? page, ICookieSource cookies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        if (IsChecking || IsAccepted || IsNotAvailable || !MyAxisPages.ReadsCookies(page))
        {
            return;
        }

        IsChecking = true;
        ErrorText = null;
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
                    NavigateRequested?.Invoke(this, StartAddress);
                    return;
                }
            }

            MyAxisLog.NotAccepted(_logger, Attempts);
            ErrorText = MyAxisTexts.NotAccepted;
            NavigateRequested?.Invoke(this, StartAddress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the window closed
        }
        finally
        {
            IsChecking = false;
        }
    }
}
