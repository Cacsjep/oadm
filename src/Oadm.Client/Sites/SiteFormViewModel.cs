using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;
using Oadm.Contracts.Sites;

namespace Oadm.Client.Sites;

/// <summary>What a <see cref="SiteFormViewModel"/> does.</summary>
public enum SiteFormMode
{
    Bind,
    Rename,
    ChangeAddress,
    AddManually,
    Replace,
    Remove,
}

/// <summary>
/// The single-operation dialogs of the Sites page (one window, <see cref="SiteFormWindow"/>): Bind (site name and pairing
/// code), Rename, Change address, Add manually, Replace with and Remove site. Errors stand below the field they belong to.
/// </summary>
public sealed partial class SiteFormViewModel : ValidatingViewModel
{
    public const string WrongCode = SiteTexts.WrongCode;

    private readonly IOadmApi _api;
    private readonly Site? _site;

    private SiteFormViewModel(IOadmApi api, SiteFormMode mode, Site? site, string title, string submitText)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        Mode = mode;
        _site = site;
        Title = title;
        SubmitText = submitText;
        if (mode is SiteFormMode.Bind or SiteFormMode.Rename)
        {
            Validation.Rule(nameof(Name), () => NameProblem(Name));
        }

        if (mode == SiteFormMode.Bind)
        {
            Validation.Rule(nameof(PairingCode), () => NormalizeCode(PairingCode) is null ? SiteTexts.CodeFormat : null);
        }

        if (mode is SiteFormMode.ChangeAddress or SiteFormMode.AddManually)
        {
            Validation.Rule(nameof(Address), () => AddressProblem(Address));
            Validation.Rule(nameof(PortText), () => ParsePort(PortText, out _) ? null : "Enter a port from 1 to 65535.");
        }

        if (mode == SiteFormMode.Replace)
        {
            Validation.Rule(nameof(NewSite), () => NewSite is null ? "Choose the site that replaces it." : null);
        }

        Validation.Validate();
    }

    public SiteFormMode Mode { get; }

    public string Title { get; }

    public string SubmitText { get; }

    public string Intro { get; private init; } = string.Empty;

    public bool HasIntro => Intro.Length > 0;

    public bool ShowName => Mode is SiteFormMode.Bind or SiteFormMode.Rename;

    public bool ShowCode => Mode == SiteFormMode.Bind;

    public bool ShowAddress => Mode is SiteFormMode.ChangeAddress or SiteFormMode.AddManually;

    public bool ShowReplace => Mode == SiteFormMode.Replace;

    public bool ShowKeepDevices => Mode == SiteFormMode.Remove && !IsRemovedSite;

    /// <summary>Remove devices of a removed site: no Keep devices choice.</summary>
    public bool IsRemovedSite { get; private init; }

    /// <summary>Remove site: the button is red.</summary>
    public bool IsDestructive => Mode == SiteFormMode.Remove;

    public bool IsPrimary => !IsDestructive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string PairingCode { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Address { get; set; } = string.Empty;

    /// <summary>Empty = 5081.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string PortText { get; set; } = string.Empty;

    public IReadOnlyList<Site> OtherSites { get; private init; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial Site? NewSite { get; set; }

    /// <summary>Remove site: keep the devices as Site offline (default) or remove them with their logins and tags.</summary>
    [ObservableProperty] public partial bool KeepDevices { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string BusyText => Mode switch
    {
        SiteFormMode.Bind => "Binding the site node",
        SiteFormMode.AddManually => "Looking for the site node",
        SiteFormMode.Replace => "Checking the devices through the new site",
        SiteFormMode.Remove => "Removing the site",
        _ => "Saving",
    };

    /// <summary>A server error that belongs to no field (Remove site, Replace with).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    public partial string? FormErrorText { get; private set; }

    public bool HasFormError => !string.IsNullOrEmpty(FormErrorText);

    public string? SubmitBlockedReason => Validation.FirstError;

    /// <summary>Add manually: the node that answered.</summary>
    public Site? FoundNode { get; private set; }

    public ReplaceSiteReply? ReplaceReply { get; private set; }

    public RemoveSiteReply? RemoveReply { get; private set; }

    /// <summary>True: done (the dialog closes); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    public static SiteFormViewModel Bind(IOadmApi api, Site node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var form = new SiteFormViewModel(api, SiteFormMode.Bind, node, "Bind site node", "Bind")
        {
            Intro = $"Enter the pairing code the OADM Site Node app shows on {(node.Name.Length > 0 ? node.Name : node.NodeAddress)}.",
        };
        form.Name = node.Name;
        form.Validation.Reset(nameof(Name));
        return form;
    }

    public static SiteFormViewModel Rename(IOadmApi api, Site site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var form = new SiteFormViewModel(api, SiteFormMode.Rename, site, "Rename site", "Save");
        form.Name = site.Name;
        form.Validation.Reset(nameof(Name));
        return form;
    }

    public static SiteFormViewModel ChangeAddress(IOadmApi api, Site site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var form = new SiteFormViewModel(api, SiteFormMode.ChangeAddress, site, "Change address", "Save")
        {
            Intro = $"Where the server finds the site node of {site.Name}.",
        };
        form.Address = site.NodeAddress;
        form.PortText = site.NodePort > 0 ? site.NodePort.ToString(CultureInfo.InvariantCulture) : string.Empty;
        form.Validation.Reset(nameof(Address), nameof(PortText));
        return form;
    }

    public static SiteFormViewModel AddManually(IOadmApi api) =>
        new(api, SiteFormMode.AddManually, null, "Add site node manually", "Find")
        {
            Intro = "A site node set to wait for the server.",
        };

    public static SiteFormViewModel Replace(IOadmApi api, Site site, IReadOnlyList<Site> others)
    {
        ArgumentNullException.ThrowIfNull(site);
        return new SiteFormViewModel(api, SiteFormMode.Replace, site, "Replace site", "Replace")
        {
            Intro = $"Moves the devices of {site.Name} to another site. Each device is checked there first.",
            OtherSites = others,
        };
    }

    public static SiteFormViewModel Remove(IOadmApi api, Site site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (site.State == SiteState.Removed)
        {
            // The site is gone already; its kept devices go now (Replace with moves them instead).
            return new SiteFormViewModel(api, SiteFormMode.Remove, site, "Remove devices", "Remove devices")
            {
                Intro = site.DeviceCount == 1
                    ? $"Remove the device of {site.Name} with its login and tags?"
                    : string.Create(CultureInfo.CurrentCulture, $"Remove the {site.DeviceCount:N0} devices of {site.Name} with their logins and tags?"),
                KeepDevices = false,
                IsRemovedSite = true,
            };
        }

        return new SiteFormViewModel(api, SiteFormMode.Remove, site, "Remove site", "Remove site")
        {
            Intro = site.DeviceCount == 1
                ? $"Remove site {site.Name}? It has 1 device."
                : string.Create(CultureInfo.CurrentCulture, $"Remove site {site.Name}? It has {site.DeviceCount:N0} devices."),
        };
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SubmitBlockedReason));
        SubmitCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The typed code as 8 characters of the pairing alphabet (case, spaces and dashes ignored), else null.</summary>
    public static string? NormalizeCode(string? typed) => PairingCodeText.Normalize(typed);

    public static string? NameProblem(string? name) => SiteTexts.NameProblem(name);

    private static string? AddressProblem(string? address)
    {
        var trimmed = (address ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Enter the address of the site node.";
        }

        return trimmed.Length > 253 || trimmed.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')
            ? "Enter an IP address or a host name."
            : null;
    }

    /// <summary>Empty = 5081; otherwise a whole number from 1 to 65535.</summary>
    public static bool ParsePort(string? text, out int port)
    {
        port = 5081;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 65535)
        {
            port = value;
            return true;
        }

        return false;
    }

    private bool CanSubmit() => !IsBusy && Validation.IsValid;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        IsBusy = true;
        FormErrorText = null;
        try
        {
            if (await RunAsync().ConfigureAwait(true))
            {
                CloseRequested?.Invoke(this, true);
            }
        }
        catch (RpcException ex)
        {
            ShowServerError(ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            ShowError("The server did not answer. Try again.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> RunAsync()
    {
        var ct = CancellationToken.None;
        switch (Mode)
        {
            case SiteFormMode.Bind:
                await _api.BindSiteAsync(_site!.NodeId, _site.NodeFingerprint, Name.Trim(), NormalizeCode(PairingCode)!, ct).ConfigureAwait(true);
                return true;
            case SiteFormMode.Rename:
                await _api.RenameSiteAsync(_site!.Id, Name.Trim(), ct).ConfigureAwait(true);
                return true;
            case SiteFormMode.ChangeAddress:
                ParsePort(PortText, out var port);
                await _api.ChangeSiteAddressAsync(_site!.Id, Address.Trim(), port, ct).ConfigureAwait(true);
                return true;
            case SiteFormMode.AddManually:
                ParsePort(PortText, out var nodePort);
                var target = SiteTexts.Address(Address.Trim(), nodePort);
                await foreach (var found in _api.FindSiteNodesAsync(0, target, ct).ConfigureAwait(true))
                {
                    if (found.Node is { } node)
                    {
                        FoundNode = node;
                    }
                }

                if (FoundNode is null)
                {
                    Validation.SetServerError(nameof(Address), $"No OADM Site Node answered at {target}.");
                    return false;
                }

                return true;
            case SiteFormMode.Replace:
                ReplaceReply = await _api.ReplaceSiteAsync(_site!.Id, NewSite!.Id, ct).ConfigureAwait(true);
                return true;
            case SiteFormMode.Remove:
                RemoveReply = await _api.RemoveSiteAsync(_site!.Id, KeepDevices, ct).ConfigureAwait(true);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Below the field the error belongs to: a name problem below Site name, everything else of Bind below Pairing code.</summary>
    private void ShowServerError(RpcException ex)
    {
        var detail = ex.Status.Detail.Length > 0 ? ex.Status.Detail : ex.StatusCode.ToString();
        switch (Mode)
        {
            case SiteFormMode.Bind:
                if (detail.Contains("pairing code", StringComparison.OrdinalIgnoreCase) || ex.StatusCode == StatusCode.ResourceExhausted
                    || !IsNameError(ex, detail))
                {
                    Validation.SetServerError(nameof(PairingCode), ex.StatusCode == StatusCode.PermissionDenied ? WrongCode : detail);
                }
                else
                {
                    Validation.SetServerError(nameof(Name), detail);
                }

                break;
            case SiteFormMode.Rename:
                Validation.SetServerError(nameof(Name), detail);
                break;
            case SiteFormMode.ChangeAddress:
            case SiteFormMode.AddManually:
                Validation.SetServerError(nameof(Address), detail);
                break;
            case SiteFormMode.Replace:
                Validation.SetServerError(nameof(NewSite), detail);
                break;
            default:
                ShowError(detail);
                break;
        }
    }

    private void ShowError(string text)
    {
        switch (Mode)
        {
            case SiteFormMode.Bind:
                Validation.SetServerError(nameof(PairingCode), text);
                break;
            case SiteFormMode.Rename:
                Validation.SetServerError(nameof(Name), text);
                break;
            case SiteFormMode.ChangeAddress:
            case SiteFormMode.AddManually:
                Validation.SetServerError(nameof(Address), text);
                break;
            default:
                FormErrorText = text;
                break;
        }
    }

    // The server's name refusals (the shared texts) belong to Site name; a copy of a node ("This site node is a copy of X")
    // is ALREADY_EXISTS too but about the node.
    private static bool IsNameError(RpcException ex, string detail) =>
        ex.StatusCode is StatusCode.AlreadyExists or StatusCode.InvalidArgument
        && detail is SiteTexts.NameTaken or SiteTexts.NameRequired or SiteTexts.NameTooLong or SiteTexts.NameInvalid;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
