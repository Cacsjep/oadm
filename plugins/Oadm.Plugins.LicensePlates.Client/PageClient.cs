using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>The plugin's backend calls with the contract's JSON (<see cref="LicensePlatesJson.Options"/>).</summary>
public sealed class PageClient(ICorePluginClientContext ctx)
{
    public ICorePluginClientContext Context { get; } = ctx ?? throw new ArgumentNullException(nameof(ctx));

    public async Task<T> CallAsync<T>(string method, object? request, CancellationToken ct = default)
    {
        var json = request is null ? null : PluginJson.Serialize(request, LicensePlatesJson.Options);
        return PluginJson.Parse<T>(await Context.InvokeAsync(method, json, ct).ConfigureAwait(true), LicensePlatesJson.Options);
    }

    public async Task<PluginDownload> DownloadAsync(string method, object? request, CancellationToken ct = default) =>
        await Context.OpenDownloadAsync(method, request is null ? null : PluginJson.Serialize(request, LicensePlatesJson.Options), ct).ConfigureAwait(true);

    public static string Serialize<T>(T value) => PluginJson.Serialize(value, LicensePlatesJson.Options);

    public static T Parse<T>(string? json) => PluginJson.Parse<T>(json, LicensePlatesJson.Options);

    /// <summary>"oadm.license-plates:log".</summary>
    public bool Has(string permission) => Context.Has(PermissionKeys.Of(LicensePlatesInfo.PluginId, permission));

    /// <summary>The camera's name as the page shows it: the host name, else the address.</summary>
    public static string NameOf(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.HostName) ? device.Address : device.HostName!;
    }
}

/// <summary>
/// The dialogs and pickers of the page, shown by the view over the main window (tests replace them). Every dialog view model
/// raises its own close request; the methods return true when the user confirmed.
/// </summary>
public interface IPageDialogs
{
    Task<bool> EditPlateAsync(PlateEditorViewModel editor);

    Task<bool> ImportAsync(ImportViewModel import);

    Task<bool> TagsAsync(TagsDialogViewModel tags);

    Task<bool> SetupAsync(SetupViewModel setup);

    Task<bool> CopySettingsAsync(CopySettingsViewModel copy);

    Task<bool> SettingsAsync(SettingsViewModel settings);

    /// <summary>The device picker for cameras; the chosen ids, null when cancelled.</summary>
    Task<IReadOnlyList<Guid>?> PickCamerasAsync(IReadOnlyList<IDeviceInfo> devices, IReadOnlyList<Guid> selected, string heading);

    /// <summary>The page's camera choice (All, tags, chosen cameras); true when it changed.</summary>
    Task<bool> PickCameraChoiceAsync(Sdk.Client.Controls.PageDeviceChoice choice);

    /// <summary>The local CSV file to import; null when cancelled.</summary>
    Task<string?> OpenCsvAsync();

    /// <summary>A local file to write <paramref name="suggestedName"/> to; null when cancelled.</summary>
    Task<Stream?> SaveFileAsync(string title, string suggestedName);
}
