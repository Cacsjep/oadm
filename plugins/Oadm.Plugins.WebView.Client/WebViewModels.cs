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
        new("Image Health Analytics", "/local/AXISImageHealthAnalytics/index.html"),
        new("Object Analytics", "/local/objectanalytics/index.html#/"),
        new("License Plate Verifier", "/local/fflprapp/setup.html#/home"),
        new(CustomName, string.Empty),
    ];
}

/// <summary>A row of the device tree: a tag group header or a device under it.</summary>
public abstract partial class WebTreeRow : ObservableObject
{
    public abstract bool IsGroup { get; }
}

/// <summary>A tag ("No tag" for devices without one); collapsing hides its devices.</summary>
public sealed partial class WebTreeGroup(string key, string name) : WebTreeRow
{
    public override bool IsGroup => true;

    /// <summary>The tag name, empty for "No tag".</summary>
    public string Key { get; } = key;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>A device under one tag group; a device with several tags has one row per tag.</summary>
public sealed partial class WebTreeDevice(IDeviceInfo device, string groupKey) : WebTreeRow
{
    public override bool IsGroup => false;

    public IDeviceInfo Device { get; } = device;

    public string GroupKey { get; } = groupKey;

    public string Address => Device.Address;

    /// <summary>For the shared device icon: the category icon, colored and explained by the status.</summary>
    public DeviceCategory Category => Device.Category;

    public DeviceStatus Status => Device.Status;

    /// <summary>The tab of this device is the active one.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>The device has an open tab.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }
}

/// <summary>
/// One browser tab: one device through its relay. Changing the device (Next, Previous) opens a new relay; the address
/// the browser shows is <see cref="Address"/>. A refused device shows <see cref="Error"/> instead of the page.
/// </summary>
public sealed partial class WebTab : ObservableObject, IDisposable
{
    private readonly WebViewPageViewModel _owner;
    private IDeviceWebSession? _session;
    private int _generation;

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

    [RelayCommand]
    private void Close() => _owner.CloseTab(this);

    [RelayCommand]
    private void Activate() => _owner.ActiveTab = this;

    /// <summary>Opens <see cref="Path"/> on the device; a new relay when the device changed or none runs.</summary>
    internal async Task LoadAsync(IDeviceInfo device, CancellationToken ct)
    {
        var generation = ++_generation;
        if (_session is not null && _session.DeviceId == device.Id && Device.Id == device.Id)
        {
            Error = null;
            Address = new Uri(_session.Origin, Path);
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
    public const string NotAllowed = "Your role does not allow opening devices.";

    public const string NoTabs = "Click a device on the left to open its web interface.";

    public const string NoTag = "No tag";

    public const string NotAvailable = "This OADM client cannot open web interfaces. Update the client.";

    public const string CustomPathHint = "A path on the device, for example /local/myapp/index.html";

    public const string CustomPathInvalid = "Start with / and stay on the device.";

    public static string CannotOpen(string reason) => "The web interface could not be opened: " + reason;
}
