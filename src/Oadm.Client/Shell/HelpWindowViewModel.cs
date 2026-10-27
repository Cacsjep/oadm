using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;

using Oadm.Client.Dialogs;
using Oadm.Client.Help;

namespace Oadm.Client.Shell;

/// <summary>
/// The Help window: the offline user docs (<see cref="HelpDocs"/>) in the embedded browser, served by a
/// <see cref="DocsServer"/> from <see cref="Start"/> until <see cref="Dispose"/> (the window closed). Without docs or
/// without an embedded browser the window shows <see cref="Message"/> instead. Links that leave the docs open in the
/// system browser.
/// </summary>
public sealed partial class HelpWindowViewModel : ObservableObject, IDisposable
{
    private readonly string? _docsRoot;
    private readonly Func<EmbeddedBrowserState> _browser;
    private readonly IUrlLauncher? _launcher;
    private readonly ILogger _logger;
    private DocsServer? _server;
    private bool _disposed;

    /// <param name="docsRoot">The docs folder (<see cref="HelpDocs.FindRoot"/>), null when this build has none.</param>
    /// <param name="browser">Checks the embedded browser of this computer (<see cref="EmbeddedBrowser.Check"/>).</param>
    public HelpWindowViewModel(string? docsRoot, Func<EmbeddedBrowserState> browser, IUrlLauncher? launcher, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(logger);
        _docsRoot = docsRoot;
        _browser = browser;
        _launcher = launcher;
        _logger = logger;
    }

    public static string WindowTitle => HelpDocs.WindowTitle;

    /// <summary>The docs folder is missing: "Help is not part of this build."</summary>
    [ObservableProperty] public partial string? MissingDocsText { get; private set; }

    /// <summary>The embedded browser is missing: what to install (the Web View page's text).</summary>
    [ObservableProperty] public partial string? MissingBrowserText { get; private set; }

    /// <summary>The docs server's start page while the window is open; null before <see cref="Start"/> or without docs.</summary>
    [ObservableProperty] public partial Uri? StartAddress { get; private set; }

    public bool HasMissingDocs => MissingDocsText is not null;

    public bool HasMissingBrowser => MissingBrowserText is not null;

    /// <summary>The docs server of the open window (tests).</summary>
    internal DocsServer? Server => _server;

    public bool IsDisposed => _disposed;

    /// <summary>The window opens: checks docs and browser, then starts the docs server.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_server is not null || HasMissingDocs || HasMissingBrowser)
        {
            return;
        }

        if (_docsRoot is null || !Directory.Exists(_docsRoot))
        {
            MissingDocsText = HelpDocs.NotInBuild;
            OnPropertyChanged(nameof(HasMissingDocs));
            return;
        }

        var browser = _browser();
        if (!browser.IsAvailable)
        {
            MissingBrowserText = browser.Problem ?? Oadm.Sdk.Client.EmbeddedBrowserTexts.ForThisSystem;
            OnPropertyChanged(nameof(HasMissingBrowser));
            return;
        }

        _server = new DocsServer(_docsRoot, _logger);
        StartAddress = _server.Origin;
    }

    /// <summary>
    /// True when the browser should not open <paramref name="address"/> itself but hand it to the system browser: web
    /// addresses of another host than the docs server, and mail links. Other schemes (about:, data:, blob:) stay.
    /// </summary>
    public bool LeavesDocs(Uri? address)
    {
        if (address is null || !address.IsAbsoluteUri)
        {
            return false;
        }

        if (address.Scheme == Uri.UriSchemeMailto)
        {
            return true;
        }

        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return StartAddress is not { } docs
            || address.Scheme != docs.Scheme
            || !string.Equals(address.Authority, docs.Authority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A link that leaves the docs: the system browser opens it.</summary>
    public async Task OpenOutsideAsync(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_launcher is not null)
        {
            await _launcher.OpenAsync(address).ConfigureAwait(true);
        }
    }

    /// <summary>The window closed: the docs server stops and frees its port.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _server?.Dispose();
        _server = null;
        StartAddress = null;
    }
}
