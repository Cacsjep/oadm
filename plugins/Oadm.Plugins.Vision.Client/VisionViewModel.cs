using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
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

    public string ChipText => Row.Problem is not null ? VisionTexts.NotRunning
        : Row.Mode == PipelineMode.OnAction ? VisionTexts.OnAction
        : Row.Slow ? VisionTexts.WatchingReads(Row.Fps, Row.Reads ?? 0)
        : VisionTexts.WatchingAt(Row.Fps);

    /// <summary>The chip's tooltip: the server's whole text of the problem.</summary>
    public string? Problem => Row.Problem;

    public bool IsError => Row.Problem is not null;

    public bool IsWarning => !IsError && Row.Mode == PipelineMode.Watching && Row.Slow;

    public bool IsOk => !IsError && !IsWarning && Row.Mode == PipelineMode.Watching;

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
    [NotifyCanExecuteChangedFor(nameof(NewCommand), nameof(DeleteCommand))]
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

    /// <summary>No pipeline at all: "Upload a model, then create a pipeline."</summary>
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
                    Editor?.ApplyPreview(VisionJson.Deserialize<PreviewEvent>(e.PayloadJson));
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
        Pipelines.ReplaceAll(_order.Select(id => _rows[id]).Where(r => query.Length == 0
            || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || r.Camera.Contains(query, StringComparison.OrdinalIgnoreCase)));
        SetSelected(Editor is { IsNew: false } e && _rows.TryGetValue(e.Id, out var shown) && Pipelines.Contains(shown) ? shown : null);
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
        var editor = new PipelineEditorViewModel(_ctx, pipeline, isNew, Engine, CameraText, PipelineNameTaken, () => Models.Models, DecodePicture, Check, Models.ClassListNameOf)
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

    /// <summary>"AXIS Q1798-LE · 10.0.0.48" of the pipelines' cameras (at most 50), found in one pass over the devices.</summary>
    internal string CameraText(Guid deviceId)
    {
        if (_cameraTexts.TryGetValue(deviceId, out var text))
        {
            return text;
        }

        var device = _ctx.Devices.FirstOrDefault(d => d.Id == deviceId);
        text = device is null ? DeviceTexts.Removed(deviceId.ToString("N")) : string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} · {device.Address}";
        _cameraTexts[deviceId] = text;
        return text;
    }

    /// <summary>Every device of the user (the device of a Device event).</summary>
    public IEnumerable<IDeviceInfo> AllDevices => _ctx.Devices;

    /// <summary>The cameras a new pipeline may start from (the picker).</summary>
    public IEnumerable<IDeviceInfo> Cameras => _ctx.Devices.Where(d => d.HasVideo);

    private void OnDevicesChanged(object? sender, EventArgs e) => _cameraTexts.Clear();

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
