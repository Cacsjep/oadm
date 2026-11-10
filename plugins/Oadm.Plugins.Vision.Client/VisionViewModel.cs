using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Shared.Vision;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A pipeline in the list: name, camera, the state chip ("Watching · 8 fps", orange slower, red with a problem), the last line.</summary>
public sealed partial class PipelineListRow : ObservableObject
{
    public PipelineListRow(Api.PipelineRow row, string camera) => Apply(row, camera);

    public string Id { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Camera { get; private set; } = string.Empty;

    public Guid DeviceId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsWarning), nameof(IsError), nameof(ChipText))]
    public partial Api.PipelineRow Row { get; private set; } = new();

    [ObservableProperty]
    public partial string Last { get; private set; } = string.Empty;

    /// <summary>This pipeline is being copied: a ring in place of its ⋯ button.</summary>
    [ObservableProperty]
    public partial bool IsCopying { get; set; }

    public string ChipText => Row.Problem is { } problem ? ProblemChips.Of(problem, Problems)
        : Row.Mode == PipelineMode.OnAction ? VisionTexts.OnAction
        : Row.Slow ? VisionTexts.WatchingReads(Row.Fps, Row.Reads ?? 0)
        : VisionTexts.WatchingAt(Row.Fps);

    /// <summary>Vision Center's own problem texts for the shared chips (plugins/Shared/Vision.Client).</summary>
    private static readonly ProblemTexts Problems = new()
    {
        IsWatchingLimit = PipelineRules.IsWatchingLimit,
        StreamErrors = [CameraFrameTexts.NoPicture, CameraFrameTexts.NoPictures, CameraFrameTexts.ResolutionNotOffered, CameraFrameTexts.SourceNotOffered],
        Starting = PipelineRules.Starting,
        Off = PipelineRules.Off,
        NotInstalled = [PipelineRules.NotRunning],
        Unknown = VisionTexts.NotRunning,
    };

    /// <summary>The chip's tooltip: the server's whole text of the problem.</summary>
    public string? Problem => Row.Problem;

    public bool IsError => Row.Problem is not null;

    public bool IsWarning => !IsError && Row.Mode == PipelineMode.Watching && Row.Slow;

    public bool IsOk => !IsError && !IsWarning && Row.Mode == PipelineMode.Watching;

    /// <summary>The camera's label after a change of the device list (no change: nothing raised).</summary>
    public void SetCamera(string camera) => Camera = camera;

    public void Apply(Api.PipelineRow row, string camera)
    {
        ArgumentNullException.ThrowIfNull(row);
        Id = row.Id;
        Name = row.Name;
        DeviceId = row.DeviceId;
        Camera = camera;
        Row = row;
        Last = row.Last;
        OnPropertyChanged(nameof(Problem));
    }
}

/// <summary>The traffic light and the load bar in the page header (as Text recognition).</summary>
public sealed partial class VisionStatsViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool HasStats { get; private set; }

    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsBusy), nameof(IsOverloaded), nameof(IsStarting))]
    public partial LoadState State { get; private set; }

    public bool IsOk => State == LoadState.Ok;

    public bool IsBusy => State == LoadState.Busy;

    public bool IsOverloaded => State == LoadState.Overloaded;

    public bool IsStarting => State == LoadState.Starting;

    [ObservableProperty]
    public partial double Load { get; private set; }

    [ObservableProperty]
    public partial string LoadText { get; private set; } = string.Empty;

    public void Apply(VisionStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        State = stats.State;
        StateText = stats.StateText;
        Load = Math.Clamp(stats.Load, 0, 100);
        LoadText = IsStarting ? string.Empty : VisionTexts.Percent(stats.Load);
        HasStats = true;
    }
}

/// <summary>
/// The Vision Center page (docs/spec/vision-center.md "Page"): the pipelines on the left (New with the camera first, Delete,
/// search) with the Models card below them, the shown pipeline's canvas in the middle and its live video and selected node on the
/// right (<see cref="Editor"/>), the load, "Runs on" and Use GPU in the page header. <see cref="Activate"/> when the page shows
/// reads the lists, watches the live events (pipelines, models, stats, the preview) and previews the shown pipeline;
/// <see cref="Deactivate"/> stops all of it.
/// </summary>
public sealed partial class VisionViewModel : ObservableObject, IDisposable
{
    /// <summary>"oadm.vision:change".</summary>
    public static readonly string ChangePermissionKey = VisionInfo.PluginId + ":" + VisionInfo.ChangePermission;

    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, PipelineListRow> _rows = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<Guid, string> _cameraTexts = [];
    private int _labelsVersion;
    private bool _disposed;
    private CancellationTokenSource? _active;
    private int _openVersion;
    private bool _applyingEngine;
    private bool _selecting;
    private IVisionDialogs? _dialogs;

    public VisionViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        MayChange = ctx.Has(ChangePermissionKey);
        Models = new ModelsViewModel(ctx);
        Models.Changed += (_, _) =>
        {
            Editor?.Canvas.Recheck();
            OnPropertyChanged(nameof(ShowEmpty));
        };
        ctx.PermissionsChanged += OnPermissionsChanged;
        ctx.DevicesChanged += OnDevicesChanged;
    }

    /// <summary>Wait before watching the events again after the stream broke.</summary>
    public TimeSpan ReconnectDelay { get; set; } = PluginEventWatch.DefaultRetryDelay;

    /// <summary>Decodes a paused frame off the UI thread (tests decode in place).</summary>
    public Func<byte[], Task<Bitmap?>> DecodePicture { get; set; } = DecodeAsync;

    /// <summary>The graph check of the editors (tests hand their own; null: the server's own <c>VisionGraph.Check</c>).</summary>
    public Func<Pipeline, GraphCheck>? Check { get; set; }

    /// <summary>The keep-alives' clock (tests pass a fake one).</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Opens the camera picker (set by the view); the argument is the camera chosen so far.</summary>
    public Func<Guid?, Task<Guid?>>? PickCamera { get; set; }

    /// <summary>Opens the picker of several cameras (set by the view) over the cameras a copy can go to; null when cancelled.</summary>
    public Func<IReadOnlyList<IDeviceInfo>, Task<IReadOnlyList<Guid>?>>? PickCameras { get; set; }

    /// <summary>The cameras a copy of <paramref name="row"/>'s pipeline can go to: every camera but its own.</summary>
    public IReadOnlyList<IDeviceInfo> CopyTargetsOf(PipelineListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return [.. Cameras.Where(d => d.Id != row.DeviceId)];
    }

    /// <summary>A copy is on its way: the ring in the row of the pipeline.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    public partial bool IsCopying { get; private set; }

    /// <summary>The shown pipeline with unsaved edits is this row's: its copy waits for Save.</summary>
    private bool HasUnsaved(PipelineListRow row) => Editor is { IsNew: false, IsDirty: true } editor && editor.Id == row.Id;

    private bool CanCopy(PipelineListRow? row) => MayChange && !IsCopying && row is not null && !HasUnsaved(row);

    /// <summary>
    /// The ⋯ menu of a pipeline row (ui:RowActions): Copy to cameras (off with its reason while the shown pipeline has unsaved
    /// edits), then a separator and Delete in red (the same confirmation as the header's Delete).
    /// </summary>
    public IReadOnlyList<IssuedCertificateAction> ActionsOf(PipelineListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return
        [
            new(HasUnsaved(row) ? VisionTexts.CopyToCamerasSaveFirst : VisionTexts.CopyToCameras, CopyCommand, row),
            IssuedCertificateAction.Separator,
            new(VisionTexts.Delete, DeleteRowCommand, row, IsDanger: true),
        ];
    }

    /// <summary>
    /// Copy to cameras: the cameras (not the pipeline's own, at most <see cref="VisionTexts.MaxCopies"/>), then one call; the
    /// copies join the list, the first one shows; the refused cameras in one message, a line each. Nothing when all worked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync(PipelineListRow? row)
    {
        if (row is null || PickCameras is not { } pick
            || await pick(CopyTargetsOf(row)).ConfigureAwait(true) is not { Count: > 0 } cameras)
        {
            return;
        }

        if (cameras.Count > VisionTexts.MaxCopies)
        {
            await _ctx.ShowMessageAsync(VisionTexts.CopyToCameras, VisionTexts.TooManyCameras).ConfigureAwait(true);
            return;
        }

        IsCopying = true;
        row.IsCopying = true;
        CopyPipelineReply reply;
        try
        {
            var json = await _ctx.InvokeAsync(VisionMethods.CopyPipeline,
                VisionJson.Serialize(new CopyPipelineRequest { PipelineId = row.Id, DeviceIds = cameras }), CancellationToken.None).ConfigureAwait(true);
            reply = VisionJson.Deserialize<CopyPipelineReply>(json);
        }
#pragma warning disable CA1031 // The server's reason in the host's message; nothing was copied.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            IsCopying = false;
            row.IsCopying = false;
            await _ctx.ShowMessageAsync(VisionTexts.CopyToCameras, ServerErrors.Message(ex)).ConfigureAwait(true);
            return;
        }

        IsCopying = false;
        row.IsCopying = false;
        foreach (var copy in reply.Created)
        {
            if (!_rows.ContainsKey(copy.Id))
            {
                _rows[copy.Id] = new PipelineListRow(new Api.PipelineRow { Id = copy.Id, Name = copy.Name, DeviceId = copy.DeviceId, Mode = copy.Mode, Fps = copy.Fps }, CameraText(copy.DeviceId));
                _order.Add(copy.Id);
            }
        }

        ApplyFilter();
        if (reply.Created.Count > 0 && _rows.TryGetValue(reply.Created[0].Id, out var first) && await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            await OpenAsync(first, _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }

        if (reply.Refused.Count > 0)
        {
            await _ctx.ShowMessageAsync(VisionTexts.CopyToCameras,
                VisionTexts.CopyRefusals(reply.Refused.Select(r => $"{CameraText(r.Key)}: {r.Value}"))).ConfigureAwait(true);
        }
    }

    /// <summary>Delete of a row's menu: the shown pipeline as the header's Delete; another one after the same question.</summary>
    [RelayCommand]
    private async Task DeleteRowAsync(PipelineListRow? row)
    {
        if (row is null || !MayChange)
        {
            return;
        }

        if (Editor is { IsNew: false } editor && editor.Id == row.Id)
        {
            await DeleteCommand.ExecuteAsync(null).ConfigureAwait(true);
            return;
        }

        if (!await _ctx.ConfirmAsync(VisionTexts.DeletePipeline, VisionTexts.DeletePipelineQuestion(row.Name.Trim()), VisionTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        var error = await GuardedRun.RunAsync(async () =>
        {
            await _ctx.InvokeAsync(VisionMethods.DeletePipeline, VisionJson.Serialize(new PipelineRequest { PipelineId = row.Id }), CancellationToken.None)
                .ConfigureAwait(true);
            _rows.Remove(row.Id);
            _order.Remove(row.Id);
            ApplyFilter();
        }).ConfigureAwait(true);
        if (error is not null)
        {
            await _ctx.ShowMessageAsync(VisionTexts.DeletePipeline, error).ConfigureAwait(true);
        }
    }

    partial void OnEditorChanged(PipelineEditorViewModel? oldValue, PipelineEditorViewModel? newValue)
    {
        oldValue?.PropertyChanged -= OnEditorPropertyChanged;
        newValue?.PropertyChanged += OnEditorPropertyChanged;
        CopyCommand.NotifyCanExecuteChanged();
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PipelineEditorViewModel.IsDirty) or nameof(PipelineEditorViewModel.IsNew))
        {
            CopyCommand.NotifyCanExecuteChanged();
        }
    }

    public IVisionDialogs? Dialogs
    {
        get => _dialogs;
        set
        {
            _dialogs = value;
            Models.Dialogs = value;
            Editor?.Dialogs = value;
        }
    }

    public ModelsViewModel Models { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewCommand), nameof(DeleteCommand), nameof(CopyCommand))]
    public partial bool MayChange { get; private set; }

    public RangeObservableCollection<PipelineListRow> Pipelines { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial PipelineListRow? SelectedPipeline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor), nameof(ShowEmpty))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial PipelineEditorViewModel? Editor { get; private set; }

    public bool HasEditor => Editor is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowNoMatch))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsOpening { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>No pipeline at all: "Import a model, then create a pipeline."</summary>
    public bool ShowEmpty => !IsLoading && _rows.Count == 0 && Editor is null;

    public static string EmptyText => VisionInfo.EmptyText;

    public bool ShowNoMatch => !IsLoading && _rows.Count > 0 && Pipelines.Count == 0;

    public static string NoMatchText => VisionTexts.NoMatch;

    [ObservableProperty]
    public partial string RunsOnText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RunsOnShortText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetGpu))]
    public partial bool GpuAvailable { get; private set; }

    [ObservableProperty]
    public partial bool UseGpu { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetGpu), nameof(ShowGpuRing))]
    public partial bool IsSettingGpu { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetGpu), nameof(ShowGpuRing))]
    public partial bool IsGpuStarting { get; private set; }

    public bool ShowGpuRing => IsSettingGpu || IsGpuStarting;

    public bool CanSetGpu => MayChange && _ctx.HasAllDevices && GpuAvailable && !IsSettingGpu && !IsGpuStarting;

    public EngineInfo Engine { get; private set; } = new();

    public VisionStatsViewModel Stats { get; } = new();

    public bool IsActive => _active is not null;

    // ---- Lifecycle ----

    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        _ = _ctx.RunAsync(HandleEvent, ReconnectDelay, token);
        _ = KeepStatsAsync(token);
        Editor?.StartPreview();
        _ = LoadAsync(token);
    }

    public void Deactivate()
    {
        if (_active is not { } active)
        {
            return;
        }

        _active = null;
        active.Cancel();
        active.Dispose();
        Editor?.StopPreview();
    }

    public void Dispose()
    {
        _disposed = true;
        Deactivate();
        _ctx.PermissionsChanged -= OnPermissionsChanged;
        _ctx.DevicesChanged -= OnDevicesChanged;
        Editor?.Dispose();
    }

    /// <summary>Reads the engine, the models and the pipelines, then opens the first pipeline (or keeps the shown one).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            await LoadEngineAsync(ct).ConfigureAwait(true);
            try
            {
                await Models.LoadAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Without the models the pipelines still show; their nodes say what is missing.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            var json = await _ctx.InvokeAsync(VisionMethods.ListPipelines, "{}", ct).ConfigureAwait(true);
            ApplyList(VisionJson.Deserialize<PipelineList>(json));
            ErrorText = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // The server's reason below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
        }

        if (Editor is null && Pipelines.Count > 0)
        {
            await OpenAsync(Pipelines[0], ct).ConfigureAwait(true);
        }
    }

    private async Task LoadEngineAsync(CancellationToken ct)
    {
        try
        {
            var json = await _ctx.InvokeAsync(VisionMethods.GetEngine, "{}", ct).ConfigureAwait(true);
            ApplyEngine(VisionJson.Deserialize<EngineInfo>(json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // No engine line; the CPU choices.
        catch (Exception)
#pragma warning restore CA1031
        {
            ApplyEngine(new EngineInfo());
        }
    }

    public void ApplyEngine(EngineInfo engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
        _applyingEngine = true;
        try
        {
            RunsOnText = string.IsNullOrEmpty(engine.RunsOn) ? string.Empty : VisionTexts.RunsOn(engine.RunsOn);
            RunsOnShortText = VisionTexts.RunsOnShort(engine.RunsOn);
            GpuAvailable = engine.GpuAvailable;
            UseGpu = engine.UseGpu;
            IsGpuStarting = engine.GpuStarting;
        }
        finally
        {
            _applyingEngine = false;
        }

        OnPropertyChanged(nameof(CanSetGpu));
    }

    partial void OnUseGpuChanged(bool value)
    {
        if (!_applyingEngine)
        {
            _ = SetUseGpuAsync(value);
        }
    }

    private async Task SetUseGpuAsync(bool value)
    {
        IsSettingGpu = true;
        try
        {
            var json = await _ctx.InvokeAsync(VisionMethods.SetUseGpu, VisionJson.Serialize(new UseGpuRequest { UseGpu = value }), CancellationToken.None)
                .ConfigureAwait(true);
            ApplyEngine(VisionJson.Deserialize<EngineInfo>(json));
        }
#pragma warning disable CA1031 // Not changed: the switch goes back, the reason in the host's message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ApplyEngine(Engine);
            await _ctx.ShowMessageAsync("Use GPU", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsSettingGpu = false;
        }
    }

    public void ApplyStats(VisionStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        Stats.Apply(stats);
        IsGpuStarting = stats.State == LoadState.Starting;
    }

    private async Task KeepStatsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ApplyStats(VisionJson.Deserialize<VisionStats>(await _ctx.InvokeAsync(VisionMethods.GetStats, "{}", ct).ConfigureAwait(true)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The header stays without the light.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            try
            {
                await Task.Delay(VisionLimits.PreviewKeepAlive, Time, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private string? _waitingPreview;
    private bool _readingPreview;

    /// <summary>The preview's state has been applied (tests wait for it).</summary>
    internal int AppliedPreviews { get; private set; }

    /// <summary>
    /// A preview's state, up to 25 a second (docs/spec/vision-center.md "Page"): read off the UI thread, the newest one only
    /// (a state that arrives while one is read replaces the waiting one), then applied on the caller's context.
    /// </summary>
    private async Task ApplyPreviewAsync(string? json)
    {
        _waitingPreview = json;
        if (_readingPreview)
        {
            return;
        }

        _readingPreview = true;
        try
        {
            while (_waitingPreview is { } next)
            {
                _waitingPreview = null;
                PreviewEvent preview;
                try
                {
                    preview = await Task.Run(() => VisionJson.Deserialize<PreviewEvent>(next)).ConfigureAwait(true);
                }
                catch (ArgumentException)
                {
                    continue; // a malformed state changes nothing; the next one brings it
                }

                if (!_disposed)
                {
                    Editor?.ApplyPreview(preview);
                    AppliedPreviews++;
                }
            }
        }
        finally
        {
            _readingPreview = false;
        }
    }

    /// <summary>A live event: the pipelines, the models, the stats, the shown pipeline's preview.</summary>
    public void HandleEvent(PluginEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        try
        {
            switch (e.Topic)
            {
                case VisionMethods.PipelinesTopic:
                    ApplyList(VisionJson.Deserialize<PipelineList>(e.PayloadJson));
                    break;
                case VisionMethods.ModelsTopic:
                    Models.Apply(VisionJson.Deserialize<ModelList>(e.PayloadJson));
                    break;
                case VisionMethods.StatsTopic:
                    ApplyStats(VisionJson.Deserialize<VisionStats>(e.PayloadJson));
                    break;
                case VisionMethods.PreviewTopic:
                    _ = ApplyPreviewAsync(e.PayloadJson);
                    break;
            }
        }
        catch (ArgumentException)
        {
            // A malformed event changes nothing; the next one or a reload brings the state.
        }
    }

    // ---- The list ----

    public void ApplyList(PipelineList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        LearnCameraTexts(list.Pipelines.Select(p => p.DeviceId)); // the new cameras' labels in one pass over the devices
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in list.Pipelines)
        {
            ids.Add(row.Id);
            if (_rows.TryGetValue(row.Id, out var existing))
            {
                existing.Apply(row, CameraText(row.DeviceId));
            }
            else
            {
                _rows[row.Id] = new PipelineListRow(row, CameraText(row.DeviceId));
                _order.Add(row.Id);
            }
        }

        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _rows.Remove(gone);
            _order.Remove(gone);
        }

        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var shown = _order.Select(id => _rows[id]).Where(r => query.Length == 0
            || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || r.Camera.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        // The rows update in place; the list resets only when its rows or their order change (an open menu stays open).
        if (!shown.SequenceEqual(Pipelines))
        {
            Pipelines.ReplaceAll(shown);
        }

        SetSelected(Editor is { IsNew: false } e && _rows.TryGetValue(e.Id, out var open) && Pipelines.Contains(open) ? open : null);
        OnPropertyChanged(nameof(ShowNoMatch));
        OnPropertyChanged(nameof(ShowEmpty));
        NewCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPipelineChanged(PipelineListRow? value)
    {
        if (_selecting || value is null || (Editor is { IsNew: false } e && e.Id == value.Id))
        {
            return;
        }

        _ = SwitchToAsync(value);
    }

    private void SetSelected(PipelineListRow? row)
    {
        _selecting = true;
        try
        {
            SelectedPipeline = row;
        }
        finally
        {
            _selecting = false;
        }
    }

    private async Task SwitchToAsync(PipelineListRow row)
    {
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            SetSelected(Editor is { IsNew: false } e && _rows.TryGetValue(e.Id, out var shown) ? shown : null);
            return;
        }

        await OpenAsync(row, _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
    }

    private async Task<bool> ConfirmDiscardAsync() =>
        Editor is not { IsDirty: true } editor
        || await _ctx.ConfirmAsync("Discard changes", VisionTexts.DiscardQuestion(editor.Name.Trim()), "Discard").ConfigureAwait(true);

    /// <summary>Shows a saved pipeline: its draft (getPipeline) and the camera's choices.</summary>
    public async Task OpenAsync(PipelineListRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        var version = ++_openVersion;
        SetSelected(row);
        IsOpening = true;
        try
        {
            var json = await _ctx.InvokeAsync(VisionMethods.GetPipeline, VisionJson.Serialize(new PipelineRequest { PipelineId = row.Id }), ct).ConfigureAwait(true);
            if (version != _openVersion)
            {
                return;
            }

            var editor = ShowEditor(VisionJson.Deserialize<Pipeline>(json), isNew: false);
            IsOpening = false;
            ErrorText = null;
            await editor.LoadCameraOptionsAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The server's reason below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (version == _openVersion)
            {
                ErrorText = ServerErrors.Message(ex);
                SetSelected(Editor is { IsNew: false } shown && _rows.TryGetValue(shown.Id, out var r) ? r : null);
            }
        }
        finally
        {
            if (version == _openVersion)
            {
                IsOpening = false;
            }
        }
    }

    /// <summary>Shows <paramref name="pipeline"/> on the canvas and the right column (tests and the page).</summary>
    internal PipelineEditorViewModel ShowEditor(Pipeline pipeline, bool isNew)
    {
        var editor = new PipelineEditorViewModel(_ctx, pipeline, isNew, Engine, CameraText, PipelineNameTaken, () => Models.Models, DecodePicture, Check)
        {
            Dialogs = Dialogs,
            PreviewTime = Time,
        };
        editor.Saved += OnEditorSaved;
        var old = Editor;
        Editor = editor;
        if (old is not null)
        {
            old.Saved -= OnEditorSaved;
            old.Dispose();
        }

        if (_active is not null)
        {
            editor.StartPreview();
        }

        return editor;
    }

    private void CloseEditor()
    {
        var old = Editor;
        Editor = null;
        SetSelected(null);
        if (old is not null)
        {
            old.Saved -= OnEditorSaved;
            old.Dispose();
        }
    }

    private bool CanNew() => MayChange && _rows.Count < VisionLimits.MaxPipelines;

    /// <summary>New: the camera first (rule 5), then a pipeline with its Camera node and a free name; saved with Save.</summary>
    [RelayCommand(CanExecute = nameof(CanNew))]
    private async Task NewAsync()
    {
        if (PickCamera is not { } pick || !await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        if (await pick(null).ConfigureAwait(true) is not { } camera)
        {
            return;
        }

        var number = _rows.Count + 1;
        while (PipelineNameTaken(string.Empty, VisionTexts.NewPipelineName(number)))
        {
            number++;
        }

        _openVersion++;
        IsOpening = false;
        var pipeline = new Pipeline
        {
            Name = VisionTexts.NewPipelineName(number),
            DeviceId = camera,
            Nodes = [new PipelineNode { Id = "n1", Kind = NodeKinds.Camera, Name = NodeKinds.CameraKind.Label, X = CanvasGeometry.Margin, Y = CanvasGeometry.Margin }],
        };
        var editor = ShowEditor(pipeline, isNew: true);
        SetSelected(null);
        await editor.LoadCameraOptionsAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
    }

    private bool CanDelete() => MayChange && Editor is not null;

    /// <summary>Delete (red): asks first; a new pipeline that was never saved is only closed.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Editor is not { } editor
            || !await _ctx.ConfirmAsync(VisionTexts.DeletePipeline, VisionTexts.DeletePipelineQuestion(editor.Name.Trim()), VisionTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        if (editor.IsNew)
        {
            CloseEditor();
            await OpenFirstAsync().ConfigureAwait(true);
            return;
        }

        var error = await GuardedRun.RunAsync(async () =>
        {
            await _ctx.InvokeAsync(VisionMethods.DeletePipeline, VisionJson.Serialize(new PipelineRequest { PipelineId = editor.Id }), CancellationToken.None)
                .ConfigureAwait(true);
            _rows.Remove(editor.Id);
            _order.Remove(editor.Id);
            CloseEditor();
            ApplyFilter();
            await OpenFirstAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
        if (error is not null)
        {
            await _ctx.ShowMessageAsync(VisionTexts.DeletePipeline, error).ConfigureAwait(true);
        }
    }

    private async Task OpenFirstAsync()
    {
        if (Pipelines.Count > 0)
        {
            await OpenAsync(Pipelines[0], _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OnEditorSaved(object? sender, Pipeline saved)
    {
        var row = new Api.PipelineRow { Id = saved.Id, Name = saved.Name, DeviceId = saved.DeviceId, Mode = saved.Mode, Fps = saved.Fps };
        if (_rows.TryGetValue(saved.Id, out var existing))
        {
            existing.Apply(existing.Row with { Name = saved.Name, Mode = saved.Mode, Fps = saved.Fps }, CameraText(saved.DeviceId));
        }
        else
        {
            existing = new PipelineListRow(row, CameraText(saved.DeviceId));
            _rows[saved.Id] = existing;
            _order.Add(saved.Id);
        }

        ApplyFilter();
        SetSelected(existing);
    }

    internal bool PipelineNameTaken(string id, string name) =>
        _rows.Values.Any(r => r.Id != id && string.Equals(r.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"AXIS Q1798-LE · 10.0.0.48" of a pipeline's camera (from the labels of <see cref="LearnCameraTexts"/>).</summary>
    internal string CameraText(Guid deviceId)
    {
        if (!_cameraTexts.TryGetValue(deviceId, out var text))
        {
            LearnCameraTexts([deviceId]);
            text = _cameraTexts[deviceId];
        }

        return text;
    }

    /// <summary>
    /// The labels of the cameras not known yet (a new pipeline, the first list), in one pass over the devices, never one per
    /// pipeline.
    /// </summary>
    private void LearnCameraTexts(IEnumerable<Guid> deviceIds)
    {
        var missing = deviceIds.Where(id => !_cameraTexts.ContainsKey(id)).ToHashSet();
        if (missing.Count > 0)
        {
            foreach (var (id, text) in CameraLabels(_ctx.Devices, missing))
            {
                _cameraTexts[id] = text;
            }
        }
    }

    /// <summary>"AXIS Q1798-LE · 10.0.0.48" of each of <paramref name="ids"/> in one pass over <paramref name="devices"/>.</summary>
    private static Dictionary<Guid, string> CameraLabels(IReadOnlyList<IDeviceInfo> devices, HashSet<Guid> ids)
    {
        var labels = new Dictionary<Guid, string>(ids.Count);
        foreach (var device in devices)
        {
            if (ids.Contains(device.Id) && !labels.ContainsKey(device.Id))
            {
                labels[device.Id] = string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} · {device.Address}";
                if (labels.Count == ids.Count)
                {
                    break;
                }
            }
        }

        foreach (var gone in ids.Where(id => !labels.ContainsKey(id)))
        {
            labels[gone] = DeviceTexts.Removed(gone.ToString("N"));
        }

        return labels;
    }

    /// <summary>
    /// The device list changed: the rows' camera labels again, in one pass off the UI thread (several milliseconds at 100,000
    /// devices); the newest change wins, only rows whose label changed update.
    /// </summary>
    private async Task RelabelCamerasAsync()
    {
        var version = ++_labelsVersion;
        var devices = _ctx.Devices;
        var ids = _rows.Values.Select(r => r.DeviceId).ToHashSet();
        var labels = await Task.Run(() => CameraLabels(devices, ids)).ConfigureAwait(true);
        if (version != _labelsVersion || _disposed)
        {
            return;
        }

        _cameraTexts.Clear();
        foreach (var (id, text) in labels)
        {
            _cameraTexts[id] = text;
        }

        foreach (var row in _rows.Values)
        {
            row.SetCamera(CameraText(row.DeviceId));
        }

        CamerasRelabeled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The camera labels follow a change of the device list (tests).</summary>
    internal event EventHandler? CamerasRelabeled;

    /// <summary>Every device of the user (the device of a Device event).</summary>
    public IEnumerable<IDeviceInfo> AllDevices => _ctx.Devices;

    /// <summary>The cameras a new pipeline may start from (the picker).</summary>
    public IEnumerable<IDeviceInfo> Cameras => _ctx.Devices.Where(d => d.HasVideo);

    /// <summary>The device list changed: the rows' camera labels again, in one pass; only rows whose label changed update.</summary>
    private void OnDevicesChanged(object? sender, EventArgs e) => _ = RelabelCamerasAsync();

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        MayChange = _ctx.Has(ChangePermissionKey);
        Models.ApplyPermissions();
        Editor?.ApplyPermissions();
        OnPropertyChanged(nameof(CanSetGpu));
    }

    private static async Task<Bitmap?> DecodeAsync(byte[] bytes) => await Task.Run(() =>
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
#pragma warning disable CA1031 // A picture that does not decode: no held frame.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }).ConfigureAwait(true);
}
