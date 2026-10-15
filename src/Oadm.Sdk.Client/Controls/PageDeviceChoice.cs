using System.Text.Json;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The device choice of a page that reads many devices (the purple "Selected devices" button): All devices, tags and chosen
/// devices, the text of the button, the devices it means now, and the last choice kept on this computer. The page starts
/// with the Devices page selection when there is one, else with its last choice, else with nothing ("Choose devices"),
/// and reads only the devices of the choice (docs/30kspec.md, section 5).
/// </summary>
public sealed partial class PageDeviceChoice : ObservableObject
{
    /// <summary>Key of the remembered choice in the plugin's settings on this computer.</summary>
    public const string SettingKey = "deviceChoice";

    /// <summary>A choice with more single devices than this is not remembered (tags and All devices always are).</summary>
    public const int MaxRememberedDevices = 1_000;

    private readonly ICorePluginClientContext _ctx;
    private readonly Func<IDeviceInfo, bool> _fits;
    private readonly string _noun;

    /// <summary>
    /// Creates the choice of a page.
    /// </summary>
    /// <param name="ctx">The page's context: devices, the Devices page selection, the settings on this computer.</param>
    /// <param name="noun">"devices" or "cameras": the button says "Choose cameras", "Selected cameras: Tag Building A".</param>
    /// <param name="fits">Which devices the page can use (e.g. video devices); others are left out. Null: every device.</param>
    public PageDeviceChoice(ICorePluginClientContext ctx, string noun = "devices", Func<IDeviceInfo, bool>? fits = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(noun);
        _ctx = ctx;
        _noun = noun;
        _fits = fits ?? (_ => true);
        var selected = ctx.SelectedDevices.Where(_fits).Select(d => d.Id).ToList();
        Choice = selected.Count > 0 ? DeviceChoice.Devices(selected) : Load() ?? DeviceChoice.Devices([]);
    }

    /// <summary>The current choice.</summary>
    public DeviceChoice Choice { get; private set; }

    /// <summary>Nothing chosen yet: the page reads nothing.</summary>
    public bool IsEmpty => Choice.IsEmpty;

    /// <summary>The text of the button: "Choose devices", "Selected devices: All", "Selected devices: Tag Building A and 3 devices".</summary>
    public string ButtonText => Choice.Kind == DeviceChoiceKind.All
        ? $"Selected {_noun}: All"
        : Choice.IsEmpty
            ? $"Choose {_noun}"
            : $"Selected {_noun}: " + DeviceSelectorTexts.Brief(Choice.DeviceIds.Count, Choice.Tags, SingleAddress());

    /// <summary>The devices the page can use (what the picker lists).</summary>
    public IReadOnlyList<IDeviceInfo> Candidates() => [.. _ctx.Devices.Where(_fits)];

    /// <summary>The devices of the choice now: all usable devices, or the chosen ones and those with a chosen tag. O(devices).</summary>
    public IReadOnlyList<IDeviceInfo> Resolve()
    {
        if (Choice.Kind == DeviceChoiceKind.All)
        {
            return Candidates();
        }

        var ids = Choice.DeviceIds.ToHashSet();
        var tags = new HashSet<string>(Choice.Tags, StringComparer.OrdinalIgnoreCase);
        return [.. _ctx.Devices.Where(d => _fits(d) && (ids.Contains(d.Id) || (tags.Count > 0 && d.Tags.Any(tags.Contains))))];
    }

    /// <summary>Sets the choice, keeps it on this computer and tells the page.</summary>
    public void Set(DeviceChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Choice = choice;
        Save();
        OnPropertyChanged(nameof(Choice));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ButtonText));
    }

    /// <summary>Opens the shared device picker (All devices, tags, chosen devices); true when a choice was made.</summary>
    public async Task<bool> PickAsync(Window owner, string heading)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var choice = await DevicePickerWindow.ShowChoiceAsync(owner, Candidates(), Choice, new DevicePickerOptions { AllowAll = true, AllowTags = true }, heading).ConfigureAwait(true);
        if (choice is null)
        {
            return false;
        }

        Set(choice);
        return true;
    }

    private string? SingleAddress() =>
        Choice.DeviceIds.Count == 1 && _ctx.Devices.FirstOrDefault(d => d.Id == Choice.DeviceIds[0]) is { } device ? device.Address : null;

    private DeviceChoice? Load()
    {
        try
        {
            return _ctx.GetLocalSetting(SettingKey) is { Length: > 0 } json && JsonSerializer.Deserialize<Stored>(json) is { } stored
                ? new DeviceChoice(stored.All ? DeviceChoiceKind.All : DeviceChoiceKind.Chosen, stored.Devices ?? [], stored.Tags ?? [])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save()
    {
        var all = Choice.Kind == DeviceChoiceKind.All;
        var devices = Choice.DeviceIds.Count <= MaxRememberedDevices ? Choice.DeviceIds : [];
        _ctx.SetLocalSetting(SettingKey, JsonSerializer.Serialize(new Stored(all, all ? [] : devices, all ? [] : Choice.Tags)));
    }

    private sealed record Stored(bool All, IReadOnlyList<Guid>? Devices, IReadOnlyList<string>? Tags);
}
