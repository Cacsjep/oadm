using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// The Text recognition page (docs/spec/ocr.md "Page"): the readers on the left (New, Delete, search), the selected reader on
/// the right (<see cref="Editor"/>), "Runs on" and Use the graphics card below the list. <see cref="Activate"/> when the
/// page is shown reads the list and the engine, watches the live events (readers changed, a reading, the preview's readings)
/// and previews the shown reader. <see cref="Deactivate"/> stops both: nothing is read or fetched while the page is hidden.
/// </summary>
public sealed partial class OcrViewModel : ObservableObject, IDisposable
{
    /// <summary>"oadm.ocr:change": New, Delete, Save, the settings.</summary>
    public static readonly string ChangePermissionKey = OcrInfo.PluginId + ":" + OcrInfo.ChangePermission;

    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, ReaderRow> _rows = new(StringComparer.Ordinal);
    private Dictionary<Guid, IDeviceInfo>? _devices;
    private CancellationTokenSource? _active;
    private int _openVersion;
    private bool _applyingEngine;

    public OcrViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        MayChange = ctx.Has(ChangePermissionKey);
        ctx.DevicesChanged += OnDevicesChanged;
        ctx.PermissionsChanged += OnPermissionsChanged;
    }

    /// <summary>Wait before watching the events again after the stream broke.</summary>
    public TimeSpan ReconnectDelay { get; set; } = PluginEventWatch.DefaultRetryDelay;

    /// <summary>Decodes a cut-out JPEG off the UI thread (tests decode in place).</summary>
    public Func<byte[], Task<Bitmap?>> DecodePicture { get; set; } = DecodeAsync;

    /// <summary>Opens the camera picker (set by the view); the argument is the camera chosen so far.</summary>
    public Func<Guid?, Task<Guid?>>? PickCamera
    {
        get => _pickCamera;
        set
        {
            _pickCamera = value;
            Editor?.PickCamera = value;
        }
    }

    private Func<Guid?, Task<Guid?>>? _pickCamera;

    /// <summary>New, Delete, Save and the settings.</summary>
    [ObservableProperty]
    public partial bool MayChange { get; private set; }

    /// <summary>The readers the search shows.</summary>
    public RangeObservableCollection<ReaderRow> Readers { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial ReaderRow? SelectedReader { get; set; }

    /// <summary>The reader on the right; null without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor), nameof(ShowEmpty))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial ReaderEditorViewModel? Editor { get; private set; }

    public bool HasEditor => Editor is not null;

    /// <summary>The list is read (the ring in the list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowNoMatch))]
    public partial bool IsLoading { get; private set; }

    /// <summary>A reader is opened (the ring in the right card).</summary>
    [ObservableProperty]
    public partial bool IsOpening { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>No reader at all: "Create a reader to read text from a camera."</summary>
    public bool ShowEmpty => !IsLoading && _rows.Count == 0 && Editor is null;

    public static string EmptyText => OcrInfo.EmptyText;

    /// <summary>Readers exist but the search shows none.</summary>
    public bool ShowNoMatch => !IsLoading && _rows.Count > 0 && Readers.Count == 0;

    public static string NoMatchText => OcrTexts.NoMatch;

    /// <summary>"Runs on: CPU (8 cores)".</summary>
    [ObservableProperty]
    public partial string RunsOnText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool GpuAvailable { get; private set; }

    /// <summary>Use the graphics card (on by default); saved at once.</summary>
    [ObservableProperty]
    public partial bool UseGpu { get; set; } = true;

    /// <summary>The setting is server-wide: the change right and all devices (the server refuses users with a device group).</summary>
    public bool CanSetGpu => MayChange && _ctx.HasAllDevices && GpuAvailable;

    public string? GpuBlockedReason =>
        !GpuAvailable ? OcrTexts.NoGpu
        : !_ctx.HasAllDevices ? DeviceGroupMessages.AllDevicesOnly
        : null;

    public EngineInfo Engine { get; private set; } = new();

    /// <summary>The page is shown: read the list and the engine, watch the events.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        _ = _ctx.RunAsync(HandleEvent, ReconnectDelay, token);
        Editor?.StartPreview();
        _ = LoadAsync(token);
    }

    /// <summary>The page is hidden: no watch, no fetch, no preview.</summary>
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

    /// <summary>The page is shown (the shown reader is previewed live).</summary>
    public bool IsActive => _active is not null;

    /// <summary>The readers set to On action this session that already showed their short message.</summary>
    private readonly HashSet<string> _onActionNoticeShown = new(StringComparer.Ordinal);

    public void Dispose()
    {
        Deactivate();
        _ctx.DevicesChanged -= OnDevicesChanged;
        _ctx.PermissionsChanged -= OnPermissionsChanged;
        Editor?.Dispose();
    }

    /// <summary>Reads the engine and the list, then opens the first reader (or keeps the shown one).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            await LoadEngineAsync(ct).ConfigureAwait(true);
            var json = await _ctx.InvokeAsync(OcrMethods.ListReaders, "{}", ct).ConfigureAwait(true);
            ApplyList(json);
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

        if (Editor is null && Readers.Count > 0)
        {
            await OpenAsync(Readers[0], ct).ConfigureAwait(true);
        }
    }

    private async Task LoadEngineAsync(CancellationToken ct)
    {
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.GetEngine, "{}", ct).ConfigureAwait(true);
            ApplyEngine(OcrJson.Deserialize<EngineInfo>(json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // An older server without the engine call: the CPU choices, no settings line.
        catch (Exception)
#pragma warning restore CA1031
        {
            ApplyEngine(new EngineInfo());
        }
    }

    /// <summary>"Runs on" and the GPU switch.</summary>
    public void ApplyEngine(EngineInfo engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
        _applyingEngine = true;
        try
        {
            RunsOnText = string.IsNullOrEmpty(engine.RunsOn) ? string.Empty : OcrTexts.RunsOn(engine.RunsOn);
            GpuAvailable = engine.GpuAvailable;
            UseGpu = engine.UseGpu;
        }
        finally
        {
            _applyingEngine = false;
        }

        OnPropertyChanged(nameof(CanSetGpu));
        OnPropertyChanged(nameof(GpuBlockedReason));
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
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.SetUseGpu, OcrJson.Serialize(new UseGpuRequest { UseGpu = value }), CancellationToken.None)
                .ConfigureAwait(true);
            ApplyEngine(OcrJson.Deserialize<EngineInfo>(json));
        }
#pragma warning disable CA1031 // Not changed: the switch goes back, the reason in the host's message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ApplyEngine(Engine);
            await _ctx.ShowMessageAsync("Use the graphics card", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>A reader list (listReaders, the readers event): rows updated in place by id, the shown list rebuilt once.</summary>
    public void ApplyList(string? json)
    {
        var list = OcrJson.Deserialize<ReaderList>(json);
        if (list.FpsChoices is { Count: > 0 } choices)
        {
            // The frame rates offered now (more on a GPU) come with the list.
            Engine = Engine with { FpsChoices = choices };
        }

        // A change of the readers may lift a refusal of the watching limits.
        Editor?.ClearModeRefusal();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var summary in list.Readers)
        {
            seen.Add(summary.Id);
            var camera = CameraText(summary.DeviceId);
            if (_rows.TryGetValue(summary.Id, out var row))
            {
                row.Apply(summary, camera);
            }
            else
            {
                _rows[summary.Id] = new ReaderRow(summary, camera);
            }
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        // The list's order is the server's (by name).
        _order = [.. list.Readers.Select(r => r.Id)];
        ApplyFilter();
        if (Editor is { IsNew: false } editor)
        {
            if (!_rows.TryGetValue(editor.Id, out var shown))
            {
                // Deleted elsewhere.
                CloseEditor();
            }
            else
            {
                editor.Problem = shown.Problem;
                SetSelected(shown);
            }
        }

        OnPropertyChanged(nameof(ShowEmpty));
        NewCommand.NotifyCanExecuteChanged();
    }

    private List<string> _order = [];

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>The search over name and camera, one reset.</summary>
    private void ApplyFilter()
    {
        var term = SearchText.Trim().ToLowerInvariant();
        var shown = new List<ReaderRow>(_order.Count);
        foreach (var id in _order)
        {
            if (_rows.TryGetValue(id, out var row) && (term.Length == 0 || row.SearchText.Contains(term, StringComparison.Ordinal)))
            {
                shown.Add(row);
            }
        }

        Readers.ReplaceAll(shown);
        if (Editor is { IsNew: false } editor && _rows.TryGetValue(editor.Id, out var selected) && shown.Contains(selected))
        {
            SetSelected(selected);
        }

        OnPropertyChanged(nameof(ShowNoMatch));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>A live event: the list changed, or a reader read a picture.</summary>
    public void HandleEvent(PluginEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        try
        {
            switch (e.Topic)
            {
                case OcrMethods.ReadersTopic:
                    ApplyList(e.PayloadJson);
                    break;
                case OcrMethods.ReadingTopic:
                    var reading = OcrJson.Deserialize<ReadingEvent>(e.PayloadJson).Reading;
                    if (_rows.TryGetValue(reading.ReaderId, out var row))
                    {
                        row.SetLast(reading.Areas.Count > 0 ? reading.Areas[0].Text : string.Empty, reading.Time);
                    }

                    break;
                case OcrMethods.PreviewTopic:
                    var preview = OcrJson.Deserialize<PreviewEvent>(e.PayloadJson);
                    if (Editor is { PreviewId: { } id } editor && id == preview.PreviewId)
                    {
                        _ = editor.RefreshPreviewAsync(_active?.Token ?? CancellationToken.None);
                    }

                    break;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed event changes nothing; the next one or a reload brings the state.
        }
    }

    partial void OnSelectedReaderChanged(ReaderRow? value)
    {
        if (_selecting || value is null || (Editor is { IsNew: false } e && e.Id == value.Id))
        {
            return;
        }

        _ = SwitchToAsync(value);
    }

    private bool _selecting;

    private void SetSelected(ReaderRow? row)
    {
        _selecting = true;
        try
        {
            SelectedReader = row;
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>The user picked another reader: unsaved changes are discarded only after asking.</summary>
    private async Task SwitchToAsync(ReaderRow row)
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
        || await _ctx.ConfirmAsync("Discard changes", $"Discard the changes of \"{DisplayName(editor)}\"?", "Discard").ConfigureAwait(true);

    private static string DisplayName(ReaderEditorViewModel editor) =>
        string.IsNullOrWhiteSpace(editor.Name) ? "the new reader" : editor.Name.Trim();

    /// <summary>Shows a saved reader: its setup (getReader), the camera's choices and its last reading.</summary>
    public async Task OpenAsync(ReaderRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        var version = ++_openVersion;
        SetSelected(row);
        IsOpening = true;
        try
        {
            var json = await _ctx.InvokeAsync(OcrMethods.GetReader, OcrJson.Serialize(new ReaderRequest { ReaderId = row.Id }), ct).ConfigureAwait(true);
            if (version != _openVersion)
            {
                return;
            }

            var editor = ShowEditor(OcrJson.Deserialize<Reader>(json), isNew: false);
            editor.Problem = row.Problem;
            IsOpening = false;
            await Task.WhenAll(editor.LoadCameraOptionsAsync(ct), editor.LoadLastReadingAsync(ct)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The server's reason below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            if (version == _openVersion)
            {
                IsOpening = false;
            }
        }
    }

    /// <summary>Shows <paramref name="editor"/>'s reader on the right (tests and the page).</summary>
    internal ReaderEditorViewModel ShowEditor(Reader reader, bool isNew)
    {
        var editor = new ReaderEditorViewModel(_ctx, reader, isNew, Engine, CameraText, ReaderNameTaken, DecodePicture, _onActionNoticeShown) { PickCamera = PickCamera };
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

    /// <summary>New: the camera first (docs/spec/ocr.md rule 3), then the reader's form with a free name; saved with Save.</summary>
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
        while (ReaderNameTaken(string.Empty, $"Reader {number}"))
        {
            number++;
        }

        var editor = ShowEditor(new Reader { Name = $"Reader {number}", DeviceId = camera, Mode = ReaderMode.Watching }, isNew: true);
        SetSelected(null);
        await editor.LoadCameraOptionsAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
    }

    private bool CanNew() => MayChange && _rows.Count < OcrLimits.MaxReaders;

    /// <summary>Delete (red): asks first; a new reader that was never saved is only closed.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        var name = DisplayName(editor);
        if (!await _ctx.ConfirmAsync("Delete reader", $"Delete \"{name}\"? Rules that use it stop reading.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        if (editor.IsNew)
        {
            CloseEditor();
            await OpenFirstAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            await _ctx.InvokeAsync(OcrMethods.DeleteReader, OcrJson.Serialize(new ReaderRequest { ReaderId = editor.Id }), CancellationToken.None)
                .ConfigureAwait(true);
            _rows.Remove(editor.Id);
            _order.Remove(editor.Id);
            CloseEditor();
            ApplyFilter();
            await OpenFirstAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The server's reason in the host's message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("Delete reader", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
    }

    private bool CanDelete() => MayChange && Editor is not null;

    private async Task OpenFirstAsync()
    {
        if (Readers.Count > 0)
        {
            await OpenAsync(Readers[0], _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OnEditorSaved(object? sender, Reader saved)
    {
        var summary = new ReaderSummary { Id = saved.Id, Name = saved.Name, DeviceId = saved.DeviceId, Mode = saved.Mode, Fps = saved.Fps };
        if (_rows.TryGetValue(saved.Id, out var row))
        {
            row.Apply(summary with { LastText = row.LastText, LastTime = row.LastTime, Problem = row.Problem }, CameraText(saved.DeviceId));
            if (row.LastTime is null)
            {
                row.SetLast(null, null);
            }
        }
        else
        {
            row = new ReaderRow(summary, CameraText(saved.DeviceId));
            _rows[saved.Id] = row;
            _order.Add(saved.Id);
        }

        ApplyFilter();
        SetSelected(row);
        OnPropertyChanged(nameof(ShowEmpty));
        NewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether another reader already has <paramref name="name"/>.</summary>
    internal bool ReaderNameTaken(string id, string name) =>
        _rows.Values.Any(r => r.Id != id && string.Equals(r.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"AXIS Q1798-LE · 10.0.0.48"; a device OADM no longer manages: "Removed device 1a2b3c4d".</summary>
    internal string CameraText(Guid deviceId)
    {
        _devices ??= _ctx.Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        if (!_devices.TryGetValue(deviceId, out var device))
        {
            return DeviceTexts.Removed(deviceId.ToString("N"));
        }

        return string.IsNullOrWhiteSpace(device.Model) ? device.Address : device.Model + " · " + device.Address;
    }

    /// <summary>Only the cameras (the picker's list).</summary>
    public IReadOnlyList<IDeviceInfo> Cameras => [.. _ctx.Devices.Where(d => d.HasVideo)];

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        _devices = null;
        foreach (var row in _rows.Values)
        {
            row.SetCamera(CameraText(row.DeviceId));
        }
    }

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        MayChange = _ctx.Has(ChangePermissionKey);
        OnPropertyChanged(nameof(CanSetGpu));
        OnPropertyChanged(nameof(GpuBlockedReason));
        NewCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private static Task<Bitmap?> DecodeAsync(byte[] jpeg) => Task.Run<Bitmap?>(() =>
    {
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            return new Bitmap(stream);
        }
#pragma warning disable CA1031 // A picture that does not decode: the page shows none.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    });
}
