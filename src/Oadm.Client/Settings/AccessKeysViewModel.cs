using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.Settings;

/// <summary>One lifetime of a new access key: "90 days".</summary>
public sealed record AccessKeyLifetime(int Days)
{
    public override string ToString() => string.Create(CultureInfo.CurrentCulture, $"{Days} days");
}

/// <summary>One access key of the list: name, role, devices, created, expires, last used.</summary>
public sealed class AccessKeyRow(AccessKey key)
{
    public AccessKey Key { get; } = key ?? throw new ArgumentNullException(nameof(key));

    public string Id => Key.Id;

    public string Name => Key.Name;

    public string RoleName => Key.RoleName;

    public string DevicesText => Key.AllDevices ? DeviceGroupWords.AllDevices : Key.DeviceGroupName;

    public string Created => AccessKeyWords.Date(Key.Created);

    public string Expires => AccessKeyWords.Date(Key.Expires);

    public string LastUsed => Key.LastUsed is null ? AccessKeyWords.NeverUsed : AccessKeyWords.Time(Key.LastUsed);

    /// <summary>The sort keys of the dates.</summary>
    public string CreatedSortKey => SortKey(Key.Created);

    public string ExpiresSortKey => SortKey(Key.Expires);

    public string LastUsedSortKey => SortKey(Key.LastUsed);

    private static string SortKey(Google.Protobuf.WellKnownTypes.Timestamp? time) =>
        time?.ToDateTime().ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>The texts of access keys in the client (one phrase per concept).</summary>
public static class AccessKeyWords
{
    public const string Title = "Access keys";
    public const string Create = "Create access key";
    public const string CopyNow = "Copy it now. OADM cannot show it again.";
    public const string NeverUsed = "Never";
    public const string EnterName = "Enter a name.";
    public const string NameTooLong = "Use at most 64 characters.";
    public const string NameCharacters = "Use only letters, digits, spaces, - _ and . in the name.";
    public const string ChooseRole = "Choose a role.";
    public const string Empty = "No access keys yet. Create one for each agent.";

    /// <summary>The refusal of a name below the Name field (the server's rule), null when it is fine.</summary>
    public static string? NameError(string? name)
    {
        var clean = name?.Trim() ?? string.Empty;
        return clean.Length == 0 ? EnterName
            : clean.Length > 64 ? NameTooLong
            : clean.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) ? NameCharacters
            : null;
    }

    public static string Date(Google.Protobuf.WellKnownTypes.Timestamp? time) =>
        time is null ? string.Empty : time.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public static string Time(Google.Protobuf.WellKnownTypes.Timestamp? time) =>
        time is null ? string.Empty : time.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>
/// The section Access keys of the Settings card AI, section MCP (administrators, docs/spec/mcp.md "Access keys"): every key with
/// its role, devices, dates and Delete (asked first, ends the key at once), and Create access key, which opens the form
/// (<see cref="AccessKeyFormViewModel"/>) that shows the new key once.
/// </summary>
public sealed partial class AccessKeysViewModel(IOadmApi api, IDialogService dialogs, IClipboardService? clipboard = null) : ObservableObject
{
    /// <summary>Close of the dialog Access keys.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Every key by name (at most 100).</summary>
    public RangeObservableCollection<AccessKeyRow> Keys { get; } = [];

    public bool HasKeys => Keys.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Loaded without a key: the list says what to do first.</summary>
    public bool IsEmpty => !IsLoading && Keys.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsOpening { get; private set; }

    [ObservableProperty] public partial string? Message { get; private set; }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            Show(await api.ListAccessKeysAsync(CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (Exception ex) when (ex is RpcException or NotSupportedException)
        {
            Message = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanCreate => !IsOpening;

    /// <summary>Create access key: the roles and device groups of now, then the form; the list after a key was made.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync() => OpenFormAsync(desktopSetup: false);

    internal async Task OpenFormAsync(bool desktopSetup)
    {
        IsOpening = true;
        AccessKeyFormViewModel form;
        try
        {
            var roles = await api.GetRoleMatrixAsync(CancellationToken.None).ConfigureAwait(true);
            var groups = await api.ListDeviceGroupsAsync(CancellationToken.None).ConfigureAwait(true);
            form = new AccessKeyFormViewModel(api, clipboard,
                [.. roles.Roles.Select(r => new RoleOption(r.Id, r.Name, r.Kind))],
                [DeviceAccessOption.All, .. groups.Groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => new DeviceAccessOption(g.Id, g.Name))], desktopSetup);
        }
        catch (RpcException ex)
        {
            Message = ex.Status.Detail;
            return;
        }
        finally
        {
            IsOpening = false;
        }

        await dialogs.ShowAccessKeyFormAsync(form).ConfigureAwait(true);
        if (form.HasNewKey)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(AccessKeyRow? row)
    {
        if (row is null || !await dialogs.ConfirmAsync("Delete access key", $"Agents that use {row.Name} can no longer connect.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            Show(await api.DeleteAccessKeyAsync(row.Id, CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (RpcException ex)
        {
            Message = ex.Status.Detail;
        }
    }

    private void Show(IReadOnlyList<AccessKey> keys)
    {
        Keys.ReplaceAll(keys.Select(k => new AccessKeyRow(k))); // one Reset
        OnPropertyChanged(nameof(HasKeys));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
