using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>What the Provisioning window does for its view model (the profile editor); tests fake it.</summary>
public interface IProvisioningUi
{
    /// <summary>Shows the profile editor modally until it closes.</summary>
    Task ShowProfileEditorAsync(ProfileEditorViewModel editor);
}

/// <summary>One profile in the list of Provisioning management.</summary>
public sealed record ProfileRow(ProfileSummary Summary)
{
    public Guid Id => Summary.Id;

    public string Name => Summary.Name;

    public int Nodes => Summary.Nodes;

    public bool IsActive => Summary.IsActive;

    /// <summary>A draft: it has errors and cannot be active.</summary>
    public bool HasErrors => !Summary.IsValid;

    /// <summary>"Active", "Has errors" or empty.</summary>
    public string StateText => HasErrors ? ProvisioningTexts.HasErrors : IsActive ? ProvisioningTexts.Active : string.Empty;

    public bool HasState => StateText.Length > 0;
}

/// <summary>
/// Provisioning management (docs/spec/provisioning.md "Where it lives"), opened by the Devices toolbar's Provisioning button:
/// the On / Off switch (On refused with the server's reason directly below it), the active profile (profiles without
/// errors), the warnings, and the profile list with New and Edit (the profile editor), Activate (refused while the profile
/// has errors) and Delete (confirmed, red). Follows the plugin's status event while the window is open.
/// </summary>
public sealed partial class ProvisioningDialogViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;

    /// <param name="ctx">The plugin's context (the toolbar button's).</param>
    public ProvisioningDialogViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Set by the window.</summary>
    public IProvisioningUi? Ui { get; set; }

    /// <summary>The waits of the status watch; tests pass their own.</summary>
    public PluginEventWatchOptions WatchOptions { get; init; } = new() { MaxRetryDelay = TimeSpan.FromSeconds(30) };

    // ---------------------------------------------------------------- status

    /// <summary>The server's status: on or off, the active profile, why On is refused, the warnings, the DHCP server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(OnText), nameof(HasOnText), nameof(Warnings), nameof(HasWarnings), nameof(SwitchTip))]
    public partial StatusReply Status { get; private set; } = new();

    /// <summary>Provisioning is on (the switch).</summary>
    public bool IsOn => Status.On;

    /// <summary>"Turned on 2026-11-18 14:02 by anna" while on; empty while off.</summary>
    public string OnText => Status is { On: true, OnAt: { } at } ? ProvisioningTexts.TurnedOn(at, Status.OnBy) : string.Empty;

    public bool HasOnText => OnText.Length > 0;

    /// <summary>Why On does not work now (the switch's tooltip while off); null when it would.</summary>
    public string? SwitchTip => Status.On ? null : Status.TurnOnRefusal;

    public IReadOnlyList<string> Warnings => Status.Warnings;

    public bool HasWarnings => Status.Warnings.Count > 0;

    /// <summary>The refusal of On or Off, directly below the switch; null when fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSwitchError))]
    public partial string? SwitchError { get; private set; }

    public bool HasSwitchError => SwitchError is not null;

    /// <summary>The switch was flipped: On (refused with the reason while the server would refuse it) or Off.</summary>
    [RelayCommand(CanExecute = nameof(CanToggle))]
    public async Task ToggleAsync()
    {
        var turnOn = !Status.On;
        try
        {
            if (turnOn && Status.TurnOnRefusal is { } refusal)
            {
                SwitchError = refusal;
                return;
            }

            await RunAsync(
                async json => Status = PluginJson.Parse<StatusReply>(
                    await _ctx.InvokeAsync(turnOn ? ProvisioningMethods.TurnOn : ProvisioningMethods.TurnOff, null, CancellationToken.None).ConfigureAwait(true), json),
                error => SwitchError = error).ConfigureAwait(true);
        }
        finally
        {
            OnPropertyChanged(nameof(IsOn)); // the switch flipped itself: it shows the server's state again
        }
    }

    public bool CanToggle() => !IsBusy && !IsLoading;

    // ---------------------------------------------------------------- active profile

    /// <summary>The profiles that can be active (without errors), for the active profile's choice.</summary>
    public RangeObservableCollection<ProfileRow> ActiveChoices { get; } = [];

    /// <summary>The active profile; choosing another one activates it.</summary>
    public ProfileRow? ActiveChoice
    {
        get => ActiveChoices.FirstOrDefault(r => r.IsActive);
        set
        {
            if (value is not null && !value.IsActive && !IsBusy)
            {
                _ = ActivateAsync(value);
            }
        }
    }

    /// <summary>The refusal of a new active profile, directly below its choice; null when fine.</summary>
    [ObservableProperty]
    public partial string? ActiveError { get; private set; }

    // ---------------------------------------------------------------- profiles

    public RangeObservableCollection<ProfileRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(ActivateCommand), nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(ActivateTip))]
    public partial ProfileRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewCommand), nameof(EditCommand), nameof(ActivateCommand), nameof(DeleteCommand), nameof(ToggleCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    /// <summary>Nothing runs: the switch and the active profile's choice can be used.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>A refusal of the server, directly below the list; null when fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    public bool HasError => ErrorText is not null;

    public bool ShowEmpty => Rows.Count == 0 && !IsLoading;

    /// <summary>Why Activate is off, or what it does.</summary>
    public string ActivateTip => Selected switch
    {
        { HasErrors: true } => ProvisioningTexts.FixProfileFirst,
        { IsActive: true } => ProvisioningTexts.ActiveAlready,
        _ => ProvisioningTexts.ActivateTip,
    };

    /// <summary>Close the window.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Reads the status and the profiles.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            Status = PluginJson.Parse<StatusReply>(await _ctx.InvokeAsync(ProvisioningMethods.GetStatus, null, ct).ConfigureAwait(true), ProvisioningJson.Options);
            await ReadProfilesAsync(ct).ConfigureAwait(true);
            ErrorText = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Keeps the window in step with the server until <paramref name="ct"/> is cancelled (the window closed): reads the status
    /// and the profiles, then applies every status event (On, Off and profile changes, also by another administrator).
    /// </summary>
    public Task WatchAsync(CancellationToken ct) =>
        _ctx.RunAsync(LoadAsync, OnEventAsync, WatchOptions, ct);

    /// <summary>A status event: the new status, and the profiles again (they may have changed with it).</summary>
    internal async Task OnEventAsync(PluginEvent item)
    {
        if (item.Topic != ProvisioningMethods.StatusTopic || PluginJson.TryParse<StatusReply>(item.PayloadJson, ProvisioningJson.Options) is not { } status)
        {
            return;
        }

        Status = status;
        await ReadProfilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanNew))]
    public Task NewAsync() => EditAsync(null);

    public bool CanNew() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    public Task EditAsync() => Selected is { } row ? EditAsync(row.Id) : Task.CompletedTask;

    public bool CanEdit() => !IsBusy && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    public Task ActivateAsync() => Selected is { } row ? ActivateAsync(row, error => ErrorText = error) : Task.CompletedTask;

    public bool CanActivate() => !IsBusy && Selected is { HasErrors: false, IsActive: false };

    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async Task DeleteAsync()
    {
        if (Selected is not { } row
            || !await _ctx.ConfirmAsync(ProvisioningTexts.DeleteProfile, ProvisioningTexts.DeleteProfileMessage(row.Name), ProvisioningTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(
            async json =>
            {
                Apply(PluginJson.Parse<ProfilesReply>(
                    await _ctx.InvokeAsync(ProvisioningMethods.DeleteProfile, PluginJson.Serialize(new IdRequest(row.Id), json), CancellationToken.None).ConfigureAwait(true), json));
                await ReadStatusAsync().ConfigureAwait(true);
            },
            error => ErrorText = error).ConfigureAwait(true);
    }

    public bool CanDelete() => !IsBusy && Selected is not null;

    /// <summary>The active profile's choice: activates <paramref name="row"/>; a refusal shows below the choice.</summary>
    private async Task ActivateAsync(ProfileRow row)
    {
        try
        {
            await ActivateAsync(row, error => ActiveError = error).ConfigureAwait(true);
        }
        finally
        {
            OnPropertyChanged(nameof(ActiveChoice)); // a refused choice goes back to the active profile
        }
    }

    private Task ActivateAsync(ProfileRow row, Action<string> refused) =>
        RunAsync(
            async json =>
            {
                Status = PluginJson.Parse<StatusReply>(
                    await _ctx.InvokeAsync(ProvisioningMethods.ActivateProfile, PluginJson.Serialize(new IdRequest(row.Id), json), CancellationToken.None).ConfigureAwait(true), json);
                await ReadProfilesAsync(CancellationToken.None).ConfigureAwait(true);
            },
            refused);

    private async Task EditAsync(Guid? id)
    {
        var editor = new ProfileEditorViewModel(_ctx, id, Status.Dhcp);
        if (Ui is { } ui)
        {
            await ui.ShowProfileEditorAsync(editor).ConfigureAwait(true);
        }

        if (editor.Saved is { } saved)
        {
            await RunAsync(
                async _ =>
                {
                    await ReadProfilesAsync(CancellationToken.None).ConfigureAwait(true);
                    await ReadStatusAsync().ConfigureAwait(true); // the first saved profile becomes the active one
                },
                error => ErrorText = error).ConfigureAwait(true);
            Selected = Rows.FirstOrDefault(r => r.Id == saved.Id);
        }
    }

    private async Task ReadStatusAsync() =>
        Status = PluginJson.Parse<StatusReply>(await _ctx.InvokeAsync(ProvisioningMethods.GetStatus, null, CancellationToken.None).ConfigureAwait(true), ProvisioningJson.Options);

    private async Task ReadProfilesAsync(CancellationToken ct) =>
        Apply(PluginJson.Parse<ProfilesReply>(await _ctx.InvokeAsync(ProvisioningMethods.ListProfiles, null, ct).ConfigureAwait(true), ProvisioningJson.Options));

    /// <summary>Runs a change; a refusal goes to <paramref name="refused"/>, the other errors of the dialog clear.</summary>
    private async Task RunAsync(Func<System.Text.Json.JsonSerializerOptions, Task> work, Action<string> refused)
    {
        IsBusy = true;
        SwitchError = null;
        ActiveError = null;
        ErrorText = null;
        try
        {
            await work(ProvisioningJson.Options).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            refused(ServerErrors.Message(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(ProfilesReply reply)
    {
        var selected = Selected?.Id;
        Rows.ReplaceAll(reply.Profiles.Select(p => new ProfileRow(p)));
        ActiveChoices.ReplaceAll(Rows.Where(r => !r.HasErrors));
        Selected = Rows.FirstOrDefault(r => r.Id == selected);
        OnPropertyChanged(nameof(ActiveChoice));
        OnPropertyChanged(nameof(ShowEmpty));
    }
}
