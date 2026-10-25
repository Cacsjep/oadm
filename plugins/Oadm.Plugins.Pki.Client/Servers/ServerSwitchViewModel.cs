using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Servers;
using Oadm.Sdk.Client;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Client.Servers;

/// <summary>
/// Turn on / Turn off of a server in PKI on its page (the 802.1X and EST servers, docs/spec/pki.md "One plugin"): the state
/// from the server part (<see cref="ServerSwitchMethods"/>), the buttons for users who may change the server, and
/// <see cref="RunningChanged"/> for the page, which reads and shows its cards only while the server runs. Without OADM PRO
/// the status is <see cref="ProLicenseTexts.NeedsPro"/> and Turn on is disabled; while the trial was never used, the off
/// state offers Start 30-day trial (administrators) through the host.
/// </summary>
public sealed partial class ServerSwitchViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly string _pluginId;
    private readonly string _turnOffTitle;
    private readonly string _turnOffMessage;
    private readonly string _name;

    /// <param name="ctx">The page's context.</param>
    /// <param name="pluginId">The core plugin of the server ("oadm.est"): its "manage" permission turns it on and off.</param>
    /// <param name="name">The server's name ("EST server").</param>
    /// <param name="turnOffMessage">What stops while it is off (the confirmation of Turn off).</param>
    public ServerSwitchViewModel(ICorePluginClientContext ctx, string pluginId, string name, string turnOffMessage)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        ArgumentNullException.ThrowIfNull(name);
        _name = name;
        _turnOffTitle = "Turn off the " + name;
        _turnOffMessage = turnOffMessage ?? throw new ArgumentNullException(nameof(turnOffMessage));
        _ctx.PermissionsChanged += (_, _) => NotifyButtons();
    }

    /// <summary>The state was read once: until then the page shows its loading overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOffState), nameof(OffTitle), nameof(ShowOffTurnOn), nameof(ShowAdminLine), nameof(OffStatus), nameof(HasOffStatus),
        nameof(ShowTrial), nameof(ShowStartTrial), nameof(ShowTrialAdminLine))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>The stored choice.</summary>
    [ObservableProperty]
    public partial bool On { get; private set; }

    /// <summary>The server runs: the page shows its cards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOffState))]
    public partial bool Running { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPro), nameof(OffTitle), nameof(ShowOffTurnOn), nameof(ShowAdminLine), nameof(OffStatus), nameof(HasOffStatus),
        nameof(ShowTrial), nameof(ShowStartTrial), nameof(ShowTrialAdminLine))]
    [NotifyCanExecuteChangedFor(nameof(StartTrialCommand))]
    public partial bool ProActive { get; private set; }

    /// <summary>The 30-day trial was never used on this server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrial), nameof(ShowStartTrial), nameof(ShowTrialAdminLine))]
    [NotifyCanExecuteChangedFor(nameof(StartTrialCommand))]
    public partial bool TrialAvailable { get; private set; }

    /// <summary>The trial offer in the off state: OADM PRO is missing and the trial was never used.</summary>
    public bool ShowTrial => NeedsPro && TrialAvailable;

    /// <summary>Start 30-day trial, the main action of the off state, for administrators.</summary>
    public bool ShowStartTrial => ShowTrial && _ctx.IsAdministrator;

    /// <summary><see cref="ProLicenseTexts.TrialAdminLine"/> for everyone else.</summary>
    public bool ShowTrialAdminLine => ShowTrial && !_ctx.IsAdministrator;

    /// <summary>"Off", "Needs an OADM PRO license or trial.", "Could not start: ..." while it does not run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed), nameof(OffTitle), nameof(OffStatus), nameof(HasOffStatus))]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TurnOnCommand), nameof(TurnOffCommand), nameof(StartTrialCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>A Turn on or Turn off that failed: below the buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The page shows only this switch (the server does not run).</summary>
    public bool ShowOffState => IsLoaded && !Running;

    /// <summary>OADM PRO is not active: the status chip warns.</summary>
    public bool NeedsPro => IsLoaded && !ProActive && ReadError is null;

    /// <summary>The server is on but could not start: the status chip is red.</summary>
    public bool Failed => ReadError is not null || StatusText.StartsWith(ServerSwitchMethods.StartFailed(string.Empty), StringComparison.Ordinal);

    /// <summary>The role may change the server and the user has all devices (the server is server-wide).</summary>
    public bool MayManage => _ctx.HasAllDevices && _ctx.Has(PermissionKeys.Of(_pluginId, PermissionKeys.Manage));

    /// <summary>Turn on: while it is off (without OADM PRO disabled, the tooltip says why).</summary>
    public bool ShowTurnOn => MayManage && IsLoaded && ReadError is null && !On;

    /// <summary>Turn off: while it is on, also without OADM PRO (so a lapsed license never brings a server back unasked).</summary>
    public bool ShowTurnOff => MayManage && IsLoaded && ReadError is null && On;

    /// <summary>The tooltip of a disabled Turn on.</summary>
    public string? TurnOnTip => ProActive ? null : ProLicenseTexts.NeedsPro;

    /// <summary>Raised after <see cref="Running"/> changed (the page reads its cards when the server started).</summary>
    public event EventHandler? RunningChanged;

    /// <summary>Reads the state (the page shown); a read that fails shows its reason in the status (red, no buttons).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        ServerSwitchState state;
        try
        {
            state = ServerSwitchMethods.Deserialize(await _ctx.InvokeAsync(ServerSwitchMethods.Get, null, ct).ConfigureAwait(true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown in the status.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReadError = ServerErrors.Message(ex);
            Apply(new ServerSwitchState(On, false, ProActive, ReadError));
            return;
        }

        ReadError = null;
        Apply(state);
    }

    /// <summary>The state could not be read: the status says why, the buttons stay hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed), nameof(ShowTurnOn), nameof(ShowTurnOff), nameof(OffTitle), nameof(ShowOffTurnOn), nameof(ShowAdminLine),
        nameof(OffStatus), nameof(HasOffStatus))]
    public partial string? ReadError { get; private set; }

    // ------------------------------------------------------------------ the off state (ui:OffState on the server's tab)

    /// <summary>"The EST server is off", "The EST server needs OADM PRO", "The EST server did not start".</summary>
    public string OffTitle => NeedsPro ? $"The {_name} needs OADM PRO" : Failed ? $"The {_name} did not start" : $"The {_name} is off";

    /// <summary>Turn on in the off state: a user who may change the server, with OADM PRO.</summary>
    public bool ShowOffTurnOn => ShowTurnOn && ProActive;

    /// <summary>"An administrator can turn it on." for users who may not.</summary>
    public bool ShowAdminLine => IsLoaded && ReadError is null && ProActive && !MayManage;

    public const string AdminLine = "An administrator can turn it on.";

    /// <summary>Why it does not run, below the text: the license it needs, or why it did not start; null when it is just off.</summary>
    public string? OffStatus => !IsLoaded ? null : NeedsPro ? ProLicenseTexts.NeedsPro : Failed ? StatusText : null;

    public bool HasOffStatus => OffStatus is not null;

    /// <summary>A live event of the page: the switch's own topic changes the state; others are not for it.</summary>
    public bool OnEvent(PluginEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Topic != ServerSwitchMethods.Topic)
        {
            return false;
        }

        Apply(ServerSwitchMethods.Deserialize(e.PayloadJson));
        return true;
    }

    /// <summary>Applies a state read from the server.</summary>
    public void Apply(ServerSwitchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var wasRunning = Running;
        var wasLoaded = IsLoaded;
        On = state.On;
        ProActive = state.ProActive;
        TrialAvailable = state.TrialAvailable;
        StatusText = state.StatusText;
        Running = state.Running;
        IsLoaded = true;
        NotifyButtons();
        if (wasRunning != Running || !wasLoaded)
        {
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanTurnOn() => MayManage && ProActive && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanTurnOn))]
    private Task TurnOnAsync() => ChangeAsync(ServerSwitchMethods.TurnOn);

    private bool CanStartTrial() => ShowStartTrial && !IsBusy;

    /// <summary>
    /// Start 30-day trial: the host asks the server (Administrator, once per server); the server part follows the license
    /// at once, so Turn on is offered next. A refusal shows below the buttons.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartTrial))]
    private async Task StartTrialAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            await _ctx.StartProTrialAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Shown below the buttons.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(CancellationToken.None).ConfigureAwait(true); // the license state the server part sees now
    }

    private bool CanTurnOff() => MayManage && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    private async Task TurnOffAsync()
    {
        if (await _ctx.ConfirmAsync(_turnOffTitle, _turnOffMessage, "Turn off").ConfigureAwait(true))
        {
            await ChangeAsync(ServerSwitchMethods.TurnOff).ConfigureAwait(true);
        }
    }

    private async Task ChangeAsync(string method)
    {
        IsBusy = true;
        Error = null;
        try
        {
            Apply(ServerSwitchMethods.Deserialize(await _ctx.InvokeAsync(method, null, CancellationToken.None).ConfigureAwait(true)));
        }
#pragma warning disable CA1031 // Shown below the buttons.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ServerErrors.Message(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyButtons()
    {
        OnPropertyChanged(nameof(MayManage));
        OnPropertyChanged(nameof(ShowTurnOn));
        OnPropertyChanged(nameof(ShowTurnOff));
        OnPropertyChanged(nameof(TurnOnTip));
        OnPropertyChanged(nameof(ShowOffTurnOn));
        OnPropertyChanged(nameof(ShowAdminLine));
        OnPropertyChanged(nameof(NeedsPro));
        OnPropertyChanged(nameof(ShowStartTrial));
        OnPropertyChanged(nameof(ShowTrialAdminLine));
        TurnOnCommand.NotifyCanExecuteChanged();
        StartTrialCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
    }
}
