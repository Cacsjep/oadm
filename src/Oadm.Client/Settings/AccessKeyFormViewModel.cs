using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>
/// The dialog Create access key (docs/spec/mcp.md "Access keys"): Name (1 to 64 letters, digits, spaces, - _ and ., unique), Role,
/// Devices (All devices or a device group; an Administrator key always has all devices) and Expires (30, 90 or 365 days, 90 by
/// default). After Create the same dialog shows the key once with Copy; the server keeps only its SHA-256. Errors below their
/// fields; the server's refusals below the field they name (trailer <c>oadm-field</c>).
/// </summary>
public sealed partial class AccessKeyFormViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IClipboardService? _clipboard;

    public AccessKeyFormViewModel(IOadmApi api, IClipboardService? clipboard, IReadOnlyList<RoleOption> roles, IReadOnlyList<DeviceAccessOption> devices, bool desktopSetup = false)
    {
        _api = api;
        _clipboard = clipboard;
        Roles = roles;
        DeviceOptions = devices;
        DesktopSetup = desktopSetup;
        Validation
            .Rule(nameof(Name), () => AccessKeyWords.NameError(Name))
            .Rule(nameof(Role), () => Role is null ? AccessKeyWords.ChooseRole : null)
            .Rule(nameof(Devices), () => Role?.Kind != RoleKind.Administrator && Devices is null ? DeviceGroupWords.ChooseDevices : null);
        if (DesktopSetup)
        {
            Name = "Claude Desktop";
            Devices = null;
            Lifetime = Lifetimes[0];
        }
        Validation.Validate();
        Validation.Reset();
    }

    public bool DesktopSetup { get; }

    public string Title => DesktopSetup ? "Set up a desktop app" : "Create access key";

    public string SubmitText => DesktopSetup ? "Set up" : "Create";

    public string KeyInstructions => DesktopSetup
        ? "Install the OADM extension in Claude Desktop, then paste this key into its access key field."
        : AccessKeyWords.CopyNow;

    private static string DesktopExtensionPath => Path.Combine(AppContext.BaseDirectory, "OADM.mcpb");

    [RelayCommand]
    private async Task CopyExtensionPathAsync()
    {
        if (_clipboard is not null && await _clipboard.SetTextAsync(DesktopExtensionPath).ConfigureAwait(true))
            Message = "Extension path copied.";
    }

    public static IReadOnlyList<AccessKeyLifetime> Lifetimes { get; } = [new(30), new(90), new(365)];

    public IReadOnlyList<RoleOption> Roles { get; }

    /// <summary>All devices, then the device groups by name.</summary>
    public IReadOnlyList<DeviceAccessOption> DeviceOptions { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevicesEnabled), nameof(DevicesHint))]
    public partial RoleOption? Role { get; set; }

    [ObservableProperty] public partial DeviceAccessOption? Devices { get; set; } = DeviceAccessOption.All;

    [ObservableProperty] public partial AccessKeyLifetime Lifetime { get; set; } = Lifetimes[1];

    /// <summary>The Devices field can be changed (not for an Administrator key).</summary>
    public bool IsDevicesEnabled => Role?.Kind != RoleKind.Administrator;

    /// <summary>Below the Devices field of an Administrator key.</summary>
    public string? DevicesHint => Role?.Kind == RoleKind.Administrator ? DeviceGroupWords.AdministratorsHaveAll : null;

    /// <summary>The new key, shown once until the dialog closes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewKey), nameof(IsForm), nameof(CloseText))]
    public partial string? NewKey { get; private set; }

    public bool HasNewKey => NewKey is not null;

    /// <summary>The form shows until the key was made.</summary>
    public bool IsForm => !HasNewKey;

    /// <summary>Cancel before the key exists, Close afterwards.</summary>
    public string CloseText => HasNewKey ? "Close" : "Cancel";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string? Message { get; private set; }

    /// <summary>Why Create is disabled (tooltip).</summary>
    public string? CreateTip => Validation.FirstError;

    /// <summary>The dialog closes (Cancel or Close).</summary>
    public event EventHandler? CloseRequested;

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CreateTip));
        CreateCommand.NotifyCanExecuteChanged();
    }

    partial void OnNameChanged(string value) => Validation.SetServerError(nameof(Name), null);

    partial void OnRoleChanged(RoleOption? value)
    {
        Validation.SetServerError(nameof(Role), null);
        Validation.Validate();
    }

    partial void OnDevicesChanged(DeviceAccessOption? value)
    {
        Validation.SetServerError(nameof(Devices), null);
        Validation.Validate();
    }

    private bool CanCreate => !IsBusy && !HasNewKey && Validation.IsValid;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid || Role is not { } role)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var admin = role.Kind == RoleKind.Administrator;
            var devices = admin ? DeviceAccessOption.All : Devices!;
            var request = new CreateAccessKeyRequest
            {
                Name = Name.Trim(),
                Days = Lifetime.Days,
                RoleId = role.Id,
                AllDevices = devices.IsAll,
                DeviceGroupId = devices.GroupId ?? string.Empty,
            };
            var created = await _api.CreateAccessKeyAsync(request, CancellationToken.None).ConfigureAwait(true);
            Message = null;
            NewKey = created.Secret;
            CreateCommand.NotifyCanExecuteChanged();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            Validation.SetServerError(FieldOf(ex), ex.Status.Detail);
        }
        catch (RpcException ex)
        {
            Message = ex.Status.Detail;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyKeyAsync()
    {
        if (NewKey is { } key && _clipboard is not null && await _clipboard.SetTextAsync(key).ConfigureAwait(true))
        {
            Message = "Copied.";
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The field a refusal names (trailer <c>oadm-field</c>); the name without one.</summary>
    private static string FieldOf(RpcException ex) => ex.Trailers.GetValue("oadm-field") switch
    {
        "role" => nameof(Role),
        "devices" => nameof(Devices),
        "days" => nameof(Lifetime),
        _ => nameof(Name),
    };
}
