using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>What the page view does for the view model (windows); tests leave it null or fake it.</summary>
public interface IProvisioningPageUi
{
    /// <summary>Shows Manage profiles modally until it closes.</summary>
    Task ShowProfilesAsync(ProfilesDialogViewModel dialog);
}

/// <summary>
/// The Provisioning page (spec "The page"): the DHCP card, Start and Stop, the active profile and its editor, the camera
/// table (paged from the server, then the batched live events, indexed by MAC address) with Run failed again, Continue and
/// Remove on the selected rows, and the flow of the camera chosen last. <see cref="Activate"/> while shown.
/// </summary>
public sealed partial class ProvisioningViewModel : ObservableObject, IDisposable
{
    /// <summary>Rows per page of the camera list.</summary>
    public const int PageSize = 1000;

    /// <summary>The DHCP server's plugin, whose page Turn on and Edit open.</summary>
    public const string DhcpPluginId = "oadm.dhcp-server";

    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, CameraRowViewModel> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CameraRowViewModel> _all = [];
    private List<CameraRowViewModel> _selected = [];
    private Dictionary<NodeKind, string> _kindNames = [];
    private CancellationTokenSource? _active;
    private StatusReply _status = new();
    private bool _applyingProfiles;
    private bool _read;

    public ProvisioningViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayManage));
            RefreshCommands();
        };
    }

    /// <summary>The waits of the event watch; a test passes its own.</summary>
    public PluginEventWatchOptions EventWatch { get; init; } = new();

    /// <summary>Set by the view.</summary>
    public IProvisioningPageUi? Ui { get; set; }

    public bool MayManage => _ctx.Has(PermissionKeys.Manage);

    // ---- Status and DHCP card ----

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    public partial bool IsStarted { get; private set; }

    public bool IsStopped => !IsStarted;

    [ObservableProperty]
    public partial string StateText { get; private set; } = ProvisioningTexts.NotStarted;

    [ObservableProperty]
    public partial bool DhcpIsRunning { get; private set; }

    [ObservableProperty]
    public partial string DhcpStatusText { get; private set; } = ProvisioningTexts.DhcpNotInstalled;

    [ObservableProperty]
    public partial string DhcpInterfaceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DhcpRangeText { get; private set; } = string.Empty;

    public string DhcpButtonText => DhcpIsRunning ? ProvisioningTexts.Edit : ProvisioningTexts.TurnOn;

    /// <summary>The page's warnings (a Set IP pool that overlaps the DHCP range), one per line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? WarningText { get; private set; }

    public bool HasWarning => WarningText is not null;

    /// <summary>A failed read or action, below the toolbar; null when fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    public bool HasError => ErrorText is not null;

    // ---- Profiles ----

    public ObservableCollection<ProfileSummary> Profiles { get; } = [];

    /// <summary>The active profile; choosing another makes it active (refused while it has errors).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial ProfileSummary? ActiveProfile { get; set; }

    public bool HasProfiles => Profiles.Count > 0;

    // ---- Cameras ----

    /// <summary>The rows the grid shows (the search applied).</summary>
    public RangeObservableCollection<CameraRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The camera whose flow the canvas shows (the row clicked last).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlow))]
    public partial CameraRowViewModel? FlowCamera { get; set; }

    public bool HasFlow => FlowCamera is not null;

    /// <summary>The flow of <see cref="FlowCamera"/>.</summary>
    public NodeChain Flow { get; } = new() { CanSelect = false };

    public int CameraCount => _all.Count;

    public bool ShowEmpty => Rows.Count == 0 && !IsLoading;

    /// <summary>One line: what to do first.</summary>
    public string EmptyText =>
        _all.Count > 0 ? ProvisioningTexts.NoMatch
        : !DhcpIsRunning ? ProvisioningTexts.EmptyDhcpFirst
        : Profiles.Count == 0 ? ProvisioningTexts.EmptyProfileFirst
        : IsStarted ? ProvisioningTexts.EmptyStarted
        : ProvisioningTexts.EmptyNotStarted;

    public IReadOnlyList<CameraRowViewModel> SelectedRows => _selected;

    /// <summary>Why Start is disabled, or null.</summary>
    public string? StartBlockedReason =>
        IsStarted ? ProvisioningTexts.StartedAlready
        : !DhcpIsRunning ? ProvisioningTexts.DhcpFirst
        : ActiveProfile is null ? ProvisioningTexts.ProfileFirst
        : !ActiveProfile.IsValid ? ProvisioningTexts.FixProfileFirst
        : null;

    public string StartTip => StartBlockedReason ?? ProvisioningTexts.StartProvisioning;

    public bool IsActive => _active is not null;

    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        _ = _ctx.RunAsync(LoadAsync, HandleEvent, EventWatch, _active.Token);
    }

    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose() => Deactivate();

    /// <summary>Reads everything: the status, the profiles, the node names and every camera row (paged).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!_read)
        {
            IsLoading = true;
            Notify();
        }

        try
        {
            var json = ProvisioningJson.Options;
            var status = PluginJson.Parse<StatusReply>(await _ctx.InvokeAsync(ProvisioningMethods.GetStatus, null, ct).ConfigureAwait(true), json);
            var profiles = PluginJson.Parse<ProfilesReply>(await _ctx.InvokeAsync(ProvisioningMethods.ListProfiles, null, ct).ConfigureAwait(true), json);
            if (_kindNames.Count == 0)
            {
                var kinds = PluginJson.Parse<List<NodeKindInfo>>(await _ctx.InvokeAsync(ProvisioningMethods.NodeKinds, null, ct).ConfigureAwait(true), json);
                _kindNames = kinds.ToDictionary(k => k.Kind, k => k.Name);
            }

            var rows = new List<CameraRow>();
            string? after = null;
            CamerasPage page;
            do
            {
                page = PluginJson.Parse<CamerasPage>(
                    await _ctx.InvokeAsync(ProvisioningMethods.ListCameras, PluginJson.Serialize(new CamerasRequest(after, PageSize), json), ct).ConfigureAwait(true), json);
                rows.AddRange(page.Rows);
                after = page.Next;
            }
            while (after is not null);

            ApplyProfiles(profiles);
            ApplyStatus(status);
            ReplaceRows(rows, page.Counts);
            ErrorText = null;
            _read = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
            throw;
        }
        finally
        {
            IsLoading = false;
            Notify();
        }
    }

    /// <summary>An event of the server part: the status, or changed and removed camera rows.</summary>
    public void HandleEvent(PluginEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var json = ProvisioningJson.Options;
        switch (e.Topic)
        {
            case ProvisioningMethods.StatusTopic:
                var wasStarted = IsStarted;
                ApplyStatus(PluginJson.Parse<StatusReply>(e.PayloadJson, json));
                _ = RefreshProfilesAsync();
                if (!wasStarted && IsStarted)
                {
                    ReplaceRows([], _status.Counts); // Start clears the last session's rows; the new ones arrive as events
                }

                break;
            case ProvisioningMethods.CamerasTopic:
                ApplyCameras(PluginJson.Parse<CamerasEvent>(e.PayloadJson, json));
                break;
        }
    }

    /// <summary>The grid's selection changed (one call per change, never per row).</summary>
    public void SetSelection(IEnumerable<CameraRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _selected = [.. rows];
        FlowCamera = _selected.Count > 0 ? _selected[^1] : null;

        RefreshCommands();
    }

    /// <summary>Shows the flow of a camera (a click on its row).</summary>
    public void ShowFlow(CameraRowViewModel? row) => FlowCamera = row;

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        await RunAsync(ProvisioningMethods.Start).ConfigureAwait(true);
    }

    public bool CanStart() => MayManage && !IsBusy && StartBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    public async Task StopAsync()
    {
        if (!await _ctx.ConfirmAsync(ProvisioningTexts.StopTitle, ProvisioningTexts.StopMessage, ProvisioningTexts.Stop).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(ProvisioningMethods.Stop).ConfigureAwait(true);
    }

    public bool CanStop() => MayManage && !IsBusy && IsStarted;

    /// <summary>Manage profiles: the dialog with the profile list, New, Edit, Activate and Delete.</summary>
    [RelayCommand(CanExecute = nameof(CanManageProfiles))]
    public async Task ManageProfilesAsync()
    {
        var dialog = new ProfilesDialogViewModel(_ctx);
        if (Ui is { } ui)
        {
            await ui.ShowProfilesAsync(dialog).ConfigureAwait(true);
        }

        await RefreshProfilesAsync().ConfigureAwait(true);
    }

    public bool CanManageProfiles() => MayManage;

    [RelayCommand(CanExecute = nameof(CanRunFailedAgain))]
    public Task RunFailedAgainAsync() => RowsAsync(ProvisioningMethods.RunFailedAgain, [.. _selected.Where(r => r.HasFailed).Select(r => r.Mac)]);

    public bool CanRunFailedAgain() => MayManage && IsStarted && _selected.Any(r => r.HasFailed);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    public Task ContinueAsync() => RowsAsync(ProvisioningMethods.Continue, [.. _selected.Where(r => r.IsWaiting).Select(r => r.Mac)]);

    public bool CanContinue() => MayManage && _selected.Any(r => r.IsWaiting);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    public async Task RemoveAsync()
    {
        var macs = _selected.Select(r => r.Mac).ToList();
        if (macs.Count == 0
            || !await _ctx.ConfirmAsync(ProvisioningTexts.RemoveTitle, ProvisioningTexts.RemoveMessage(macs.Count), ProvisioningTexts.Remove).ConfigureAwait(true))
        {
            return;
        }

        await RowsAsync(ProvisioningMethods.RemoveCameras, macs).ConfigureAwait(true);
    }

    public bool CanRemove() => MayManage && _selected.Count > 0;

    [RelayCommand]
    public Task OpenDhcpAsync() => _ctx.OpenAsync("plugin:" + DhcpPluginId);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnActiveProfileChanged(ProfileSummary? oldValue, ProfileSummary? newValue)
    {
        OnPropertyChanged(nameof(StartBlockedReason));
        OnPropertyChanged(nameof(StartTip));
        if (_applyingProfiles || newValue is null || newValue.IsActive)
        {
            return;
        }

        _ = ActivateAsync(newValue, oldValue);
    }

    partial void OnDhcpIsRunningChanged(bool value) => OnPropertyChanged(nameof(DhcpButtonText));

    partial void OnFlowCameraChanged(CameraRowViewModel? value) => UpdateFlow();

    private async Task ActivateAsync(ProfileSummary profile, ProfileSummary? previous)
    {
        try
        {
            var json = ProvisioningJson.Options;
            ApplyStatus(PluginJson.Parse<StatusReply>(
                await _ctx.InvokeAsync(ProvisioningMethods.ActivateProfile, PluginJson.Serialize(new IdRequest(profile.Id), json), CancellationToken.None).ConfigureAwait(true), json));
            await RefreshProfilesAsync().ConfigureAwait(true);
            ErrorText = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
            _applyingProfiles = true;
            ActiveProfile = previous;
            _applyingProfiles = false;
        }
    }

    private async Task RunAsync(string method)
    {
        IsBusy = true;
        try
        {
            var json = ProvisioningJson.Options;
            var wasStarted = IsStarted;
            ApplyStatus(PluginJson.Parse<StatusReply>(await _ctx.InvokeAsync(method, null, CancellationToken.None).ConfigureAwait(true), json));
            if (!wasStarted && IsStarted)
            {
                ReplaceRows([], _status.Counts);
            }

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

    private async Task RowsAsync(string method, List<string> macs)
    {
        if (macs.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _ctx.InvokeAsync(method, PluginJson.Serialize(new MacsRequest(macs), ProvisioningJson.Options), CancellationToken.None).ConfigureAwait(true);
            if (method == ProvisioningMethods.RemoveCameras)
            {
                RemoveRows(macs);
            }

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

    private async Task RefreshProfilesAsync()
    {
        try
        {
            ApplyProfiles(PluginJson.Parse<ProfilesReply>(await _ctx.InvokeAsync(ProvisioningMethods.ListProfiles, null, CancellationToken.None).ConfigureAwait(true), ProvisioningJson.Options));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    private void ApplyProfiles(ProfilesReply reply)
    {
        _applyingProfiles = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in reply.Profiles)
            {
                Profiles.Add(profile);
            }

            ActiveProfile = Profiles.FirstOrDefault(p => p.Id == reply.ActiveId);
        }
        finally
        {
            _applyingProfiles = false;
        }

        OnPropertyChanged(nameof(HasProfiles));
        Notify();
    }

    private void ApplyStatus(StatusReply status)
    {
        _status = status;
        IsStarted = status.Started;
        StateText = status.Started && status.StartedAt is { } at ? ProvisioningTexts.Started(at, status.StartedBy) : ProvisioningTexts.NotStarted;
        var dhcp = status.Dhcp;
        DhcpIsRunning = dhcp?.IsRunning == true;
        DhcpStatusText = dhcp?.StatusText ?? ProvisioningTexts.DhcpNotInstalled;
        DhcpInterfaceText = dhcp?.InterfaceName is { Length: > 0 } name
            ? "Interface " + name + (dhcp.ServerAddress is { Length: > 0 } address ? " (" + address + ")" : string.Empty)
            : string.Empty;
        DhcpRangeText = dhcp?.RangeStart is { Length: > 0 } start ? "Range " + start + " to " + dhcp.RangeEnd : string.Empty;
        WarningText = status.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, status.Warnings);
        UpdateFlow();
        Notify();
    }

    private void ReplaceRows(IReadOnlyList<CameraRow> rows, CameraCounts counts)
    {
        _byMac.Clear();
        _all.Clear();
        foreach (var row in rows)
        {
            var vm = new CameraRowViewModel(row);
            if (_byMac.TryAdd(row.Mac, vm))
            {
                _all.Add(vm);
            }
        }

        if (FlowCamera is { } flow)
        {
            FlowCamera = _byMac.GetValueOrDefault(flow.Mac);
        }

        _selected = [.. _selected.Select(s => _byMac.GetValueOrDefault(s.Mac)).OfType<CameraRowViewModel>()];
        ApplyFilter();
    }

    /// <summary>A batch of the live feed: changed rows in place (O(1) each by MAC), new rows appended, removed rows out.</summary>
    private void ApplyCameras(CamerasEvent e)
    {
        var added = new List<CameraRowViewModel>();
        foreach (var row in e.Changed)
        {
            if (_byMac.TryGetValue(row.Mac, out var vm))
            {
                vm.Apply(row);
            }
            else
            {
                vm = new CameraRowViewModel(row);
                _byMac[row.Mac] = vm;
                _all.Add(vm);
                added.Add(vm);
            }
        }

        if (e.Removed.Count > 0)
        {
            RemoveRows(e.Removed);
        }

        if (added.Count > 0)
        {
            var search = SearchText.Trim().ToLowerInvariant();
            Rows.AddRange(search.Length == 0 ? added : added.Where(r => r.SearchKey.Contains(search, StringComparison.Ordinal)));
        }

        if (FlowCamera is not null)
        {
            UpdateFlow();
        }

        RefreshCommands();
        Notify();
    }

    private void RemoveRows(IReadOnlyCollection<string> macs)
    {
        var gone = new HashSet<CameraRowViewModel>();
        foreach (var mac in macs)
        {
            if (_byMac.Remove(mac, out var vm))
            {
                gone.Add(vm);
            }
        }

        if (gone.Count == 0)
        {
            return;
        }

        _all.RemoveAll(gone.Contains);
        Rows.RemoveAll(gone.Contains);
        _selected.RemoveAll(gone.Contains);
        if (FlowCamera is { } flow && gone.Contains(flow))
        {
            FlowCamera = null;
        }

        RefreshCommands();
        Notify();
    }

    /// <summary>The search over every row: one pass, one reset of the grid.</summary>
    public void ApplyFilter()
    {
        var search = SearchText.Trim().ToLowerInvariant();
        Rows.ReplaceAll(search.Length == 0 ? _all : _all.Where(r => r.SearchKey.Contains(search, StringComparison.Ordinal)));
        Notify();
    }

    private void UpdateFlow()
    {
        Flow.Nodes = FlowCamera is { } camera ? CameraFlow.Nodes(_status.SessionProfile, _kindNames, camera.Row) : [];
    }

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ManageProfilesCommand.NotifyCanExecuteChanged();
        RunFailedAgainCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CameraCount));
        OnPropertyChanged(nameof(StartBlockedReason));
        OnPropertyChanged(nameof(StartTip));
        RefreshCommands();
    }
}
