using CommunityToolkit.Mvvm.ComponentModel;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.Shell;

/// <summary>A row of the "Your role" list: a group header or one permission.</summary>
public sealed record MyRoleRow(string Title, bool IsGroup, string? Description = null, bool FullDeviceControl = false)
{
    public bool IsPermission => !IsGroup;
}

/// <summary>
/// "Your role" (a click on the user at the bottom of the navigation rail): the role's name and what it allows, grouped
/// like the permission matrix of the Users page. Every user sees their own role (RoleService.Mine).
/// </summary>
public sealed partial class MyRoleViewModel(IOadmApi api) : ObservableObject
{
    /// <summary>"Operator".</summary>
    [ObservableProperty]
    public partial string RoleName { get; private set; } = "";

    /// <summary>"Operator may:" or, for the Administrator role, that it has everything.</summary>
    [ObservableProperty]
    public partial string Intro { get; private set; } = "";

    /// <summary>A failed load.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    public RangeObservableCollection<MyRoleRow> Rows { get; } = [];

    public async Task LoadAsync()
    {
        try
        {
            MyRole mine = await api.GetMyRoleAsync(CancellationToken.None).ConfigureAwait(true);
            RoleName = mine.RoleName;
            Intro = mine.AllPermissions
                ? $"{mine.RoleName} has every permission, also those that plugins add later."
                : mine.Permissions.Count == 0 ? $"{mine.RoleName} sees the devices and changes nothing." : $"{mine.RoleName} may:";
            var rows = new List<MyRoleRow>();
            string? group = null;
            if (!mine.AllPermissions)
            {
                foreach (PermissionInfo permission in mine.Permissions)
                {
                    if (permission.Group != group)
                    {
                        group = permission.Group;
                        rows.Add(new MyRoleRow(group, IsGroup: true));
                    }

                    rows.Add(new MyRoleRow(permission.Name, IsGroup: false, permission.Description, permission.FullDeviceControl));
                }
            }

            Rows.ReplaceAll(rows);
            ErrorText = null;
        }
        catch (RpcException ex)
        {
            ErrorText = ex.Status.Detail;
        }
    }
}
