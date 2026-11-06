using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.WebView.Client;

/// <summary>A page of the device the select opens: the main web interface, an app's page, or a path typed in.</summary>
public sealed record WebPagePreset(string Name, string Path)
{
    public const string CustomName = "Custom";

    public bool IsCustom => Name == CustomName;

    public override string ToString() => Name;

    /// <summary>The presets in the order of the select; Custom last.</summary>
    public static IReadOnlyList<WebPagePreset> All { get; } =
    [
        new("Web interface", "/"),
        new("Apps", "/camera/index.html#/apps"),
        new("Overlays", "/camera/index.html#/video/overlays"),
        new("Privacy masks", "/camera/index.html#/video/privacymasks"),
        new("Recordings", "/camera/index.html#/recordings"),
        new("Security", "/camera/index.html#/system/security"),
        new("Image Health Analytics", "/local/AXISImageHealthAnalytics/index.html"),
        new("Object Analytics", "/local/objectanalytics/index.html#/"),
        new("License Plate Verifier", "/local/fflprapp/setup.html#/home"),
        new(CustomName, string.Empty),
    ];
}

/// <summary>Where an address the page of a tab asks for opens.</summary>
internal enum WebLinkTarget
{
    /// <summary>A page of the tab's device (its relay): the tab.</summary>
    Tab,

    /// <summary>No web address (about:, data:, blob:): the browser's own.</summary>
    Browser,

    /// <summary>Another website, a mail link: the system browser.</summary>
    Outside,

    /// <summary>Another address on this computer (another tab's relay): not opened.</summary>
    Nowhere,
}

/// <summary>
/// One browser tab: one device through its relay. Changing the device (Next, Previous) opens a new relay; the address
/// the browser shows is <see cref="Address"/>. A refused device shows <see cref="Error"/> instead of the page.
/// </summary>
public sealed partial class WebTab : ObservableObject, IDisposable
{
    /// <summary>A tab hands the system browser at most one address in this time (<see cref="Open"/>).</summary>
    internal static readonly TimeSpan OutsidePause = TimeSpan.FromSeconds(5);

    private readonly WebViewPageViewModel _owner;
    private IDeviceWebSession? _session;
    private int _generation;
    private long? _lastOutside;

    internal WebTab(WebViewPageViewModel owner, IDeviceInfo device, string groupKey, WebPagePreset preset, string path)
    {
        _owner = owner;
        Device = device;
        GroupKey = groupKey;
        Preset = preset;
        Path = path;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ToolTip))]
    public partial IDeviceInfo Device { get; private set; }

    /// <summary>The tree group the tab was opened from: Next and Previous continue there.</summary>
    public string GroupKey { get; internal set; }

    public string Title => Device.Address;

    public string ToolTip => string.IsNullOrEmpty(Device.Model) ? Device.Address : $"{Device.Address} · {Device.Model}";

    /// <summary>The page the tab shows (the select follows the active tab).</summary>
    public WebPagePreset Preset { get; internal set; }

    /// <summary>The path on the device, "/" or a preset's or the custom path.</summary>
    public string Path { get; internal set; }

    /// <summary>What the browser opens; null while the relay starts or when the device was refused.</summary>
    [ObservableProperty]
    public partial Uri? Address { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The browser of the tab has a page to go back to (set by the view from the native browser).</summary>
    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    /// <summary>
    /// The address that opens <paramref name="page"/> again with a fresh session cookie, when the page is on this tab's
    /// relay; null otherwise.
    /// </summary>
    internal Uri? StartAddressFor(Uri page) =>
        _session is { } session && page.IsAbsoluteUri && Uri.Compare(page, session.Origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? session.StartAddress(page.PathAndQuery + page.Fragment)
            : null;

    /// <summary>
    /// Where <paramref name="address"/> opens when the tab's page asks for it (a link, a script, a redirect): the tab shows
    /// only its device. Everything else leaves the tab, so no other website appears under the device's tab title.
    /// </summary>
    internal WebLinkTarget TargetOf(Uri? address)
    {
        if (!EmbeddedBrowserLinks.Leaves(address, _session?.Origin))
        {
            return address is { IsAbsoluteUri: true } && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
                ? WebLinkTarget.Tab
                : WebLinkTarget.Browser;
        }

        return address!.IsLoopback ? WebLinkTarget.Nowhere : WebLinkTarget.Outside;
    }

    /// <summary>
    /// <see cref="TargetOf"/> for an address the page asks for now. The system browser opens without a click (a script, a
    /// redirect), so a tab hands it one address in <see cref="OutsidePause"/>; the others in that time are dropped
    /// (<see cref="WebLinkTarget.Nowhere"/>): a page that asks again and again cannot open window after window.
    /// </summary>
    internal WebLinkTarget Open(Uri? address)
    {
        var target = TargetOf(address);
        if (target != WebLinkTarget.Outside)
        {
            return target;
        }

        var now = _owner.Time.GetTimestamp();
        if (_lastOutside is { } last && _owner.Time.GetElapsedTime(last, now) < OutsidePause)
        {
            return WebLinkTarget.Nowhere;
        }

        _lastOutside = now;
        return WebLinkTarget.Outside;
    }

    [RelayCommand]
    private void Close() => _owner.CloseTab(this);

    [RelayCommand]
    private void Activate() => _owner.ActiveTab = this;

    /// <summary>The browser opens this page now (Reload, another page on a running relay); also the same page again.</summary>
    public event EventHandler<Uri>? NavigateRequested;

    /// <summary>The device's record changed (address, model): the tab's title follows.</summary>
    internal void Update(IDeviceInfo device)
    {
        if (device.Id == Device.Id)
        {
            Device = device;
        }
    }

    /// <summary>Opens <see cref="Path"/> on the device; a new relay when the device changed or none runs.</summary>
    internal async Task LoadAsync(IDeviceInfo device, CancellationToken ct)
    {
        if (_session is null && IsLoading && Device.Id == device.Id)
        {
            return; // the relay starts: it opens the current Path when it is ready (one audit entry, one relay)
        }

        var generation = ++_generation;
        if (_session is not null && _session.DeviceId == device.Id && Device.Id == device.Id)
        {
            Error = null;
            NavigateRequested?.Invoke(this, new Uri(_session.Origin, Path));
            return;
        }

        Device = device;
        _session?.Dispose();
        _session = null;
        Address = null;
        Error = null;
        if (_owner.Access is not { } access)
        {
            Error = WebViewTexts.NotAvailable;
            return;
        }

        IsLoading = true;
        try
        {
            var session = await access.OpenAsync(device.Id, ct).ConfigureAwait(true);
            if (generation != _generation || _owner.IsClosed(this))
            {
                session.Dispose();
                return;
            }

            _session = session;
            Address = session.StartAddress(Path);
        }
        catch (DeviceWebRefusedException ex)
        {
            if (generation == _generation)
            {
                Error = ex.Message;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation == _generation)
            {
                Error = WebViewTexts.CannotOpen(ex.Message);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    public void Dispose()
    {
        _generation++;
        _session?.Dispose();
        _session = null;
    }
}

/// <summary>The page's texts (one phrase per concept).</summary>
public static class WebViewTexts
{
    public const string NoDevices = "No devices yet. Add devices on the Devices page.";

    /// <summary>The role lacks "Open devices".</summary>
    public const string NotAllowed = Oadm.Sdk.Client.DeviceTexts.OpenNotAllowed;

    public const string NoTabs = "Click a device on the left to open its web interface.";

    public const string NotAvailable = "This OADM client cannot open web interfaces. Update the client.";

    public const string CustomPathHint = "A path on the device, for example /local/myapp/index.html";

    public const string CustomPathInvalid = "Start with / and stay on the device.";

    public static string CannotOpen(string reason) => "The web interface could not be opened: " + reason;
}
