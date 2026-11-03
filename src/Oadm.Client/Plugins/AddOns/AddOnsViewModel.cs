using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.Plugins.AddOns;

/// <summary>One add-on of the Optional plugins card: name, one line, version, its state and Install or Remove.</summary>
public sealed partial class AddOnRowViewModel : ObservableObject
{
    private readonly AddOnsViewModel _owner;

    internal AddOnRowViewModel(AddOnsViewModel owner, AddOnInfo info)
    {
        _owner = owner;
        Id = info.Id;
        Apply(info);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>One line what it adds.</summary>
    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    /// <summary>"V4.11.0".</summary>
    [ObservableProperty]
    public partial string MetaText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(IsRemoved), nameof(IsNotInstalled), nameof(ShowInstall), nameof(ShowRemove),
        nameof(ShowNotInstalled), nameof(IsInstalling), nameof(ProgressText), nameof(MessageIsError), nameof(IsFailed))]
    public partial AddOnState State { get; private set; }

    /// <summary>The server's reason why it cannot be installed or is not loaded; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage), nameof(InstallTip))]
    public partial string Message { get; private set; } = string.Empty;

    /// <summary>False while the server cannot reach the release.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(InstallTip))]
    public partial bool ServerCanInstall { get; private set; } = true;

    /// <summary>The step of a running install of this client; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalling), nameof(ProgressText), nameof(CanInstall), nameof(ShowInstall))]
    public partial AddOnStep? Step { get; private set; }

    /// <summary>0 to 100 while installing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int Percent { get; private set; }

    /// <summary>Why the last install or remove of this client failed (below the description, red).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanRemove))]
    public partial bool IsBusy { get; internal set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasMessage => Message.Length > 0;

    /// <summary>The add-on could not be loaded after an update: the message is a problem, not a note.</summary>
    public bool MessageIsError => IsFailed;

    /// <summary>Installed before, not loaded now (a failed download after an update): Install tries again, Remove drops it.</summary>
    public bool IsFailed => State == AddOnState.AddOnFailed;

    /// <summary>A newer version came from the server while this client runs the older one (below the description).</summary>
    [ObservableProperty]
    public partial bool RestartNeeded { get; internal set; }

    public bool IsInstalled => State == AddOnState.AddOnInstalled;

    public bool IsRemoved => State == AddOnState.AddOnRemoved;

    public bool IsNotInstalled => State is AddOnState.AddOnNotInstalled or AddOnState.AddOnFailed;

    public bool IsInstalling => Step is not null || State == AddOnState.AddOnInstalling;

    /// <summary>"Download 42 %" while this client installs, "Installing 42 %" for an install of another administrator.</summary>
    public string ProgressText => Step is { } step ? OptionalPluginTexts.Progress(step, Percent) : OptionalPluginTexts.Installing(Percent);

    /// <summary>
    /// Administrators: Install for an add-on that is not installed, not loaded after an update, or removed (the server keeps
    /// the loaded one, nothing is downloaded).
    /// </summary>
    public bool ShowInstall => _owner.IsAdmin && (IsNotInstalled || IsRemoved) && !IsInstalling;

    public bool CanInstall => ServerCanInstall && !IsBusy && Step is null;

    /// <summary>The tooltip of a disabled Install: the server's reason.</summary>
    public string? InstallTip => ServerCanInstall || Message.Length == 0 ? null : Message;

    /// <summary>Users who cannot install see the state instead of the button.</summary>
    public bool ShowNotInstalled => !_owner.IsAdmin && IsNotInstalled;

    /// <summary>Administrators: Remove for an installed add-on and for one that failed to load (its files go at once).</summary>
    public bool ShowRemove => _owner.IsAdmin && (IsInstalled || IsFailed) && !IsInstalling;

    public bool CanRemove => !IsBusy;

    internal void Apply(AddOnInfo info)
    {
        Name = info.DisplayName;
        Description = info.Description;
        MetaText = OptionalPluginTexts.Meta(info.Version);
        State = info.State;
        Message = info.Message;
        ServerCanInstall = info.CanInstall;
        if (Step is null)
        {
            Percent = info.Percent;
        }
    }

    internal void Report(AddOnStep step, int percent)
    {
        Step = step;
        Percent = Math.Clamp(percent, 0, 100);
    }

    internal void EndInstall()
    {
        Step = null;
        Percent = 0;
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(ShowInstall));
        OnPropertyChanged(nameof(ShowRemove));
        OnPropertyChanged(nameof(ShowNotInstalled));
    }
}

/// <summary>
/// The Optional plugins card of the Plugins page (docs/spec/add-ons.md "Plugins page"): the add-ons of the server's version
/// with their state. Administrators install (the progress of Download, Check, Install in the row) and remove them (with a
/// confirmation; the files go at the next server start). After either, the plugin list is read again: the add-on's package
/// and its client part follow (<see cref="PluginPackageStore"/>); while the client parts come the card shows a progress row.
/// A server without add-ons hides the card.
/// </summary>
public sealed partial class AddOnsViewModel : ObservableObject
{
    private readonly IOadmApi _api;
    private readonly PluginPackageStore _store;
    private readonly IDialogService? _dialogs;
    private readonly UserSession? _session;
    private readonly AddOnClientSync? _sync;
    private readonly ILogger<AddOnsViewModel> _logger;

    public AddOnsViewModel(IOadmApi api, PluginPackageStore store, ILogger<AddOnsViewModel> logger, IDialogService? dialogs = null, UserSession? session = null,
        AddOnClientSync? sync = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        _dialogs = dialogs;
        _session = session;
        _sync = sync;
        if (sync is not null)
        {
            // Raised on the UI thread (the sync runs there between its background steps).
            sync.StateChanged += (_, _) => FollowSync();
        }

        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    foreach (AddOnRowViewModel row in AddOns)
                    {
                        row.NotifyRole();
                    }
                }
            };
        }
    }

    public bool IsAdmin => _session?.IsAdmin ?? true;

    public RangeObservableCollection<AddOnRowViewModel> AddOns { get; } = [];

    /// <summary>The server offers add-ons: the card shows (false for an older server).</summary>
    [ObservableProperty]
    public partial bool HasAddOns { get; private set; }

    /// <summary>The first read runs: the card shows a progress row.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The list could not be read (below the list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>The client parts are coming from the server: the card shows a progress row.</summary>
    [ObservableProperty]
    public partial bool IsGettingParts { get; private set; }

    private void FollowSync()
    {
        if (_sync is null)
        {
            return;
        }

        IsGettingParts = _sync.IsSyncing;
        foreach (AddOnRowViewModel row in AddOns)
        {
            row.RestartNeeded = _sync.RestartNeeded.Contains(row.Id);
        }
    }

    /// <summary>Reads the add-ons (the page opened).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = AddOns.Count == 0;
        try
        {
            Apply(await _api.ListAddOnsAsync(checkReach: true, ct).ConfigureAwait(true));
            ErrorText = null;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            AddOns.ReplaceAll([]);
            HasAddOns = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = OptionalPluginTexts.ListFailed(Message(ex));
            HasAddOns = true;
            LogListFailed(_logger, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Downloads, checks and installs an add-on on the server; the row shows each step.</summary>
    [RelayCommand]
    private async Task InstallAsync(AddOnRowViewModel? row)
    {
        if (row is null || !IsAdmin || !row.CanInstall)
        {
            return;
        }

        row.Error = null;
        row.IsBusy = true;
        row.Report(AddOnStep.Download, 0);
        string? error = OptionalPluginTexts.InstallInterrupted;
        try
        {
            await foreach (AddOnProgress progress in _api.InstallAddOnAsync(row.Id, CancellationToken.None).ConfigureAwait(true))
            {
                if (!string.IsNullOrEmpty(progress.Error))
                {
                    error = progress.Error;
                    break;
                }

                if (progress.Done)
                {
                    error = null;
                    break;
                }

                row.Report(progress.Step, progress.Percent);
            }
        }
        catch (Exception ex)
        {
            error = Message(ex);
        }

        row.EndInstall();
        row.Error = error;
        if (error is null)
        {
            LogInstalled(_logger, row.Id);
        }

        // The list and the plugin list again: the package (off) and its client part come with them.
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        row.IsBusy = false;
        if (error is null)
        {
            await _store.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Turns an add-on off at once after a confirmation; its files go at the next server start.</summary>
    [RelayCommand]
    private async Task RemoveAsync(AddOnRowViewModel? row)
    {
        if (row is null || !IsAdmin || !row.ShowRemove || !row.CanRemove)
        {
            return;
        }

        if (_dialogs is not null
            && !await _dialogs.ConfirmAsync(OptionalPluginTexts.RemoveTitle, OptionalPluginTexts.RemoveQuestion(row.Name), OptionalPluginTexts.Remove).ConfigureAwait(true))
        {
            return;
        }

        row.Error = null;
        row.IsBusy = true;
        try
        {
            Apply(await _api.RemoveAddOnAsync(row.Id, CancellationToken.None).ConfigureAwait(true));
            LogRemoved(_logger, row.Id);
            await _store.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            row.Error = OptionalPluginTexts.RemoveFailed(row.Name, Message(ex));
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private void Apply(AddOnList list)
    {
        var byId = AddOns.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<AddOnRowViewModel>(list.AddOns.Count);
        foreach (AddOnInfo info in list.AddOns)
        {
            if (byId.TryGetValue(info.Id, out AddOnRowViewModel? row))
            {
                row.Apply(info); // the row keeps its error and a running install
            }
            else
            {
                row = new AddOnRowViewModel(this, info);
            }

            rows.Add(row);
        }

        if (!rows.SequenceEqual(AddOns))
        {
            AddOns.ReplaceAll(rows);
        }

        HasAddOns = rows.Count > 0;
        FollowSync();
    }

    /// <summary>gRPC errors carry the user message in Status.Detail.</summary>
    private static string Message(Exception ex) =>
        ex is RpcException rpc && !string.IsNullOrEmpty(rpc.Status.Detail) ? rpc.Status.Detail : ex.Message;

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} installed")]
    private static partial void LogInstalled(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Optional plugin {Id} removed")]
    private static partial void LogRemoved(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The optional plugins could not be read: {Message}")]
    private static partial void LogListFailed(ILogger logger, string message);
}
