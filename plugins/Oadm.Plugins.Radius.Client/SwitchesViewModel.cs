using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One switch of the Switches card.</summary>
public sealed class SwitchRowViewModel(SwitchRow source)
{
    /// <summary>The site text of a switch of this server.</summary>
    public const string ThisServer = "This server";

    public SwitchRow Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    public string Name => Source.Name;

    public string Address => Source.Address;

    public string Site => Source.SiteName.Length > 0 ? Source.SiteName : Source.SiteId is null ? ThisServer : string.Empty;

    public string State => StateText(Source.State);

    public bool IsOk => Source.State == SwitchStates.Answered;

    public bool IsError => Source.State is SwitchStates.WrongSecret or SwitchStates.SiteOffline;

    /// <summary>"2026-10-24 14:03:12"; empty before the first request.</summary>
    public string LastRequest => Source.LastRequestUtc is { } utc ? RadiusTexts.Time(utc) : string.Empty;

    public DateTime LastRequestSortKey => Source.LastRequestUtc ?? DateTime.MinValue;

    /// <summary>"Answered", "No requests yet", "Wrong secret", "Site offline".</summary>
    public static string StateText(string? state) => state switch
    {
        SwitchStates.Answered => "Answered",
        SwitchStates.WrongSecret => "Wrong secret",
        SwitchStates.SiteOffline => "Site offline",
        _ => "No requests yet",
    };
}

/// <summary>What the Switches card asks of the view: the switch dialog.</summary>
public interface ISwitchDialogs
{
    /// <summary>Shows the dialog until it saved (true) or was closed.</summary>
    Task<bool> ShowSwitchAsync(SwitchDialogViewModel dialog);
}

/// <summary>
/// The Switches card: name, address, site, state chip, last request; Add switch, Edit, Delete (red, confirmed). A change
/// applies to the running server at once; at most 500 switches.
/// </summary>
public sealed partial class SwitchesViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Func<bool> _mayManage;
    private readonly Func<RadiusState?> _state;
    private readonly Func<IReadOnlyList<SiteRef>> _sites;

    public SwitchesViewModel(ICorePluginClientContext ctx, Func<bool> mayManage, Func<RadiusState?> state, Func<IReadOnlyList<SiteRef>> sites)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _sites = sites ?? throw new ArgumentNullException(nameof(sites));
    }

    /// <summary>The switch dialog (the page view).</summary>
    public ISwitchDialogs? Dialogs { get; set; }

    public RangeObservableCollection<SwitchRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    public partial SwitchRowViewModel? Selected { get; set; }

    public bool MayManage => _mayManage();

    /// <summary>One line when there is no switch: what to do first.</summary>
    public string? EmptyText => Rows.Count > 0 ? null
        : MayManage ? "No switches yet. Add the switches whose ports use 802.1X." : "No switches yet.";

    /// <summary>Why Add switch is off.</summary>
    public string? AddTip => Rows.Count >= RadiusLimits.MaxSwitches ? RadiusRules.TooManySwitches : null;

    public async Task LoadAsync(CancellationToken ct)
    {
        Apply(RadiusJson.Deserialize<SwitchList>(await _ctx.InvokeAsync(RadiusMethods.ListSwitches, null, ct).ConfigureAwait(true)).Switches);
    }

    /// <summary>Replaces the rows (one reset), keeps the selected switch.</summary>
    public void Apply(IReadOnlyList<SwitchRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var selected = Selected?.Source.Id;
        Rows.ReplaceAll(rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => new SwitchRowViewModel(r)));
        Selected = selected is { } id ? Rows.FirstOrDefault(r => r.Source.Id == id) : null;
        NotifyRows();
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(EmptyText));
        AddCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void NotifyRows()
    {
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(AddTip));
        AddCommand.NotifyCanExecuteChanged();
    }

    private bool CanAdd() => MayManage && Rows.Count < RadiusLimits.MaxSwitches;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (Dialogs is { } dialogs && await dialogs.ShowSwitchAsync(NewDialog(null)).ConfigureAwait(true))
        {
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private bool CanEdit() => MayManage && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAsync()
    {
        if (Selected is { } row && Dialogs is { } dialogs && await dialogs.ShowSwitchAsync(NewDialog(row.Source)).ConfigureAwait(true))
        {
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (!await _ctx.ConfirmAsync("Delete switch", $"Delete the switch {row.Name}? OADM no longer answers it, and its open sessions end.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync(RadiusMethods.DeleteSwitch, RadiusJson.Serialize(new SwitchRef(row.Source.Id)), CancellationToken.None).ConfigureAwait(true);
            Rows.Remove(row);
            Selected = null;
            NotifyRows();
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("Delete switch", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
    }

    internal SwitchDialogViewModel NewDialog(SwitchRow? existing)
    {
        var state = _state();
        var others = Rows.Where(r => r.Source.Id != existing?.Id).Select(r => r.Source).ToList();
        return new SwitchDialogViewModel(_ctx, existing, _sites(), others, state);
    }

    private async Task ReloadAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The switches event reads again too.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

/// <summary>
/// The switch dialog: Name, Address, Site, Shared secret (Generate fills a random one and shows it once to copy; on Edit
/// empty keeps the current secret), the address the switch must use as its RADIUS server, errors below the fields.
/// </summary>
public sealed partial class SwitchDialogViewModel : ValidatingViewModel
{
    private readonly ICorePluginClientContext _ctx;
    private readonly SwitchRow? _existing;
    private readonly IReadOnlyList<SwitchRow> _others;
    private readonly RadiusState? _state;

    public SwitchDialogViewModel(ICorePluginClientContext ctx, SwitchRow? existing, IReadOnlyList<SiteRef> sites, IReadOnlyList<SwitchRow> others, RadiusState? state)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        ArgumentNullException.ThrowIfNull(sites);
        _existing = existing;
        _others = others ?? [];
        _state = state;
        SiteChoices = [new Choice<Guid?>(null, SwitchRowViewModel.ThisServer), .. sites.OrderBy(s => s.SiteName, StringComparer.CurrentCultureIgnoreCase).Select(s => new Choice<Guid?>(s.SiteId, s.SiteName))];
        Name = existing?.Name ?? string.Empty;
        Address = existing?.Address ?? string.Empty;
        Site = SiteChoices.FirstOrDefault(c => c.Value == existing?.SiteId) ?? SiteChoices[0];
        Validation
            .Rule(nameof(Name), () => Name.Trim().Length == 0 ? RadiusRules.EnterName
                : Name.Trim().Length > RadiusLimits.MaxNameLength ? RadiusRules.NameTooLong
                : _others.Any(o => string.Equals(o.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)) ? RadiusRules.NameTaken
                : null)
            .Rule(nameof(Address), () => !RadiusRules.IsAddress(Address) ? RadiusRules.EnterAddress
                : _others.Any(o => o.SiteId == Site.Value && RadiusRules.SameAddress(o.Address, Address)) ? RadiusRules.AddressTaken
                : null)
            .Rule(nameof(Secret), () => Secret.Length == 0 && IsEditing ? null
                : Secret.Length == 0 ? RadiusRules.EnterSecret
                : Secret.Length < RadiusLimits.MinSecretLength ? RadiusRules.SecretTooShort
                : Secret.Length > RadiusLimits.MaxSecretLength ? RadiusRules.SecretTooLong
                : !Secret.All(c => c is > ' ' and <= '~') ? RadiusRules.SecretCharacters
                : null);
        Validation.Validate();
    }

    public bool IsEditing => _existing is not null;

    public string Title => IsEditing ? "Edit switch" : "Add switch";

    public IReadOnlyList<Choice<Guid?>> SiteChoices { get; }

    /// <summary>A server without sites shows no Site field.</summary>
    public bool HasSites => SiteChoices.Count > 1;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RadiusServerHint))]
    public partial Choice<Guid?> Site { get; set; }

    [ObservableProperty]
    public partial string Secret { get; set; } = string.Empty;

    /// <summary>The secret shows in plain text (after Generate, to copy it once).</summary>
    [ObservableProperty]
    public partial bool ShowSecret { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(GenerateCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>"Leave empty to keep the current secret" on Edit; "Copy it now ..." after Generate.</summary>
    public string SecretHint => ShowSecret ? "Copy it now into the switch: OADM does not show it again."
        : IsEditing ? "Leave empty to keep the current secret."
        : "16 characters or more, the same on the switch.";

    /// <summary>"RADIUS server for this switch: 10.0.0.17, port 1812".</summary>
    public string RadiusServerHint
    {
        get
        {
            var port = (_state?.Settings.Port ?? RadiusLimits.DefaultPort).ToString(CultureInfo.InvariantCulture);
            string address;
            if (_existing is { RadiusServerAddress: { Length: > 0 } known } existing && existing.SiteId == Site.Value)
            {
                address = known;
            }
            else if (Site.Value is not null)
            {
                address = "the address of the site's OADM Site Node";
            }
            else
            {
                var listen = _state?.Interfaces.FirstOrDefault(i => i.Id == _state.Settings.ListenOn);
                address = listen is { Addresses.Count: > 0 } && !Sdk.Network.InterfaceOptions.IsAll(listen.Id) ? listen.Addresses[0] : "an address of this server";
            }

            return $"RADIUS server for this switch: {address}, port {port}";
        }
    }

    /// <summary>The dialog closes (true = saved).</summary>
    public event EventHandler<bool>? CloseRequested;

    partial void OnShowSecretChanged(bool value) => OnPropertyChanged(nameof(SecretHint));

    partial void OnSiteChanged(Choice<Guid?> value) => Validation.Validate();

    protected override void OnValidationChanged() => SaveCommand.NotifyCanExecuteChanged();

    private bool CanGenerate() => !IsBusy;

    /// <summary>Generate: a random secret from the server, shown once to copy.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        IsBusy = true;
        try
        {
            var reply = RadiusJson.Deserialize<GeneratedSecret>(await _ctx.InvokeAsync(RadiusMethods.GenerateSecret, null, CancellationToken.None).ConfigureAwait(true));
            Secret = reply.Secret;
            ShowSecret = true;
        }
#pragma warning disable CA1031 // Shown below the field.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Validation.SetServerError(nameof(Secret), ServerErrors.Message(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && IsFormValid;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        if (!IsFormValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var request = new SaveSwitchRequest
            {
                Id = _existing?.Id,
                Name = Name.Trim(),
                Address = Address.Trim(),
                SiteId = Site.Value,
                Secret = Secret.Length == 0 ? null : Secret,
            };
            await _ctx.InvokeAsync(RadiusMethods.SaveSwitch, RadiusJson.Serialize(request), CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, true);
        }
#pragma warning disable CA1031 // Shown below the field it belongs to, else in a message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ServerErrors.Message(ex);
            var field = message switch
            {
                RadiusRules.EnterName or RadiusRules.NameTooLong or RadiusRules.NameTaken => nameof(Name),
                RadiusRules.EnterAddress or RadiusRules.AddressTaken => nameof(Address),
                RadiusRules.EnterSecret or RadiusRules.SecretTooShort or RadiusRules.SecretTooLong or RadiusRules.SecretCharacters => nameof(Secret),
                RadiusRules.TooManySwitches => null,
                _ => message.Contains("secret", StringComparison.OrdinalIgnoreCase) ? nameof(Secret)
                : message.Contains("address", StringComparison.OrdinalIgnoreCase) || message.Contains("host", StringComparison.OrdinalIgnoreCase) ? nameof(Address)
                : message.Contains("name", StringComparison.OrdinalIgnoreCase) ? nameof(Name)
                : null,
            };
            if (field is not null)
            {
                Validation.SetServerError(field, message);
            }
            else
            {
                await _ctx.ShowMessageAsync(Title, message).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

}
