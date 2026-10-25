using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Settings;

/// <summary>One device group of the Users page: name, rules summary, device count, users.</summary>
public sealed class DeviceGroupRowViewModel(DeviceGroupInfo info)
{
    public DeviceGroupInfo Info { get; } = info;

    public string Id => Info.Id;

    public string Name => Info.Name;

    /// <summary>The description as the name's tooltip; null without one.</summary>
    public string? DescriptionTip => Info.Description.Length > 0 ? Info.Description : null;

    /// <summary>"Site Building B, tag PTZ, 3 devices, except 1".</summary>
    public string Summary => Info.Summary;

    public int DeviceCount => Info.DeviceCount;

    /// <summary>"412".</summary>
    public string DeviceCountText => Info.DeviceCount.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"tech2, guard"; empty without users.</summary>
    public string UsersText => string.Join(", ", Info.UserNames);
}

/// <summary>
/// Card "Device groups" of the Users page (administrator): every group with its rules summary, device count and users;
/// New device group and Edit open the group editor (the shared device picker with sites and tags,
/// <see cref="DeviceGroupEditorViewModel"/>), Delete asks first and names the users who lose their devices. A group decides
/// which devices its users see and work with (docs/spec/device-scope.md).
/// </summary>
public sealed partial class DeviceGroupsViewModel : ObservableObject
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly UserSession _session;
    private readonly Func<IReadOnlyList<IDeviceInfo>> _devices;

    /// <param name="devices">Every device of the client's list (the editor's device list).</param>
    public DeviceGroupsViewModel(IOadmApi api, IDialogService dialogs, UserSession session, Func<IReadOnlyList<IDeviceInfo>>? devices = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _devices = devices ?? (() => []);
    }

    /// <summary>Raised after the groups were loaded or changed (the user form's Devices choices follow).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a group was saved or deleted: its users show the new name or no devices.</summary>
    public event EventHandler? UsersAffected;

    public RangeObservableCollection<DeviceGroupRowViewModel> Groups { get; } = [];

    /// <summary>Every device of the server ("412 of 4,812 devices").</summary>
    [ObservableProperty]
    public partial int TotalDevices { get; private set; }

    /// <summary>No group yet: the list says what to do first.</summary>
    public bool IsEmpty => Groups.Count == 0 && !IsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The editor is being prepared (sites, tags, the group): New and Edit wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewCommand), nameof(EditCommand))]
    public partial bool IsOpening { get; private set; }

    /// <summary>A failed load or a refused delete, directly below the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public async Task LoadAsync()
    {
        if (!_session.IsAdmin)
        {
            Groups.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        IsLoading = true;
        try
        {
            DeviceGroupList list = await _api.ListDeviceGroupsAsync(CancellationToken.None).ConfigureAwait(true);
            Groups.ReplaceAll([.. list.Groups.Select(g => new DeviceGroupRowViewModel(g))]);
            TotalDevices = list.TotalDevices;
            ErrorText = null;
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanOpen() => !IsOpening;

    /// <summary>"New device group": the editor without rules.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task NewAsync() => OpenEditorAsync(null);

    /// <summary>The row's Edit: the editor with the group as the server has it now.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task EditAsync(DeviceGroupRowViewModel? row) => row is null ? Task.CompletedTask : OpenEditorAsync(row);

    /// <summary>Prepares the editor (the group, sites and tags as the server has them now) and shows it.</summary>
    public async Task<DeviceGroupEditorViewModel?> PrepareEditorAsync(DeviceGroupRowViewModel? row)
    {
        DeviceGroupInfo? group = row is null ? null : await _api.GetDeviceGroupAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        SiteList sites = await _api.ListSitesAsync(CancellationToken.None).ConfigureAwait(true);
        IReadOnlyList<DeviceTag> tags = await _api.ListTagsAsync(CancellationToken.None).ConfigureAwait(true);
        IReadOnlyList<IDeviceInfo> devices = _devices();
        List<string> otherNames = [.. Groups.Where(g => g.Id != group?.Id).Select(g => g.Name)];

        // 30,000 devices: the device list is built off the UI thread.
        return await Task.Run(() => new DeviceGroupEditorViewModel(_api, devices, sites.Sites, tags, group, otherNames)).ConfigureAwait(true);
    }

    private async Task OpenEditorAsync(DeviceGroupRowViewModel? row)
    {
        ErrorText = null;
        IsOpening = true;
        DeviceGroupEditorViewModel? editor;
        try
        {
            editor = await PrepareEditorAsync(row).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
            return;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message; // never a crash of the page: the card shows it
            return;
        }
        finally
        {
            IsOpening = false;
        }

        if (editor is not null && await _dialogs.ShowDeviceGroupEditorAsync(editor).ConfigureAwait(true))
        {
            await LoadAsync().ConfigureAwait(true);
            UsersAffected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The row's ⋯ menu (<c>ui:RowActions</c>): Edit, then Delete in red.</summary>
    public IReadOnlyList<IssuedCertificateAction> RowActionsOf(DeviceGroupRowViewModel row) =>
    [
        new("Edit", EditCommand, row),
        IssuedCertificateAction.Separator,
        new("Delete", DeleteCommand, row, IsDanger: true),
    ];

    /// <summary>The row's Delete (red): asks first, naming the users who lose their devices.</summary>
    [RelayCommand]
    private async Task DeleteAsync(DeviceGroupRowViewModel? row)
    {
        if (row is null || !await _dialogs.ConfirmAsync("Delete device group", DeleteQuestion(row), "Delete").ConfigureAwait(true))
        {
            return;
        }

        ErrorText = null;
        try
        {
            await _api.DeleteDeviceGroupAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
            return;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return;
        }

        await LoadAsync().ConfigureAwait(true);
        UsersAffected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The question of Delete: who loses their devices.</summary>
    public static string DeleteQuestion(DeviceGroupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var users = row.Info.UserNames;
        string who = users.Count switch
        {
            0 => "No user has this group.",
            1 => $"{users[0]} has no devices until you give them another group.",
            _ => $"{string.Join(", ", users)} have no devices until you give them another group.",
        };
        return $"{who} Delete {row.Name}?";
    }
}
