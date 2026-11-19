using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>What the Manage profiles window does for its view model (the editor); tests fake it.</summary>
public interface IProfilesUi
{
    /// <summary>Shows the profile editor modally until it closes.</summary>
    Task ShowProfileEditorAsync(ProfileEditorViewModel editor);
}

/// <summary>One profile in the list of Manage profiles.</summary>
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
/// Manage profiles (spec "The page"): every profile with its number of nodes and its state (Active, Has errors), New and Edit
/// (the profile editor), Activate (refused while the profile has errors), Delete (confirmed, red).
/// </summary>
public sealed partial class ProfilesDialogViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;

    public ProfilesDialogViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Set by the window.</summary>
    public IProfilesUi? Ui { get; set; }

    public RangeObservableCollection<ProfileRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(ActivateCommand), nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(ActivateTip))]
    public partial ProfileRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewCommand), nameof(EditCommand), nameof(ActivateCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; set; }

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

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            Apply(PluginJson.Parse<ProfilesReply>(await _ctx.InvokeAsync(ProvisioningMethods.ListProfiles, null, ct).ConfigureAwait(true), ProvisioningJson.Options));
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

    [RelayCommand(CanExecute = nameof(CanNew))]
    public Task NewAsync() => EditAsync(null);

    public bool CanNew() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    public Task EditAsync() => Selected is { } row ? EditAsync(row.Id) : Task.CompletedTask;

    public bool CanEdit() => !IsBusy && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    public Task ActivateAsync() => Selected is { } row
        ? RunAsync(async json =>
        {
            await _ctx.InvokeAsync(ProvisioningMethods.ActivateProfile, PluginJson.Serialize(new IdRequest(row.Id), json), CancellationToken.None).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        })
        : Task.CompletedTask;

    public bool CanActivate() => !IsBusy && Selected is { HasErrors: false, IsActive: false };

    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async Task DeleteAsync()
    {
        if (Selected is not { } row
            || !await _ctx.ConfirmAsync(ProvisioningTexts.DeleteProfile, ProvisioningTexts.DeleteProfileMessage(row.Name), ProvisioningTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(async json => Apply(PluginJson.Parse<ProfilesReply>(
            await _ctx.InvokeAsync(ProvisioningMethods.DeleteProfile, PluginJson.Serialize(new IdRequest(row.Id), json), CancellationToken.None).ConfigureAwait(true), json)))
            .ConfigureAwait(true);
    }

    public bool CanDelete() => !IsBusy && Selected is not null;

    private async Task EditAsync(Guid? id)
    {
        var editor = new ProfileEditorViewModel(_ctx, id);
        if (Ui is { } ui)
        {
            await ui.ShowProfileEditorAsync(editor).ConfigureAwait(true);
        }

        if (editor.Saved is { } saved)
        {
            await LoadAsync().ConfigureAwait(true);
            Selected = Rows.FirstOrDefault(r => r.Id == saved.Id);
        }
    }

    private async Task RunAsync(Func<System.Text.Json.JsonSerializerOptions, Task> work)
    {
        IsBusy = true;
        try
        {
            await work(ProvisioningJson.Options).ConfigureAwait(true);
            ErrorText = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
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
        Selected = Rows.FirstOrDefault(r => r.Id == selected);
        OnPropertyChanged(nameof(ShowEmpty));
    }
}
