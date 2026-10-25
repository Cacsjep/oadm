using CommunityToolkit.Mvvm.ComponentModel;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Settings;

/// <summary>
/// New or Edit device group: name and description (errors below the fields) above the shared device picker in its group
/// mode (<see cref="DevicePickerOptions.Group"/>: sites and tags on the left, the devices on the right, the live count).
/// Save sends the rules with the revision the editor started from; refusals of the rules show below the device list.
/// </summary>
public sealed partial class DeviceGroupEditorViewModel : ValidatingViewModel
{
    /// <summary>The longest name the server takes.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest description the server takes.</summary>
    public const int MaxDescriptionLength = 200;

    private readonly IOadmApi _api;
    private readonly DeviceGroupInfo? _group;
    private readonly HashSet<string> _otherNames;

    /// <param name="api">The server.</param>
    /// <param name="devices">Every device of the client's list.</param>
    /// <param name="sites">The sites of the server (bound sites only).</param>
    /// <param name="tags">The tag definitions of the server.</param>
    /// <param name="group">The group to edit; null for a new group.</param>
    /// <param name="otherNames">The names of the other groups (a name in use shows at once).</param>
    public DeviceGroupEditorViewModel(
        IOadmApi api,
        IEnumerable<IDeviceInfo> devices,
        IEnumerable<Site> sites,
        IEnumerable<DeviceTag> tags,
        DeviceGroupInfo? group,
        IEnumerable<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(tags);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _group = group;
        _otherNames = new HashSet<string>(otherNames ?? [], StringComparer.OrdinalIgnoreCase);
        Name = group?.Name ?? "";
        Description = group?.Description ?? "";
        var options = new DeviceGroupPickerOptions
        {
            Sites = [new DevicePickerSite(null, DeviceGroupWords.ThisServer),
                .. sites.Where(s => Guid.TryParse(s.Id, out _)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(s => new DevicePickerSite(Guid.Parse(s.Id), s.Name))],
            Tags = [.. tags.Where(t => t.Defined && t.Id.Length > 0).Select(t => new DevicePickerTag(t.Id, t.Name))],
        };
        Picker = new DevicePickerViewModel(devices, Choice(group?.Rules), options);
        Validation
            .Rule(nameof(Name), () => Name.Trim().Length == 0 ? "Enter a name."
                : Name.Trim().Length > MaxNameLength ? $"The name must have at most {MaxNameLength} characters."
                : _otherNames.Contains(Name.Trim()) ? $"A device group named {Name.Trim()} exists already."
                : null)
            .Rule(nameof(Description), () => Description.Trim().Length > MaxDescriptionLength
                ? $"The description must have at most {MaxDescriptionLength} characters." : null);
        Validation.Validate();
    }

    /// <summary>"New device group" / "Edit Building B".</summary>
    public string Title => _group is null ? "New device group" : $"Edit {_group.Name}";

    /// <summary>The group being edited; null for a new one.</summary>
    public string? GroupId => _group?.Id;

    /// <summary>The device list with sites and tags.</summary>
    public DevicePickerViewModel Picker { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    /// <summary>Why Save is disabled (its tooltip); null when it can save.</summary>
    public string? SaveBlockedReason => Validation.FirstErrorOf([nameof(Name), nameof(Description)]);

    /// <summary>Save can run.</summary>
    public bool CanSave => SaveBlockedReason is null && !IsSaving;

    /// <summary>A save is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; private set; }

    /// <summary>The group the server stored after Save.</summary>
    public DeviceGroupInfo? Saved { get; private set; }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Saves the group; true when the server stored it (the window closes). Name and description refusals show below
    /// their field, refusals of the rules or a change made meanwhile below the device list.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        Validation.ShowAll(nameof(Name), nameof(Description));
        if (SaveBlockedReason is not null || IsSaving)
        {
            return false;
        }

        Picker.ErrorText = null;
        IsSaving = true;
        try
        {
            Saved = await _api.SaveDeviceGroupAsync(Request(), CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (RpcException ex)
        {
            string detail = ex.Status.Detail;
            if (ex.StatusCode == StatusCode.AlreadyExists || detail.StartsWith("Enter a name", StringComparison.Ordinal)
                || detail.StartsWith("The name", StringComparison.Ordinal))
            {
                Validation.SetServerError(nameof(Name), detail);
            }
            else if (detail.StartsWith("The description", StringComparison.Ordinal))
            {
                Validation.SetServerError(nameof(Description), detail);
            }
            else
            {
                Picker.ErrorText = detail;
            }

            return false;
        }
        catch (Exception ex)
        {
            Picker.ErrorText = ex.Message; // e.g. the connection broke: below the device list, the window stays open
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>The Save request of the editor's state.</summary>
    public SaveDeviceGroupRequest Request()
    {
        DeviceGroupChoice choice = Picker.GroupChoice;
        var rules = new DeviceGroupRules { ThisServer = choice.ThisServer };
        rules.SiteIds.AddRange(choice.SiteIds.Select(id => id.ToString()));
        rules.TagIds.AddRange(choice.TagIds);
        rules.Serials.AddRange(choice.Serials);
        rules.ExceptSerials.AddRange(choice.ExceptSerials);
        return new SaveDeviceGroupRequest
        {
            Id = _group?.Id ?? "",
            Name = Name.Trim(),
            Description = Description.Trim(),
            Rules = rules,
            Revision = _group?.Revision ?? 0,
        };
    }

    private static DeviceGroupChoice Choice(DeviceGroupRules? rules) => rules is null
        ? DeviceGroupChoice.Empty
        : new DeviceGroupChoice(
            rules.ThisServer,
            [.. rules.SiteIds.Select(id => Guid.TryParse(id, out var site) ? site : Guid.Empty).Where(id => id != Guid.Empty)],
            [.. rules.TagIds],
            [.. rules.Serials],
            [.. rules.ExceptSerials]);
}
